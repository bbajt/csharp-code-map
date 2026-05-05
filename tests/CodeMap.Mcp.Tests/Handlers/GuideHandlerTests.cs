namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMap.Mcp;
using CodeMap.Mcp.Handlers;
using FluentAssertions;

/// <summary>Unit tests for <see cref="GuideHandler"/> — codemap.guide tool (#28).</summary>
public sealed class GuideHandlerTests
{
    private readonly GuideHandler _handler = new();

    [Fact]
    public async Task Handle_NoArgs_ReturnsGuide()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Handle_EmptyArgs_ReturnsGuide()
    {
        var result = await _handler.HandleGetGuideAsync(new JsonObject(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Handle_ReturnsVersionField()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.TryGetProperty("version", out var versionProp).Should().BeTrue();
        versionProp.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Handle_SessionStart_HasTwoCommands()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        var commands = doc.RootElement
            .GetProperty("session_start")
            .GetProperty("commands");

        commands.GetArrayLength().Should().Be(2);
        commands[0].GetString().Should().Contain("index.ensure_baseline");
        commands[1].GetString().Should().Contain("workspace.create");
    }

    [Fact]
    public async Task Handle_DecisionTable_HasExpectedEntries()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        var table = doc.RootElement.GetProperty("decision_table");

        table.GetArrayLength().Should().BeGreaterThanOrEqualTo(12);

        var useTools = Enumerable.Range(0, table.GetArrayLength())
            .Select(i => table[i].GetProperty("use_tool").GetString())
            .ToList();

        useTools.Should().Contain("symbols.search");
        useTools.Should().Contain("symbols.get_context");
        useTools.Should().Contain("graph.callers");
        useTools.Should().Contain("graph.callees");
        useTools.Should().Contain("codemap.summarize");

        // BUG-5 regression: don't advertise tools that aren't registered.
        // surfaces.list_di_registrations was on the decision_table but had no
        // matching ToolDefinition — agents calling it got NOT_FOUND.
        useTools.Should().NotContain("surfaces.list_di_registrations",
            because: "decision_table must list only registered tools");
    }

    [Fact]
    public async Task Handle_Rules_HasFourRules()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        var rules = doc.RootElement.GetProperty("rules");

        rules.GetArrayLength().Should().Be(4);

        var ruleTexts = Enumerable.Range(0, 4)
            .Select(i => rules[i].GetString()!)
            .ToList();

        ruleTexts.Should().Contain(r => r.Contains("workspace_id"));
        ruleTexts.Should().Contain(r => r.Contains("refresh_overlay"));
        ruleTexts.Should().Contain(r => r.Contains("FQN") || r.Contains("manually"));
        ruleTexts.Should().Contain(r => r.Contains("XML") || r.Contains("<summary>"));
    }

    [Fact]
    public async Task Handle_AfterEditCommand_Present()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.TryGetProperty("after_edit_command", out var prop).Should().BeTrue();
        prop.GetString().Should().Contain("refresh_overlay");
    }

    [Fact]
    public async Task Handle_VerboseFalse_NoToolsField()
    {
        var result = await _handler.HandleGetGuideAsync(
            new JsonObject { ["verbose"] = false },
            CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_VerboseTrue_ContainsToolsList()
    {
        var result = await _handler.HandleGetGuideAsync(
            new JsonObject { ["verbose"] = true },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.TryGetProperty("tools", out var toolsProp).Should().BeTrue();
        toolsProp.GetArrayLength().Should().Be(28);
    }

    [Fact]
    public async Task Handle_VerboseTrue_ToolsContainCodeMapGuide()
    {
        var result = await _handler.HandleGetGuideAsync(
            new JsonObject { ["verbose"] = true },
            CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        var tools = doc.RootElement.GetProperty("tools");
        var names = Enumerable.Range(0, tools.GetArrayLength())
            .Select(i => tools[i].GetProperty("name").GetString())
            .ToList();

        names.Should().Contain("codemap.guide");
        names.Should().Contain("symbols.search");
        names.Should().Contain("symbols.get_context");
    }

    [Fact]
    public async Task Handle_ToolCount_Is28()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("tool_count").GetInt32().Should().Be(28);
    }

    [Fact]
    public async Task Handle_KnownLimitations_HintAndDocPresent()
    {
        var result = await _handler.HandleGetGuideAsync(null, CancellationToken.None);

        var doc = JsonDocument.Parse(result.Content);
        var hint = doc.RootElement.GetProperty("known_limitations_hint").GetString();
        var docPath = doc.RootElement.GetProperty("known_limitations_doc").GetString();

        hint.Should().NotBeNullOrEmpty();
        hint.Should().Contain("KNOWN-LIMITATIONS");
        docPath.Should().Be("docs/KNOWN-LIMITATIONS.md");
    }

    [Fact]
    public void Register_ToolName_IsCodeMapGuide()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);

        registry.Find("codemap.guide").Should().NotBeNull();
    }

    [Fact]
    public void Register_Schema_HasNoRequiredParameters()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);

        var tool = registry.Find("codemap.guide")!;
        var required = tool.InputSchema["required"]?.AsArray();
        required?.Count.Should().Be(0);
    }

    [Fact]
    public void Register_Description_MentionsGuide()
    {
        var registry = new ToolRegistry();
        _handler.Register(registry);

        var tool = registry.Find("codemap.guide")!;
        tool.Description.ToLowerInvariant().Should().Contain("guide");
    }
}
