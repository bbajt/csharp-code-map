namespace CodeMap.Query.Tests;

using System.Text.Json;
using FluentAssertions;

public class TokenSavingsTrackerPersistenceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TokenSavingsTrackerPersistenceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveToDisk_CreatesFile()
    {
        var tracker = new TokenSavingsTracker(_tempDir);
        tracker.RecordSaving(100, new Dictionary<string, decimal> { ["claude_sonnet"] = 0.3m });

        tracker.SaveToDisk();

        File.Exists(Path.Combine(_tempDir, "_savings.json")).Should().BeTrue();
    }

    [Fact]
    public void SaveToDisk_WritesCorrectContent()
    {
        var tracker = new TokenSavingsTracker(_tempDir);
        tracker.RecordSaving(500, new Dictionary<string, decimal> { ["claude_sonnet"] = 1.5m });

        tracker.SaveToDisk();

        var json = File.ReadAllText(Path.Combine(_tempDir, "_savings.json"));
        json.Should().Contain("500");
        json.Should().Contain("1.5");
    }

    [Fact]
    public void LoadFromDisk_RestoresTotals()
    {
        // Arrange: save some totals first
        var tracker1 = new TokenSavingsTracker(_tempDir);
        tracker1.RecordSaving(1000, new Dictionary<string, decimal> { ["claude_sonnet"] = 3.0m });
        tracker1.SaveToDisk();

        // Act: create new tracker pointing at same dir
        var tracker2 = new TokenSavingsTracker(_tempDir);

        // Assert
        tracker2.TotalTokensSaved.Should().Be(1000);
        tracker2.TotalCostAvoided["claude_sonnet"].Should().Be(3.0m);
    }

    [Fact]
    public void LoadFromDisk_FileNotExists_StartsFromZero()
    {
        var tracker = new TokenSavingsTracker(_tempDir); // no _savings.json yet

        tracker.TotalTokensSaved.Should().Be(0);
        tracker.TotalCostAvoided.Should().BeEmpty();
    }

    [Fact]
    public void LoadFromDisk_CorruptFile_StartsFromZero()
    {
        File.WriteAllText(Path.Combine(_tempDir, "_savings.json"), "not-valid-json{{{{");

        var act = () => new TokenSavingsTracker(_tempDir);
        act.Should().NotThrow();

        var tracker = new TokenSavingsTracker(_tempDir);
        tracker.TotalTokensSaved.Should().Be(0);
    }

    [Fact]
    public void SaveToDisk_NullDir_NoOp()
    {
        var tracker = new TokenSavingsTracker(codeMapDir: null);
        tracker.RecordSaving(100, new Dictionary<string, decimal>());

        var act = () => tracker.SaveToDisk();
        act.Should().NotThrow();
    }

    [Fact]
    public void RoundTrip_SaveThenLoad_Consistent()
    {
        var original = new TokenSavingsTracker(_tempDir);
        original.RecordSaving(250, new Dictionary<string, decimal> { ["claude_sonnet"] = 0.75m });
        original.RecordSaving(250, new Dictionary<string, decimal> { ["claude_sonnet"] = 0.75m });
        original.SaveToDisk();

        var restored = new TokenSavingsTracker(_tempDir);

        restored.TotalTokensSaved.Should().Be(500);
        restored.TotalCostAvoided["claude_sonnet"].Should().BeApproximately(1.5m, 0.001m);
    }
}
