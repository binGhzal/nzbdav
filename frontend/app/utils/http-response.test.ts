import { describe, expect, it } from "vitest";
import { getHttpErrorMessage, readHttpActionResult, readJsonObjectOrEmpty } from "./http-response";

describe("http-response helpers", () => {
    it("rejects a plain text backend error body", async () => {
        const response = new Response("backend unavailable", { status: 502 });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("HTTP 502");
    });

    it("rejects arbitrary JSON error fields", async () => {
        const response = new Response(JSON.stringify({ error: "invalid host" }), {
            status: 400,
            headers: { "content-type": "application/json" },
        });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("HTTP 400");
    });

    it("accepts a bounded stable failure envelope", async () => {
        const response = new Response(JSON.stringify({
            status: false,
            error: "The backend is unavailable.",
            code: "upstream_unavailable",
            correlation_id: "0123456789abcdef0123456789abcdef",
        }), { status: 502, headers: { "content-type": "application/json" } });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("The backend is unavailable. (0123456789abcdef0123456789abcdef)");
    });

    it("rejects conflicting valid body and header failure identities", async () => {
        const response = new Response(JSON.stringify({
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
        });

        await expect(getHttpErrorMessage(response)).resolves.toBe("HTTP 400");
    });

    it.each([
        [404, "resource_not_found", "The requested resource was not found."],
        [409, "maintenance_run_active", "A maintenance run is already active."],
        [403, "endpoint_disabled", "This endpoint is disabled."],
        [504, "connection_timeout", "The connection test timed out."],
    ])("accepts reachable backend code %s/%s", async (status, code, error) => {
        const correlationId = "0123456789abcdef0123456789abcdef";
        const response = new Response(JSON.stringify({
            status: false,
            error,
            code,
            correlation_id: correlationId,
        }), { status });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe(`${error} (${correlationId})`);
    });

    it.each([
        [504, "connection_timeout", "The connection test timed out.", {
            status: true,
            connected: false,
            error: "The connection test timed out.",
            code: "connection_timeout",
            correlation_id: "0123456789abcdef0123456789abcdef",
        }],
        [409, "maintenance_run_active", "A maintenance run is already active.", {
            status: false,
            error: "A maintenance run is already active.",
            code: "maintenance_run_active",
            correlation_id: "0123456789abcdef0123456789abcdef",
            activeRun: { id: "fixture" },
        }],
    ])("uses fixed headers for compatibility failure %s/%s", async (status, code, error, body) => {
        const correlationId = "0123456789abcdef0123456789abcdef";
        const response = new Response(JSON.stringify(body), {
            status,
            headers: {
                "X-Correlation-ID": correlationId,
                "X-Error-Code": code,
            },
        });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe(`${error} (${correlationId})`);
    });

    it.each([
        ["code/message mismatch", "The request is invalid.", "upstream_unavailable", "0123456789abcdef0123456789abcdef"],
        ["malformed correlation", "The backend is unavailable.", "upstream_unavailable", "not-a-correlation"],
    ])("rejects a %s", async (_name, error, code, correlationId) => {
        const response = new Response(JSON.stringify({
            status: false,
            error,
            code,
            correlation_id: correlationId,
        }), { status: 502, headers: { "content-type": "application/json" } });

        await expect(getHttpErrorMessage(response)).resolves.toBe("HTTP 502");
    });

    it("rejects controls and oversized values without rendering them", async () => {
        const fragments = ["credential-marker", "provider-body-marker"];
        const hostile = `${fragments.join("|")}\r\n\u001b\u0001${"x".repeat(5000)}`;
        const response = new Response(JSON.stringify({
            status: false,
            error: hostile,
            code: "internal_error",
            correlation_id: "0123456789abcdef0123456789abcdef",
        }), { status: 500, headers: { "content-type": "application/json" } });

        const rendered = await getHttpErrorMessage(response);

        expect(rendered).toBe("HTTP 500");
        for (const fragment of fragments) expect(rendered.includes(fragment)).toBe(false);
    });

    it("cancels a failure body stream as soon as the envelope byte cap is exceeded", async () => {
        let cancelled = false;
        const stream = new ReadableStream<Uint8Array>({
            start(controller) {
                controller.enqueue(new TextEncoder().encode("x".repeat(513)));
            },
            cancel() {
                cancelled = true;
            },
        });
        const response = new Response(stream, { status: 500 });

        await expect(getHttpErrorMessage(response)).resolves.toBe("HTTP 500");
        expect(cancelled).toBe(true);
    });

    it("cancels a failure body whose declared length exceeds the envelope byte cap", async () => {
        let cancelled = false;
        const stream = new ReadableStream<Uint8Array>({
            pull() {
                // A hostile body may never produce bytes; declared oversize must still release it.
            },
            cancel() {
                cancelled = true;
            },
        });
        const response = new Response(stream, {
            status: 500,
            headers: { "Content-Length": "513" },
        });

        await expect(getHttpErrorMessage(response)).resolves.toBe("HTTP 500");
        expect(cancelled).toBe(true);
    });

    it("falls back to status when the backend response body is empty", async () => {
        const response = new Response("", { status: 503, statusText: "Service Unavailable" });

        await expect(getHttpErrorMessage(response))
            .resolves.toBe("HTTP 503");
    });

    it("returns an empty object for malformed JSON success bodies", async () => {
        const response = new Response("not-json", { status: 200 });

        await expect(readJsonObjectOrEmpty(response))
            .resolves.toEqual({});
    });

    it("accepts the bounded SAB mutation success contract", async () => {
        await expect(readHttpActionResult(Response.json({ status: true })))
            .resolves.toMatchObject({ success: true, data: { status: true } });
    });

    it("fails closed when a SAB mutation success body exceeds its contract cap", async () => {
        const result = await readHttpActionResult(Response.json({ status: true, padding: "x".repeat(600) }));

        expect(result).toEqual({ success: false, data: {}, error: "HTTP 200" });
    });
});
