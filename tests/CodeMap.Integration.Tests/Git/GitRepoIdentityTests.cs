namespace CodeMap.Integration.Tests.Git;

using CodeMap.Git;
using CodeMap.TestUtilities.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Integration")]
public class GitRepoIdentityTests
{
    private static GitService CreateService() =>
        new(NullLogger<GitService>.Instance);

    [Fact]
    public async Task RepoId_SameRemoteUrl_AlwaysProduceSameId()
    {
        const string url = "https://github.com/test/same-repo";
        using var repo1 = TempGitRepo.Create(remoteUrl: url);
        using var repo2 = TempGitRepo.Create(remoteUrl: url);
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo1.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo2.Path);

        id1.Should().Be(id2);
    }

    [Fact]
    public async Task RepoId_DifferentRemoteUrls_ProduceDifferentIds()
    {
        using var repo1 = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo-alpha");
        using var repo2 = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo-beta");
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo1.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo2.Path);

        id1.Should().NotBe(id2);
    }

    [Fact]
    public async Task RepoId_UrlWithAndWithoutGitSuffix_SameId()
    {
        using var repoWith = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo.git");
        using var repoWithout = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo");
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repoWith.Path);
        var id2 = await svc.GetRepoIdentityAsync(repoWithout.Path);

        id1.Should().Be(id2);
    }

    [Fact]
    public async Task RepoId_UrlWithAndWithoutTrailingSlash_SameId()
    {
        using var repoWith = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo/");
        using var repoWithout = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo");
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repoWith.Path);
        var id2 = await svc.GetRepoIdentityAsync(repoWithout.Path);

        id1.Should().Be(id2);
    }

    [Fact]
    public async Task RepoId_HttpVsHttps_DifferentIds()
    {
        using var repoHttp = TempGitRepo.Create(remoteUrl: "http://github.com/org/repo");
        using var repoHttps = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo");
        var svc = CreateService();

        var httpId = await svc.GetRepoIdentityAsync(repoHttp.Path);
        var httpsId = await svc.GetRepoIdentityAsync(repoHttps.Path);

        httpId.Should().NotBe(httpsId);
    }

    [Fact]
    public async Task RepoId_SshVsHttps_DifferentIds()
    {
        using var repoSsh = TempGitRepo.Create(remoteUrl: "git@github.com:org/repo.git");
        using var repoHttps = TempGitRepo.Create(remoteUrl: "https://github.com/org/repo");
        var svc = CreateService();

        var sshId = await svc.GetRepoIdentityAsync(repoSsh.Path);
        var httpsId = await svc.GetRepoIdentityAsync(repoHttps.Path);

        sshId.Should().NotBe(httpsId);
    }

    [Fact]
    public async Task RepoId_LocalRepo_StableAcrossMultipleCalls()
    {
        using var repo = TempGitRepo.CreateLocal();
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo.Path);
        var id3 = await svc.GetRepoIdentityAsync(repo.Path);

        id1.Should().Be(id2);
        id2.Should().Be(id3);
    }

    [Fact]
    public async Task RepoId_LocalRepo_DifferentPaths_DifferentIds()
    {
        using var repo1 = TempGitRepo.CreateLocal();
        using var repo2 = TempGitRepo.CreateLocal();
        var svc = CreateService();

        var id1 = await svc.GetRepoIdentityAsync(repo1.Path);
        var id2 = await svc.GetRepoIdentityAsync(repo2.Path);

        id1.Should().NotBe(id2);
    }
}
