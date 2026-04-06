namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Mutable workspace overlay backed by WAL. One instance per workspace.
/// Read methods are thread-safe via ReaderWriterLockSlim. Write via IOverlayWriteBatch.
/// Dispose triggers graceful checkpoint (C-019) then releases file handles.
/// </summary>
internal sealed class EngineOverlay : IEngineOverlay
{
    private readonly string _overlayDir;
    private readonly EngineBaselineReader _baseline;
    private readonly ReaderWriterLockSlim _lock = new();
    private WalWriter? _walWriter;
    private uint _lastWalSequence;
    private uint _walRecordCount;
    private bool _disposed;

    // In-memory overlay state
    internal readonly Dictionary<string, SymbolRecord> SymbolsByStableId = new(StringComparer.Ordinal);
    internal readonly HashSet<string> TombstoneSet = new(StringComparer.Ordinal);
    internal readonly Dictionary<int, List<EdgeRecord>> OutgoingEdges = [];
    internal readonly Dictionary<int, List<EdgeRecord>> IncomingEdges = [];
    internal readonly Dictionary<int, List<FactRecord>> FactsBySymbol = [];
    internal readonly Dictionary<string, FileRecord> FilesByPath = new(StringComparer.OrdinalIgnoreCase);
    internal readonly SortedDictionary<string, HashSet<int>> TokenMap = new(StringComparer.Ordinal);
    internal readonly Dictionary<int, string> OverlayDictionary = [];
    internal readonly Dictionary<string, int> OverlayDictReverse = new(StringComparer.Ordinal);

    internal int NextOverlayStringId;
    internal int NextOverlaySymbolIntId = -1;
    internal int NextOverlayEdgeIntId = -1;
    internal int NextOverlayFileIntId = -1;
    internal int NextOverlayFactIntId = -1;

    public string WorkspaceId { get; }
    public int Revision { get; internal set; }
    public string BaseCommitSha { get; }
    public int NBaselineStringIds { get; }

    public EngineOverlay(string overlayDir, string workspaceId, EngineBaselineReader baseline)
    {
        _overlayDir = overlayDir;
        _baseline = baseline;
        WorkspaceId = workspaceId;
        BaseCommitSha = baseline.CommitSha;
        NBaselineStringIds = baseline.Manifest.NStringIds;
        NextOverlayStringId = NBaselineStringIds + 1;

        Directory.CreateDirectory(overlayDir);

        var manifestPath = Path.Combine(overlayDir, "manifest.json");
        if (File.Exists(manifestPath))
        {
            // Recover: load snapshot + replay WAL
            LoadSnapshot();
            ReplayWal();
        }
        else
        {
            // New overlay
            WriteManifest();
        }

        OpenWalWriter();
    }

    // ── String resolution ────────────────────────────────────────────────────

