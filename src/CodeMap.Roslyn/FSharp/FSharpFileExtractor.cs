namespace CodeMap.Roslyn.FSharp;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Extracts file metadata from F# source files (no Roslyn Project needed).
/// </summary>
internal static class FSharpFileExtractor
{
    public static IReadOnlyList<ExtractedFile> ExtractFiles(
        IReadOnlyList<string> sourceFiles,
        string projectName,
        string solutionDir)
    {
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';
        var files = new List<ExtractedFile>();

        foreach (var fsFile in sourceFiles)
        {
            try
            {
                var content = File.ReadAllText(fsFile);
                var relative = FSharpSymbolMapper.MakeRepoRelative(fsFile, normalizedDir);
                var sha256 = ComputeSha256(content);

                files.Add(new ExtractedFile(
                    FileId: sha256[..16],
                    Path: FilePath.From(relative),
                    Sha256Hash: sha256,
                    ProjectName: projectName,
                    Content: content));
            }
            catch { /* skip unreadable files */ }
        }

        return files;
    }

    private static string ComputeSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
