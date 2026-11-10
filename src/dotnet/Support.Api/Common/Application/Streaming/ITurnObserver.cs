namespace Support.Api.Common.Application.Streaming;

/// <summary>
/// Receives a running turn's live events (L12): that it started, its progress, and the reply text as it is produced.
/// The streaming endpoint turns these into Server-Sent Events; a plain JSON request uses <see cref="TurnObserver.None"/>.
/// Events are display-only: nothing here drives routing, approval, or any decision, and the finished turn stays the
/// authoritative result. Implementations must be cheap and must not throw.
/// </summary>
public interface ITurnObserver
{
    void Started(TurnStarted started);

    void Progress(TurnProgress progress);

    /// <summary>A piece of reply text, possibly a model token. The final reply may still differ (the stream then resets).</summary>
    void Delta(string text);
}

public sealed record TurnStarted(string ConversationId, string TurnId, string? ClientTurnId, string? ResumedFromTurnId);

/// <summary>
/// One progress step. <paramref name="RouteTarget"/> and <paramref name="UnroutedReason"/> are contract names
/// (e.g. "orders", "ambiguous_request"); <paramref name="Tool"/> is a tool name, never arguments or message content.
/// </summary>
public sealed record TurnProgress(TurnStage Stage, string? RouteTarget = null, string? UnroutedReason = null, string? Tool = null);

/// <summary>The contract's progress stages.</summary>
public enum TurnStage
{
    Triage,
    Routed,
    Specialist,
    ToolCall,
    AwaitingApproval,
    ExecutingOperation,
}

public static class TurnObserver
{
    /// <summary>Ignores every event: used when nobody is listening.</summary>
    public static readonly ITurnObserver None = new NoObserver();

    private sealed class NoObserver : ITurnObserver
    {
        public void Started(TurnStarted started)
        {
        }

        public void Progress(TurnProgress progress)
        {
        }

        public void Delta(string text)
        {
        }
    }
}
