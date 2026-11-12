import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

// Every page of the product, so each is checked for accessibility, CSP violations and errors in
// light, dark and mobile, and compared with its screenshot. Add each new page here (with real ids
// for dynamic routes), then make its screenshots with `pnpm build && pnpm e2e:docker --update-snapshots=changed`.
const PAGES = ["/", "/a-page-that-does-not-exist"];

// WCAG 2.2 A and AA, as axe tags them.
const WCAG = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

declare global {
  interface Window {
    cspViolations: string[];
  }
}

test.beforeEach(async ({ page }) => {
  // Injected by the browser's debugger, so the page's own policy doesn't block it.
  await page.addInitScript(() => {
    window.cspViolations = [];
    document.addEventListener("securitypolicyviolation", (event) => {
      window.cspViolations.push(`${event.violatedDirective} ${event.blockedURI}`);
    });
  });
});

function screenshotName(path: string) {
  return path === "/" ? "home" : path.slice(1).replaceAll("/", "-");
}

for (const path of PAGES) {
  test(`${path} is accessible and runs within its CSP`, async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));

    await page.goto(path);
    await page.waitForLoadState("networkidle");

    const { violations } = await new AxeBuilder({ page }).withTags(WCAG).analyze();
    expect(violations.map(({ id, help, nodes }) => ({ id, help, nodes: nodes.length }))).toEqual(
      [],
    );
    expect(await page.evaluate(() => window.cspViolations)).toEqual([]);
    expect(errors).toEqual([]);
    await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
    // Compared only in Playwright's Linux image (see playwright.config.ts).
    await expect(page).toHaveScreenshot(`${screenshotName(path)}.png`, { fullPage: true });
  });
}
