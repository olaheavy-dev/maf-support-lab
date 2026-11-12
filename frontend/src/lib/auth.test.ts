// @vitest-environment node
import { backendCredentials } from "./auth";

describe("backendCredentials", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("sends no identity: the backends own it", async () => {
    vi.stubEnv("NODE_ENV", "development");
    expect(await backendCredentials(new Headers())).toEqual({});
  });

  it("never forwards an identity the browser claims", async () => {
    vi.stubEnv("NODE_ENV", "development");
    const claimed = new Headers({
      "x-customer-id": "CUST-1002",
      "x-dev-user": "admin",
      authorization: "Bearer x",
    });
    expect(await backendCredentials(claimed)).toEqual({});
  });
});
