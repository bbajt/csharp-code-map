namespace CodeMap.Harness.Concurrency;

using System.Diagnostics;

/// <summary>
/// Disposable scratch copy of a target repo for one concurrency run: a local
/// <c>git clone</c> of the source repo's committed HEAD plus one detached
/// <c>git worktree</c> per agent. Agents edit their own worktree and never touch
/// the developer's checkout (uncommitted source changes are deliberately excluded).
/// </summary>
/// <remarks>
/// A local clone carries an <c>origin</c> remote (the source path) that all worktrees
/// share, so every agent derives the <b>same</b> RepoId (<c>GitService.DeriveRepoId</c>
/// hashes the remote URL) — they race for one baseline, which is the T1 situation the
/// benchmark measures. Without a remote, the RepoId would hash each worktree path and
/// every agent would silently build a private baseline.
/// </remarks>
public sealed class AgentScratchRepo : IAsyncDisposable
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(20);
    private const int MaxParallelRestores = 4;
    private const int MaxErrorOutputChars = 2000;

    private readonly string _root;

    private AgentScratchRepo(string root, string commitSha, IReadOnlyList<string> worktrees)
    {
        _root = root;
        CommitSha = commitSha;
        Worktrees = worktrees;
    }

    /// <summary>Commit every worktree is checked out at (the source repo's HEAD).</summary>
    public string CommitSha { get; }

    /// <summary>Absolute worktree paths, one per requested worktree.</summary>
    public IReadOnlyList<string> Worktrees { get; }

    /// <summary>
    /// Clones <paramref name="sourceGitRoot"/> into <c>{root}/source</c> and adds
    /// <paramref name="worktreeCount"/> detached worktrees at <c>{root}/wt-{i}</c>.
    /// </summary>
    public static async Task<AgentScratchRepo> CreateAsync(
        string sourceGitRoot, string root, int worktreeCount, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        // --config writes core.longpaths into the clone, so every `worktree add` below inherits it.
        // Without it git refuses paths over 260 chars on Windows even when the OS allows them
        // (Bitwarden under a scratch prefix, PHASE-21-06 T02). No effect elsewhere.
        await RunGitAsync(root, ct, "clone", "--quiet", "--no-checkout", "--local",
                "--config", "core.longpaths=true", sourceGitRoot, source)
            .ConfigureAwait(false);
        var sha = (await RunGitAsync(source, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

        var worktrees = new List<string>(worktreeCount);
        for (var i = 0; i < worktreeCount; i++)
        {
            var wt = Path.Combine(root, $"wt-{i}");
            await RunGitAsync(source, ct, "worktree", "add", "--quiet", "--detach", wt, sha).ConfigureAwait(false);
            worktrees.Add(wt);
        }
        return new AgentScratchRepo(root, sha, worktrees);
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> on <paramref name="solutionRelativePath"/> in every worktree (up to
    /// four at a time; NuGet's global package cache is shared and safe for concurrent restores).
    /// Needed because <c>obj/</c> is gitignored and MSBuildWorkspace does not restore: an unrestored
    /// worktree compiles with unresolved package references, so the index and every figure measured
    /// on it are degraded (PHASE-21-06 T02, F12). Returns the wall-clock time; throws
    /// <see cref="InvalidOperationException"/> with the restore output on failure.
    /// </summary>
    public async Task<TimeSpan> RestoreAsync(string solutionRelativePath, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Worktrees,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRestores, CancellationToken = ct },
            async (wt, token) => await RunProcessAsync("dotnet", wt, RestoreTimeout, token,
                "restore", Path.Combine(wt, solutionRelativePath), "--nologo", "--verbosity", "quiet",
                // Restore only makes references resolvable for indexing. NuGet's vulnerability audit
                // uses live advisory data, so a repo with warnings-as-errors (Bitwarden, NU1902) stops
                // restoring the day an advisory is published for one of its pins.
                "-p:NuGetAudit=false")
                .ConfigureAwait(false)).ConfigureAwait(false);
        return clock.Elapsed;
    }

    /// <summary>When true, <see cref="DisposeAsync"/> leaves the clone and worktrees on disk for inspection.</summary>
    public bool Keep { get; set; }

    /// <summary>Deletes the clone and all worktrees (clears read-only git object files first) unless <see cref="Keep"/>.</summary>
    public ValueTask DisposeAsync()
    {
        if (!Keep) DeleteDirectoryRobust(_root);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Recursively deletes <paramref name="path"/>, clearing read-only attributes that git sets
    /// on pack/object files (Windows refuses to delete those otherwise). Best-effort.
    /// </summary>
    public static void DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A just-exited daemon may still hold a handle for a moment.
                Thread.Sleep(500);
            }
        }
    }

    private static Task<string> RunGitAsync(string workingDir, CancellationToken ct, params string[] args)
        => RunProcessAsync("git", workingDir, GitTimeout, ct, args);

    private static async Task<string> RunProcessAsync(
        string fileName, string workingDir, TimeSpan timeoutAfter, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName} — is it on PATH?");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            // git reports on stderr; dotnet restore reports its errors on stdout.
            var err = (await stderr.ConfigureAwait(false)).Trim();
            var output = err.Length > 0 ? err : (await stdout.ConfigureAwait(false)).Trim();
            if (output.Length > MaxErrorOutputChars) output = "…" + output[^MaxErrorOutputChars..];
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} failed ({process.ExitCode}): {output}");
        }
        return await stdout.ConfigureAwait(false);
    }
}
