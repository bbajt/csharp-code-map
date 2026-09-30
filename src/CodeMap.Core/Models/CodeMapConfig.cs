namespace CodeMap.Core.Models;

/// <summary>
/// Settings loaded from <c>config.json</c> in the data root (<c>~/.codemap</c> or <c>CODEMAP_HOME</c>) at startup.
/// A missing or corrupt file results in all defaults being used; keys the daemon does not know (including
/// the removed <c>budget_overrides</c>) are ignored with one startup warning (ADR-059).
/// Changes to config.json require a daemon restart (hot-reload not supported).
/// </summary>
/// <param name="LogLevel">Minimum log level (<c>log_level</c>), e.g. <c>Debug</c>; unparseable → Information.</param>
/// <param name="SharedCacheDir">
/// Shared baseline cache (<c>shared_cache_dir</c>), used only when <c>CODEMAP_CACHE_DIR</c> is unset.
/// Same rules as the env var: absolute or <c>~/…</c>; relative → exit 2; blank → disabled.
/// </param>
public record CodeMapConfig(
    string? LogLevel = "Information",
    string? SharedCacheDir = null
);
