namespace Support.Api.Common.Application.Resilience;

/// <summary>
/// Every limit that bounds a turn, in one place (configuration section <c>Resilience</c>). Defaults are sized for
/// the slowest normal turn with headroom; see docs/observability.md for the retry inventory and the worst case.
/// </summary>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Wall-clock limit for one turn, from acceptance to its result. On expiry the turn ends failed/timeout.</summary>
    public int TurnTimeoutSeconds { get; set; } = 90;

    /// <summary>Model calls one turn may make (triage, specialist steps, declines, resume). Then: failed/budget_exhausted.</summary>
    public int MaxModelCallsPerTurn { get; set; } = 10;

    /// <summary>Tool-calling iterations within one agent run (MAF's function-invocation loop).</summary>
    public int MaxToolIterations { get; set; } = 4;

    /// <summary>Time one tool call may take. Then: failed/tool_unavailable.</summary>
    public int ToolTimeoutSeconds { get; set; } = 10;

    /// <summary>The provider SDK's own retries per model call: the only retry layer for model calls.</summary>
    public int ModelMaxRetries { get; set; } = 2;

    /// <summary>Time one HTTP attempt to the model may take (the SDK retries a timed-out attempt).</summary>
    public int ModelAttemptTimeoutSeconds { get; set; } = 30;

    /// <summary>Output cap per model call.</summary>
    public int MaxOutputTokens { get; set; } = 1024;
}

/// <summary>
/// Refuses to start with limits that would break or unbound a turn (Codex review on PR #81): every limit must be
/// positive (retries may be zero) and within a sane maximum, so a deadline can always be created.
/// </summary>
internal sealed class ResilienceOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<ResilienceOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        Check(options.TurnTimeoutSeconds, 1, 3600, nameof(options.TurnTimeoutSeconds));
        Check(options.MaxModelCallsPerTurn, 1, 100, nameof(options.MaxModelCallsPerTurn));
        Check(options.MaxToolIterations, 1, 50, nameof(options.MaxToolIterations));
        Check(options.ToolTimeoutSeconds, 1, 600, nameof(options.ToolTimeoutSeconds));
        Check(options.ModelMaxRetries, 0, 10, nameof(options.ModelMaxRetries));
        Check(options.ModelAttemptTimeoutSeconds, 1, 600, nameof(options.ModelAttemptTimeoutSeconds));
        Check(options.MaxOutputTokens, 1, 64000, nameof(options.MaxOutputTokens));
        return failures.Count == 0 ? Microsoft.Extensions.Options.ValidateOptionsResult.Success : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(failures);

        void Check(int value, int min, int max, string setting)
        {
            if (value < min || value > max)
            {
                failures.Add($"{ResilienceOptions.SectionName}:{setting} must be between {min} and {max} (was {value}).");
            }
        }
    }
}
