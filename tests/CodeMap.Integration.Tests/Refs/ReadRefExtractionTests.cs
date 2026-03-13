namespace CodeMap.Integration.Tests.Refs;

using CodeMap.Core.Enums;
using CodeMap.Roslyn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Verifies that RefKind.Read references are extracted and queryable
/// in the full Roslyn indexing pipeline (ADR-007 completion).
/// Uses SampleSolution which contains property and field reads.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadRefExtractionTests
{
    private static string SampleSolutionPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata", "SampleSolution", "SampleSolution.sln"));

    private static RoslynCompiler CreateCompiler() =>
        new(NullLogger<RoslynCompiler>.Instance);

    [Fact]
    public async Task E2E_ReadRef_PropertyAccess_Extracted()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // SampleSolution has property reads (e.g., order.Id, Status == ...)
        result.References.Should().Contain(r => r.Kind == RefKind.Read);
    }

    [Fact]
    public async Task E2E_ReadRef_FieldAccess_Extracted()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // Calculator.cs has field reads: a._memory in operator+
        result.References.Should().Contain(r =>
            r.Kind == RefKind.Read && r.ToSymbol.Value.Contains("_memory"));
    }

    [Fact]
    public async Task E2E_ReadRef_NoFalsePositives_LocalVarsExcluded()
    {
        MsBuildInitializer.EnsureRegistered();
        var compiler = CreateCompiler();

        var result = await compiler.CompileAndExtractAsync(SampleSolutionPath);

        // All Read refs should reference IPropertySymbol/IFieldSymbol/IEventSymbol paths
        // (the symbol IDs follow "P:", "F:", "E:" prefixes from documentation comment IDs)
        var readRefs = result.References.Where(r => r.Kind == RefKind.Read).ToList();
        readRefs.Should().NotBeEmpty();

        // None should reference local variable patterns (local vars don't have doc IDs)
        // All valid Read refs have proper documentation comment IDs
        readRefs.Should().AllSatisfy(r =>
            r.ToSymbol.Value.Should().MatchRegex(@"^[PFETMC]:|^#"));
    }
}
