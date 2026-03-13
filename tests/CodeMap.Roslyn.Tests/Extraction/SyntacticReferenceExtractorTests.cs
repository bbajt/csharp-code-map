namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Roslyn.Extraction;
using FluentAssertions;

public class SyntacticReferenceExtractorTests
{
    private static IReadOnlyList<Core.Interfaces.ExtractedReference> Extract(string source, string filePath = "src/Test.cs") =>
        SyntacticReferenceExtractor.ExtractAll([(filePath, source)], "/repo");

    // ── Method invocations ───────────────────────────────────────────────────

    [Fact]
    public void Extract_MethodInvocation_ProducesUnresolvedCallRef()
    {
        const string source = "class C { void M() { _svc.Execute(); } }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r =>
            r.Kind == RefKind.Call &&
            r.ToName == "Execute" &&
            r.ToContainerHint == "_svc" &&
            r.ResolutionState == ResolutionState.Unresolved);
    }

    [Fact]
    public void Extract_MethodInvocation_CapturesReceiverAsContainerHint()
    {
        const string source = "class C { void M() { this.foo.bar.Baz(); } }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r => r.Kind == RefKind.Call && r.ToName == "Baz");
        refs.Single(r => r.ToName == "Baz").ToContainerHint.Should().Be("this.foo.bar");
    }

    [Fact]
    public void Extract_LocalMethodCall_ContainerHintIsNull()
    {
        const string source = "class C { void M() { DoStuff(); } }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r => r.Kind == RefKind.Call && r.ToName == "DoStuff");
        refs.Single(r => r.ToName == "DoStuff").ToContainerHint.Should().BeNull();
    }

    // ── Object creation ──────────────────────────────────────────────────────

    [Fact]
    public void Extract_ObjectCreation_ProducesUnresolvedInstantiateRef()
    {
        const string source = "class C { void M() { var x = new Widget(); } }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r =>
            r.Kind == RefKind.Instantiate &&
            r.ToName == "Widget" &&
            r.ResolutionState == ResolutionState.Unresolved);
    }

    [Fact]
    public void Extract_ObjectCreation_CapturesGenericTypeName()
    {
        const string source = "class C { void M() { var x = new List<int>(); } }";
        var refs = Extract(source);

        // Generic type name extraction captures the base identifier "List"
        refs.Should().ContainSingle(r => r.Kind == RefKind.Instantiate && r.ToName == "List");
    }

    // ── Member access ────────────────────────────────────────────────────────

    [Fact]
    public void Extract_MemberAccess_Read_ProducesUnresolvedReadRef()
    {
        const string source = "class C { void M() { var x = _svc.Name; } }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r =>
            r.Kind == RefKind.Read &&
            r.ToName == "Name" &&
            r.ToContainerHint == "_svc" &&
            r.ResolutionState == ResolutionState.Unresolved);
    }

    [Fact]
    public void Extract_Assignment_ProducesUnresolvedWriteRef()
    {
        const string source = "class C { string _name; void M() { _svc.Name = \"test\"; } private object _svc; }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r =>
            r.Kind == RefKind.Write &&
            r.ToName == "Name" &&
            r.ToContainerHint == "_svc" &&
            r.ResolutionState == ResolutionState.Unresolved);
    }

    // ── From symbol (containing method) ─────────────────────────────────────

    [Fact]
    public void Extract_ContainingSymbol_UsesNearestMethodDeclaration()
    {
        const string source = "class Outer { void Inner() { _svc.Go(); } private object _svc; }";
        var refs = Extract(source);

        refs.Should().ContainSingle(r => r.ToName == "Go");
        var fromVal = refs.Single(r => r.ToName == "Go").FromSymbol.Value;
        fromVal.Should().Contain("Outer");
        fromVal.Should().Contain("Inner");
    }

    // ── Multiple refs ────────────────────────────────────────────────────────

    [Fact]
    public void Extract_NestedCalls_ProducesMultipleRefs()
    {
        const string source = "class C { void M() { a.X(); b.Y(); c.Z(); } private object a, b, c; }";
        var refs = Extract(source).Where(r => r.Kind == RefKind.Call).ToList();

        refs.Should().HaveCount(3);
        refs.Select(r => r.ToName).Should().Contain(["X", "Y", "Z"]);
    }

    // ── Truncation ───────────────────────────────────────────────────────────

    [Fact]
    public void Extract_LongReceiverExpression_TruncatesContainerHint()
    {
        var longReceiver = new string('a', 150);
        var source = $"class C {{ void M() {{ {longReceiver}.Baz(); }} }}";
        var refs = Extract(source).Where(r => r.ToName == "Baz").ToList();

        refs.Should().HaveCount(1);
        refs[0].ToContainerHint.Should().NotBeNull();
        refs[0].ToContainerHint!.Length.Should().BeLessOrEqualTo(100);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Extract_EmptyFile_ReturnsEmpty()
    {
        var refs = Extract("");
        refs.Should().BeEmpty();
    }

    [Fact]
    public void Extract_SyntaxErrors_ExtractsWhatItCan()
    {
        const string source = "class C { void M() { _svc.Execute(); BROKEN SYNTAX }";
        var refs = Extract(source);

        // Despite syntax errors, at least the valid call should be extracted
        refs.Should().Contain(r => r.ToName == "Execute");
    }

    [Fact]
    public void Extract_PropertyAccessor_ProducesRef()
    {
        const string source = "class C { int P => _svc.Value; private object _svc; }";
        var refs = Extract(source);

        refs.Should().Contain(r => r.ToName == "Value");
    }

    // ── ResolutionState on all refs ──────────────────────────────────────────

    [Fact]
    public void Extract_AllRefs_HaveUnresolvedState()
    {
        const string source = "class C { void M() { _svc.X(); var y = _obj.Prop; } private object _svc, _obj; }";
        var refs = Extract(source);

        refs.Should().NotBeEmpty();
        refs.Should().AllSatisfy(r => r.ResolutionState.Should().Be(ResolutionState.Unresolved));
    }

    // ── ToSymbol is empty ────────────────────────────────────────────────────

    [Fact]
    public void Extract_UnresolvedRef_HasEmptyToSymbol()
    {
        const string source = "class C { void M() { _svc.Execute(); } private object _svc; }";
        var refs = Extract(source);

        refs.Should().Contain(r => r.ToName == "Execute");
        refs.Where(r => r.ToName == "Execute")
            .Should().AllSatisfy(r => r.ToSymbol.Value.Should().BeEmpty());
    }
}
