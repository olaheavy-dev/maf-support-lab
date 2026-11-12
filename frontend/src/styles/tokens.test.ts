// @vitest-environment node
// Measures the tokens' contrast (WCAG 2.2 relative luminance) in both themes, so a re-skin can't ship
// unreadable text: body text 4.5:1 on every background, interactive edges and focus 3:1.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const css = readFileSync(fileURLToPath(new URL("./tokens.css", import.meta.url)), "utf8");

function block(start: string): Record<string, string> {
  const from = css.indexOf(start);
  const body = css.slice(css.indexOf("{", from) + 1, css.indexOf("}", from));
  return Object.fromEntries(
    [...body.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6})/gi)].map(([, name, value]) => [
      name!,
      value!,
    ]),
  );
}

const light = block("@theme");
const dark = { ...light, ...block(':root[data-theme="dark"]') };
const systemDark = { ...light, ...block(':root:not([data-theme="light"])') };

function luminance(hex: string): number {
  const channel = (offset: number) => {
    const value = parseInt(hex.slice(offset, offset + 2), 16) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

function contrast(a: string, b: string): number {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high! + 0.05) / (low! + 0.05);
}

const BACKGROUNDS = [
  "color-bg-primary",
  "color-bg-secondary",
  "color-bg-tertiary",
  "color-accent-tint",
];
const TEXT = [
  "color-text-primary",
  "color-text-secondary",
  "color-text-tertiary",
  "color-text-link",
  "color-accent-primary",
  "color-status-success",
  "color-status-warning",
  "color-status-danger",
];
const EDGES = ["color-border-secondary", "color-focus-ring", "color-accent-primary"];

describe.each([
  ["light", light],
  ["dark", dark],
])("%s tokens", (_, tokens) => {
  it.each(TEXT.flatMap((text) => BACKGROUNDS.map((background) => [text, background])))(
    "%s on %s is at least 4.5:1",
    (text, background) => {
      expect(contrast(tokens[text!]!, tokens[background!]!)).toBeGreaterThanOrEqual(4.5);
    },
  );

  it.each(EDGES.flatMap((edge) => BACKGROUNDS.slice(0, 3).map((background) => [edge, background])))(
    "%s on %s is at least 3:1",
    (edge, background) => {
      expect(contrast(tokens[edge!]!, tokens[background!]!)).toBeGreaterThanOrEqual(3);
    },
  );

  it("text on the accent is at least 4.5:1", () => {
    expect(
      contrast(tokens["color-accent-contrast"]!, tokens["color-accent-primary"]!),
    ).toBeGreaterThanOrEqual(4.5);
  });
});

it("the system's dark mode and the chosen dark theme are the same palette", () => {
  expect(systemDark).toEqual(dark);
});
