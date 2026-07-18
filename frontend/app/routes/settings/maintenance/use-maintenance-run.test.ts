import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { MaintenanceRun, MaintenanceRunKind } from "./start-maintenance-task";
import { useMaintenanceRun } from "./use-maintenance-run";

describe("useMaintenanceRun", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("does not overlap a delayed status poll with another refresh", async () => {
        const firstResponse = deferred<Response>();
        const fetchMock = vi.fn()
            .mockReturnValueOnce(firstResponse.promise)
            .mockResolvedValueOnce(statusResponse(maintenanceRun({ message: "newer" })));
        vi.stubGlobal("fetch", fetchMock);
        const { result } = renderHook(() => useMaintenanceRun("recreate-strm-files"));

        let joinedRefresh!: Promise<void>;
        act(() => {
            joinedRefresh = result.current.refresh();
        });

        expect(fetchMock).toHaveBeenCalledTimes(1);

        firstResponse.resolve(statusResponse(maintenanceRun({ message: "initial" })));
        await act(async () => joinedRefresh);

        await act(async () => result.current.refresh());

        expect(fetchMock).toHaveBeenCalledTimes(2);
        expect(result.current.progressMessage).toBe("newer");
    });

    it("does not let a delayed poll overwrite a newly accepted run", async () => {
        const delayedResponse = deferred<Response>();
        vi.stubGlobal("fetch", vi.fn().mockReturnValueOnce(delayedResponse.promise));
        const { result } = renderHook(() => useMaintenanceRun("recreate-strm-files"));
        const acceptedRun = maintenanceRun({
            id: "new-run",
            message: "accepted",
            updatedAt: "2026-07-12T08:00:02Z",
        });

        act(() => result.current.acceptRun(acceptedRun));
        expect(result.current.progressMessage).toBe("accepted");

        delayedResponse.resolve(statusResponse(maintenanceRun({
            id: "old-run",
            message: "stale",
            updatedAt: "2026-07-12T08:00:01Z",
        })));
        await act(async () => {
            await delayedResponse.promise;
            await Promise.resolve();
            await Promise.resolve();
        });

        expect(result.current.progressMessage).toBe("accepted");
        expect(result.current.visibleRun?.id).toBe("new-run");
    });

    it("starts a new poll and ignores the old response when the requested kind changes", async () => {
        const oldKindResponse = deferred<Response>();
        const newKindResponse = deferred<Response>();
        const fetchMock = vi.fn()
            .mockReturnValueOnce(oldKindResponse.promise)
            .mockReturnValueOnce(newKindResponse.promise);
        vi.stubGlobal("fetch", fetchMock);
        const { result, rerender } = renderHook(
            ({ kind }) => useMaintenanceRun(kind),
            { initialProps: { kind: "recreate-strm-files" as MaintenanceRunKind } });

        rerender({ kind: "convert-strm-to-symlinks" });

        expect(fetchMock).toHaveBeenCalledTimes(2);
        expect(fetchMock.mock.calls[1][0]).toContain("kind=convert-strm-to-symlinks");

        newKindResponse.resolve(statusResponse(maintenanceRun({
            id: "new-kind-run",
            kind: "convert-strm-to-symlinks",
            message: "new kind",
        })));
        await act(async () => {
            await newKindResponse.promise;
            await Promise.resolve();
            await Promise.resolve();
        });

        oldKindResponse.resolve(statusResponse(maintenanceRun({
            id: "old-kind-run",
            kind: "recreate-strm-files",
            message: "old kind",
        })));
        await act(async () => {
            await oldKindResponse.promise;
            await Promise.resolve();
            await Promise.resolve();
        });

        expect(result.current.progressMessage).toBe("new kind");
        expect(result.current.visibleRun?.id).toBe("new-kind-run");
    });
});

function maintenanceRun(overrides: Partial<MaintenanceRun> = {}): MaintenanceRun {
    return {
        id: "run",
        kind: "recreate-strm-files",
        status: "running",
        requestedBy: "manual",
        createdAt: "2026-07-12T08:00:00Z",
        startedAt: "2026-07-12T08:00:00Z",
        updatedAt: "2026-07-12T08:00:00Z",
        completedAt: null,
        cancellationRequestedAt: null,
        progressCurrent: 0,
        progressTotal: null,
        message: null,
        error: null,
        ...overrides,
    };
}

function statusResponse(run: MaintenanceRun) {
    return Response.json({ activeRun: run, lastRun: null });
}

function deferred<T>() {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, reject, resolve };
}
