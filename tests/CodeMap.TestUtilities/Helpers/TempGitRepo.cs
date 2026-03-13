namespace CodeMap.TestUtilities.Helpers;

using LibGit2Sharp;

/// <summary>
/// Creates a temporary Git repository for testing. Disposes = deletes.
/// Usage: using var repo = TempGitRepo.Create();
/// </summary>
public sealed class TempGitRepo : IDisposable
{
    public string Path { get; }
    public Repository Repository => _repo ??= new Repository(Path);

    private Repository? _repo;

    private TempGitRepo(string path)
    {
        Path = path;
    }

    /// <summary>Creates an initialized repo with optional remote.</summary>
    public static TempGitRepo Create(string? remoteName = "origin", string? remoteUrl = null)
    {
        string tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codemap-test-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(tempDir);
        Repository.Init(tempDir);

        var temp = new TempGitRepo(tempDir);

        var repo = temp.Repository;
        repo.Config.Set("user.email", "test@codemap.dev");
        repo.Config.Set("user.name", "CodeMap Test");

        if (remoteName != null)
        {
            string url = remoteUrl ?? $"https://github.com/codemap-test/{Guid.NewGuid():N}.git";
            repo.Network.Remotes.Add(remoteName, url);
        }

        return temp;
    }

    /// <summary>Creates a repo with no remotes (local-only).</summary>
    public static TempGitRepo CreateLocal() => Create(remoteName: null);

    /// <summary>Creates a bare repository.</summary>
    public static TempGitRepo CreateBare()
    {
        string tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codemap-test-bare-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(tempDir);
        Repository.Init(tempDir, isBare: true);
        return new TempGitRepo(tempDir);
    }

    /// <summary>Creates a file, stages, and commits it. Returns the commit SHA.</summary>
    public string CommitFile(string relativePath, string content, string message = "test commit")
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        string? dir = System.IO.Path.GetDirectoryName(fullPath);
        if (dir != null) Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content);
        Commands.Stage(Repository, relativePath);

        var author = new Signature("CodeMap Test", "test@codemap.dev", DateTimeOffset.Now);
        var commit = Repository.Commit(message, author, author);
        return commit.Sha;
    }

    /// <summary>Stages a file that already exists on disk.</summary>
    public void StageFile(string relativePath)
    {
        Commands.Stage(Repository, relativePath);
    }

    /// <summary>Deletes a file, stages the deletion, and commits it. Returns the commit SHA.</summary>
    public string DeleteFile(string relativePath, string message = "delete file")
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        File.Delete(fullPath);
        Commands.Stage(Repository, relativePath);

        var author = new Signature("CodeMap Test", "test@codemap.dev", DateTimeOffset.Now);
        var commit = Repository.Commit(message, author, author);
        return commit.Sha;
    }

    /// <summary>Creates a file without staging or committing (dirty working tree).</summary>
    public void CreateUnstagedFile(string relativePath, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        string? dir = System.IO.Path.GetDirectoryName(fullPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>Modifies an existing file without staging.</summary>
    public void ModifyFile(string relativePath, string newContent)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, relativePath), newContent);
    }

    /// <summary>Creates and checks out a new branch.</summary>
    public void CreateBranch(string name)
    {
        var branch = Repository.CreateBranch(name);
        Commands.Checkout(Repository, branch);
    }

    /// <summary>Checks out an existing branch.</summary>
    public void Checkout(string name)
    {
        Commands.Checkout(Repository, Repository.Branches[name]);
    }

    /// <summary>Checks out a specific commit (detached HEAD).</summary>
    public void DetachHead(string commitSha)
    {
        Commands.Checkout(Repository, Repository.Lookup<Commit>(commitSha));
    }

    public void Dispose()
    {
        _repo?.Dispose();
        _repo = null;

        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }
}
