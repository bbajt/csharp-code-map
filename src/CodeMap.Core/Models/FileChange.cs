namespace CodeMap.Core.Models;

/// <summary>
/// Represents a changed file in the working tree relative to a baseline commit.
/// </summary>
public record FileChange(
    Types.FilePath FilePath,
    FileChangeKind Kind
);

/// <summary>
/// The nature of a file change.
/// </summary>
public enum FileChangeKind
{
    /// <summary>File was added (new file not in baseline).</summary>
    Added,

    /// <summary>File was modified (exists in both baseline and working tree).</summary>
    Modified,

    /// <summary>File was deleted (in baseline but not in working tree).</summary>
    Deleted,

    /// <summary>File was renamed (path changed between baseline and working tree).</summary>
    Renamed,
}
