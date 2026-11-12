import { expect, test } from "@playwright/test";

// The production server's real responses, not just the config: the proxy's CSP and next.config's
// headers both arrive, on prerendered pages and on the 404.
test.describe("security headers", () => {
  test.skip(
    ({ isMobile, colorScheme }) => isMobile || colorScheme === "dark",
    "the same server answers every project: once is enough",
  );

  for (const [path, status] of [
    ["/", 200],
    ["/a-page-that-does-not-exist", 404],
  ] as const) {
    test(`${path} answers ${status} with the security headers`, async ({ request }) => {
      const response = await request.get(path);
      expect(response.status()).toBe(status);
      const headers = response.headers();
      expect(headers["content-security-policy"]).toContain("frame-ancestors 'none'");
      expect(headers["content-security-policy"]).toContain("object-src 'none'");
      expect(headers["x-content-type-options"]).toBe("nosniff");
      expect(headers["x-frame-options"]).toBe("DENY");
      expect(headers["referrer-policy"]).toBe("no-referrer");
      expect(headers["strict-transport-security"]).toBe("max-age=31536000");
      expect(headers["x-powered-by"]).toBeUndefined();
    });
  }
});
