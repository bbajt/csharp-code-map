namespace CodeMap.Core.Models;

/// <summary>
/// A symbol card paired with its source code.
/// Used in <see cref="SymbolContextResponse"/> for both the primary symbol and its callees.
/// </summary>
public record SymbolCardWithCode(
    SymbolCard Card,
    string? SourceCode,
    bool Truncated
);

/// <summary>
/// Response payload for symbols.get_context.
/// Returns the primary symbol's card with source code, plus cards of its immediate callees.
/// One call replaces the typical search → card → get_definition_span → callees chain.
/// </summary>
public record SymbolContextResponse(
    SymbolCardWithCode PrimarySymbol,
    IReadOnlyList<SymbolCardWithCode> Callees,
    int TotalCalleesFound,
    string Markdown
);
