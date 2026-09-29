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

    /// <summary>
    /// Resolves the shared baseline cache directory (F11, ADR-047). Pure, like <see cref="Resolve"/>.
    /// Unset/blank → <c>null</c> (cache disabled); <c>~</c>, <c>~/x</c>, <c>~\x</c> → expanded against
    /// the profile; fully qualified → normalised full path; relative → <see cref="ErrorCodes.InvalidArgument"/>.
    /// A blank or relative value used to be taken as-is and resolved against the daemon's working
    /// directory, usually the user's repo root, so baselines were written into the working tree.
    /// </summary>
    public static Result<string?, CodeMapError> ResolveCacheDir(string? envValue, string userProfile)
    {
        if (string.IsNullOrWhiteSpace(envValue))
            return (string?)null;

        var value = envValue.Trim();

        if (value == "~")
            return Path.GetFullPath(userProfile);

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(userProfile, value[2..]);

        if (!Path.IsPathFullyQualified(value))
            return Result<string?, CodeMapError>.Failure(CodeMapError.InvalidArgument(
                $"{CacheEnvVar} must be an absolute path or start with '~/' (or be unset to disable the shared cache); got '{envValue}'."));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    /// <summary>
    /// Resolves the shared cache directory from the process environment (<c>CODEMAP_CACHE_DIR</c>).
    /// See <see cref="ResolveCacheDir"/> for the rules.
    /// </summary>
    public static Result<string?, CodeMapError> ResolveCacheDirFromEnvironment() =>
        ResolveCacheDir(
            Environment.GetEnvironmentVariable(CacheEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
