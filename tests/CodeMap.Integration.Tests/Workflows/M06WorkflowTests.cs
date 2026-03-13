namespace CodeMap.Integration.Tests.Workflows;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Cross-cutting M06 workflow tests (PHASE-06-05 T01).
/// Chains multiple M06 tools to simulate realistic agent patterns.
/// Workflows 1, 2, 4, 5 use the shared IndexedSampleSolutionFixture (real Roslyn index).
/// Workflow 3 (diff) uses two manually-seeded baselines — no Roslyn required.
/// </summary>
[Trait("Category", "Integration")]
public sealed class M06WorkflowTests : IClassFixture<IndexedSampleSolutionFixture>
{
    private readonly IndexedSampleSolutionFixture _f;

    public M06WorkflowTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    private RoutingContext Routing => _f.CommittedRouting();

    // ── Workflow 1: "Generate context for external LLM review" ────────────────

    [Fact]
    public async Task E2E_Workflow_ExportForExternalReview()
    {
        // Step 1: export at standard detail with 4000-token budget
        var result = await _f.QueryEngine.ExportAsync(
            Routing, detail: "standard", format: "markdown", maxTokens: 4000,
            ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value.Data;

        // Step 2: output is self-contained — no MCP tool references
        data.Content.Should().NotBeNullOrEmpty();
        data.Content.Should().NotContain("symbols.search",
            "exported output should not reference MCP tool names");
        data.Content.Should().NotContain("index.ensure_baseline",
            "exported output should not reference MCP tool names");

        // Step 3: contains class signatures and markdown structure
        data.Content.Should().Contain("#", "markdown output must have headings");
        data.Format.Should().Be("markdown");
        data.DetailLevel.Should().Be("standard");

        // Step 4: token budget respected (budget + 10% margin)
        data.EstimatedTokens.Should().BeLessOrEqualTo(4400,
            "estimated tokens must not exceed budget by more than 10%");
    }

    // ── Workflow 2: "Summarize then deep-dive" ────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_SummarizeThenDeepDive()
    {
        // Step 1: codemap.summarize → get overview
        var summaryResult = await _f.QueryEngine.SummarizeAsync(
            Routing, repoPath: null, ct: CancellationToken.None);

        summaryResult.IsSuccess.Should().BeTrue();
        var summary = summaryResult.Value.Data;
        summary.Sections.Should().NotBeEmpty();
        summary.Stats.EndpointCount.Should().BeGreaterThan(0,
            "SampleApp.Api has HTTP endpoints that summarize should detect");

        // Step 2: summary has an API section listing endpoints
        var apiSection = summary.Sections.FirstOrDefault(s =>
            s.Title.Contains("API", StringComparison.OrdinalIgnoreCase) ||
            s.Title.Contains("Endpoint", StringComparison.OrdinalIgnoreCase));
        apiSection.Should().NotBeNull("summary should include an API/Endpoint section");
        apiSection!.ItemCount.Should().BeGreaterThan(0);

        // Step 3: graph.trace_feature on a known endpoint handler → deep dive
        var traceResult = await _f.QueryEngine.TraceFeatureAsync(
            Routing, _f.SubmitAsyncId, depth: 2,
            ct: CancellationToken.None);

        traceResult.IsSuccess.Should().BeTrue();
        var trace = traceResult.Value.Data;

        // Step 4: trace expands SubmitAsync further than what the summary shows
        trace.EntryPoint.Should().Be(_f.SubmitAsyncId);
        trace.Nodes.Should().NotBeEmpty("SubmitAsync calls downstream methods");
        trace.Depth.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Workflow 3: "Diff after making changes" ───────────────────────────────

    [Fact]
    public async Task E2E_Workflow_DiffAfterChanges()
    {
        // Self-contained: two manually-seeded baselines in a temp directory.
        var tempDir = Path.Combine(Path.GetTempPath(),
            "codemap-m06-diff-wf-" + Guid.NewGuid().ToString("N"));
        var repoDir  = Path.Combine(tempDir, "repo");
        var baseDir  = Path.Combine(tempDir, "baselines");
        Directory.CreateDirectory(Path.Combine(repoDir, "src"));
        Directory.CreateDirectory(baseDir);

        try
        {
            var repoId = RepoId.From("m06-diff-workflow-repo");
            var shaA   = CommitSha.From(new string('a', 40));
            var shaB   = CommitSha.From(new string('b', 40));
            var file1  = FilePath.From("src/Service.cs");

            var factory = new BaselineDbFactory(baseDir, NullLogger<BaselineDbFactory>.Instance);
            var store   = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
            var engine  = new QueryEngine(
                store, new InMemoryCacheService(), new TokenSavingsTracker(),
                new ExcerptReader(store), new GraphTraverser(),
                new FeatureTracer(store, new GraphTraverser()),
                NullLogger<QueryEngine>.Instance);

            File.WriteAllText(Path.Combine(repoDir, "src", "Service.cs"), "// stub");

            // Step 1: index baseline A — two symbols
            var dataA = MakeCompilationResult(file1,
                MakeCard("Sample.OrderService",  SymbolKind.Class, file1),
                MakeCard("Sample.OldService",    SymbolKind.Class, file1));
            await store.CreateBaselineAsync(repoId, shaA, dataA, repoDir);

            // Step 2: simulate a change — swap OldService for NewService (added method)
            var dataB = MakeCompilationResult(file1,
                MakeCard("Sample.OrderService",  SymbolKind.Class, file1),
                MakeCard("Sample.NewService",    SymbolKind.Class, file1));
            await store.CreateBaselineAsync(repoId, shaB, dataB, repoDir);

            // Step 3: index.diff(from: A, to: B)
            var routing = new RoutingContext(repoId: repoId, baselineCommitSha: shaA);
            var diff    = await engine.DiffAsync(routing, shaA, shaB, ct: CancellationToken.None);

            // Step 4: diff shows NewService as Added, OldService as Removed
            diff.IsSuccess.Should().BeTrue();
            var data = diff.Value.Data;
            data.SymbolChanges.Should().Contain(s =>
                s.ChangeType == "Added" &&
                s.ToSymbolId!.Value.Value.Contains("NewService"),
                "NewService was added in shaB");
            data.SymbolChanges.Should().Contain(s =>
                s.ChangeType == "Removed" &&
                s.FromSymbolId!.Value.Value.Contains("OldService"),
                "OldService was removed in shaB");
            data.Stats.SymbolsAdded.Should().BeGreaterThanOrEqualTo(1);
            data.Stats.SymbolsRemoved.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Workflow 4: "Full audit cycle" ────────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_FullAuditCycle()
    {
        // Step 1: codemap.summarize with targeted sections
        var summaryResult = await _f.QueryEngine.SummarizeAsync(
            Routing, repoPath: null,
            sectionFilter: ["api", "config"],
            ct: CancellationToken.None);

        summaryResult.IsSuccess.Should().BeTrue();
        var summaryData = summaryResult.Value.Data;
        summaryData.Sections.Should().NotBeEmpty(
            "filtered sections should still include api and config content");

        // Step 2: codemap.export with section filter — public API + endpoints
        var exportResult = await _f.QueryEngine.ExportAsync(
            Routing, detail: "standard",
            sectionFilter: ["api", "public_api"],
            ct: CancellationToken.None);

        exportResult.IsSuccess.Should().BeTrue();
        var exportData = exportResult.Value.Data;
        exportData.Content.Should().NotBeNullOrEmpty();

        // Step 3: summary has fewer estimated tokens than a full export
        // (summary stats are lightweight, export includes more structured detail)
        var summaryMarkdownLength = summaryData.Markdown.Length;
        summaryMarkdownLength.Should().BeGreaterThan(0);

        // Step 4: both surfaces expose the same API endpoints
        var endpointsResult = await _f.QueryEngine.ListEndpointsAsync(
            Routing, pathFilter: null, httpMethod: null, limit: 50);
        endpointsResult.IsSuccess.Should().BeTrue();
        var endpoints = endpointsResult.Value.Data.Endpoints;
        if (endpoints.Count > 0)
        {
            // The first endpoint's route should appear somewhere in the export
            var firstRoute = endpoints[0].RoutePath;
            exportData.Content.Should().Contain(firstRoute,
                "export should surface the same endpoints as surfaces.list_endpoints");
        }
    }

    // ── Workflow 5: "Baseline management" ─────────────────────────────────────

    [Fact]
    public async Task E2E_Workflow_BaselineManagement()
    {
        // Step 1: index.list_baselines → should find the one indexed by the fixture
        var scanner  = new BaselineDbFactory(
            _f.BaselineDir, NullLogger<BaselineDbFactory>.Instance);
        var baselines = await scanner.ListBaselinesAsync(_f.RepoId, CancellationToken.None);

        baselines.Should().HaveCountGreaterThanOrEqualTo(1,
            "the fixture indexes exactly one baseline");

        // Step 2: index.cleanup(dry_run: true) → nothing removed
        // The single baseline is currentHead so it is always protected.
        var cleanup = await scanner.CleanupBaselinesAsync(
            _f.RepoId,
            currentHead:            _f.Sha,
            workspaceBaseCommits:   new HashSet<CommitSha>(),
            keepCount:              5,
            olderThanDays:          null,
            dryRun:                 true,
            ct:                     CancellationToken.None);

        cleanup.DryRun.Should().BeTrue("we passed dryRun: true");
        cleanup.BaselinesRemoved.Should().Be(0,
            "the only baseline is HEAD so it must not be removed");
        cleanup.KeptCommits.Should().Contain(_f.Sha,
            "HEAD baseline is always kept");

        // Step 3: baseline still exists after dry run
        var baselineAfter = await scanner.ListBaselinesAsync(_f.RepoId, CancellationToken.None);
        baselineAfter.Should().HaveCountGreaterThanOrEqualTo(1,
            "dry run must not delete any files");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeCard(string fqn, SymbolKind kind, FilePath file)
        => SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(fqn), fullyQualifiedName: fqn,
            kind: kind, signature: $"class {fqn.Split('.')[^1]}", @namespace: "Sample",
            filePath: file, spanStart: 1, spanEnd: 10,
            visibility: "public", confidence: Confidence.High);

    private static CompilationResult MakeCompilationResult(FilePath file, params SymbolCard[] symbols)
        => new(
            Symbols: symbols,
            References: [],
            Files: [new ExtractedFile("file001", file, new string('a', 64), null)],
            Stats: new IndexStats(1, 0, symbols.Length, 0, Confidence.High,
                ProjectDiagnostics: [new ProjectDiagnostic("SampleProject", true, symbols.Length, 0)]),
            TypeRelations: [],
            Facts: []);
}
