// The colour theme: "system" (follow the operating system) or a chosen "light" or "dark". A choice is
// stored in localStorage and stamped on <html data-theme>; "system" stamps nothing, so the CSS media
// query decides (src/styles/tokens.css). The attribute is the one source of truth components read.

export type Theme = "system" | "light" | "dark";

export const THEME_KEY = "voltcart-theme";

/**
 * Runs inline in <head>, before the first paint. Storage can be unavailable (private windows): ignore it.
 * A fixed string, with nothing interpolated into the script; src/lib/theme.test.ts checks it uses THEME_KEY.
 */
export const THEME_SCRIPT =
  '(function(){try{var t=localStorage.getItem("voltcart-theme");if(t==="light"||t==="dark")document.documentElement.dataset.theme=t;}catch(e){}})();';

export function readTheme(): Theme {
  const value = document.documentElement.dataset.theme;
  return value === "light" || value === "dark" ? value : "system";
}

export function applyTheme(theme: Theme): void {
  const root = document.documentElement;
  if (theme === "system") delete root.dataset.theme;
  else root.dataset.theme = theme;
  try {
    if (theme === "system") localStorage.removeItem(THEME_KEY);
    else localStorage.setItem(THEME_KEY, theme);
  } catch {
    // Storage blocked: the choice still applies to this page.
  }
}

/** Calls back whenever data-theme changes, for useSyncExternalStore. */
export function subscribeTheme(callback: () => void): () => void {
  const observer = new MutationObserver(callback);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  return () => observer.disconnect();
}
