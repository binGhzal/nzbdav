import { afterEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";
import { backendClient } from "~/clients/backend-client.server";

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
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        expect(await (response as Response).json()).toEqual({
            error: "Repair run id is required.",
        });
        expect(backendClient.cancelRepairRun).not.toHaveBeenCalled();
    });

    it("returns 400 for unsupported repair actions", async () => {
        const form = new FormData();
        form.append("intent", "unknown");

        const response = await action({
            request: new Request("https://example.test/health", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        expect(await (response as Response).json()).toEqual({
            error: "Unsupported repair action.",
        });
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
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(502);
        expect(await (response as Response).json()).toEqual({
            error: "backend offline",
        });
    });
});
