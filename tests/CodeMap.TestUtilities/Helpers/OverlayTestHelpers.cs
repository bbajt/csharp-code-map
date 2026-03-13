namespace CodeMap.TestUtilities.Helpers;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>Builder helpers for overlay test data. Used by Storage.Tests and Integration.Tests.</summary>
public static class OverlayTestHelpers
{
    public static ExtractedFile MakeFile(
        string path = "src/Foo.cs",
        string fileId = "aabbccdd11223344",
        string? sha256 = null) =>
        new(
            FileId: fileId,
            Path: FilePath.From(path),
            Sha256Hash: sha256 ?? new string('a', 64),
            ProjectName: "TestProject");

    public static SymbolCard MakeSymbol(
        string symbolId = "T:TestNs.Foo",
        string fqname = "TestNs.Foo",
        string filePath = "src/Foo.cs",
        SymbolKind kind = SymbolKind.Class,
        int spanStart = 1,
        int spanEnd = 10) =>
        SymbolCard.CreateMinimal(
            symbolId: SymbolId.From(symbolId),
            fullyQualifiedName: fqname,
            kind: kind,
            signature: $"public class {fqname.Split('.')[^1]}",
            @namespace: "TestNs",
            filePath: FilePath.From(filePath),
            spanStart: spanStart,
            spanEnd: spanEnd,
            visibility: "public",
            confidence: Confidence.High);

    public static ExtractedReference MakeRef(
        string fromSymbol = "M:TestNs.Bar.Run",
        string toSymbol = "T:TestNs.Foo",
        string filePath = "src/Bar.cs",
        RefKind kind = RefKind.Call) =>
        new(
            FromSymbol: SymbolId.From(fromSymbol),
            ToSymbol: SymbolId.From(toSymbol),
            Kind: kind,
            FilePath: FilePath.From(filePath),
            LineStart: 5,
            LineEnd: 5);

    public static OverlayDelta MakeDelta(
        IReadOnlyList<ExtractedFile>? files = null,
        IReadOnlyList<SymbolCard>? symbols = null,
        IReadOnlyList<SymbolId>? deletedIds = null,
        IReadOnlyList<ExtractedReference>? refs = null,
        IReadOnlyList<FilePath>? deletedRefFiles = null,
        int newRevision = 1)
    {
        var defaultFile = MakeFile();
        var defaultSymbol = MakeSymbol();

        return new OverlayDelta(
            ReindexedFiles: files ?? [defaultFile],
            AddedOrUpdatedSymbols: symbols ?? [defaultSymbol],
            DeletedSymbolIds: deletedIds ?? [],
            AddedOrUpdatedReferences: refs ?? [],
            DeletedReferenceFiles: deletedRefFiles ?? [],
            NewRevision: newRevision);
    }
}
