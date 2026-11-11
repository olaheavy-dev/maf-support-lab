// L22 browser probes: the same checks against each backend, through the relative-URL proxy, in real Chromium.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "@playwright/test";

const BACKENDS = ["dotnet", "python"];

async function open(page, alias) {
  await page.goto("/");
  await expect(page.locator("body[data-ready=true]")).toBeAttached();
  await page.getByTestId("backend").selectOption(alias);
}

async function say(page, message) {
  await page.getByTestId("message").fill(message);
  await page.getByTestId("send").click();
}

const assistant = (page) => page.locator('[data-testid=transcript] .entry[data-role="assistant"]');

for (const alias of BACKENDS) {
  test.describe(`${alias} backend`, () => {
    test("a chat turn streams its reply and stores the same text", async ({ page }) => {
      await open(page, alias);
      await say(page, "Where is my order ORD-1002?");
      const reply = assistant(page).last();
      await expect(reply).toHaveAttribute("data-outcome", "completed");
      await expect(reply).toContainText("ORD-1002");
      const events = await page.evaluate(() => window.__probe.events.map((e) => e.type));
      expect(events[0]).toBe("turn.started");
      expect(events.filter((type) => type === "reply.delta").length).toBeGreaterThan(0);
      expect(events.at(-1)).toBe("turn.final");
      // The streamed text is what the server stored for the turn.
      const stored = await page.evaluate(async () => {
        const { alias, conversationId, lastTurnId } = window.__probe.state;
        const response = await fetch(`/api/${alias}/v1/conversations/${conversationId}/turns/${lastTurnId}`);
        return (await response.json()).reply.text;
      });
      await expect(reply).toHaveText(stored);
      await expect(page.getByTestId("conversation")).toHaveAttribute("data-alias", alias);
    });

    test("a write pauses for approval and completes once approved", async ({ page }) => {
      await open(page, alias);
      await say(page, "Please cancel order ORD-1003.");
      await expect(assistant(page).last()).toHaveAttribute("data-outcome", "awaiting_approval");
      const pending = page.getByTestId("pending").locator("li");
      await expect(pending).toHaveCount(1);
      await expect(pending).toContainText("ORD-1003");
      await pending.getByTestId("approve").click();
      const result = assistant(page).last();
      await expect(result).toHaveAttribute("data-kind", "approval_result");
      await expect(result).toContainText("ORD-1003");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(0);
      const operation = await page.evaluate(async () => {
        const { alias, conversationId } = window.__probe.state;
        const turns = window.__probe.events.filter((e) => e.type === "turn.final").map((e) => e.data.result);
        const id = turns.at(-1).operations[0].operation_id;
        return (await (await fetch(`/api/${alias}/v1/operations/${id}`)).json()).status;
      });
      expect(operation).toBe("committed");
    });

    test("a rejected write changes nothing", async ({ page }) => {
      await open(page, alias);
      await say(page, "I want a refund for ORD-1001, the headphones crackle.");
      const pending = page.getByTestId("pending").locator("li");
      await expect(pending).toHaveCount(1);
      await pending.getByTestId("reject").click();
      const result = assistant(page).last();
      await expect(result).toHaveAttribute("data-kind", "approval_result");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(0);
      const status = await page.evaluate(async () => {
        const { alias } = window.__probe.state;
        const final = window.__probe.events.filter((e) => e.type === "turn.final").at(-1).data.result;
        return (await (await fetch(`/api/${alias}/v1/operations/${final.operations[0].operation_id}`)).json()).status;
      });
      expect(status).toBe("rejected");
    });

    test("after a refresh the browser rediscovers the pending approval and can decide it", async ({ page }) => {
      await open(page, alias);
      await say(page, "Please open a support ticket: my fridge shows error Z-77.");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(1);
      await page.reload();
      await expect(page.locator("body[data-ready=true]")).toBeAttached();
      // Same backend and conversation after the refresh, found from the browser's own session storage.
      await expect(page.getByTestId("conversation")).toHaveAttribute("data-alias", alias);
      await expect(page.getByTestId("backend")).toBeDisabled();
      const pending = page.getByTestId("pending").locator("li");
      await expect(pending).toHaveCount(1);
      await pending.getByTestId("approve").click();
      await expect(assistant(page).last()).toHaveAttribute("data-kind", "approval_result");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(0);
    });

    test("a dropped stream is not a cancellation: the turn is recovered by its id", async ({ page }) => {
      await open(page, alias);
      const result = await page.evaluate(() => window.__probeApi.send("Where is my order ORD-1002?", { abortAfterStart: true }));
      expect(result).toBeNull(); // the browser stopped reading right after turn.started
      await expect(page.getByTestId("status")).toHaveText("Disconnected.");
      await page.reload();
      const recovered = page.locator('[data-recovered="true"]');
      await expect(recovered).toHaveAttribute("data-outcome", "completed");
      await expect(recovered).toContainText("ORD-1002");
    });

    test("a failed turn and a problem response render as safe text", async ({ page }) => {
      await open(page, alias);
      await say(page, "Where is ORD-1002? [fake:model-error]");
      await expect(page.getByTestId("error")).not.toBeEmpty();
      const shown = await page.getByTestId("error").textContent();
      expect(shown).not.toMatch(/exception|traceback|stack|fake model failed|\bat [A-Z]/i);
      const final = await page.evaluate(() => window.__probe.events.filter((e) => e.type === "turn.final").at(-1).data.result);
      expect(final.outcome).toBe("failed");
      expect(shown).toBe(final.error.detail);
      // Markup typed by the customer is shown as text and never becomes an element.
      await say(page, '<img src=x onerror="window.__pwned = true"> Where is ORD-1002?');
      await expect(assistant(page).last()).toHaveAttribute("data-outcome", "completed");
      await expect(page.locator("[data-testid=transcript] img")).toHaveCount(0);
      expect(await page.evaluate(() => window.__pwned)).toBeUndefined();
      await expect(page.locator('.entry[data-role="user"]').last()).toContainText("<img src=x");
    });

    test("a conversation stays on its backend: other aliases and unknown upstreams get 404", async ({ page }) => {
      await open(page, alias);
      await say(page, "Where is my order ORD-1002?");
      await expect(assistant(page).last()).toHaveAttribute("data-outcome", "completed");
      const other = BACKENDS.find((name) => name !== alias);
      const statuses = await page.evaluate(async ({ other }) => {
        const { conversationId } = window.__probe.state;
        const get = async (path) => (await fetch(path)).status;
        return {
          own: await get(`/api/${window.__probe.state.alias}/v1/conversations/${conversationId}`),
          otherBackend: await get(`/api/${other}/v1/conversations/${conversationId}`),
          unknownAlias: await get(`/api/evil/v1/conversations/${conversationId}`),
          encodedUpstream: await get(`/api/http%3A%2F%2Fexample.com/v1/conversations`),
          traversal: await get(`/api/${other}/v1/../../health`),
        };
      }, { other });
      expect(statuses).toEqual({ own: 200, otherBackend: 404, unknownAlias: 404, encodedUpstream: 404, traversal: 404 });
    });
  });
}

