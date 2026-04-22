namespace CodeMap.Core.Errors;

/// <summary>
/// Structured error returned by all fallible CodeMap operations.
/// Maps to the MCP error response contract.
/// </summary>
public record CodeMapError(
    string Code,
    string Message,
    Dictionary<string, object>? Details = null,
    bool Retryable = false
)
{
    /// <summary>Symbol or resource was not found.</summary>
    public static CodeMapError NotFound(string entity, string id) =>
        new(ErrorCodes.NotFound, $"{entity} '{id}' not found.");

    /// <summary>Request argument failed validation.</summary>
    public static CodeMapError InvalidArgument(string message) =>
        new(ErrorCodes.InvalidArgument, message);

    /// <summary>A budget limit was exceeded.</summary>
    public static CodeMapError BudgetExceeded(string limit, int requested, int max) =>
        new(ErrorCodes.BudgetExceeded,
            $"{limit} budget exceeded: requested {requested}, hard cap is {max}.",
            new Dictionary<string, object>
            {
                ["limit"] = limit,
                ["requested"] = requested,
                ["hard_cap"] = max
            });

    /// <summary>No baseline index exists for the given repo + commit. Retryable after indexing.</summary>
    public static CodeMapError IndexNotAvailable(string repoId, string commitSha) =>
        new(ErrorCodes.IndexNotAvailable,
            $"No baseline index for repo '{repoId}' at commit '{commitSha}'. Call index.ensure_baseline first.",
            Retryable: true);

    /// <summary>Compilation failed during indexing. Retryable after fixing build errors.</summary>
    public static CodeMapError CompilationFailed(string message, IReadOnlyList<string>? failedProjects = null) =>
        new(ErrorCodes.CompilationFailed, message,
            failedProjects is { Count: > 0 }
                ? new Dictionary<string, object> { ["failed_projects"] = failedProjects }
                : null,
            Retryable: true);

    /// <summary>
    /// A name-based lookup matched multiple symbols. Details carries a "candidates"
    /// list so the caller can pick one and retry with symbol_id (or narrow with name_filter).
    /// </summary>
    public static CodeMapError Ambiguous(string message, IReadOnlyList<string> candidates) =>
        new(ErrorCodes.Ambiguous, message,
            new Dictionary<string, object> { ["candidates"] = candidates });
}
