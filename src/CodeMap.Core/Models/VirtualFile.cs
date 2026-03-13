namespace CodeMap.Core.Models;

/// <summary>
/// An in-memory virtual file used for ephemeral workspace overlays.
/// Allows agents to query against unsaved edits.
/// </summary>
public record VirtualFile(
    Types.FilePath FilePath,
    string Content
);
