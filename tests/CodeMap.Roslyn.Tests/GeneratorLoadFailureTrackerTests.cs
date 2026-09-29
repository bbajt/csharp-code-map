namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Enums;
using ProjectDiagnostic = CodeMap.Core.Models.ProjectDiagnostic;
using FluentAssertions;
using Microsoft.CodeAnalysis;

/// <summary>
/// <see cref="GeneratorLoadFailureTracker"/> and <see cref="RoslynCompiler.ComputeSemanticLevel"/>
/// (F10, PHASE-21-05 T01, ADR-046). The real failure (SDK Razor generator built for a newer Roslyn)
/// is covered end-to-end by <c>GeneratorLoadFailureTests</c> in the Linux rig.
/// </summary>
public sealed class GeneratorLoadFailureTrackerTests
{
    private static string ThisAssembly => typeof(FixtureGenerator).Assembly.Location;
    private static string CoreAssembly => typeof(ProjectDiagnostic).Assembly.Location;

    [Fact]
    public void IsGeneratorAssembly_AssemblyWithGeneratorAttribute_True() =>
        GeneratorLoadFailureTracker.IsGeneratorAssembly(ThisAssembly).Should().BeTrue();

    [Fact]
    public void IsGeneratorAssembly_PlainAssembly_False() =>
        GeneratorLoadFailureTracker.IsGeneratorAssembly(CoreAssembly).Should().BeFalse();

    [Fact]
    public void IsGeneratorAssembly_MissingFile_False() =>
        GeneratorLoadFailureTracker.IsGeneratorAssembly(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"))
            .Should().BeFalse();

    [Fact]
    public void IsGeneratorAssembly_CorruptFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codemap-corrupt-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "not a PE file");
        try
        {
            GeneratorLoadFailureTracker.IsGeneratorAssembly(path).Should().BeFalse();
            GeneratorLoadFailureTracker.ReferencedCompilerVersion(path).Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReferencedCompilerVersion_ReadsMicrosoftCodeAnalysisReference()
    {
        GeneratorLoadFailureTracker.ReferencedCompilerVersion(ThisAssembly)
            .Should().Be(GeneratorLoadFailureTracker.RunningCompilerVersion);
        GeneratorLoadFailureTracker.ReferencedCompilerVersion(CoreAssembly).Should().BeNull("Core has zero dependencies");
    }

    [Fact]
    public void Format_NewerCompiler_NamesBothVersions()
    {
        var running = GeneratorLoadFailureTracker.RunningCompilerVersion;
        var newer = new Version(running.Major, running.Minor + 6, 0, 0);

        GeneratorLoadFailureTracker.Format("Microsoft.CodeAnalysis.Razor.Compiler", newer)
            .Should().Be($"Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn {newer}, CodeMap runs {running})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.0.0")]
    public void Format_NotNewer_IsUnableToLoad(string? referenced) =>
        GeneratorLoadFailureTracker.Format("Some.Generator", referenced is null ? null : Version.Parse(referenced))
            .Should().StartWith("Some.Generator: UnableToLoadGenerator");

    [Fact]
    public void ComputeSemanticLevel_AllCompiledNoGeneratorFailures_Full() =>
        RoslynCompiler.ComputeSemanticLevel([Project("A"), Project("B")]).Should().Be(SemanticLevel.Full);

    [Fact]
    public void ComputeSemanticLevel_CompiledButGeneratorFailed_Partial() =>
        RoslynCompiler.ComputeSemanticLevel([Project("A"), Project("B", generatorFailures: ["X: ReferencesNewerCompiler"])])
            .Should().Be(SemanticLevel.Partial);

    [Fact]
    public void ComputeSemanticLevel_OnlyProjectHasGeneratorFailure_Partial() =>
        RoslynCompiler.ComputeSemanticLevel([Project("A", generatorFailures: ["X: ReferencesNewerCompiler"])])
            .Should().Be(SemanticLevel.Partial, "compiled without its generated code is not full semantics");

    [Fact]
    public void ComputeSemanticLevel_NoneCompiled_SyntaxOnly() =>
        RoslynCompiler.ComputeSemanticLevel([Project("A", compiled: false)]).Should().Be(SemanticLevel.SyntaxOnly);

    [Fact]
    public void ComputeSemanticLevel_SomeNotCompiled_Partial() =>
        RoslynCompiler.ComputeSemanticLevel([Project("A"), Project("B", compiled: false)]).Should().Be(SemanticLevel.Partial);

    private static ProjectDiagnostic Project(string name, bool compiled = true, IReadOnlyList<string>? generatorFailures = null) =>
        new(name, compiled, SymbolCount: 1, ReferenceCount: 0, GeneratorLoadFailures: generatorFailures);

    // Fixture only: it's never loaded as a generator by any compiler, it just has to carry
    // [Generator] in metadata. The extension-authoring rules below target shipped generators.
#pragma warning disable RS1036, RS1038, RS1041
    /// <summary>Fixture: makes this test assembly a "generator assembly" for the metadata check.</summary>
    [Generator]
    internal sealed class FixtureGenerator : IIncrementalGenerator
    {
        /// <inheritdoc/>
        public void Initialize(IncrementalGeneratorInitializationContext context) { }
    }
#pragma warning restore RS1036, RS1038, RS1041
}
