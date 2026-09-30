namespace CodeMap.Storage.Engine;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// <c>overlay.meta.json</c> in a workspace overlay directory records the baseline commit the workspace
/// is based on (PHASE-21-10). The in-memory workspace registry is per process, so this file is how
/// <c>index_cleanup</c> in one process sees the workspaces of every other process.
/// </summary>
internal static partial class OverlayMarker
{
    /// <summary>File name inside <c>store/&lt;repo&gt;/overlays/&lt;workspace&gt;/</c>.</summary>
    internal const string FileName = "overlay.meta.json";

    /// <summary>
    /// Writes the marker atomically (temp file + rename). Best effort: a failure leaves the overlay
    /// "without baseline record", which cleanup treats conservatively.
    /// </summary>
    internal static void Write(string overlayDir, string baselineCommitSha)
    {
        try
        {
            Directory.CreateDirectory(overlayDir);
            var json = new JsonObject
            {
                ["baseline_commit_sha"] = baselineCommitSha,
                ["created_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            }.ToJsonString();
            var temp = Path.Combine(overlayDir, $"{FileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, json);
            File.Move(temp, Path.Combine(overlayDir, FileName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Conservative fallback: see summary.
        }
    }

    /// <summary>The recorded baseline commit, or null when there is no (valid) marker.</summary>
    internal static string? TryReadBaseline(string overlayDir)
    {
        try
        {
            var path = Path.Combine(overlayDir, FileName);
            if (!File.Exists(path)) return null;
            var sha = JsonNode.Parse(File.ReadAllText(path))?["baseline_commit_sha"]?.GetValue<string>();
            return sha is not null && IsCommitSha(sha) ? sha : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for an overlay directory named by a commit SHA: the baseline-level overlay of that commit
    /// (edge upgrades, metadata stubs, decompiled sources), not a workspace.
    /// </summary>
    internal static bool IsBaselineLevel(string overlayName) => IsCommitSha(overlayName);

    private static bool IsCommitSha(string value) => CommitShaPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitShaPattern();
}
