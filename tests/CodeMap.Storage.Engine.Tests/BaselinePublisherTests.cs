namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="BaselinePublisher"/> — completeness checks and the
/// non-destructive publish algorithm (PHASE-21-02 T01, ADR-040).
/// </summary>
public sealed class BaselinePublisherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-publisher-{Guid.NewGuid():N}");
    private string _template = "";

    private string Final => Path.Combine(_root, "repo", "baselines", "sha");

    private string Quarantine => Path.Combine(_root, "repo", "temp");

    /// <summary>Builds one real baseline to copy from.</summary>
    public async ValueTask InitializeAsync()
    {
        var result = await new EngineBaselineBuilder(Path.Combine(_root, "template-store"))
            .BuildAsync(Input(), CancellationToken.None);
        result.Success.Should().BeTrue(result.ErrorMessage);
        _template = result.BaselinePath;
    }

    /// <summary>Removes the temp root.</summary>
    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    // ── IsComplete ────────────────────────────────────────────────────────────

    [Fact]
    public void IsComplete_AllRequiredFilesAndValidManifest_True() =>
        BaselinePublisher.IsComplete(_template).Should().BeTrue();

    [Fact]
    public void RequiredFiles_MatchWhatTheBuilderWrites() =>
        Directory.GetFiles(_template).Select(Path.GetFileName).Should().BeEquivalentTo(BaselinePublisher.RequiredFiles);

    public static TheoryData<string> RequiredFileNames() => [.. BaselinePublisher.RequiredFiles];

    [Theory]
    [MemberData(nameof(RequiredFileNames))]
    public void IsComplete_MissingAnyRequiredFile_False(string file)
    {
        var dir = Staged("missing");
        File.Delete(Path.Combine(dir, file));

        BaselinePublisher.IsComplete(dir).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_CorruptManifest_False()
    {
        var dir = Staged("corrupt");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ not json");

        BaselinePublisher.IsComplete(dir).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_MissingDir_False() =>
        BaselinePublisher.IsComplete(Path.Combine(_root, "nope")).Should().BeFalse();

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]
    public void Publish_FinalAbsent_MovesStaging_ReturnsPublished()
    {
        var staging = Staged("s1");

        var outcome = BaselinePublisher.Publish(staging, Final, Quarantine);

        outcome.Should().Be(PublishOutcome.Published);
        BaselinePublisher.IsComplete(Final).Should().BeTrue();
        Directory.Exists(staging).Should().BeFalse();
    }

    [Fact]
    public void Publish_FinalComplete_ReturnsAdopted_StagingDeleted_FinalByteIdentical()
    {
        BaselinePublisher.Publish(Staged("s1"), Final, Quarantine);
        var before = Snapshot(Final);
        var staging = Staged("s2");
        File.AppendAllText(Path.Combine(staging, "facts.seg"), "different");

        var outcome = BaselinePublisher.Publish(staging, Final, Quarantine);

        outcome.Should().Be(PublishOutcome.AdoptedExisting);
        Directory.Exists(staging).Should().BeFalse();
        Snapshot(Final).Should().Equal(before);
    }

    [Fact]
    public void Publish_FinalIncomplete_QuarantinesAndPublishes()
    {
        BaselinePublisher.Publish(Staged("s1"), Final, Quarantine);
        File.Delete(Path.Combine(Final, "search.idx"));
        File.WriteAllText(Path.Combine(Final, "marker.txt"), "old");

        var outcome = BaselinePublisher.Publish(Staged("s2"), Final, Quarantine);

        outcome.Should().Be(PublishOutcome.Published);
        BaselinePublisher.IsComplete(Final).Should().BeTrue();
        File.Exists(Path.Combine(Final, "marker.txt")).Should().BeFalse();
        var quarantined = Directory.GetDirectories(Quarantine, BaselinePublisher.QuarantinePrefix + "*").Should().ContainSingle().Subject;
        File.ReadAllText(Path.Combine(quarantined, "marker.txt")).Should().Be("old");
    }

    [Fact]
    public void Publish_FinalIncompleteAndLocked_ThrowsStorageBusy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory rename is blocked by open handles only on Windows.");
        BaselinePublisher.Publish(Staged("s1"), Final, Quarantine);
        File.Delete(Path.Combine(Final, "search.idx"));
        var staging = Staged("s2");

        using (new FileStream(Path.Combine(Final, "symbols.seg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => BaselinePublisher.Publish(staging, Final, Quarantine);
            act.Should().Throw<StorageBusyException>().WithMessage("*in use by another process*");
        }
    }

    [Fact]
    public async Task Publish_ManyConcurrentPublishers_ExactlyOnePublished_RestAdopted()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            if (Directory.Exists(Final)) Directory.Delete(Final, recursive: true);
            var stagings = Enumerable.Range(0, 16).Select(i => Staged($"it{iteration}-{i}")).ToList();
            using var start = new ManualResetEventSlim();

            var tasks = stagings.Select(s => Task.Run(() =>
            {
                start.Wait();
                return BaselinePublisher.Publish(s, Final, Quarantine);
            })).ToList();
            start.Set();
            var outcomes = await Task.WhenAll(tasks);

            outcomes.Count(o => o == PublishOutcome.Published).Should().Be(1, $"iteration {iteration}");
            outcomes.Count(o => o == PublishOutcome.AdoptedExisting).Should().Be(15);
            BaselinePublisher.IsComplete(Final).Should().BeTrue();
            stagings.Should().OnlyContain(s => !Directory.Exists(s));
        }
    }

    [Fact]
    public void SweepQuarantine_DeletesQuarantinedDirs_LeavesOthers()
    {
        Directory.CreateDirectory(Path.Combine(Quarantine, BaselinePublisher.QuarantinePrefix + "a"));
        Directory.CreateDirectory(Path.Combine(Quarantine, "build-inprogress"));

        BaselinePublisher.SweepQuarantine(Quarantine);

        Directory.GetDirectories(Quarantine).Select(Path.GetFileName).Should().Equal("build-inprogress");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Staged(string name)
    {
        var dir = Path.Combine(_root, "repo", "temp", "staging-" + name);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(_template))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        return dir;
    }

    private static List<string> Snapshot(string dir) =>
        Directory.GetFiles(dir).Order()
            .Select(f => $"{Path.GetFileName(f)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))}")
            .ToList();

    private static BaselineBuildInput Input()
    {
        var files = new List<ExtractedFile>
        {
            new("f1", FilePath.From("src/A.cs"), "aa" + new string('0', 62), "App", "public class A { }"),
        };
        var symbols = new List<SymbolCard>
        {
            SymbolCard.CreateMinimal(SymbolId.From("T:App.A"), "global::App.A", SymbolKind.Class,
                "public class A", "App", FilePath.From("src/A.cs"), 1, 1, "public", Confidence.High),
        };
        return new BaselineBuildInput(new string('c', 40), @"C:\repo", symbols, files, [], [], []);
    }
}
