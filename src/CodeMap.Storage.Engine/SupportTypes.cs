namespace CodeMap.Storage.Engine;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;

// ── Filter types ──────────────────────────────────────────────────────────────

/// <summary>Symbol search filter. All fields are optional (null = no constraint).</summary>
internal readonly record struct SymbolSearchFilter(
    short?  Kind               = null,
    string? NamespacePrefix    = null,
    string? FilePathPrefix     = null,
    string? ProjectName        = null,
    bool    ExcludeDecompiled  = false,
    bool    ExcludeTestSymbols = false,
    int     Limit              = 50);

/// <summary>Text search filter for code.search_text.</summary>
internal readonly record struct TextSearchFilter(
    string? FileGlob = null,
    int     Limit    = 200);

/// <summary>Edge traversal filter.</summary>
internal readonly record struct EdgeFilter(
    short? EdgeKind     = null,
    bool   ResolvedOnly = false);

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>Symbol search result with computed relevance score.</summary>
internal readonly record struct SymbolSearchResult(SymbolRecord Symbol, int Score);

/// <summary>Single pattern match within a file.</summary>
internal readonly record struct TextMatch(
    string FilePath,
    int    Line,
    string LineText,
    int    MatchStart,
    int    MatchLength);

// ── Baseline manifest ─────────────────────────────────────────────────────────

/// <summary>Deserialized from manifest.json. Presence of manifest.json = baseline complete.</summary>
internal sealed record BaselineManifest(
    int             FormatMajor,
    int             FormatMinor,
    string          CommitSha,
    DateTimeOffset  CreatedAt,
    int             SymbolCount,
    int             FileCount,
    int             ProjectCount,
    int             EdgeCount,
    int             FactCount,
    int             NStringIds,
    IReadOnlyDictionary<string, SegmentInfo> Segments,
    string?         RepoRootPath = null,
    IReadOnlyList<Core.Models.ProjectDiagnostic>? ProjectDiagnostics = null);

/// <summary>Per-segment CRC32 checksum entry in the baseline manifest.</summary>
internal sealed record SegmentInfo(string File, string Crc32Hex);

// ── Build types ───────────────────────────────────────────────────────────────

/// <summary>Input to IEngineBaselineBuilder.BuildAsync.</summary>
internal sealed record BaselineBuildInput(
    string                              CommitSha,
    string                              RepoRootPath,
    IReadOnlyList<SymbolCard>           Symbols,
    IReadOnlyList<ExtractedFile>        Files,
    IReadOnlyList<ExtractedReference>   References,
    IReadOnlyList<ExtractedFact>        Facts,
    IReadOnlyList<ExtractedTypeRelation> TypeRelations,
    IReadOnlyList<Core.Models.ProjectDiagnostic>? ProjectDiagnostics = null);

/// <summary>Returned by IEngineBaselineBuilder.BuildAsync.</summary>
internal sealed record BaselineBuildResult(
    string   CommitSha,
    string   BaselinePath,
    TimeSpan Elapsed,
    int      SymbolCount,
    int      EdgeCount,
    int      FactCount,
    int      FileCount,
    bool     Success,
    string?  ErrorMessage = null);
