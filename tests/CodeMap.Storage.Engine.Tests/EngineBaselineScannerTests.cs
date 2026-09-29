namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// <see cref="EngineBaselineScanner"/> — legacy overlay sweep added with repo-scoped overlays
/// (PHASE-21-03 T02, ADR-043): <c>index.cleanup</c> removes the pre-v2.8.2 flat
/// <c>store/overlays/&lt;ws&gt;/</c> tree, skipping any overlay a running daemon still holds.
/// </summary>
public sealed class EngineBaselineScannerTests : IDisposable
{
    private readonly string _storeBaseDir = Path.Combine(Path.GetTempPath(), $"codemap-scanner-{Guid.NewGuid():N}");
    private readonly EngineBaselineScanner _scanner;

    private static readonly RepoId Repo = RepoId.From("repo-a");
    private static readonly CommitSha Head = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    public EngineBaselineScannerTests()
    {
        Directory.CreateDirectory(_storeBaseDir);
        _scanner = new EngineBaselineScanner(_storeBaseDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_storeBaseDir)) Directory.Delete(_storeBaseDir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task CleanupBaselinesAsync_NotDryRun_RemovesUnheldLegacyOverlays()
    {
        CreateOverlayDir(LegacyDir("session"));
        CreateOverlayDir(LegacyDir("other"));

        await CleanupAsync(dryRun: false);

        Directory.Exists(Path.Combine(_storeBaseDir, "overlays")).Should().BeFalse(
            "every legacy overlay was unheld, so the whole flat tree goes");
    }

    [Fact]
    public async Task CleanupBaselinesAsync_LegacyOverlayWalHeld_IsSkipped()
    {
        var held = LegacyDir("held");
        CreateOverlayDir(held);
        CreateOverlayDir(LegacyDir("free"));

        // Stand-in for a running pre-v2.8.2 daemon holding its overlay WAL.
        using (new FileStream(Path.Combine(held, "overlay.wal"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await CleanupAsync(dryRun: false);
        }

        Directory.Exists(held).Should().BeTrue("a held overlay belongs to a live daemon");
        File.Exists(Path.Combine(held, "overlay.wal")).Should().BeTrue();
        Directory.Exists(LegacyDir("free")).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupBaselinesAsync_DryRun_LeavesLegacyOverlays()
    {
        CreateOverlayDir(LegacyDir("session"));

        await CleanupAsync(dryRun: true);

        Directory.Exists(LegacyDir("session")).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupBaselinesAsync_NeverTouchesRepoScopedOverlays()
    {
        var scoped = Path.Combine(_storeBaseDir, Repo.Value, "overlays", "session");
        CreateOverlayDir(scoped);
        CreateOverlayDir(LegacyDir("session"));

        await CleanupAsync(dryRun: false);

        File.Exists(Path.Combine(scoped, "overlay.wal")).Should().BeTrue();
    }

    private string LegacyDir(string workspaceId) => Path.Combine(_storeBaseDir, "overlays", workspaceId);

    private static void CreateOverlayDir(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "overlay.wal"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
    }

    private Task CleanupAsync(bool dryRun)
        => _scanner.CleanupBaselinesAsync(Repo, Head, new HashSet<CommitSha>(), dryRun: dryRun);
}
