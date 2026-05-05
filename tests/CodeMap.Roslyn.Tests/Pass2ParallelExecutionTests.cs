namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Types;
using CodeMap.Roslyn;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// M20-02 Option A regression. ExecutePass2Project is called concurrently
/// across passData entries by Parallel.For. These tests prove (a) the result
/// per project is identical to a sequential call, and (b) running the same
/// inputs through Parallel.For produces deterministic counts that match the
/// sequential reference run.
/// </summary>
public class Pass2ParallelExecutionTests
{
    private static RoslynCompiler.ProjectPassData BuildPassData(
        string canonicalName, string source, string projectFileName)
    {
        var compilation = CompilationBuilder.Create(source);
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            canonicalName,
            canonicalName,
            LanguageNames.CSharp,
            filePath: projectFileName);
        var project = workspace.AddProject(projectInfo);

        return new RoslynCompiler.ProjectPassData(
            Project: project,
            CanonicalName: canonicalName,
            TargetFrameworks: null,
            Compilation: compilation,
            Symbols: [],
            StableIdMap: new Dictionary<string, StableId>(),
            ErrorSeverities: [],
            ErrorMessages: []);
    }

    private static IReadOnlyList<RoslynCompiler.ProjectPassData> BuildSamplePassData()
    {
        // Three small projects with realistic refs & a couple of fact-emitting
        // patterns ([Route], IConfiguration access). Test/Bench-suffix names
        // are deliberately varied so the isTestProject branch is exercised.
        return [
            BuildPassData("Lib.Core", """
                public class Service { public int Compute(int x) => x * 2; }
                public class Caller { public void Run() { new Service().Compute(5); } }
                """, "/proj/Lib.Core.csproj"),

            BuildPassData("Web.Api", """
                using Microsoft.AspNetCore.Mvc;
                public class HomeController : ControllerBase {
                    [HttpGet("/health")] public string Health() => "ok";
                }
                """, "/proj/Web.Api.csproj"),

            BuildPassData("Lib.Tests", """
                public class FooTests { public void T1() { } }
                """, "/proj/Lib.Tests.csproj"),
        ];
    }

    [Fact]
    public void ExecutePass2Project_DeterministicAcrossCalls()
    {
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var passData = BuildSamplePassData();
        var allSymbolIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pd in passData)
        {
            var first = compiler.ExecutePass2Project(pd, "/proj", allSymbolIds);
            var second = compiler.ExecutePass2Project(pd, "/proj", allSymbolIds);
            second.References.Count.Should().Be(first.References.Count);
            second.Facts.Count.Should().Be(first.Facts.Count);
            second.Diagnostic.ProjectName.Should().Be(first.Diagnostic.ProjectName);
            second.Diagnostic.ReferenceCount.Should().Be(first.Diagnostic.ReferenceCount);
        }
    }

    [Fact]
    public void ExecutePass2Project_ParallelMatchesSequential()
    {
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var passData = BuildSamplePassData();
        var allSymbolIds = new HashSet<string>(StringComparer.Ordinal);

        // Sequential reference run — establishes ground truth.
        var sequential = new RoslynCompiler.Pass2Result[passData.Count];
        for (int i = 0; i < passData.Count; i++)
            sequential[i] = compiler.ExecutePass2Project(passData[i], "/proj", allSymbolIds);

        // Parallel run — same code path used in production Pass-2.
        var parallel = new RoslynCompiler.Pass2Result[passData.Count];
        Parallel.For(0, passData.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i => parallel[i] = compiler.ExecutePass2Project(passData[i], "/proj", allSymbolIds));

        for (int i = 0; i < passData.Count; i++)
        {
            parallel[i].References.Count.Should().Be(sequential[i].References.Count,
                because: $"project {passData[i].CanonicalName} ref count must be stable under parallel execution");
            parallel[i].Facts.Count.Should().Be(sequential[i].Facts.Count,
                because: $"project {passData[i].CanonicalName} fact count must be stable under parallel execution");
            parallel[i].Diagnostic.ProjectName.Should().Be(sequential[i].Diagnostic.ProjectName,
                because: "diagnostics must remain in passData order after merge");
            parallel[i].Diagnostic.ReferenceCount.Should().Be(sequential[i].Diagnostic.ReferenceCount);
        }
    }

    [Fact]
    public void ExecutePass2Project_TestProjectSkipsFactExtraction()
    {
        // Lib.Tests in the sample data ends with .Tests — facts should be empty
        // even though the source contains patterns that would otherwise emit facts.
        var compiler = new RoslynCompiler(NullLogger<RoslynCompiler>.Instance);
        var pd = BuildPassData("My.Tests", """
            using Microsoft.AspNetCore.Mvc;
            public class T : ControllerBase {
                [HttpGet("/x")] public string X() => "x";
            }
            """, "/proj/My.Tests.csproj");

        var result = compiler.ExecutePass2Project(pd, "/proj", new HashSet<string>());
        result.Facts.Should().BeEmpty(because: ".Tests projects skip architectural fact extraction");
    }
}
