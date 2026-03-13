namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Represents the result of an incremental compilation over a set of changed files.
/// Contains symbols added/updated, symbols deleted, references, and the new revision number.
/// </summary>
public record OverlayDelta(
    IReadOnlyList<ExtractedFile> ReindexedFiles,
    IReadOnlyList<SymbolCard> AddedOrUpdatedSymbols,
    IReadOnlyList<SymbolId> DeletedSymbolIds,
    IReadOnlyList<ExtractedReference> AddedOrUpdatedReferences,
    IReadOnlyList<FilePath> DeletedReferenceFiles,
    int NewRevision,
    IReadOnlyList<ExtractedTypeRelation>? TypeRelations = null,
    Enums.SemanticLevel? SemanticLevel = null,
    IReadOnlyList<ExtractedFact>? Facts = null
)
{
    /// <summary>An empty delta with revision 1 — no changes detected.</summary>
    public static OverlayDelta Empty(int newRevision = 1) =>
        new(
            ReindexedFiles: [],
            AddedOrUpdatedSymbols: [],
            DeletedSymbolIds: [],
            AddedOrUpdatedReferences: [],
            DeletedReferenceFiles: [],
            NewRevision: newRevision);
}
