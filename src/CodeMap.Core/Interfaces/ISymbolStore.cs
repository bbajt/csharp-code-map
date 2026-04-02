namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Persistence layer for baseline and overlay indexes.
/// Implementation: CodeMap.Storage.
///</summary>
public interface ISymbolStore
{
    // === Baseline Write ===

    /// <summary>
    /// Creates a new baseline database for the given repo + commit.
    /// Bulk-inserts all symbols, references, and files.
    /// </summary>
    Task CreateBaselineAsync(
        RepoId repoId,
        CommitSha commitSha,
        CompilationResult data,
        string repoRootPath = "",
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if a baseline index exists for the given repo + commit.
    /// </summary>
    Task<bool> BaselineExistsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    // === Baseline Read ===

    /// <summary>Gets a single symbol by ID from the baseline.</summary>
    Task<SymbolCard?> GetSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>Full-text search across symbol names, signatures, and documentation.</summary>
    Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(
        RepoId repoId,
        CommitSha commitSha,
        string query,
        SymbolSearchFilters? filters,
        int limit,
        CancellationToken ct = default);

    /// <summary>Gets references to or from a symbol.</summary>
    Task<IReadOnlyList<StoredReference>> GetReferencesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default,
        Enums.ResolutionState? resolutionState = null);

    /// <summary>Reads file content lines from the repo working directory.</summary>
    Task<FileSpan?> GetFileSpanAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        int startLine,
        int endLine,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all symbols from a specific file in the baseline.
    /// Used by the incremental compiler to detect which baseline symbols were deleted.
    /// Does not use FTS — direct SQL join on file path (ADR-017).
    /// </summary>
    Task<IReadOnlyList<SymbolCard>> GetSymbolsByFileAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all outgoing references from a symbol — i.e. what the symbol calls.
    /// Queries refs WHERE from_symbol_id = symbolId using idx_refs_from index.
    /// </summary>
    Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingReferencesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        RefKind? kind,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the base type and directly implemented interfaces for the given type symbol.
    /// Queries type_relations WHERE type_symbol_id = symbolId.
    /// </summary>
    Task<IReadOnlyList<StoredTypeRelation>> GetTypeRelationsAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all types that extend or implement the given type symbol.
    /// Queries type_relations WHERE related_symbol_id = symbolId using idx_type_rel_related.
    /// </summary>
    Task<IReadOnlyList<StoredTypeRelation>> GetDerivedTypesAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the SemanticLevel stored in the baseline meta, or null if not available
    /// (e.g. baseline indexed before PHASE-02-08).
    /// </summary>
    Task<Enums.SemanticLevel?> GetSemanticLevelAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up a symbol by its stable structural fingerprint.
    /// Returns null if the stable_id is not found (or if the baseline predates PHASE-03-01).
    /// Uses the idx_symbols_stable unique index for O(1) lookup.
    /// </summary>
    Task<SymbolCard?> GetSymbolByStableIdAsync(
        RepoId repoId,
        CommitSha commitSha,
        Types.StableId stableId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all facts of the given kind from the baseline, ordered by value.
    /// </summary>
    Task<IReadOnlyList<StoredFact>> GetFactsByKindAsync(
        RepoId repoId,
        CommitSha commitSha,
        Enums.FactKind kind,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns symbols by kind(s) using a direct SQL query on the symbols table.
    /// Pass <c>null</c> for <paramref name="kinds"/> to return all symbols regardless of kind.
    /// Use this instead of <see cref="SearchSymbolsAsync"/> when no text query is needed —
    /// FTS5 does not support bare <c>*</c> as a match-all wildcard (ADR-017).
    /// </summary>
    Task<IReadOnlyList<SymbolSearchHit>> GetSymbolsByKindsAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<SymbolKind>? kinds,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all facts associated with a specific symbol from the baseline.
    /// Used to hydrate SymbolCard.Facts in GetSymbolCardAsync (PHASE-03-05).
    /// </summary>
    Task<IReadOnlyList<StoredFact>> GetFactsForSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all unresolved edges from specific files.
    /// Used by the resolution worker after successful recompilation.
    /// </summary>
    Task<IReadOnlyList<UnresolvedEdge>> GetUnresolvedEdgesAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<FilePath> filePaths,
        CancellationToken ct = default);

    /// <summary>
    /// Upgrades an unresolved edge to resolved by setting to_symbol_id, stable_to_id,
    /// and clearing the syntactic hints. Matched by from_symbol_id + file_id + loc_start.
    /// </summary>
    Task UpgradeEdgeAsync(
        RepoId repoId,
        CommitSha commitSha,
        EdgeUpgrade upgrade,
        CancellationToken ct = default);

    /// <summary>
    /// Returns per-project diagnostics stored when the baseline was created,
    /// including project names, symbol counts, and reference counts.
    /// Returns an empty list if the baseline predates project-diagnostics storage.
    /// </summary>
    Task<IReadOnlyList<ProjectDiagnostic>> GetProjectDiagnosticsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a lightweight summary of every symbol in the baseline — just the fields
    /// needed for semantic diffing (stable_id, FQN, signature, visibility, kind).
    /// No documentation, no file spans, no facts.
    /// Used by <c>SemanticDiffer</c> to compare two baselines efficiently.
    /// </summary>
    Task<IReadOnlyList<SymbolSummary>> GetAllSymbolSummariesAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all repo-relative file paths indexed for the given commit.
    /// Used by <c>code.search_text</c> to enumerate files for content search.
    /// </summary>
    Task<IReadOnlyList<FilePath>> GetAllFilePathsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all repo-relative file paths and their stored source content for the given commit.
    /// <c>Content</c> is <c>null</c> for old baselines that pre-date the content column.
    /// Used by <c>code.search_text</c> to search file content without disk I/O.
    /// </summary>
    Task<IReadOnlyList<(FilePath Path, string? Content)>> GetAllFileContentsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the repository root path stored in the baseline metadata.
    /// Used by <c>code.search_text</c> to resolve repo-relative paths to absolute paths for disk reads.
    /// </summary>
    Task<string?> GetRepoRootAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    // === Lazy Metadata Resolution (PHASE-12-01) ===

    /// <summary>
    /// Inserts metadata stub <see cref="SymbolCard"/> records for a DLL type into
    /// the baseline using <c>INSERT OR IGNORE</c> semantics (concurrent-safe).
    /// Stubs are marked <c>is_decompiled = 1</c>.
    /// A synthetic virtual file row is inserted into the <c>files</c> table first
    /// to satisfy the FK constraint.
    /// </summary>
    /// <param name="typeRelations">Optional type relations for the resolved type (base class, interfaces).</param>
    /// <returns>Number of symbol rows actually inserted (0 if already present).</returns>
    Task<int> InsertMetadataStubsAsync(
        RepoId repoId,
        CommitSha commitSha,
        IReadOnlyList<SymbolCard> stubs,
        IReadOnlyList<Models.ExtractedTypeRelation>? typeRelations = null,
        CancellationToken ct = default);

    /// <summary>
    /// Triggers an FTS5 content table rebuild for the baseline's <c>symbols_fts</c>
    /// virtual table. Call after <see cref="InsertMetadataStubsAsync"/> to keep
    /// full-text search in sync with newly inserted stubs.
    /// </summary>
    Task RebuildFtsAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the JSON DLL fingerprint stored in the baseline <c>repo_meta</c> table,
    /// or <c>null</c> if the baseline predates PHASE-12-01.
    /// </summary>
    Task<string?> GetDllFingerprintAsync(
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    // === Lazy Decompiled Source (PHASE-12-02) ===

    /// <summary>
    /// Inserts a virtual file row into the <c>files</c> table with the given decompiled
    /// C# source. The file path must follow the convention
    /// <c>decompiled/{AssemblyName}/{TypeFullName.Replace('.', '/')}.cs</c>.
    /// Idempotent: uses <c>INSERT OR IGNORE</c> so concurrent lazy writes are safe.
    /// </summary>
    Task InsertVirtualFileAsync(
        RepoId repoId,
        CommitSha commitSha,
        string virtualPath,
        string content,
        IReadOnlyList<ExtractedReference>? decompiledRefs = null,
        CancellationToken ct = default);

    /// <summary>
    /// Upgrades a metadata stub (<c>is_decompiled=1</c>) to a decompiled symbol
    /// (<c>is_decompiled=2</c>). Updates the symbol's <c>file_id</c> to point at the
    /// virtual file and sets <c>is_decompiled=2</c>.
    /// Idempotent: the <c>WHERE is_decompiled=1</c> guard prevents double-upgrades.
    /// </summary>
    Task UpgradeDecompiledSymbolAsync(
        RepoId repoId,
        CommitSha commitSha,
        SymbolId symbolId,
        string virtualFilePath,
        CancellationToken ct = default);
}

// Supporting types for store operations

/// <summary>A symbol search result with a relevance score.</summary>
public record SymbolSearchHit(
    SymbolId SymbolId,
    string FullyQualifiedName,
    SymbolKind Kind,
    string Signature,
    string? DocumentationSnippet,
    FilePath FilePath,
    int Line,
    double Score
);

/// <summary>A stored reference from one symbol location to a target symbol.</summary>
public record StoredReference(
    RefKind Kind,
    SymbolId FromSymbol,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    string? Excerpt,
    Enums.ResolutionState ResolutionState = Enums.ResolutionState.Resolved,
    string? ToName = null,
    string? ToContainerHint = null
);

/// <summary>An outgoing reference: what a symbol calls or references.</summary>
public record StoredOutgoingReference(
    RefKind Kind,
    SymbolId ToSymbol,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    Enums.ResolutionState ResolutionState = Enums.ResolutionState.Resolved,
    string? ToName = null,
    string? ToContainerHint = null
);

/// <summary>A bounded excerpt of file content with line metadata.</summary>
public record FileSpan(
    FilePath FilePath,
    int StartLine,
    int EndLine,
    int TotalFileLines,
    string Content,
    bool Truncated
);

/// <summary>Optional filters for symbol search queries.</summary>
public record SymbolSearchFilters(
    IReadOnlyList<SymbolKind>? Kinds = null,
    string? Namespace = null,
    string? FilePath = null,
    string? ProjectName = null
);

/// <summary>A type-level relationship stored in the type_relations table.</summary>
public record StoredTypeRelation(
    SymbolId TypeSymbolId,
    SymbolId RelatedSymbolId,
    Enums.TypeRelationKind RelationKind,
    string DisplayName
);

/// <summary>An architectural fact stored in the facts table.</summary>
public record StoredFact(
    SymbolId SymbolId,
    Types.StableId? StableId,
    Enums.FactKind Kind,
    string Value,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    Enums.Confidence Confidence
);

/// <summary>
/// An unresolved reference edge produced by syntactic fallback extraction.
/// Used by the resolution worker to attempt upgrade to a resolved edge.
/// </summary>
public record UnresolvedEdge(
    string FromSymbolId,
    string? ToName,
    string? ToContainerHint,
    string RefKind,
    string FileId,
    int LocStart,
    int LocEnd
);

/// <summary>
/// Carries the resolved target information for upgrading an unresolved edge.
/// </summary>
public record EdgeUpgrade(
    string FromSymbolId,
    string FileId,
    int LocStart,
    SymbolId ResolvedToSymbolId,
    Types.StableId? ResolvedStableToId
);

/// <summary>
/// A lightweight symbol record for semantic diff comparisons.
/// Contains only the fields needed for stable_id / FQN matching and change detection.
/// </summary>
public record SymbolSummary(
    SymbolId SymbolId,
    Types.StableId? StableId,
    string FullyQualifiedName,
    string Signature,
    string Visibility,
    SymbolKind Kind
);
