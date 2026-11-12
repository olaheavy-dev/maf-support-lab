// @vitest-environment node
import { GET } from "./route";

describe("the liveness check", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("answers ok without calling anything", async () => {
    const response = GET();
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ status: "ok" });
  });

  it("names the commit it was built from", async () => {
    vi.stubEnv("GIT_COMMIT", "2555364");
    expect(await GET().json()).toEqual({ status: "ok", commit: "2555364" });
  });

  it("reports no commit for a build without one", async () => {
    vi.stubEnv("GIT_COMMIT", "");
    expect(await GET().json()).toEqual({ status: "ok", commit: null });
  });
});
