using System.Diagnostics;

namespace Support.Api.Modules.Agents.Application;

/// <summary>
/// Telemetry names for the AI layers. Two names keep the layers apart in Aspire and let one of them be filtered:
/// the agent layer (invoke_agent and execute_tool spans, whole-run duration) and the model layer (one chat span,
/// duration, and token usage per model call).
/// </summary>
public static class AgentTelemetry
{
    public const string AgentSourceName = "Support.Agents";

    public const string ModelSourceName = "Support.Model";

    /// <summary>Workflow layer: invoke_workflow and executor spans from MAF Workflows.</summary>
    public const string WorkflowSourceName = "Support.Workflows";

    /// <summary>Handed to MAF's WithOpenTelemetry so workflow spans use our name instead of the library default.</summary>
    public static readonly ActivitySource WorkflowActivitySource = new(WorkflowSourceName);
}

/// <summary>Bound from the "Telemetry" configuration section.</summary>
public sealed class AgentTelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Opt-in. When true, prompts, replies, tool arguments, and tool results are written into spans. Off by default,
    /// and always set explicitly so the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT environment variable
    /// cannot switch it on by accident.
    /// </summary>
    public bool CaptureSensitiveData { get; set; }
}
