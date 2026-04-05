namespace CodeMap.Storage.Telemetry;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Shared ActivitySource and Meter for the CodeMap storage layer.
/// Both the SQLite engine and the custom engine instrument through this class.
/// All instruments are pre-created at startup; no allocations on the hot path.
/// </summary>
public static class StorageTelemetry
{
    public const string ActivitySourceName = "CodeMap.Storage";
    public const string MeterName          = "CodeMap.Storage";
    public const string Version            = "2.0.0";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
    public static readonly Meter          Meter          = new(MeterName, Version);

    // Counters
    public static readonly Counter<long> SymbolLookups        = Meter.CreateCounter<long>("codemap.storage.symbol_lookups",        description: "Total symbol lookup requests");
    public static readonly Counter<long> SearchQueries        = Meter.CreateCounter<long>("codemap.storage.search_queries",        description: "Total symbol search queries");
    public static readonly Counter<long> AdjacencyTraversals  = Meter.CreateCounter<long>("codemap.storage.adjacency_traversals",  description: "Total adjacency traversal requests");
    public static readonly Counter<long> CacheHits            = Meter.CreateCounter<long>("codemap.storage.cache_hits",            description: "L1 cache hits");
    public static readonly Counter<long> CacheMisses          = Meter.CreateCounter<long>("codemap.storage.cache_misses",          description: "L1 cache misses");
    public static readonly Counter<long> WalAppends           = Meter.CreateCounter<long>("codemap.storage.wal_appends",           description: "WAL record appends");
    public static readonly Counter<long> Checkpoints          = Meter.CreateCounter<long>("codemap.storage.checkpoints",           description: "Overlay checkpoint operations");
    public static readonly Counter<long> BaselineBuilds       = Meter.CreateCounter<long>("codemap.storage.baseline_builds",       description: "Baseline build operations completed");
    public static readonly Counter<long> TombstoneApplications = Meter.CreateCounter<long>("codemap.storage.tombstone_applications", description: "Tombstones applied during merge reads");
    public static readonly Counter<long> MergeOverlayHits     = Meter.CreateCounter<long>("codemap.storage.merge_overlay_hits",    description: "Query results served from overlay");

    // Histograms
    public static readonly Histogram<double> SymbolLookupDuration   = Meter.CreateHistogram<double>("codemap.storage.symbol_lookup.duration",          unit: "ms", description: "Symbol lookup latency");
    public static readonly Histogram<double> SearchDuration         = Meter.CreateHistogram<double>("codemap.storage.search.duration",                  unit: "ms", description: "Symbol search query latency");
    public static readonly Histogram<double> AdjacencyDuration      = Meter.CreateHistogram<double>("codemap.storage.adjacency.duration",               unit: "ms", description: "Adjacency traversal latency");
    public static readonly Histogram<double> MergeDuration          = Meter.CreateHistogram<double>("codemap.storage.merge.duration",                   unit: "ms", description: "Baseline + overlay merge latency");
    public static readonly Histogram<double> BaselineBuildDuration  = Meter.CreateHistogram<double>("codemap.storage.baseline_build.duration",          unit: "ms", description: "Full baseline build latency");
    public static readonly Histogram<double> CheckpointDuration     = Meter.CreateHistogram<double>("codemap.storage.overlay_checkpoint.duration",      unit: "ms", description: "Overlay checkpoint latency");
    public static readonly Histogram<double> WalAppendDuration      = Meter.CreateHistogram<double>("codemap.storage.wal_append.duration",              unit: "ms", description: "Single WAL record append latency");
    public static readonly Histogram<double> DictionaryLookupDuration = Meter.CreateHistogram<double>("codemap.storage.dictionary_lookup.duration",     unit: "ms", description: "String dictionary lookup latency");
    public static readonly Histogram<double> SearchPostingsFetched  = Meter.CreateHistogram<double>("codemap.storage.search.postings_fetched",          unit: "{postings}", description: "Postings evaluated per search query");
}
