"use client";

import { useSyncExternalStore } from "react";

import { applyTheme, readTheme, subscribeTheme, type Theme } from "@/lib/theme";

import { MoonIcon, SunIcon, SystemIcon } from "./icons";

const OPTIONS: { value: Theme; label: string; Icon: typeof SunIcon }[] = [
  { value: "system", label: "Match the system", Icon: SystemIcon },
  { value: "light", label: "Light", Icon: SunIcon },
  { value: "dark", label: "Dark", Icon: MoonIcon },
];

/** Three-way theme choice. It reads <html data-theme> (the one source of truth) rather than copying it. */
export function ThemeSwitch() {
  const theme = useSyncExternalStore(subscribeTheme, readTheme, () => "system" as Theme);

  function move(event: React.KeyboardEvent, index: number) {
    const step =
      event.key === "ArrowRight" || event.key === "ArrowDown"
        ? 1
        : event.key === "ArrowLeft" || event.key === "ArrowUp"
          ? -1
          : 0;
    if (!step) return;
    event.preventDefault();
    const next = OPTIONS[(index + step + OPTIONS.length) % OPTIONS.length]!;
    applyTheme(next.value);
    document.getElementById(`theme-${next.value}`)?.focus();
  }

  return (
    <div
      role="radiogroup"
      aria-label="Colour theme"
      className="border-border-primary flex rounded-md border p-0.5"
    >
      {OPTIONS.map(({ value, label, Icon }, index) => {
        const checked = theme === value;
        return (
          <button
            key={value}
            id={`theme-${value}`}
            type="button"
            role="radio"
            aria-checked={checked}
            aria-label={label}
            title={label}
            tabIndex={checked ? 0 : -1}
            onClick={() => applyTheme(value)}
            onKeyDown={(event) => move(event, index)}
            className="text-text-tertiary hover:text-text-primary aria-checked:bg-bg-tertiary aria-checked:text-text-primary grid size-8 place-items-center rounded-sm transition-colors duration-150"
          >
            <Icon />
          </button>
        );
      })}
    </div>
  );
}
