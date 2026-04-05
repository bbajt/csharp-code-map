namespace CodeMap.Harness.Queries;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Harness.Repos;

/// <summary>Category of a harness query, used to select the correct comparison logic.</summary>
public enum QuerySuiteCategory
{
    SymbolSearch,
    CardAndContext,
    GraphTraversal,
    TypeHierarchy,
    Surfaces,
    TextSearch,
    SummarizeExport,
    Diff,
    OverlayWorkspace,
}

/// <summary>
/// A single parameterized query that can be executed against any IQueryEngine.
/// The harness executes each query against one or two engines and compares results.
/// </summary>
public interface IHarnessQuery
{
    /// <summary>
    /// Unique name, e.g. "symbols.search:OrderService" or "graph.callers:IOrderService.SubmitAsync".
    /// Used as the golden file key.
    /// </summary>
    string Name { get; }

    QuerySuiteCategory Category { get; }

    /// <summary>True = included in the CI smoke suite (Micro + Small repos, fast subset).</summary>
    bool IncludeInSmoke { get; }

    Task<HarnessQueryResult> ExecuteAsync(
        IQueryEngine engine,
        RepoDescriptor repo,
        CommitSha commitSha,
        CancellationToken ct);
}
