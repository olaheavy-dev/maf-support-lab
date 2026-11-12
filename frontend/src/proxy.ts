// Runs before every page request: sets the Content Security Policy (src/lib/csp.ts). The other
// security headers are static and come from next.config.ts.
import { NextResponse, type NextRequest } from "next/server";

import { connectOrigins, contentSecurityPolicy, usesNonce } from "@/lib/csp";

const CSP = "content-security-policy";

export function proxy(request: NextRequest) {
  const options = {
    dev: process.env.NODE_ENV === "development",
    // Read per request, like API_BASE_URL, so one build serves every environment.
    connectSrc: connectOrigins(process.env.CSP_CONNECT_SRC),
  };
  if (!usesNonce(request.nextUrl.pathname)) {
    const response = NextResponse.next();
    response.headers.set(CSP, contentSecurityPolicy(options));
    return response;
  }
  const nonce = btoa(crypto.randomUUID());
  const policy = contentSecurityPolicy({ ...options, nonce });
  // Next.js reads the nonce from the request's policy and puts it on every script it renders.
  const headers = new Headers(request.headers);
  headers.set(CSP, policy);
  const response = NextResponse.next({ request: { headers } });
  response.headers.set(CSP, policy);
  return response;
}

export const config = {
  matcher: [
    {
      // Pages only: not the API proxy, build assets or prefetches (which aren't documents).
      source: "/((?!api(?:/|$)|_next/static|_next/image|favicon.ico).*)",
      missing: [
        { type: "header", key: "next-router-prefetch" },
        { type: "header", key: "purpose", value: "prefetch" },
      ],
    },
  ],
};
