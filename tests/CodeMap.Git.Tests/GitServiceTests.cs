namespace CodeMap.Git.Tests;

using CodeMap.Core.Types;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

public class GitServiceTests
{
    private static GitService CreateService() =>
        new(NullLogger<GitService>.Instance);

    // ===== GetRepoIdentityAsync =====

    [Fact]
    public async Task GetRepoIdentityAsync_WithOriginRemote_ReturnsSha256BasedId()
    {
        using var repo = TempGitRepo.Create(remoteUrl: "https://github.com/org/myrepo.git");
        var svc = CreateService();

        var result = await svc.GetRepoIdentityAsync(repo.Path);

        result.Value.Should().HaveLength(16);
        result.Value.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public async Task GetRepoIdentityAsync_WithTrailingGitSuffix_NormalizesBeforeHash()
    {
        using var repoWith = TempGitRepo.Create(remoteUrl: "https://github.com/org/myrepo.git");
        using var repoWithout = TempGitRepo.Create(remoteUrl: "https://github.com/org/myrepo");
        var svc = CreateService();

        var idWith = await svc.GetRepoIdentityAsync(repoWith.Path);
        var idWithout = await svc.GetRepoIdentityAsync(repoWithout.Path);

        idWith.Should().Be(idWithout);
    }

    [Fact]
    public async Task GetRepoIdentityAsync_CaseInsensitive_SameUrlDifferentCase_SameId()
    {
        using var repo1 = TempGitRepo.Create(remoteUrl: "https://GITHUB.COM/Org/MyRepo");
        using var repo2 = TempGitRepo.Create(remoteUrl: "https://github.com/org/myrepo");
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo1.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo2.Path);

        id1.Should().Be(id2);
    }

    [Fact]
    public async Task GetRepoIdentityAsync_NoRemote_ReturnsLocalPrefixedId()
    {
        using var repo = TempGitRepo.CreateLocal();
        var svc = CreateService();

        var result = await svc.GetRepoIdentityAsync(repo.Path);

        result.Value.Should().StartWith("local-");
        result.Value.Should().HaveLength("local-".Length + 16);
    }

    [Fact]
    public async Task GetRepoIdentityAsync_SamePath_AlwaysSameId()
    {
        using var repo = TempGitRepo.CreateLocal();
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo.Path);

