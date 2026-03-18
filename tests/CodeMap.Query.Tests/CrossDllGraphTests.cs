namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class CrossDllGraphTests
{
    private static readonly RepoId Repo = RepoId.From("cross-dll-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('c', 40));

    private static SymbolCard MakeCard(string id, int isDecompiled = 0) =>
        SymbolCard.CreateMinimal(
            SymbolId.From(id), id, SymbolKind.Method, $"public void {id}()", "Ns",
            FilePath.From("decompiled/Dll/Ns/Cls.cs"), 0, 0, "public", Confidence.High)
            with { IsDecompiled = isDecompiled };

    // ─── TraceNode.IsDecompiled set correctly based on card ──────────────────

    [Fact]
    public async Task TraceAsync_DllNode_HasIsDecompiledTrue()
    {
        var store = Substitute.For<ISymbolStore>();
        var entryId = SymbolId.From("M:Source.Controller.Action");
        var dllMethodId = SymbolId.From("M:Dll.Service.DoWork");

        var entryCard = MakeCard("M:Source.Controller.Action", isDecompiled: 0);
        var dllCard = MakeCard("M:Dll.Service.DoWork", isDecompiled: 2);

        store.GetSymbolAsync(Repo, Sha, entryId, Arg.Any<CancellationToken>()).Returns(entryCard);
        store.GetSymbolAsync(Repo, Sha, dllMethodId, Arg.Any<CancellationToken>()).Returns(dllCard);
        store.GetFactsForSymbolAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoredFact>());

        // Entry → dllMethod call edge
        store.GetOutgoingReferencesAsync(Repo, Sha, entryId, null, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<StoredOutgoingReference>
            {
                new(RefKind.Call, dllMethodId, FilePath.From("src/Controller.cs"), 10, 10)
            });
        store.GetOutgoingReferencesAsync(Repo, Sha, dllMethodId, null, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoredOutgoingReference>());

        var traverser = new GraphTraverser();
        var tracer = new FeatureTracer(store, traverser);
        var result = await tracer.TraceAsync(Repo, Sha, entryId, depth: 2, limit: 10);

        result.IsSuccess.Should().BeTrue();
        var tree = result.Value;
        tree.Nodes.Should().HaveCount(1);
        var rootNode = tree.Nodes[0];
        rootNode.IsDecompiled.Should().BeFalse(); // source symbol
        rootNode.Children.Should().HaveCount(1);
        rootNode.Children[0].IsDecompiled.Should().BeTrue(); // DLL symbol (is_decompiled=2)
    }

    [Fact]
    public async Task TraceAsync_SourceNode_HasIsDecompiledFalse()
    {
        var store = Substitute.For<ISymbolStore>();
        var entryId = SymbolId.From("M:Source.Controller.Action");
        var childId = SymbolId.From("M:Source.Service.Process");

        store.GetSymbolAsync(Repo, Sha, entryId, Arg.Any<CancellationToken>())
            .Returns(MakeCard("M:Source.Controller.Action", isDecompiled: 0));
        store.GetSymbolAsync(Repo, Sha, childId, Arg.Any<CancellationToken>())
            .Returns(MakeCard("M:Source.Service.Process", isDecompiled: 0));
        store.GetFactsForSymbolAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(),
            Arg.Any<SymbolId>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoredFact>());
        store.GetOutgoingReferencesAsync(Repo, Sha, entryId, null, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<StoredOutgoingReference>
            {
                new(RefKind.Call, childId, FilePath.From("src/Controller.cs"), 5, 5)
            });
        store.GetOutgoingReferencesAsync(Repo, Sha, childId, null, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StoredOutgoingReference>());

        var tracer = new FeatureTracer(store, new GraphTraverser());
        var result = await tracer.TraceAsync(Repo, Sha, entryId, depth: 2, limit: 10);

        result.IsSuccess.Should().BeTrue();
        result.Value.Nodes[0].Children[0].IsDecompiled.Should().BeFalse();
    }

    // ─── GraphTraverser: MaxLazyResolutions budget ────────────────────────────

    [Fact]
    public async Task GraphTraverser_BudgetExhausted_SetsTruncatedAndStopsExpanding()
    {
        var resolver = Substitute.For<IMetadataResolver>();
        // Resolver always returns null (decompilation fails — but budget is still consumed)
        resolver.TryDecompileTypeAsync(Arg.Any<SymbolId>(), Arg.Any<RepoId>(),
            Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var traverser = new GraphTraverser(resolver, maxLazyResolutions: 2);

        var rootId = SymbolId.From("M:Source.Root");
        var stub1 = SymbolId.From("M:Dll.Stub1.Method");
        var stub2 = SymbolId.From("M:Dll.Stub2.Method");
        var stub3 = SymbolId.From("M:Dll.Stub3.Method");

        // Root → [stub1, stub2, stub3]; stubs have no outgoing edges
        var expandCalls = new Dictionary<SymbolId, IReadOnlyList<SymbolId>>
        {
            [rootId]  = [stub1, stub2, stub3],
            [stub1]   = [],
            [stub2]   = [],
            [stub3]   = [],
        };

        var result = await traverser.TraverseAsync(
            rootId,
            (sid, _) => Task.FromResult(
                expandCalls.TryGetValue(sid, out var ids) ? ids : Array.Empty<SymbolId>()),
            maxDepth: 3,
            limitPerLevel: 10,
            lazyRepoId: Repo,
            lazyCommitSha: Sha);

        result.Truncated.Should().BeTrue();
        // Only 2 decompilation calls (budget = 2); 3rd stub hits budget == 0 → truncated
        await resolver.Received(2).TryDecompileTypeAsync(
            Arg.Any<SymbolId>(), Repo, Sha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphTraverser_WithinBudget_CallsResolverAndRetries()
    {
        var resolver = Substitute.For<IMetadataResolver>();
        var stub = SymbolId.From("M:Dll.Service.DoWork");
        var child = SymbolId.From("M:Dll.Service.InnerCall");

        // Decompilation succeeds for the stub → retry expandNode finds a child
        resolver.TryDecompileTypeAsync(stub, Arg.Any<RepoId>(),
            Arg.Any<CommitSha>(), Arg.Any<CancellationToken>())
            .Returns("decompiled/Dll/Ns.cs");

        var rootId = SymbolId.From("M:Source.Root");
        var callCount = new Dictionary<SymbolId, int>();

        var result = await new GraphTraverser(resolver, maxLazyResolutions: 5).TraverseAsync(
            rootId,
            (sid, _) =>
            {
                callCount.TryAdd(sid, 0);
                callCount[sid]++;
                return Task.FromResult<IReadOnlyList<SymbolId>>(sid == rootId ? [stub] :
                    sid == stub && callCount[sid] > 1 ? [child] : []);
            },
            maxDepth: 3,
            limitPerLevel: 10,
            lazyRepoId: Repo,
            lazyCommitSha: Sha);

        // stub was retried → child was found
        result.Nodes.Any(n => n.SymbolId == child).Should().BeTrue();
        result.Truncated.Should().BeFalse();
    }
}
