namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using CodeMap.Mcp.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public sealed class DbTablesHandlerTests
{
    private const string RepoPath = "/fake/repo";
    private const string ValidSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string WsIdStr = "ws-db-001";

    private readonly IQueryEngine _engine = Substitute.For<IQueryEngine>();
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly SurfacesHandler _handler;

    private static readonly CommitSha Sha = CommitSha.From(ValidSha);
    private static readonly RepoId Repo = RepoId.From("db-test-repo");

    public DbTablesHandlerTests()
    {
        _git.GetRepoIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Repo));
        _git.GetCurrentCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Sha));

        _engine.ListDbTablesAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Success(
                       MakeDbTablesEnvelope([
                           MakeDbTable("Orders", null),
                       ]))));

        _handler = new SurfacesHandler(_engine, _git, new RepoRegistry(), new WorkspaceStickyRegistry(), NullLogger<SurfacesHandler>.Instance);
    }

    [Fact]
    public async Task ListDbTables_ValidParams_DelegatesToEngine()
    {
        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleDbTablesAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
        await _engine.Received(1).ListDbTablesAsync(
            Arg.Any<RoutingContext>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListDbTables_WithTableFilter_PassedToEngine()
    {
        string? capturedFilter = null;
        _engine.ListDbTablesAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedFilter = ci.ArgAt<string?>(1);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Success(
                           MakeDbTablesEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["table_filter"] = "Order",
        };
        await _handler.HandleDbTablesAsync(args, CancellationToken.None);

        capturedFilter.Should().Be("Order");
    }

    [Fact]
    public async Task ListDbTables_NoTables_ReturnsEmptyList()
    {
        _engine.ListDbTablesAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(
                   Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Success(
                       MakeDbTablesEnvelope([]))));

        var args = new JsonObject { ["repo_path"] = RepoPath };

        var result = await _handler.HandleDbTablesAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("tables");
    }

    [Fact]
    public async Task ListDbTables_WithWorkspaceId_SetsWorkspaceMode()
    {
        RoutingContext? capturedRouting = null;
        _engine.ListDbTablesAsync(
                Arg.Any<RoutingContext>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   capturedRouting = ci.ArgAt<RoutingContext>(0);
                   return Task.FromResult(
                       Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>.Success(
                           MakeDbTablesEnvelope([])));
               });

        var args = new JsonObject
        {
            ["repo_path"] = RepoPath,
            ["workspace_id"] = WsIdStr,
        };
        await _handler.HandleDbTablesAsync(args, CancellationToken.None);

        capturedRouting.Should().NotBeNull();
        capturedRouting!.Consistency.Should().Be(ConsistencyMode.Workspace);
        capturedRouting.WorkspaceId.Should().NotBeNull();
        capturedRouting.WorkspaceId!.Value.Value.Should().Be(WsIdStr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DbTableInfo MakeDbTable(string tableName, string? schema) =>
        new(TableName: tableName,
            Schema: schema,
            EntitySymbol: SymbolId.From("T:Fake.Entity"),
            ReferencedBy: [SymbolId.From("P:Fake.Context.Orders")],
            Confidence: Confidence.High);

    private static ResponseEnvelope<ListDbTablesResponse> MakeDbTablesEnvelope(
        IReadOnlyList<DbTableInfo> tables)
    {
        var data = new ListDbTablesResponse(tables, tables.Count, false);
        var meta = new ResponseMeta(
            new TimingBreakdown(0, 0, 0), Sha,
            new Dictionary<string, LimitApplied>(), 0, 0);
        return new ResponseEnvelope<ListDbTablesResponse>("answer", data, [], [], Confidence.High, meta);
    }
}
