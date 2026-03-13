namespace CodeMap.TestUtilities.Builders;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Fluent builder for creating SymbolCard instances in tests.
/// All fields have sensible defaults so you only set what matters for the test.
/// </summary>
public class SymbolCardBuilder
{
    private SymbolId _symbolId = SymbolId.From("TestNamespace.TestClass.TestMethod");
    private string _fqname = "TestNamespace.TestClass.TestMethod";
    private SymbolKind _kind = SymbolKind.Method;
    private string _signature = "void TestMethod()";
    private string? _documentation = null;
    private string _namespace = "TestNamespace";
    private string? _containingType = "TestClass";
    private FilePath _filePath = FilePath.From("src/TestClass.cs");
    private int _spanStart = 10;
    private int _spanEnd = 20;
    private string _visibility = "public";
    private Confidence _confidence = Confidence.High;
    private List<SymbolRef> _callsTop = [];
    private List<Fact> _facts = [];
    private List<string> _sideEffects = [];
    private List<string> _thrownExceptions = [];
    private List<EvidencePointer> _evidence = [];

    public SymbolCardBuilder WithSymbolId(string id) { _symbolId = SymbolId.From(id); _fqname = id; return this; }
    public SymbolCardBuilder WithKind(SymbolKind kind) { _kind = kind; return this; }
    public SymbolCardBuilder WithSignature(string sig) { _signature = sig; return this; }
    public SymbolCardBuilder WithDocumentation(string doc) { _documentation = doc; return this; }
    public SymbolCardBuilder WithNamespace(string ns) { _namespace = ns; return this; }
    public SymbolCardBuilder WithContainingType(string? type) { _containingType = type; return this; }
    public SymbolCardBuilder WithFilePath(string path) { _filePath = FilePath.From(path); return this; }
    public SymbolCardBuilder WithSpan(int start, int end) { _spanStart = start; _spanEnd = end; return this; }
    public SymbolCardBuilder WithVisibility(string v) { _visibility = v; return this; }
    public SymbolCardBuilder WithConfidence(Confidence c) { _confidence = c; return this; }
    public SymbolCardBuilder WithCallsTop(params SymbolRef[] calls) { _callsTop = [.. calls]; return this; }
    public SymbolCardBuilder WithFacts(params Fact[] facts) { _facts = [.. facts]; return this; }

    public SymbolCard Build() => new(
        _symbolId, _fqname, _kind, _signature, _documentation,
        _namespace, _containingType, _filePath, _spanStart, _spanEnd,
        _visibility, _callsTop, _facts, _sideEffects, _thrownExceptions,
        _evidence, _confidence
    );
}
