// @vitest-environment node
import { createServer, type IncomingMessage, type Server } from "node:http";
import type { AddressInfo } from "node:net";

import { forward } from "./proxy";

type Seen = { method: string; url: string; headers: IncomingMessage["headers"]; body: string };

// A real HTTP server standing in for the backend: it records what reached it and answers as told.
let server: Server;
let baseUrl: string;
let seen: Seen[];
let answer: (
  respond: (status: number, headers: Record<string, string>, body?: string) => void,
) => void;

beforeAll(async () => {
  server = createServer((request, response) => {
    let body = "";
    request.on("data", (chunk) => (body += chunk));
    request.on("end", () => {
      seen.push({ method: request.method!, url: request.url!, headers: request.headers, body });
      answer((status, headers, text) => response.writeHead(status, headers).end(text));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(() => {
  server.closeAllConnections(); // the slow-backend test leaves one open
  return new Promise<void>((resolve) => server.close(() => resolve()));
});

beforeEach(() => {
  seen = [];
  answer = (respond) => respond(200, { "content-type": "application/json" }, '{"id":"u_1"}');
});

function call(path: string[], init?: RequestInit, credentials: Record<string, string> = {}) {
  const query = init?.method === "POST" ? "" : "?after=abc&limit=2";
  return forward(new Request(`http://frontend.test/api/${path.join("/")}${query}`, init), path, {
    baseUrl,
    credentials,
  });
}

describe("the API proxy", () => {
  it("forwards the path and query and returns the backend's answer", async () => {
    const response = await call(["v1", "me"]);
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ id: "u_1" });
    expect(seen[0]).toMatchObject({ method: "GET", url: "/v1/me?after=abc&limit=2" });
  });

  it("keeps the base URL's own path", async () => {
    await forward(new Request("http://frontend.test/api/v1/me"), ["v1", "me"], {
      baseUrl: `${baseUrl}/backend`,
      credentials: {},
    });
    expect(seen[0]!.url).toBe("/backend/v1/me");
  });

  it("streams a request body through", async () => {
    const response = await call(["v1", "notes"], {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ text: "hello" }),
    });
    expect(response.status).toBe(200);
    expect(seen[0]).toMatchObject({ method: "POST", body: '{"text":"hello"}' });
    expect(seen[0]!.headers["content-type"]).toBe("application/json");
  });

  it("attaches the server's credentials and never the browser's", async () => {
    await call(
      ["v1", "me"],
      {
        headers: {
          cookie: "session=frontend-secret",
          authorization: "Bearer from-the-browser",
          "x-dev-user": "someone-else",
          "x-request-id": "req-1",
        },
      },
      { authorization: "Bearer from-the-server" },
    );
    const headers = seen[0]!.headers;
    expect(headers.authorization).toBe("Bearer from-the-server");
    expect(headers.cookie).toBeUndefined();
    expect(headers["x-dev-user"]).toBeUndefined();
    expect(headers["x-request-id"]).toBe("req-1");
  });

  it("passes the backend's errors on as they are", async () => {
    answer = (respond) =>
      respond(
        401,
        { "content-type": "application/problem+json", "www-authenticate": "Bearer" },
        '{"status":401}',
      );
    const response = await call(["v1", "me"]);
    expect(response.status).toBe(401);
    expect(response.headers.get("content-type")).toBe("application/problem+json");
    expect(response.headers.get("www-authenticate")).toBe("Bearer");
  });

  it("drops the backend's cookies and hop-by-hop headers", async () => {
    answer = (respond) =>
      respond(200, { "set-cookie": "backend=1", connection: "keep-alive", "x-request-id": "r" });
    const response = await call(["v1", "me"]);
    expect(response.headers.get("set-cookie")).toBeNull();
    expect(response.headers.get("connection")).toBeNull();
    expect(response.headers.get("x-request-id")).toBe("r");
  });

  it("hands a redirect back instead of following it", async () => {
    answer = (respond) => respond(307, { location: "https://elsewhere.example/" });
    const response = await call(["v1", "me"]);
    expect(response.status).toBe(307);
    expect(response.headers.get("location")).toBe("https://elsewhere.example/");
    expect(seen).toHaveLength(1);
  });

  it.each([[[".."]], [["v1", "..", "admin"]], [["v1", "a/b"]], [["v1", "a\\b"]], [[]]])(
    "refuses a path that could leave the API's base: %j",
    async (path) => {
      const response = await forward(new Request("http://frontend.test/api/x"), path, {
        baseUrl,
        credentials: {},
      });
      expect(response.status).toBe(404);
      expect(seen).toHaveLength(0);
    },
  );

  it("encodes each segment, so one can't add a query or fragment", async () => {
    await forward(new Request("http://frontend.test/api/x"), ["v1", "a?b#c"], {
      baseUrl,
      credentials: {},
    });
    expect(seen[0]!.url).toBe("/v1/a%3Fb%23c");
  });

  it("answers 503 when no backend is configured", async () => {
    const response = await forward(new Request("http://frontend.test/api/v1/me"), ["v1", "me"], {
      baseUrl: undefined,
      credentials: {},
    });
    expect(response.status).toBe(503);
    expect(response.headers.get("content-type")).toBe("application/problem+json");
  });

  it("answers 502 when the backend can't be reached", async () => {
    const response = await forward(new Request("http://frontend.test/api/v1/me"), ["v1", "me"], {
      baseUrl: "http://127.0.0.1:1",
      credentials: {},
    });
    expect(response.status).toBe(502);
    expect(await response.json()).toMatchObject({ status: 502, title: "Bad Gateway" });
  });

  it("answers 504 when the backend is too slow", async () => {
    answer = () => {}; // never answers
    const response = await forward(new Request("http://frontend.test/api/v1/me"), ["v1", "me"], {
      baseUrl,
      credentials: {},
      timeoutMs: 50,
    });
    expect(response.status).toBe(504);
  });
});
