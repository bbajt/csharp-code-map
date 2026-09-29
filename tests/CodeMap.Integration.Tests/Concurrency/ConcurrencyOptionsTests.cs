namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>Unit tests for <see cref="ConcurrencyOptions.Parse"/>.</summary>
public class ConcurrencyOptionsTests
{
    private const string DataRoot = "data-root";

    [Fact]
    public void Parse_NoArgs_UsesDefaults()
    {
        var (options, error) = ConcurrencyOptions.Parse([], DataRoot);

        error.Should().BeNull();
        options!.AgentCounts.Should().Equal(1, 4);
        options.Duration.Should().Be(TimeSpan.FromSeconds(60));
        options.Idle.Should().Be(TimeSpan.FromSeconds(10));
        options.Seed.Should().Be(42);
        options.WorkspaceMode.Should().Be(WorkspaceMode.Isolated);
        options.KeepArtifacts.Should().BeFalse();
        options.CacheDir.Should().BeNull();
        options.DaemonPath.Should().EndWith("CodeMap.Daemon.dll");
    }

    [Fact]
    public void Parse_AllFlags_AreApplied()
    {
        var (options, error) = ConcurrencyOptions.Parse(
            ["--agents", "1, 8,16", "--duration", "30", "--idle", "0", "--seed", "7",
             "--workspace-mode", "shared", "--cache-dir", "cache", "--call-timeout", "5", "--keep"],
            DataRoot);

        error.Should().BeNull();
        options!.AgentCounts.Should().Equal(1, 8, 16);
        options.Duration.Should().Be(TimeSpan.FromSeconds(30));
        options.Idle.Should().Be(TimeSpan.Zero);
        options.Seed.Should().Be(7);
        options.WorkspaceMode.Should().Be(WorkspaceMode.Shared);
        options.CacheDir.Should().Be("cache");
        options.CallTimeout.Should().Be(TimeSpan.FromSeconds(5));
        options.KeepArtifacts.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoRepoNameOrMemoryFlags_LeavesThemNull()
    {
        var (options, _) = ConcurrencyOptions.Parse([], DataRoot);

        options!.RepoNames.Should().BeNull();
        options.MaxMemoryGb.Should().BeNull();
        options.Restore.Should().BeTrue("worktrees are restored unless --no-restore");
    }

    [Fact]
    public void Parse_NoRestore_DisablesRestore()
    {
        var (options, error) = ConcurrencyOptions.Parse(["--no-restore"], DataRoot);

        error.Should().BeNull();
        options!.Restore.Should().BeFalse();
    }

    [Fact]
    public void Parse_RepoName_CommaListIsSplitAndTrimmed()
    {
        var (options, error) = ConcurrencyOptions.Parse(["--repo-name", "eShopOnWeb, nopCommerce ,CodeMap"], DataRoot);

        error.Should().BeNull();
        options!.RepoNames.Should().Equal("eShopOnWeb", "nopCommerce", "CodeMap");
    }

    [Fact]
    public void Parse_RepoAndRepoName_Together_IsError()
    {
        var (options, error) = ConcurrencyOptions.Parse(["--repo", "large", "--repo-name", "nopCommerce"], DataRoot);

        options.Should().BeNull();
        error.Should().Contain("--repo-name").And.Contain("--repo");
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("48", 48.0)]
    public void Parse_MaxMemoryGb_IsParsedInvariant(string raw, double expected)
    {
        var (options, error) = ConcurrencyOptions.Parse(["--max-memory-gb", raw], DataRoot);

        error.Should().BeNull();
        options!.MaxMemoryGb.Should().Be(expected);
    }

    [Theory]
    [InlineData("--agents", "0")]
    [InlineData("--agents", "65")]
    [InlineData("--agents", "x")]
    [InlineData("--duration", "0")]
    [InlineData("--duration", "-5")]
    [InlineData("--workspace-mode", "both")]
    [InlineData("--daemon", "does-not-exist.dll")]
    [InlineData("--max-memory-gb", "0")]
    [InlineData("--max-memory-gb", "-1")]
    [InlineData("--max-memory-gb", "lots")]
    [InlineData("--repo-name", " , ")]
    public void Parse_InvalidValue_ReturnsErrorNamingFlag(string flag, string value)
    {
        var (options, error) = ConcurrencyOptions.Parse([flag, value], DataRoot);

        options.Should().BeNull();
        error.Should().Contain(flag);
    }
}
