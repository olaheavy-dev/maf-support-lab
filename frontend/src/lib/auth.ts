import "server-only";

/**
 * Who is calling, as the backend should hear it: the headers that prove the user to the backend.
 *
 * In this repository the backends own identity: each serves one configured development customer and
 * never reads identity from a request (docs/threat-model.md). So the frontend sends nothing, in every
 * environment, and the request it proxies can't claim to be anyone. A real deployment would replace
 * this with its sign-in provider and the backends' authentication together (the threat model's
 * production requirements).
 */
export async function backendCredentials(incoming: Headers): Promise<Record<string, string>> {
  void incoming; // what a real implementation would read its session from
  return {};
}
