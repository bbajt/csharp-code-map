namespace SampleBlazorApp.Services;

/// <summary>
/// Default in-memory implementation of <see cref="IGreetingService"/>.
/// </summary>
public sealed class GreetingService : IGreetingService
{
    /// <inheritdoc />
    public string Greet(string name) => $"Hello, {name}!";
}
