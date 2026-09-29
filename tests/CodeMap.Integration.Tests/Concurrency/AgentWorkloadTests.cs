namespace CodeMap.Integration.Tests.Concurrency;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodeMap.Harness.Concurrency;
using FluentAssertions;

/// <summary>
/// Unit tests for <see cref="AgentWorkload"/> driven by a fake <see cref="IMcpClient"/> (no daemon).
/// </summary>
public sealed class AgentWorkloadTests : IDisposable
{
    private const string TypeId = "T:Shop.Order";
    private const string MethodId = "M:Shop.OrderService.PlaceOrder";

    private readonly string _editFile = Path.Combine(Path.GetTempPath(), "cm-workload-" + Path.GetRandomFileName() + ".cs");

    /// <summary>Removes the edit file the loop appends to.</summary>
    public void Dispose()
    {
        if (File.Exists(_editFile)) File.Delete(_editFile);
    }

    [Fact]
    public async Task Setup_InputsMatchOnlyTypes_MethodToolsStillTargetMethods()
    {
        // PHASE-21-06 T01 smoke finding: eShopOnWeb's query inputs ("Order", "Basket", …) only
        // match types, the method pool stayed empty, and graph_callers / get_context /
        // trace_feature fell back to types → INVALID_ARGUMENT on ~57% of graph_callers calls.
        var client = new TypesUnlessKindsFilterClient();
        var workload = new AgentWorkload(0, client, "repo", "repo/x.sln", "agent-0", _editFile,
            seed: 42, WorkspaceMode.Isolated, new Lock(), ["Order"]);
        workload.Peers = [workload];

        (await workload.SetupAsync(Task.CompletedTask, TestContext.Current.CancellationToken)).Should().BeTrue();
        await workload.RunLoopAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        var methodTargets = client.Calls
            .Where(c => c.Tool is "graph_callers" or "symbols_get_context")
            .Select(c => c.Args["symbol_id"]!.GetValue<string>())
            .Concat(client.Calls.Where(c => c.Tool == "graph_trace_feature")
                .Select(c => c.Args["entry_point"]!.GetValue<string>()))
            .ToList();
        methodTargets.Should().NotBeEmpty();
        methodTargets.Should().AllBe(MethodId);
    }

    /// <summary>
    /// Answers every call successfully. <c>symbols_search</c> returns a type hit, unless the call
    /// filters on <c>kinds: ["method"]</c>, which returns a method hit.
    /// </summary>
    private sealed class TypesUnlessKindsFilterClient : IMcpClient
    {
        public ConcurrentQueue<(string Tool, JsonObject Args)> Calls { get; } = new();

        public int ProcessId => 0;

        public TimeSpan InitializeElapsed => TimeSpan.Zero;

        public Task<McpCallResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
        {
            Calls.Enqueue((tool, (JsonObject)arguments.DeepClone()));
            var payload = tool switch
            {
                "index_ensure_baseline" => "{\"already_existed\":true}",
                "symbols_search" => IsMethodFilter(arguments)
                    ? Hits(MethodId, "method")
                    : Hits(TypeId, "class"),
                _ => "{}",
            };
            return Task.FromResult(new McpCallResult(tool, true, null, null, TimeSpan.FromMilliseconds(1), payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static bool IsMethodFilter(JsonObject args) =>
            args["kinds"] is JsonArray kinds
            && kinds.Any(k => string.Equals(k?.GetValue<string>(), "method", StringComparison.OrdinalIgnoreCase));

        private static string Hits(string id, string kind) =>
            $"{{\"data\":{{\"hits\":[{{\"symbol_id\":\"{id}\",\"kind\":\"{kind}\"}}]}}}}";
    }
}
