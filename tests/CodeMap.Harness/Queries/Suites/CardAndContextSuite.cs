namespace CodeMap.Harness.Queries.Suites;

using System.Diagnostics;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Telemetry;

/// <summary>
/// symbols.get_card and symbols.get_context.
/// Parity rule: field-by-field (fqn, kind); FactKeys set-equal.
/// </summary>
public sealed class CardForFirstSearchHitSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 0
        ? repo.KnownQueryInputs[0]
        : "Service";

    public string Name => $"symbols.get_card:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.CardAndContext;
    public bool IncludeInSmoke => true;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        // Step 1: search to find the symbol_id
        var searchResult = await engine.SearchSymbolsAsync(routing, _term, filters: null, budgets: null, ct)
            .ConfigureAwait(false);
        if (!searchResult.IsSuccess || searchResult.Value.Data.Hits.Count == 0)
        {
            sw.Stop();
            return QueryHelpers.RunError(Name, searchResult.IsFailure
                ? searchResult.Error.Code
                : "NOT_FOUND", sw.Elapsed);
        }

        var symbolId = searchResult.Value.Data.Hits[0].SymbolId;

        // Step 2: get_card
        var cardResult = await engine.GetSymbolCardAsync(routing, symbolId, ct).ConfigureAwait(false);
        sw.Stop();

        return cardResult.IsSuccess
            ? QueryHelpers.RunQuery(Name, ResultNormalizer.FromSymbolCard(cardResult.Value.Data), sw.Elapsed)
            : QueryHelpers.RunError(Name, cardResult.Error.Code, sw.Elapsed);
    }
}

/// <summary>symbols.get_context for anchor[1] (kind-filtered search for deterministic results).</summary>
public sealed class ContextForFirstSearchHitSuite(RepoDescriptor repo, RepoId repoId) : IHarnessQuery
{
    private readonly string _term = repo.KnownQueryInputs.Count > 1
        ? repo.KnownQueryInputs[1]
        : repo.KnownQueryInputs.Count > 0 ? repo.KnownQueryInputs[0] : "Service";

    // Use anchor kind to filter search — eliminates BM25 vs custom ranking divergence
    private readonly SymbolKind? _anchorKind = repo.Anchors.Count > 1 ? repo.Anchors[1].Kind : null;

    public string Name => $"symbols.get_context:{_term}";
    public QuerySuiteCategory Category => QuerySuiteCategory.CardAndContext;
    public bool IncludeInSmoke => false;

    public async Task<HarnessQueryResult> ExecuteAsync(IQueryEngine engine, RepoDescriptor repo, CommitSha commitSha, CancellationToken ct)
    {
        var routing = QueryHelpers.CommittedRouting(repoId, commitSha);
        var sw = Stopwatch.StartNew();

        var filters = _anchorKind.HasValue ? new SymbolSearchFilters(Kinds: [_anchorKind.Value]) : null;
        var searchResult = await engine.SearchSymbolsAsync(routing, _term, filters, budgets: null, ct)
            .ConfigureAwait(false);
        if (!searchResult.IsSuccess || searchResult.Value.Data.Hits.Count == 0)
        {
            sw.Stop();
            return QueryHelpers.RunError(Name, "NOT_FOUND", sw.Elapsed);
        }

        var symbolId = searchResult.Value.Data.Hits[0].SymbolId;
        var ctxResult = await engine.GetContextAsync(routing, symbolId, ct: ct).ConfigureAwait(false);
        sw.Stop();

        if (!ctxResult.IsSuccess)
            return QueryHelpers.RunError(Name, ctxResult.Error.Code, sw.Elapsed);

        var ctx = ctxResult.Value.Data;
        // Normalize as a CardAndContext: SymbolId = primary + callees; FactKeys = primary facts
        var ids = new List<string> { ctx.PrimarySymbol.Card.SymbolId.Value };
        ids.AddRange(ctx.Callees.Select(c => c.Card.SymbolId.Value));
        ids.Sort();

        var result = new NormalizedResult(
            QuerySuiteCategory.CardAndContext,
            SymbolIds: ids,
            EdgeKeys: [],
            FactKeys: ctx.PrimarySymbol.Card.Facts.Select(f => $"{f.Kind}:{f.Value}").OrderBy(x => x).ToList(),
            ScalarFields: new Dictionary<string, string>
            {
                ["fqn"] = ctx.PrimarySymbol.Card.FullyQualifiedName,
                ["callee_count"] = ctx.Callees.Count.ToString(),
            },
            IsTruncated: false,
            TotalAvailable: null);

        return QueryHelpers.RunQuery(Name, result, sw.Elapsed);
    }
}

public static class CardAndContextSuiteFactory
{
    public static IReadOnlyList<IHarnessQuery> Create(RepoDescriptor repo, RepoId repoId) =>
    [
        new CardForFirstSearchHitSuite(repo, repoId),
        new ContextForFirstSearchHitSuite(repo, repoId),
    ];
}
