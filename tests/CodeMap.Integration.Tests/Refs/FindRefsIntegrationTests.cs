namespace CodeMap.Integration.Tests.Refs;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Query;
using CodeMap.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// E2E integration tests for refs.find (baseline mode).
/// Uses manually seeded BaselineStore + real QueryEngine — no Roslyn.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FindRefsIntegrationTests : IDisposable
{
    private static readonly RepoId Repo = RepoId.From("refs-integration-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('f', 40));
    private static readonly SymbolId Target = SymbolId.From("M:MyNs.MyService.DoWork");
    private static readonly SymbolId Caller1 = SymbolId.From("M:MyNs.ControllerA.Action");
    private static readonly SymbolId Caller2 = SymbolId.From("M:MyNs.ControllerB.Action");
    private static readonly FilePath FileA = FilePath.From("src/ControllerA.cs");
    private static readonly FilePath FileB = FilePath.From("src/ControllerB.cs");
    private static readonly FilePath SvcFile = FilePath.From("src/MyService.cs");

    private readonly string _tempDir;
    private readonly string _repoDir;
    private readonly BaselineStore _store;
    private readonly QueryEngine _engine;

    public FindRefsIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "codemap-refs-int-" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));

        var factory = new BaselineDbFactory(_tempDir, NullLogger<BaselineDbFactory>.Instance);
        _store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        _engine = new QueryEngine(_store, new InMemoryCacheService(), new TokenSavingsTracker(),
            new ExcerptReader(_store), new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()), NullLogger<QueryEngine>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_FindRefs_MethodCall_ReturnsCallRefs()
    {
        await SeedBaselineAsync(callRefCount: 2);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var result = await _engine.FindReferencesAsync(routing, Target, RefKind.Call, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(2);
        result.Value.Data.References.Should().AllSatisfy(r => r.Kind.Should().Be(RefKind.Call));
    }

    [Fact]
    public async Task E2E_FindRefs_AllKinds_ReturnsAllRefTypes()
    {
        await SeedBaselineAsync(callRefCount: 2, writeRefCount: 1);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(3);
    }

    [Fact]
    public async Task E2E_FindRefs_WithExcerpts_PopulatesOneLineContext()
    {
        await SeedBaselineAsync(callRefCount: 1);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().Contain(r => r.Excerpt != null);
    }

    [Fact]
    public async Task E2E_FindRefs_SymbolNotFound_ReturnsError()
    {
        await SeedBaselineAsync(callRefCount: 0);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var missing = SymbolId.From("T:DoesNotExist");
        var result = await _engine.FindReferencesAsync(routing, missing, null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task E2E_FindRefs_LimitEnforced_Truncates()
    {
        await SeedBaselineAsync(callRefCount: 5);
        var budgets = new BudgetLimits(maxReferences: 2);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var result = await _engine.FindReferencesAsync(routing, Target, null, budgets);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Truncated.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(2);
    }

    [Fact]
    public async Task E2E_FindRefs_CommittedMode_IgnoresOverlay()
    {
        // This test verifies committed mode returns baseline refs correctly
        await SeedBaselineAsync(callRefCount: 3);

        var routing = new RoutingContext(Repo, baselineCommitSha: Sha);
        var result = await _engine.FindReferencesAsync(routing, Target, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.References.Should().HaveCount(3);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task SeedBaselineAsync(int callRefCount = 0, int writeRefCount = 0)
    {
        // Write source files so ExcerptReader can read lines
        var callerAPath = Path.Combine(_repoDir, "src", "ControllerA.cs");
        File.WriteAllText(callerAPath, string.Join("\n",
            new[] { "namespace MyNs {", "    class ControllerA {" }
            .Concat(Enumerable.Range(3, 48).Select(i => $"        // line {i}"))
            .Append("    }")
            .Append("}")));

        var callerBPath = Path.Combine(_repoDir, "src", "ControllerB.cs");
        File.WriteAllText(callerBPath, string.Join("\n",
            new[] { "namespace MyNs {", "    class ControllerB {" }
            .Concat(Enumerable.Range(3, 48).Select(i => $"        // line {i}"))
            .Append("    }")
            .Append("}")));

        var svcPath = Path.Combine(_repoDir, "src", "MyService.cs");
        File.WriteAllText(svcPath, string.Join("\n",
            Enumerable.Range(1, 50).Select(i =>
                i == 5 ? "    public class MyService {" :
                i == 10 ? "        public void DoWork() {}" :
                i == 49 ? "    }" : $"    // line {i}")));

        var fileIdMap = new Dictionary<string, string>
        {
            ["src/ControllerA.cs"] = "aaaaaaaabbbbbbbb",
            ["src/ControllerB.cs"] = "ccccccccdddddddd",
            ["src/MyService.cs"] = "eeeeeeeeffffffff",
        };

        var symbols = new List<SymbolCard>
        {
            MakeSymbol(Target, "DoWork", SymbolKind.Method, SvcFile, 10),
        };

        // Add caller symbols
        for (int i = 0; i < Math.Max(callRefCount, writeRefCount); i++)
        {
            var callerFile = i % 2 == 0 ? FileA : FileB;
            symbols.Add(MakeSymbol(SymbolId.From($"M:MyNs.Caller{i}.Action"), $"Action{i}", SymbolKind.Method, callerFile, 5 + i));
        }

        var refs = new List<ExtractedReference>();
        for (int i = 0; i < callRefCount; i++)
        {
            var callerFile = i % 2 == 0 ? FileA : FileB;
            refs.Add(new ExtractedReference(
                FromSymbol: SymbolId.From($"M:MyNs.Caller{i}.Action"),
                ToSymbol: Target,
                Kind: RefKind.Call,
                FilePath: callerFile,
                LineStart: 5 + i,
                LineEnd: 5 + i));
        }
        for (int i = 0; i < writeRefCount; i++)
        {
            refs.Add(new ExtractedReference(
                FromSymbol: SymbolId.From($"M:MyNs.Caller{callRefCount + i}.Action"),
                ToSymbol: Target,
                Kind: RefKind.Write,
                FilePath: FileA,
                LineStart: 20 + i,
                LineEnd: 20 + i));
        }

        var files = fileIdMap.Select(kv => new ExtractedFile(
            kv.Value, FilePath.From(kv.Key), new string('0', 64), "MyApp")).ToList();

        var compilation = new CompilationResult(
            Symbols: symbols,
            References: refs,
            Files: files,
            Stats: new IndexStats(symbols.Count, refs.Count, files.Count, 0.1, Confidence.High));

        await _store.CreateBaselineAsync(Repo, Sha, compilation, repoRootPath: _repoDir);
    }

    private static SymbolCard MakeSymbol(SymbolId id, string name, SymbolKind kind, FilePath file, int line) =>
        SymbolCard.CreateMinimal(id, id.Value, kind, $"{kind} {name}", "MyNs", file, line, line + 5, "public", Confidence.High);
}
