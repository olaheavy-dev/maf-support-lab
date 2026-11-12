// @vitest-environment node
import { THEME_KEY, THEME_SCRIPT } from "./theme";

describe("THEME_SCRIPT", () => {
  it("reads the same storage key the theme switch writes", () => {
    expect(THEME_SCRIPT).toContain(`localStorage.getItem(${JSON.stringify(THEME_KEY)})`);
  });

  it("only ever applies light or dark", () => {
    expect(THEME_SCRIPT).toContain('if(t==="light"||t==="dark")');
  });
});
