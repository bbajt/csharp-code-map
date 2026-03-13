namespace CodeMap.Core.Models;

/// <summary>
/// A reference from one symbol to another, extracted from Roslyn analysis.
/// Used in SymbolCard.CallsTop to show outgoing calls.
/// </summary>
public record SymbolRef(
    Types.SymbolId ToSymbol,
    string FullyQualifiedName,
    Enums.RefKind Kind,
    Types.FilePath FilePath,
    int Line
);
