namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Enums;
using CodeMap.Core.Errors;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// High-level query orchestrator.
/// Implementation: CodeMap.Query.
/// </summary>
public interface IQueryEngine
{
    /// <summary>
    /// Searches for symbols matching the query. Returns summary hits — NOT full cards.
    /// Facts are not hydrated in search results; call <see cref="GetSymbolCardAsync"/> for full detail.
    /// </summary>
    Task<Result<ResponseEnvelope<SymbolSearchResponse>, CodeMapError>> SearchSymbolsAsync(
        RoutingContext routing,
        string? query,
        SymbolSearchFilters? filters,
        BudgetLimits? budgets,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a full symbol card for the given symbol ID, with <see cref="SymbolCard.Facts"/> hydrated.
    /// </summary>
    Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolCardAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Reads a range of source lines from a file on disk.
    /// In Ephemeral mode, virtual file content overrides the on-disk content for the file.
    /// </summary>
    Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetSpanAsync(
        RoutingContext routing,
        FilePath filePath,
        int startLine,
        int endLine,
        int contextLines,
        BudgetLimits? budgets,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the source span of a symbol's definition using its SpanStart/SpanEnd line numbers.
    /// Reads from disk — not from the database.
    /// </summary>
    Task<Result<ResponseEnvelope<SpanResponse>, CodeMapError>> GetDefinitionSpanAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int maxLines,
        int contextLines,
        CancellationToken ct = default);

    /// <summary>
    /// Returns classified references to the given symbol, with optional one-line source excerpts.
    /// Returns both resolved and unresolved references by default.
    /// Use <paramref name="resolutionState"/> to filter to only resolved or only unresolved edges.
    /// </summary>
    Task<Result<ResponseEnvelope<FindRefsResponse>, CodeMapError>> FindReferencesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        RefKind? kind,
        BudgetLimits? budgets,
        CancellationToken ct = default,
        Enums.ResolutionState? resolutionState = null);

