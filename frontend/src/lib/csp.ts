/**
 * The Content Security Policy, set on every page by src/proxy.ts.
 *
 * Two policies, because a nonce needs the page rendered per request (Next.js 16's CSP guide):
 *
 * - Prerendered pages (the default under Cache Components) are built once, before any request, so
 *   their inline scripts can't carry a nonce. They get `script-src 'self' 'unsafe-inline'`: no
 *   scripts from other origins, but inline ones run. React escapes what it renders, and the
 *   rest of the policy still blocks plugins, framing, other origins and form posts elsewhere.
 * - Paths in NONCE_PATHS get a fresh nonce per request and `'strict-dynamic'`: only scripts
 *   Next.js rendered for that response run. Use it where a page shows sensitive data (account,
 *   billing, admin). Each such page must render per request: `export const instant = false` and
 *   `await connection()` first, or its prerendered scripts carry no nonce and the browser
 *   blocks them (the end-to-end tests fail on any CSP violation).
 */

/** Path prefixes whose pages get the nonce policy, e.g. ["/account"]. */
export const NONCE_PATHS: readonly string[] = [];

export function usesNonce(pathname: string, paths: readonly string[] = NONCE_PATHS): boolean {
  return paths.some((path) => pathname === path || pathname.startsWith(`${path}/`));
}

// An origin a page may fetch from, as CSP_CONNECT_SRC lists them: scheme, host, optional port.
const ORIGIN = /^(https|wss|http|ws):\/\/(\*\.)?[a-z0-9.-]+(:\d{1,5})?$/i;

/** The extra origins in CSP_CONNECT_SRC (space-separated), keeping only well-formed ones. */
export function connectOrigins(value: string | undefined): string[] {
  return (value ?? "").split(/\s+/).filter((origin) => ORIGIN.test(origin));
}

export type PolicyOptions = {
  /** A per-request nonce: present only for NONCE_PATHS. */
  nonce?: string;
  /** `next dev`: React needs eval for its error overlays (never in production). */
  dev?: boolean;
  /** Origins the browser may call besides this one, e.g. the object store for uploads. */
  connectSrc?: readonly string[];
};

export function contentSecurityPolicy({ nonce, dev = false, connectSrc = [] }: PolicyOptions) {
  const scripts = nonce ? [`'nonce-${nonce}'`, "'strict-dynamic'"] : ["'unsafe-inline'"];
  const directives: Record<string, string[]> = {
    "default-src": ["'self'"],
    "script-src": ["'self'", ...scripts, ...(dev ? ["'unsafe-eval'"] : [])],
    // React and Next.js set inline styles; style injection can't run code.
    "style-src": ["'self'", "'unsafe-inline'"],
    "img-src": ["'self'", "blob:", "data:"],
    "font-src": ["'self'"],
    "connect-src": ["'self'", ...connectSrc],
    "object-src": ["'none'"],
    "base-uri": ["'self'"],
    "form-action": ["'self'"],
    "frame-ancestors": ["'none'"],
  };
  return Object.entries(directives)
    .map(([name, values]) => `${name} ${values.join(" ")}`)
    .join("; ");
}
