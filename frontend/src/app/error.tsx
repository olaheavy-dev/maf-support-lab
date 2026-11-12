"use client"; // Error boundaries are Client Components.

import { ErrorMessage } from "./error-message";

// Shown in place of a page that threw while rendering, inside the root layout.
export default function ErrorPage({ error, retry }: { error: unknown; retry: () => void }) {
  return <ErrorMessage error={error} retry={retry} />;
}
