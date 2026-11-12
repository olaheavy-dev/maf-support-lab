"use client"; // Error boundaries are Client Components.

import "./globals.css";

import { ErrorMessage } from "./error-message";

// Replaces the root layout when the layout itself throws, so it brings its own document.
export default function GlobalError({ error, retry }: { error: unknown; retry: () => void }) {
  return (
    <html lang="en">
      <body className="bg-bg-primary text-text-primary antialiased">
        <title>Something went wrong</title>
        <ErrorMessage error={error} retry={retry} />
      </body>
    </html>
  );
}