    public string ResolveString(int stringId)
    {
        if (stringId <= 0) return string.Empty;
        if (stringId <= NBaselineStringIds)
            return _baseline.Dictionary.Resolve(stringId);
        _lock.EnterReadLock();
        try
        {
            return OverlayDictionary.TryGetValue(stringId, out var v) ? v : "";
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Overlay read (thread-safe via read lock) ─────────────────────────────

    public SymbolRecord? TryGetOverlaySymbol(string stableId, out bool isTombstoned)
    {
        _lock.EnterReadLock();
        try
        {
            isTombstoned = TombstoneSet.Contains(stableId);
            return SymbolsByStableId.TryGetValue(stableId, out var rec) ? rec : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<EdgeRecord> GetOverlayOutgoingEdges(int baselineSymbolIntId)
    {
        _lock.EnterReadLock();
        try
        {
            return OutgoingEdges.TryGetValue(baselineSymbolIntId, out var list)
                ? list.ToArray() : [];
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<EdgeRecord> GetOverlayIncomingEdges(int baselineSymbolIntId)
    {
        _lock.EnterReadLock();
        try
        {
            return IncomingEdges.TryGetValue(baselineSymbolIntId, out var list)
                ? list.ToArray() : [];
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<FactRecord> GetOverlayFacts(int symbolIntId)
    {
        _lock.EnterReadLock();
        try
        {
            return FactsBySymbol.TryGetValue(symbolIntId, out var list)
                ? list.ToArray() : [];
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<SymbolRecord> GetOverlayNewSymbols()
    {
        _lock.EnterReadLock();
        try
        {
            return SymbolsByStableId.Values.Where(s => s.SymbolIntId < 0).ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlySet<string> Tombstones
    {
        get
        {
            _lock.EnterReadLock();
            try { return TombstoneSet.ToHashSet(); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Returns a snapshot of all overlay file paths under read lock.</summary>
    public IReadOnlySet<string> GetFilePathsSnapshot()
    {
        _lock.EnterReadLock();
        try { return FilesByPath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Returns the total number of overlay facts under read lock.</summary>
    public int GetFactCount()
    {
        _lock.EnterReadLock();
        try { return FactsBySymbol.Values.Sum(l => l.Count); }
        finally { _lock.ExitReadLock(); }
    }

    public FileRecord? TryGetOverlayFile(string repoRelativePath)
    {
        _lock.EnterReadLock();
        try
        {
            return FilesByPath.TryGetValue(repoRelativePath, out var f) ? f : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<int> GetOverlaySymbolsForTokenPrefix(string tokenPrefix)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new HashSet<int>();
            foreach (var (token, ids) in TokenMap)
            {
                if (string.Compare(token, tokenPrefix, StringComparison.Ordinal) < 0) continue;
                if (!token.StartsWith(tokenPrefix, StringComparison.Ordinal)) break;
                result.UnionWith(ids);
            }
            return result.ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public IOverlayWriteBatch BeginBatch() => new OverlayWriteBatch(this);

    // ── Checkpoint ───────────────────────────────────────────────────────────

    public Task CheckpointIfNeededAsync(CancellationToken ct = default)
    {
        if (_walWriter == null) return Task.CompletedTask;
        if (_walWriter.Length <= 16 * 1024 * 1024 && _walRecordCount <= 50_000)
            return Task.CompletedTask;
        return DoCheckpointAsync(ct);
    }

    internal async Task DoCheckpointAsync(CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshotPath = Path.Combine(_overlayDir, "overlay.snapshot");
            var tmpPath = snapshotPath + ".tmp";

            SnapshotSerializer.Write(tmpPath, this);
            File.Move(tmpPath, snapshotPath, overwrite: true);

            // Truncate WAL
            _walWriter?.Dispose();
            var walPath = Path.Combine(_overlayDir, "overlay.wal");
            await using (var fs = new FileStream(walPath, FileMode.Create, FileAccess.Write))
            {
                // Empty file
            }
            _walRecordCount = 0;
            _lastWalSequence = 0;
            OpenWalWriter();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Synchronous checkpoint for use in Dispose — avoids sync-over-async.</summary>
    private void DoCheckpointSync()
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshotPath = Path.Combine(_overlayDir, "overlay.snapshot");
            var tmpPath = snapshotPath + ".tmp";

            SnapshotSerializer.Write(tmpPath, this);
            File.Move(tmpPath, snapshotPath, overwrite: true);

            _walWriter?.Dispose();
            var walPath = Path.Combine(_overlayDir, "overlay.wal");
            using (var fs = new FileStream(walPath, FileMode.Create, FileAccess.Write)) { }
            _walRecordCount = 0;
            _lastWalSequence = 0;
            OpenWalWriter();
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    internal void ApplySymbol(SymbolRecord record, string stableId, string[] tokens)
    {
        SymbolsByStableId[stableId] = record;
        foreach (var t in tokens)
        {
            if (!TokenMap.TryGetValue(t, out var set))
            {
                set = [];
                TokenMap[t] = set;
            }
            set.Add(record.SymbolIntId);
        }
    }

    internal void ApplyEdge(EdgeRecord record)
    {
        if (record.FromSymbolIntId != 0)
        {
            if (!OutgoingEdges.TryGetValue(record.FromSymbolIntId, out var outList))
            {
                outList = [];
                OutgoingEdges[record.FromSymbolIntId] = outList;
            }
            outList.Add(record);
        }
        if (record.ToSymbolIntId != 0)
        {
            if (!IncomingEdges.TryGetValue(record.ToSymbolIntId, out var inList))
            {
                inList = [];
                IncomingEdges[record.ToSymbolIntId] = inList;
            }
            inList.Add(record);
        }
    }

    internal void ApplyFact(FactRecord record)
    {
        if (!FactsBySymbol.TryGetValue(record.OwnerSymbolIntId, out var list))
        {
            list = [];
            FactsBySymbol[record.OwnerSymbolIntId] = list;
        }
        list.Add(record);
    }

    internal void ApplyFile(FileRecord record, string path)
    {
        FilesByPath[path] = record;
    }

    internal void ApplyTombstone(string stableId)
    {
        TombstoneSet.Add(stableId);
        SymbolsByStableId.Remove(stableId);
    }

    internal void ApplyDictionaryEntry(int stringId, string value)
    {
        OverlayDictionary[stringId] = value;
        OverlayDictReverse[value] = stringId;
        if (stringId >= NextOverlayStringId)
            NextOverlayStringId = stringId + 1;
    }

    internal int InternStringInternal(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (_baseline.Dictionary.TryFind(value, out var baselineId)) return baselineId;

        _lock.EnterWriteLock();
        try
        {
            // Double-check under lock (another batch may have interned the same string)
            if (OverlayDictReverse.TryGetValue(value, out var overlayId)) return overlayId;
            var id = NextOverlayStringId++;
            OverlayDictionary[id] = value;
            OverlayDictReverse[value] = id;
            return id;
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal WalWriter GetWalWriter() => _walWriter!;
    internal void IncrementWalRecordCount() => _walRecordCount++;

    // ── Snapshot + WAL recovery ──────────────────────────────────────────────

    private void LoadSnapshot()
    {
        var snapshotPath = Path.Combine(_overlayDir, "overlay.snapshot");
        if (!File.Exists(snapshotPath)) return;
        SnapshotSerializer.Read(snapshotPath, this);
    }

    private void ReplayWal()
    {
        var walPath = Path.Combine(_overlayDir, "overlay.wal");
        _lastWalSequence = WalReader.Replay(walPath, _lastWalSequence, (recordType, seq, payload) =>
        {
            ApplyWalRecord(recordType, payload);
            _walRecordCount++;
        });
    }

    private void ApplyWalRecord(ushort recordType, byte[] payload)
    {
        switch (recordType)
        {
            case 0x01: // AddSymbol
            case 0x02: // UpdateSymbol
                var sym = MemoryMarshal.Read<SymbolRecord>(payload);
                var stableId = ResolveString(sym.StableIdStringId);
                var nameTokens = sym.NameTokensStringId > 0 ? ResolveString(sym.NameTokensStringId).Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                ApplySymbol(sym, stableId, nameTokens);
                break;
            case 0x03: // AddEdge
            case 0x04: // UpdateEdge
                var edge = MemoryMarshal.Read<EdgeRecord>(payload);
                ApplyEdge(edge);
                break;
            case 0x05: // AddFact
                var fact = MemoryMarshal.Read<FactRecord>(payload);
                ApplyFact(fact);
                break;
            case 0x06: // AddFile
                var file = MemoryMarshal.Read<FileRecord>(payload);
                var path = ResolveString(file.PathStringId);
                ApplyFile(file, path);
                break;
            case 0x07: // Tombstone
                var tsStableIdSid = BitConverter.ToInt32(payload.AsSpan(8));
                if (tsStableIdSid > 0)
                    ApplyTombstone(ResolveString(tsStableIdSid));
                break;
            case 0x08: // DictionaryAdd
                var (sid, val) = WalReader.ParseDictionaryAdd(payload);
                ApplyDictionaryEntry(sid, val);
                break;
            case 0x09: // CheckpointBegin — no-op during replay
            case 0x0A: // CheckpointComplete — no-op during replay
                break;
        }
    }

    private void WriteManifest()
    {
        var manifest = new
        {
            format_major = StorageConstants.FormatMajor,
            format_minor = StorageConstants.FormatMinor,
            workspace_id = WorkspaceId,
            base_commit_sha = BaseCommitSha,
            n_baseline_string_ids = NBaselineStringIds,
            created_at_utc = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(Path.Combine(_overlayDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void OpenWalWriter()
    {
        var walPath = Path.Combine(_overlayDir, "overlay.wal");
        _walWriter = new WalWriter(walPath, _lastWalSequence);
    }

    // ── Lock accessors for OverlayWriteBatch ───────────────────────────────

    internal void EnterReadLock() => _lock.EnterReadLock();
    internal void ExitReadLock() => _lock.ExitReadLock();
    internal void EnterWriteLock() => _lock.EnterWriteLock();
    internal void ExitWriteLock() => _lock.ExitWriteLock();

    // ── Dispose (graceful shutdown checkpoint, C-019) ────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_walRecordCount > 0)
                DoCheckpointSync();
        }
        catch
        {
            // Best effort on shutdown
        }

        _walWriter?.Dispose();
        _lock.Dispose();
    }
}
