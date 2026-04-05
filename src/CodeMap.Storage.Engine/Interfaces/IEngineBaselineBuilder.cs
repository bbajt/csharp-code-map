namespace CodeMap.Storage.Engine;

/// <summary>Builds an immutable baseline snapshot from Roslyn extraction output. Single-use. Not thread-safe.</summary>
internal interface IEngineBaselineBuilder : IDisposable
{
    /// <summary>
    /// Builds the baseline in a temp directory, then atomically renames it to the final location.
    /// On failure cleans up the temp directory and returns <see cref="BaselineBuildResult"/> with Success=false.
    /// </summary>
    Task<BaselineBuildResult> BuildAsync(BaselineBuildInput input, CancellationToken ct);
}
