namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Mcp.Handlers;
using FluentAssertions;

/// <summary>
/// <see cref="JsonArgs.GetBool(JsonObject?, string)"/>: JSON booleans and their string forms
/// (some MCP clients stringify parameters); anything else reads as absent (PHASE-21-13 T02).
/// </summary>
public sealed class JsonArgsTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"False\"", false)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\" false \"", false)]
    public void GetBool_BooleanOrItsString_ReturnsValue(string json, bool expected)
    {
        var args = JsonNode.Parse($"{{\"flag\": {json}}}")!.AsObject();

        args.GetBool("flag").Should().Be(expected);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    public void GetBool_NotABoolean_ReturnsNull(string json)
    {
        var args = JsonNode.Parse($"{{\"flag\": {json}}}")!.AsObject();

        args.GetBool("flag").Should().BeNull();
    }

    [Fact]
    public void GetBool_Absent_ReturnsNull()
    {
        new JsonObject().GetBool("flag").Should().BeNull();
        ((JsonObject?)null).GetBool("flag").Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetBool_WithDefault_UnparseableValue_GivesDefault(bool defaultValue)
    {
        var args = new JsonObject { ["flag"] = "yes" };

        args.GetBool("flag", defaultValue).Should().Be(defaultValue);
    }
}
