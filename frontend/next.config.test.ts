// @vitest-environment node
import { unstable_getResponseFromNextConfig } from "next/experimental/testing/server";

async function headersFor(url: string, env: "production" | "development") {
  vi.resetModules();
  vi.stubEnv("NODE_ENV", env);
  const { default: nextConfig } = await import("./next.config");
  const response = await unstable_getResponseFromNextConfig({ url, nextConfig });
  return response.headers;
}

describe("the security headers", () => {
  afterEach(() => vi.unstubAllEnvs());

  it.each(["/", "/api/v1/me", "/_next/static/chunks/a.js"])(
    "are on every response: %s",
    async (url) => {
      const headers = await headersFor(url, "production");
      expect(headers.get("x-content-type-options")).toBe("nosniff");
      expect(headers.get("x-frame-options")).toBe("DENY");
      expect(headers.get("referrer-policy")).toBe("no-referrer");
      expect(headers.get("cross-origin-opener-policy")).toBe("same-origin");
      expect(headers.get("permissions-policy")).toContain("camera=()");
      expect(headers.get("strict-transport-security")).toBe("max-age=31536000");
    },
  );

  it("pin HTTPS in production builds only", async () => {
    expect((await headersFor("/", "development")).get("strict-transport-security")).toBeNull();
  });
});

describe("the coding-agent settings", () => {
  it("keep the docs block in AGENTS.md and leave agent feedback off", async () => {
    const { default: nextConfig } = await import("./next.config");
    expect(nextConfig.agentRules).not.toBe(false);
    expect(nextConfig.experimental?.agentFeedback).toBe(false);
  });
});
