namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Types;

/// <summary>
/// Resolves symbols from referenced DLL metadata on demand (lazy on-miss resolution).
/// </summary>
/// <remarks>
/// Level 1 (PHASE-12-01): Roslyn <c>INamedTypeSymbol</c> walk — signatures, XML doc,
/// type hierarchy. No method bodies. No new NuGet dependency.
/// Level 2 (PHASE-12-02): <c>ICSharpCode.Decompiler</c> — reconstructed C# source.
/// The interface is stable across both levels.
/// </remarks>
public interface IMetadataResolver
{
    /// <summary>
    /// Attempts to extract metadata stubs for the type containing
    /// <paramref name="symbolId"/> from any loaded <c>MetadataReference</c>.
    /// Writes the stubs to the baseline DB via <see cref="ISymbolStore"/>.
    /// </summary>
    /// <param name="symbolId">The FQN symbol ID that was not found in the DB.</param>
    /// <param name="repoId">Baseline repo identifier.</param>
    /// <param name="commitSha">Baseline commit SHA.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Number of symbols inserted (0 if the FQN is not resolvable from any
    /// loaded compilation, the symbol comes from a source file, the containing
    /// type's assembly is on the exclusion list, or all members were already
    /// present via INSERT OR IGNORE).
    /// </returns>
    Task<int> TryResolveTypeAsync(
        SymbolId symbolId,
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to decompile the type containing <paramref name="symbolId"/>
    /// to C# source using ICSharpCode.Decompiler.
    /// </summary>
    /// <returns>
    /// The virtual file path if decompilation succeeds, <c>null</c> if
    /// decompilation is unavailable or the type cannot be resolved.
    /// </returns>
    Task<string?> TryDecompileTypeAsync(
        SymbolId symbolId,
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default);
}
