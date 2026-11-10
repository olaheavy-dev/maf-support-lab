using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Tests.Modules.Conversations;

/// <summary>Codex review on PR #78: a status lookup reads "running" and "finished" under one lock, so it never sees neither.</summary>
public sealed class TurnStatusTests
{
    [Fact]
    public async Task A_turn_is_running_then_finished_and_never_missing_in_between()
    {
        var conversation = Conversation.Start(new SupportPrincipal("CUST-1001"), DateTimeOffset.UtcNow);
        var (reservation, _) = conversation.Reserve(Guid.NewGuid().ToString(), "Hi");
        var turnId = reservation!.TurnId;

        var whileRunning = conversation.FindForStatus(turnId);
        Assert.Same(reservation, whileRunning.Running);
        Assert.Null(whileRunning.Finished);

        // Lookups racing with completion always see one of the two states.
        var turn = new Turn(conversation.ConversationId, turnId, null, TurnRoute.Routed(RouteTarget.Orders), ReplyKind.Answer, "Done", "trace", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var missing = 0;
        var polling = Task.Run(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                var (finished, running) = conversation.FindForStatus(turnId);
                if (finished is null && running is null)
                {
                    Interlocked.Increment(ref missing);
                }
            }
        }, TestContext.Current.CancellationToken);
        conversation.Complete(reservation, turn, reply: null);
        await polling;

        Assert.Equal(0, missing);
        Assert.Same(turn, conversation.FindForStatus(turnId).Finished);
        Assert.Null(conversation.FindForStatus(turnId).Running);
    }
}
