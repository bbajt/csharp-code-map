namespace CodeMap.Query;

/// <summary>
/// Heuristic estimates of tokens saved by using CodeMap instead of raw file reads.
/// </summary>
public static class TokenSavingsEstimator
{
    private const decimal SonnetRatePerKToken = 0.003m;
    private const decimal OpusRatePerKToken = 0.015m;
    private const decimal Gpt4RatePerKToken = 0.01m;

    /// <summary>
    /// Estimates tokens saved for a symbol search.
    /// Without CodeMap: ~800 tokens per result (grep + open files).
    /// With CodeMap: ~50 tokens per result (structured hit).
    /// </summary>
    public static int ForSearch(int hitCount)
    {
        var rawTokens = hitCount * 800;
        var codeMapTokens = hitCount * 50;
        return Math.Max(0, rawTokens - codeMapTokens);
    }

    /// <summary>
    /// Estimates tokens saved for a symbol card lookup.
    /// Without CodeMap: ~2000 tokens (read entire file).
    /// With CodeMap: ~350 tokens (structured card).
    /// </summary>
    public static int ForCard() => 2000 - 350;

    /// <summary>
    /// Estimates tokens saved for a span read.
    /// Without CodeMap: entire file. With CodeMap: requested span only.
    /// </summary>
    public static int ForSpan(int totalFileLines, int returnedLines)
    {
        var rawTokens = totalFileLines * 10;
        var codeMapTokens = returnedLines * 10;
        return Math.Max(0, rawTokens - codeMapTokens);
    }

    /// <summary>
    /// Estimates tokens saved for a symbol context lookup (card + code + callees).
    /// Without CodeMap: read target file + all callee files (~2000 tokens each).
    /// With CodeMap: structured response with code snippets (~500 tokens per symbol).
    /// </summary>
    public static int ForContext(int symbolCount)
    {
        var rawTokens = symbolCount * 2000;
        var codeMapTokens = symbolCount * 500;
        return Math.Max(0, rawTokens - codeMapTokens);
    }

    /// <summary>
    /// Estimates tokens saved for a text search across indexed files.
    /// Without CodeMap: agent would Bash-grep or read every file manually.
    /// Savings modelled as 200 tokens per file scanned, minus output cost.
    /// </summary>
    public static int ForSearchText(int totalFiles, int matchCount)
    {
        var rawTokens = totalFiles * 200;
        var codeMapTokens = matchCount * 20;
        return Math.Max(0, rawTokens - codeMapTokens);
    }

    /// <summary>
    /// Estimates cost avoided (USD) in the default claude_sonnet model.
    /// Used to populate ResponseMeta.CostAvoided (single decimal field).
    /// </summary>
    public static decimal EstimateCostAvoided(int tokensSaved) =>
        tokensSaved / 1000.0m * SonnetRatePerKToken;

    /// <summary>
    /// Estimates cost avoided per model. Used with ITokenSavingsTracker.RecordSaving.
    /// </summary>
    public static Dictionary<string, decimal> EstimateCostPerModel(int tokensSaved)
    {
        var k = tokensSaved / 1000.0m;
        return new Dictionary<string, decimal>
        {
            ["claude_sonnet"] = k * SonnetRatePerKToken,
            ["claude_opus"] = k * OpusRatePerKToken,
            ["gpt4"] = k * Gpt4RatePerKToken,
        };
    }
}
