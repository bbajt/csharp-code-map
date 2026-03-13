namespace CodeMap.Integration.Tests.Git;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Git;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public class GitServiceIntegrationTests
{
    private static GitService CreateService() =>
        new(NullLogger<GitService>.Instance);

    // ===== Branch Switching =====

    [Fact]
    public async Task ChangedFiles_AfterBranchSwitch_DetectsCorrectDiff()
    {
        using var repo = TempGitRepo.Create();
        string fileA = "fileA.cs";
        string fileB = "fileB.cs";

        string baselineSha = repo.CommitFile(fileA, "original A");
        repo.CreateBranch("feature");
        repo.CommitFile(fileA, "modified A");
        repo.CommitFile(fileB, "new B");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().Contain(c => c.FilePath.Value == fileA && c.Kind == FileChangeKind.Modified);
        result.Should().Contain(c => c.FilePath.Value == fileB && c.Kind == FileChangeKind.Added);
    }

    [Fact]
    public async Task ChangedFiles_ComparingAcrossBranches_ShowsBranchDiff()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("fileA.cs", "a");

        // Feature branch: add fileB
        repo.CreateBranch("feature");
        repo.CommitFile("fileB.cs", "b");

        // Switch back to main, add fileC
        repo.Checkout("master");
        repo.CommitFile("fileC.cs", "c");

        // Switch to feature — check diff from baseline
        repo.Checkout("feature");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().Contain(c => c.FilePath.Value == "fileB.cs" && c.Kind == FileChangeKind.Added);
        result.Should().NotContain(c => c.FilePath.Value == "fileC.cs");
    }

    [Fact]
    public async Task CurrentBranch_AfterCheckout_ReturnsNewBranch()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        repo.CreateBranch("feature/auth");
        var svc = CreateService();

        var result = await svc.GetCurrentBranchAsync(repo.Path);

        result.Should().Be("feature/auth");
    }

    [Fact]
    public async Task CurrentBranch_AfterCheckoutBack_ReturnsOriginal()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        string defaultBranch = repo.Repository.Head.FriendlyName;
        repo.CreateBranch("feature");
        repo.Checkout(defaultBranch);
        var svc = CreateService();

        var result = await svc.GetCurrentBranchAsync(repo.Path);

        result.Should().Be(defaultBranch);
    }

    // ===== Multiple Remotes =====

    [Fact]
    public async Task RepoIdentity_MultipleRemotes_PrefersOrigin()
    {
        using var repo = TempGitRepo.Create(remoteName: "upstream",
            remoteUrl: "https://github.com/upstream/repo");
        repo.Repository.Network.Remotes.Add("origin", "https://github.com/origin/repo");
        var svc = CreateService();

        var result = await svc.GetRepoIdentityAsync(repo.Path);

        // Should be derived from origin URL
        var expectedId = DeriveExpectedId("https://github.com/origin/repo");
        result.Value.Should().Be(expectedId);
    }

    [Fact]
    public async Task RepoIdentity_NoOrigin_UsesFirstRemote()
    {
        using var repo = TempGitRepo.Create(remoteName: "upstream",
            remoteUrl: "https://github.com/upstream/myrepo");
        var svc = CreateService();

        var result = await svc.GetRepoIdentityAsync(repo.Path);

        var expectedId = DeriveExpectedId("https://github.com/upstream/myrepo");
        result.Value.Should().Be(expectedId);
    }

    // ===== Nested Directories =====

    [Fact]
    public async Task ChangedFiles_NestedDirectories_ReturnsFullRelativePath()
    {
        using var repo = TempGitRepo.Create();
        string path = "src/Services/Auth/AuthService.cs";
        string baselineSha = repo.CommitFile(path, "class AuthService {}");
        repo.ModifyFile(path, "class AuthService { void Updated() {} }");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle();
        result[0].FilePath.Value.Should().Be(path);
    }

    [Fact]
    public async Task ChangedFiles_MultipleFilesInSubdirs_AllDetected()
    {
        using var repo = TempGitRepo.Create();
        string[] paths =
        [
            "src/Domain/Order.cs",
            "src/Infrastructure/Db/Context.cs",
            "tests/Unit/OrderTests.cs"
        ];

        string baselineSha = repo.CommitFile("initial.txt", "init");
        foreach (var p in paths)
            repo.CommitFile(p, "class Foo {}");

        string newBaseline = repo.CommitFile("marker.txt", "mark");
        foreach (var p in paths)
            repo.ModifyFile(p, "class Foo { void Updated() {} }");

        var svc = CreateService();
        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(newBaseline));

        result.Should().HaveCount(3);
        foreach (var p in paths)
            result.Should().Contain(c => c.FilePath.Value == p && c.Kind == FileChangeKind.Modified);
    }

    // ===== Edge Cases =====

    [Fact]
    public async Task CurrentCommit_ManyCommits_ReturnsLatest()
    {
        using var repo = TempGitRepo.Create();
        string lastSha = string.Empty;
        for (int i = 0; i < 20; i++)
            lastSha = repo.CommitFile($"file{i}.cs", $"content {i}", $"commit {i}");
        var svc = CreateService();

        var result = await svc.GetCurrentCommitAsync(repo.Path);

        result.Value.Should().Be(lastSha);
    }

    [Fact]
    public async Task IsClean_OnlyGitignoredFiles_ReturnsTrue()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile(".gitignore", "*.log");
        repo.CreateUnstagedFile("debug.log", "log content");
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ChangedFiles_EmptyDiff_SameCommit_ReturnsEmpty()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedFiles_DeletedAndReaddedFile_DetectsChange()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "original content");
        repo.DeleteFile("README.md");
        repo.CommitFile("README.md", "completely different content");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        // Delete + re-add shows as Modified (same path, different content)
        result.Should().ContainSingle(c => c.FilePath.Value == "README.md");
    }

    // ===== Concurrent Access =====

    [Fact]
    public async Task MultipleReads_SameRepo_AllSucceed()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => svc.GetCurrentCommitAsync(repo.Path))
            .ToList();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(5);
        var firstSha = results[0].Value;
        results.Should().AllSatisfy(r => r.Value.Should().Be(firstSha));
    }

    // ===== Helpers =====

    private static string DeriveExpectedId(string remoteUrl)
    {
        string normalized = remoteUrl.Trim();
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        normalized = normalized.TrimEnd('/').ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
