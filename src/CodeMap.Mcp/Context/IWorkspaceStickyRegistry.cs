namespace CodeMap.Mcp.Context;

/// <summary>
/// Tracks the sticky default workspace per repo. When a handler call omits
/// <c>workspace_id</c>, the sticky value is used so agents don't have to pass the
/// same workspace on every call. Explicit <c>workspace_id</c> always wins for a
/// single call; stickiness is overridden, not replaced.
/// </summary>
/// <remarks>
/// State is in-memory and per-process. <c>workspace.create</c> sets the sticky value;
/// <c>workspace.delete</c> clears it if the deleted workspace was sticky. Agents that
/// juggle multiple workspaces in one session should keep passing <c>workspace_id</c>
/// explicitly — defaulting only removes ceremony in the single-workspace case.
/// </remarks>
public interface IWorkspaceStickyRegistry
{
    /// <summary>Records <paramref name="workspaceId"/> as the sticky default for <paramref name="repoPath"/>.</summary>
    void Set(string repoPath, string workspaceId);

    /// <summary>
    /// Clears the sticky default for <paramref name="repoPath"/> if and only if it currently
    /// equals <paramref name="workspaceId"/>. No-op otherwise — prevents <c>workspace.delete</c>
    /// of a non-sticky workspace from clearing an unrelated default.
    /// </summary>
    void Clear(string repoPath, string workspaceId);

    /// <summary>Returns the sticky workspace for <paramref name="repoPath"/>, or null if none.</summary>
    string? Get(string repoPath);
}
