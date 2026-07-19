import { afterEach, describe, expect, it, vi } from "vitest";
import { startMaintenanceTask } from "./start-maintenance-task";

describe("startMaintenanceTask", () => {
    afterEach(() => {
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("starts maintenance with POST and returns the accepted durable run", async () => {
        const fetchMock = vi.fn(async () => Response.json({
            run: {
                id: "0ed445e8-3784-4ec1-a440-46cd881f3c1f",
                kind: "remove-unlinked-files",
                status: "queued",
                requestedBy: "manual",
                createdAt: "2026-07-12T08:00:00Z",
                startedAt: null,
                updatedAt: "2026-07-12T08:00:00Z",
                completedAt: null,
                cancellationRequestedAt: null,
                progressCurrent: 0,
                progressTotal: null,
                message: "Queued.",
                error: null,
            },
        }, {
            status: 202,
            headers: { Location: "/api/maintenance/runs/0ed445e8-3784-4ec1-a440-46cd881f3c1f" },
        }));
        vi.stubGlobal("fetch", fetchMock);
        const errors: Array<string | null> = [];

        const run = await startMaintenanceTask(
            "/api/remove-unlinked-files",
            "remove unlinked files",
            error => errors.push(error));

        expect(fetchMock).toHaveBeenCalledWith("/nzbdav/api/remove-unlinked-files", { method: "POST" });
        expect(run?.status).toBe("queued");
        expect(errors.at(-1)).toBeNull();
    });

    it("rejects a legacy false body even when the server returns HTTP 200", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: false,
            error: "A maintenance run is already active.",
        })));
        const errors: Array<string | null> = [];

        const run = await startMaintenanceTask(
            "/api/remove-unlinked-files",
            "remove unlinked files",
            error => errors.push(error));

        expect(run).toBeNull();
        expect(errors.at(-1)).toBe("Failed to start remove unlinked files: HTTP 200.");
        expect(errors.at(-1)).not.toContain("already active");
    });

    it("renders an exact stable maintenance conflict from compatibility headers", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: false,
            error: "hostile-legacy-detail",
            activeRun: { id: "fixture" },
        }, {
            status: 409,
            headers: {
                "X-Error-Code": "maintenance_run_active",
                "X-Correlation-ID": "0123456789abcdef0123456789abcdef",
            },
        })));
        const errors: Array<string | null> = [];

        await startMaintenanceTask(
            "/api/remove-unlinked-files",
            "remove unlinked files",
            error => errors.push(error));

        expect(errors.at(-1)).toBe(
            "Failed to start remove unlinked files: A maintenance run is already active. (0123456789abcdef0123456789abcdef).");
        expect(errors.at(-1)).not.toContain("hostile-legacy-detail");
    });

    it("does not render a thrown transport error", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => {
            throw new Error("transport-secret\r\n\u001b[31m");
        }));
        const errors: Array<string | null> = [];

        await startMaintenanceTask(
            "/api/remove-unlinked-files",
            "remove unlinked files",
            error => errors.push(error));

        expect(errors.at(-1)).toBe("Failed to start remove unlinked files: request failed.");
        expect(errors.at(-1)).not.toContain("transport-secret");
    });
});
