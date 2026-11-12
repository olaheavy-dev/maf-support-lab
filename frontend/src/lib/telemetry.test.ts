// @vitest-environment node
import { type Context, ROOT_CONTEXT, SpanKind, TraceFlags, trace } from "@opentelemetry/api";
import { AlwaysOnSampler, SamplingDecision } from "@opentelemetry/sdk-trace-base";
import { SkipHealthChecks, telemetryConfig } from "./telemetry";

function decide(
  sampler: { shouldSample: SkipHealthChecks["shouldSample"] },
  target: string,
  context: Context = ROOT_CONTEXT,
) {
  return sampler.shouldSample(
    context,
    "0af7651916cd43dd8448eb211c80319c",
    "GET",
    SpanKind.SERVER,
    { "http.method": "GET", "http.target": target },
    [],
  ).decision;
}

describe("telemetry", () => {
  it("leaves out health checks and keeps everything else", () => {
    const sampler = telemetryConfig({}).traceSampler as SkipHealthChecks;
    expect(decide(sampler, "/health/live")).toBe(SamplingDecision.NOT_RECORD);
    expect(decide(sampler, "/api/health/ready")).toBe(SamplingDecision.NOT_RECORD);
    expect(decide(sampler, "/")).toBe(SamplingDecision.RECORD_AND_SAMPLED);
    expect(decide(sampler, "/api/v1/me")).toBe(SamplingDecision.RECORD_AND_SAMPLED);
    expect(decide(sampler, "/healthy-recipes")).toBe(SamplingDecision.RECORD_AND_SAMPLED);
    expect(decide(new SkipHealthChecks(new AlwaysOnSampler()), "/health/live")).toBe(
      SamplingDecision.NOT_RECORD,
    );
  });

  it("leaves out health checks whose caller sampled them", () => {
    // A probe from an instrumented load balancer or monitor arrives with a sampled traceparent.
    const sampledCaller = trace.setSpanContext(ROOT_CONTEXT, {
      traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
      spanId: "00f067aa0ba902b7",
      traceFlags: TraceFlags.SAMPLED,
      isRemote: true,
    });
    const sampler = telemetryConfig({}).traceSampler as SkipHealthChecks;
    expect(decide(sampler, "/health/live", sampledCaller)).toBe(SamplingDecision.NOT_RECORD);
    expect(decide(sampler, "/api/v1/me", sampledCaller)).toBe(SamplingDecision.RECORD_AND_SAMPLED);
  });

  it("sends the trace context to the backend and nowhere else", () => {
    const fetch = (env: Record<string, string>) =>
      telemetryConfig(env).instrumentationConfig?.fetch;
    expect(fetch({ API_BASE_URL: "http://api:8000" })?.propagateContextUrls).toEqual([
      "http://api:8000/",
    ]);
    expect(fetch({ API_BASE_URL: "https://api.example.com/base/" })?.propagateContextUrls).toEqual([
      "https://api.example.com/base/",
    ]);
    expect(fetch({})?.propagateContextUrls).toEqual([]);
  });

  it("names the service web unless OTEL_SERVICE_NAME says otherwise", () => {
    expect(telemetryConfig({}).serviceName).toBe("web");
    expect(telemetryConfig({ OTEL_SERVICE_NAME: "course-lens-web" }).serviceName).toBe(
      "course-lens-web",
    );
  });
});
