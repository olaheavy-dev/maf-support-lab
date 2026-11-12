// The frontend's liveness check (the AppHost watches it): answers while the server runs, without
// calling the backend, and names the commit it was built from when GIT_COMMIT is set at build time.
// The backend's own check is at /api/health. The response is prerendered, so GIT_COMMIT is read by
// `next build`, not by the running server.
export function GET() {
  return Response.json({ status: "ok", commit: process.env.GIT_COMMIT || null });
}
