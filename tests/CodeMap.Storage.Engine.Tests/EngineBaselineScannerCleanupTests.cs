namespace CodeMap.Storage.Engine.Tests;

using System.IO.MemoryMappedFiles;
using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-10 T02: <see cref="EngineBaselineScanner.CleanupBaselinesAsync"/> protected only the current
/// HEAD and this process's in-memory workspaces, so another agent's workspace baseline could be deleted,
/// and it deleted with a swallowed recursive delete (half-deleted on Windows when mapped). Baselines are
/// built through the real <see cref="CustomSymbolStore"/>.
/// </summary>
public sealed class EngineBaselineScannerCleanupTests : IAsyncLifetime
{
    private static readonly RepoId Repo = RepoId.From("cleanup-repo");
    private static readonly CommitSha ShaOld = CommitSha.From("1111111111111111111111111111111111111111");
    private static readonly CommitSha ShaMid = CommitSha.From("2222222222222222222222222222222222222222");
    private static readonly CommitSha ShaHead = CommitSha.From("3333333333333333333333333333333333333333");

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"codemap-cleanup-{Guid.NewGuid():N}");
    private CustomSymbolStore _store = null!;
    private EngineBaselineScanner _scanner = null!;

    private string RepoDir => Path.Combine(_storeDir, Repo.Value);
    private string BaselineDir(CommitSha sha) => Path.Combine(RepoDir, "baselines", sha.Value);
    private string OverlayDir(string name) => Path.Combine(RepoDir, "overlays", name);

    /// <summary>Builds three baselines through the real store.</summary>
    public async ValueTask InitializeAsync()
    {
        _store = new CustomSymbolStore(_storeDir);
        foreach (var sha in new[] { ShaOld, ShaMid, ShaHead })
            await _store.CreateBaselineAsync(Repo, sha, Data(), @"C:\repo", CancellationToken.None);
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

    private Task<CleanupResponse> CleanupAsync(bool dryRun = false) =>
        _scanner.CleanupBaselinesAsync(Repo, ShaHead, new HashSet<CommitSha>(), keepCount: 0,
            olderThanDays: null, dryRun: dryRun, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CreateOverlay_WritesMarkerWithBaselineCommit()
    {
        var overlays = new CustomEngineOverlayStore(_store, _storeDir);

        await overlays.CreateOverlayAsync(Repo, WorkspaceId.From("session"), ShaMid, TestContext.Current.CancellationToken);

        var marker = Path.Combine(OverlayDir("session"), "overlay.meta.json");
        File.Exists(marker).Should().BeTrue("the baseline a workspace depends on must be visible to other processes");
        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        doc.RootElement.GetProperty("baseline_commit_sha").GetString().Should().Be(ShaMid.Value);
    }

    [Fact]
    public async Task Cleanup_BaselineOfWorkspaceInAnotherProcess_Protected()
    {
        // Another agent's workspace (not in this process's registry) is based on ShaOld.
        WriteMarker("agent-b", ShaOld);

        var response = await CleanupAsync();

        BaselinePublisher.IsComplete(BaselineDir(ShaOld)).Should().BeTrue("agent B's workspace is based on it");
        response.ProtectedByWorkspaces.Should().Equal(ShaOld);
        response.RemovedCommits.Should().Equal(ShaMid);
        response.KeptCommits.Should().BeEquivalentTo([ShaOld, ShaHead]);
    }

    [Fact]
    public async Task Cleanup_SegmentMapped_BaselineIntactAndSkipped()
    {
        // Stand-in for another daemon reading ShaMid (probe detects it on Windows and Linux alike).
        using var mmf = MemoryMappedFile.CreateFromFile(
            Path.Combine(BaselineDir(ShaMid), "symbols.seg"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

        var response = await CleanupAsync();

        BaselinePublisher.IsComplete(BaselineDir(ShaMid)).Should().BeTrue("a baseline in use must never be half-deleted");
        response.SkippedInUse.Should().Equal(ShaMid);
        response.RemovedCommits.Should().Equal(ShaOld);
        response.BaselinesRemoved.Should().Be(1);
    }

    [Fact]
    public async Task Cleanup_BaselineOpenInThisProcess_Removed()
    {
        await _store.SearchSymbolsAsync(Repo, ShaMid, "Widget", null, 5, TestContext.Current.CancellationToken);

        var response = await CleanupAsync();

        response.RemovedCommits.Should().BeEquivalentTo([ShaOld, ShaMid]);
        Directory.Exists(BaselineDir(ShaMid)).Should().BeFalse("this process's own readers are released first");
    }

    [Fact]
    public async Task Cleanup_LiveOverlayWithoutMarker_NothingDeleted()
    {
        Directory.CreateDirectory(OverlayDir("old-agent"));
        using (new FileStream(Path.Combine(OverlayDir("old-agent"), "overlay.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var response = await CleanupAsync();

            response.WorkspacesInUse.Should().Equal("old-agent");
            response.RemovedCommits.Should().BeEmpty("its baseline is unknown, so any baseline might be the one");
        }

        BaselinePublisher.IsComplete(BaselineDir(ShaOld)).Should().BeTrue();
        BaselinePublisher.IsComplete(BaselineDir(ShaMid)).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_IdleOverlayWithoutMarker_ReportedAndCleanupProceeds()
    {
        Directory.CreateDirectory(OverlayDir("stale-ws"));
        File.WriteAllText(Path.Combine(OverlayDir("stale-ws"), "overlay.wal"), "");

        var response = await CleanupAsync();

        response.WorkspacesWithoutBaselineRecord.Should().Equal("stale-ws");
        response.RemovedCommits.Should().BeEquivalentTo([ShaOld, ShaMid]);
    }

    [Fact]
    public async Task Cleanup_BaselineLevelOverlay_IsNotAWorkspace_RemovedWithItsBaseline()
    {
        // overlays/<sha>/ holds edge upgrades / stubs written at baseline level, not a workspace.
        Directory.CreateDirectory(OverlayDir(ShaMid.Value));
        File.WriteAllText(Path.Combine(OverlayDir(ShaMid.Value), "overlay.wal"), "");

        var response = await CleanupAsync();

        response.WorkspacesWithoutBaselineRecord.Should().BeNullOrEmpty();
        response.ProtectedByWorkspaces.Should().BeNullOrEmpty();
        response.RemovedCommits.Should().Contain(ShaMid);
        Directory.Exists(OverlayDir(ShaMid.Value)).Should().BeFalse("it is useless without its baseline");
    }

    [Fact]
    public async Task Cleanup_DryRun_ReportsProtectionAndDeletesNothing()
    {
        WriteMarker("agent-b", ShaOld);

        var response = await CleanupAsync(dryRun: true);

        response.DryRun.Should().BeTrue();
        response.ProtectedByWorkspaces.Should().Equal(ShaOld);
        response.RemovedCommits.Should().Equal(ShaMid);
        BaselinePublisher.IsComplete(BaselineDir(ShaMid)).Should().BeTrue();
    }

    private void WriteMarker(string workspace, CommitSha sha)
    {
        Directory.CreateDirectory(OverlayDir(workspace));
        File.WriteAllText(Path.Combine(OverlayDir(workspace), "overlay.meta.json"),
            $$"""{ "baseline_commit_sha": "{{sha.Value}}", "created_utc": "2026-09-29T00:00:00Z" }""");
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
