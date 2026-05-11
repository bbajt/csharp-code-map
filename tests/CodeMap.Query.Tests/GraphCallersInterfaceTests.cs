namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>
/// M20-03 — graph.callers must surface an InterfaceImplementationHint when the
/// target method implements an interface, and union interface-routed callers
/// into the result when follow_interface=true.
/// </summary>
public class GraphCallersInterfaceTests
{
    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();
    private readonly ITokenSavingsTracker _tracker = Substitute.For<ITokenSavingsTracker>();
    private readonly InMemoryCacheService _cache = new();
    private readonly QueryEngine _engine;

    private static readonly RepoId Repo = RepoId.From("iface-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));
    private static readonly FilePath File1 = FilePath.From("src/Service.cs");
    private static readonly RoutingContext Routing = new(Repo, baselineCommitSha: Sha);

    // Concrete implementing IFoo.X()
    private static readonly SymbolId ConcreteMethod = SymbolId.From("M:Ns.Foo.X");
    private static readonly SymbolId InterfaceMethod = SymbolId.From("M:Ns.IFoo.X");
    private static readonly SymbolId ConcreteType = SymbolId.From("T:Ns.Foo");
    private static readonly SymbolId InterfaceType = SymbolId.From("T:Ns.IFoo");

    public GraphCallersInterfaceTests()
    {
        _engine = new QueryEngine(_store, _cache, _tracker, new ExcerptReader(_store),
            new GraphTraverser(), new FeatureTracer(_store, new GraphTraverser()),
            NullLogger<QueryEngine>.Instance);
        _store.BaselineExistsAsync(Repo, Sha, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static SymbolCard MethodCard(SymbolId id, string fqn, string? containingType) =>
        SymbolCard.CreateMinimal(id, fqn, SymbolKind.Method, "void X()",
            "Ns", File1, 5, 20, "public", Confidence.High,
            containingType: containingType);

    private void WireImplementsInterface(SymbolId concrete, string concreteFqn, string concreteContainingType,
        SymbolId interfaceMember, SymbolId concreteType, SymbolId interfaceType)
    {
        _store.GetSymbolAsync(Repo, Sha, concrete, Arg.Any<CancellationToken>())
              .Returns(MethodCard(concrete, concreteFqn, concreteContainingType));
        _store.GetTypeRelationsAsync(Repo, Sha, concreteType, Arg.Any<CancellationToken>())
              .Returns([new StoredTypeRelation(concreteType, interfaceType, TypeRelationKind.Interface, "IFoo")]);
        _store.GetSymbolAsync(Repo, Sha, interfaceMember, Arg.Any<CancellationToken>())
              .Returns(MethodCard(interfaceMember, interfaceMember.Value, "Ns.IFoo"));
    }

    [Fact]
    public async Task Callers_OnImplicitImpl_EmitsHint()
    {
        WireImplementsInterface(ConcreteMethod, "Ns.Foo.X", "Ns.Foo", InterfaceMethod, ConcreteType, InterfaceType);
        // One caller routed through the interface only — no concrete callers
        var ifaceCaller = SymbolId.From("M:Ns.Handler.DoIt");
        _store.GetReferencesAsync(Repo, Sha, ConcreteMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);
        _store.GetReferencesAsync(Repo, Sha, InterfaceMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, ifaceCaller, File1, 10, 10, null)]);

        var result = await _engine.GetCallersAsync(Routing, ConcreteMethod, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var hint = result.Value.Data.InterfaceImplementationHint;
        hint.Should().NotBeNull(because: "concrete method implements IFoo.X, so the hint must be present");
        hint!.Implements.Should().ContainSingle().Which.Should().Be(InterfaceMethod);
        hint.AdditionalCallersViaInterface.Should().Be(1);
        hint.RetryHint.Should().Contain("follow_interface=true");
        // Without follow_interface, the interface caller does NOT appear in the BFS result
        result.Value.Data.Nodes.Should().NotContain(n => n.SymbolId == ifaceCaller);
    }

    [Fact]
    public async Task Callers_OnImplicitImpl_FollowInterface_UnionsResults()
    {
        WireImplementsInterface(ConcreteMethod, "Ns.Foo.X", "Ns.Foo", InterfaceMethod, ConcreteType, InterfaceType);
        var concreteCaller = SymbolId.From("M:Ns.Internal.CallConcrete");
        var ifaceCaller = SymbolId.From("M:Ns.Handler.DoIt");
        _store.GetReferencesAsync(Repo, Sha, ConcreteMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, concreteCaller, File1, 5, 5, null)]);
        _store.GetReferencesAsync(Repo, Sha, InterfaceMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, ifaceCaller, File1, 10, 10, null)]);
        _store.GetSymbolAsync(Repo, Sha, concreteCaller, Arg.Any<CancellationToken>())
              .Returns(MethodCard(concreteCaller, "Ns.Internal.CallConcrete", "Ns.Internal"));
        _store.GetSymbolAsync(Repo, Sha, ifaceCaller, Arg.Any<CancellationToken>())
              .Returns(MethodCard(ifaceCaller, "Ns.Handler.DoIt", "Ns.Handler"));

        var result = await _engine.GetCallersAsync(
            Routing, ConcreteMethod, depth: 1, limitPerLevel: 20, null,
            ct: default, followInterface: true);

        result.IsSuccess.Should().BeTrue();
        var ids = result.Value.Data.Nodes.Select(n => n.SymbolId).ToHashSet();
        ids.Should().Contain(concreteCaller);
        ids.Should().Contain(ifaceCaller);
        // Hint is still surfaced even though the union was applied
        result.Value.Data.InterfaceImplementationHint.Should().NotBeNull();
        result.Value.Data.InterfaceImplementationHint!.RetryHint.Should().Contain("already applied");
    }

    [Fact]
    public async Task Callers_OnExplicitImpl_ResolvesViaSimpleNamePrefix()
    {
        // Explicit impl: M:Ns.Foo.IFoo.X has containingType "Ns.Foo" and the member tail
        // includes "IFoo." — the resolver must strip the interface simple name from the
        // tail when constructing the candidate interface-member SymbolId.
        var explicitImpl = SymbolId.From("M:Ns.Foo.IFoo.X");
        var explicitCard = SymbolCard.CreateMinimal(
            explicitImpl, "Ns.Foo.IFoo.X", SymbolKind.Method, "void IFoo.X()",
            "Ns", File1, 5, 20, "public", Confidence.High, containingType: "Ns.Foo");
        _store.GetSymbolAsync(Repo, Sha, explicitImpl, Arg.Any<CancellationToken>()).Returns(explicitCard);
        _store.GetTypeRelationsAsync(Repo, Sha, ConcreteType, Arg.Any<CancellationToken>())
              .Returns([new StoredTypeRelation(ConcreteType, InterfaceType, TypeRelationKind.Interface, "IFoo")]);
        _store.GetSymbolAsync(Repo, Sha, InterfaceMethod, Arg.Any<CancellationToken>())
              .Returns(MethodCard(InterfaceMethod, InterfaceMethod.Value, "Ns.IFoo"));
        _store.GetReferencesAsync(Repo, Sha, Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        var result = await _engine.GetCallersAsync(Routing, explicitImpl, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.InterfaceImplementationHint.Should().NotBeNull();
        result.Value.Data.InterfaceImplementationHint!.Implements.Should().Contain(InterfaceMethod);
    }

    [Fact]
    public async Task Callers_ContainingTypeIsSimpleName_StillResolvesInterface()
    {
        // Smoke regression: in production extraction card.ContainingType holds the
        // SIMPLE type name (e.g. "Foo"), not the FQN ("Ns.Foo"). The resolver must
        // derive the FQN from the doc-comment ID itself, not blindly trust the field.
        var concrete = SymbolId.From("M:Ns.Foo.X(System.String)");
        var ifaceMember = SymbolId.From("M:Ns.IFoo.X(System.String)");
        var cardWithSimpleContainingType = SymbolCard.CreateMinimal(
            concrete, "Ns.Foo.X(System.String)", SymbolKind.Method, "void X(string)",
            "Ns", File1, 5, 20, "public", Confidence.High,
            containingType: "Foo");  // SIMPLE name, like real Roslyn extraction
        _store.GetSymbolAsync(Repo, Sha, concrete, Arg.Any<CancellationToken>())
              .Returns(cardWithSimpleContainingType);
        _store.GetTypeRelationsAsync(Repo, Sha, ConcreteType, Arg.Any<CancellationToken>())
              .Returns([new StoredTypeRelation(ConcreteType, InterfaceType, TypeRelationKind.Interface, "IFoo")]);
        _store.GetSymbolAsync(Repo, Sha, ifaceMember, Arg.Any<CancellationToken>())
              .Returns(MethodCard(ifaceMember, ifaceMember.Value, "IFoo"));
        _store.GetReferencesAsync(Repo, Sha, Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        var result = await _engine.GetCallersAsync(Routing, concrete, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.InterfaceImplementationHint.Should().NotBeNull(
            because: "real-world cards store containing_type as a simple name, not the FQN");
        result.Value.Data.InterfaceImplementationHint!.Implements.Should().ContainSingle()
            .Which.Should().Be(ifaceMember);
    }

    [Fact]
    public async Task Callers_NotAnImpl_NoHint()
    {
        // Method on a class with no implemented interfaces
        var standalone = SymbolId.From("M:Ns.Plain.Run");
        var plainType = SymbolId.From("T:Ns.Plain");
        _store.GetSymbolAsync(Repo, Sha, standalone, Arg.Any<CancellationToken>())
              .Returns(MethodCard(standalone, "Ns.Plain.Run", "Ns.Plain"));
        _store.GetTypeRelationsAsync(Repo, Sha, plainType, Arg.Any<CancellationToken>())
              .Returns([]);
        _store.GetReferencesAsync(Repo, Sha, standalone, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        var result = await _engine.GetCallersAsync(Routing, standalone, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.InterfaceImplementationHint.Should().BeNull(
            because: "Ns.Plain implements no interfaces — no hint expected");
    }

    [Fact]
    public async Task Callers_OnInterfaceMember_NoUpwardWalk()
    {
        // Caller already passed the interface symbol directly. We don't try to walk
        // further upward — the result is the existing behavior, no hint emitted.
        _store.GetSymbolAsync(Repo, Sha, InterfaceMethod, Arg.Any<CancellationToken>())
              .Returns(MethodCard(InterfaceMethod, "Ns.IFoo.X", "Ns.IFoo"));
        _store.GetTypeRelationsAsync(Repo, Sha, InterfaceType, Arg.Any<CancellationToken>())
              .Returns([]);  // interface has no implements-edges of its own
        _store.GetReferencesAsync(Repo, Sha, InterfaceMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        var result = await _engine.GetCallersAsync(Routing, InterfaceMethod, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.InterfaceImplementationHint.Should().BeNull();
    }

    [Fact]
    public async Task Callers_MultipleInterfaces_AllMatched()
    {
        // class Foo : IA, IB, both declaring X() — hint must list both interface members.
        var ifaceAMember = SymbolId.From("M:Ns.IA.X");
        var ifaceBMember = SymbolId.From("M:Ns.IB.X");
        var ifaceAType = SymbolId.From("T:Ns.IA");
        var ifaceBType = SymbolId.From("T:Ns.IB");
        _store.GetSymbolAsync(Repo, Sha, ConcreteMethod, Arg.Any<CancellationToken>())
              .Returns(MethodCard(ConcreteMethod, "Ns.Foo.X", "Ns.Foo"));
        _store.GetTypeRelationsAsync(Repo, Sha, ConcreteType, Arg.Any<CancellationToken>())
              .Returns([
                  new StoredTypeRelation(ConcreteType, ifaceAType, TypeRelationKind.Interface, "IA"),
                  new StoredTypeRelation(ConcreteType, ifaceBType, TypeRelationKind.Interface, "IB"),
              ]);
        _store.GetSymbolAsync(Repo, Sha, ifaceAMember, Arg.Any<CancellationToken>())
              .Returns(MethodCard(ifaceAMember, "Ns.IA.X", "Ns.IA"));
        _store.GetSymbolAsync(Repo, Sha, ifaceBMember, Arg.Any<CancellationToken>())
              .Returns(MethodCard(ifaceBMember, "Ns.IB.X", "Ns.IB"));
        _store.GetReferencesAsync(Repo, Sha, Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);

        var result = await _engine.GetCallersAsync(Routing, ConcreteMethod, depth: 1, limitPerLevel: 20, null);

        result.IsSuccess.Should().BeTrue();
        var hint = result.Value.Data.InterfaceImplementationHint;
        hint.Should().NotBeNull();
        hint!.Implements.Should().BeEquivalentTo([ifaceAMember, ifaceBMember]);
    }

    [Fact]
    public async Task Callers_FollowInterface_DedupesAcrossSources()
    {
        // Synthesise a caller that appears under both the concrete and the interface
        // (artificial — same from_symbol on both refs). The union must dedupe by SymbolId.
        WireImplementsInterface(ConcreteMethod, "Ns.Foo.X", "Ns.Foo", InterfaceMethod, ConcreteType, InterfaceType);
        var caller = SymbolId.From("M:Ns.X.Y");
        _store.GetReferencesAsync(Repo, Sha, ConcreteMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, caller, File1, 5, 5, null)]);
        _store.GetReferencesAsync(Repo, Sha, InterfaceMethod, Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new StoredReference(RefKind.Call, caller, File1, 6, 6, null)]);
        _store.GetSymbolAsync(Repo, Sha, caller, Arg.Any<CancellationToken>())
              .Returns(MethodCard(caller, "Ns.X.Y", "Ns.X"));

        var result = await _engine.GetCallersAsync(
            Routing, ConcreteMethod, depth: 1, limitPerLevel: 20, null,
            ct: default, followInterface: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Nodes.Count(n => n.SymbolId == caller).Should().Be(1, because: "duplicates across (concrete, interface) sources must be deduped");
    }

    [Fact]
    public async Task Callers_OnOverload_OnlyMatchingArityHinted()
    {
        // M:Foo.X(System.Int32) implements M:IFoo.X(System.Int32); the no-arg overload
        // M:Foo.X() must NOT speculatively hint M:IFoo.X(System.Int32).
        var concreteIntOverload = SymbolId.From("M:Ns.Foo.X(System.Int32)");
        var interfaceIntOverload = SymbolId.From("M:Ns.IFoo.X(System.Int32)");
        var concreteNoArg = SymbolId.From("M:Ns.Foo.X");
        // For the int-overload: the candidate substitution should land an existing iface member
        _store.GetSymbolAsync(Repo, Sha, concreteIntOverload, Arg.Any<CancellationToken>())
              .Returns(MethodCard(concreteIntOverload, "Ns.Foo.X(int)", "Ns.Foo"));
        _store.GetSymbolAsync(Repo, Sha, interfaceIntOverload, Arg.Any<CancellationToken>())
              .Returns(MethodCard(interfaceIntOverload, "Ns.IFoo.X(int)", "Ns.IFoo"));
        _store.GetTypeRelationsAsync(Repo, Sha, ConcreteType, Arg.Any<CancellationToken>())
              .Returns([new StoredTypeRelation(ConcreteType, InterfaceType, TypeRelationKind.Interface, "IFoo")]);
        _store.GetReferencesAsync(Repo, Sha, Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([]);
        // For the no-arg overload: candidate M:Ns.IFoo.X resolves to NULL (no interface no-arg overload)
        _store.GetSymbolAsync(Repo, Sha, concreteNoArg, Arg.Any<CancellationToken>())
              .Returns(MethodCard(concreteNoArg, "Ns.Foo.X", "Ns.Foo"));
        _store.GetSymbolAsync(Repo, Sha, SymbolId.From("M:Ns.IFoo.X"), Arg.Any<CancellationToken>())
              .Returns((SymbolCard?)null);

        var intResult = await _engine.GetCallersAsync(Routing, concreteIntOverload, depth: 1, limitPerLevel: 20, null);
        var noArgResult = await _engine.GetCallersAsync(Routing, concreteNoArg, depth: 1, limitPerLevel: 20, null);

        intResult.Value.Data.InterfaceImplementationHint!.Implements.Should().ContainSingle().Which.Should().Be(interfaceIntOverload);
        noArgResult.Value.Data.InterfaceImplementationHint.Should().BeNull(
            because: "no-arg overload has no interface counterpart — no speculative hint");
    }
}
