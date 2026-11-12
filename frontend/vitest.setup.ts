import "@testing-library/jest-dom/vitest";

// `server-only` throws when imported outside a React Server environment; the tests import server
// modules directly.
vi.mock("server-only", () => ({}));
