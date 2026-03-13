namespace CodeMap.Core.Tests.Enums;

using CodeMap.Core.Enums;
using FluentAssertions;

public sealed class EnumTests
{
    [Fact]
    public void ConsistencyMode_HasExpectedValues()
    {
        var values = Enum.GetNames<ConsistencyMode>();
        values.Should().Contain("Committed");
        values.Should().Contain("Workspace");
        values.Should().Contain("Ephemeral");
    }

    [Fact]
    public void Confidence_HasExpectedValues()
    {
        var values = Enum.GetNames<Confidence>();
        values.Should().Contain("High");
        values.Should().Contain("Medium");
        values.Should().Contain("Low");
    }

    [Fact]
    public void SymbolKind_HasExpectedValues()
    {
        var values = Enum.GetNames<SymbolKind>();
        values.Should().Contain("Class");
        values.Should().Contain("Struct");
        values.Should().Contain("Interface");
        values.Should().Contain("Enum");
        values.Should().Contain("Delegate");
        values.Should().Contain("Record");
        values.Should().Contain("Method");
        values.Should().Contain("Property");
        values.Should().Contain("Field");
        values.Should().Contain("Event");
        values.Should().Contain("Constant");
        values.Should().Contain("Constructor");
        values.Should().Contain("Indexer");
        values.Should().Contain("Operator");
    }

    [Fact]
    public void SymbolKind_Count_Is14() =>
        Enum.GetValues<SymbolKind>().Should().HaveCount(14);

    [Fact]
    public void RefKind_HasExpectedValues()
    {
        var values = Enum.GetNames<RefKind>();
        values.Should().Contain("Call");
        values.Should().Contain("Read");
        values.Should().Contain("Write");
        values.Should().Contain("Instantiate");
        values.Should().Contain("Override");
        values.Should().Contain("Implementation");
    }

    [Fact]
    public void RefKind_Count_Is6() =>
        Enum.GetValues<RefKind>().Should().HaveCount(6);

    [Fact]
    public void FactKind_HasExpectedValues()
    {
        var values = Enum.GetNames<FactKind>();
        values.Should().Contain("Route");
        values.Should().Contain("Config");
        values.Should().Contain("DbTable");
        values.Should().Contain("DiRegistration");
        values.Should().Contain("Middleware");
        values.Should().Contain("Exception");
        values.Should().Contain("Log");
        values.Should().Contain("RetryPolicy");
    }

    [Fact]
    public void FactKind_Count_Is8() =>
        Enum.GetValues<FactKind>().Should().HaveCount(8);
}
