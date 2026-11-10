using System.Runtime.CompilerServices;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Turns the standard GenAI content-capture variable ON for the whole test process, before any test runs.
/// Microsoft.Extensions.AI reads it once per process (a static default), so it cannot be flipped inside a test.
/// With it on everywhere, the telemetry test that finds no message text proves the app's explicit
/// Telemetry:CaptureSensitiveData=false wins over the variable.
/// </summary>
internal static class GenAiCaptureVariable
{
    public const string Name = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

#pragma warning disable CA2255 // Intended: must run before Microsoft.Extensions.AI reads the variable.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void TurnOn() => Environment.SetEnvironmentVariable(Name, "true");
}
