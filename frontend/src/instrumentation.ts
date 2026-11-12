// OpenTelemetry for the server (https://nextjs.org/docs/app/guides/open-telemetry), set up through
// @vercel/otel: Next.js's own spans for every request, and a span for each fetch. As in the backend,
// nothing is set up, or even loaded, until an OTLP endpoint is configured; src/lib/telemetry.ts
// has the settings.

type Env = Record<string, string | undefined>;

export function exportConfigured(env: Env): boolean {
  return Boolean(env.OTEL_EXPORTER_OTLP_ENDPOINT || env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT);
}

export async function register() {
  if (process.env.NEXT_RUNTIME !== "nodejs" || !exportConfigured(process.env)) return;
  const [{ registerOTel }, { telemetryConfig }] = await Promise.all([
    import("@vercel/otel"),
    import("@/lib/telemetry"),
  ]);
  registerOTel(telemetryConfig(process.env));
}
