namespace CodeMap.Storage.Engine;

using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Core.Models;

/// <summary>
/// Serializes and deserializes manifest.json per STORAGE-FORMAT.MD §14.1.
/// Written last — its presence marks a baseline as complete.
/// </summary>
internal static class ManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string path, BaselineManifest manifest)
    {
        var dto = new ManifestDto
        {
            FormatMajor = manifest.FormatMajor,
            FormatMinor = manifest.FormatMinor,
            Engine = "custom",
            CommitSha = manifest.CommitSha,
            CreatedAtUtc = manifest.CreatedAt.ToString("O"),
            SymbolCount = manifest.SymbolCount,
            FileCount = manifest.FileCount,
            ProjectCount = manifest.ProjectCount,
            EdgeCount = manifest.EdgeCount,
            FactCount = manifest.FactCount,
            NStringIds = manifest.NStringIds,
            RepoRootPath = manifest.RepoRootPath,
            ProjectDiagnostics = manifest.ProjectDiagnostics?.Select(d => new ProjectDiagnosticDto
            {
                ProjectName = d.ProjectName, Compiled = d.Compiled,
                SymbolCount = d.SymbolCount, ReferenceCount = d.ReferenceCount,
            }).ToList(),
            Segments = manifest.Segments.ToDictionary(
                kv => kv.Key,
                kv => new SegmentInfoDto { File = kv.Value.File, Crc32 = kv.Value.Crc32Hex }),
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static BaselineManifest? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        if (dto is null) return null;

        return new BaselineManifest(
            dto.FormatMajor,
            dto.FormatMinor,
            dto.CommitSha ?? "",
            DateTimeOffset.TryParse(dto.CreatedAtUtc, out var ts) ? ts : DateTimeOffset.UtcNow,
            dto.SymbolCount,
            dto.FileCount,
            dto.ProjectCount,
            dto.EdgeCount,
            dto.FactCount,
            dto.NStringIds,
            dto.Segments?.ToDictionary(
                kv => kv.Key,
                kv => new SegmentInfo(kv.Value.File ?? "", kv.Value.Crc32 ?? ""))
            ?? new Dictionary<string, SegmentInfo>(),
            dto.RepoRootPath,
            dto.ProjectDiagnostics?.Select(d => new ProjectDiagnostic(
                d.ProjectName ?? "", d.Compiled, d.SymbolCount, d.ReferenceCount)).ToList());
    }

    private sealed class ManifestDto
    {
        public int FormatMajor { get; set; }
        public int FormatMinor { get; set; }
        public string? Engine { get; set; }
        public string? CommitSha { get; set; }
        public string? CreatedAtUtc { get; set; }
        public int SymbolCount { get; set; }
        public int FileCount { get; set; }
        public int ProjectCount { get; set; }
        public int EdgeCount { get; set; }
        public int FactCount { get; set; }
        public int NStringIds { get; set; }
        public string? RepoRootPath { get; set; }
        public List<ProjectDiagnosticDto>? ProjectDiagnostics { get; set; }
        public Dictionary<string, SegmentInfoDto>? Segments { get; set; }
    }

    private sealed class SegmentInfoDto
    {
        public string? File { get; set; }
        public string? Crc32 { get; set; }
    }

    private sealed class ProjectDiagnosticDto
    {
        public string? ProjectName { get; set; }
        public bool Compiled { get; set; }
        public int SymbolCount { get; set; }
        public int ReferenceCount { get; set; }
    }
}
