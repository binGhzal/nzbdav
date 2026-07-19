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

    it("does not surface non-json backend error bodies", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response("backend unavailable", {
                status: 502,
                statusText: "Bad Gateway",
            })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow("Failed to get config items: HTTP 502");
    });

    it("accepts only the bounded stable backend failure envelope", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response(JSON.stringify({
                status: false,
                error: "The request is invalid.",
                code: "invalid_request",
                correlation_id: "0123456789abcdef0123456789abcdef",
            }), { status: 400 })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow(
                "Failed to get config items: The request is invalid. (0123456789abcdef0123456789abcdef)",
            );
    });

    it("rejects a valid backend envelope when valid failure headers conflict", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response(JSON.stringify({
                status: false,
                error: "The request is invalid.",
                code: "invalid_request",
                correlation_id: "0123456789abcdef0123456789abcdef",
            }), {
                status: 400,
                headers: {
                    "X-Correlation-ID": "fedcba9876543210fedcba9876543210",
                    "X-Error-Code": "internal_error",
                },
            })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow("Failed to get config items: HTTP 400");
    });

    it.each([
        ["code/message mismatch", "Authentication is required.", "invalid_request", "0123456789abcdef0123456789abcdef"],
        ["malformed correlation", "The request is invalid.", "invalid_request", "invalid"],
    ])("rejects backend envelopes with a %s", async (_name, error, code, correlationId) => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response(JSON.stringify({
                status: false,
                error,
                code,
                correlation_id: correlationId,
            }), { status: 400 })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow("Failed to get config items: HTTP 400");
    });

    it("does not render arbitrary JSON, controls, parser text, or oversized values", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        const fragments = ["credential-marker", "provider-body-marker"];
        const hostile = `${fragments.join("|")}\r\n\u001b\u0001${"x".repeat(5000)}`;
        vi.stubGlobal("fetch", vi.fn(async () =>
            new Response(JSON.stringify({ error: hostile }), { status: 500 })));

        let message = "";
        try {
            await backendClient.getConfig(["api.key"]);
        } catch (error) {
            message = error instanceof Error ? error.message : String(error);
        }

        expect(message).toBe("Failed to get config items: HTTP 500");
        for (const fragment of fragments) expect(message.includes(fragment)).toBe(false);
    });

    it("cancels an oversized backend failure stream", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        let cancelled = false;
        const stream = new ReadableStream<Uint8Array>({
            start(controller) {
                controller.enqueue(new TextEncoder().encode("x".repeat(513)));
            },
            cancel() {
                cancelled = true;
            },
        });
        vi.stubGlobal("fetch", vi.fn(async () => new Response(stream, { status: 500 })));

        await expect(backendClient.getConfig(["api.key"]))
            .rejects.toThrow("Failed to get config items: HTTP 500");
        expect(cancelled).toBe(true);
    });

    it("does not render parser-controlled text from malformed success JSON", async () => {
        process.env.BACKEND_URL = "http://backend.test";
        process.env.FRONTEND_BACKEND_API_KEY = "secret";
        const fragments = ["credential-marker", "provider-body-marker"];
        const hostile = `{${fragments.join("|")}\r\n\u001b${"x".repeat(1000)}`;
        vi.stubGlobal("fetch", vi.fn(async () => new Response(hostile, { status: 200 })));

        let message = "";
        try {
            await backendClient.getQueue({ start: 0, limit: 10 });
        } catch (error) {
            message = error instanceof Error ? error.message : String(error);
        }

        expect(message).toBe("Failed to parse get queue response.");
        for (const fragment of fragments) expect(message.includes(fragment)).toBe(false);
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
