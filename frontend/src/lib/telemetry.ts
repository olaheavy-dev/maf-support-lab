/**
 * The frontend's OpenTelemetry settings (src/instrumentation.ts registers them), matching the
 * backend's: traces go over OTLP HTTP/protobuf to OTEL_EXPORTER_OTLP_ENDPOINT (traces at
 * /v1/traces) with OTEL_EXPORTER_OTLP_HEADERS, and health checks are left out so probes don't
 * drown the real traffic. Calls to the backend carry the W3C traceparent header, so a request's
 * spans in the frontend and in the API form one trace.
 */
import {
  AlwaysOnSampler,
  ParentBasedSampler,
  SamplingDecision,
  type Sampler,
  type SamplingResult,
} from "@opentelemetry/sdk-trace-base";
import type { Configuration } from "@vercel/otel";

type Env = Record<string, string | undefined>;

// The liveness check (/health/live) and the backend's checks through the proxy (/api/health/*).
const HEALTH_CHECK = /^\/(api\/)?health\//;

/** Drops health check requests, whatever the caller decided; everything else goes to `next`. */
export class SkipHealthChecks implements Sampler {
  constructor(private readonly next: Sampler) {}

  shouldSample(...args: Parameters<Sampler["shouldSample"]>): SamplingResult {
    const target = args[4]["http.target"];
    if (typeof target === "string" && HEALTH_CHECK.test(target)) {
      return { decision: SamplingDecision.NOT_RECORD };
    }
    return this.next.shouldSample(...args);
  }

  toString(): string {
    return `SkipHealthChecks{${this.next.toString()}}`;
  }
}

export function telemetryConfig(env: Env): Configuration {
  const backend = env.API_BASE_URL?.replace(/\/*$/, "/");
  return {
    serviceName: env.OTEL_SERVICE_NAME || "web",
    // Every trace is kept, as the backend keeps them, and a span whose caller sampled follows its
    // caller; to keep a fraction, use a TraceIdRatioBasedSampler as the root. The health check
    // filter goes outside: a parent-based sampler never asks its root when the caller (a probe
    // from an instrumented load balancer, say) sent a sampled traceparent.
    traceSampler: new SkipHealthChecks(new ParentBasedSampler({ root: new AlwaysOnSampler() })),
    // traceparent goes to the backend only, never to other hosts the server fetches from.
    instrumentationConfig: { fetch: { propagateContextUrls: backend ? [backend] : [] } },
  };
}
