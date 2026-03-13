namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using NSubstitute;

public sealed class SemanticDifferTests
{
    private static readonly RepoId Repo     = RepoId.From("differ-repo");
    private static readonly CommitSha ShaA  = CommitSha.From(new string('a', 40));
    private static readonly CommitSha ShaB  = CommitSha.From(new string('b', 40));

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();

    private void SetupSymbols(CommitSha sha, IReadOnlyList<SymbolSummary> symbols)
        => _store.GetAllSymbolSummariesAsync(Repo, sha, Arg.Any<CancellationToken>())
                 .Returns(symbols);

    private void SetupFacts(CommitSha sha, params StoredFact[] facts)
    {
        foreach (var kind in Enum.GetValues<FactKind>())
        {
            var kindFacts = facts.Where(f => f.Kind == kind).ToList<StoredFact>();
            _store.GetFactsByKindAsync(Repo, sha, kind, Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(kindFacts);
        }
    }

    private static SymbolSummary Sym(string fqn, SymbolKind kind = SymbolKind.Method,
        string sig = "void M()", string? stableVal = null, string vis = "public")
        => new(SymbolId: SymbolId.From(fqn),
               StableId: stableVal is null ? null : new StableId(stableVal),
               FullyQualifiedName: fqn,
               Signature: sig,
               Visibility: vis,
               Kind: kind);

    private static StoredFact Fact(FactKind kind, string value)
        => new(SymbolId: SymbolId.From("M:Test.M"),
               StableId: null,
               Kind: kind,
               Value: value,
               FilePath: FilePath.From("Test.cs"),
               LineStart: 1, LineEnd: 1,
               Confidence: Confidence.High);

    private async Task<DiffResponse> Diff(
        IReadOnlyList<SymbolSummary> fromSyms, IReadOnlyList<SymbolSummary> toSyms,
        StoredFact[]? fromFacts = null, StoredFact[]? toFacts = null)
    {
        SetupSymbols(ShaA, fromSyms);
        SetupSymbols(ShaB, toSyms);
        SetupFacts(ShaA, fromFacts ?? []);
        SetupFacts(ShaB, toFacts ?? []);
        var routing = new RoutingContext(repoId: Repo, baselineCommitSha: ShaA);
        return await SemanticDiffer.DiffAsync(_store, Repo, ShaA, ShaB, null, true, CancellationToken.None);
    }

    // ── Symbol tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_IdenticalBaselines_NoChanges()
    {
        var syms = new[] { Sym("N.Foo", stableVal: "stab1") };
        var result = await Diff(syms, syms);

        result.SymbolChanges.Should().BeEmpty();
        result.FactChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Diff_SymbolAdded_Detected()
    {
        var result = await Diff(
            fromSyms: [],
            toSyms:   [Sym("N.NewClass", SymbolKind.Class, stableVal: "stab2")]);

        result.SymbolChanges.Should().HaveCount(1);
        result.SymbolChanges[0].ChangeType.Should().Be("Added");
        result.SymbolChanges[0].ToSymbolId.Should().Be(SymbolId.From("N.NewClass"));
    }

    [Fact]
    public async Task Diff_SymbolRemoved_Detected()
    {
        var result = await Diff(
            fromSyms: [Sym("N.OldClass", SymbolKind.Class, stableVal: "stab3")],
            toSyms:   []);

        result.SymbolChanges.Should().HaveCount(1);
        result.SymbolChanges[0].ChangeType.Should().Be("Removed");
        result.SymbolChanges[0].FromSymbolId.Should().Be(SymbolId.From("N.OldClass"));
    }

    [Fact]
    public async Task Diff_SymbolRenamed_DetectedByStableId()
    {
        var result = await Diff(
            fromSyms: [Sym("N.OrderService.Submit",      stableVal: "stabX")],
            toSyms:   [Sym("N.OrderService.SubmitOrder", stableVal: "stabX")]);

        // Must be 1 Renamed, not 1 Added + 1 Removed
        result.SymbolChanges.Should().HaveCount(1);
        result.SymbolChanges[0].ChangeType.Should().Be("Renamed");
        result.SymbolChanges[0].StableId!.Value.Value.Should().Be("stabX");
    }

    [Fact]
    public async Task Diff_SignatureChanged_DetectedByStableId()
    {
        var result = await Diff(
            fromSyms: [Sym("N.S.GetById", sig: "User GetById(int id)",  stableVal: "stabY")],
            toSyms:   [Sym("N.S.GetById", sig: "User GetById(Guid id)", stableVal: "stabY")]);

        result.SymbolChanges.Should().HaveCount(1);
        result.SymbolChanges[0].ChangeType.Should().Be("SignatureChanged");
        result.SymbolChanges[0].FromSignature.Should().Contain("int");
        result.SymbolChanges[0].ToSignature.Should().Contain("Guid");
    }

    [Fact]
    public async Task Diff_FqnFallback_WhenNoStableId()
    {
        var result = await Diff(
            fromSyms: [Sym("N.S.Process", sig: "void Process(string s)")],
            toSyms:   [Sym("N.S.Process", sig: "void Process(int n)")]);

        result.SymbolChanges.Should().HaveCount(1);
        result.SymbolChanges[0].ChangeType.Should().Be("SignatureChanged");
        result.SymbolChanges[0].StableId.Should().BeNull();
    }

    // ── Fact tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_FactAdded_EndpointAdded()
    {
        var result = await Diff(
            fromSyms: [], toSyms: [],
            fromFacts: [],
            toFacts:   [Fact(FactKind.Route, "POST /api/payments")]);

        result.FactChanges.Should().HaveCount(1);
        result.FactChanges[0].ChangeType.Should().Be("Added");
        result.FactChanges[0].Kind.Should().Be(FactKind.Route);
        result.FactChanges[0].ToValue.Should().Be("POST /api/payments");
    }

    [Fact]
    public async Task Diff_FactRemoved_ConfigKeyRemoved()
    {
        var result = await Diff(
            fromSyms: [], toSyms: [],
            fromFacts: [Fact(FactKind.Config, "App:Timeout|GetValue")],
            toFacts:   []);

        result.FactChanges.Should().HaveCount(1);
        result.FactChanges[0].ChangeType.Should().Be("Removed");
        result.FactChanges[0].Kind.Should().Be(FactKind.Config);
    }

    [Fact]
    public async Task Diff_FactChanged_DiLifetimeChanged()
    {
        var result = await Diff(
            fromSyms: [], toSyms: [],
            fromFacts: [Fact(FactKind.DiRegistration, "IOrderService \u2192 OrderService|Scoped")],
            toFacts:   [Fact(FactKind.DiRegistration, "IOrderService \u2192 OrderService|Singleton")]);

        result.FactChanges.Should().HaveCount(1);
        result.FactChanges[0].ChangeType.Should().Be("Changed");
        result.FactChanges[0].FromValue.Should().Contain("Scoped");
        result.FactChanges[0].ToValue.Should().Contain("Singleton");
    }

    [Fact]
    public async Task Diff_MixedChanges_AllCategoriesCounted()
    {
        var result = await Diff(
            fromSyms: [Sym("N.OldSvc", stableVal: "s1"),
                       Sym("N.Kept",   stableVal: "s2")],
            toSyms:   [Sym("N.Kept",    stableVal: "s2"),
                       Sym("N.NewSvc",  stableVal: "s3")],
            fromFacts: [Fact(FactKind.Route, "GET /old")],
            toFacts:   [Fact(FactKind.Route, "GET /new")]);

        result.SymbolChanges.Should().HaveCount(2); // 1 Removed + 1 Added
        result.SymbolChanges.Should().Contain(s => s.ChangeType == "Removed");
        result.SymbolChanges.Should().Contain(s => s.ChangeType == "Added");
        result.FactChanges.Should().HaveCount(2);   // 1 Removed + 1 Added
    }

    [Fact]
    public async Task Diff_StatsPopulated_Correctly()
    {
        var result = await Diff(
            fromSyms: [Sym("N.A", stableVal: "s1"),
                       Sym("N.OldName", stableVal: "s2"),
                       Sym("N.SigChange", sig: "void M(int x)", stableVal: "s3")],
            toSyms:   [Sym("N.NewName",  stableVal: "s2"),
                       Sym("N.SigChange", sig: "void M(string x)", stableVal: "s3"),
                       Sym("N.B", stableVal: "s4")],
            fromFacts: [Fact(FactKind.Route, "GET /a")],
            toFacts:   [Fact(FactKind.Route, "GET /b")]);

        result.Stats.SymbolsAdded.Should().Be(1);
        result.Stats.SymbolsRemoved.Should().Be(1);
        result.Stats.SymbolsRenamed.Should().Be(1);
        result.Stats.SymbolsSignatureChanged.Should().Be(1);
        result.Stats.EndpointsAdded.Should().Be(1);
        result.Stats.EndpointsRemoved.Should().Be(1);
    }

    [Fact]
    public async Task Diff_MarkdownRendered_ValidFormat()
    {
        var result = await Diff(
            fromSyms: [], toSyms: [Sym("N.NewClass", SymbolKind.Class)]);

        result.Markdown.Should().StartWith("# Semantic Diff:");
        result.Markdown.Should().Contain("## Summary");
        result.Markdown.Should().Contain("## Symbols");
    }

    [Fact]
    public async Task Diff_DuplicateFactKeys_NoCrash()
    {
        // Three Config facts in FROM all reading "App:MaxRetries" — should not throw
        var fromFacts = new[]
        {
            Fact(FactKind.Config, "App:MaxRetries|GetValue"),
            Fact(FactKind.Config, "App:MaxRetries|GetValue"),
            Fact(FactKind.Config, "App:MaxRetries|GetValue"),
        };
        var toFacts = new[]
        {
            Fact(FactKind.Config, "App:MaxRetries|GetValue"),
            Fact(FactKind.Config, "App:MaxRetries|GetValue"),
        };

        var act = async () => await Diff(fromSyms: [], toSyms: [], fromFacts, toFacts);

        await act.Should().NotThrowAsync();
        var result = await Diff(fromSyms: [], toSyms: [], fromFacts, toFacts);
        result.FactChanges.Should().BeEmpty(); // same key in both → no change
    }

    // ── Null StableId (old-baseline) tests ────────────────────────────────────

    [Fact]
    public async Task Diff_SymbolsWithNullStableId_DoesNotThrow()
    {
        // Old-baseline scenario: symbols have StableId = null
        var fromSyms = new[] { Sym("N.Foo") }; // stableVal = null by default
        var toSyms = Array.Empty<SymbolSummary>();

        var act = async () => await Diff(fromSyms, toSyms);

        await act.Should().NotThrowAsync();
        var result = await Diff(fromSyms, toSyms);
        result.Stats.SymbolsRemoved.Should().Be(1);
    }

    [Fact]
    public async Task Diff_MixedStableIdPresence_ProcessesBothPaths()
    {
        // One symbol has StableId, one does not — both paths exercised in one diff
        var syms = new[]
        {
            Sym("N.WithStable",    stableVal: "sym_abcdef1234567890"),
            Sym("N.WithoutStable"),   // stableVal = null → FQN path
        };

        // Identical from/to — expect zero changes
        var result = await Diff(syms, syms);

        result.Stats.SymbolsAdded.Should().Be(0);
        result.Stats.SymbolsRemoved.Should().Be(0);
        result.Stats.SymbolsRenamed.Should().Be(0);
    }
}
