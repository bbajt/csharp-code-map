namespace CodeMap.Roslyn.FSharp;

using global::FSharp.Compiler.CodeAnalysis;

/// <summary>
/// Holds FCS parse + type-check results for a single F# source file.
/// </summary>
internal sealed record FSharpFileAnalysis(
    string FilePath,
    FSharpCheckFileResults? CheckResults,
    FSharpParseFileResults ParseResults);
