namespace CodeMap.Core.Tests.Models;

using System.Text.RegularExpressions;
using CodeMap.Core.Models;
using FluentAssertions;

/// <summary>Unit tests for <see cref="ToolNames"/> (PHASE-21-09 T01, ADR-051).</summary>
public sealed class ToolNamesTests
{
    // Findings §12: current (dotted) name → canonical name.
    private static readonly (string Dotted, string Canonical)[] FindingsMapping =
    [
        ("repo.status", "repo_status"),
        ("index.ensure_baseline", "index_ensure_baseline"),
        ("index.refresh_overlay", "index_refresh_overlay"),
        ("index.diff", "index_diff"),
        ("index.list_baselines", "index_list_baselines"),
        ("index.cleanup", "index_cleanup"),
        ("index.remove_repo", "index_remove_repo"),
        ("workspace.create", "workspace_create"),
        ("workspace.reset", "workspace_reset"),
        ("workspace.list", "workspace_list"),
        ("workspace.delete", "workspace_delete"),
        ("symbols.search", "symbols_search"),
        ("symbols.get_card", "symbols_get_card"),
        ("symbols.get_context", "symbols_get_context"),
        ("symbols.get_definition_span", "symbols_get_definition_span"),
        ("code.get_span", "code_get_span"),
        ("code.search_text", "code_search_text"),
        ("refs.find", "refs_find"),
        ("graph.callers", "graph_callers"),
        ("graph.callees", "graph_callees"),
        ("graph.trace_feature", "graph_trace_feature"),
        ("types.hierarchy", "types_hierarchy"),
        ("codemap.summarize", "codemap_summarize"),
        ("codemap.export", "codemap_export"),
        ("codemap.guide", "codemap_guide"),
        ("surfaces.list_endpoints", "surfaces_list_endpoints"),
        ("surfaces.list_config_keys", "surfaces_list_config_keys"),
        ("surfaces.list_db_tables", "surfaces_list_db_tables"),
    ];

    [Fact]
    public void All_Has28Names_AllMatchMcpSafeRegex()
    {
        ToolNames.All.Should().HaveCount(28);
        ToolNames.All.Should().OnlyHaveUniqueItems();
        ToolNames.All.Should().AllSatisfy(n => Regex.IsMatch(n, "^[a-zA-Z0-9_-]{1,64}$").Should().BeTrue(n));
    }

    [Fact]
    public void All_EqualsFindingsCanonicalNames()
    {
        ToolNames.All.Should().Equal(FindingsMapping.Select(m => m.Canonical));
    }

    [Fact]
    public void LegacyAliases_MatchFindingsMapping()
    {
        ToolNames.LegacyAliases.Should().HaveCount(28);
        foreach (var (dotted, canonical) in FindingsMapping)
            ToolNames.LegacyAliases[dotted].Should().Be(canonical);
        ToolNames.LegacyAliases.Values.Should().BeEquivalentTo(ToolNames.All);
    }

    [Fact]
    public void AliasRemovalVersion_Is2_11_0() =>
        ToolNames.AliasRemovalVersion.Should().Be("2.11.0");
}
