namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Roslyn.Extraction;
using FluentAssertions;

public class MetadataResolverTests
{
    // ─── FqnToMetadataName ────────────────────────────────────────────────────

    [Theory]
    [InlineData("T:System.Collections.Generic.List`1", "System.Collections.Generic.List`1")]
    [InlineData("M:System.String.Format(System.String,System.Object)", "System.String")]
    [InlineData("P:System.Collections.Generic.List`1.Count", "System.Collections.Generic.List`1")]
    [InlineData("F:System.Int32.MaxValue", "System.Int32")]
    [InlineData("E:System.AppDomain.UnhandledException", "System.AppDomain")]
    public void FqnToMetadataName_WithDocCommentPrefix_ReturnsContainingTypeName(string fqn, string expected)
    {
        var result = MetadataResolver.FqnToMetadataName(fqn);
        result.Should().Be(expected);
    }

    [Fact]
    public void FqnToMetadataName_TypeWithoutPrefix_ReturnsAsIs()
    {
        var result = MetadataResolver.FqnToMetadataName("System.String");
        result.Should().Be("System.String");
    }

    [Fact]
    public void FqnToMetadataName_EmptyString_ReturnsNull()
    {
        var result = MetadataResolver.FqnToMetadataName(string.Empty);
        result.Should().BeNull();
    }

    [Fact]
    public void FqnToMetadataName_NullString_ReturnsNull()
    {
        var result = MetadataResolver.FqnToMetadataName(null!);
        result.Should().BeNull();
    }

    [Fact]
    public void FqnToMetadataName_MethodWithParamList_StripsParens()
    {
        // Method FQN has parameters — these must be stripped
        var result = MetadataResolver.FqnToMetadataName("M:MyNs.MyClass.MyMethod(System.Int32,System.String)");
        result.Should().Be("MyNs.MyClass");
    }

    [Fact]
    public void FqnToMetadataName_TypePrefixNoStrip_ReturnsSelf()
    {
        // T: prefix — the FQN IS the type, don't strip last segment
        var result = MetadataResolver.FqnToMetadataName("T:MyNs.MyClass");
        result.Should().Be("MyNs.MyClass");
    }
}
