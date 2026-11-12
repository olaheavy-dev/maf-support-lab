import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { defineConfig, devices } from "@playwright/test";

// The chat page's flows end to end: the production frontend in front of a real backend, every scenario
// group through the browser. Not the template's page checks (playwright.config.ts): these need a backend.
//
//   BACKEND=dotnet|python  which backend (default dotnet)
//   MODEL=fake|live        fake (default; deterministic, no key, what CI runs) or live Claude Haiku.
//                          Live is opt-in and billed: .NET reads its key from user secrets, Python from
//                          ANTHROPIC_API_KEY. Assertions in live mode check structure, not wording.
//   RECORD=1               keep a video of every test (for the demo recording)
//
// Build the frontend first (`pnpm build`), then `pnpm e2e:flows`.
const CI = Boolean(process.env.CI);
const BACKEND = process.env.BACKEND === "python" ? "python" : "dotnet";
const LIVE = process.env.MODEL === "live";
const PORT = 3200;
const API_PORT = BACKEND === "python" ? 5280 : 5180;
// A fresh store per run, so every scenario starts from the fixtures (a cancelled order stays cancelled).
const store = mkdtempSync(join(tmpdir(), "voltcart-flows-"));

const backend: { command: string; cwd: string; env: Record<string, string> } =
  BACKEND === "python"
    ? {
        command: "uv run --locked python -m support_agent",
        cwd: "../src/python",
        env: {
          APP_ENVIRONMENT: "development",
          IDENTITY__DEVELOPMENT_CUSTOMER_ID: "CUST-1001",
          MODEL_PROVIDER__MODE: LIVE ? "live" : "fake",
          ...(LIVE ? { MODEL_PROVIDER__ANTHROPIC__MODEL: "claude-haiku-5-5" } : {}),
          STORAGE__PATH: join(store, "support.db"),
          PORT: String(API_PORT),
          ...(process.env.ANTHROPIC_API_KEY
            ? { ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY }
            : {}),
        },
      }
    : {
        command: "dotnet run --project ../src/dotnet/Support.Api --launch-profile http",
        cwd: ".",
        env: {
          ModelProvider__Mode: LIVE ? "Live" : "Fake",
          Storage__Path: join(store, "support.db"),
        },
      };

export default defineConfig({
  testDir: "./e2e-flows",
  forbidOnly: CI,
  // Scenarios share one backend and its store; they run one at a time.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: LIVE ? 120_000 : 30_000,
  expect: { timeout: LIVE ? 60_000 : 10_000 },
  reporter: CI ? [["github"], ["list"]] : "list",
  metadata: { backend: BACKEND, model: LIVE ? "live" : "fake" },
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: "retain-on-failure",
    video: process.env.RECORD ? { mode: "on", size: { width: 1440, height: 900 } } : "off",
  },
  // Desktop width, so the circuit rail beside the conversation is visible and can be checked.
  projects: [
    {
      name: `${BACKEND}-${LIVE ? "live" : "fake"}`,
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1440, height: 900 },
        colorScheme: "light",
      },
    },
  ],
  webServer: [
    {
      command: backend.command,
      cwd: backend.cwd,
      env: { ...backend.env },
      url: `http://127.0.0.1:${API_PORT}/health`,
      reuseExistingServer: false,
      timeout: 180_000,
      stdout: "ignore",
      stderr: "pipe",
    },
    {
      command: "cp -R .next/static .next/standalone/.next/ && node .next/standalone/server.js",
      url: `http://127.0.0.1:${PORT}/health/live`,
      env: {
        PORT: String(PORT),
        HOSTNAME: "127.0.0.1",
        NEXT_TELEMETRY_DISABLED: "1",
        API_BASE_URL: `http://127.0.0.1:${API_PORT}/api`,
      },
      reuseExistingServer: false,
      timeout: 30_000,
    },
  ],
});
