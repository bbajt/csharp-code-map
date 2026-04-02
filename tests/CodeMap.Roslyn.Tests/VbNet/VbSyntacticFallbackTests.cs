namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction.VbNet;
using FluentAssertions;
using CmSymbolKind = CodeMap.Core.Enums.SymbolKind;

public class VbSyntacticFallbackTests
{
    [Fact]
    public void ExtractAll_VbSource_ProducesClassAndMethodSymbols()
    {
        const string vbSource = """
            Public Class Foo
                Public Sub Bar()
                End Sub
            End Class
            """;

        var files = new[] { ("Foo.vb", vbSource) };
        var (symbols, _) = VbSyntacticExtractor.ExtractAll(files, "");

        symbols.Should().Contain(s =>
            s.FullyQualifiedName.Contains("Foo") && s.Kind == CmSymbolKind.Class);
        symbols.Should().Contain(s =>
            s.FullyQualifiedName.Contains("Bar") && s.Kind == CmSymbolKind.Method);
    }

    [Fact]
    public void ExtractAll_VbCallSite_ProducesUnresolvedRef()
    {
        const string vbSource = """
            Public Class Foo
                Public Sub Bar()
                    Baz.Qux()
                End Sub
            End Class
            """;

        var files = new[] { ("Foo.vb", vbSource) };
        var (_, refs) = VbSyntacticExtractor.ExtractAll(files, "");

        refs.Should().Contain(r =>
            r.ToName == "Qux" && r.ResolutionState == ResolutionState.Unresolved);
    }

    [Fact]
    public void ExtractAll_NestedClass_InnerMethodRefUsesInnerContainerName()
    {
        // CRITICAL-2 regression: _currentContainer must be stacked so that methods inside
        // a nested class use the inner class name as container prefix, not the outer's.
        const string vbSource = """
            Public Class Outer
                Public Class Inner
                    Public Sub Foo()
                        Bar()
                    End Sub
                End Class
            End Class
            """;

        var files = new[] { ("Nested.vb", vbSource) };
        var (_, refs) = VbSyntacticExtractor.ExtractAll(files, "");

        refs.Should().Contain(r =>
            r.FromSymbol.Value.StartsWith("Inner::") &&
            r.ToName == "Bar");
    }

    [Fact]
    public void ExtractAll_InterfaceBlock_SymbolAndMethodRefUseInterfaceName()
    {
        // CRITICAL-2 regression: VisitInterfaceBlock must push its name so that methods
        // inside interface bodies emit the correct container in fromId.
        const string vbSource = """
            Public Class Svc
                Public Sub Process()
                    DoWork()
                End Sub
            End Class
            """;

        var files = new[] { ("Svc.vb", vbSource) };
        var (symbols, refs) = VbSyntacticExtractor.ExtractAll(files, "");

        // Symbol for Process should use Svc as container → ID "Svc::Process"
        symbols.Should().Contain(s =>
            s.SymbolId.Value == "Svc::Process" && s.Kind == CmSymbolKind.Method);
        // Ref from Process should carry "Svc::Process" as fromId
        refs.Should().Contain(r =>
            r.FromSymbol.Value == "Svc::Process" && r.ToName == "DoWork");
    }
}
