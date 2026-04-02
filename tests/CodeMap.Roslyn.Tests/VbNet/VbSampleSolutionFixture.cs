namespace CodeMap.Roslyn.Tests.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Shared xUnit fixture that compiles SampleVbSolution once using real Roslyn.
/// Exposes extracted Symbols, Refs, and SemanticLevel for VB.NET extraction tests.
/// Shared across test classes via [Collection("VbSampleSolution")].
/// </summary>
[Trait("Category", "Integration")]
public sealed class VbSampleSolutionFixture : IAsyncLifetime
{
    private static string SampleVbSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleVbSolution", "SampleVbSolution.sln"));

    public IReadOnlyList<SymbolCard> Symbols { get; private set; } = [];
    public IReadOnlyList<ExtractedReference> Refs { get; private set; } = [];
    public SemanticLevel SemanticLevel { get; private set; } = SemanticLevel.Full;

    public async ValueTask InitializeAsync()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var result = await compiler.CompileAndExtractAsync(SampleVbSolutionPath);
        Symbols = result.Symbols;
        Refs = result.References;
        SemanticLevel = result.Stats.SemanticLevel;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
