namespace SampleApp.Models;

/// <summary>Result of a batch order processing operation.</summary>
public record OrderProcessingResult(int Processed, int Failed, TimeSpan Duration)
{
    /// <summary>Whether all orders in the batch were processed successfully.</summary>
    public bool IsFullySuccessful => Failed == 0;

    /// <summary>Total orders in the batch.</summary>
    public int Total => Processed + Failed;
}
