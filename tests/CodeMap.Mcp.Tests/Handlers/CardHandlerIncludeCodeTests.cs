namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// Tests for the include_code=true/false behavior added to symbols.get_card in PHASE-07-05 T01.
/// </summary>
public sealed class CardHandlerIncludeCodeTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SymbolIdStr = "M:MyNs.MyClass.DoWork";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    public CardHandlerIncludeCodeTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(RepoId.From("my-repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));

        _handler = new McpToolHandlers(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);
    }

    [Fact]
    public async Task GetCard_IncludeCodeDefault_CallsDefinitionSpan()
    {
        SetupCardAndSpan(spanStart: 10, spanEnd: 20, code: "public void DoWork() { }");

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        // Default include_code=true should trigger a span read
        await _queryEngine.Received(1).GetDefinitionSpanAsync(
            Arg.Any<RoutingContext>(), SymbolId.From(SymbolIdStr),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_IncludeCodeTrue_ReturnsSourceCodeInData()
    {
        const string code = "public void DoWork() { var x = 1; }";
        SetupCardAndSpan(spanStart: 10, spanEnd: 15, code: code);

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr, ["include_code"] = true },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonNode.Parse(result.Content)!.AsObject();
        json["data"]!.AsObject().TryGetPropertyValue("source_code", out var sc).Should().BeTrue();
        sc!.GetValue<string>().Should().Contain(code);
    }

    [Fact]
    public async Task GetCard_IncludeCodeFalse_NoSourceCode()
    {
        SetupCardAndSpan(spanStart: 10, spanEnd: 20, code: "irrelevant");

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr, ["include_code"] = false },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        // No span read should happen
        await _queryEngine.DidNotReceive().GetDefinitionSpanAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        var json = JsonNode.Parse(result.Content)!.AsObject();
        json["data"]!.AsObject().TryGetPropertyValue("source_code", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetCard_NoSpanInfo_NoSourceCodeEvenWhenIncludeCodeTrue()
    {
        // Card with SpanStart = 0 (no span info — e.g., external symbol)
        SetupCardAndSpan(spanStart: 0, spanEnd: 0, code: "irrelevant");

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr, ["include_code"] = true },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        // No span read should happen when SpanStart <= 0
        await _queryEngine.DidNotReceive().GetDefinitionSpanAsync(
            Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCard_SpanReadFails_ReturnsCardWithoutError()
    {
        SetupCardOnly(spanStart: 5, spanEnd: 15);
        _queryEngine.GetDefinitionSpanAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Failure(
                CodeMapError.NotFound("File", "src/MyClass.cs")));

        // Should succeed and return card without source_code (graceful degradation)
        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("symbol_id");
    }

    [Fact]
    public async Task GetCard_RichMetadata_FilePathAndSpanPresentInData()
    {
        SetupCardAndSpan(spanStart: 10, spanEnd: 25, code: "public void DoWork() { }");

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var json = JsonNode.Parse(result.Content)!.AsObject();
        var data = json["data"]!.AsObject();
        // file_path, span_start, span_end should be present from SymbolCard serialization
        data.TryGetPropertyValue("file_path", out _).Should().BeTrue();
        data.TryGetPropertyValue("span_start", out _).Should().BeTrue();
        data.TryGetPropertyValue("span_end", out _).Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupCardAndSpan(int spanStart, int spanEnd, string code)
    {
        SetupCardOnly(spanStart, spanEnd);

        var spanResponse = new SpanResponse(
            FilePath.From("src/MyClass.cs"), spanStart, spanEnd, 100, code, false);
        var spanMeta = new ResponseMeta(new TimingBreakdown(0), CommitSha.From(ValidSha),
            new Dictionary<string, LimitApplied>(), 0, 0m);
        var spanEnvelope = new ResponseEnvelope<SpanResponse>(
            $"Lines {spanStart}–{spanEnd}", spanResponse, [], [], Confidence.High, spanMeta);

        _queryEngine.GetDefinitionSpanAsync(
                Arg.Any<RoutingContext>(), Arg.Any<SymbolId>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(spanEnvelope));
    }

    private void SetupCardOnly(int spanStart, int spanEnd)
    {
        var card = SymbolCard.CreateMinimal(
            SymbolId.From(SymbolIdStr), "MyNs.MyClass.DoWork", SymbolKind.Method,
            "public void DoWork()", "MyNs",
            FilePath.From("src/MyClass.cs"), spanStart, spanEnd, "public", Confidence.High);
        var meta = new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
            new Dictionary<string, LimitApplied>(), 0, 0m);
        var envelope = new ResponseEnvelope<SymbolCard>(
            "Got card.", card, [], [], Confidence.High, meta);

        _queryEngine.GetSymbolCardAsync(
                Arg.Any<RoutingContext>(), SymbolId.From(SymbolIdStr),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(envelope));
    }
}
