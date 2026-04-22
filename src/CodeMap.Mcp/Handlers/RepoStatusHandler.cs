namespace CodeMap.Mcp.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Serialization;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles the <c>repo.status</c> MCP tool.
/// Reports current Git state and whether a baseline index exists.
/// </summary>
/// <remarks>
/// <b>JSON params:</b> repo_path (required, string).
/// Returns INVALID_ARGUMENT if repo_path is missing.
/// Response includes RepoId, CommitSha, BranchName, IsClean, BaselineIndexExists,
/// and the list of active Workspaces for this repo.
/// </remarks>
public sealed class RepoStatusHandler
{
    private readonly IGitService _git;
    private readonly ISymbolStore _store;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IRepoRegistry _repoRegistry;
    private readonly ILogger<RepoStatusHandler> _logger;

    public RepoStatusHandler(
        IGitService git,
        ISymbolStore store,
        WorkspaceManager workspaceManager,
        IRepoRegistry repoRegistry,
        ILogger<RepoStatusHandler> logger)
    {
        _git = git;
        _store = store;
        _workspaceManager = workspaceManager;
        _repoRegistry = repoRegistry;
        _logger = logger;
    }

    /// <summary>Registers the <c>repo.status</c> tool into the ToolRegistry.</summary>
    public void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition(
            "repo.status",
            "Get the current Git state of a repository and whether a baseline index exists.",
            new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["required"] = new System.Text.Json.Nodes.JsonArray(),
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["repo_path"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute path to the repository root",
                    },
                },
            },
            HandleAsync));
    }

    internal async Task<ToolCallResult> HandleAsync(JsonObject? args, CancellationToken ct)
    {
        var (repoPath, repoErr) = HandlerHelpers.ResolveRepoPath(args, _repoRegistry);
        if (repoErr is { } re) return re;

        try
        {
            var repoId = await _git.GetRepoIdentityAsync(repoPath!, ct).ConfigureAwait(false);
            var commitSha = await _git.GetCurrentCommitAsync(repoPath!, ct).ConfigureAwait(false);
            var branch = await _git.GetCurrentBranchAsync(repoPath!, ct).ConfigureAwait(false);
            var isClean = await _git.IsCleanAsync(repoPath!, ct).ConfigureAwait(false);
            var hasIndex = await _store.BaselineExistsAsync(repoId, commitSha, ct).ConfigureAwait(false);
            var workspaces = await _workspaceManager.ListWorkspacesAsync(repoId, ct).ConfigureAwait(false);

            var response = new RepoStatusResponse(
                RepoId: repoId,
                CurrentCommitSha: commitSha,
                BranchName: branch,
                IsClean: isClean,
                BaselineIndexExists: hasIndex,
                Workspaces: workspaces);

            _logger.LogInformation(
                "repo.status {RepoId}: branch={Branch} sha={Sha} clean={Clean} indexed={Indexed}",
                repoId.Value, branch, commitSha.Value[..8], isClean, hasIndex);

            return new ToolCallResult(JsonSerializer.Serialize(response, CodeMapJsonOptions.Default));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "repo.status failed for {RepoPath}", repoPath);
            return Error($"Failed to get repo status: {ex.Message}");
        }
    }

    private static ToolCallResult Error(string message) =>
        new(JsonSerializer.Serialize(
            new { code = "INVALID_ARGUMENT", message },
            CodeMapJsonOptions.Default),
            IsError: true);

    // ── Response type ──────────────────────────────────────────────────────────

    private record RepoStatusResponse(
        RepoId RepoId,
        CommitSha CurrentCommitSha,
        string BranchName,
        bool IsClean,
        bool BaselineIndexExists,
        IReadOnlyList<WorkspaceSummary> Workspaces);
}
