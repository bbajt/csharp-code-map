namespace CodeMap.TestUtilities.Builders;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Fixtures;

/// <summary>
/// Fluent builder for creating EvidencePointer instances in tests.
/// </summary>
public class EvidencePointerBuilder
{
    private RepoId _repoId = TestConstants.SampleRepoId;
    private FilePath _filePath = TestConstants.SampleFilePath;
    private int _lineStart = 1;
    private int _lineEnd = 10;
    private SymbolId? _symbolId = null;
    private string? _excerpt = null;

    public EvidencePointerBuilder WithRepoId(string id) { _repoId = RepoId.From(id); return this; }
    public EvidencePointerBuilder WithFilePath(string path) { _filePath = FilePath.From(path); return this; }
    public EvidencePointerBuilder WithLines(int start, int end) { _lineStart = start; _lineEnd = end; return this; }
    public EvidencePointerBuilder WithSymbolId(string id) { _symbolId = SymbolId.From(id); return this; }
    public EvidencePointerBuilder WithExcerpt(string excerpt) { _excerpt = excerpt; return this; }

    public EvidencePointer Build() =>
        new(_repoId, _filePath, _lineStart, _lineEnd, _symbolId, _excerpt);
}
