// @vitest-environment node
import { connectOrigins, contentSecurityPolicy, usesNonce } from "./csp";

function directives(policy: string): Record<string, string[]> {
  return Object.fromEntries(
    policy.split("; ").map((directive) => {
      const [name, ...values] = directive.split(" ");
      return [name!, values];
    }),
  );
}

describe("the Content Security Policy", () => {
  it("lets a prerendered page run its own inline scripts and nothing from elsewhere", () => {
    const policy = directives(contentSecurityPolicy({}));
    expect(policy["script-src"]).toEqual(["'self'", "'unsafe-inline'"]);
    expect(policy["object-src"]).toEqual(["'none'"]);
    expect(policy["frame-ancestors"]).toEqual(["'none'"]);
    expect(policy["base-uri"]).toEqual(["'self'"]);
    expect(policy["form-action"]).toEqual(["'self'"]);
    expect(policy["connect-src"]).toEqual(["'self'"]);
  });

  it("allows only the response's own scripts on a nonce page", () => {
    const policy = directives(contentSecurityPolicy({ nonce: "abc123" }));
    expect(policy["script-src"]).toEqual(["'self'", "'nonce-abc123'", "'strict-dynamic'"]);
    expect(policy["script-src"]).not.toContain("'unsafe-inline'");
  });

  it("allows eval only in development", () => {
    expect(directives(contentSecurityPolicy({ dev: true }))["script-src"]).toContain(
      "'unsafe-eval'",
    );
    expect(contentSecurityPolicy({})).not.toContain("unsafe-eval");
  });

  it("adds the origins the browser may call", () => {
    const connectSrc = ["https://files.example.com"];
    expect(directives(contentSecurityPolicy({ connectSrc }))["connect-src"]).toEqual([
      "'self'",
      "https://files.example.com",
    ]);
  });
});

describe("CSP_CONNECT_SRC", () => {
  it("keeps well-formed origins", () => {
    expect(
      connectOrigins(" https://acct.r2.cloudflarestorage.com  wss://live.example.com:8443 "),
    ).toEqual(["https://acct.r2.cloudflarestorage.com", "wss://live.example.com:8443"]);
    expect(connectOrigins("https://*.example.com")).toEqual(["https://*.example.com"]);
    expect(connectOrigins(undefined)).toEqual([]);
  });

  it("drops anything that could add a directive or a wildcard source", () => {
    expect(connectOrigins("https://a.example.com;script-src *")).toEqual([]);
    expect(connectOrigins("* 'unsafe-inline' data: https://ok.example.com/path")).toEqual([]);
  });
});

describe("nonce paths", () => {
  it("cover the path and everything under it, and nothing that only starts the same", () => {
    const paths = ["/account"];
    expect(usesNonce("/account", paths)).toBe(true);
    expect(usesNonce("/account/billing", paths)).toBe(true);
    expect(usesNonce("/accounts", paths)).toBe(false);
    expect(usesNonce("/", paths)).toBe(false);
  });
});
