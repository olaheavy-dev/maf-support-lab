# Browser probes (L22)

A disposable developer probe, **not** the product UI (that's L23): a minimal page plus a zero-dependency Node proxy that proves the version-1 contract works from a real browser against either backend. The decisions are in [0006-browser-integration](../../docs/decisions/0006-browser-integration.md).

| Path | What it is |
| --- | --- |
| `proxy/server.mjs` | Serves the page and forwards `/api/{alias}/v1/...` to the backend for that alias (`dotnet` or `python`). Addresses come only from `services__<resource>__http__0` / `__https__0` (Aspire's references). Streams pass through unbuffered. |
| `proxy/public/` | The page: relative URLs, `fetch` streaming, approve/reject, refresh discovery, disconnect recovery, `textContent` everywhere. |
| `tests/contract.spec.mjs` | Playwright (Chromium): the same probes against each backend. |
| `playwright.config.mjs` | Starts both backends (fake mode, throwaway databases) and the proxy. |

```bash
# Through Aspire: the "browser-probe" resource (open its URL in the dashboard)
aspire start --apphost apphost/Support.AppHost.csproj

# Tests (needs the .NET SDK, uv and Node 26)
dotnet build src/dotnet/Support.Api -c Release
cd frontend/probes && npm ci && npx playwright install chromium && npx playwright test
```

The `browser` CI job runs the same tests on every pull request.
