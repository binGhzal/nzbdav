import { afterEach, describe, expect, it, vi } from "vitest";
import { action, headers } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import {
    INTERNAL_REQUEST_CORRELATION_HEADER,
    parsePublicFailureEnvelope,
} from "~/utils/public-failure";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        updateConfig: vi.fn(async () => true),
    },
}));

describe("settings update action", () => {
    const correlationId = "b".repeat(32);

    afterEach(() => {
        vi.clearAllMocks();
    });

    it("projects only a valid closed public failure identity into the route headers", () => {
        const projected = headers({
            actionHeaders: new Headers({
                "Set-Cookie": "private=credential-marker",
                "X-Correlation-ID": correlationId,
                "X-Error-Code": "invalid_request",
                "X-Fixture": "private-runtime-path",
            }),
        } as never);

        expect(Object.fromEntries(projected)).toEqual({
            "x-correlation-id": correlationId,
            "x-error-code": "invalid_request",
        });
        expect(Object.fromEntries(headers({
            actionHeaders: new Headers({
                "X-Correlation-ID": "not-a-correlation",
                "X-Error-Code": "invalid_request",
            }),
        } as never))).toEqual({});
    });

    it("returns 400 when config form value is missing", async () => {
        const form = new FormData();

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        await expectPublicFailure(response as Response, 400, "invalid_request", correlationId);
        expect(backendClient.updateConfig).not.toHaveBeenCalled();
    });

    it("returns 400 when config form value is malformed JSON", async () => {
        const form = new FormData();
        form.append("config", "{bad-json");

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        await expectPublicFailure(response as Response, 400, "invalid_request", correlationId);
        expect(backendClient.updateConfig).not.toHaveBeenCalled();
    });

    it("updates config entries from valid JSON object values", async () => {
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
            "queue.max-concurrent-downloads": "12",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toEqual({
            config: {
                "webdav.user": "admin",
                "queue.max-concurrent-downloads": "12",
            },
        });
        expect(backendClient.updateConfig).toHaveBeenCalledWith([
            { configName: "webdav.user", configValue: "admin" },
            { configName: "queue.max-concurrent-downloads", configValue: "12" },
        ]);
    });

    it("returns 502 when backend config update fails", async () => {
        vi.mocked(backendClient.updateConfig).mockRejectedValueOnce(new Error("credential-marker|backend offline\r\n"));
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(502);
        await expectPublicFailure(response as Response, 502, "upstream_unavailable", correlationId);
    });

    it("returns 502 when backend returns unsuccessful status", async () => {
        vi.mocked(backendClient.updateConfig).mockResolvedValueOnce(false);
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(502);
        await expectPublicFailure(response as Response, 502, "upstream_unavailable", correlationId);
    });
});

async function expectPublicFailure(
    response: Response,
    status: number,
    code: "invalid_request" | "upstream_unavailable",
    correlationId: string,
) {
    expect(response.status).toBe(status);
    expect(response.headers.get("x-error-code")).toBe(code);
    expect(response.headers.get("x-correlation-id")).toBe(correlationId);
    const body = await response.text();
    expect(parsePublicFailureEnvelope(body)).toEqual({
        status: false,
        error: code === "invalid_request" ? "The request is invalid." : "The backend is unavailable.",
        code,
        correlation_id: correlationId,
    });
}
