import { describe, expect, it } from "vitest";
import { getHttpErrorMessage, readJsonObjectOrEmpty } from "./http-response";

describe("http-response helpers", () => {
    it("uses a plain text backend error body as the error message", async () => {
        const response = new Response("backend unavailable", { status: 502 });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("backend unavailable");
    });

    it("uses a JSON error field as the error message", async () => {
        const response = new Response(JSON.stringify({ error: "invalid host" }), {
            status: 400,
            headers: { "content-type": "application/json" },
        });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("invalid host");
    });

    it("falls back to status when the backend response body is empty", async () => {
        const response = new Response("", { status: 503, statusText: "Service Unavailable" });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("503 Service Unavailable");
    });

    it("returns an empty object for malformed JSON success bodies", async () => {
        const response = new Response("not-json", { status: 200 });

        await expect(readJsonObjectOrEmpty(response))
            .resolves.toEqual({});
    });
});
