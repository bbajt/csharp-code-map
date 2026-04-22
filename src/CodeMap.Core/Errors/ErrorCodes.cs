namespace CodeMap.Core.Errors;

/// <summary>
/// All error codes as string constants.
/// These values appear in CodeMapError.Code and in MCP error responses.
/// </summary>
public static class ErrorCodes
{
    /// <summary>A parameter or input value failed validation.</summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>A requested symbol or resource was not found in the index.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>A query exceeded its configured hard cap (results, lines, or characters).</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    /// <summary>No baseline index exists for the requested repo + commit. Retryable after indexing.</summary>
    public const string IndexNotAvailable = "INDEX_NOT_AVAILABLE";

    /// <summary>A workspace-scoped operation was attempted without providing a workspace ID.</summary>
    public const string WorkspaceRequired = "WORKSPACE_REQUIRED";

    /// <summary>The Roslyn compilation failed during indexing. Retryable after fixing build errors.</summary>
    public const string CompilationFailed = "COMPILATION_FAILED";

    /// <summary>A name-based lookup matched multiple symbols. The error message lists candidate symbol_ids so the caller can pick one.</summary>
    public const string Ambiguous = "AMBIGUOUS";
}
