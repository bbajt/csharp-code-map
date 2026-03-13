namespace CodeMap.Storage.Tests.Helpers;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.Extensions.Logging.Abstractions;

internal static class StorageTestHelpers
{
    public static readonly RepoId TestRepo = RepoId.From("test-repo");
    public static readonly CommitSha TestSha = CommitSha.From(new string('a', 40));

    public static (BaselineStore Store, string TempDir) CreateDiskStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var factory = new BaselineDbFactory(tempDir, NullLogger<BaselineDbFactory>.Instance);
        var store = new BaselineStore(factory, NullLogger<BaselineStore>.Instance);
        return (store, tempDir);
    }

    public static ExtractedFile MakeFile(string path, string fileId, string? project = null)
        => new(fileId, FilePath.From(path), Sha256Hash: new string('0', 64), ProjectName: project);

    public static SymbolCard MakeSymbol(
        string symbolId,
        string fqname,
        SymbolKind kind,
        string filePath,
        int spanStart = 1,
        int spanEnd = 10,
        string @namespace = "TestNs",
        string visibility = "public",
        string? documentation = null,
        string? containingType = null,
        Confidence confidence = Confidence.High)
        => SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(symbolId),
            fullyQualifiedName: fqname,
            kind: kind,
            signature: $"{fqname}()",
            @namespace: @namespace,
            filePath: FilePath.From(filePath),
            spanStart: spanStart,
            spanEnd: spanEnd,
            visibility: visibility,
            confidence: confidence,
            documentation: documentation,
            containingType: containingType);

    public static ExtractedReference MakeRef(
        string from,
        string to,
        RefKind kind,
        string filePath,
        int lineStart = 5,
        int lineEnd = 5)
        => new(SymbolId.From(from), SymbolId.From(to), kind, FilePath.From(filePath), lineStart, lineEnd);

    public static CompilationResult MakeResult(
        IReadOnlyList<SymbolCard>? symbols = null,
        IReadOnlyList<ExtractedReference>? refs = null,
        IReadOnlyList<ExtractedFile>? files = null)
        => new(
            symbols ?? [],
            refs ?? [],
            files ?? [],
            new IndexStats(
                SymbolCount: symbols?.Count ?? 0,
                ReferenceCount: refs?.Count ?? 0,
                FileCount: files?.Count ?? 0,
                ElapsedSeconds: 0,
                Confidence: Confidence.High));
}
