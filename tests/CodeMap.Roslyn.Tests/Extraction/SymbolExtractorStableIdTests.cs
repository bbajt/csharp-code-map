namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class SymbolExtractorStableIdTests
{
    // ── Cards have stable IDs ────────────────────────────────────────────────

    [Fact]
    public void ExtractAllWithStableIds_ClassWithMethod_AllCardsHaveStableId()
    {
        var compilation = CompilationBuilder.Create("""
            namespace TestNs;
            public class MyService
            {
                public void Execute(int x) { }
                public string Compute(string s) => s;
            }
            """);

        var (cards, map) = SymbolExtractor.ExtractAllWithStableIds(compilation, "TestProject");

        cards.Should().NotBeEmpty();
        cards.Should().AllSatisfy(c => c.StableId.Should().NotBeNull("every extracted card should have a stable ID"));
        map.Should().NotBeEmpty();
    }

    // ── StableIdMap keys match SymbolId values ───────────────────────────────

    [Fact]
    public void ExtractAllWithStableIds_StableIdMapKeys_MatchCardSymbolIds()
    {
        var compilation = CompilationBuilder.Create("""
            namespace TestNs;
            public class Alpha
            {
                public void Foo() { }
            }
            """);

        var (cards, map) = SymbolExtractor.ExtractAllWithStableIds(compilation, "TestProject");

        foreach (var card in cards)
        {
            card.StableId.Should().NotBeNull();
            map.Should().ContainKey(card.SymbolId.Value);
            map[card.SymbolId.Value].Should().Be(card.StableId!.Value);
        }
    }

    // ── Stable IDs have sym_ prefix ──────────────────────────────────────────

    [Fact]
    public void ExtractAllWithStableIds_StableIds_HaveSymPrefix()
    {
        var compilation = CompilationBuilder.Create("""
            namespace TestNs;
            public interface IHandler
            {
                void Handle();
            }
            """);

        var (cards, _) = SymbolExtractor.ExtractAllWithStableIds(compilation, "TestProject");

        cards.Should().AllSatisfy(c =>
            c.StableId!.Value.Value.Should().StartWith("sym_",
                "stable IDs must use the sym_ prefix to distinguish from Roslyn FQN format"));
    }

    // ── Stable IDs are deterministic across two calls ────────────────────────

    [Fact]
    public void ExtractAllWithStableIds_SameSource_ProducesSameStableIds()
    {
        const string source = """
            namespace TestNs;
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public int Subtract(int a, int b) => a - b;
            }
            """;

        var comp1 = CompilationBuilder.Create(source);
        var comp2 = CompilationBuilder.Create(source);

        var (cards1, _) = SymbolExtractor.ExtractAllWithStableIds(comp1, "TestProject");
        var (cards2, _) = SymbolExtractor.ExtractAllWithStableIds(comp2, "TestProject");

        var ids1 = cards1.Select(c => c.StableId!.Value.Value).OrderBy(x => x).ToList();
        var ids2 = cards2.Select(c => c.StableId!.Value.Value).OrderBy(x => x).ToList();

        ids1.Should().Equal(ids2, "stable IDs must be deterministic for the same source");
    }
}
