namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Tests for <see cref="EngineBaselineCacheManager"/> pull/push publication — neither may delete a
/// complete baseline that already exists at the target (F6 / ADR-040, PHASE-21-02 T01).
/// </summary>
public sealed class EngineBaselineCacheManagerTests : IDisposable
{
    private const string RepoName = "cache-repo";
    private static readonly RepoId Repo = RepoId.From(RepoName);
    private static readonly CommitSha Sha = CommitSha.From(new string('d', 40));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-cache-{Guid.NewGuid():N}");

    private string LocalStore => Path.Combine(_root, "local");

    private string SharedCache => Path.Combine(_root, "shared");

    private string LocalDir => Path.Combine(LocalStore, RepoName, "baselines", Sha.Value);

    // Shared-cache layout has no "baselines" level: <cache>/<repo>/<sha>.
    private string CacheDir => Path.Combine(SharedCache, RepoName, Sha.Value);

    /// <summary>Removes the temp root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Push_BlankSharedCacheDir_IsDisabled_WritesNothingRelativeToWorkingDirectory(string blank)
    {
        // F11: a blank cache dir was treated as enabled; Path.Combine("", repo, sha) is relative,
        // so the push landed in the process's working directory (for a daemon: the repo root).
        await BuildInto(LocalStore);
        var strayTarget = Path.GetFullPath(Path.Combine(blank, RepoName));
        try
        {
            var manager = new EngineBaselineCacheManager(LocalStore, blank);
            await manager.PushAsync(Repo, Sha, TestContext.Current.CancellationToken);

            Directory.Exists(strayTarget).Should().BeFalse("a blank shared cache dir means caching is disabled");
            (await manager.ExistsInCacheAsync(Repo, Sha, TestContext.Current.CancellationToken)).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(strayTarget)) Directory.Delete(strayTarget, recursive: true);
        }
    }

    [Fact]
    public async Task Pull_LocalCompleteExists_DoesNotDelete()
    {
        await SeedCache();
        await BuildInto(LocalStore);
        var marker = Path.Combine(LocalDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "local", TestContext.Current.CancellationToken);

        var path = await Manager().PullAsync(Repo, Sha, TestContext.Current.CancellationToken);

        path.Should().Be(LocalDir);
        File.Exists(marker).Should().BeTrue("an existing complete local baseline must not be replaced");
    }

    [Fact]
    public async Task Push_CacheCompleteExists_DoesNotDelete()
    {
        await BuildInto(LocalStore);
        await SeedCache();
        var marker = Path.Combine(CacheDir, "marker.txt");
        await File.WriteAllTextAsync(marker, "shared", TestContext.Current.CancellationToken);

        await Manager().PushAsync(Repo, Sha, TestContext.Current.CancellationToken);

        File.Exists(marker).Should().BeTrue("an existing complete cache entry must not be replaced");
    }

    [Fact]
    public async Task Pull_LocalGutted_ReplacedFromCache()
    {
        await SeedCache();
        await BuildInto(LocalStore);
        File.Delete(Path.Combine(LocalDir, "search.idx"));

        var path = await Manager().PullAsync(Repo, Sha, TestContext.Current.CancellationToken);

        path.Should().Be(LocalDir);
        BaselinePublisher.IsComplete(LocalDir).Should().BeTrue();
    }

    [Fact]
    public async Task ExistsInCache_GuttedEntry_False()
    {
        await SeedCache();
        File.Delete(Path.Combine(CacheDir, "edges.seg"));

        (await Manager().ExistsInCacheAsync(Repo, Sha, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Pull_ConcurrentWithBuilderPublish_BothConverge()
    {
        await SeedCache();
        for (var i = 0; i < 10; i++)
        {
            if (Directory.Exists(LocalStore)) Directory.Delete(LocalStore, recursive: true);

            var pull = Manager().PullAsync(Repo, Sha, TestContext.Current.CancellationToken);
            var build = BuildInto(LocalStore);
            await Task.WhenAll(pull, build);

            (await pull).Should().Be(LocalDir, $"iteration {i}");
            BaselinePublisher.IsComplete(LocalDir).Should().BeTrue($"iteration {i}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private EngineBaselineCacheManager Manager() => new(LocalStore, SharedCache);

    /// <summary>Builds a baseline in a seed store and copies it into the shared-cache layout.</summary>
    private async Task SeedCache()
    {
        var seedStore = Path.Combine(_root, "seed");
        if (!Directory.Exists(seedStore)) await BuildInto(seedStore);
        var seedDir = Path.Combine(seedStore, RepoName, "baselines", Sha.Value);
        Directory.CreateDirectory(CacheDir);
        foreach (var file in Directory.GetFiles(seedDir))
            File.Copy(file, Path.Combine(CacheDir, Path.GetFileName(file)), overwrite: true);
    }

    private static async Task BuildInto(string storeRoot)
    {
        var result = await new EngineBaselineBuilder(Path.Combine(storeRoot, RepoName))
            .BuildAsync(Input(), CancellationToken.None);
        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    private static BaselineBuildInput Input()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/A.cs"), "aa" + new string('0', 62), "App", "public class A { }"),
        };
        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:App.A"), "global::App.A", SymbolKind.Class,
                "public class A", "App", FilePath.From("src/A.cs"), 1, 1, "public", Confidence.High),
        };
        return new BaselineBuildInput(Sha.Value, @"C:\repo", symbols, files, [], [], []);
    }
}
