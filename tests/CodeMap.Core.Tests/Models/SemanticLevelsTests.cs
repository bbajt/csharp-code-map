namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using FluentAssertions;
using Xunit;

public sealed class SemanticLevelsTests
{
    private const string GeneratorFailure =
        "Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)";

    // Each project is encoded as: "c" = compiled, "u" = not compiled, "g" = compiled with a generator failure.
    [Theory]
    [InlineData("", SemanticLevel.Full)]
    [InlineData("c", SemanticLevel.Full)]
    [InlineData("c,c", SemanticLevel.Full)]
    [InlineData("c,g", SemanticLevel.Partial)]
    [InlineData("g", SemanticLevel.Partial)]
    [InlineData("c,u", SemanticLevel.Partial)]
    [InlineData("u", SemanticLevel.SyntaxOnly)]
    [InlineData("u,u", SemanticLevel.SyntaxOnly)]
    public void Compute_ProjectOutcomes_ReturnsLevel(string projects, SemanticLevel expected)
    {
        var diagnostics = projects.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((kind, i) => kind switch
            {
                "c" => new ProjectDiagnostic($"P{i}", true, 1, 0),
                "u" => new ProjectDiagnostic($"P{i}", false, 1, 0),
                "g" => new ProjectDiagnostic($"P{i}", true, 1, 0, GeneratorLoadFailures: [GeneratorFailure]),
                _ => throw new ArgumentOutOfRangeException(nameof(projects), kind, null),
            })
            .ToList();

        SemanticLevels.Compute(diagnostics).Should().Be(expected);
    }

    [Fact]
    public void IsComplete_CompiledWithEmptyGeneratorFailureList_True()
    {
        SemanticLevels.IsComplete(new ProjectDiagnostic("P", true, 1, 0, GeneratorLoadFailures: []))
            .Should().BeTrue();
    }

    [Fact]
    public void IsComplete_CompiledWithCompileErrorsOnly_True()
    {
        // Compile errors alone keep today's behaviour (they lower confidence, not the level); the
        // missing-restore case that changes this is PHASE-21-07 T02.
        SemanticLevels.IsComplete(new ProjectDiagnostic("P", true, 1, 0, Errors: ["CS0103"]))
            .Should().BeTrue();
    }

    [Fact]
    public void IsComplete_MissingRestoreOutputWithErrors_False()
    {
        SemanticLevels.IsComplete(new ProjectDiagnostic("P", true, 1, 0,
            Errors: ["CS0400: The type or namespace name 'System' could not be found"],
            MissingRestoreOutput: "P/obj/project.assets.json")).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_MissingRestoreOutputWithoutErrors_True()
    {
        // A project without packages whose framework ships with the SDK compiles completely unrestored
        // (probed: net10.0 on SDK 10.0.204, 0 errors): reported, not downgraded.
        SemanticLevels.IsComplete(new ProjectDiagnostic("P", true, 1, 0,
            MissingRestoreOutput: "P/obj/project.assets.json")).Should().BeTrue();
    }

    [Fact]
    public void Compute_OneProjectUnrestoredWithErrors_Partial()
    {
        SemanticLevels.Compute(
        [
            new ProjectDiagnostic("A", true, 1, 0),
            new ProjectDiagnostic("B", true, 1, 0, Errors: ["CS0246"], MissingRestoreOutput: "B/obj/project.assets.json"),
        ]).Should().Be(SemanticLevel.Partial);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]   // generator failure
    [InlineData(false, true, true, true)]    // unrestored + errors
    [InlineData(false, true, false, false)]  // unrestored, compiles fine
    [InlineData(false, false, true, false)]  // real compile errors only: inherent to the commit
    public void IsEnvironmentDegraded_Cases(bool generatorFailure, bool missingRestore, bool errors, bool expected)
    {
        var d = new ProjectDiagnostic("P", true, 1, 0,
            Errors: errors ? ["CS0246"] : null,
            GeneratorLoadFailures: generatorFailure ? [GeneratorFailure] : null,
            MissingRestoreOutput: missingRestore ? "P/obj/project.assets.json" : null);

        SemanticLevels.IsEnvironmentDegraded(d).Should().Be(expected);
    }

    [Fact]
    public void IsComplete_NotCompiled_False()
    {
        SemanticLevels.IsComplete(new ProjectDiagnostic("P", false, 1, 0)).Should().BeFalse();
    }
}
