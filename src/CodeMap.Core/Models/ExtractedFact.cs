namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;

/// <summary>
/// An architectural fact extracted about a symbol during compilation.
/// Examples: HTTP route, DI registration, database table, config key.
/// </summary>
public record ExtractedFact(
    SymbolId SymbolId,
    StableId? StableId,
    FactKind Kind,
    string Value,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    Confidence Confidence
);
