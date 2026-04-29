namespace SampleBlazorApp.Services;

/// <summary>
/// Produces a greeting string. Used by Blazor components via @inject to
/// exercise the M19 Razor inject fact extractor.
/// </summary>
public interface IGreetingService
{
    /// <summary>Returns a greeting for the given name.</summary>
    string Greet(string name);
}
