import type { NextConfig } from "next";

// Headers every response gets, pages, assets and the /api proxy alike. The Content Security Policy
// varies per request, so src/proxy.ts sets it.
export const SECURITY_HEADERS = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  // frame-ancestors in the CSP does the same for current browsers.
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Referrer-Policy", value: "no-referrer" },
  { key: "Cross-Origin-Opener-Policy", value: "same-origin" },
  {
    key: "Permissions-Policy",
    value: "camera=(), microphone=(), geolocation=(), browsing-topics=()",
  },
  // HTTPS only, for a year, once a browser has seen the site over HTTPS (ignored over plain HTTP).
  // Production builds only, so local HTTP on another port is never pinned.
  ...(process.env.NODE_ENV === "production"
    ? [{ key: "Strict-Transport-Security", value: "max-age=31536000" }]
    : []),
];

const nextConfig: NextConfig = {
  // The minimal standalone server (`node server.js`), which the end-to-end tests run.
  output: "standalone",
  // No X-Powered-By: it tells a scanner which framework to try.
  poweredByHeader: false,
  // Next.js 16's caching model: pages are static unless they read request data or opt out.
  cacheComponents: true,
  partialPrefetching: true,
  // agentRules (on by default) keeps the AGENTS.md block that points coding agents at the docs in
  // node_modules/next/dist/docs/. Agent feedback, which has agents draft issue reports for
  // Next.js, stays off: create-next-app turns it on, so say so explicitly.
  experimental: { agentFeedback: false },
  async headers() {
    return [{ source: "/:path*", headers: SECURITY_HEADERS }];
  },
  // Tailwind v4 through Turbopack's loader, as create-next-app 16.4 sets it up.
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
