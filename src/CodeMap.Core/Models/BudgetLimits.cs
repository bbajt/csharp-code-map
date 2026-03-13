namespace CodeMap.Core.Models;

/// <summary>
/// Budget limits controlling the size of query responses.
/// All values are validated to be positive in the constructor.
/// </summary>
public record BudgetLimits
{
    public int MaxResults { get; init; }
    public int MaxReferences { get; init; }
    public int MaxDepth { get; init; }
    public int MaxLines { get; init; }
    public int MaxChars { get; init; }

    /// <summary>Default budget limits per the API specification.</summary>
    public static readonly BudgetLimits Defaults = new();

    /// <summary>Hard caps — no request may exceed these values.</summary>
    public static readonly BudgetLimits HardCaps = new(100, 500, 6, 400, 40_000);

    /// <summary>
    /// Creates a BudgetLimits instance. All values must be positive.
    /// </summary>
    public BudgetLimits(
        int maxResults = 20,
        int maxReferences = 50,
        int maxDepth = 3,
        int maxLines = 120,
        int maxChars = 12_000)
    {
        if (maxResults <= 0) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (maxReferences <= 0) throw new ArgumentOutOfRangeException(nameof(maxReferences));
        if (maxDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxLines <= 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        MaxResults = maxResults;
        MaxReferences = maxReferences;
        MaxDepth = maxDepth;
        MaxLines = maxLines;
        MaxChars = maxChars;
    }

    /// <summary>
    /// Clamps this instance to hard caps. Returns a new instance with
    /// any values exceeding hard caps reduced, and a dictionary of
    /// limits that were applied.
    /// </summary>
    public (BudgetLimits Clamped, Dictionary<string, LimitApplied> Applied) ClampToHardCaps()
    {
        var applied = new Dictionary<string, LimitApplied>();

        int ClampField(int requested, int hardCap, string name)
        {
            if (requested > hardCap)
            {
                applied[name] = new LimitApplied(requested, hardCap);
                return hardCap;
            }
            return requested;
        }

        var clamped = new BudgetLimits(
            ClampField(MaxResults, HardCaps.MaxResults, nameof(MaxResults)),
            ClampField(MaxReferences, HardCaps.MaxReferences, nameof(MaxReferences)),
            ClampField(MaxDepth, HardCaps.MaxDepth, nameof(MaxDepth)),
            ClampField(MaxLines, HardCaps.MaxLines, nameof(MaxLines)),
            ClampField(MaxChars, HardCaps.MaxChars, nameof(MaxChars))
        );

        return (clamped, applied);
    }
}
