import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = { title: "Page not found" };

export default function NotFound() {
  return (
    <main className="mx-auto flex min-h-dvh max-w-2xl flex-col justify-center gap-4 px-6">
      <h1 className="text-3xl font-semibold tracking-tight">Page not found</h1>
      <p className="text-text-secondary">
        There is nothing at this address. It may have moved, or the link may be wrong.
      </p>
      <p>
        <Link href="/" className="text-text-link underline underline-offset-4">
          Go to the home page
        </Link>
      </p>
    </main>
  );
}
