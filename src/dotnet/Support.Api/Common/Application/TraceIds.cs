using System.Diagnostics;

namespace Support.Api.Common.Application;

public static class TraceIds
{
    /// <summary>The current W3C trace id, so a client can quote it and we can find the request in telemetry.</summary>
    public static string Current() =>
        (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();
}
