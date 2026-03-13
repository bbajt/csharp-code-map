namespace CodeMap.Roslyn.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using NSubstitute;

public sealed class SymbolDifferTests
{
    private readonly ISymbolStore _baseline = Substitute.For<ISymbolStore>();
    private readonly SymbolDiffer _differ;

    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));

    public SymbolDifferTests()
    {
        _differ = new SymbolDiffer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SymbolDiffer>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolCard MakeSymbol(string id, string filePath = "src/Foo.cs")
        => SymbolCard.CreateMinimal(
            SymbolId.From(id), id, SymbolKind.Class,
            "public class X", "NS", FilePath.From(filePath),
            1, 10, "public", Confidence.High);

    private void SetupBaseline(FilePath file, params SymbolCard[] symbols)
        => _baseline
            .GetSymbolsByFileAsync(Repo, Sha, file, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SymbolCard>>(symbols));

    // ── Delta computation tests ───────────────────────────────────────────────

    [Fact]
    public async Task Diff_NewSymbolNotInBaseline_AppearsInAdded()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file); // empty baseline

        var newSymbol = MakeSymbol("T:NS.NewClass");
        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [newSymbol], [], [], 0);

        delta.AddedOrUpdatedSymbols.Should().Contain(newSymbol);
        delta.DeletedSymbolIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Diff_SymbolInBaselineButNotNew_AppearsInDeleted()
    {
        var file = FilePath.From("src/Foo.cs");
        var baselineSymbol = MakeSymbol("T:NS.OldClass");
        SetupBaseline(file, baselineSymbol);

        // New extraction has no symbols (method was removed)
        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [], [], [], 0);

        delta.DeletedSymbolIds.Should().Contain(SymbolId.From("T:NS.OldClass"));
        delta.AddedOrUpdatedSymbols.Should().BeEmpty();
    }

    [Fact]
    public async Task Diff_SymbolInBothBaselineAndNew_AppearsInAdded()
    {
        var file = FilePath.From("src/Foo.cs");
        var symbol = MakeSymbol("T:NS.Foo");
        SetupBaseline(file, symbol); // in baseline

        var updatedSymbol = MakeSymbol("T:NS.Foo"); // still exists (same id)
        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [updatedSymbol], [], [], 0);

        delta.AddedOrUpdatedSymbols.Should().Contain(updatedSymbol);
        delta.DeletedSymbolIds.Should().NotContain(SymbolId.From("T:NS.Foo"));
    }

    [Fact]
    public async Task Diff_NoChanges_EmptyDeltaSymbols()
    {
        var file = FilePath.From("src/Foo.cs");
        var symbol = MakeSymbol("T:NS.Foo");
        SetupBaseline(file, symbol);

        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [symbol], [], [], 0);

        delta.DeletedSymbolIds.Should().BeEmpty();
        delta.AddedOrUpdatedSymbols.Should().HaveCount(1);
    }

    [Fact]
    public async Task Diff_MultipleFiles_AggregatesCorrectly()
    {
        var fileA = FilePath.From("src/A.cs");
        var fileB = FilePath.From("src/B.cs");
        SetupBaseline(fileA, MakeSymbol("T:NS.A", "src/A.cs"));
        SetupBaseline(fileB, MakeSymbol("T:NS.B", "src/B.cs"));

        // Only A is re-extracted; B's symbol is removed
        var newSymbolA = MakeSymbol("T:NS.A", "src/A.cs");
        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [fileA, fileB], [newSymbolA], [], [], 0);

        delta.AddedOrUpdatedSymbols.Should().HaveCount(1);
        delta.DeletedSymbolIds.Should().Contain(SymbolId.From("T:NS.B"));
    }

    [Fact]
    public async Task Diff_BaselineEmpty_AllSymbolsAreNew()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file); // empty

        var newSymbols = new[]
        {
            MakeSymbol("T:NS.X"),
            MakeSymbol("T:NS.Y"),
        };

        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], newSymbols, [], [], 0);

        delta.AddedOrUpdatedSymbols.Should().HaveCount(2);
        delta.DeletedSymbolIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Diff_NewEmpty_AllBaselineSymbolsDeleted()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file, MakeSymbol("T:NS.A"), MakeSymbol("T:NS.B"));

        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [], [], [], 0);

        delta.DeletedSymbolIds.Should().HaveCount(2);
        delta.AddedOrUpdatedSymbols.Should().BeEmpty();
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_RevisionIsCurrentPlusOne()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file);

        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [], [], [], currentRevision: 5);

        delta.NewRevision.Should().Be(6);
    }

    [Fact]
    public async Task Diff_DeltaIncludesReindexedFiles()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file);

        var newFile = new ExtractedFile("aabbccdd11223344", file, new string('a', 64), "MyProject");
        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [], [], [newFile], 0);

        delta.ReindexedFiles.Should().Contain(newFile);
    }

    [Fact]
    public async Task Diff_DeletedReferenceFilesMatchChangedFiles()
    {
        var file = FilePath.From("src/Foo.cs");
        SetupBaseline(file);

        var delta = await _differ.ComputeDeltaAsync(
            _baseline, Repo, Sha,
            [file], [], [], [], 0);

        delta.DeletedReferenceFiles.Should().Contain(file);
    }
}
