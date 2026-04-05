namespace CodeMap.Roslyn.FSharp;

using global::FSharp.Compiler.CodeAnalysis;
using global::FSharp.Compiler.Text;
using global::Microsoft.FSharp.Control;
using global::Microsoft.FSharp.Core;

/// <summary>
/// Analyzes F# source files using FSharp.Compiler.Service.
/// MSBuildWorkspace cannot load .fsproj — this is the F# alternative.
/// </summary>
internal static class FSharpProjectAnalyzer
{
    /// <summary>
    /// Analyzes all F# source files in a project.
    /// Uses FSharpChecker for parsing and type-checking.
    /// </summary>
    public static IReadOnlyList<FSharpFileAnalysis> AnalyzeProject(
        string fsprojPath,
        string solutionDir,
        CancellationToken ct = default)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath))!;

        // Collect .fs source files from the .fsproj (respects Compile Include order)
        var sourceFiles = GetSourceFiles(fsprojPath, projectDir);
        if (sourceFiles.Count == 0)
            return [];

        // Build FCS project options with assembly references
        var checker = CreateChecker();
        var references = ResolveReferences(fsprojPath);
        var projOptions = BuildProjectOptions(fsprojPath, sourceFiles, references, checker);

        // Parse and type-check each file
        var results = new List<FSharpFileAnalysis>(sourceFiles.Count);

        foreach (var fsFile in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();

            var sourceText = SourceText.ofString(File.ReadAllText(fsFile));
            var parsingOptions = checker.GetParsingOptionsFromProjectOptions(projOptions);

            var ctOption = FSharpOption<System.Threading.CancellationToken>.Some(ct);

            var parseResult = FSharpAsync.RunSynchronously(
                checker.ParseFile(fsFile, sourceText, parsingOptions.Item1, null, null),
                timeout: null, cancellationToken: ctOption);

            FSharpCheckFileResults? checkResults = null;
            try
            {
                var checkAnswer = FSharpAsync.RunSynchronously(
                    checker.CheckFileInProject(parseResult, fsFile, 0, sourceText, projOptions, null),
                    timeout: null, cancellationToken: ctOption);

                if (checkAnswer.IsSucceeded)
                    checkResults = ((FSharpCheckFileAnswer.Succeeded)checkAnswer).Item;
            }
            catch
            {
                // Type-check failed — keep parse results for syntactic fallback
            }

            results.Add(new FSharpFileAnalysis(fsFile, checkResults, parseResult));
        }

        return results;
    }

    /// <summary>
    /// Reads Compile Include items from .fsproj to get source file order.
    /// F# file order matters — files can only reference symbols from files listed before them.
    /// </summary>
    internal static IReadOnlyList<string> GetSourceFiles(string fsprojPath, string projectDir)
    {
        var files = new List<string>();
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(fsprojPath);
            var compileElements = xml.Descendants()
                .Where(e => e.Name.LocalName == "Compile")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null);

            foreach (var rel in compileElements)
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectDir, rel!));
                if (File.Exists(fullPath))
                    files.Add(fullPath);
            }
        }
        catch
        {
            // Fallback: glob .fs files (loses ordering)
            files.AddRange(Directory.GetFiles(projectDir, "*.fs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)));
        }
        return files;
    }

    private static FSharpChecker CreateChecker()
    {
        return FSharpChecker.Create(
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
    }

    private static FSharpProjectOptions BuildProjectOptions(
        string fsprojPath,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> references,
        FSharpChecker checker)
    {
        var args = new List<string> { "fsc.exe" };
        args.AddRange(sourceFiles);
        foreach (var r in references)
            args.Add($"-r:{r}");

        return checker.GetProjectOptionsFromCommandLineArgs(
            Path.GetFileNameWithoutExtension(fsprojPath),
            args.ToArray(), null, null, null);
    }

    /// <summary>
    /// Resolves assembly references for the project:
    /// 1. All framework assemblies from the runtime directory
    /// 2. FSharp.Core from the loaded assembly
    /// 3. ProjectReference output DLLs (if built)
    /// 4. PackageReference DLLs from NuGet cache (via project.assets.json)
    /// </summary>
    internal static IReadOnlyList<string> ResolveReferences(string fsprojPath)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Framework assemblies — targeted set that FCS needs
        var dotnetDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var essentialAssemblies = new[]
        {
            "System.Runtime.dll", "System.Private.CoreLib.dll", "netstandard.dll",
            "System.Console.dll", "System.Collections.dll", "System.Linq.dll",
            "System.Collections.Immutable.dll", "System.Text.RegularExpressions.dll",
            "System.IO.dll", "System.IO.FileSystem.dll", "System.Threading.dll",
            "System.Threading.Tasks.dll", "System.Diagnostics.Debug.dll",
            "System.Runtime.InteropServices.dll", "System.Runtime.Numerics.dll",
            "System.Numerics.Vectors.dll", "System.Memory.dll",
            "System.Reflection.dll", "System.Reflection.Metadata.dll",
            "System.Reflection.Emit.dll", "System.Reflection.Emit.ILGeneration.dll",
            "System.Reflection.Primitives.dll",
            "System.Text.Encoding.dll", "System.Text.Json.dll",
            "System.ComponentModel.dll", "System.ObjectModel.dll",
            "System.Buffers.dll", "System.Net.Primitives.dll",
        };

        foreach (var asm in essentialAssemblies)
        {
            var path = Path.Combine(dotnetDir, asm);
            if (File.Exists(path)) refs.Add(path);
        }

        var coreLib = typeof(object).Assembly.Location;
        refs.Add(coreLib);

        // 2. FSharp.Core from the loaded assembly
        var fsharpCore = typeof(FSharpOption<int>).Assembly.Location;
        if (File.Exists(fsharpCore))
            refs.Add(fsharpCore);

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath))!;

        // 3. ProjectReference output DLLs
        ResolveProjectReferences(fsprojPath, projectDir, refs);

        // 4. PackageReference DLLs from NuGet restore output
        ResolvePackageReferences(fsprojPath, projectDir, refs);

        return refs.ToList();
    }

    /// <summary>
    /// Parses ProjectReference items from .fsproj and resolves their output DLLs.
    /// </summary>
    private static void ResolveProjectReferences(string fsprojPath, string projectDir, HashSet<string> refs)
    {
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(fsprojPath);
            var projectRefs = xml.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null);

            foreach (var relPath in projectRefs)
            {
                // Skip build-only refs (ReferenceOutputAssembly=false)
                var element = xml.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ProjectReference" &&
                                         e.Attribute("Include")?.Value == relPath);
                var refOutput = element?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ReferenceOutputAssembly");
                if (refOutput?.Value.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                // Resolve MSBuild variables in path
                var resolvedPath = relPath!
                    .Replace("$(MSBuildThisFileDirectory)", projectDir.Replace('\\', '/') + "/")
                    .Replace("$(RepoRoot)", Path.GetDirectoryName(Path.GetDirectoryName(projectDir))?.Replace('\\', '/') + "/" ?? "");

                var refProjPath = Path.GetFullPath(Path.Combine(projectDir, resolvedPath));
                if (!File.Exists(refProjPath)) continue;

                var refProjDir = Path.GetDirectoryName(refProjPath)!;
                var refProjName = Path.GetFileNameWithoutExtension(refProjPath);

                // Look for output DLL in standard locations
                if (TryFindProjectOutputDll(refProjDir, refProjName, refs))
                    continue;

                // Also check artifacts/ layout (dotnet SDK artifacts output)
                // Walk up from project dir to find artifacts/bin/<ProjectName>/
                var searchDir = refProjDir;
                for (int depth = 0; depth < 5; depth++)
                {
                    searchDir = Path.GetDirectoryName(searchDir);
                    if (searchDir is null) break;
                    var artifactsDir = Path.Combine(searchDir, "artifacts", "bin", refProjName);
                    if (Directory.Exists(artifactsDir))
                    {
                        TryFindProjectOutputDll(artifactsDir, refProjName, refs);
                        break;
                    }
                }
            }
        }
        catch { /* Best-effort — skip on parse errors */ }
    }

    /// <summary>
    /// Searches bin/Debug and bin/Release under a directory for a project's output DLL.
    /// Returns true if found.
    /// </summary>
    private static bool TryFindProjectOutputDll(string baseDir, string projectName, HashSet<string> refs)
    {
        foreach (var config in new[] { "Debug", "Release" })
        {
            var binDir = Path.Combine(baseDir, config);
            if (!Directory.Exists(binDir))
            {
                // Also check direct bin/<config> under baseDir
                binDir = Path.Combine(baseDir, "bin", config);
                if (!Directory.Exists(binDir)) continue;
            }

            foreach (var tfmDir in Directory.GetDirectories(binDir))
            {
                var dllPath = Path.Combine(tfmDir, $"{projectName}.dll");
                if (File.Exists(dllPath))
                {
                    refs.Add(dllPath);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves PackageReference DLLs from the NuGet assets file or global packages cache.
    /// </summary>
    private static void ResolvePackageReferences(string fsprojPath, string projectDir, HashSet<string> refs)
    {
        // Try obj/<project>.assets.json (generated by dotnet restore)
        var projectName = Path.GetFileNameWithoutExtension(fsprojPath);
        var assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");

        if (!File.Exists(assetsFile))
        {
            // Fallback: try to find packages in global NuGet cache by parsing .fsproj
            ResolvePackagesFromGlobalCache(fsprojPath, projectDir, refs);
            return;
        }

        // Parse the assets file for compile-time assemblies
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(assetsFile));
            var packageFolders = json?["packageFolders"]?.AsObject()?.Select(kv => kv.Key).ToList() ?? [];
            var targets = json?["targets"]?.AsObject();
            if (targets == null) return;

            // Use first target framework
            var firstTarget = targets.FirstOrDefault();
            if (firstTarget.Value is not System.Text.Json.Nodes.JsonObject packages) return;

            foreach (var (packageKey, packageNode) in packages)
            {
                var compile = packageNode?["compile"]?.AsObject();
                if (compile == null) continue;

                foreach (var (dllRelPath, _) in compile)
                {
                    if (dllRelPath == "_._" || !dllRelPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // packageKey format: "Package.Name/1.2.3"
                    foreach (var folder in packageFolders)
                    {
                        var fullPath = Path.Combine(folder, packageKey.ToLowerInvariant(), dllRelPath);
                        if (File.Exists(fullPath))
                        {
                            refs.Add(fullPath);
                            break;
                        }
                    }
                }
            }
        }
        catch { /* Best-effort */ }
    }

    /// <summary>
    /// Fallback: resolve packages from global NuGet cache by parsing PackageReference items.
    /// </summary>
    private static void ResolvePackagesFromGlobalCache(string fsprojPath, string projectDir, HashSet<string> refs)
    {
        try
        {
            var nugetCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
            if (!Directory.Exists(nugetCache)) return;

            var xml = System.Xml.Linq.XDocument.Load(fsprojPath);
            var packageRefs = xml.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (
                    Name: e.Attribute("Include")?.Value,
                    Version: e.Attribute("Version")?.Value))
                .Where(p => p.Name != null && p.Version != null &&
                            !p.Version!.StartsWith("$(")); // Skip MSBuild variable versions

            foreach (var (name, version) in packageRefs)
            {
                var packageDir = Path.Combine(nugetCache, name!.ToLowerInvariant(), version!);
                if (!Directory.Exists(packageDir)) continue;

                // Look for DLLs in lib/<tfm>/
                var libDir = Path.Combine(packageDir, "lib");
                if (!Directory.Exists(libDir)) continue;

                // Prefer net9.0 > net8.0 > netstandard2.1 > netstandard2.0
                foreach (var tfm in new[] { "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" })
                {
                    var tfmDir = Path.Combine(libDir, tfm);
                    if (!Directory.Exists(tfmDir)) continue;

                    foreach (var dll in Directory.GetFiles(tfmDir, "*.dll"))
                        refs.Add(dll);
                    break; // Use first matching TFM
                }
            }
        }
        catch { /* Best-effort */ }
    }
}
