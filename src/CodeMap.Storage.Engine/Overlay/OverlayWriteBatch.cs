namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Atomic write batch. Mutations buffered in memory until CommitAsync().
/// Dispose without commit = rollback. Not thread-safe.
/// </summary>
internal sealed class OverlayWriteBatch : IOverlayWriteBatch
{
    private readonly EngineOverlay _overlay;
    private readonly List<Action> _pendingApply = [];
    private readonly List<Action<WalWriter>> _pendingWal = [];
    private readonly HashSet<int> _newStringIds = [];
    private bool _committed;
    private bool _disposed;

    internal OverlayWriteBatch(EngineOverlay overlay) => _overlay = overlay;

    public int InternString(string value)
    {
        var id = _overlay.InternStringInternal(value);
        if (id > _overlay.NBaselineStringIds)
            _newStringIds.Add(id);
        return id;
    }

    public void UpsertSymbol(SymbolRecord record, string[] tokens)
    {
        var stableId = _overlay.ResolveString(record.StableIdStringId);
        // Track all overlay-local StringIds on this record
        TrackStringId(record.StableIdStringId);
        TrackStringId(record.FqnStringId);
        TrackStringId(record.DisplayNameStringId);
        TrackStringId(record.NamespaceStringId);
        TrackStringId(record.NameTokensStringId);
        _pendingWal.Add(w => w.WriteSymbolRecord(0x01, record));
        _pendingApply.Add(() => _overlay.ApplySymbol(record, stableId, tokens));
    }

    private void TrackStringId(int stringId)
    {
        if (stringId > _overlay.NBaselineStringIds)
            _newStringIds.Add(stringId);
    }

    public void AddEdge(EdgeRecord record)
    {
        _pendingWal.Add(w => w.WriteEdgeRecord(0x03, record));
        _pendingApply.Add(() => _overlay.ApplyEdge(record));
    }

    public void AddFact(FactRecord record)
    {
        _pendingWal.Add(w => w.WriteFactRecord(record));
        _pendingApply.Add(() => _overlay.ApplyFact(record));
    }

    public void UpsertFile(FileRecord record)
    {
        var path = _overlay.ResolveString(record.PathStringId);
        _pendingWal.Add(w => w.WriteFileRecord(record));
        _pendingApply.Add(() => _overlay.ApplyFile(record, path));
    }

    public void Tombstone(int entityKind, int entityIntId, string? stableId = null)
    {
        var stableIdSid = stableId != null ? InternString(stableId) : 0;
        var flags = entityIntId > 0 ? 1 : 0; // TargetsBaseline
        _pendingWal.Add(w => w.WriteTombstone(entityKind, entityIntId, stableIdSid, flags));
        if (stableId != null)
            _pendingApply.Add(() => _overlay.ApplyTombstone(stableId));
    }

    public void ResolveEdge(int fromSymbolIntId, int fileIntId, int spanStart, int resolvedToSymbolIntId)
    {
        // Find the unresolved edge and create an update record
        var updated = new EdgeRecord(
            edgeIntId: 0, // edge ID not tracked in overlay edge update
            fromSymbolIntId: fromSymbolIntId,
            toSymbolIntId: resolvedToSymbolIntId,
            toNameStringId: 0, // cleared on resolution
            fileIntId: fileIntId,
            spanStart: spanStart,
            spanEnd: spanStart,
            edgeKind: 1, // Call (most common)
            resolutionState: 0, // Resolved
            flags: 0,
            weight: 1);

        _pendingWal.Add(w => w.WriteEdgeRecord(0x04, updated)); // UpdateEdge
        _pendingApply.Add(() => _overlay.ApplyEdge(updated));
    }

    public Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) throw new InvalidOperationException("Batch already committed.");
        _committed = true;

        // Step 1: Write WAL records (outside lock — I/O)
        var writer = _overlay.GetWalWriter();

        // Write DictionaryAdd WAL records for all new overlay strings in this batch
        foreach (var id in _newStringIds)
        {
            if (_overlay.OverlayDictionary.TryGetValue(id, out var value))
            {
                writer.WriteDictionaryAdd(id, value);
                _overlay.IncrementWalRecordCount();
            }
        }

        foreach (var walAction in _pendingWal)
        {
            walAction(writer);
            _overlay.IncrementWalRecordCount();
        }

        writer.Flush(flushToDisk: true);

        // Step 2: Apply to in-memory state (under write lock)
        _overlay._lock.EnterWriteLock();
        try
        {
            foreach (var apply in _pendingApply)
                apply();
            _overlay.Revision++;
        }
        finally { _overlay._lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }


    public void Dispose()
    {
        _disposed = true;
        _pendingApply.Clear();
        _pendingWal.Clear();
    }
}
