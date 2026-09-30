namespace CodeMap.Storage.Engine.Tests;

using System.IO.MemoryMappedFiles;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-10 T01: <see cref="EngineBaselineScanner.RemoveRepoAsync"/> deleted <c>store/&lt;repo&gt;/</c>
/// recursively (overlays included, unreported), swallowed IO errors and still reported every baseline as
/// removed. Baselines are built through the real <see cref="CustomSymbolStore"/>, so its reader cache and
/// overlays are the ones a daemon would hold.
/// </summary>
public sealed class EngineBaselineScannerRemoveRepoTests : IAsyncLifetime
{
    private static readonly RepoId Repo = RepoId.From("remove-repo");
    private static readonly CommitSha ShaA = CommitSha.From("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly CommitSha ShaB = CommitSha.From("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"codemap-removerepo-{Guid.NewGuid():N}");
    private CustomSymbolStore _store = null!;
    private EngineBaselineScanner _scanner = null!;

    private string RepoDir => Path.Combine(_storeDir, Repo.Value);
    private string BaselineDir(CommitSha sha) => Path.Combine(RepoDir, "baselines", sha.Value);
    private string OverlayDir(string ws) => Path.Combine(RepoDir, "overlays", ws);

    /// <summary>Builds two baselines through the real store.</summary>
    public async ValueTask InitializeAsync()
    {
        _store = new CustomSymbolStore(_storeDir);
        await _store.CreateBaselineAsync(Repo, ShaA, Data(), @"C:\repo", CancellationToken.None);
        await _store.CreateBaselineAsync(Repo, ShaB, Data(), @"C:\repo", CancellationToken.None);
        _scanner = new EngineBaselineScanner(_storeDir, _store);
    }

    /// <summary>Releases the store and removes the temp dir.</summary>
    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try { Directory.Delete(_storeDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RemoveRepo_BaselineOpenInThisProcess_RemovedCompletely()
    {
        // The reader cache now maps ShaA's segments — the "remove the repo you just queried" case.
        await _store.SearchSymbolsAsync(Repo, ShaA, "Widget", null, 5, TestContext.Current.CancellationToken);

        var response = await _scanner.RemoveRepoAsync(Repo, dryRun: false, TestContext.Current.CancellationToken);

        Directory.Exists(RepoDir).Should().BeFalse("this process's own readers must not block removal");
        response.RemovedCommits.Should().BeEquivalentTo([ShaA, ShaB]);
        response.SkippedInUse.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task RemoveRepo_SegmentMappedByAnotherHandle_BaselineIntactAndSkipped()
    {
        // Stand-in for another agent's daemon that has ShaA open. On Windows the mapping blocks delete and
        // rename; on Linux both succeed under a mapping, so only the exclusive-open probe detects it.
        // Either way the baseline must be left intact.
        using var mmf = MemoryMappedFile.CreateFromFile(
            Path.Combine(BaselineDir(ShaA), "symbols.seg"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

        var response = await _scanner.RemoveRepoAsync(Repo, dryRun: false, TestContext.Current.CancellationToken);

        BaselinePublisher.IsComplete(BaselineDir(ShaA)).Should().BeTrue(
            "a baseline another process is reading must be left complete, never half-deleted");
        response.SkippedInUse.Should().Equal(ShaA);
        response.RemovedCommits.Should().Equal(ShaB);
        response.BaselinesRemoved.Should().Be(1);
        Directory.Exists(BaselineDir(ShaB)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveRepo_IdleOverlay_RemovedAndReported()
    {
        CreateOverlayDir("ws-idle");

        var response = await _scanner.RemoveRepoAsync(Repo, dryRun: false, TestContext.Current.CancellationToken);

        response.WorkspacesRemoved.Should().Equal("ws-idle");
        Directory.Exists(OverlayDir("ws-idle")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveRepo_OverlayOpenInThisProcess_ClosedAndRemoved()
    {
        var (reader, _) = _store.GetOrOpenBaseline(Repo.Value, ShaA.Value);
        _store.GetOrCreateOverlay(Repo.Value, "ws-mine", reader);   // holds overlay.lock in this process

        var response = await _scanner.RemoveRepoAsync(Repo, dryRun: false, TestContext.Current.CancellationToken);

        response.WorkspacesInUse.Should().BeNullOrEmpty("this process's own overlays are closed first");
        response.WorkspacesRemoved.Should().Equal("ws-mine");
        Directory.Exists(RepoDir).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveRepo_OverlayLockHeldElsewhere_NothingDeleted()
    {
        CreateOverlayDir("ws-other");
        CreateOverlayDir("ws-idle");

        // Stand-in for another agent's daemon holding its workspace (ADR-044 writer lock).
        using (new FileStream(Path.Combine(OverlayDir("ws-other"), "overlay.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var response = await _scanner.RemoveRepoAsync(Repo, dryRun: false, TestContext.Current.CancellationToken);

            response.WorkspacesInUse.Should().Equal("ws-other");
            response.BaselinesRemoved.Should().Be(0);
            response.RemovedCommits.Should().BeEmpty();
            response.WorkspacesRemoved.Should().BeNullOrEmpty();
        }

        BaselinePublisher.IsComplete(BaselineDir(ShaA)).Should().BeTrue();
        BaselinePublisher.IsComplete(BaselineDir(ShaB)).Should().BeTrue();
        Directory.Exists(OverlayDir("ws-other")).Should().BeTrue();
        Directory.Exists(OverlayDir("ws-idle")).Should().BeTrue("nothing is deleted while another process has a workspace");
    }

    [Fact]
    public async Task RemoveRepo_DryRun_ReportsWorkspacesAndDeletesNothing()
    {
        CreateOverlayDir("ws-idle");

        var response = await _scanner.RemoveRepoAsync(Repo, dryRun: true, TestContext.Current.CancellationToken);

        response.DryRun.Should().BeTrue();
        response.RemovedCommits.Should().BeEquivalentTo([ShaA, ShaB]);
        response.WorkspacesRemoved.Should().Equal("ws-idle");
        Directory.Exists(OverlayDir("ws-idle")).Should().BeTrue();
        BaselinePublisher.IsComplete(BaselineDir(ShaA)).Should().BeTrue();
    }

    [Fact]
    public void SweepQuarantine_RemovedPrefixLeftovers_Deleted()
    {
        // A moved-aside baseline whose delete failed is retried by the next sweep.
        var tempRoot = Path.Combine(RepoDir, "temp");
        var leftover = Path.Combine(tempRoot, $"removed-{ShaA.Value}-20260929000000-0123abcd");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "symbols.seg"), "x");

        BaselinePublisher.SweepQuarantine(tempRoot);

        Directory.Exists(leftover).Should().BeFalse();
    }

    private void CreateOverlayDir(string ws)
    {
        var dir = OverlayDir(ws);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "overlay.wal"), "");
    }

    private static CompilationResult Data()
    {
        var path = FilePath.From("src/App/Widget.cs");
        var files = new List<ExtractedFile>
        {
            new("f1", path, "01" + new string('0', 62), "App", "public class Widget { public void Run() { } }"),
        };
        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:App.Widget"), "global::App.Widget",
                SymbolKind.Class, "public class Widget", "App", path, 1, 1, "public", Confidence.High),
        };
        return new CompilationResult(symbols, [], files, new IndexStats(symbols.Count, 0, files.Count, 0.0, Confidence.High));
    }
}
