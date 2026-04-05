// F# Support Spike — Tests two approaches:
// Approach A: MSBuildWorkspace (does it load .fsproj at all?)
// Approach B: FSharp.Compiler.Service directly (FSharpChecker)
//
// Key questions:
// 1. Can we get ISymbol-compatible objects from F# code?
// 2. Can we get syntax trees we can walk?
// 3. Can we resolve symbol references from syntax nodes?
// 4. What are the syntax node types (namespace)?

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Text;
using FSharp.Compiler.Symbols;

class Program
{
    static async Task Main(string[] args)
    {
        MSBuildLocator.RegisterDefaults();

        var spikeDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        var solutionPath = Path.Combine(spikeDir, "FSharpSpike.sln");
        var fsprojPath = Path.Combine(spikeDir, "SampleFSharp", "SampleFSharp.fsproj");
        var fsFile = Path.Combine(spikeDir, "SampleFSharp", "Library.fs");

        if (!File.Exists(solutionPath))
        {
            spikeDir = Environment.CurrentDirectory;
            solutionPath = Path.Combine(spikeDir, "FSharpSpike.sln");
            fsprojPath = Path.Combine(spikeDir, "SampleFSharp", "SampleFSharp.fsproj");
            fsFile = Path.Combine(spikeDir, "SampleFSharp", "Library.fs");
        }

        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║     F# Support Spike — CodeMap M15      ║");
        Console.WriteLine("╚══════════════════════════════════════════╝");
        Console.WriteLine();

        // ══════════════════════════════════════════
        // APPROACH A: MSBuildWorkspace
        // ══════════════════════════════════════════
        Console.WriteLine("═══ Approach A: MSBuildWorkspace ═══");
        Console.WriteLine($"Solution: {solutionPath}");
        Console.WriteLine();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (_, e) =>
                Console.WriteLine($"  [WARN] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

            var solution = await workspace.OpenSolutionAsync(solutionPath);
            Console.WriteLine($"  Projects loaded: {solution.Projects.Count()}");

            foreach (var project in solution.Projects)
            {
                Console.WriteLine($"  Project: {project.Name} (Language={project.Language})");
                var compilation = await project.GetCompilationAsync();
                Console.WriteLine($"  Compilation: {(compilation != null ? $"OK ({compilation.GetType().Name})" : "NULL")}");
            }

            if (!solution.Projects.Any())
            {
                Console.WriteLine("  RESULT: MSBuildWorkspace cannot load .fsproj — no F# language service registered.");
                Console.WriteLine("  This is expected. Roslyn has no built-in Microsoft.CodeAnalysis.FSharp.Workspaces package.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();

        // ══════════════════════════════════════════
        // APPROACH B: FSharp.Compiler.Service
        // ══════════════════════════════════════════
        Console.WriteLine("═══ Approach B: FSharp.Compiler.Service ═══");
        Console.WriteLine($"F# file: {fsFile}");
        Console.WriteLine();

        try
        {
            var checker = FSharpChecker.Create(
                projectCacheSize: null,
                keepAssemblyContents: FSharpOption<bool>.Some(true),
                keepAllBackgroundResolutions: null,
                legacyReferenceResolver: null,
                tryGetMetadataSnapshot: null,
                suggestNamesForErrors: null,
                keepAllBackgroundSymbolUses: null,
                enableBackgroundItemKeyStoreAndSemanticClassification: null,
                enablePartialTypeChecking: null,
                parallelReferenceResolution: null,
                captureIdentifiersWhenParsing: null,
                documentSource: null,
                useSyntaxTreeCache: null,
                useTransparentCompiler: null);

            var sourceText = SourceText.ofString(File.ReadAllText(fsFile));

            // Get framework references
            var dotnetDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var refs = new[]
            {
                Path.Combine(dotnetDir, "System.Runtime.dll"),
                Path.Combine(dotnetDir, "System.Console.dll"),
                Path.Combine(dotnetDir, "System.Private.CoreLib.dll"),
                Path.Combine(dotnetDir, "netstandard.dll"),
                typeof(object).Assembly.Location,
            }
            .Where(File.Exists)
            .Distinct()
            .Select(r => $"-r:{r}")
            .ToArray();

            var fsharpCoreRef = typeof(FSharpOption<int>).Assembly.Location;

            var options = new[] { "fsc.exe", fsFile, $"-r:{fsharpCoreRef}" }
                .Concat(refs)
                .ToArray();

            var projOptions = checker.GetProjectOptionsFromCommandLineArgs(
                "SampleFSharp", options, null, null, null);

            // Parse
            Console.WriteLine("  Parsing...");
            var parsingOptions = checker.GetParsingOptionsFromProjectOptions(projOptions);
            var parseResult = FSharpAsync.RunSynchronously(
                checker.ParseFile(fsFile, sourceText, parsingOptions.Item1, null, null),
                timeout: null, cancellationToken: null);

            Console.WriteLine($"  Parse tree: {(parseResult.ParseTree != null ? "OK" : "NULL")}");
            Console.WriteLine($"  Parse errors: {parseResult.Diagnostics.Length}");

            // Type-check
            Console.WriteLine("  Type-checking...");
            var checkAnswer = FSharpAsync.RunSynchronously(
                checker.CheckFileInProject(parseResult, fsFile, 0, sourceText, projOptions, null),
                timeout: null, cancellationToken: null);

            if (checkAnswer.IsSucceeded)
            {
                var checkResults = ((FSharpCheckFileAnswer.Succeeded)checkAnswer).Item;
                Console.WriteLine($"  Check: SUCCEEDED");
                Console.WriteLine($"  Check errors: {checkResults.Diagnostics.Length}");

                foreach (var diag in checkResults.Diagnostics.Take(5))
                    Console.WriteLine($"    [{diag.Severity}] {diag.Message}");

                // Walk symbols from PartialAssemblySignature
                var sig = checkResults.PartialAssemblySignature;
                Console.WriteLine($"  Entities:");

                foreach (var entity in sig.Entities)
                {
                    WalkEntity(entity, "    ");
                }

                // Try GetSymbolUseAtLocation or GetAllUsesOfAllSymbolsInFile
                Console.WriteLine();
                Console.WriteLine("  --- Symbol uses in file ---");
                var allUses = checkResults.GetAllUsesOfAllSymbolsInFile(null).ToArray();

                Console.WriteLine($"  Total symbol uses: {allUses.Length}");
                for (int i = 0; i < Math.Min(20, allUses.Length); i++)
                {
                    var use = allUses[i];
                    var sym = use.Symbol;
                    var range = use.Range;
                    Console.WriteLine($"    [{sym.GetType().Name}] {sym.DisplayName}  " +
                        $"L{range.StartLine}:{range.StartColumn}-L{range.EndLine}:{range.EndColumn}  " +
                        $"IsFromDefinition={use.IsFromDefinition}");
                }
                if (allUses.Length > 20)
                    Console.WriteLine($"    ... and {allUses.Length - 20} more");

                // TEST: Can we get doc-comment ID equivalents?
                Console.WriteLine();
                Console.WriteLine("  --- FQN / XmlDoc IDs ---");
                foreach (var entity in sig.Entities)
                {
                    PrintEntityIds(entity, "    ");
                }
            }
            else
            {
                Console.WriteLine($"  Check: FAILED (aborted)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}");
        }

        Console.WriteLine();
        Console.WriteLine("═══ Spike complete ═══");
    }

    static void WalkEntity(FSharpEntity entity, string indent)
    {
        var kind = entity.IsFSharpModule ? "Module"
            : entity.IsFSharpUnion ? "Union"
            : entity.IsFSharpRecord ? "Record"
            : entity.IsInterface ? "Interface"
            : entity.IsClass ? "Class"
            : entity.IsEnum ? "Enum"
            : entity.IsValueType ? "Struct"
            : "Type";

        Console.WriteLine($"{indent}[{kind}] {entity.FullName}  (AccessPath={entity.AccessPath})");

        // Members
        foreach (var m in entity.MembersFunctionsAndValues)
        {
            var memberKind = m.IsProperty ? "Property"
                : m.IsEvent ? "Event"
                : "Function";
            Console.WriteLine($"{indent}  [{memberKind}] {m.DisplayName} : {m.FullType}");
        }

        // Nested entities
        foreach (var nested in entity.NestedEntities)
        {
            WalkEntity(nested, indent + "  ");
        }
    }

    static void PrintEntityIds(FSharpEntity entity, string indent)
    {
        var xmlDocId = entity.XmlDocSig;
        Console.WriteLine($"{indent}{entity.FullName}  XmlDocSig={xmlDocId}");

        foreach (var m in entity.MembersFunctionsAndValues)
        {
            Console.WriteLine($"{indent}  {m.DisplayName}  XmlDocSig={m.XmlDocSig}");
        }

        foreach (var nested in entity.NestedEntities)
        {
            PrintEntityIds(nested, indent + "  ");
        }
    }
}
