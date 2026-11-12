import type { Metadata, Viewport } from "next";
import { IBM_Plex_Mono, IBM_Plex_Sans, IBM_Plex_Sans_Condensed } from "next/font/google";

import { THEME_SCRIPT } from "@/lib/theme";

import "./globals.css";

// IBM Plex: a datasheet's type, for an electronics retailer's support desk. Downloaded at build time and
// served from this origin (the CSP allows fonts from 'self' only).
const sans = IBM_Plex_Sans({
  subsets: ["latin"],
  weight: ["400", "500", "600"],
  variable: "--font-plex-sans",
});
const condensed = IBM_Plex_Sans_Condensed({
  subsets: ["latin"],
  weight: ["600"],
  variable: "--font-plex-condensed",
});
const mono = IBM_Plex_Mono({
  subsets: ["latin"],
  weight: ["400", "500", "600"], // 600 for record ids inside bold reply text
  variable: "--font-plex-mono",
});

export const metadata: Metadata = {
  title: "VoltCart Support",
  description:
    "A support conversation with VoltCart's agents: triage routes each message in code, specialists answer from scoped tools, and every change waits for your approval.",
};

export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#f3f5f1" },
    { media: "(prefers-color-scheme: dark)", color: "#0c1510" },
  ],
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // The theme script sets data-theme before React hydrates, so the attribute may differ.
    <html
      lang="en"
      className={`${sans.variable} ${condensed.variable} ${mono.variable}`}
      suppressHydrationWarning
    >
      <head>
        {/* Applies a chosen theme before the first paint, so the page never flashes the other one. It runs
            because "/" is prerendered, whose CSP allows inline scripts (src/lib/csp.ts); a page added to
            NONCE_PATHS would need this script to carry the nonce. */}
        <script dangerouslySetInnerHTML={{ __html: THEME_SCRIPT }} />
      </head>
      <body className="bg-bg-primary text-text-primary antialiased">{children}</body>
    </html>
  );
}
