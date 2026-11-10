namespace Support.Api.Modules.Agents.Application;

/// <summary>What a read tool found. Recorded by the tool, read by code to decide the reply kind.</summary>
public enum ToolOutcome
{
    /// <summary>The requested record exists and belongs to the current customer (or a search had results).</summary>
    Found,

    /// <summary>No such record for this customer. Another customer's record is reported exactly the same way.</summary>
    NotFound,

    /// <summary>The input was not a valid identifier, so nothing was looked up.</summary>
    InvalidInput,

    /// <summary>A search ran and matched nothing: an unresolved issue the customer may want escalated.</summary>
    NoMatch,
}

/// <summary>
/// Facts recorded by tools while one turn runs. The reply kind is derived from these facts, not from the model's
/// wording. Scoped to the turn's async flow.
/// </summary>
public sealed class TurnActivity
{
    private static readonly AsyncLocal<TurnActivity?> CurrentTurn = new();
    private readonly List<ToolOutcome> outcomes = [];
    private string? verifiedOrderId;
    private readonly Lock gate = new();

    public static TurnActivity? Current => CurrentTurn.Value;

    public IReadOnlyList<ToolOutcome> Outcomes
    {
        get
        {
            lock (gate)
            {
                return [.. outcomes];
            }
        }
    }

    /// <summary>The last order a tool found for the current customer during this turn, if any.</summary>
    public string? VerifiedOrderId
    {
        get
        {
            lock (gate)
            {
                return verifiedOrderId;
            }
        }
    }

    public static Scope Begin()
    {
        var activity = new TurnActivity();
        CurrentTurn.Value = activity;
        return new Scope(activity);
    }

    /// <summary>
    /// Records what a tool found. Pass <paramref name="orderId"/> only for <see cref="ToolOutcome.Found"/> results
    /// owned by the current customer: it becomes the verified order a follow-up can refer to.
    /// </summary>
    public void Record(ToolOutcome outcome, string? orderId = null)
    {
        lock (gate)
        {
            outcomes.Add(outcome);
            if (outcome == ToolOutcome.Found && orderId is not null)
            {
                verifiedOrderId = orderId;
            }
        }
    }

    public readonly struct Scope(TurnActivity activity) : IDisposable
    {
        public TurnActivity Activity { get; } = activity;

        public void Dispose() => CurrentTurn.Value = null;
    }
}
