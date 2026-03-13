namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests verifying enriched WorkspaceSummary fields: IsStale, SemanticLevel,
/// FactCount, CreatedAt, and GetStaleWorkspacesAsync (PHASE-03-07 T01).
/// </summary>
public sealed class WorkspaceManagerEnrichmentTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha ShaA = CommitSha.From(new string('a', 40));
    private static readonly CommitSha ShaB = CommitSha.From(new string('b', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-001");
    private static readonly string SlnPath = "/fake/solution.sln";
    private static readonly string RepoRoot = "/fake/repo";
    private static readonly DateTimeOffset CreatedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IOverlayStore _overlay = Substitute.For<IOverlayStore>();
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();
    private readonly ISymbolStore _baseline = Substitute.For<ISymbolStore>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly WorkspaceManager _manager;

    public WorkspaceManagerEnrichmentTests()
    {
        _manager = new WorkspaceManager(
            _overlay, _compiler, _baseline, _git, _cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        // Default overlayStore stubs
        _overlay.GetOverlayFilePathsAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(new HashSet<FilePath>());
        _overlay.GetOverlaySemanticLevelAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns((SemanticLevel?)SemanticLevel.Full);
        _overlay.GetOverlayFactCountAsync(Arg.Any<RepoId>(), Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
                .Returns(5);

        // Default git stub — HEAD matches workspace (fresh)
        _git.GetCurrentCommitAsync(RepoRoot, Arg.Any<CancellationToken>())
            .Returns(ShaA);
    }

    private async Task RegisterWorkspaceAsync(CommitSha sha, DateTimeOffset? createdAt = null)
    {
        _baseline.BaselineExistsAsync(Repo, sha, Arg.Any<CancellationToken>()).Returns(true);
        await _manager.CreateWorkspaceAsync(Repo, WsId, sha, SlnPath, RepoRoot);

        // Override CreatedAt via registry (WorkspaceManager stores DateTimeOffset.UtcNow at creation)
        // We can't easily inject time, so for CreatedAt tests we just verify it's set
        _ = createdAt; // not used — CreatedAt is set to UtcNow at creation time
    }

    // ── SemanticLevel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_IncludesSemanticLevel()
    {
        _overlay.GetOverlaySemanticLevelAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns((SemanticLevel?)SemanticLevel.Partial);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries.Should().HaveCount(1);
        summaries[0].SemanticLevel.Should().Be(SemanticLevel.Partial);
    }

    [Fact]
    public async Task ListWorkspaces_SemanticLevel_NullWhenOverlayHasNone()
    {
        _overlay.GetOverlaySemanticLevelAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns((SemanticLevel?)null);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].SemanticLevel.Should().BeNull();
    }

    // ── FactCount ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_IncludesFactCount()
    {
        _overlay.GetOverlayFactCountAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(42);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].FactCount.Should().Be(42);
    }

    [Fact]
    public async Task ListWorkspaces_FactCount_ZeroWhenNoFacts()
    {
        _overlay.GetOverlayFactCountAsync(Repo, WsId, Arg.Any<CancellationToken>())
                .Returns(0);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].FactCount.Should().Be(0);
    }

    // ── CreatedAt ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_IncludesCreatedAt()
    {
        var before = DateTimeOffset.UtcNow;
        await RegisterWorkspaceAsync(ShaA);
        var after = DateTimeOffset.UtcNow;

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].CreatedAt.Should().NotBeNull();
        summaries[0].CreatedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── Stale detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkspaces_StaleDetection_CommitMismatch()
    {
        // Workspace created at ShaA; HEAD has moved to ShaB
        _git.GetCurrentCommitAsync(RepoRoot, Arg.Any<CancellationToken>())
            .Returns(ShaB);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].IsStale.Should().BeTrue(
            because: "workspace base commit A != current HEAD B");
    }

    [Fact]
    public async Task ListWorkspaces_FreshDetection_CommitMatch()
    {
        // Workspace created at ShaA; HEAD is still ShaA
        _git.GetCurrentCommitAsync(RepoRoot, Arg.Any<CancellationToken>())
            .Returns(ShaA);

        await RegisterWorkspaceAsync(ShaA);

        var summaries = await _manager.ListWorkspacesAsync(Repo);

        summaries[0].IsStale.Should().BeFalse(
            because: "workspace base commit == current HEAD");
    }

    // ── GetStaleWorkspacesAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetStaleWorkspaces_ReturnsOnlyStale()
    {
        // Create two workspaces: ws-001 on ShaA, ws-002 on ShaA — then HEAD moves to ShaB
        var ws2 = WorkspaceId.From("ws-002");
        _baseline.BaselineExistsAsync(Repo, ShaA, Arg.Any<CancellationToken>()).Returns(true);

        await _manager.CreateWorkspaceAsync(Repo, WsId, ShaA, SlnPath, RepoRoot);
        await _manager.CreateWorkspaceAsync(Repo, ws2, ShaA, SlnPath, RepoRoot);

        // HEAD moves
        _git.GetCurrentCommitAsync(RepoRoot, Arg.Any<CancellationToken>())
            .Returns(ShaB);

        var stale = await _manager.GetStaleWorkspacesAsync(Repo);

        stale.Should().HaveCount(2, because: "both were created against ShaA, HEAD is now ShaB");
        stale.Should().OnlyContain(ws => ws.IsStale);
    }

    [Fact]
    public async Task GetStaleWorkspaces_EmptyWhenAllFresh()
    {
        await RegisterWorkspaceAsync(ShaA);
        // HEAD still ShaA (default stub)

        var stale = await _manager.GetStaleWorkspacesAsync(Repo);

        stale.Should().BeEmpty();
    }
}