        id1.Should().Be(id2);
    }

    [Fact]
    public async Task GetRepoIdentityAsync_InvalidPath_ThrowsDirectoryNotFoundException()
    {
        var svc = CreateService();

        var act = async () => await svc.GetRepoIdentityAsync("/nonexistent/path/codemap");

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task GetRepoIdentityAsync_NotAGitRepo_ThrowsRepositoryNotFoundException()
    {
        string plainDir = Path.Combine(Path.GetTempPath(), "codemap-plain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plainDir);

        try
        {
            var svc = CreateService();
            var act = async () => await svc.GetRepoIdentityAsync(plainDir);

            await act.Should().ThrowAsync<RepositoryNotFoundException>();
        }
        finally
        {
            Directory.Delete(plainDir, recursive: true);
        }
    }

    // ===== GetCurrentCommitAsync =====

    [Fact]
    public async Task GetCurrentCommitAsync_WithOneCommit_ReturnsSha()
    {
        using var repo = TempGitRepo.Create();
        string expectedSha = repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var result = await svc.GetCurrentCommitAsync(repo.Path);

        result.Value.Should().HaveLength(40);
        result.Value.Should().MatchRegex("^[0-9a-f]{40}$");
        result.Value.Should().Be(expectedSha);
    }

    [Fact]
    public async Task GetCurrentCommitAsync_AfterSecondCommit_ReturnsLatestSha()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("file1.cs", "first");
        string secondSha = repo.CommitFile("file2.cs", "second");
        var svc = CreateService();

        var result = await svc.GetCurrentCommitAsync(repo.Path);

        result.Value.Should().Be(secondSha);
    }

    [Fact]
    public async Task GetCurrentCommitAsync_EmptyRepo_ThrowsInvalidOperation()
    {
        using var repo = TempGitRepo.Create(remoteName: null);
        var svc = CreateService();

        var act = async () => await svc.GetCurrentCommitAsync(repo.Path);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unborn HEAD*");
    }

    // ===== GetCurrentBranchAsync =====

    [Fact]
    public async Task GetCurrentBranchAsync_OnDefaultBranch_ReturnsBranchName()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var result = await svc.GetCurrentBranchAsync(repo.Path);

        result.Should().BeOneOf("main", "master");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_OnFeatureBranch_ReturnsBranchName()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        repo.CreateBranch("feature/my-feature");
        var svc = CreateService();

        var result = await svc.GetCurrentBranchAsync(repo.Path);

        result.Should().Be("feature/my-feature");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_DetachedHead_ReturnsHEAD()
    {
        using var repo = TempGitRepo.Create();
        string sha = repo.CommitFile("README.md", "hello");
        repo.CommitFile("other.cs", "second");
        repo.DetachHead(sha);
        var svc = CreateService();

        var result = await svc.GetCurrentBranchAsync(repo.Path);

        result.Should().Be("HEAD");
    }

    // ===== GetChangedFilesAsync =====

    [Fact]
    public async Task GetChangedFilesAsync_NoChanges_ReturnsEmptyList()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChangedFilesAsync_OneFileAdded_ReturnsAdded()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "hello");
        repo.CommitFile("new.cs", "content");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle(c =>
            c.FilePath.Value == "new.cs" && c.Kind == Core.Models.FileChangeKind.Added);
    }

    [Fact]
    public async Task GetChangedFilesAsync_OneFileModified_ReturnsModified()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "hello");
        repo.CommitFile("README.md", "modified content");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle(c =>
            c.FilePath.Value == "README.md" && c.Kind == Core.Models.FileChangeKind.Modified);
    }

    [Fact]
    public async Task GetChangedFilesAsync_OneFileDeleted_ReturnsDeleted()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("file.cs", "content");
        repo.DeleteFile("file.cs");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle(c =>
            c.FilePath.Value == "file.cs" && c.Kind == Core.Models.FileChangeKind.Deleted);
    }

    [Fact]
    public async Task GetChangedFilesAsync_UnstagedChanges_IncludesWorktreeDiff()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("README.md", "hello");
        repo.ModifyFile("README.md", "modified but not staged");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle(c =>
            c.FilePath.Value == "README.md" && c.Kind == Core.Models.FileChangeKind.Modified);
    }

    [Fact]
    public async Task GetChangedFilesAsync_MultipleChanges_ReturnsAll()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("keep.cs", "keep");
        repo.CommitFile("keep.cs", "also commit");  // reuse to get a commit
        _ = repo.CommitFile("file-a.cs", "a");
        _ = repo.CommitFile("file-b.cs", "b");
        _ = repo.CommitFile("file-c.cs", "c");

        // Reset by doing a new baseline after all three exist
        string newBaseline = repo.CommitFile("marker.txt", "baseline");
        repo.CommitFile("file-a.cs", "modified");  // modify a
        repo.DeleteFile("file-b.cs");               // delete b
        repo.CommitFile("file-d.cs", "new");        // add d
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(newBaseline));

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.FilePath.Value == "file-a.cs" && c.Kind == Core.Models.FileChangeKind.Modified);
        result.Should().Contain(c => c.FilePath.Value == "file-b.cs" && c.Kind == Core.Models.FileChangeKind.Deleted);
        result.Should().Contain(c => c.FilePath.Value == "file-d.cs" && c.Kind == Core.Models.FileChangeKind.Added);
    }

    [Fact]
    public async Task GetChangedFilesAsync_InvalidBaselineCommit_Throws()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        var svc = CreateService();
        var fakeSha = CommitSha.From(new string('a', 40));

        var act = async () => await svc.GetChangedFilesAsync(repo.Path, fakeSha);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{fakeSha.Value}*");
    }

    [Fact]
    public async Task GetChangedFilesAsync_PathsAreRepoRelative_ForwardSlash()
    {
        using var repo = TempGitRepo.Create();
        string baselineSha = repo.CommitFile("src/Services/OrderService.cs", "content");
        repo.ModifyFile("src/Services/OrderService.cs", "modified");
        var svc = CreateService();

        var result = await svc.GetChangedFilesAsync(repo.Path, CommitSha.From(baselineSha));

        result.Should().ContainSingle();
        result[0].FilePath.Value.Should().Be("src/Services/OrderService.cs");
        result[0].FilePath.Value.Should().NotContain("\\");
        result[0].FilePath.Value.Should().NotStartWith("/");
    }

    // ===== IsCleanAsync =====

    [Fact]
    public async Task IsCleanAsync_CleanRepo_ReturnsTrue()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_UnstagedChanges_ReturnsFalse()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        repo.ModifyFile("README.md", "dirty");
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_StagedChanges_ReturnsFalse()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        repo.ModifyFile("README.md", "staged content");
        repo.StageFile("README.md");
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_UntrackedFile_ReturnsFalse()
    {
        using var repo = TempGitRepo.Create();
        repo.CommitFile("README.md", "hello");
        repo.CreateUnstagedFile("untracked.cs", "new");
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_EmptyRepo_ReturnsTrue()
    {
        using var repo = TempGitRepo.CreateLocal();
        var svc = CreateService();

        var result = await svc.IsCleanAsync(repo.Path);

        result.Should().BeTrue();
    }
}
