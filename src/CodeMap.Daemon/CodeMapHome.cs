namespace CodeMap.Daemon;

using CodeMap.Core.Errors;

/// <summary>
/// Resolves the CodeMap data root — the directory holding <c>config.json</c>,
/// <c>logs/</c>, <c>store/</c> (baselines + overlays) and <c>_savings.json</c>.
/// Defaults to <c>~/.codemap</c>; overridable via the <c>CODEMAP_HOME</c> environment
/// variable (ADR-039). The install layout (<c>bin/</c>, <c>swap.bat</c>) is not affected.
/// </summary>
public static class CodeMapHome
{
    /// <summary>Environment variable that overrides the data root.</summary>
    public const string EnvVar = "CODEMAP_HOME";

    /// <summary>Directory name of the default data root under the user profile.</summary>
    public const string DefaultDirName = ".codemap";

    /// <summary>
    /// Resolves the data root from an explicit <paramref name="envValue"/> and
    /// <paramref name="userProfile"/>. Pure — reads no environment state and creates no directories.
    /// Unset/blank → <c>{userProfile}/.codemap</c>; <c>~</c>, <c>~/x</c>, <c>~\x</c> → expanded
    /// against the profile; fully qualified → normalised full path; anything else (relative)
    /// → <see cref="ErrorCodes.InvalidArgument"/>, because MCP clients launch the server
    /// from an unpredictable working directory.
    /// </summary>
    public static Result<string, CodeMapError> Resolve(string? envValue, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(envValue))
            return Path.Combine(userProfile, DefaultDirName);

        var value = envValue.Trim();

        if (value == "~")
            return Path.GetFullPath(userProfile);

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(userProfile, value[2..]);

        if (!Path.IsPathFullyQualified(value))
            return Result<string, CodeMapError>.Failure(CodeMapError.InvalidArgument(
                $"{EnvVar} must be an absolute path or start with '~/'; got '{envValue}'."));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    /// <summary>
    /// Resolves the data root from the process environment (<c>CODEMAP_HOME</c>) and the
    /// current user's profile directory. See <see cref="Resolve"/> for the rules.
    /// </summary>
    public static Result<string, CodeMapError> ResolveFromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable(EnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Environment variable that enables the shared baseline cache.</summary>
    public const string CacheEnvVar = "CODEMAP_CACHE_DIR";

    /// <summary>The <c>config.json</c> key that names the shared cache when <see cref="CacheEnvVar"/> is unset.</summary>
    public const string CacheConfigKey = "shared_cache_dir";

    /// <summary>
    /// Resolves the shared baseline cache directory from <c>CODEMAP_CACHE_DIR</c> alone (F11, ADR-047).
    /// Pure, like <see cref="Resolve"/>. Unset/blank → <c>null</c> (cache disabled); <c>~</c>, <c>~/x</c>,
    /// <c>~\x</c> → expanded against the profile; fully qualified → normalised full path; relative →
    /// <see cref="ErrorCodes.InvalidArgument"/>. A blank or relative value used to be taken as-is and
    /// resolved against the daemon's working directory, usually the user's repo root, so baselines were
    /// written into the working tree.
    /// </summary>
    public static Result<string?, CodeMapError> ResolveCacheDir(string? envValue, string userProfile) =>
        ResolveCacheValue(envValue, userProfile,
            $"{CacheEnvVar} must be an absolute path or start with '~/' (or be unset to disable the shared cache); got '{envValue}'.");

    /// <summary>
    /// Resolves the shared baseline cache directory (ADR-047, ADR-059). <c>CODEMAP_CACHE_DIR</c> wins
    /// whenever it is set, and a set-but-blank value disables the cache. When it is unset (<c>null</c>),
    /// <c>config.json</c>'s <c>shared_cache_dir</c> is used with the same rules: blank/null → disabled;
    /// absolute or <c>~/…</c> accepted; relative → <see cref="ErrorCodes.InvalidArgument"/> naming
    /// <c>config.json</c> (exit 2 at startup).
    /// </summary>
    public static Result<string?, CodeMapError> ResolveCacheDir(string? envValue, string? configValue, string userProfile) =>
        envValue is not null
            ? ResolveCacheDir(envValue, userProfile)
            : ResolveCacheValue(configValue, userProfile,
                $"config.json: {CacheConfigKey} must be an absolute path or start with '~/' (or be removed to disable the shared cache); got '{configValue}'.");

    /// <summary>
    /// Resolves the shared cache directory from the process environment (<c>CODEMAP_CACHE_DIR</c>), falling
    /// back to <paramref name="configValue"/> (<c>config.json</c>'s <c>shared_cache_dir</c>).
    /// See <see cref="ResolveCacheDir(string?, string?, string)"/> for the rules.
    /// </summary>
    public static Result<string?, CodeMapError> ResolveCacheDirFromEnvironment(string? configValue) =>
        ResolveCacheDir(
            Environment.GetEnvironmentVariable(CacheEnvVar),
            configValue,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Shared path rule for both cache sources: blank → <c>null</c>; <c>~</c> / <c>~/x</c> / <c>~\x</c> →
    /// expanded against <paramref name="userProfile"/>; fully qualified → normalised; relative → failure
    /// with <paramref name="relativeMessage"/>.
    /// </summary>
    private static Result<string?, CodeMapError> ResolveCacheValue(string? raw, string userProfile, string relativeMessage)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (string?)null;

        var value = raw.Trim();

        if (value == "~")
            return Path.GetFullPath(userProfile);

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(userProfile, value[2..]);

        if (!Path.IsPathFullyQualified(value))
            return Result<string?, CodeMapError>.Failure(CodeMapError.InvalidArgument(relativeMessage));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
