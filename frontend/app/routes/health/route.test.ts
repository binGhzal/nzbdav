import { afterEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import {
    INTERNAL_REQUEST_CORRELATION_HEADER,
    parsePublicFailureEnvelope,
} from "~/utils/public-failure";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        startRepairRun: vi.fn(async () => ({ status: true })),
        cancelRepairRun: vi.fn(async () => ({ status: true })),
        clearRepairRuns: vi.fn(async () => ({ status: true })),
        retryArrSearchNudge: vi.fn(async () => ({ id: "nudge-1" })),
        clearArrSearchNudges: vi.fn(async () => ({ status: true, deleted: 1 })),
        saveArrCorrelation: vi.fn(async () => ({ status: true })),
        deleteArrCorrelation: vi.fn(async () => ({ status: true })),
    },
}));

describe("health action", () => {
    const correlationId = "a".repeat(32);

    afterEach(() => {
        vi.clearAllMocks();
    });

    it("returns 400 when cancelling a repair run without a run id", async () => {
        const form = new FormData();
        form.append("intent", "cancel");

        const response = await action({
            request: new Request("https://example.test/health", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        await expectPublicFailure(response as Response, 400, "invalid_request", correlationId);
        expect(backendClient.cancelRepairRun).not.toHaveBeenCalled();
    });

    it("returns 400 for unsupported repair actions", async () => {
        const form = new FormData();
        form.append("intent", "unknown");

        const response = await action({
            request: new Request("https://example.test/health", {
                method: "POST",
                body: form,
                headers: { [INTERNAL_REQUEST_CORRELATION_HEADER]: correlationId },
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        await expectPublicFailure(response as Response, 400, "invalid_request", correlationId);
    });

    it("returns 502 when a backend health action fails", async () => {
        vi.mocked(backendClient.cancelRepairRun).mockRejectedValueOnce(new Error("backend offline"));
        const form = new FormData();
        form.append("intent", "cancel");
        form.append("runId", "run-1");

        const response = await action({
            request: new Request("https://example.test/health", {
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
