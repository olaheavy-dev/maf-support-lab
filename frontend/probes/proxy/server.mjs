// Browser-probe proxy (L22): serves the probe page and forwards /api/{alias}/v1/... to a backend chosen by alias.
//
// - The browser only ever uses relative URLs. Backend addresses are server-side: Aspire passes them as
//   services__<resource>__http__0 (WithReference), and tests pass the same variables. No backend URL, Aspire service
//   name, or provider key reaches the browser.
// - The alias allowlist is fixed here; a request can never name an arbitrary upstream.
// - Streams are forwarded as they arrive (no buffering), so Server-Sent Events reach the browser live.
// - Zero dependencies (node:http only): this is a disposable probe, not the L23 frontend.
//
// Run: PORT=5300 SUPPORT_API_DOTNET_HTTP=http://localhost:5180 node server.mjs
//
// Addresses: Aspire's WithReference sets both SUPPORT_API_<NAME>_HTTP(S) and services__<resource>__http(s)__0. The
// shell-safe names are read first: POSIX shells such as Ubuntu's dash drop variables whose names contain "-", so the
// services__… form vanishes whenever the proxy is started through a shell (found in CI). Under Aspire a backend may be
// https only (the Python app gets Aspire's development certificate); Aspire then sets NODE_EXTRA_CA_CERTS, so Node
// trusts it without verification ever being turned off.

import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import http from "node:http";
import https from "node:https";
import { dirname, join } from "node:path";
import { pipeline } from "node:stream";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// Alias -> the Aspire resource whose URL backs it. The only upstreams this proxy will ever call.
const BACKENDS = { dotnet: "support-api-dotnet", python: "support-api-python" };

export function upstreams(env = process.env) {
  // No prototype: an alias like "constructor" or "__proto__" must never resolve to anything (Codex review on PR #97).
  const found = Object.create(null);
  for (const [alias, resource] of Object.entries(BACKENDS)) {
    const key = resource.toUpperCase().replaceAll("-", "_");
    const url =
      env[`${key}_HTTP`] ??
      env[`${key}_HTTPS`] ??
      env[`services__${resource}__http__0`] ??
      env[`services__${resource}__https__0`];
    if (url) found[alias] = new URL(url);
  }
  return found;
}

// The page and its script, read once. Only these paths are served: no directory access at all.
const STATIC = {
  "/": { type: "text/html; charset=utf-8", body: readFileSync(join(here, "public", "index.html")) },
  "/probe.js": { type: "text/javascript; charset=utf-8", body: readFileSync(join(here, "public", "probe.js")) },
};

// Headers the backends need; nothing else from the browser is forwarded (no cookies, no identity headers).
const FORWARD_REQUEST = ["content-type", "accept", "content-length"];
const FORWARD_RESPONSE = ["content-type", "cache-control", "x-accel-buffering", "content-length"];
const API = /^\/api\/([a-z]+)(\/v1\/[A-Za-z0-9_\-/.]*)$/;

function problem(response, status, code, title, detail) {
  // The contract's problem shape, so the browser renders the proxy's errors like any other.
  response.writeHead(status, { "content-type": "application/problem+json" });
  response.end(
    JSON.stringify({
      contract_version: "1.1",
      type: `https://contracts.maf-support-lab.example/v1/errors/${code}`,
      title,
      status,
      code,
      detail,
      trace_id: randomBytes(16).toString("hex"),
    }),
  );
}

export function createProxy(targets = upstreams()) {
  return http.createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://probe.local");

    if (request.method === "GET" && url.pathname === "/health") {
      response.writeHead(200, { "content-type": "text/plain" }).end("Healthy");
      return;
    }
    if (request.method === "GET" && url.pathname === "/api/backends") {
      // Aliases only, never addresses: the browser picks one of these when it starts a conversation.
      response.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
      response.end(JSON.stringify({ backends: Object.keys(targets) }));
      return;
    }
    const page = request.method === "GET" ? STATIC[url.pathname] : undefined;
    if (page) {
      response.writeHead(200, { "content-type": page.type, "cache-control": "no-store" }).end(page.body);
      return;
    }

    const match = API.exec(url.pathname);
    const target = match && Object.hasOwn(targets, match[1]) ? targets[match[1]] : undefined;
    if (!target || match[2].includes("..") || !["GET", "POST"].includes(request.method ?? "")) {
      problem(response, 404, "not_found", "Not found", "The resource does not exist or is not available to you.");
      return;
    }

    const headers = {};
    for (const name of FORWARD_REQUEST) {
      if (request.headers[name]) headers[name] = request.headers[name];
    }
    // The upstream's own loopback host: .NET's Development DevUI guard admits only a localhost Host header on
    // /api/v1/conversations, and the browser's Host (this proxy) is not the backend's.
    headers.host = target.host;

    const client = target.protocol === "https:" ? https : http;
    const upstream = client.request(
      { protocol: target.protocol, hostname: target.hostname, port: target.port, method: request.method, path: `/api${match[2]}${url.search}`, headers },
      (reply) => {
        const forwarded = {};
        for (const name of FORWARD_RESPONSE) {
          if (reply.headers[name]) forwarded[name] = reply.headers[name];
        }
        response.writeHead(reply.statusCode ?? 502, forwarded);
        response.flushHeaders();
        // Chunk by chunk (SSE events are not held back). pipeline, not pipe: if the backend drops the connection
        // mid-body, the browser's response is destroyed too, so the page sees the interruption instead of waiting
        // forever (Codex review on PR #97).
        pipeline(reply, response, (error) => {
          if (error) response.destroy();
        });
      },
    );
    upstream.on("error", () => {
      if (!response.headersSent) {
        problem(response, 503, "service_unavailable", "Service unavailable", "That backend is not reachable right now.");
      } else {
        response.destroy();
      }
    });
    // A browser that goes away stops waiting; the backend's turn is detached and runs on (contract: a disconnect is
    // not a cancellation), so the browser can recover it later by turn id.
    response.on("close", () => {
      if (!response.writableFinished) upstream.destroy();
    });
    request.pipe(upstream);
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const targets = upstreams();
  if (Object.keys(targets).length === 0) {
    console.error("No backends configured: set SUPPORT_API_DOTNET_HTTP and/or SUPPORT_API_PYTHON_HTTP (Aspire sets them).");
    process.exit(1);
  }
  const port = Number(process.env.PORT ?? 5300);
  createProxy(targets).listen(port, "127.0.0.1", () => {
    console.log(`Browser probe on http://127.0.0.1:${port} (backends: ${Object.keys(targets).join(", ")})`);
  });
}
