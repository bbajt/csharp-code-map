namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Read-only view of a finalized v2 baseline. Opens all segment files via mmap,
/// builds in-memory lookup tables on construction. Thread-safe after construction.
/// Dispose closes all mmap handles.
/// </summary>
internal sealed class EngineBaselineReader : IEngineBaselineReader
{
    private readonly SegmentReader<SymbolRecord> _symbols;
    private readonly SegmentReader<FileRecord> _files;
    private readonly SegmentReader<ProjectRecord> _projects;
    private readonly SegmentReader<EdgeRecord> _edges;
    private readonly SegmentReader<FactRecord> _facts;
    private readonly DictionaryReader _dictionary;
    private readonly ContentSegmentReader _content;
    private readonly string _baselineDir;

    // Lookup tables (built on open)
    private readonly Dictionary<string, int> _stableIdIndex;  // stableId string → SymbolIntId
    private readonly Dictionary<string, int> _fqnIndex;       // FQN string → SymbolIntId
    private readonly Dictionary<string, int> _pathIndex;      // normalized path → FileIntId
    private readonly Dictionary<int, List<int>> _symbolsByFile;  // FileIntId → SymbolIntId list
    private readonly Dictionary<int, List<int>> _factsBySymbol;  // SymbolIntId → FactIntId list (0-based index)
    private readonly Dictionary<int, List<int>> _factsByKind;    // FactKind → FactIntId list (0-based index)

    // Search + adjacency (set by InitSearch/InitAdjacency)
    private SearchIndexReader? _search;
    private AdjacencyIndexReader? _adjacency;

    private bool _disposed;

