using Microsoft.Agents.AI.Workflows;
using Support.Api.Common.Application.Streaming;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// A progress step raised by an executor with <c>IWorkflowContext.AddEventAsync</c>. The router reads these from
/// the workflow's native event stream (<c>WatchStreamAsync</c>) and forwards them to the turn's observer, so executors
/// never know whether anyone is streaming. DevUI ignores them: it only forwards agent response updates.
/// </summary>
internal sealed class SupportProgressEvent(TurnProgress progress) : WorkflowEvent(progress)
{
    public TurnProgress Progress { get; } = progress;
}

/// <summary>A piece of the specialist's reply text, as the model streams it. Display-only.</summary>
internal sealed class SupportReplyDeltaEvent(string text) : WorkflowEvent(text)
{
    public string Text { get; } = text;
}
