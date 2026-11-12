import "@testing-library/jest-dom/vitest";

// `server-only` throws when imported outside a React Server environment; the tests import server
// modules directly.
vi.mock("server-only", () => ({}));

// jsdom doesn't lay pages out, so it has no scrollIntoView; the page calls it to follow the conversation.
if (typeof Element !== "undefined") Element.prototype.scrollIntoView = vi.fn();
