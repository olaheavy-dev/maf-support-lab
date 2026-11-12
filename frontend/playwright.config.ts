import { defineConfig, devices } from "@playwright/test";

const CI = Boolean(process.env.CI);
const PORT = 3100;
// Set by scripts/e2e-docker.sh (CI, and `pnpm e2e:docker --update-snapshots=changed`): Chromium in Playwright's Linux
// image, the one place screenshots are compared, because fonts and rendering differ by machine.
const BROWSER_SERVER = process.env.PLAYWRIGHT_BROWSER_SERVER;

// End-to-end tests against the production build, served as it would be deployed: the
// standalone server. Build first (`pnpm build`), then `pnpm e2e`, or `pnpm e2e:docker` to compare
// the screenshots too, as CI does.
export default defineConfig({
  testDir: "./e2e",
  forbidOnly: CI,
  retries: CI ? 1 : 0,
  reporter: CI ? [["github"], ["list"]] : "list",
  ignoreSnapshots: !BROWSER_SERVER,
  // CI never writes a screenshot: a page without one fails until it is made and committed.
  updateSnapshots: CI ? "none" : "missing",
  snapshotPathTemplate: "{testDir}/screenshots/{testFilePath}/{arg}-{projectName}{ext}",
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: "retain-on-failure",
    // The browser in the container reaches this machine's 127.0.0.1 (the server) through the connection.
    connectOptions: BROWSER_SERVER
      ? { wsEndpoint: BROWSER_SERVER, exposeNetwork: "<loopback>" }
      : undefined,
  },
  // Each page is checked in light and dark, and at a phone's width.
  projects: [
    { name: "light", use: { ...devices["Desktop Chrome"], colorScheme: "light" } },
    { name: "dark", use: { ...devices["Desktop Chrome"], colorScheme: "dark" } },
    { name: "mobile", use: { ...devices["Pixel 7"] } },
  ],
  webServer: {
    // The standalone server serves .next/static only once it is copied in (as a deployment would).
    command: "cp -R .next/static .next/standalone/.next/ && node .next/standalone/server.js",
    url: `http://127.0.0.1:${PORT}`,
    env: { PORT: String(PORT), HOSTNAME: "127.0.0.1", NEXT_TELEMETRY_DISABLED: "1" },
    reuseExistingServer: !CI,
    timeout: 30_000,
  },
});
