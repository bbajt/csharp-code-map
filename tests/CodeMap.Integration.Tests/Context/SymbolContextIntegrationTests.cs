namespace CodeMap.Integration.Tests.Context;

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
/// Integration tests for symbols.get_context (PHASE-07-05 T02).
/// Uses manually seeded BaselineStore + real files on disk + real QueryEngine.
///
/// Seeded call chain:
///   M:MyNs.Service.DoWork → M:MyNs.Repo.FindAll
///   M:MyNs.Service.DoWork → M:MyNs.Repo.Save
///   M:MyNs.Service.DoWork → M:MyNs.Repo.Delete
///   T:MyNs.Service = class (no callees — type symbols are excluded)
/// </summary>
[Trait("Category", "Integration")]
public sealed class SymbolContextIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("ctx-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('7', 40));

    private static readonly SymbolId ServiceMethod = SymbolId.From("M:MyNs.Service.DoWork");
    private static readonly SymbolId ServiceClass  = SymbolId.From("T:MyNs.Service");
    private static readonly SymbolId RepoFindAll   = SymbolId.From("M:MyNs.Repo.FindAll");
    private static readonly SymbolId RepoSave      = SymbolId.From("M:MyNs.Repo.Save");
    private static readonly SymbolId RepoDelete    = SymbolId.From("M:MyNs.Repo.Delete");

    private static readonly FilePath ServiceFile = FilePath.From("src/Service.cs");
    private static readonly FilePath RepoFile    = FilePath.From("src/Repo.cs");

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _baselineStore;
    private readonly QueryEngine _queryEngine;

    public SymbolContextIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-ctx-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        var baselineDir = Path.Combine(_tempDir, "baselines");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));
        Directory.CreateDirectory(baselineDir);

        // Real source files on disk so GetFileSpanAsync can read them
        File.WriteAllLines(Path.Combine(_repoDir, "src", "Service.cs"),
        [
            "namespace MyNs {",         // line 1
            "  public class Service {", // line 2
            "    public void DoWork() {",// line 3
            "      _repo.FindAll();",   // line 4
            "    }",                    // line 5
            "    void Extra() {}",      // line 6
            "  }",                      // line 7
            "}",                        // line 8
        ]);
        File.WriteAllLines(Path.Combine(_repoDir, "src", "Repo.cs"),
        [
            "namespace MyNs {",                    // line 1
            "  public class Repo {",               // line 2
            "    public object FindAll() {",        // line 3
            "      return new object();",           // line 4
            "    }",                               // line 5
            "    public void Save() {}",            // line 6
            "    public void Delete() {}",          // line 7
            "  }",                                 // line 8
            "}",                                   // line 9
        ]);

        var baselineFactory = new BaselineDbFactory(baselineDir, NullLogger<BaselineDbFactory>.Instance);
        _baselineStore = new BaselineStore(baselineFactory, NullLogger<BaselineStore>.Instance);

        SeedBaseline();

        _queryEngine = new QueryEngine(
            _baselineStore,
            new InMemoryCacheService(),
            new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore),
            new GraphTraverser(),
            new FeatureTracer(_baselineStore, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_GetContext_Method_ReturnsCardAndCode()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        var ctx = result.Value.Data;

        ctx.PrimarySymbol.Card.SymbolId.Should().Be(ServiceMethod);
        ctx.PrimarySymbol.SourceCode.Should().NotBeNullOrEmpty("DoWork method body should be read from disk");
        ctx.PrimarySymbol.SourceCode!.Should().Contain("DoWork", "file content should include method name");
    }

    [Fact]
    public async Task E2E_GetContext_Method_ReturnsCallees()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        var ctx = result.Value.Data;

        ctx.Callees.Should().NotBeEmpty("DoWork calls FindAll, Save, Delete");
        ctx.Callees.Should().Contain(c => c.Card.SymbolId == RepoFindAll);
        ctx.TotalCalleesFound.Should().BeGreaterThanOrEqualTo(ctx.Callees.Count);
    }

    [Fact]
    public async Task E2E_GetContext_CalleeDepth0_NoCallees()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 0, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Callees.Should().BeEmpty("callee_depth: 0 requests no callee expansion");
    }

    [Fact]
    public async Task E2E_GetContext_ClassSymbol_NoCallees()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceClass, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        var ctx = result.Value.Data;
        ctx.PrimarySymbol.Card.SymbolId.Should().Be(ServiceClass);
        ctx.Callees.Should().BeEmpty("type symbols return an error from GetCalleesAsync, which is gracefully ignored");
    }

    [Fact]
    public async Task E2E_GetContext_MaxCallees_Respected()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 1, maxCallees: 2, includeCode: false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Callees.Should().HaveCount(2, "max_callees: 2 limits callee expansion to 2");
    }

    [Fact]
    public async Task E2E_GetContext_IncludeCodeFalse_NoSourceCode()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 1, maxCallees: 10, includeCode: false);

        result.IsSuccess.Should().BeTrue();
        var ctx = result.Value.Data;
        ctx.PrimarySymbol.SourceCode.Should().BeNull("include_code: false suppresses code reading");
        ctx.Callees.Should().OnlyContain(c => c.SourceCode == null,
            "include_code: false applies to callees too");
    }

    [Fact]
    public async Task E2E_GetContext_MarkdownRendered()
    {
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), ServiceMethod, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        var markdown = result.Value.Data.Markdown;
        markdown.Should().StartWith("# ", "markdown has a top-level # header");
        markdown.Should().Contain("## Source Code", "markdown has source code section when includeCode=true");
    }

    [Fact]
    public async Task E2E_GetContext_SymbolNotFound_ReturnsError()
    {
        var nonExistent = SymbolId.From("M:Nobody.Missing");
        var result = await _queryEngine.GetContextAsync(
            CommittedRouting(), nonExistent, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(Core.Errors.ErrorCodes.NotFound);
    }

    [Fact]
    public async Task E2E_GetContext_WorkspaceMode_ReturnsPrimaryCard()
    {
        // Workspace mode: MergedQueryEngine delegates to ContextBuilder(this, workspaceRouting)
        var overlayDir = Path.Combine(_tempDir, "overlays");
        Directory.CreateDirectory(overlayDir);

        var overlayFactory = new OverlayDbFactory(overlayDir, NullLogger<OverlayDbFactory>.Instance);
        var overlayStore = new OverlayStore(overlayFactory, NullLogger<OverlayStore>.Instance);

        var git = Substitute.For<IGitService>();
        git.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
           .Returns(Task.FromResult<IReadOnlyList<FileChange>>([]));

        var wsId = WorkspaceId.From("ws-ctx-int-01");
        var workspaceMgr = new WorkspaceManager(
            overlayStore, Substitute.For<IIncrementalCompiler>(), _baselineStore, git,
            new InMemoryCacheService(), Substitute.For<IResolutionWorker>(),
            NullLogger<WorkspaceManager>.Instance);

        await workspaceMgr.CreateWorkspaceAsync(Repo, wsId, Sha, "/fake/solution.sln", _repoDir);

        var mergedEngine = new MergedQueryEngine(
            _queryEngine, overlayStore, workspaceMgr,
            new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_baselineStore),
            new GraphTraverser(),
            NullLogger<MergedQueryEngine>.Instance);

        var wsRouting = new RoutingContext(
            repoId: Repo, workspaceId: wsId,
            consistency: ConsistencyMode.Workspace, baselineCommitSha: Sha);

        var result = await mergedEngine.GetContextAsync(
            wsRouting, ServiceMethod, calleeDepth: 1, maxCallees: 10, includeCode: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.PrimarySymbol.Card.SymbolId.Should().Be(ServiceMethod);
        result.Value.Data.PrimarySymbol.SourceCode.Should().NotBeNullOrEmpty(
            "workspace mode reads files from disk via repoRootPath");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RoutingContext CommittedRouting() =>
        new(repoId: Repo, baselineCommitSha: Sha);

    private void SeedBaseline()
    {
        var symMethod  = MakeCard(ServiceMethod, "Service.DoWork", SymbolKind.Method, ServiceFile, 3, 5);
        var symClass   = MakeCard(ServiceClass,  "Service",        SymbolKind.Class,  ServiceFile, 1, 8);
        var symFindAll = MakeCard(RepoFindAll,   "Repo.FindAll",   SymbolKind.Method, RepoFile,    3, 5);
        var symSave    = MakeCard(RepoSave,      "Repo.Save",      SymbolKind.Method, RepoFile,    6, 6);
        var symDelete  = MakeCard(RepoDelete,    "Repo.Delete",    SymbolKind.Method, RepoFile,    7, 7);

        var fService = new ExtractedFile("svc001", ServiceFile, new string('a', 64), null);
        var fRepo    = new ExtractedFile("rep001", RepoFile,    new string('b', 64), null);

        var refs = new List<ExtractedReference>
        {
            new(FromSymbol: ServiceMethod, ToSymbol: RepoFindAll, Kind: RefKind.Call,
                FilePath: ServiceFile, LineStart: 4, LineEnd: 4),
            new(FromSymbol: ServiceMethod, ToSymbol: RepoSave,    Kind: RefKind.Call,
                FilePath: ServiceFile, LineStart: 4, LineEnd: 4),
            new(FromSymbol: ServiceMethod, ToSymbol: RepoDelete,  Kind: RefKind.Call,
                FilePath: ServiceFile, LineStart: 4, LineEnd: 4),
        };

        var compilation = new CompilationResult(
            Symbols: [symMethod, symClass, symFindAll, symSave, symDelete],
            References: refs,
            Files: [fService, fRepo],
            Facts: [],
            Stats: new IndexStats(5, refs.Count, 2, 0.1, Confidence.High));

        _baselineStore.CreateBaselineAsync(Repo, Sha, compilation, _repoDir)
                      .GetAwaiter().GetResult();
    }

    private static SymbolCard MakeCard(
        SymbolId id, string name, SymbolKind kind, FilePath file, int spanStart, int spanEnd) =>
        SymbolCard.CreateMinimal(
            symbolId: id,
            fullyQualifiedName: id.Value,
            kind: kind,
            signature: $"public void {name}()",
            @namespace: "MyNs",
            filePath: file,
            spanStart: spanStart,
            spanEnd: spanEnd,
            visibility: "public",
            confidence: Confidence.High);
}