test("browser code holds no backend address, Aspire service name, or provider key", async () => {
  const files = ["index.html", "probe.js"].map((name) => readFileSync(join(import.meta.dirname, "..", "proxy", "public", name), "utf8"));
  for (const text of files) {
    expect(text).not.toMatch(/https?:\/\/(?!contracts\.maf-support-lab\.example)/);
    expect(text).not.toMatch(/localhost|127\.0\.0\.1|services__|support-api-|\.internal\b|ANTHROPIC|sk-ant/i);
  }
});

test("the proxy only tells the browser which aliases exist, never where they are", async ({ request }) => {
  const body = await (await request.get("/api/backends")).json();
  expect(body).toEqual({ backends: ["dotnet", "python"] });
});

test("backend addresses come only from Aspire-style service variables, http first, https when that's all there is", async () => {
  const { upstreams } = await import("../proxy/server.mjs");
  const found = upstreams({
    "services__support-api-dotnet__http__0": "http://localhost:5180",
    "services__support-api-dotnet__https__0": "https://localhost:7180",
    "services__support-api-python__https__0": "https://localhost:5443",
    "services__some-other-service__http__0": "http://localhost:9999",
  });
  expect(Object.fromEntries(Object.entries(found).map(([alias, url]) => [alias, url.href]))).toEqual({
    dotnet: "http://localhost:5180/",
    python: "https://localhost:5443/",
  });
  expect(upstreams({})).toEqual({});
});
