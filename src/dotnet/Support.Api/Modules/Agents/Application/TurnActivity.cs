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

    public static Scope Begin()
    {
        var activity = new TurnActivity();
        CurrentTurn.Value = activity;
        return new Scope(activity);
    }

    public void Record(ToolOutcome outcome)
    {
        lock (gate)
        {
            outcomes.Add(outcome);
        }
    }

    public readonly struct Scope(TurnActivity activity) : IDisposable
    {
        public TurnActivity Activity { get; } = activity;

        public void Dispose() => CurrentTurn.Value = null;
    }
}
