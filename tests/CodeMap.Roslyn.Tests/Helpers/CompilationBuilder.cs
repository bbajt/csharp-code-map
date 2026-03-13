namespace CodeMap.Roslyn.Tests.Helpers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Builds in-memory Roslyn compilations for unit testing extractors.
/// </summary>
internal static class CompilationBuilder
{
    private static readonly MetadataReference[] _coreRefs =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(IEnumerable<>).Assembly.Location),
        // System.Runtime
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    ];

    /// <summary>Creates a compilation from one or more C# source strings.</summary>
    public static Compilation Create(params string[] sources)
    {
        var trees = sources.Select((src, i) =>
            CSharpSyntaxTree.ParseText(src, path: $"Test{i}.cs")).ToArray();

        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: trees,
            references: _coreRefs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    /// <summary>Creates a compilation with a single named file.</summary>
    public static Compilation CreateFromFile(string source, string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [tree],
            references: _coreRefs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }
}
