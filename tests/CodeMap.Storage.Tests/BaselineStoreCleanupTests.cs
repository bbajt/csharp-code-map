namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for <see cref="BaselineDbFactory.CleanupBaselinesAsync"/>.
/// </summary>
public sealed class BaselineStoreCleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private static readonly RepoId TestRepo = RepoId.From("test-repo");

    private static readonly CommitSha ShaA = CommitSha.From(new string('a', 40));
    private static readonly CommitSha ShaB = CommitSha.From(new string('b', 40));
    private static readonly CommitSha ShaC = CommitSha.From(new string('c', 40));
    private static readonly CommitSha ShaD = CommitSha.From(new string('d', 40));

    private BaselineDbFactory CreateFactory() =>
        new(_tempDir, NullLogger<BaselineDbFactory>.Instance);

    private static readonly IReadOnlySet<CommitSha> NoWorkspaces =
        new HashSet<CommitSha>();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Creates a plain placeholder file in the baseline directory.
    /// Uses a plain file write (not SQLite) to avoid any file-locking issues on Windows.
    /// </summary>
    private void CreateBaseline(BaselineDbFactory factory, CommitSha sha, TimeSpan? ageOffset = null)
    {
        var path = factory.GetDbPath(TestRepo, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write minimal content so SizeBytes > 0
        File.WriteAllBytes(path, new byte[512]);

        if (ageOffset.HasValue)
            File.SetCreationTimeUtc(path, DateTime.UtcNow + ageOffset.Value);
    }

    [Fact]
    public async Task Cleanup_NoBaselines_ReturnsZero()
    {
        var factory = CreateFactory();
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces);

        result.BaselinesRemoved.Should().Be(0);
        result.BytesReclaimed.Should().Be(0);
        result.RemovedCommits.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_DryRunDefault_DoesNotDeleteFiles()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head
        CreateBaseline(factory, ShaB); // candidate

        // keepCount=0 → keep nothing, so ShaB is a candidate; but dry_run=true (default)
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0);

        result.DryRun.Should().BeTrue();
        File.Exists(factory.GetDbPath(TestRepo, ShaB)).Should().BeTrue("dry run should not delete files");
    }

    [Fact]
    public async Task Cleanup_DryRun_ReportsWhatWouldBeDeleted()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate

        // keepCount=0 → ShaB is not kept; ShaA is protected; ShaB would be deleted
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: true);

        result.DryRun.Should().BeTrue();
        result.BaselinesRemoved.Should().Be(1);
        result.RemovedCommits.Should().ContainSingle(c => c.Value == ShaB.Value);
    }

    [Fact]
    public async Task Cleanup_Execute_DeletesFiles()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate

        // keepCount=0 → ShaB is not kept → deleted; ShaA is protected → kept
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: false);

        result.DryRun.Should().BeFalse();
        result.BaselinesRemoved.Should().Be(1);
        File.Exists(factory.GetDbPath(TestRepo, ShaB)).Should().BeFalse("should have been deleted");
        File.Exists(factory.GetDbPath(TestRepo, ShaA)).Should().BeTrue("current head must not be deleted");
    }

    [Fact]
    public async Task Cleanup_NeverDeletesCurrentHead()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA);

        // keepCount=0 would normally remove everything, but HEAD is protected
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: false);

        result.BaselinesRemoved.Should().Be(0);
        File.Exists(factory.GetDbPath(TestRepo, ShaA)).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_NeverDeletesWorkspaceReferenced()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA);
        CreateBaseline(factory, ShaB);

        var workspaceBaseCommits = new HashSet<CommitSha> { ShaB };

        // keepCount=1 (ShaA is newest and current head), ShaB is workspace-referenced
        var result = await factory.CleanupBaselinesAsync(
            TestRepo, ShaA, workspaceBaseCommits, keepCount: 1, dryRun: false);

        result.BaselinesRemoved.Should().Be(0);
        File.Exists(factory.GetDbPath(TestRepo, ShaB)).Should().BeTrue("workspace-referenced baseline must not be deleted");
    }

    [Fact]
    public async Task Cleanup_KeepCount_KeepsNewestN()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB);
        CreateBaseline(factory, ShaC);

        // keepCount=3 → all 3 are in the keep set; nothing gets removed
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 3, dryRun: false);

        result.BaselinesRemoved.Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_KeepCount_RemovesExcessOldBaselines()
    {
        var factory = CreateFactory();
        // 4 baselines; ShaA is current head. ShaB, ShaC, ShaD are old candidates.
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate
        CreateBaseline(factory, ShaC); // candidate
        CreateBaseline(factory, ShaD); // candidate

        // keepCount=0 → all non-protected are candidates; ShaA is protected
        // ShaB, ShaC, ShaD all removed
        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: false);

        result.BaselinesRemoved.Should().Be(3);
        result.RemovedCommits.Select(c => c.Value).Should().BeEquivalentTo([ShaB.Value, ShaC.Value, ShaD.Value]);
    }

    [Fact]
    public async Task Cleanup_AllProtected_RemovesNothing()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA);
        CreateBaseline(factory, ShaB);

        var workspaceBaseCommits = new HashSet<CommitSha> { ShaB };

        var result = await factory.CleanupBaselinesAsync(
            TestRepo, ShaA, workspaceBaseCommits, keepCount: 0, dryRun: false);

        result.BaselinesRemoved.Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_BytesReclaimed_NonZeroWhenDeleted()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate

        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: false);

        result.BytesReclaimed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Cleanup_KeptCommits_IncludesProtectedAndRetained()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate

        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: false);

        result.KeptCommits.Should().ContainSingle(c => c.Value == ShaA.Value);
        result.RemovedCommits.Should().ContainSingle(c => c.Value == ShaB.Value);
    }

    [Fact]
    public async Task Cleanup_DryRun_BytesReclaimed_CalculatedFromFileSizes()
    {
        var factory = CreateFactory();
        CreateBaseline(factory, ShaA); // current head (protected)
        CreateBaseline(factory, ShaB); // candidate

        var result = await factory.CleanupBaselinesAsync(TestRepo, ShaA, NoWorkspaces, keepCount: 0, dryRun: true);

        result.DryRun.Should().BeTrue();
        result.BytesReclaimed.Should().BeGreaterThan(0, "dry run still reports bytes that would be reclaimed");
        File.Exists(factory.GetDbPath(TestRepo, ShaB)).Should().BeTrue();
    }
}
