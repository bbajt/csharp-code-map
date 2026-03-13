namespace CodeMap.Core.Models;

/// <summary>
/// Settings loaded from ~/.codemap/config.json at startup.
/// Missing or corrupt file results in all defaults being used.
/// Changes to config.json require a daemon restart (hot-reload not supported).
/// </summary>
public record CodeMapConfig(
    string? LogLevel = "Information",
    string? SharedCacheDir = null,
    BudgetOverrides? BudgetOverrides = null
);

/// <summary>Budget limit overrides for hardcap enforcement.</summary>
public record BudgetOverrides(
    int? MaxResults = null,
    int? MaxLines = null,
    int? MaxChars = null
);
