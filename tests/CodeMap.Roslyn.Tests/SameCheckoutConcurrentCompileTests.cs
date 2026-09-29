namespace CodeMap.Roslyn.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Regression for F9 (PHASE-21-03 T01): two compilers evaluating the <b>same checkout</b> at the
/// same time raced on generated files under <c>obj/</c> (e.g. <c>EmbeddedAttribute.cs</c> —
/// "Cannot create a file when that file already exists"), degrading one compilation that was then
/// published as a normal baseline. Each <see cref="RoslynCompiler"/> instance stands in for one
/// daemon process (MSBuildWorkspace evaluates out-of-process, so the builds truly overlap).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SameCheckoutConcurrentCompileTests : IAsyncLifetime
{
    private const int Iterations = 5;

    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "codemap-f9-" + Guid.NewGuid().ToString("N"));

    private string SolutionPath => Path.Combine(_checkout, "SampleBlazorSolution.slnx");

    private string IntermediateDir => Path.Combine(_checkout, "SampleBlazorApp", "obj", "Debug");

    /// <summary>Copies testdata/SampleBlazorSolution (no bin/obj) and restores it once.</summary>
    public async ValueTask InitializeAsync()
    {
        CopySource(Path.Combine(FindRepoRoot(), "testdata", "SampleBlazorSolution"), _checkout);
        await RunAsync("dotnet", $"restore \"{SolutionPath}\"", _checkout);
    }

    /// <summary>Removes the temp checkout.</summary>
    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_checkout, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoCompilersSameCheckout_Parallel_ProduceIdenticalResults()
    {
        var referenceLog = new RecordingLogger();
        DeleteIntermediates();
        var reference = await new RoslynCompiler(referenceLog).CompileAndExtractAsync(SolutionPath, TestContext.Current.CancellationToken);
        referenceLog.WorkspaceFailures.Should().BeEmpty("the sequential reference compile must be clean");

        for (var i = 1; i <= Iterations; i++)
        {
            DeleteIntermediates(); // fresh obj/Debug: the generated files get (re)written — the race window
            var logA = new RecordingLogger();
            var logB = new RecordingLogger();

            var a = new RoslynCompiler(logA).CompileAndExtractAsync(SolutionPath, TestContext.Current.CancellationToken);
            var b = new RoslynCompiler(logB).CompileAndExtractAsync(SolutionPath, TestContext.Current.CancellationToken);
            var results = await Task.WhenAll(a, b);

            var failures = logA.WorkspaceFailures.Concat(logB.WorkspaceFailures).ToList();
            failures.Should().BeEmpty($"iteration {i}: concurrent same-checkout evaluation must not fail MSBuild targets");
            foreach (var r in results)
            {
                r.Symbols.Count.Should().Be(reference.Symbols.Count, $"iteration {i}: symbol count must match the reference");
                r.References.Count.Should().Be(reference.References.Count, $"iteration {i}: reference count must match the reference");
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void DeleteIntermediates()
    {
        if (Directory.Exists(IntermediateDir))
            Directory.Delete(IntermediateDir, recursive: true);
    }

    private static void CopySource(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj")) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static async Task RunAsync(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0, $"{exe} {args}\n{await stdout}\n{await stderr}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodeMap.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("CodeMap.sln not found above test output.");
    }

    /// <summary>Captures the compiler's "Workspace diagnostic [Failure]" warnings.</summary>
    private sealed class RecordingLogger : ILogger<RoslynCompiler>
    {
        private readonly ConcurrentQueue<string> _failures = new();

        public IReadOnlyCollection<string> WorkspaceFailures => _failures;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("Workspace diagnostic [Failure]", StringComparison.Ordinal))
                _failures.Enqueue(message);
        }
    }
}
