namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Interfaces;
using CodeMap.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Smoke tests for DI container composition. Validates that AddCodeMapServices
/// registers every dependency required to resolve the full object graph.
/// Using validateOnBuild:true catches missing registrations at container build
/// time rather than at first resolution.
/// </summary>
public class ServiceRegistrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ServiceRegistrationTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void AddCodeMapServices_CanBuildContainerWithoutMissingRegistrations()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);

        // validateOnBuild: true eagerly validates the entire dependency graph —
        // any missing registration throws here rather than at first resolve.
        Action act = () => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        act.Should().NotThrow();
    }

    [Fact]
    public void AddCodeMapServices_CanResolveIQueryEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);

        using var sp = services.BuildServiceProvider();

        var engine = sp.GetRequiredService<IQueryEngine>();
        engine.Should().NotBeNull();
    }

    [Fact]
    public void AddCodeMapServices_CanResolveMcpServer()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCodeMapServices(baseDir: _tempDir);

        using var sp = services.BuildServiceProvider();

        var server = sp.GetRequiredService<McpServer>();
        server.Should().NotBeNull();
    }
}
