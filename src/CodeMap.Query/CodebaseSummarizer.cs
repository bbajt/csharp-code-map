namespace CodeMap.Query;

using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Builds a structured codebase summary by querying all 8 FactKinds from the index.
/// No file reading, no LLM — deterministic composition of stored fact data into markdown.
/// Sections with zero items are omitted from output.
/// </summary>
internal static class CodebaseSummarizer
{
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "overview", "api", "data", "config", "di",
        "middleware", "resilience", "exceptions", "logging", "metrics"
    };

    /// <summary>
    /// Generates the codebase summary. Returns a <see cref="SummarizeResponse"/> with
    /// markdown text, structured sections, and aggregate stats.
    /// </summary>
    public static async Task<SummarizeResponse> SummarizeAsync(
        ISymbolStore store,
        RepoId repoId,
        CommitSha commitSha,
        string solutionName,
        string[]? sectionFilter,
        int maxItemsPerSection,
        CancellationToken ct)
    {
        var cap = Math.Max(maxItemsPerSection, 1);
        var filter = sectionFilter is { Length: > 0 }
            ? new HashSet<string>(sectionFilter, StringComparer.OrdinalIgnoreCase)
            : null;

        bool Include(string section) => filter is null || filter.Contains(section);

        // ── Query all fact kinds (cap+1 probe detects truncation) ─────────────
        var routeRaw      = Include("api")         ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.Route,          cap + 1, ct) : (IReadOnlyList<StoredFact>)[];
        var configRaw     = Include("config")      ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.Config,         cap + 1, ct) : [];
        var dbRaw         = Include("data")        ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.DbTable,        cap + 1, ct) : [];
        var diRaw         = Include("di")          ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.DiRegistration, cap + 1, ct) : [];
        var middlewareRaw = Include("middleware")  ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.Middleware,     cap + 1, ct) : [];
        var retryRaw      = Include("resilience")  ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.RetryPolicy,   cap + 1, ct) : [];
        var exceptionRaw  = Include("exceptions")  ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.Exception,     cap + 1, ct) : [];
        var logRaw        = Include("logging")     ? await store.GetFactsByKindAsync(repoId, commitSha, FactKind.Log,           cap + 1, ct) : [];

        // Trim to cap and detect truncation
        var (routeFacts,      routeTrunc)      = CapFacts(routeRaw,      cap);
        var (configFacts,     configTrunc)     = CapFacts(configRaw,     cap);
        var (dbFacts,         dbTrunc)         = CapFacts(dbRaw,         cap);
        var (diFacts,         diTrunc)         = CapFacts(diRaw,         cap);
        var (middlewareFacts, middlewareTrunc) = CapFacts(middlewareRaw, cap);
        var (retryFacts,      retryTrunc)      = CapFacts(retryRaw,      cap);
        var (exceptionFacts,  exceptionTrunc)  = CapFacts(exceptionRaw,  cap);
        var (logFacts,        logTrunc)        = CapFacts(logRaw,        cap);

        var projectDiags   = await store.GetProjectDiagnosticsAsync(repoId, commitSha, ct);
        var semanticLevel  = await store.GetSemanticLevelAsync(repoId, commitSha, ct) ?? SemanticLevel.Full;

        // ── Build sections ────────────────────────────────────────────────────
        var sections = new List<SummarySection>();

        if (Include("overview"))
            sections.Add(BuildOverviewSection(projectDiags, semanticLevel,
                routeFacts.Count, configFacts.Count, dbFacts.Count, diFacts.Count,
                middlewareFacts.Count, retryFacts.Count, exceptionFacts.Count, logFacts.Count));

        if (Include("api") && routeFacts.Count > 0)
            sections.Add(WithTruncation(BuildApiSection(routeFacts), routeTrunc, cap));
        else if (filter is not null && filter.Contains("api"))
            sections.Add(new SummarySection("API Endpoints", "_No HTTP endpoints detected in this codebase._", 0));

        if (Include("data") && dbFacts.Count > 0)
            sections.Add(WithTruncation(BuildDataSection(dbFacts), dbTrunc, cap));
        else if (filter is not null && filter.Contains("data"))
            sections.Add(new SummarySection("Data Layer", "_No database tables or DbContext detected in this codebase._", 0));

        if (Include("config") && configFacts.Count > 0)
            sections.Add(WithTruncation(BuildConfigSection(configFacts), configTrunc, cap));
        else if (filter is not null && filter.Contains("config"))
            sections.Add(new SummarySection("Configuration Keys", "_No configuration key usages detected in this codebase._", 0));

        if (Include("di") && diFacts.Count > 0)
            sections.Add(WithTruncation(BuildDiSection(diFacts), diTrunc, cap));
        else if (filter is not null && filter.Contains("di"))
            sections.Add(new SummarySection("DI Registrations", "_No dependency injection registrations detected in this codebase._", 0));

        if (Include("middleware") && middlewareFacts.Count > 0)
            sections.Add(WithTruncation(BuildMiddlewareSection(middlewareFacts), middlewareTrunc, cap));
        else if (filter is not null && filter.Contains("middleware"))
            sections.Add(new SummarySection("Middleware Pipeline", "_No middleware pipeline detected in this codebase._", 0));

        if (Include("resilience") && retryFacts.Count > 0)
            sections.Add(WithTruncation(BuildResilienceSection(retryFacts), retryTrunc, cap));
        else if (filter is not null && filter.Contains("resilience"))
            sections.Add(new SummarySection("Resilience Policies", "_No retry or circuit-breaker policies detected in this codebase._", 0));

        if (Include("exceptions") && exceptionFacts.Count > 0)
            sections.Add(WithTruncation(BuildExceptionSection(exceptionFacts), exceptionTrunc, cap));
        else if (filter is not null && filter.Contains("exceptions"))
            sections.Add(new SummarySection("Exception Types", "_No thrown exceptions detected in this codebase._", 0));

        if (Include("logging") && logFacts.Count > 0)
            sections.Add(WithTruncation(BuildLoggingSection(logFacts), logTrunc, cap));
        else if (filter is not null && filter.Contains("logging"))
            sections.Add(new SummarySection("Logging", "_No log statements detected in this codebase._", 0));

        if (Include("metrics"))
            sections.Add(BuildMetricsSection(projectDiags, semanticLevel,
                routeFacts.Count, configFacts.Count, dbFacts.Count, diFacts.Count,
                middlewareFacts.Count, retryFacts.Count, exceptionFacts.Count, logFacts.Count));

        var markdown = RenderMarkdown(solutionName, sections);

        int totalSymbols = projectDiags.Sum(p => p.SymbolCount);
        int totalRefs    = projectDiags.Sum(p => p.ReferenceCount);
        int totalFacts   = routeFacts.Count + configFacts.Count + dbFacts.Count + diFacts.Count
                         + middlewareFacts.Count + retryFacts.Count + exceptionFacts.Count + logFacts.Count;

        var stats = new SummaryStats(
            ProjectCount:        projectDiags.Count,
            SymbolCount:         totalSymbols,
            ReferenceCount:      totalRefs,
            FactCount:           totalFacts,
            EndpointCount:       routeFacts.Count,
            ConfigKeyCount:      configFacts.Count,
            DbTableCount:        dbFacts.Count,
            DiRegistrationCount: diFacts.Count,
            ExceptionTypeCount:  exceptionFacts.Select(f => ParsePipe(f.Value).Left).Distinct(StringComparer.Ordinal).Count(),
            LogTemplateCount:    logFacts.Select(f => ParsePipe(f.Value).Left).Distinct(StringComparer.Ordinal).Count(),
            SemanticLevel:       semanticLevel);

        return new SummarizeResponse(solutionName, markdown, sections, stats);
    }

    // ── Section builders ──────────────────────────────────────────────────────

    private static SummarySection BuildOverviewSection(
        IReadOnlyList<ProjectDiagnostic> projectDiags,
        SemanticLevel semanticLevel,
        int endpoints, int configKeys, int dbTables, int diRegs,
        int middleware, int retryPolicies, int exceptions, int logTemplates)
    {
        var sb = new StringBuilder();
        int totalSymbols = projectDiags.Sum(p => p.SymbolCount);
        int totalRefs    = projectDiags.Sum(p => p.ReferenceCount);

        if (projectDiags.Count > 0)
        {
            sb.AppendLine($"{projectDiags.Count} projects, {totalSymbols:N0} symbols, {totalRefs:N0} references.");
        }
        sb.AppendLine($"Semantic level: {semanticLevel} (all projects compiled{(semanticLevel == SemanticLevel.Full ? " successfully" : " — partial results")}).");

        if (projectDiags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Project | Compiled | Symbols |");
            sb.AppendLine("|---------|----------|---------|");
            foreach (var p in projectDiags)
                sb.AppendLine($"| {p.ProjectName} | {(p.Compiled ? "✓" : "✗")} | {p.SymbolCount} |");
        }

        return new SummarySection("Solution Overview", sb.ToString().TrimEnd(), projectDiags.Count);
    }

    private static SummarySection BuildApiSection(IReadOnlyList<StoredFact> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Method | Route | Handler |");
        sb.AppendLine("|--------|-------|---------|");
        foreach (var fact in facts)
        {
            var space = fact.Value.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) continue;
            var method = fact.Value[..space];
            var path   = fact.Value[(space + 1)..];
            var handler = ShortSymbolName(fact.SymbolId);
            sb.AppendLine($"| {method} | {path} | {handler} |");
        }
        return new SummarySection($"API Surface ({facts.Count} endpoint{Plural(facts.Count)})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildDataSection(IReadOnlyList<StoredFact> facts)
    {
        // Deduplicate by table name (same key convention as DbTableExtractor)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string Table, string Source)>();
        foreach (var fact in facts)
        {
            var (tableName, source) = ParsePipe(fact.Value);
            if (seen.Add(tableName))
                rows.Add((tableName, source));
        }
        var sb = new StringBuilder();
        sb.AppendLine("| Table | Source |");
        sb.AppendLine("|-------|--------|");
        foreach (var (table, source) in rows)
            sb.AppendLine($"| {table} | {source} |");
        return new SummarySection($"Data Layer ({rows.Count} table{Plural(rows.Count)})",
            sb.ToString().TrimEnd(), rows.Count);
    }

    private static SummarySection BuildConfigSection(IReadOnlyList<StoredFact> facts)
    {
        // Parse and group by top-level prefix
        var parsed = facts
            .Select(f => { var (key, pattern) = ParsePipe(f.Value); return (key, pattern); })
            .GroupBy(x => x.key.Split(':')[0])
            .OrderBy(g => g.Key)
            .ToList();

        var sb = new StringBuilder();
        foreach (var group in parsed)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var (key, pattern) in group)
                sb.AppendLine($"- `{key}` — {pattern}");
            sb.AppendLine();
        }
        return new SummarySection($"Configuration ({facts.Count} key{Plural(facts.Count)})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildDiSection(IReadOnlyList<StoredFact> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Service | Implementation | Lifetime |");
        sb.AppendLine("|---------|---------------|----------|");
        foreach (var fact in facts)
        {
            // Value format: "ServiceType → ImplType|Lifetime"
            var pipeIdx = fact.Value.LastIndexOf('|');
            if (pipeIdx < 0) continue;
            var registration = fact.Value[..pipeIdx];
            var lifetime     = fact.Value[(pipeIdx + 1)..];
            var arrowIdx     = registration.IndexOf(" \u2192 ", StringComparison.Ordinal);
            var service = arrowIdx >= 0 ? registration[..arrowIdx] : registration;
            var impl    = arrowIdx >= 0 ? registration[(arrowIdx + 3)..] : registration;
            sb.AppendLine($"| {service} | {impl} | {lifetime} |");
        }
        return new SummarySection($"Dependency Injection ({facts.Count} registration{Plural(facts.Count)})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildMiddlewareSection(IReadOnlyList<StoredFact> facts)
    {
        // Parse "MiddlewareName|pos:N" or "MiddlewareName|pos:N|terminal"
        var entries = facts
            .Select(f =>
            {
                var parts = f.Value.Split('|');
                var name  = parts[0];
                int pos   = 0;
                bool terminal = parts.Any(p => p == "terminal");
                foreach (var p in parts)
                    if (p.StartsWith("pos:", StringComparison.Ordinal) &&
                        int.TryParse(p[4..], out var n))
                        pos = n;
                return (pos, name, terminal);
            })
            .OrderBy(e => e.pos)
            .ToList();

        var sb = new StringBuilder();
        foreach (var (pos, name, terminal) in entries)
        {
            var suffix = terminal ? " *(terminal)*" : "";
            sb.AppendLine($"{pos}. {name}{suffix}");
        }
        return new SummarySection("Middleware Pipeline",
            sb.ToString().TrimEnd(), entries.Count);
    }

    private static SummarySection BuildResilienceSection(IReadOnlyList<StoredFact> facts)
    {
        var sb = new StringBuilder();
        foreach (var fact in facts)
        {
            var (description, framework) = ParsePipe(fact.Value);
            sb.AppendLine($"- {description} — {framework}");
        }
        return new SummarySection($"Resilience ({facts.Count} polic{(facts.Count == 1 ? "y" : "ies")})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildExceptionSection(IReadOnlyList<StoredFact> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Exception | Context | Thrown By |");
        sb.AppendLine("|-----------|---------|-----------|");
        foreach (var fact in facts)
        {
            var (exType, context) = ParsePipe(fact.Value);
            var thrownBy = ShortSymbolName(fact.SymbolId);
            sb.AppendLine($"| {exType} | {context} | {thrownBy} |");
        }
        return new SummarySection($"Error Handling ({facts.Count} throw{Plural(facts.Count)})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildLoggingSection(IReadOnlyList<StoredFact> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Level | Template | Service |");
        sb.AppendLine("|-------|----------|---------|");
        foreach (var fact in facts)
        {
            var (template, level) = ParsePipe(fact.Value);
            var service = ShortSymbolName(fact.SymbolId);
            sb.AppendLine($"| {level} | {template} | {service} |");
        }
        return new SummarySection($"Logging ({facts.Count} template{Plural(facts.Count)})",
            sb.ToString().TrimEnd(), facts.Count);
    }

    private static SummarySection BuildMetricsSection(
        IReadOnlyList<ProjectDiagnostic> projectDiags,
        SemanticLevel semanticLevel,
        int endpoints, int configKeys, int dbTables, int diRegs,
        int middleware, int retryPolicies, int exceptions, int logTemplates)
    {
        int totalFacts = endpoints + configKeys + dbTables + diRegs
                       + middleware + retryPolicies + exceptions + logTemplates;
        var sb = new StringBuilder();
        if (projectDiags.Count > 0)
        {
            int sym = projectDiags.Sum(p => p.SymbolCount);
            int refs = projectDiags.Sum(p => p.ReferenceCount);
            sb.AppendLine($"- Projects: {projectDiags.Count}");
            sb.AppendLine($"- Symbols: {sym:N0}");
            sb.AppendLine($"- References: {refs:N0}");
        }
        sb.AppendLine($"- Total facts: {totalFacts}");
        sb.AppendLine($"  - Endpoints: {endpoints}, Config keys: {configKeys}, DB tables: {dbTables}");
        sb.AppendLine($"  - DI registrations: {diRegs}, Middleware: {middleware}, Retry policies: {retryPolicies}");
        sb.AppendLine($"  - Exceptions: {exceptions}, Log templates: {logTemplates}");
        sb.AppendLine($"- Index quality: {semanticLevel}");
        return new SummarySection("Key Metrics", sb.ToString().TrimEnd(), totalFacts);
    }

    // ── Markdown renderer ──────────────────────────────────────────────────────

    private static string RenderMarkdown(string solutionName, IReadOnlyList<SummarySection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {solutionName} — Codebase Summary");
        sb.AppendLine();
        foreach (var section in sections)
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.Append("*Generated by CodeMap MCP*");
        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (string Left, string Right) ParsePipe(string value)
    {
        var idx = value.IndexOf('|', StringComparison.Ordinal);
        return idx < 0
            ? (value, "")
            : (value[..idx], value[(idx + 1)..]);
    }

    private static string ShortSymbolName(SymbolId symbolId)
    {
        if (symbolId == SymbolId.Empty || string.IsNullOrEmpty(symbolId.Value))
            return "—";
        // Documentation comment ID format: M:Namespace.Class.Method(params)
        // Strip leading type prefix (M:, T:, P:, etc.)
        var id = symbolId.Value;
        if (id.Length > 2 && id[1] == ':') id = id[2..];
        // Drop parameters
        var paren = id.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) id = id[..paren];
        // Keep last two segments: Class.Method
        var dot = id.LastIndexOf('.');
        if (dot > 0)
        {
            var prev = id.LastIndexOf('.', dot - 1);
            if (prev > 0) id = id[(prev + 1)..];
        }
        return id;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    /// <summary>
    /// Returns the capped list and a flag indicating whether items were dropped.
    /// Uses a limit+1 probe: if the raw list exceeds the cap, truncation occurred.
    /// </summary>
    private static (IReadOnlyList<StoredFact> Facts, bool Truncated) CapFacts(
        IReadOnlyList<StoredFact> raw, int cap)
    {
        if (raw.Count <= cap) return (raw, false);
        return (raw.Take(cap).ToList(), true);
    }

    /// <summary>
    /// Applies truncation metadata and an inline markdown note to a section when the
    /// data was capped by <c>max_items_per_section</c>.
    /// </summary>
    private static SummarySection WithTruncation(SummarySection section, bool truncated, int cap)
    {
        if (!truncated) return section with { TotalAvailable = section.ItemCount };
        var note = $"\n\n_Showing {cap} of {cap}+ items. "
                 + "Increase `max_items_per_section` to see all._";
        return section with
        {
            Truncated = true,
            TotalAvailable = null,
            Content = section.Content + note
        };
    }
}