    /// <summary>
    /// BFS traversal upward through the call graph to find all callers of a symbol.
    /// Depth-limited to prevent unbounded traversal.
    /// </summary>
    Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCallersAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default);

    /// <summary>
    /// BFS traversal downward through the call graph to find all callees of a symbol.
    /// Depth-limited to prevent unbounded traversal.
    /// </summary>
    Task<Result<ResponseEnvelope<CallGraphResponse>, CodeMapError>> GetCalleesAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int depth,
        int limitPerLevel,
        BudgetLimits? budgets,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the base type, implemented interfaces, and derived types for a given type symbol.
    /// </summary>
    Task<Result<ResponseEnvelope<TypeHierarchyResponse>, CodeMapError>> GetTypeHierarchyAsync(
        RoutingContext routing,
        SymbolId symbolId,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up a symbol by its stable structural fingerprint.
    /// Enables agents to find renamed symbols by their stable_id.
    /// Returns NOT_FOUND if the stable_id is absent or the baseline predates PHASE-03-01.
    /// </summary>
    Task<Result<ResponseEnvelope<SymbolCard>, CodeMapError>> GetSymbolByStableIdAsync(
        RoutingContext routing,
        Types.StableId stableId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists HTTP endpoints extracted from the indexed solution.
    /// Supports optional path prefix filter and HTTP method filter.
    /// </summary>
    Task<Result<ResponseEnvelope<ListEndpointsResponse>, CodeMapError>> ListEndpointsAsync(
        RoutingContext routing,
        string? pathFilter,
        string? httpMethod,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Lists configuration keys used by the indexed solution.
    /// Supports optional key prefix filter.
    /// </summary>
    Task<Result<ResponseEnvelope<ListConfigKeysResponse>, CodeMapError>> ListConfigKeysAsync(
        RoutingContext routing,
        string? keyFilter,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Lists database tables referenced by the indexed solution.
    /// Detects EF Core DbSet&lt;T&gt; properties, [Table] attributes, and raw SQL strings.
    /// Supports optional table name prefix filter.
    /// </summary>
    Task<Result<ResponseEnvelope<ListDbTablesResponse>, CodeMapError>> ListDbTablesAsync(
        RoutingContext routing,
        string? tableFilter,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Traces a feature through the codebase starting at the given entry point.
    /// Composes call graph traversal (BFS callees) with architectural fact annotation
    /// to produce a hierarchical tree showing what a feature does end-to-end.
    /// </summary>
    Task<Result<ResponseEnvelope<FeatureTraceResponse>, CodeMapError>> TraceFeatureAsync(
        RoutingContext routing,
        SymbolId entryPoint,
        int depth = 3,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Exports the indexed codebase as a self-contained markdown or JSON document
    /// suitable for pasting into any LLM chat interface.
    /// Supports three detail levels (summary / standard / full) and a token budget
    /// to control output size.
    /// </summary>
    Task<Result<ResponseEnvelope<ExportResponse>, CodeMapError>> ExportAsync(
        RoutingContext routing,
        string detail = "standard",
        string format = "markdown",
        int maxTokens = 4000,
        string[]? sectionFilter = null,
        string? repoPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a structured codebase summary from the index — no file reading, no LLM.
    /// Queries all 8 FactKinds and project metadata to produce a markdown document that
    /// describes the solution's architecture, API surface, data layer, config, DI, and more.
    /// </summary>
    Task<Result<ResponseEnvelope<SummarizeResponse>, CodeMapError>> SummarizeAsync(
        RoutingContext routing,
        string? repoPath = null,
        string[]? sectionFilter = null,
        int maxItemsPerSection = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Compares two baseline indexes and returns a semantic diff: symbols added/removed/renamed,
    /// and fact-level changes (endpoints, config keys, DB tables, DI registrations).
    /// Uses stable_id as primary matching key (rename-aware); falls back to FQN for old baselines.
    /// Both commits must have existing baselines — returns INDEX_NOT_AVAILABLE otherwise.
    /// Only <see cref="RoutingContext.RepoId"/> is read from <paramref name="routing"/>; diff is
    /// always between committed baselines (no overlay involvement).
    /// </summary>
    Task<Result<ResponseEnvelope<DiffResponse>, CodeMapError>> DiffAsync(
        RoutingContext routing,
        CommitSha fromCommit,
        CommitSha toCommit,
        IReadOnlyList<SymbolKind>? kinds = null,
        bool includeFacts = true,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the primary symbol's card with source code, plus cards of its immediate callees.
    /// One call replaces the typical search → get_card → get_definition_span → get_callees chain.
    /// Primary symbol code is capped at 200 lines; callee code at 50 lines each.
    /// </summary>
    Task<Result<ResponseEnvelope<SymbolContextResponse>, CodeMapError>> GetContextAsync(
        RoutingContext routing,
        SymbolId symbolId,
        int calleeDepth = 1,
        int maxCallees = 10,
        bool includeCode = true,
        CancellationToken ct = default);

    /// <summary>
    /// Searches indexed source file content for lines matching a regex pattern.
    /// Reads files from disk (working copy). Results bounded by <see cref="BudgetLimits.MaxResults"/>.
    /// In workspace mode, searches the current working-copy files using the baseline file inventory.
    /// </summary>
    Task<Result<ResponseEnvelope<SearchTextResponse>, CodeMapError>> SearchTextAsync(
        RoutingContext routing,
        string pattern,
        string? filePathFilter,
        BudgetLimits? budgets,
        CancellationToken ct = default);
}

// Response types used by IQueryEngine

/// <summary>Response payload for symbols.search.</summary>
public record SymbolSearchResponse(
    IReadOnlyList<SymbolSearchHit> Hits,
    int TotalCount,
    bool Truncated
);

/// <summary>Response payload for code.get_span and symbols.get_definition_span.</summary>
public record SpanResponse(
    FilePath FilePath,
    int StartLine,
    int EndLine,
    int TotalFileLines,
    string Content,
    bool Truncated
);

/// <summary>Response payload for refs.find.</summary>
public record FindRefsResponse(
    SymbolId TargetSymbol,
    IReadOnlyList<ClassifiedReference> References,
    int TotalCount,
    bool Truncated
);

/// <summary>A classified reference with optional one-line source excerpt.</summary>
public record ClassifiedReference(
    Enums.RefKind Kind,
    SymbolId FromSymbol,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    string? Excerpt,
    Enums.ResolutionState ResolutionState = Enums.ResolutionState.Resolved,
    string? ToName = null,
    string? ToContainerHint = null
);

/// <summary>Response payload for graph.callers and graph.callees.</summary>
public record CallGraphResponse(
    SymbolId Root,
    IReadOnlyList<CallGraphNode> Nodes,
    int TotalNodesFound,
    bool Truncated
);

/// <summary>A single node in a call graph result.</summary>
public record CallGraphNode(
    SymbolId SymbolId,
    string DisplayName,
    Enums.SymbolKind Kind,
    int Depth,
    FilePath? FilePath,
    int Line,
    IReadOnlyList<SymbolId> EdgesTo
);

/// <summary>A type reference in a hierarchy result (base type, interface, or derived type).</summary>
public record TypeRef(
    SymbolId SymbolId,
    string DisplayName
);

/// <summary>Response payload for types.hierarchy.</summary>
public record TypeHierarchyResponse(
    SymbolId TargetType,
    TypeRef? BaseType,
    IReadOnlyList<TypeRef> Interfaces,
    IReadOnlyList<TypeRef> DerivedTypes
);

/// <summary>Response payload for surfaces.list_endpoints.</summary>
public record ListEndpointsResponse(
    IReadOnlyList<EndpointInfo> Endpoints,
    int TotalCount,
    bool Truncated
);

/// <summary>A single HTTP endpoint extracted from the codebase.</summary>
public record EndpointInfo(
    string HttpMethod,
    string RoutePath,
    SymbolId HandlerSymbol,
    FilePath FilePath,
    int Line,
    Enums.Confidence Confidence
);

/// <summary>Response payload for surfaces.list_config_keys.</summary>
public record ListConfigKeysResponse(
    IReadOnlyList<ConfigKeyInfo> Keys,
    int TotalCount,
    bool Truncated
);

/// <summary>A single configuration key usage extracted from the codebase.</summary>
public record ConfigKeyInfo(
    string Key,
    SymbolId UsedBySymbol,
    FilePath FilePath,
    int Line,
    string UsagePattern,
    Enums.Confidence Confidence
);

/// <summary>Response payload for surfaces.list_db_tables.</summary>
public record ListDbTablesResponse(
    IReadOnlyList<DbTableInfo> Tables,
    int TotalCount,
    bool Truncated
);

/// <summary>A single database table reference extracted from the codebase.</summary>
public record DbTableInfo(
    string TableName,
    string? Schema,
    SymbolId? EntitySymbol,
    IReadOnlyList<SymbolId> ReferencedBy,
    Enums.Confidence Confidence
);
