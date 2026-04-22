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

public class CardHandlerDecompilerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SymbolIdStr = "T:Foo.Bar";

    private readonly IQueryEngine _queryEngine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly McpToolHandlers _handler;

    public CardHandlerDecompilerTests()
    {
        _git.GetRepoIdentityAsync(RepoPath, Arg.Any<CancellationToken>()).Returns(RepoId.From("repo"));
        _git.GetCurrentCommitAsync(RepoPath, Arg.Any<CancellationToken>())
            .Returns(CommitSha.From(ValidSha));
        _handler = new McpToolHandlers(_queryEngine, _git, new McpSymbolResolver(_queryEngine), new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<McpToolHandlers>.Instance);
    }

    private ResponseEnvelope<SymbolCard> MakeEnvelope(int isDecompiled, int spanStart = 0, int spanEnd = 0)
    {
        var card = SymbolCard.CreateMinimal(
            SymbolId.From(SymbolIdStr), "Foo.Bar", SymbolKind.Class, "public class Bar", "Foo",
            FilePath.From("decompiled/Foo/Foo/Bar.cs"), spanStart, spanEnd, "public", Confidence.High)
            with { IsDecompiled = isDecompiled };
        var meta = new ResponseMeta(new TimingBreakdown(1.0), CommitSha.From(ValidSha),
            new Dictionary<string, LimitApplied>(), 0, 0m);
        return new ResponseEnvelope<SymbolCard>("Got card.", card, [], [], Confidence.High, meta);
    }

    private void SetupCard(int isDecompiled, int spanStart = 0, int spanEnd = 0)
    {
        _queryEngine.GetSymbolCardAsync(Arg.Any<RoutingContext>(), SymbolId.From(SymbolIdStr),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SymbolCard>, CodeMapError>.Success(
                MakeEnvelope(isDecompiled, spanStart, spanEnd)));
    }

    [Fact]
    public async Task HandleGetCardAsync_DecompiledCard_InjectsSourceDecompiled()
    {
        SetupCard(isDecompiled: 2);
        var spanEnvelope = new ResponseEnvelope<SpanResponse>(
            "Lines.", new SpanResponse(FilePath.From("decompiled/Foo/Foo/Bar.cs"), 1, 5, 10,
                "// decompiled source", false),
            [], [], Confidence.High,
            new ResponseMeta(new TimingBreakdown(0), CommitSha.From(ValidSha),
                new Dictionary<string, LimitApplied>(), 0, 0m));
        _queryEngine.GetSpanAsync(Arg.Any<RoutingContext>(), Arg.Any<FilePath>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<BudgetLimits?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<ResponseEnvelope<SpanResponse>, CodeMapError>.Success(spanEnvelope));

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr,
                             ["include_code"] = true }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonNode.Parse(result.Content)!["data"]!.AsObject();
        data["source"]!.GetValue<string>().Should().Be("decompiled");
        data.TryGetPropertyValue("source_code", out _).Should().BeTrue();
    }

    [Fact]
    public async Task HandleGetCardAsync_MetadataStubIncludeCodeFails_InjectsNoteAndSourceMetadataStub()
    {
        SetupCard(isDecompiled: 1); // still 1 — decompilation failed in QueryEngine

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr,
                             ["include_code"] = true }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonNode.Parse(result.Content)!["data"]!.AsObject();
        data["source"]!.GetValue<string>().Should().Be("metadata_stub");
        data.TryGetPropertyValue("note", out var note).Should().BeTrue();
        note!.GetValue<string>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleGetCardAsync_MetadataStubIncludeCodeFalse_NoNote()
    {
        SetupCard(isDecompiled: 1);

        var result = await _handler.HandleGetCardAsync(
            new JsonObject { ["repo_path"] = RepoPath, ["symbol_id"] = SymbolIdStr,
                             ["include_code"] = false }, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var data = JsonNode.Parse(result.Content)!["data"]!.AsObject();
        data["source"]!.GetValue<string>().Should().Be("metadata_stub");
        data.TryGetPropertyValue("note", out _).Should().BeFalse();
    }
}
