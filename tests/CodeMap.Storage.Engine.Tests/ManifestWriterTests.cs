namespace CodeMap.Storage.Engine.Tests;

using FluentAssertions;
using Xunit;

public sealed class ManifestWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-manifest-test-{Guid.NewGuid():N}");

    public ManifestWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string ManifestPath => Path.Combine(_tempDir, "manifest.json");

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var original = new BaselineManifest(
            FormatMajor: 2,
            FormatMinor: 0,
            CommitSha: "abcdef0123456789abcdef0123456789abcdef01",
            CreatedAt: new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
            SymbolCount: 100,
            FileCount: 20,
            ProjectCount: 3,
            EdgeCount: 500,
            FactCount: 42,
            NStringIds: 1234,
            Segments: new Dictionary<string, SegmentInfo>
            {
                ["dictionary"] = new("dictionary.seg", "AABBCCDD"),
                ["symbols"] = new("symbols.seg", "11223344"),
            });

        ManifestWriter.Write(ManifestPath, original);
        var loaded = ManifestWriter.Read(ManifestPath);

        loaded.Should().NotBeNull();
        loaded!.FormatMajor.Should().Be(2);
        loaded.FormatMinor.Should().Be(0);
        loaded.CommitSha.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
        loaded.SymbolCount.Should().Be(100);
        loaded.FileCount.Should().Be(20);
        loaded.ProjectCount.Should().Be(3);
        loaded.EdgeCount.Should().Be(500);
        loaded.FactCount.Should().Be(42);
        loaded.NStringIds.Should().Be(1234);
        loaded.Segments.Should().ContainKey("dictionary");
        loaded.Segments["dictionary"].File.Should().Be("dictionary.seg");
        loaded.Segments["dictionary"].Crc32Hex.Should().Be("AABBCCDD");
        loaded.Segments["symbols"].Crc32Hex.Should().Be("11223344");
    }

    [Fact]
    public void WriteRead_ProjectDiagnosticsWithOptionalFields_RoundTrips()
    {
        // F13 (PHASE-21-07 T01): the manifest kept only name/compiled/counts, so a degradation the
        // build detected (e.g. F10 generator failures) was lost and every query reported Full.
        var original = Manifest(
        [
            new CodeMap.Core.Models.ProjectDiagnostic("Web", true, 10, 20,
                Errors: ["CS0246: The type or namespace name 'Xunit' could not be found"],
                TargetFrameworks: ["net10.0", "net9.0"],
                GeneratorLoadFailures: ["Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)"],
                MissingRestoreOutput: "src/Web/obj/project.assets.json"),
            new CodeMap.Core.Models.ProjectDiagnostic("Core", true, 5, 6),
        ]);

        ManifestWriter.Write(ManifestPath, original);
        var diags = ManifestWriter.Read(ManifestPath)!.ProjectDiagnostics!;

        diags.Should().HaveCount(2);
        diags[0].Errors.Should().Equal("CS0246: The type or namespace name 'Xunit' could not be found");
        diags[0].TargetFrameworks.Should().Equal("net10.0", "net9.0");
        diags[0].GeneratorLoadFailures.Should().Equal(
            "Microsoft.CodeAnalysis.Razor.Compiler: ReferencesNewerCompiler (built for Roslyn 5.9.0.0, CodeMap runs 5.3.0.0)");
        diags[0].MissingRestoreOutput.Should().Be("src/Web/obj/project.assets.json");
        diags[1].MissingRestoreOutput.Should().BeNull();
        diags[1].Errors.Should().BeNull();
        diags[1].TargetFrameworks.Should().BeNull();
        diags[1].GeneratorLoadFailures.Should().BeNull();
    }

    [Fact]
    public void Read_ManifestWithoutOptionalFields_ReadsAsNull()
    {
        // A manifest written before PHASE-21-07 (only the four original diagnostic fields).
        File.WriteAllText(ManifestPath, """
            {
              "format_major": 2, "format_minor": 0, "engine": "custom",
              "commit_sha": "abcdef0123456789abcdef0123456789abcdef01",
              "created_at_utc": "2026-09-01T00:00:00.0000000+00:00",
              "symbol_count": 1, "file_count": 1, "project_count": 1, "edge_count": 0,
              "fact_count": 0, "n_string_ids": 1,
              "project_diagnostics": [ { "project_name": "Old", "compiled": true, "symbol_count": 1, "reference_count": 0 } ],
              "segments": {}
            }
            """);

        var d = ManifestWriter.Read(ManifestPath)!.ProjectDiagnostics!.Single();

        d.ProjectName.Should().Be("Old");
        d.Compiled.Should().BeTrue();
        d.Errors.Should().BeNull();
        d.TargetFrameworks.Should().BeNull();
        d.GeneratorLoadFailures.Should().BeNull();
    }

    private static BaselineManifest Manifest(IReadOnlyList<CodeMap.Core.Models.ProjectDiagnostic> diagnostics) =>
        new(2, 0, "abcdef0123456789abcdef0123456789abcdef01", DateTimeOffset.UtcNow,
            1, 1, diagnostics.Count, 0, 0, 1, new Dictionary<string, SegmentInfo>(),
            RepoRootPath: null, ProjectDiagnostics: diagnostics);

    [Fact]
    public void Read_NonexistentFile_ReturnsNull()
    {
        ManifestWriter.Read(Path.Combine(_tempDir, "nope.json")).Should().BeNull();
    }

    [Fact]
    public void Write_ProducesValidJson()
    {
        var manifest = new BaselineManifest(
            2, 0, "a" + new string('0', 39),
            DateTimeOffset.UtcNow, 1, 1, 1, 1, 1, 10,
            new Dictionary<string, SegmentInfo>());

        ManifestWriter.Write(ManifestPath, manifest);

        var json = File.ReadAllText(ManifestPath);
        json.Should().Contain("format_major");
        json.Should().Contain("commit_sha");
        json.Should().Contain("\"engine\"");
        json.Should().Contain("\"custom\"");
    }
}
