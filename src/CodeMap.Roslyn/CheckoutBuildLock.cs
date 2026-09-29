namespace CodeMap.Roslyn;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Cross-process, per-checkout mutual exclusion around MSBuildWorkspace evaluation (F9, ADR-042).
/// Two design-time builds of the same checkout race on generated files under <c>obj/</c>
/// ("Cannot create a file when that file already exists"), degrading one compilation. The lock is
/// a <see cref="FileShare.None"/> handle on <c>&lt;TEMP&gt;/codemap-locks/&lt;hash&gt;.lock</c> keyed by
/// the checkout root — independent of <c>CODEMAP_HOME</c>, so processes with different data roots
/// still exclude each other. The OS releases the handle if the holder dies.
/// </summary>
internal static class CheckoutBuildLock
{
    /// <summary>Environment variable overriding <see cref="DefaultTimeout"/> (seconds).</summary>
    internal const string TimeoutEnvVar = "CODEMAP_BUILD_LOCK_TIMEOUT_SECONDS";

    /// <summary>Large solutions can take minutes to evaluate; waiters give up after this.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string[] TransientFailurePatterns =
    [
        "being used by another process",
        "already exists",
        "Could not write",
        "Access to the path",
        "The process cannot access",
    ];

    /// <summary>
    /// The checkout a solution/project belongs to: the nearest ancestor directory containing
    /// <c>.git</c> (a directory, or the file a git worktree uses); otherwise the file's directory.
    /// </summary>
    internal static string ResolveCheckoutRoot(string solutionOrProjectPath)
    {
        var full = Path.GetFullPath(solutionOrProjectPath);
        var start = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return Path.TrimEndingDirectorySeparator(dir);
        }
        return Path.TrimEndingDirectorySeparator(start);
    }

    /// <summary>
    /// Lock file path for <paramref name="checkoutRoot"/>. Spelling-insensitive: normalised full
    /// path, case-folded except on Linux (case-sensitive file system).
    /// </summary>
    internal static string LockPathFor(string checkoutRoot)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutRoot));
        if (!OperatingSystem.IsLinux()) key = key.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "codemap-locks", hash + ".lock");
    }

    /// <summary>The configured wait timeout: <see cref="TimeoutEnvVar"/> if valid, else <see cref="DefaultTimeout"/>.</summary>
    internal static TimeSpan ConfiguredTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutEnvVar), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;

    /// <summary>
    /// Acquires the lock for <paramref name="checkoutRoot"/>, polling until it is free. Logs once at
    /// Information when it has to wait. Throws <see cref="IOException"/> after
    /// <paramref name="timeout"/> (classified <c>STORAGE_ERROR</c>, retryable — ADR-041).
    /// Dispose the result to release.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquireAsync(
        string checkoutRoot, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        var path = LockPathFor(checkoutRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var logged = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (logged)
                    logger.LogInformation("Checkout build lock acquired for {Checkout} after {Ms} ms", checkoutRoot, waited.ElapsedMilliseconds);
                return new Releaser(handle);
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                if (!logged)
                {
                    logger.LogInformation(
                        "Waiting for another CodeMap process to finish evaluating {Checkout} (lock {LockPath})",
                        checkoutRoot, path);
                    logged = true;
                }
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Checkout build lock busy: another CodeMap process has been evaluating '{checkoutRoot}' for over {timeout.TotalSeconds:0}s (lock '{path}'). Retry later.",
                    ex);
            }
        }
    }

    /// <summary>
    /// True when an MSBuildWorkspace failure diagnostic looks like a transient file conflict —
    /// e.g. with another build (IDE, <c>dotnet build</c>) writing the same <c>obj/</c> — worth one retry.
    /// </summary>
    internal static bool IsTransientWorkspaceFailure(string message) =>
        TransientFailurePatterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));

    private sealed class Releaser(FileStream handle) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => handle.DisposeAsync();
    }
}
