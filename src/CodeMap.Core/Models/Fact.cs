namespace CodeMap.Core.Models;

/// <summary>
/// An architectural fact extracted about a symbol (route, DI registration, etc.).
/// </summary>
public record Fact(
    Enums.FactKind Kind,
    string Value,
    string? Detail = null
);
