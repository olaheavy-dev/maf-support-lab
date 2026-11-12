// @vitest-environment node
import { exportConfigured } from "./instrumentation";

describe("instrumentation", () => {
  it("sets up telemetry only when an OTLP endpoint is configured", () => {
    expect(exportConfigured({})).toBe(false);
    expect(exportConfigured({ OTEL_EXPORTER_OTLP_ENDPOINT: "" })).toBe(false);
    expect(exportConfigured({ OTEL_EXPORTER_OTLP_ENDPOINT: "http://collector:4318" })).toBe(true);
    expect(
      exportConfigured({ OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: "http://collector:4318/v1/traces" }),
    ).toBe(true);
  });
});
