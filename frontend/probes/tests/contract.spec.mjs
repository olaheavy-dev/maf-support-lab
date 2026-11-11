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

    test("a reload while a write turn is still running shows its approval once the turn is recovered", async ({ page }) => {
      await open(page, alias);
      // A ticket: always eligible, whatever earlier tests changed on this shared backend.
      await say(page, "Please open a support ticket: my printer shows error P-12.");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(1);
      // Reproduce the race (Codex review on PR #97): the first read after the reload sees the conversation as it was
      // while the turn ran, with that turn active and its approval not yet listed. Later reads are the real ones.
      let first = true;
      await page.route("**/v1/conversations/*", async (route) => {
        const response = await route.fetch();
        const body = await response.json();
        if (first && route.request().method() === "GET") {
          first = false;
          const { lastTurnId } = await page.evaluate(() => window.__probe.state ?? JSON.parse(sessionStorage.getItem("support-probe")));
          body.pending_approvals = [];
          body.active_turn_id = lastTurnId;
        }
        await route.fulfill({ response, json: body });
      });
      await page.reload();
      await expect(page.locator('[data-recovered="true"]')).toHaveAttribute("data-outcome", "awaiting_approval");
      const pending = page.getByTestId("pending").locator("li");
      await expect(pending).toHaveCount(1);
      await pending.getByTestId("approve").click();
      await expect(assistant(page).last()).toHaveAttribute("data-kind", "approval_result");
    });

    test("a reload before turn.started arrived recovers the conversation's active turn", async ({ page }) => {
      await open(page, alias);
      await say(page, "Please open a support ticket: my scanner shows error S-3.");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(1);
      // As if the page reloaded before it read turn.started: it has no id for that turn (Codex review on PR #97).
      const turnId = await page.evaluate(() => {
        const state = JSON.parse(sessionStorage.getItem("support-probe"));
        const id = state.lastTurnId;
        delete state.lastTurnId;
        sessionStorage.setItem("support-probe", JSON.stringify(state));
        return id;
      });
      let first = true;
      await page.route("**/v1/conversations/*", async (route) => {
        const response = await route.fetch();
        const body = await response.json();
        if (first && route.request().method() === "GET") {
          first = false; // the mid-turn view: the turn is active and its approval not yet listed
          body.pending_approvals = [];
          body.active_turn_id = turnId;
        }
        await route.fulfill({ response, json: body });
      });
      await page.reload();
      await expect(page.locator('[data-recovered="true"]')).toHaveAttribute("data-turn-id", turnId);
      await expect(page.locator('[data-recovered="true"]')).toHaveAttribute("data-outcome", "awaiting_approval");
      await expect(page.getByTestId("pending").locator("li")).toHaveCount(1);
    });

    test("recovery that outlasts its window says so and can check again", async ({ page }) => {
      await open(page, alias);
      await say(page, "Where is my order ORD-1002?");
      await expect(assistant(page).last()).toHaveAttribute("data-outcome", "completed");
      // The turn reads as still running until the test lets it finish (Codex review on PR #97).
      let running = true;
      await page.route("**/v1/conversations/*/turns/*", async (route) => {
        const response = await route.fetch();
        const body = await response.json();
        if (running) {
          for (const key of ["outcome", "finished_at", "reply", "route", "error", "operations", "pending_approval"]) delete body[key];
          body.state = "running";
        }
        await route.fulfill({ response, json: body });
      });
      await page.goto("/?recoveryMs=800");
      await expect(page.getByTestId("retry")).toBeVisible();
      await expect(page.getByTestId("status")).toContainText("longer than usual");
      running = false;
      await page.getByTestId("retry").click();
      await expect(page.locator('[data-recovered="true"]')).toHaveAttribute("data-outcome", "completed");
      await expect(page.getByTestId("retry")).toHaveCount(0);
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
          // Inherited object properties are not aliases (Codex review on PR #97: these used to crash the proxy).
          constructor: await get(`/api/constructor/v1/conversations/${conversationId}`),
          proto: await get(`/api/__proto__/v1/conversations/${conversationId}`),
          toString: await get(`/api/tostring/v1/conversations/${conversationId}`),
          stillUp: await get("/api/backends"),
        };
      }, { other });
      expect(statuses).toEqual({
        own: 200,
        otherBackend: 404,
        unknownAlias: 404,
        encodedUpstream: 404,
        traversal: 404,
        constructor: 404,
        proto: 404,
        toString: 404,
        stillUp: 200,
      });
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
  const asHrefs = (found) => Object.fromEntries(Object.entries(found).map(([alias, url]) => [alias, url.href]));
  // Aspire's shell-safe names first (a POSIX shell drops names with "-"), then the services__… form; http before https.
  expect(
    asHrefs(
      upstreams({
        SUPPORT_API_DOTNET_HTTP: "http://localhost:5180",
        "services__support-api-dotnet__http__0": "http://localhost:1111",
        SUPPORT_API_PYTHON_HTTPS: "https://localhost:5443",
        "services__some-other-service__http__0": "http://localhost:9999",
      }),
    ),
  ).toEqual({ dotnet: "http://localhost:5180/", python: "https://localhost:5443/" });
  expect(
    asHrefs(
      upstreams({
        "services__support-api-dotnet__https__0": "https://localhost:7180",
        "services__support-api-python__http__0": "http://localhost:5280",
      }),
    ),
  ).toEqual({ dotnet: "https://localhost:7180/", python: "http://localhost:5280/" });
  const none = upstreams({});
  expect(Object.keys(none)).toEqual([]);
  expect("constructor" in none).toBe(false); // no prototype, so no inherited "aliases"
});

test("a backend that drops the connection mid-stream ends the browser's response too", async () => {
  // Codex review on PR #97: the proxy used to forward the first chunk and then hold the browser's response open.
  const http = await import("node:http");
  const { createProxy } = await import("../proxy/server.mjs");
  const backend = http.createServer((request, response) => {
    response.writeHead(200, { "content-type": "text/event-stream" });
    response.write('event: turn.started\ndata: {"type":"turn.started"}\n\n');
    setTimeout(() => response.destroy(), 50); // gone before the stream is complete
  });
  await new Promise((resolve) => backend.listen(0, "127.0.0.1", resolve));
  const proxy = createProxy({ dotnet: new URL(`http://127.0.0.1:${backend.address().port}`) });
  await new Promise((resolve) => proxy.listen(0, "127.0.0.1", resolve));
  try {
    const response = await fetch(`http://127.0.0.1:${proxy.address().port}/api/dotnet/v1/conversations/x/turns`, {
      method: "POST",
      headers: { "content-type": "application/json", accept: "text/event-stream" },
      body: "{}",
    });
    const reader = response.body.getReader();
    const first = await reader.read();
    expect(new TextDecoder().decode(first.value)).toContain("turn.started");
    // The rest must end (an error or done) promptly, not hang.
    const outcome = await Promise.race([
      reader.read().then(() => "ended", () => "ended"),
      new Promise((resolve) => setTimeout(() => resolve("hung"), 3000)),
    ]);
    expect(outcome).toBe("ended");
  } finally {
    proxy.close();
    backend.close();
    proxy.closeAllConnections();
    backend.closeAllConnections();
  }
});
