namespace CodeMap.TestUtilities.Fixtures;

using CodeMap.Core.Types;

/// <summary>
/// Reusable constant values for tests. Use these instead of magic strings.
/// </summary>
public static class TestConstants
{
    public static readonly RepoId SampleRepoId = RepoId.From("test-repo-001");
    public static readonly CommitSha SampleCommitSha =
        CommitSha.From("aabbccddee00112233445566778899aabbccddee");
    public static readonly WorkspaceId SampleWorkspaceId = WorkspaceId.From("agent-workspace-1");
    public static readonly FilePath SampleFilePath = FilePath.From("src/Services/OrderService.cs");
    public static readonly SymbolId SampleSymbolId =
        SymbolId.From("SampleApp.Services.OrderService.SubmitAsync");
}
