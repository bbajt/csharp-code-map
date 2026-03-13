namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class DiffHandlerTests
{
    private const string RepoPath   = "/fake/repo";
    private const string ShaAStr    = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaBStr    = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly CommitSha ShaA = CommitSha.From(ShaAStr);
    private static readonly CommitSha ShaB = CommitSha.From(ShaBStr);
    private static readonly RepoId    Repo = RepoId.From("diff-test-repo");

    private readonly IQueryEngine  _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService   _git    = Substitute.For<IGitService>();
    private readonly ISymbolStore  _store  = Substitute.For<ISymbolStore>();
    private readonly DiffHandler   _handler;

    private static readonly DiffStats EmptyStats = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static ResponseEnvelope<DiffResponse> MakeDiffEnvelope(string markdown = "# Semantic Diff: aaaaaa → bbbbbb\n")
    {
        var response = new DiffResponse(ShaA, ShaB, markdown, EmptyStats, [], []);
        var timing   = new TimingBreakdown(0, 0, 0, 0);
        var meta     = new ResponseMeta(timing, ShaB, new Dictionary<string, LimitApplied>(), 0, 0m);
        return new ResponseEnvelope<DiffResponse>(
            Answer: "Diff complete.",
            Data: response, Evidence: [], NextActions: [],
            Confidence: Confidence.High, Meta: meta);
    }

    public DiffHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Repo);
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ShaB);

        _store.BaselineExistsAsync(Repo, Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
              .Returns(true);

        _engine.DiffAsync(
                Arg.Any<RoutingContext>(), Arg.Any<CommitSha>(), Arg.Any<CommitSha>(),
                Arg.Any<IReadOnlyList<SymbolKind>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(Result<ResponseEnvelope<DiffResponse>, CodeMapError>.Success(MakeDiffEnvelope()));

        _handler = new DiffHandler(_engine, _git, _store, NullLogger<DiffHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject
        {
            ["repo_path"]    = RepoPath,
            ["from_commit"]  = ShaAStr,
            ["to_commit"]    = ShaBStr,
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Semantic Diff:");
        await _engine.Received(1).DiffAsync(
            Arg.Any<RoutingContext>(),
            Arg.Is<CommitSha>(s => s == ShaA),
            Arg.Is<CommitSha>(s => s == ShaB),
            Arg.Any<IReadOnlyList<SymbolKind>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_HeadResolved_ViaGetCurrentCommitAsync()
    {
        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = ShaAStr,
            ["to_commit"]   = "HEAD",
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        await _git.Received().GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>());
        await _engine.Received(1).DiffAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<CommitSha>(),
            Arg.Is<CommitSha>(s => s == ShaB), // HEAD resolved to ShaB
            Arg.Any<IReadOnlyList<SymbolKind>?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingFromBaseline_ReturnsError()
    {
        _store.BaselineExistsAsync(Repo, ShaA, Arg.Any<CancellationToken>())
              .Returns(false);

        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = ShaAStr,
            ["to_commit"]   = ShaBStr,
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("INDEX_NOT_AVAILABLE");
        await _engine.DidNotReceive().DiffAsync(
            Arg.Any<RoutingContext>(), Arg.Any<CommitSha>(), Arg.Any<CommitSha>(),
            Arg.Any<IReadOnlyList<SymbolKind>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingToBaseline_ReturnsError()
    {
        _store.BaselineExistsAsync(Repo, ShaB, Arg.Any<CancellationToken>())
              .Returns(false);

        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = ShaAStr,
            ["to_commit"]   = ShaBStr,
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("INDEX_NOT_AVAILABLE");
    }

    // ─── T02: ArgumentException narrowing (PHASE-09-05) ──────────────────────

    [Fact]
    public async Task HandleAsync_InvalidFromCommitSha_ReturnsInvalidArgument()
    {
        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = "not-a-valid-sha",
            ["to_commit"]   = ShaBStr,
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("INVALID_ARGUMENT");
        await _engine.DidNotReceive().DiffAsync(
            Arg.Any<RoutingContext>(), Arg.Any<CommitSha>(), Arg.Any<CommitSha>(),
            Arg.Any<IReadOnlyList<SymbolKind>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_InvalidToCommitSha_ReturnsInvalidArgument()
    {
        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = ShaAStr,
            ["to_commit"]   = "ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ",
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("INVALID_ARGUMENT");
    }

    [Fact]
    public async Task HandleAsync_ReturnsMarkdownContent()
    {
        const string expectedMarkdown = "# Semantic Diff: aaaaaaa \u2192 bbbbbbb\n## Summary\n";
        _engine.DiffAsync(
                Arg.Any<RoutingContext>(), Arg.Any<CommitSha>(), Arg.Any<CommitSha>(),
                Arg.Any<IReadOnlyList<SymbolKind>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(Result<ResponseEnvelope<DiffResponse>, CodeMapError>.Success(
                   MakeDiffEnvelope(expectedMarkdown)));

        var args = new JsonObject
        {
            ["repo_path"]   = RepoPath,
            ["from_commit"] = ShaAStr,
            ["to_commit"]   = ShaBStr,
        };

        var result = await _handler.HandleAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be(expectedMarkdown);
    }
}
