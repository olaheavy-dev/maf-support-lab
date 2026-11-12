import createClient from "openapi-fetch";

import type { paths } from "./schema";

/**
 * The typed API client for Client Components. It calls the frontend's own `/api` proxy, which
 * forwards it to the backend; the backends own identity, so nothing about the caller is added:
 *
 *     const { data, error } = await api.POST("/v1/conversations", { body: {} });
 */
export const api = createClient<paths>({ baseUrl: "/api" });
