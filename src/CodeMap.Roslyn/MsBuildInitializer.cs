namespace CodeMap.Roslyn;

using Microsoft.Build.Locator;

/// <summary>
/// Ensures MSBuildLocator is registered exactly once per process.
/// Must be called before any Microsoft.CodeAnalysis.MSBuild type is loaded.
/// </summary>
public static class MsBuildInitializer
{
    private static readonly object _lock = new();
    private static bool _registered;

    /// <summary>
    /// Registers MSBuild defaults if not already registered. Thread-safe via double-checked locking.
    /// Idempotent — safe to call from multiple call sites.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;

        lock (_lock)
        {
            if (_registered) return;

            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();

            _registered = true;
        }
    }
}
