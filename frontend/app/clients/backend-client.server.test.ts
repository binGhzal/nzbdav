import { afterEach, describe, expect, it, vi } from "vitest";
import { backendClient } from "./backend-client.server";

describe("backendClient", () => {
    const originalBackendUrl = process.env.BACKEND_URL;
    const originalApiKey = process.env.FRONTEND_BACKEND_API_KEY;

    afterEach(() => {
        vi.unstubAllGlobals();
        process.env.BACKEND_URL = originalBackendUrl;
        process.env.FRONTEND_BACKEND_API_KEY = originalApiKey;
    });

    it("surfaces non-json backend error bodies without masking them as parse failures", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response("backend unavailable", {
                status: 502,
                statusText: "Bad Gateway",
            })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow("Failed to get config items: backend unavailable");
    });

    it("adds operation context when a successful backend response is malformed", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response("{not-json", {
                status: 200,
                headers: { "Content-Type": "application/json" },
            })));

        await expect(backendClient.getQueue({ start: 0, limit: 10 }))
            .rejects.toThrow("Failed to parse get queue response");
    });
});
