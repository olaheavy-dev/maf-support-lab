# 0007 - A demo frontend from the house Next.js template

- Status: Accepted (2026-11-12). Reverses the 2026-11-11 retirement of the frontend ([0006](0006-browser-integration.md)) for a demo chat page.
- Versions: Next.js 16.4.3, React 19.3.1, Tailwind CSS 4.3.3, TypeScript 5.9.3, Playwright 1.64.1, on Node 26 with pnpm 12, as rendered from `house-template-nextjs` v0.3.0. `Aspire.Hosting.JavaScript` 13.6.5 starts it.

## Context

The demos so far are a command-line run of `scripts/demo.sh` and a DevUI recording.
- **DevUI doesn't show the conversation.** Its workflow view lights up the route on a graph, but the reply sits in a narrow event panel. In the owner's words: "the response from the assistant is not showing in the recording".
- **DevUI can't show writes.** It has no support conversation, so approvals and the approval gate's checks can't be demonstrated there.

The owner asked "how complicated is a frontend for this?" and then "see if next js template repo in my org can help". Of the three options offered, they chose to copy the template's frontend. Their standard for it: "the chat page needs to be beautiful and premium, excellence and professionalism is non negotiable".

## Decision

1. **Copy, don't layer.** `house-template-nextjs` is a Copier layer on `house-template-base` and refuses to apply to a project not generated from base. Adopting base would rework this repository's CI and governance. So both templates were rendered in a scratch folder (base with the `nextjs` stack; the layer with backend `openapi`), and `frontend/` and `.node-version` were copied in. The trade-off: `copier update` doesn't reach it, so template fixes are applied by hand.
2. **Changes made to the copy:**
   - `backendCredentials` sends no identity in any environment, because the backends own identity and never read it from a request ([threat model](../threat-model.md)).
   - The Dockerfile and Compose file are left out: there is no container deployment.
   - `make` references become the `pnpm` commands this repository runs.
3. **Same origin.** The browser only calls the frontend's `/api/*` proxy, which forwards to `API_BASE_URL`, so no backend address reaches the browser and there is no CORS. The AppHost points it at the .NET backend; run on its own, it can point at either backend.
4. **The template's checks are CI's checks.** A required `frontend` job runs:
   - peer dependencies, lint, format, type check and unit tests;
   - a production build;
   - the end-to-end tests: axe (WCAG 2.2 AA), CSP and security headers, and screenshots in light, dark and mobile, in Playwright's Linux image.

   CodeQL adds JavaScript/TypeScript, and Dependabot adds npm.
5. **The AppHost uses `AddJavaScriptApp`**, which runs the `dev` script. `AddNextJsApp` is marked experimental (ASPIREJAVASCRIPT001) in Aspire 13.6.5.

## Consequences

- **More to maintain:** Node 26, pnpm 12 and a Next.js stack join CI, Dependabot and CodeQL.
- **Contract types:** the chat page needs TypeScript types for the contract. They will be generated from the existing JSON Schemas, through an OpenAPI description that references them, rather than by adding OpenAPI to both backends.
- **A second identity boundary:** the frontend never sends identity. A real deployment would need sign-in in the frontend and authentication in the backends together.
