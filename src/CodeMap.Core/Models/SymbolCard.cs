namespace CodeMap.Core.Models;

/// <summary>
/// Full structured representation of a C# symbol, returned by symbols.get_card.
/// All collection fields use IReadOnlyList to ensure immutability.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Facts"/> is populated since PHASE-03-05. Cards returned by
/// <c>IQueryEngine.GetSymbolCardAsync</c> have Facts hydrated from the database.
/// Cards returned by search results do NOT hydrate Facts (performance) — call
/// <c>IQueryEngine.GetSymbolCardAsync</c> for full detail.
/// </para>
/// <para>
/// <see cref="StableId"/> is nullable for backwards compatibility with baselines
/// indexed before PHASE-03-01. New baselines always populate it.
/// Accepts <c>sym_</c>-prefixed input to distinguish from FQN symbol IDs.
/// </para>
/// </remarks>
public record SymbolCard(
    Types.SymbolId SymbolId,
    string FullyQualifiedName,
    Enums.SymbolKind Kind,
    string Signature,
    string? Documentation,
    string Namespace,
    string? ContainingType,
    Types.FilePath FilePath,
    int SpanStart,
    int SpanEnd,
    string Visibility,
    IReadOnlyList<SymbolRef> CallsTop,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> ThrownExceptions,
    IReadOnlyList<EvidencePointer> Evidence,
    Enums.Confidence Confidence,
    Types.StableId? StableId = null
)
{
    /// <summary>
    /// Indicates the origin of this symbol's data.
    /// 0 = indexed from C# source (existing behavior).
    /// 1 = Roslyn metadata stub — lazy Level 1 (PHASE-12-01).
    /// 2 = ICSharpCode.Decompiler reconstructed source — lazy Level 2 (PHASE-12-02).
    /// Set by BaselineStore.GetSymbolAsync when reading from the DB.
    /// All existing construction sites default to 0 (source symbol).
    /// </summary>
    public int IsDecompiled { get; init; } = 0;
    /// <summary>
    /// Creates a minimal SymbolCard with empty collections.
    /// Used during extraction when not all data is available yet.
    /// </summary>
    public static SymbolCard CreateMinimal(
        Types.SymbolId symbolId,
        string fullyQualifiedName,
        Enums.SymbolKind kind,
        string signature,
        string @namespace,
        Types.FilePath filePath,
        int spanStart,
        int spanEnd,
        string visibility,
        Enums.Confidence confidence,
        string? documentation = null,
        string? containingType = null)
        => new(
            symbolId, fullyQualifiedName, kind, signature,
            documentation, @namespace, containingType,
            filePath, spanStart, spanEnd, visibility,
            CallsTop: [],
            Facts: [],
            SideEffects: [],
            ThrownExceptions: [],
            Evidence: [],
            confidence);
}