    public EngineBaselineReader(string baselineDir)
    {
        _baselineDir = baselineDir;

        // Read manifest
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        Manifest = ManifestWriter.Read(manifestPath)
            ?? throw new StorageFormatException($"Missing or invalid manifest.json in {baselineDir}");
        CommitSha = Manifest.CommitSha;

        // Open segments
        _dictionary = new DictionaryReader(Path.Combine(baselineDir, "dictionary.seg"));
        _content = new ContentSegmentReader(Path.Combine(baselineDir, "content.seg"));
        _symbols = new SegmentReader<SymbolRecord>(Path.Combine(baselineDir, "symbols.seg"));
        _files = new SegmentReader<FileRecord>(Path.Combine(baselineDir, "files.seg"));
        _projects = new SegmentReader<ProjectRecord>(Path.Combine(baselineDir, "projects.seg"));
        _edges = new SegmentReader<EdgeRecord>(Path.Combine(baselineDir, "edges.seg"));
        _facts = new SegmentReader<FactRecord>(Path.Combine(baselineDir, "facts.seg"));

        // Build lookup tables
        _stableIdIndex = new Dictionary<string, int>(SymbolCount, StringComparer.Ordinal);
        _fqnIndex = new Dictionary<string, int>(SymbolCount, StringComparer.Ordinal);
        _symbolsByFile = new Dictionary<int, List<int>>();

        for (var i = 0; i < SymbolCount; i++)
        {
            ref readonly var sym = ref _symbols[i];
            var intId = sym.SymbolIntId;

            if (sym.StableIdStringId > 0)
            {
                var stableId = _dictionary.Resolve(sym.StableIdStringId);
                _stableIdIndex.TryAdd(stableId, intId);
            }

            if (sym.FqnStringId > 0)
            {
                var fqn = _dictionary.Resolve(sym.FqnStringId);
                _fqnIndex.TryAdd(fqn, intId);
            }

            if (sym.FileIntId > 0)
            {
                if (!_symbolsByFile.TryGetValue(sym.FileIntId, out var list))
                {
                    list = [];
                    _symbolsByFile[sym.FileIntId] = list;
                }
                list.Add(intId);
            }
        }

        _pathIndex = new Dictionary<string, int>(FileCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < FileCount; i++)
        {
            ref readonly var file = ref _files[i];
            if (file.NormalizedStringId > 0)
            {
                var normalized = _dictionary.Resolve(file.NormalizedStringId);
                _pathIndex.TryAdd(normalized, file.FileIntId);
            }
            // Also index by original path (case-insensitive)
            if (file.PathStringId > 0)
            {
                var path = _dictionary.Resolve(file.PathStringId);
                _pathIndex.TryAdd(path.ToLowerInvariant(), file.FileIntId);
            }
        }

        _factsBySymbol = new Dictionary<int, List<int>>();
        _factsByKind = new Dictionary<int, List<int>>();
        for (var i = 0; i < FactCount; i++)
        {
            ref readonly var fact = ref _facts[i];

            if (fact.OwnerSymbolIntId > 0)
            {
                if (!_factsBySymbol.TryGetValue(fact.OwnerSymbolIntId, out var symList))
                {
                    symList = [];
                    _factsBySymbol[fact.OwnerSymbolIntId] = symList;
                }
                symList.Add(i);
            }

            if (!_factsByKind.TryGetValue(fact.FactKind, out var kindList))
            {
                kindList = [];
                _factsByKind[fact.FactKind] = kindList;
            }
            kindList.Add(i);
        }
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    public string CommitSha { get; }
    public BaselineManifest Manifest { get; }
    public IDictionaryReader Dictionary => _dictionary;

    // ── Counts ───────────────────────────────────────────────────────────────

    public int SymbolCount => _symbols.Count;
    public int FileCount => _files.Count;
    public int ProjectCount => _projects.Count;
    public int EdgeCount => _edges.Count;
    public int FactCount => _facts.Count;

    // ── Content access ───────────────────────────────────────────────────────

    public ContentSegmentReader Content => _content;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EngineBaselineReader));
    }

    // ── Symbol access ────────────────────────────────────────────────────────

    public ref readonly SymbolRecord GetSymbolByIntId(int symbolIntId)
    {
        ThrowIfDisposed();
        if (symbolIntId < 1 || symbolIntId > SymbolCount)
            throw new StorageFormatException($"SymbolIntId {symbolIntId} out of range [1..{SymbolCount}]");
        return ref _symbols[symbolIntId - 1]; // 0-based index
    }

    public SymbolRecord? GetSymbolByStableId(string stableId)
        => _stableIdIndex.TryGetValue(stableId, out var intId) ? GetSymbolByIntId(intId) : null;

    public SymbolRecord? GetSymbolByFqn(string fqn)
        => _fqnIndex.TryGetValue(fqn, out var intId) ? GetSymbolByIntId(intId) : null;

    public IReadOnlyList<SymbolRecord> GetSymbolsByFile(int fileIntId)
    {
        if (!_symbolsByFile.TryGetValue(fileIntId, out var intIds))
            return [];
        var result = new SymbolRecord[intIds.Count];
        for (var i = 0; i < intIds.Count; i++)
            result[i] = GetSymbolByIntId(intIds[i]);
        return result;
    }

    public IEnumerable<SymbolRecord> EnumerateSymbols()
    {
        ThrowIfDisposed();
        for (var i = 0; i < SymbolCount; i++)
            yield return _symbols[i];
    }

    // ── File access ──────────────────────────────────────────────────────────

    public ref readonly FileRecord GetFileByIntId(int fileIntId)
    {
        ThrowIfDisposed();
        if (fileIntId < 1 || fileIntId > FileCount)
            throw new StorageFormatException($"FileIntId {fileIntId} out of range [1..{FileCount}]");
        return ref _files[fileIntId - 1];
    }

    public FileRecord? GetFileByPath(string repoRelativePath)
    {
        var key = repoRelativePath.ToLowerInvariant();
        return _pathIndex.TryGetValue(key, out var intId) ? GetFileByIntId(intId) : null;
    }

    public IEnumerable<FileRecord> EnumerateFiles()
    {
        ThrowIfDisposed();
        for (var i = 0; i < FileCount; i++)
            yield return _files[i];
    }

    // ── Project access ───────────────────────────────────────────────────────

    public ref readonly ProjectRecord GetProjectByIntId(int projectIntId)
    {
        ThrowIfDisposed();
        if (projectIntId < 1 || projectIntId > ProjectCount)
            throw new StorageFormatException($"ProjectIntId {projectIntId} out of range [1..{ProjectCount}]");
        return ref _projects[projectIntId - 1];
    }

    public IEnumerable<ProjectRecord> EnumerateProjects()
    {
        ThrowIfDisposed();
        for (var i = 0; i < ProjectCount; i++)
            yield return _projects[i];
    }

    // ── Edge access ──────────────────────────────────────────────────────────

    public ref readonly EdgeRecord GetEdgeByIntId(int edgeIntId)
    {
        ThrowIfDisposed();
        if (edgeIntId < 1 || edgeIntId > EdgeCount)
            throw new StorageFormatException($"EdgeIntId {edgeIntId} out of range [1..{EdgeCount}]");
        return ref _edges[edgeIntId - 1];
    }

    public IReadOnlyList<EdgeRecord> GetOutgoingEdges(int symbolIntId, EdgeFilter filter = default)
    {
        if (_adjacency == null) return [];
        var edgeIds = _adjacency.GetOutgoingEdgeIds(symbolIntId);
        return FilterEdges(edgeIds, filter);
    }

    public IReadOnlyList<EdgeRecord> GetIncomingEdges(int symbolIntId, EdgeFilter filter = default)
    {
        if (_adjacency == null) return [];
        var edgeIds = _adjacency.GetIncomingEdgeIds(symbolIntId);
        return FilterEdges(edgeIds, filter);
    }

    private List<EdgeRecord> FilterEdges(ReadOnlySpan<int> edgeIds, EdgeFilter filter)
    {
        var result = new List<EdgeRecord>(edgeIds.Length);
        foreach (var eid in edgeIds)
        {
            ref readonly var edge = ref GetEdgeByIntId(eid);
            if (filter.EdgeKind.HasValue && edge.EdgeKind != filter.EdgeKind.Value) continue;
            if (filter.ResolvedOnly && edge.ResolutionState != 0) continue;
            result.Add(edge);
        }
        return result;
    }

    public IEnumerable<EdgeRecord> EnumerateEdges()
    {
        ThrowIfDisposed();
        for (var i = 0; i < EdgeCount; i++)
            yield return _edges[i];
    }

    // ── Fact access ──────────────────────────────────────────────────────────

    public IReadOnlyList<FactRecord> GetFactsBySymbol(int symbolIntId)
    {
        ThrowIfDisposed();
        if (!_factsBySymbol.TryGetValue(symbolIntId, out var indices))
            return [];
        var result = new FactRecord[indices.Count];
        for (var i = 0; i < indices.Count; i++)
            result[i] = _facts[indices[i]];
        return result;
    }

    public IReadOnlyList<FactRecord> GetFactsByKind(int factKind)
    {
        ThrowIfDisposed();
        if (!_factsByKind.TryGetValue(factKind, out var indices))
            return [];
        var result = new FactRecord[indices.Count];
        for (var i = 0; i < indices.Count; i++)
            result[i] = _facts[indices[i]];
        return result;
    }

    public IEnumerable<FactRecord> EnumerateFacts()
    {
        ThrowIfDisposed();
        for (var i = 0; i < FactCount; i++)
            yield return _facts[i];
    }

    // ── Sub-readers ──────────────────────────────────────────────────────────

    public IEngineSearchIndex Search => _search
        ?? throw new InvalidOperationException("Search index not initialized. Call InitSearch first.");

    public IEngineAdjacencyIndex Adjacency => _adjacency
        ?? throw new InvalidOperationException("Adjacency index not initialized. Call InitAdjacency first.");

    /// <summary>Initializes the search index reader. Called after construction.</summary>
    internal void InitSearch(SearchIndexReader search) => _search = search;

    /// <summary>Initializes the adjacency index reader. Called after construction.</summary>
    internal void InitAdjacency(AdjacencyIndexReader adjacency) => _adjacency = adjacency;

    // ── Resolve helpers (used by merged reader + CustomSymbolStore) ──────────

    public string ResolveString(int stringId)
    {
        ThrowIfDisposed();
        return _dictionary.Resolve(stringId);
    }

    public string ResolveContent(int contentId)
    {
        ThrowIfDisposed();
        return _content.ResolveContent(contentId);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _search = null;
        _adjacency?.Dispose();
        _facts.Dispose();
        _edges.Dispose();
        _projects.Dispose();
        _files.Dispose();
        _symbols.Dispose();
        _content.Dispose();
        _dictionary.Dispose();
    }
}
