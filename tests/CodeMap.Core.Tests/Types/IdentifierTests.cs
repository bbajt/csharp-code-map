namespace CodeMap.Core.Tests.Types;

using CodeMap.Core.Types;
using FluentAssertions;

public sealed class IdentifierTests
{
    // ─── RepoId ───────────────────────────────────────────────────────────────

    [Fact]
    public void RepoId_From_ValidValue_CreatesInstance() =>
        RepoId.From("my-repo").Value.Should().Be("my-repo");

    [Fact]
    public void RepoId_From_NullValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => RepoId.From(null!)).Should().Throw<ArgumentException>();

    [Fact]
    public void RepoId_From_EmptyValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => RepoId.From("")).Should().Throw<ArgumentException>();

    [Fact]
    public void RepoId_From_WhitespaceValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => RepoId.From("   ")).Should().Throw<ArgumentException>();

    [Fact]
    public void RepoId_ToString_ReturnsValue() =>
        RepoId.From("my-repo").ToString().Should().Be("my-repo");

    [Fact]
    public void RepoId_ImplicitConversionToString_ReturnsValue() =>
        ((string)RepoId.From("my-repo")).Should().Be("my-repo");

    [Fact]
    public void RepoId_EqualityByValue_TwoIdenticalValues_AreEqual() =>
        RepoId.From("repo-1").Should().Be(RepoId.From("repo-1"));

    [Fact]
    public void RepoId_EqualityByValue_TwoDifferentValues_AreNotEqual() =>
        RepoId.From("repo-1").Should().NotBe(RepoId.From("repo-2"));

    // ─── WorkspaceId ──────────────────────────────────────────────────────────

    [Fact]
    public void WorkspaceId_From_ValidValue_CreatesInstance() =>
        WorkspaceId.From("agent-1").Value.Should().Be("agent-1");

    [Fact]
    public void WorkspaceId_From_NullValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => WorkspaceId.From(null!)).Should().Throw<ArgumentException>();

    [Fact]
    public void WorkspaceId_From_EmptyValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => WorkspaceId.From("")).Should().Throw<ArgumentException>();

    [Fact]
    public void WorkspaceId_From_WhitespaceValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => WorkspaceId.From("  ")).Should().Throw<ArgumentException>();

    [Fact]
    public void WorkspaceId_ToString_ReturnsValue() =>
        WorkspaceId.From("ws").ToString().Should().Be("ws");

    [Fact]
    public void WorkspaceId_ImplicitConversionToString_ReturnsValue() =>
        ((string)WorkspaceId.From("ws")).Should().Be("ws");

    [Fact]
    public void WorkspaceId_EqualityByValue_TwoIdenticalValues_AreEqual() =>
        WorkspaceId.From("ws-1").Should().Be(WorkspaceId.From("ws-1"));

    [Fact]
    public void WorkspaceId_EqualityByValue_TwoDifferentValues_AreNotEqual() =>
        WorkspaceId.From("ws-1").Should().NotBe(WorkspaceId.From("ws-2"));

    // ─── CommitSha ────────────────────────────────────────────────────────────

    private const string ValidSha = "aabbccddee00112233445566778899aabbccddee";

    [Fact]
    public void CommitSha_From_Exactly40HexChars_CreatesInstance() =>
        CommitSha.From(ValidSha).Value.Should().Be(ValidSha);

    [Fact]
    public void CommitSha_From_NullValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From(null!)).Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_EmptyValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From("")).Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_WhitespaceValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From("   ")).Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_39Chars_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From(ValidSha[..39])).Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_41Chars_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From(ValidSha + "a")).Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_UppercaseHex_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From("AABBCCDDEE00112233445566778899AABBCCDDEE"))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_From_NonHexChars_ThrowsArgumentException() =>
        FluentActions.Invoking(() => CommitSha.From("ggbbccddee00112233445566778899aabbccddee"))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void CommitSha_ToString_ReturnsValue() =>
        CommitSha.From(ValidSha).ToString().Should().Be(ValidSha);

    [Fact]
    public void CommitSha_ImplicitConversionToString_ReturnsValue() =>
        ((string)CommitSha.From(ValidSha)).Should().Be(ValidSha);

    [Fact]
    public void CommitSha_EqualityByValue_TwoIdenticalValues_AreEqual() =>
        CommitSha.From(ValidSha).Should().Be(CommitSha.From(ValidSha));

    // ─── SymbolId ─────────────────────────────────────────────────────────────

    [Fact]
    public void SymbolId_From_ValidValue_CreatesInstance() =>
        SymbolId.From("Ns.Class.Method").Value.Should().Be("Ns.Class.Method");

    [Fact]
    public void SymbolId_From_NullValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => SymbolId.From(null!)).Should().Throw<ArgumentException>();

    [Fact]
    public void SymbolId_From_EmptyValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => SymbolId.From("")).Should().Throw<ArgumentException>();

    [Fact]
    public void SymbolId_From_WhitespaceValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => SymbolId.From(" ")).Should().Throw<ArgumentException>();

    [Fact]
    public void SymbolId_ToString_ReturnsValue() =>
        SymbolId.From("X.Y").ToString().Should().Be("X.Y");

    [Fact]
    public void SymbolId_ImplicitConversionToString_ReturnsValue() =>
        ((string)SymbolId.From("X.Y")).Should().Be("X.Y");

    [Fact]
    public void SymbolId_EqualityByValue_TwoIdenticalValues_AreEqual() =>
        SymbolId.From("A.B").Should().Be(SymbolId.From("A.B"));

    [Fact]
    public void SymbolId_EqualityByValue_TwoDifferentValues_AreNotEqual() =>
        SymbolId.From("A.B").Should().NotBe(SymbolId.From("A.C"));

    // ─── FilePath ─────────────────────────────────────────────────────────────

    [Fact]
    public void FilePath_From_ForwardSlashPath_CreatesInstance() =>
        FilePath.From("src/Foo.cs").Value.Should().Be("src/Foo.cs");

    [Fact]
    public void FilePath_From_NullValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => FilePath.From(null!)).Should().Throw<ArgumentException>();

    [Fact]
    public void FilePath_From_EmptyValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => FilePath.From("")).Should().Throw<ArgumentException>();

    [Fact]
    public void FilePath_From_WhitespaceValue_ThrowsArgumentException() =>
        FluentActions.Invoking(() => FilePath.From(" ")).Should().Throw<ArgumentException>();

    [Fact]
    public void FilePath_From_BackslashPath_ThrowsArgumentException() =>
        FluentActions.Invoking(() => FilePath.From("src\\Foo.cs")).Should().Throw<ArgumentException>();

    [Fact]
    public void FilePath_From_LeadingSlash_ThrowsArgumentException() =>
        FluentActions.Invoking(() => FilePath.From("/src/Foo.cs")).Should().Throw<ArgumentException>();

    [Fact]
    public void FilePath_From_NestedPath_CreatesInstance() =>
        FilePath.From("src/Services/OrderService.cs").Value
            .Should().Be("src/Services/OrderService.cs");

    [Fact]
    public void FilePath_ToString_ReturnsValue() =>
        FilePath.From("a/b.cs").ToString().Should().Be("a/b.cs");

    [Fact]
    public void FilePath_ImplicitConversionToString_ReturnsValue() =>
        ((string)FilePath.From("a/b.cs")).Should().Be("a/b.cs");

    [Fact]
    public void FilePath_EqualityByValue_TwoIdenticalValues_AreEqual() =>
        FilePath.From("src/A.cs").Should().Be(FilePath.From("src/A.cs"));

    [Fact]
    public void FilePath_EqualityByValue_TwoDifferentValues_AreNotEqual() =>
        FilePath.From("src/A.cs").Should().NotBe(FilePath.From("src/B.cs"));
}
