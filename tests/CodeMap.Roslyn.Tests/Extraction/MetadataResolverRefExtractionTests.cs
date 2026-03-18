namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

public class MetadataResolverRefExtractionTests
{
    private static readonly RepoId Repo = RepoId.From("test-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('a', 40));
    private static readonly SymbolId SymId = SymbolId.From("T:System.String");

    // ─── TryDecompileTypeAsync: already Level 2 — InsertVirtualFileAsync NOT called ───

    [Fact]
    public async Task TryDecompileTypeAsync_AlreadyLevel2_DoesNotCallInsertVirtualFile()
    {
        var store = Substitute.For<ISymbolStore>();
        var existingCard = SymbolCard.CreateMinimal(
            SymId, "System.String", Core.Enums.SymbolKind.Class, "public sealed class String", "System",
            FilePath.From("decompiled/System.Runtime/System/String.cs"), 0, 0, "public", Core.Enums.Confidence.High)
            with { IsDecompiled = 2 };
        store.GetSymbolAsync(Repo, Sha, SymId, Arg.Any<CancellationToken>()).Returns(existingCard);

        var resolver = new MetadataResolver(null!, store, NullLogger<MetadataResolver>.Instance);
        await resolver.TryDecompileTypeAsync(SymId, Repo, Sha);

        // InsertVirtualFileAsync should NOT be called — Level 2 short-circuit returns early
        await store.DidNotReceive().InsertVirtualFileAsync(
            Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<ExtractedReference>?>(), Arg.Any<CancellationToken>());
    }

    // ─── TryDecompileTypeAsync: null compiler → returns null, no store writes ──

    [Fact]
    public async Task TryDecompileTypeAsync_NullStoredCardAndNullCompiler_ThrowsNullRef()
    {
        var store = Substitute.For<ISymbolStore>();
        store.GetSymbolAsync(Arg.Any<RepoId>(), Arg.Any<CommitSha>(), Arg.Any<SymbolId>(),
            Arg.Any<CancellationToken>()).Returns((SymbolCard?)null);

        var resolver = new MetadataResolver(null!, store, NullLogger<MetadataResolver>.Instance);
        var act = async () => await resolver.TryDecompileTypeAsync(SymId, Repo, Sha);

        await act.Should().ThrowAsync<NullReferenceException>();
    }

    // ─── FqnToMetadataName: type and member prefix stripping ─────────────────

    [Theory]
    [InlineData("T:System.String", "System.String")]
    [InlineData("M:System.String.Format(System.String)", "System.String")]
    [InlineData("P:System.String.Length", "System.String")]
    [InlineData("T:System.Collections.Generic.List`1", "System.Collections.Generic.List`1")]
    public void FqnToMetadataName_VariousPrefixes_ExtractsTypeName(string fqn, string expected)
    {
        MetadataResolver.FqnToMetadataName(fqn).Should().Be(expected);
    }
}
