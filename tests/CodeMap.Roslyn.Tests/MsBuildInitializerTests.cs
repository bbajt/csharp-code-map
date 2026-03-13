namespace CodeMap.Roslyn.Tests;

using FluentAssertions;

public class MsBuildInitializerTests
{
    [Fact]
    public void EnsureRegistered_CalledMultipleTimes_DoesNotThrow()
    {
        // Call multiple times — should be idempotent
        var act = () =>
        {
            MsBuildInitializer.EnsureRegistered();
            MsBuildInitializer.EnsureRegistered();
            MsBuildInitializer.EnsureRegistered();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureRegistered_AfterCall_MSBuildIsRegistered()
    {
        MsBuildInitializer.EnsureRegistered();
        Microsoft.Build.Locator.MSBuildLocator.IsRegistered.Should().BeTrue();
    }
}
