/**
 * The same-origin API proxy behind `/api/*` (src/app/api/[...path]/route.ts).
 *
 * The browser calls `/api/v1/...` on the frontend's own origin, so the backend needs no CORS and
 * its credentials never reach the browser: this forwards the request to `API_BASE_URL`, read on
 * every request (Next.js rewrites are fixed at build time, so one build couldn't point at either
 * backend). Only a short list of request headers goes through; the browser's cookies and
 * Authorization never do, and `backendCredentials` (src/lib/auth.ts) adds nothing, because the
 * backends own identity. Bodies stream both ways, so a turn's Server-Sent Events arrive as sent.
 */

// Request headers the backend may see. Everything else, cookies and Authorization above all, stays
// with the frontend.
const FORWARDED_REQUEST_HEADERS = [
  "accept",
  "accept-language",
  "content-type",
  "if-match",
  "if-none-match",
  "x-request-id",
];

// Hop-by-hop headers (RFC 9110 section 7.6.1), the encoding and length fetch has already undone,
// and cookies: the backend's cookies must not land on the frontend's origin.
const DROPPED_RESPONSE_HEADERS = [
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-connection",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
  "content-encoding",
  "content-length",
  "set-cookie",
];

// Longer than the backends' turn deadline (90 s, Resilience:TurnTimeoutSeconds), so a slow streamed
// turn ends with its own turn.final instead of being cut off here.
export const TIMEOUT_MS = 120_000;

export type ProxyOptions = {
  /** Where the backend's API is, e.g. http://127.0.0.1:5180/api; unset answers 503. */
  baseUrl: string | undefined;
  /** Headers that say who is calling, from backendCredentials. */
  credentials: Record<string, string>;
  fetch?: typeof fetch;
  timeoutMs?: number;
};

/** An RFC 9457 problem, the shape the backend's own errors have. */
export function problem(status: number, title: string, detail: string): Response {
  return Response.json(
    { type: "about:blank", title, status, detail },
    {
      status,
      headers: { "content-type": "application/problem+json", "cache-control": "no-store" },
    },
  );
}

/** A path segment that could climb out of the API's base path, or address no resource. */
function unsafe(segment: string): boolean {
  return segment === "" || segment === "." || segment === ".." || /[/\\]/.test(segment);
}

export async function forward(
  request: Request,
  path: string[],
  options: ProxyOptions,
): Promise<Response> {
  const { baseUrl, credentials, fetch: send = fetch, timeoutMs = TIMEOUT_MS } = options;
  if (!baseUrl) {
    return problem(503, "Service Unavailable", "The API is not configured: set API_BASE_URL.");
  }
  if (path.length === 0 || path.some(unsafe)) {
    return problem(404, "Not Found", "No such API path.");
  }

  const base = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  const target = new URL(path.map(encodeURIComponent).join("/"), base);
  target.search = new URL(request.url).search;

  const headers = new Headers();
  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value !== null) headers.set(name, value);
  }
  for (const [name, value] of Object.entries(credentials)) headers.set(name, value);

  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  const init: RequestInit & { duplex?: "half" } = {
    method: request.method,
    headers,
    body: hasBody ? request.body : undefined,
    // A streamed request body needs half-duplex (the Fetch standard; Node's fetch insists).
    duplex: hasBody ? "half" : undefined,
    // A redirect goes back to the browser as it is, never followed with the user's credentials.
    redirect: "manual",
    signal: AbortSignal.any([request.signal, AbortSignal.timeout(timeoutMs)]),
  };

  let upstream: Response;
  try {
    upstream = await send(target, init);
  } catch (error) {
    if (error instanceof DOMException && error.name === "TimeoutError") {
      return problem(504, "Gateway Timeout", "The API took too long to answer.");
    }
    return problem(502, "Bad Gateway", "The API could not be reached.");
  }

  const responseHeaders = new Headers(upstream.headers);
  for (const name of DROPPED_RESPONSE_HEADERS) responseHeaders.delete(name);
  return new Response(upstream.body, {
    status: upstream.status,
    statusText: upstream.statusText,
    headers: responseHeaders,
  });
}
