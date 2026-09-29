namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Errors;
using FluentAssertions;

/// <summary>
/// Unit tests for <see cref="CodeMapHome"/> — CODEMAP_HOME data-root resolution rules
/// (PHASE-21-01 T01, ADR-039).
/// </summary>
public class CodeMapHomeTests
{
    private static readonly string Profile = OperatingSystem.IsWindows()
        ? @"C:\Users\bench"
        : "/home/bench";

    private static readonly string Absolute = OperatingSystem.IsWindows()
        ? @"D:\codemap-data"
        : "/var/codemap-data";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrWhitespace_ReturnsProfileDotCodemap(string? envValue)
    {
        var result = CodeMapHome.Resolve(envValue, Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(Profile, ".codemap"));
    }

    [Fact]
    public void Resolve_AbsolutePath_ReturnsNormalizedFullPath()
    {
        var messy = Path.Combine(Absolute, "sub", "..", "home") + Path.DirectorySeparatorChar;

        var result = CodeMapHome.Resolve(messy, Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(Absolute, "home"));
    }

    [Fact]
    public void Resolve_AbsolutePathWithSurroundingWhitespace_IsTrimmed()
    {
        var result = CodeMapHome.Resolve("  " + Absolute + "  ", Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Absolute);
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/data", "data")]
    [InlineData("~\\data", "data")]
    [InlineData("~/a/b", "a/b")]
    public void Resolve_TildeForms_ExpandAgainstProfile(string envValue, string relative)
    {
        var result = CodeMapHome.Resolve(envValue, Profile);

        var expected = relative.Length == 0
            ? Profile
            : Path.GetFullPath(Path.Combine(Profile, relative.Replace('/', Path.DirectorySeparatorChar)));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("./data")]
    [InlineData("..\\data")]
    [InlineData("~data")]
    public void Resolve_RelativePath_ReturnsInvalidArgument(string envValue)
    {
        var result = CodeMapHome.Resolve(envValue, Profile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Resolve_RelativePath_ErrorMessageNamesVariableAndValue()
    {
        var result = CodeMapHome.Resolve("relative-dir", Profile);

        result.Error.Message.Should().Contain(CodeMapHome.EnvVar).And.Contain("relative-dir");
    }

    [Fact]
    public void ResolveFromEnvironment_Unset_MatchesDefault()
    {
        Assert.SkipWhen(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CodeMapHome.EnvVar)),
            "CODEMAP_HOME is set in the test environment.");

        var result = CodeMapHome.ResolveFromEnvironment();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codemap"));
    }

    // ── ResolveCacheDir: CODEMAP_CACHE_DIR (F11, ADR-047) ───────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCacheDir_NullOrWhitespace_ReturnsNull_CacheDisabled(string? envValue)
    {
        var result = CodeMapHome.ResolveCacheDir(envValue, Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("a blank value must disable the cache, not resolve against the working directory");
    }

    [Fact]
    public void ResolveCacheDir_AbsolutePath_ReturnsNormalizedFullPath()
    {
        var messy = Path.Combine(Absolute, "sub", "..", "cache") + Path.DirectorySeparatorChar;

        var result = CodeMapHome.ResolveCacheDir("  " + messy + "  ", Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(Absolute, "cache"));
    }

    [Theory]
    [InlineData("~/shared-cache")]
    [InlineData(@"~\shared-cache")]
    public void ResolveCacheDir_TildePrefix_ExpandsAgainstProfile(string envValue)
    {
        var result = CodeMapHome.ResolveCacheDir(envValue, Profile);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(Profile, "shared-cache"));
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("./cache")]
    [InlineData(@"..\cache")]
    public void ResolveCacheDir_RelativePath_IsInvalidArgument(string envValue)
    {
        var result = CodeMapHome.ResolveCacheDir(envValue, Profile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ErrorCodes.InvalidArgument);
        result.Error.Message.Should().Contain("CODEMAP_CACHE_DIR").And.Contain(envValue);
    }
}
