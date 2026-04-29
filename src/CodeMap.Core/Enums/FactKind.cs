namespace CodeMap.Core.Enums;

/// <summary>
/// Classification of extracted architectural facts about a symbol.
/// </summary>
public enum FactKind
{
    /// <summary>HTTP endpoint route (ASP.NET attribute routing).</summary>
    Route,

    /// <summary>Configuration key usage (IConfiguration, IOptions, etc.).</summary>
    Config,

    /// <summary>Database table or EF Core entity.</summary>
    DbTable,

    /// <summary>Dependency injection registration (AddSingleton, AddScoped, etc.).</summary>
    DiRegistration,

    /// <summary>Middleware pipeline entry (UseMiddleware, Use, etc.).</summary>
    Middleware,

    /// <summary>Thrown exception type (throw new X()).</summary>
    Exception,

    /// <summary>Structured log message (ILogger.LogXxx).</summary>
    Log,

    /// <summary>Resilience / retry policy (Polly, etc.).</summary>
    RetryPolicy,

    /// <summary>
    /// Blazor <c>[Inject]</c> property on a <c>ComponentBase</c> derivative.
    /// <c>Value</c> format: <c>"PropertyName: TypeFqn"</c> (colon-separated).
    /// Note: this differs from <see cref="DiRegistration"/> which uses
    /// <c>"Service → Impl|Lifetime"</c>; the colon form was kept because
    /// no realistic Roslyn FQN starts with whitespace, so splitting on the
    /// first <c>": "</c> is unambiguous.
    /// </summary>
    RazorInject,

    /// <summary>
    /// Blazor <c>[Parameter]</c> property on a <c>ComponentBase</c> derivative.
    /// <c>Value</c> format: <c>"PropertyName: TypeFqn"</c> (colon-separated) —
    /// same shape as <see cref="RazorInject"/>.
    /// </summary>
    RazorParameter,
}
