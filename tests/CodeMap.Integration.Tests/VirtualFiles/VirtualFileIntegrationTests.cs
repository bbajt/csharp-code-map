namespace CodeMap.Integration.Tests.VirtualFiles;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// End-to-end integration tests for Ephemeral mode + virtual files (PHASE-02-06 T03/T04).
/// Uses manually seeded BaselineStore — tests that virtual file content substitutes disk reads.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VirtualFileIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("virtualfile-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly WorkspaceId WsId = WorkspaceId.From("ws-virtual-int-01");

    private static readonly SymbolId Sym1 = SymbolId.From("T:MyNs.OrderService");
    private static readonly FilePath File1 = FilePath.From("src/OrderService.cs");
    private static readonly FilePath OtherF = FilePath.From("src/Other.cs");

    // ── Fixture ───────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly OverlayStore _overlayStore;
    private readonly QueryEngine _queryEngine;
    private readonly MergedQueryEngine _mergedEngine;
    private readonly WorkspaceManager _workspaceMgr;
    private readonly IIncrementalCompiler _compiler = Substitute.For<IIncrementalCompiler>();

    public VirtualFileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-vf-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        // Real source files on disk
        File.WriteAllLines(Path.Combine(_repoDir, "src", "OrderService.cs"),
            ["namespace MyNs {",
             "    public class OrderService {",
             "        public void Process() {}",
             "        public void Cancel() {}",
             "    }",
             "}"]);
        File.WriteAllLines(Path.Combine(_repoDir, "src", "Other.cs"),
            ["namespace MyNs {",
             "    public class Other {}",
             "}"]);

        var baselineDir = Path.Combine(_tempDir, "baselines");
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(overlayDir);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        _overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        SeedBaseline();

        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var cache = new InMemoryCacheService();

        _workspaceMgr = new WorkspaceManager(
            _overlayStore, _compiler, _baselineStore, git, cache,
            Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        _queryEngine = new QueryEngine(
            _baselineStore, cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), new FeatureTracer(_baselineStore, new GraphTraverser()), NullLogger<QueryEngine>.Instance);

        _mergedEngine = new MergedQueryEngine(
            _queryEngine, _overlayStore, _workspaceMgr,
            cache, new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore), new GraphTraverser(), NullLogger<MergedQueryEngine>.Instance);

        _compiler.ComputeDeltaAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FilePath>>(),
                    Arg.Any<ISymbolStore>(), Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
                    Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(OverlayDelta.Empty(1)));

        _workspaceMgr.CreateWorkspaceAsync(Repo, WsId, Sha, "/fake/solution.sln", _repoDir)
                     .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetSpan_Ephemeral_VirtualContent_ReturnsVirtualLines()
    {
        // Virtual file with different content than what's on disk
        var virtualContent = "// virtual\nclass VirtualOrderService {\n    void VirtualMethod() {}\n}";
        var vf = new List<VirtualFile> { new(File1, virtualContent) };
        var routing = EphemeralRouting(vf);

        var result = await _mergedEngine.GetSpanAsync(routing, File1, 1, 2, 0, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("// virtual");
        result.Value.Data.Content.Should().Contain("VirtualOrderService");
        // Must NOT contain the real disk content
        result.Value.Data.Content.Should().NotContain("namespace MyNs");
    }

    [Fact]
    public async Task E2E_GetSpan_Ephemeral_NonVirtualFile_ReadsFromDisk()
    {
        // Virtual files only include "other.cs", NOT File1 (OrderService.cs)
        var vf = new List<VirtualFile> { new(OtherF, "// other virtual content") };
        var routing = EphemeralRouting(vf);

        var result = await _mergedEngine.GetSpanAsync(routing, File1, 1, 2, 0, null);

        result.IsSuccess.Should().BeTrue();
        // Content from disk — OrderService.cs content
        result.Value.Data.Content.Should().Contain("MyNs");
    }

    [Fact]
    public async Task E2E_GetDefinitionSpan_Ephemeral_VirtualContent()
    {
        // The symbol's card says it's at lines 2-5 in File1
        // Virtual file has different content at those lines
        var virtualLines = new[]
        {
            "namespace MyNs {",
            "    // [virtual] class OrderService {",
            "        public void VirtualProcess() {}",
            "        public void VirtualCancel() {}",
            "    }",
            "}"
        };
        var virtualContent = string.Join("\n", virtualLines);
        var vf = new List<VirtualFile> { new(File1, virtualContent) };
        var routing = EphemeralRouting(vf);

        var result = await _mergedEngine.GetDefinitionSpanAsync(routing, Sym1, 120, 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Content.Should().Contain("[virtual]");
    }

    [Fact]
    public async Task E2E_Search_Ephemeral_BehavesAsWorkspace()
    {
        // Even in Ephemeral mode with virtual files, search uses the index (not virtual content)
        var vf = new List<VirtualFile> { new(File1, "// completely different virtual content") };
        var routing = EphemeralRouting(vf);

        var result = await _mergedEngine.SearchSymbolsAsync(routing, "OrderService", null, null);

        // Should return results from baseline index (not affected by virtual content)
        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Hits.Should().Contain(h => h.SymbolId == Sym1);
    }

    [Fact]
    public void E2E_Ephemeral_ExceedsMaxChars_BudgetCapExists()
    {
        // The size validation is in the MCP handler layer (BuildRoutingResultAsync).
        // Verify the constant is set to 40,000 as specified.
        BudgetLimits.HardCaps.MaxChars.Should().Be(40_000);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RoutingContext EphemeralRouting(IReadOnlyList<VirtualFile>? vf = null) =>
        new(repoId: Repo, workspaceId: WsId, consistency: ConsistencyMode.Ephemeral,
            baselineCommitSha: Sha, virtualFiles: vf);

    private void SeedBaseline()
    {
        var symCard = SymbolCard.CreateMinimal(
            symbolId: Sym1,
            fullyQualifiedName: Sym1.Value,
            kind: SymbolKind.Class,
            signature: "class OrderService",
            @namespace: "MyNs",
            filePath: File1,
            spanStart: 2,
            spanEnd: 5,
            visibility: "public",
            confidence: Confidence.High);

        var file = new ExtractedFile("vf001", File1, new string('b', 64), null);

        var compilation = new CompilationResult(
            Symbols: [symCard],
            References: [],
            Files: [file],
            Stats: new IndexStats(1, 0, 1, 0.1, Confidence.High));

        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _repoDir)
                      .GetAwaiter().GetResult();
    }
}
