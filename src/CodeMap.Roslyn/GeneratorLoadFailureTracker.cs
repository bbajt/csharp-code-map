namespace CodeMap.Roslyn;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Finds source-generator assemblies that a project references but that the running compiler could
/// not load (F10, PHASE-21-05, ADR-046). The typical cause is an SDK whose generators — e.g. the
/// Razor compiler — are built for a newer Roslyn than CodeMap bundles: Roslyn refuses to load them,
/// MSBuildWorkspace swallows the failure, and the generated code (Razor components) silently
/// vanishes from the index.
/// </summary>
/// <remarks>
/// Deterministic by design: an analyzer reference that yields no generators is inspected from
/// metadata only (it can't be loaded). If it declares a <c>[Generator]</c> type, it failed. The
/// <c>AnalyzerLoadFailed</c> event is not used: it fires once per (shared, cached) reference, so a
/// second project using the same generator would stay silent.
/// </remarks>
internal static class GeneratorLoadFailureTracker
{
    private const string GeneratorAttributeNamespace = "Microsoft.CodeAnalysis";
    private const string GeneratorAttributeName = "GeneratorAttribute";

    /// <summary>The Roslyn version CodeMap runs (the <c>Microsoft.CodeAnalysis</c> assembly version).</summary>
    internal static Version RunningCompilerVersion { get; } = typeof(Compilation).Assembly.GetName().Version!;

    /// <summary>
    /// Returns one formatted entry per generator assembly of <paramref name="project"/> that loaded no
    /// generators, e.g. <c>"Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn
    /// 5.9.0.0, CodeMap runs 5.3.0.0)"</c>. Empty when every generator loaded.
    /// </summary>
    /// <param name="project">Project whose analyzer references to inspect.</param>
    /// <param name="generatorAssemblyCache">Per-run memo of <see cref="IsGeneratorAssembly"/> by path.</param>
    internal static IReadOnlyList<string> Inspect(Project project, IDictionary<string, bool>? generatorAssemblyCache = null)
    {
        List<string>? failures = null;
        foreach (var reference in project.AnalyzerReferences)
        {
            if (reference is not AnalyzerFileReference fileReference || string.IsNullOrEmpty(fileReference.FullPath))
                continue;

            // Loaded generators (or a plain analyzer assembly) → nothing missing.
            if (fileReference.GetGeneratorsForAllLanguages().Length > 0)
                continue;

            var path = fileReference.FullPath;
            if (generatorAssemblyCache is null || !generatorAssemblyCache.TryGetValue(path, out var isGenerator))
            {
                isGenerator = IsGeneratorAssembly(path);
                if (generatorAssemblyCache is not null) generatorAssemblyCache[path] = isGenerator;
            }
            if (!isGenerator) continue;

            (failures ??= []).Add(Format(fileReference.Display ?? Path.GetFileNameWithoutExtension(path), ReferencedCompilerVersion(path)));
        }
        return failures ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Formats one failure. A referenced compiler newer than <see cref="RunningCompilerVersion"/> is
    /// reported as <c>ReferencesNewerCompiler</c>; anything else as <c>UnableToLoadGenerator</c>.
    /// </summary>
    internal static string Format(string assemblyName, Version? referencedCompiler) =>
        referencedCompiler is not null && referencedCompiler > RunningCompilerVersion
            ? $"{assemblyName}: ReferencesNewerCompiler (built for Roslyn {referencedCompiler}, CodeMap runs {RunningCompilerVersion})"
            : $"{assemblyName}: UnableToLoadGenerator (declares a source generator, but none could be loaded)";

    /// <summary>
    /// True when the assembly at <paramref name="assemblyPath"/> declares a type carrying
    /// <c>[Microsoft.CodeAnalysis.Generator]</c> — Roslyn's own generator discovery rule — read from
    /// metadata without loading it. False for missing or unreadable files.
    /// </summary>
    internal static bool IsGeneratorAssembly(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();

            foreach (var typeHandle in md.TypeDefinitions)
            {
                foreach (var attributeHandle in md.GetTypeDefinition(typeHandle).GetCustomAttributes())
                {
                    if (IsGeneratorAttribute(md, md.GetCustomAttribute(attributeHandle)))
                        return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The version of <c>Microsoft.CodeAnalysis</c> the assembly references, read from metadata;
    /// null when it doesn't reference it or can't be read.
    /// </summary>
    internal static Version? ReferencedCompilerVersion(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                if (md.StringComparer.Equals(reference.Name, "Microsoft.CodeAnalysis"))
                    return reference.Version;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsGeneratorAttribute(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle attributeType;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                attributeType = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                attributeType = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return false;
        }

        return attributeType.Kind switch
        {
            HandleKind.TypeReference => Matches(md, md.GetTypeReference((TypeReferenceHandle)attributeType).Namespace,
                md.GetTypeReference((TypeReferenceHandle)attributeType).Name),
            HandleKind.TypeDefinition => Matches(md, md.GetTypeDefinition((TypeDefinitionHandle)attributeType).Namespace,
                md.GetTypeDefinition((TypeDefinitionHandle)attributeType).Name),
            _ => false,
        };

        static bool Matches(MetadataReader md, StringHandle ns, StringHandle name) =>
            md.StringComparer.Equals(ns, GeneratorAttributeNamespace) && md.StringComparer.Equals(name, GeneratorAttributeName);
    }
}
