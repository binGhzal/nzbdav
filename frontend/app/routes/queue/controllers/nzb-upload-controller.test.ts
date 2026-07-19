import { describe, expect, it } from "vitest";
import { getUploadFailureMessage, shouldAbortUploadResponse } from "./nzb-upload-controller";

describe("getUploadFailureMessage", () => {
    it("renders an exact stable failure envelope", () => {
        const body = JSON.stringify({
            status: false,
            error: "The request is invalid.",
            code: "invalid_request",
            correlation_id: "0123456789abcdef0123456789abcdef",
        });

        expect(getUploadFailureMessage(200, body, () => null)).toBe(
            "The request is invalid. (0123456789abcdef0123456789abcdef)");
    });

    it("uses a fixed fallback for hostile and oversized bodies", () => {
        const body = `upload-secret\r\n\u001b[31m${"x".repeat(1024)}`;

        const message = getUploadFailureMessage(502, body, () => null);

        expect(message).toBe("HTTP 502");
        expect(message).not.toContain("upload-secret");
    });

    it("uses exact known headers without rendering an incompatible legacy body", () => {
        const values: Record<string, string> = {
            "x-error-code": "invalid_request",
            "x-correlation-id": "0123456789abcdef0123456789abcdef",
        };

        const message = getUploadFailureMessage(
            400,
            JSON.stringify({ status: false, error: "upload-secret", extra: true }),
            name => values[name.toLowerCase()] ?? null);

        expect(message).toBe("The request is invalid. (0123456789abcdef0123456789abcdef)");
        expect(message).not.toContain("upload-secret");
    });

    it("rejects conflicting valid body and header failure identities", () => {
        const body = JSON.stringify({
            status: false,
            error: "The request is invalid.",
            code: "invalid_request",
            correlation_id: "0123456789abcdef0123456789abcdef",
        });
        const values: Record<string, string> = {
            "x-error-code": "internal_error",
            "x-correlation-id": "fedcba9876543210fedcba9876543210",
        };

        expect(getUploadFailureMessage(400, body, name => values[name.toLowerCase()] ?? null))
            .toBe("HTTP 400");
    });

    it("aborts response buffering as soon as progress exceeds the public envelope cap", () => {
        expect(shouldAbortUploadResponse(512)).toBe(false);
        expect(shouldAbortUploadResponse(513)).toBe(true);
        expect(shouldAbortUploadResponse(50_000)).toBe(true);
    });
});
