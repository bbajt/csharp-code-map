namespace CodeMap.Query;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Resolves file content from an in-memory virtual file list.
/// Used by span queries in Ephemeral mode to return unsaved buffer contents
/// without reading from disk.
/// </summary>
public static class VirtualFileResolver
{
    /// <summary>
    /// Returns the full virtual content for the given file path, or null if not found.
    /// </summary>
    public static string? Resolve(FilePath filePath, IReadOnlyList<VirtualFile>? virtualFiles)
    {
        if (virtualFiles is null or { Count: 0 }) return null;
        return virtualFiles.FirstOrDefault(vf => vf.FilePath == filePath)?.Content;
    }

    /// <summary>
    /// Returns a specific line range from the virtual file, or null if the file is not virtual.
    /// Line numbers are 1-indexed (same convention as FileSpan).
    /// </summary>
    public static string? ResolveLines(
        FilePath filePath,
        IReadOnlyList<VirtualFile>? virtualFiles,
        int startLine,
        int endLine)
    {
        var content = Resolve(filePath, virtualFiles);
        if (content is null) return null;

        var lines = content.Split('\n');
        int start = Math.Max(0, startLine - 1);    // 1-indexed → 0-indexed
        int end = Math.Min(lines.Length - 1, endLine - 1);

        if (start >= lines.Length || start > end) return string.Empty;

        return string.Join('\n', lines[start..(end + 1)]);
    }

    /// <summary>
    /// Builds a <see cref="FileSpan"/> from virtual file content for the given line range.
    /// Returns null if the file is not in the virtual files list.
    /// </summary>
    public static FileSpan? BuildSpan(
        FilePath filePath,
        IReadOnlyList<VirtualFile>? virtualFiles,
        int startLine,
        int endLine)
    {
        var content = Resolve(filePath, virtualFiles);
        if (content is null) return null;

        var allLines = content.Split('\n');
        int totalLines = allLines.Length;
        int actualStart = Math.Max(1, startLine);
        int actualEnd = Math.Min(totalLines, endLine);

        if (actualStart > totalLines)
            return new FileSpan(filePath, startLine, endLine, totalLines, string.Empty, false);

        var lines = allLines[(actualStart - 1)..actualEnd];

        var spanContent = string.Join('\n',
            lines.Select((line, i) => $"{actualStart + i,5} | {line}"));

        return new FileSpan(
            filePath,
            actualStart,
            actualStart + lines.Length - 1,
            totalLines,
            spanContent,
            false);
    }
}
