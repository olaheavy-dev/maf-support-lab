using Microsoft.Agents.AI.Workflows;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>The real orders specialist: the L05 orders agent and its tool, given the customer's original message.</summary>
[YieldsOutput(typeof(SupportReply))]
internal sealed partial class OrdersSpecialistExecutor(SupportAgents agents) : Executor<TriageOutcome>("orders-specialist")
{
    public override async ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The agent gets the original text; the principal reaches the tool through trusted DI, never through this payload.
        using var activity = TurnActivity.Begin();
        var response = await agents.Orders.RunAsync(request.OriginalMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
        var reply = new SupportReply(request.Route, ReplyKindFrom(activity.Activity), ReplyText(response.Text));
        await context.YieldOutputAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The reply kind comes from what the tools actually did, not from the model's wording.</summary>
    private static ReplyKind ReplyKindFrom(TurnActivity activity)
    {
        var lookups = activity.OrderLookups;
        if (lookups.Contains(OrderLookupOutcome.Found))
        {
            return ReplyKind.Answer;
        }

        return lookups.Contains(OrderLookupOutcome.NotFound) ? ReplyKind.NotFound : ReplyKind.Clarification;
    }

    private static string ReplyText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't produce an answer. Please try again." : text.Trim();
}

/// <summary>An order question without an order id: ask for it, deterministically, without spending a model call.</summary>
[YieldsOutput(typeof(SupportReply))]
internal sealed partial class AskForOrderIdExecutor() : Executor<TriageOutcome>("ask-for-order-id")
{
    public const string Text = "Which order do you mean? Please share the order id, for example ORD-1234.";

    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return context.YieldOutputAsync(new SupportReply(request.Route, ReplyKind.Clarification, Text), cancellationToken);
    }
}

/// <summary>
/// PLACEHOLDER until L08: billing and technical have no specialist yet. Routing to them works and is tested; the reply
/// says honestly that this help is not available in this build. It is not an operational specialist.
/// </summary>
[YieldsOutput(typeof(SupportReply))]
internal sealed partial class PlaceholderSpecialistExecutor(RouteTarget target) : Executor<TriageOutcome>($"{Names.Of(target)}-placeholder")
{
    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var text = $"I can see this is a {Names.Of(target)} question, but {Names.Of(target)} help isn't available in this version yet. Your request has been noted.";
        return context.YieldOutputAsync(new SupportReply(request.Route, ReplyKind.EscalationOffer, text), cancellationToken);
    }
}

/// <summary>The fallback for invalid or ambiguous triage: never guess a route; ask the customer instead.</summary>
[YieldsOutput(typeof(SupportReply))]
internal sealed partial class UnroutedExecutor() : Executor<TriageOutcome>("unrouted")
{
    public const string Text = "I'm not sure what you need. Is this about an order, billing, or a technical problem?";

    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        // The default branch also catches anything no case matched, so make sure the route says "unrouted".
        var route = request.Route.IsRouted ? TurnRoute.Unrouted(UnroutedReason.AmbiguousRequest) : request.Route;
        return context.YieldOutputAsync(new SupportReply(route, ReplyKind.Clarification, Text), cancellationToken);
    }
}
