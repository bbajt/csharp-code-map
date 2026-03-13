namespace CodeMap.Storage.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for <see cref="BaselineDbFactory.ListBaselinesAsync"/>.
/// </summary>
public sealed class BaselineStoreListTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private static readonly RepoId TestRepo = RepoId.From("test-repo");

    private BaselineDbFactory CreateFactory() =>
        new(_tempDir, NullLogger<BaselineDbFactory>.Instance);

    private static readonly string ValidSha1 = new string('a', 40);
    private static readonly string ValidSha2 = new string('b', 40);
    private static readonly string ValidSha3 = new string('c', 40);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ListBaselines_EmptyDirectory_ReturnsEmpty()
    {
        var factory = CreateFactory();
        var result = await factory.ListBaselinesAsync(TestRepo);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListBaselines_NoDirectoryForRepo_ReturnsEmpty()
    {
        var factory = CreateFactory();
        var otherRepo = RepoId.From("other-repo");
        var result = await factory.ListBaselinesAsync(otherRepo);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListBaselines_SingleBaseline_ReturnsIt()
    {
        var factory = CreateFactory();
        // Create a real baseline DB
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        var result = await factory.ListBaselinesAsync(TestRepo);

        result.Should().HaveCount(1);
        result[0].CommitSha.Value.Should().Be(ValidSha1);
        result[0].SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListBaselines_MultipleBaselines_ReturnsAllSortedByDateDescending()
    {
        var factory = CreateFactory();

        // Create three baselines with small delays to get distinct timestamps
        foreach (var sha in new[] { ValidSha1, ValidSha2, ValidSha3 })
        {
            using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(sha));
        }
        SqliteConnection.ClearAllPools();

        var result = await factory.ListBaselinesAsync(TestRepo);

        result.Should().HaveCount(3);
        // Sorted newest-first (or at minimum stable)
        result.Select(b => b.CommitSha.Value).Should().BeEquivalentTo([ValidSha1, ValidSha2, ValidSha3]);
        // Verify descending order
        result.Should().BeInDescendingOrder(b => b.CreatedAt);
    }

    [Fact]
    public async Task ListBaselines_TotalSizeNonZero_WhenFileExists()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        var result = await factory.ListBaselinesAsync(TestRepo);

        result[0].SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListBaselines_NonDbFilesIgnored()
    {
        var factory = CreateFactory();

        // Create a real baseline
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        // Drop a non-.db file into the repo directory
        var dir = factory.GetBaselineDirectory(TestRepo);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "ignore me");
        File.WriteAllText(Path.Combine(dir, "random.json"), "{}");

        var result = await factory.ListBaselinesAsync(TestRepo);

        result.Should().HaveCount(1, "only .db files with SHA names should be included");
    }

    [Fact]
    public async Task ListBaselines_NonShaDbFilesIgnored()
    {
        var factory = CreateFactory();

        // Create a real baseline
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        // Drop a .db file with a non-SHA name
        var dir = factory.GetBaselineDirectory(TestRepo);
        File.WriteAllText(Path.Combine(dir, "notasha.db"), "data");
        File.WriteAllText(Path.Combine(dir, "short.db"), "data");

        var result = await factory.ListBaselinesAsync(TestRepo);

        result.Should().HaveCount(1, "only 40-char hex .db files should be included");
    }

    [Fact]
    public async Task ListBaselines_IsCurrentHeadDefault_False()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        var result = await factory.ListBaselinesAsync(TestRepo);

        result[0].IsCurrentHead.Should().BeFalse("factory does not resolve HEAD — caller enriches");
    }

    [Fact]
    public async Task ListBaselines_IsActiveWorkspaceBaseDefault_False()
    {
        var factory = CreateFactory();
        using var conn = factory.OpenOrCreate(TestRepo, CommitSha.From(ValidSha1));
        conn.Dispose();
        SqliteConnection.ClearAllPools();

        var result = await factory.ListBaselinesAsync(TestRepo);

        result[0].IsActiveWorkspaceBase.Should().BeFalse("factory does not query workspaces — caller enriches");
    }

    [Fact]
    public async Task ListBaselines_CancellationRequested_Throws()
    {
        var factory = CreateFactory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.ListBaselinesAsync(TestRepo, cts.Token));
    }
}
