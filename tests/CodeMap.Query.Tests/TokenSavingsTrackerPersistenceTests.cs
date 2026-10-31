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

    private static Dictionary<string, decimal> Cost(decimal v) => new() { ["claude_sonnet"] = v };

    private long DiskTotal() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "_savings.json")))
            .RootElement.GetProperty("tokens_saved_total").GetInt64();

    [Fact]
    public void ZeroSavingsInstance_Exit_DoesNotClobberOtherInstancesTotal()
    {
        var idle = new TokenSavingsTracker(_tempDir);   // started before the other recorded anything
        var busy = new TokenSavingsTracker(_tempDir);
        busy.RecordSaving(1000, Cost(3m));
        busy.SaveToDisk();

        idle.SaveToDisk();                               // exits with zero savings

        DiskTotal().Should().Be(1000);
    }

    [Fact]
    public void OverlappingInstances_SavesAreSummed_NotOverwritten()
    {
        var a = new TokenSavingsTracker(_tempDir);
        var b = new TokenSavingsTracker(_tempDir);
        a.RecordSaving(100, Cost(1m));
        b.RecordSaving(250, Cost(2m));

        a.SaveToDisk();
        b.SaveToDisk();

        DiskTotal().Should().Be(350);
        new TokenSavingsTracker(_tempDir).TotalCostAvoided["claude_sonnet"].Should().Be(3m);
        b.TotalTokensSaved.Should().Be(350);
    }

    [Fact]
    public void RepeatedSave_DoesNotDoubleCount()
    {
        var t = new TokenSavingsTracker(_tempDir);
        t.RecordSaving(100, Cost(1m));
        t.SaveToDisk();
        t.SaveToDisk();
        t.RecordSaving(50, Cost(1m));
        t.SaveToDisk();

        DiskTotal().Should().Be(150);
        t.TotalTokensSaved.Should().Be(150);
    }

    [Fact]
    public void ConcurrentInstances_NoLostUpdates()
    {
        const int instances = 8, perInstance = 25;
        Parallel.For(0, instances, _ =>
        {
            var t = new TokenSavingsTracker(_tempDir);
            for (var i = 0; i < perInstance; i++)
            {
                t.RecordSaving(10, Cost(0.1m));
                t.SaveToDisk();
            }
        });

        DiskTotal().Should().Be(instances * perInstance * 10);
    }

    [Fact]
    public void RecordSaving_PersistsPeriodically_WithoutExplicitSave()
    {
        var t = new TokenSavingsTracker(_tempDir, saveInterval: TimeSpan.Zero);

        t.RecordSaving(42, Cost(0.5m));

        DiskTotal().Should().Be(42);   // simulates a hard kill: no SaveToDisk call
    }

    [Fact]
    public void SaveToDisk_CorruptFile_RecoversAndKeepsBackup()
    {
        var path = Path.Combine(_tempDir, "_savings.json");
        File.WriteAllText(path, "not-valid-json{{{{");
        var t = new TokenSavingsTracker(_tempDir);
        t.RecordSaving(77, Cost(1m));

        t.SaveToDisk();

        DiskTotal().Should().Be(77);
        File.Exists(path + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public void SaveToDisk_RecordsDailyBreakdown_AndKeepsLegacyFields()
    {
        var t = new TokenSavingsTracker(_tempDir);
        t.RecordSaving(10, Cost(1m));
        t.RecordSaving(5, Cost(1m));
        t.SaveToDisk();

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "_savings.json"))).RootElement;
        root.TryGetProperty("cost_avoided_total", out _).Should().BeTrue();
        root.TryGetProperty("last_updated", out _).Should().BeTrue();
        root.GetProperty("daily_tokens_saved").EnumerateObject().Sum(p => p.Value.GetInt64()).Should().Be(15);
    }

    [Fact]
    public void SaveToDisk_LegacyFileWithoutDaily_StillLoads()
    {
        File.WriteAllText(Path.Combine(_tempDir, "_savings.json"),
            "{\"tokens_saved_total\": 900, \"cost_avoided_total\": {\"claude_sonnet\": 2.0}, \"last_updated\": \"2026-01-01T00:00:00+00:00\"}");

        var t = new TokenSavingsTracker(_tempDir);
        t.RecordSaving(100, Cost(1m));
        t.SaveToDisk();

        t.TotalTokensSaved.Should().Be(1000);
        DiskTotal().Should().Be(1000);
    }
}
