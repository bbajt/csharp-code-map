namespace CodeMap.Integration.Tests.Regression.Razor;

using CodeMap.Core.Enums;
using CodeMap.Roslyn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// F10 invariant (PHASE-21-05 T01, ADR-046): when an SDK's source generator can't be loaded by
/// CodeMap's bundled Roslyn (<c>ReferencesNewerCompiler</c>), the generated code — Razor components
/// here — is missing from the index. That must never be silent: either the components are indexed,
/// or the failure is reported in <c>ProjectDiagnostic.GeneratorLoadFailures</c> and the run's
/// <see cref="SemanticLevel"/> is <c>Partial</c>. Holds on every SDK/Roslyn combination; fails only
/// on a silent loss (e.g. SDK 10.0.4xx + Roslyn 5.3 before T01, reproduced in the Linux rig).
/// </summary>
[Trait("Category", "Integration")]
public class GeneratorLoadFailureTests
{
    private static string BlazorSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleBlazorSolution", "SampleBlazorSolution.slnx"));

    [Fact]
    public async Task Blazor_ComponentsIndexed_OrGeneratorFailureReported()
    {
        var result = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(BlazorSolutionPath);

        var componentsIndexed = result.Symbols.Any(s =>
            s.FullyQualifiedName.EndsWith(".Counter", StringComparison.Ordinal) && s.Kind == SymbolKind.Class);
        if (componentsIndexed)
            return;

        var app = result.Stats.ProjectDiagnostics!.Single(d => d.ProjectName == "SampleBlazorApp");
        var because = $"Razor components are missing (symbols: {result.Symbols.Count}, " +
                      $"semantic_level: {result.Stats.SemanticLevel}) — the loss must be reported, not silent";
        app.GeneratorLoadFailures.Should().NotBeNullOrEmpty(because);
        app.GeneratorLoadFailures!.Should().Contain(f => f.StartsWith("Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.Ordinal));
        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Partial, because);
    }

    [Fact]
    public async Task Blazor_ComponentsIndexed_NoGeneratorFailuresReported()
    {
        // No false positives: when the Razor generator ran, nothing may be reported (the metadata check
        // must not flag loadable generators or plain analyzers).
        var result = await new RoslynCompiler(NullLogger<RoslynCompiler>.Instance)
            .CompileAndExtractAsync(BlazorSolutionPath);

        var componentsIndexed = result.Symbols.Any(s =>
            s.FullyQualifiedName.EndsWith(".Counter", StringComparison.Ordinal) && s.Kind == SymbolKind.Class);
        Assert.SkipUnless(componentsIndexed, "Razor generator not loadable on this SDK — covered by the invariant test");

        result.Stats.ProjectDiagnostics!.Should().OnlyContain(d => d.GeneratorLoadFailures == null);
        result.Stats.SemanticLevel.Should().Be(SemanticLevel.Full);
    }
}
