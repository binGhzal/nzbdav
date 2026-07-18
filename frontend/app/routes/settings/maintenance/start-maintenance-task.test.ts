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
                kind: "recreate-strm-files",
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
            "/api/recreate-strm-files",
            "recreate STRM files",
            error => errors.push(error));

        expect(fetchMock).toHaveBeenCalledWith("/nzbdav/api/recreate-strm-files", { method: "POST" });
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
            "/api/recreate-strm-files",
            "recreate STRM files",
            error => errors.push(error));

        expect(run).toBeNull();
        expect(errors.at(-1)).toBe(
            "Failed to start recreate STRM files: A maintenance run is already active.");
    });
});
