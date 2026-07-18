import { afterEach, describe, expect, it, vi } from "vitest";
import { createHealthRefreshController } from "./health-refresh-controller";

describe("createHealthRefreshController", () => {
    afterEach(() => {
        vi.useRealTimers();
    });

    it("does not poll while the health page is hidden", () => {
        vi.useFakeTimers();
        const revalidate = vi.fn(async () => undefined);
        const controller = createHealthRefreshController({
            getVisibility: () => "hidden",
            canRevalidate: () => true,
            revalidate,
            intervalMs: 1_000,
        });

        controller.start();
        vi.advanceTimersByTime(5_000);

        expect(revalidate).not.toHaveBeenCalled();
        controller.dispose();
    });

    it("does not overlap a slow revalidation with later timer ticks", async () => {
        vi.useFakeTimers();
        const first = deferred<void>();
        const revalidate = vi.fn()
            .mockReturnValueOnce(first.promise)
            .mockResolvedValue(undefined);
        const controller = createHealthRefreshController({
            getVisibility: () => "visible",
            canRevalidate: () => true,
            revalidate,
            intervalMs: 1_000,
        });

        controller.start();
        await vi.advanceTimersByTimeAsync(1_000);
        expect(revalidate).toHaveBeenCalledTimes(1);

        await vi.advanceTimersByTimeAsync(3_000);
        expect(revalidate).toHaveBeenCalledTimes(1);

        first.resolve();
        await first.promise;
        await Promise.resolve();
        await vi.advanceTimersByTimeAsync(1_000);

        expect(revalidate).toHaveBeenCalledTimes(2);
        controller.dispose();
    });

    it("refreshes immediately when the page becomes visible and restarts polling", async () => {
        vi.useFakeTimers();
        let visibility: DocumentVisibilityState = "hidden";
        const revalidate = vi.fn(async () => undefined);
        const controller = createHealthRefreshController({
            getVisibility: () => visibility,
            canRevalidate: () => true,
            revalidate,
            intervalMs: 1_000,
        });

        controller.start();
        visibility = "visible";
        controller.visibilityChanged();
        await Promise.resolve();

        expect(revalidate).toHaveBeenCalledTimes(1);

        await vi.advanceTimersByTimeAsync(1_000);
        expect(revalidate).toHaveBeenCalledTimes(2);
        controller.dispose();
    });

    it("waits for router navigation to become idle before refreshing", async () => {
        vi.useFakeTimers();
        let canRevalidate = false;
        const revalidate = vi.fn(async () => undefined);
        const controller = createHealthRefreshController({
            getVisibility: () => "visible",
            canRevalidate: () => canRevalidate,
            revalidate,
            intervalMs: 1_000,
        });

        controller.start();
        await vi.advanceTimersByTimeAsync(1_000);
        expect(revalidate).not.toHaveBeenCalled();

        canRevalidate = true;
        await vi.advanceTimersByTimeAsync(1_000);
        expect(revalidate).toHaveBeenCalledTimes(1);
        controller.dispose();
    });
});

function deferred<T>() {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, reject, resolve };
}
