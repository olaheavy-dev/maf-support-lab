// Browser probes (L22): Chromium against the probe page, through the proxy, with both real backends in fake mode
// (no key, no network). Each backend gets its own throwaway database. The proxy receives the backends' URLs the way
// Aspire passes them (SUPPORT_API_<NAME>_HTTP), so the browser never sees them.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { defineConfig, devices } from "@playwright/test";

const root = resolve(import.meta.dirname, "../..");
const ports = {
  proxy: Number(process.env.PROBE_PORT ?? 5380),
  dotnet: Number(process.env.PROBE_DOTNET_PORT ?? 5381),
  python: Number(process.env.PROBE_PYTHON_PORT ?? 5382),
};
const data = mkdtempSync(join(tmpdir(), "browser-probes-"));
const hermetic = Object.fromEntries(
  Object.entries(process.env).filter(([name]) => !/^(OTEL_|ANTHROPIC_|MODELPROVIDER__|MODEL_PROVIDER__|ASPNETCORE_)/i.test(name)),
);

export default defineConfig({
  testDir: "tests",
  timeout: 60_000,
  fullyParallel: false, // the backends are shared; each test makes its own conversation
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: { baseURL: `http://127.0.0.1:${ports.proxy}`, trace: "retain-on-failure", ...devices["Desktop Chrome"] },
  projects: [{ name: "chromium", use: { browserName: "chromium" } }],
  webServer: [
    {
      name: "dotnet",
      command: "dotnet run --project src/dotnet/Support.Api -c Release --no-build --no-launch-profile",
      cwd: root,
      url: `http://127.0.0.1:${ports.dotnet}/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: {
        ...hermetic,
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: `http://localhost:${ports.dotnet}`,
        Identity__DevelopmentCustomerId: "CUST-1001",
        Storage__Path: join(data, "dotnet.db"),
        ModelProvider__Mode: "Fake",
      },
    },
    {
      name: "python",
      command: `uv run --locked uvicorn support_agent.app:app --host 127.0.0.1 --port ${ports.python}`,
      cwd: join(root, "src", "python"),
      url: `http://127.0.0.1:${ports.python}/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: {
        ...hermetic,
        APP_ENVIRONMENT: "development",
        IDENTITY__DEVELOPMENT_CUSTOMER_ID: "CUST-1001",
        STORAGE__PATH: join(data, "python.db"),
        MODEL_PROVIDER__MODE: "fake",
      },
    },
    {
      name: "proxy",
      command: "node proxy/server.mjs",
      cwd: import.meta.dirname,
      url: `http://127.0.0.1:${ports.proxy}/health`,
      timeout: 30_000,
      reuseExistingServer: false,
      env: {
        ...hermetic,
        PORT: String(ports.proxy),
        // Aspire's shell-safe names (a POSIX shell drops the services__support-api-… form; see server.mjs).
        SUPPORT_API_DOTNET_HTTP: `http://localhost:${ports.dotnet}`,
        SUPPORT_API_PYTHON_HTTP: `http://localhost:${ports.python}`,
      },
    },
  ],
});
