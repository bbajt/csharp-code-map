namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Compiles a .NET solution and extracts symbols + references.
/// Implementation: CodeMap.Roslyn.
/// </summary>
public interface IRoslynCompiler
{
    /// <summary>
    /// Loads and compiles a .NET solution. Returns extracted symbols and references.
    /// If compilation partially fails, returns available data with Confidence.Low
    /// for affected projects.
    /// </summary>
    Task<CompilationResult> CompileAndExtractAsync(
        string solutionPath,
        CancellationToken ct = default);

    /// <summary>
    /// Incrementally recompiles only affected projects for the given changed files.
    /// Returns only the changed/new symbols and references.
    /// </summary>
    Task<CompilationResult> IncrementalExtractAsync(
        string solutionPath,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default);
}

/// <summary>
/// Holds the full extraction output from a compilation.
/// </summary>
public record CompilationResult(
    IReadOnlyList<SymbolCard> Symbols,
    IReadOnlyList<ExtractedReference> References,
    IReadOnlyList<ExtractedFile> Files,
    IndexStats Stats,
    IReadOnlyList<ExtractedTypeRelation>? TypeRelations = null,
    IReadOnlyList<ExtractedFact>? Facts = null,
    string? DllFingerprint = null
);

/// <summary>
/// A reference between two symbols, extracted from Roslyn analysis.
/// Resolved edges have exact ToSymbol identity; unresolved edges (from syntactic
/// extraction) have ToSymbol = SymbolId.Empty and ToName/ToContainerHint populated.
/// </summary>
public record ExtractedReference(
    SymbolId FromSymbol,
    SymbolId ToSymbol,
    RefKind Kind,
    FilePath FilePath,
    int LineStart,
    int LineEnd,
    Enums.ResolutionState ResolutionState = Enums.ResolutionState.Resolved,
    string? ToName = null,
    string? ToContainerHint = null,
    Types.StableId? StableFromId = null,
    Types.StableId? StableToId = null,
    bool IsDecompiled = false
);

/// <summary>
/// Metadata about an indexed file.
/// </summary>
public record ExtractedFile(
    string FileId,
    FilePath Path,
    string Sha256Hash,
    string? ProjectName
);
