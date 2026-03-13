namespace SampleApp.Api;

// Stub Polly types for compilation (real Polly not referenced in SampleSolution)
public static class Policy
{
    public static PolicyBuilder Handle<T>() where T : Exception => new();
}

public class PolicyBuilder
{
    public object RetryAsync(int retryCount) => new();
    public object WaitAndRetryAsync(int retryCount, Func<int, TimeSpan> sleepProvider) => new();
}

public static class ResilienceSetup
{
    public static void Configure()
    {
        Policy.Handle<Exception>().RetryAsync(3);
        Policy.Handle<Exception>().WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(attempt));
    }
}
