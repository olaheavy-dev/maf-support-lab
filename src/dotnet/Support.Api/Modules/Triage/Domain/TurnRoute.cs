namespace Support.Api.Modules.Triage.Domain;

/// <summary>Why a request was not routed. Matches the contract's unrouted_reason.</summary>
public enum UnroutedReason
{
    /// <summary>Triage output was not valid JSON, or broke the triage-decision schema.</summary>
    InvalidModelOutput,

    /// <summary>Valid shape, but not a request we can route with certainty (action "other", or action and target disagree).</summary>
    AmbiguousRequest,
}

/// <summary>The routing result reported on a turn: routed to a target, or unrouted with a reason. Never both.</summary>
public sealed record TurnRoute
{
    private TurnRoute(RouteTarget? target, UnroutedReason? unroutedReason)
    {
        Target = target;
        UnroutedReason = unroutedReason;
    }

    public RouteTarget? Target { get; }

    public UnroutedReason? UnroutedReason { get; }

    public bool IsRouted => Target is not null;

    public static TurnRoute Routed(RouteTarget target) => new(target, null);

    public static TurnRoute Unrouted(UnroutedReason reason) => new(null, reason);
}
