namespace CodeMap.Roslyn.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Unit tests for <see cref="CheckoutBuildLock"/> — checkout-root resolution, lock keying, and
/// cross-handle exclusion (PHASE-21-03 T01, ADR-042).
/// </summary>
public sealed class CheckoutBuildLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-lock-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temp root.</summary>
    public CheckoutBuildLockTests() => Directory.CreateDirectory(_root);

    /// <summary>Removes the temp root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ── ResolveCheckoutRoot ───────────────────────────────────────────────────

    [Fact]
    public void ResolveCheckoutRoot_FindsGitDirAncestor()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var sln = Touch("src", "app", "App.sln");

        CheckoutBuildLock.ResolveCheckoutRoot(sln).Should().Be(_root);
    }

    [Fact]
    public void ResolveCheckoutRoot_GitWorktreeFile_Counts()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: /elsewhere/.git/worktrees/wt");
        var sln = Touch("App.sln");

        CheckoutBuildLock.ResolveCheckoutRoot(sln).Should().Be(_root);
    }

    [Fact]
    public void ResolveCheckoutRoot_NearestGitWins()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var inner = Path.Combine(_root, "vendor", "lib");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));
        var sln = Touch("vendor", "lib", "Lib.sln");

        CheckoutBuildLock.ResolveCheckoutRoot(sln).Should().Be(inner);
    }

    [Fact]
    public void ResolveCheckoutRoot_NoGit_UsesFileDirectory()
    {
        // Temp is not inside a git repo on CI/dev machines; guard in case it is.
        var sln = Touch("nogit", "App.sln");
        var resolved = CheckoutBuildLock.ResolveCheckoutRoot(sln);

        Assert.SkipWhen(resolved != Path.Combine(_root, "nogit") && Directory.Exists(Path.Combine(resolved, ".git")),
            "Temp directory is inside a git repository on this machine.");
        resolved.Should().Be(Path.Combine(_root, "nogit"));
    }

    // ── LockPathFor ───────────────────────────────────────────────────────────

    [Fact]
    public void LockPathFor_SameCheckoutDifferentSpelling_SameLock()
    {
        var canonical = CheckoutBuildLock.LockPathFor(_root);

        CheckoutBuildLock.LockPathFor(_root + Path.DirectorySeparatorChar).Should().Be(canonical);
        CheckoutBuildLock.LockPathFor(Path.Combine(_root, "sub", "..")).Should().Be(canonical);
        if (!OperatingSystem.IsLinux())
            CheckoutBuildLock.LockPathFor(_root.ToUpperInvariant()).Should().Be(canonical);
    }

    [Fact]
    public void LockPathFor_DifferentCheckouts_DifferentLocks() =>
        CheckoutBuildLock.LockPathFor(Path.Combine(_root, "a"))
            .Should().NotBe(CheckoutBuildLock.LockPathFor(Path.Combine(_root, "b")));

    [Fact]
    public void LockPathFor_LivesUnderTempCodemapLocks() =>
        Path.GetDirectoryName(CheckoutBuildLock.LockPathFor(_root))
            .Should().Be(Path.Combine(Path.GetTempPath(), "codemap-locks"));

    // ── AcquireAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Acquire_Held_SecondWaitsUntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromSeconds(30), NullLogger.Instance, ct);

        var second = CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromSeconds(30), NullLogger.Instance, ct);
        await Task.Delay(600, ct);
        second.IsCompleted.Should().BeFalse("the lock is still held");

        await first.DisposeAsync();
        await using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task Acquire_DifferentCheckouts_DoNotBlock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var a = await CheckoutBuildLock.AcquireAsync(Path.Combine(_root, "a"), TimeSpan.FromSeconds(5), NullLogger.Instance, ct);

        var b = CheckoutBuildLock.AcquireAsync(Path.Combine(_root, "b"), TimeSpan.FromSeconds(5), NullLogger.Instance, ct);

        await using var acquired = await b.WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    [Fact]
    public async Task Acquire_Timeout_ThrowsIOExceptionNamingCheckout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var held = await CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromSeconds(5), NullLogger.Instance, ct);

        var act = () => CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromMilliseconds(700), NullLogger.Instance, ct);

        (await act.Should().ThrowAsync<IOException>()).WithMessage("*Checkout build lock busy*" + Path.GetFileName(_root) + "*");
    }

    [Fact]
    public async Task Acquire_Cancelled_Throws()
    {
        await using var held = await CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromSeconds(5), NullLogger.Instance, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(300);

        var act = () => CheckoutBuildLock.AcquireAsync(_root, TimeSpan.FromSeconds(30), NullLogger.Instance, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Transient failure classification ─────────────────────────────────────

    [Theory]
    [InlineData("Could not write lines to file \"obj\\Debug\\net10.0\\EmbeddedAttribute.cs\". Cannot create a file when that file already exists.", true)]
    [InlineData("The process cannot access the file 'obj/project.assets.json' because it is being used by another process.", true)]
    [InlineData("Access to the path 'obj/Debug/x.cache' is denied.", true)]
    [InlineData("The project file could not be loaded. Could not find file 'Missing.csproj'.", false)]
    [InlineData("The SDK 'Microsoft.NET.Sdk.Foo' specified could not be found.", false)]
    public void IsTransientWorkspaceFailure_Theory(string message, bool expected) =>
        CheckoutBuildLock.IsTransientWorkspaceFailure(message).Should().Be(expected);

    [Fact]
    public void ConfiguredTimeout_Default_Is30Minutes()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(CheckoutBuildLock.TimeoutEnvVar) is not null,
            "Timeout override set in the environment.");
        CheckoutBuildLock.ConfiguredTimeout().Should().Be(TimeSpan.FromMinutes(30));
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }
}
