namespace Support.Api.Agents;

public enum OrderLookupOutcome
{
    Found,
    NotFound,
    InvalidOrderId,
}

/// <summary>
/// Facts recorded by tools while one turn runs. The API derives the reply kind from these facts,
/// not from the model's wording. Scoped to the turn's async flow.
/// </summary>
public sealed class TurnActivity
{
    private static readonly AsyncLocal<TurnActivity?> CurrentTurn = new();
    private readonly List<OrderLookupOutcome> orderLookups = [];
    private readonly Lock gate = new();

    public static TurnActivity? Current => CurrentTurn.Value;

    public IReadOnlyList<OrderLookupOutcome> OrderLookups
    {
        get
        {
            lock (gate)
            {
                return [.. orderLookups];
            }
        }
    }

    public static Scope Begin()
    {
        var activity = new TurnActivity();
        CurrentTurn.Value = activity;
        return new Scope(activity);
    }

    public void RecordOrderLookup(OrderLookupOutcome outcome)
    {
        lock (gate)
        {
            orderLookups.Add(outcome);
        }
    }

    public readonly struct Scope(TurnActivity activity) : IDisposable
    {
        public TurnActivity Activity { get; } = activity;

        public void Dispose() => CurrentTurn.Value = null;
    }
}
