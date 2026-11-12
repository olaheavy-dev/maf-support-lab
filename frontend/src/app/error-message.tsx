"use client";

/**
 * What error.tsx and global-error.tsx show. Never the error's message: from a Server Component it
 * is a generic one anyway, and from a Client Component it may hold anything. The digest is the id
 * the server logged the real error under, so a user can quote it.
 */
export function ErrorMessage({ error, retry }: { error: unknown; retry: () => void }) {
  const digest =
    error instanceof Error && "digest" in error && typeof error.digest === "string"
      ? error.digest
      : undefined;
  return (
    <main className="mx-auto flex min-h-dvh max-w-2xl flex-col justify-center gap-4 px-6">
      <h1 className="text-3xl font-semibold tracking-tight">Something went wrong</h1>
      <p className="text-text-secondary">
        This page couldn&apos;t be shown. Try again; if it keeps happening, tell us what you were
        doing{digest ? " and quote the reference below" : ""}.
      </p>
      {digest ? (
        <p className="text-text-secondary">
          Reference: <code className="font-mono">{digest}</code>
        </p>
      ) : null}
      <p>
        <button
          type="button"
          onClick={() => retry()}
          className="bg-accent-primary text-text-inverse hover:bg-accent-primary-hover rounded-md px-4 py-2 font-medium"
        >
          Try again
        </button>
      </p>
    </main>
  );
}
