import { describe, expect, it, vi } from "vitest";
import { createQueueRefreshScheduler } from "./queue-refresh-controller";

describe("createQueueRefreshScheduler", () => {
    it("coalesces refresh requests while revalidation is already running", () => {
        vi.useFakeTimers();
        let state: "idle" | "loading" = "loading";
        const revalidate = vi.fn();
        const scheduler = createQueueRefreshScheduler({
            getState: () => state,
            revalidate,
            delayMs: 350,
        });

        scheduler.request();
        vi.advanceTimersByTime(350);

        expect(revalidate).not.toHaveBeenCalled();

        scheduler.request();
        vi.advanceTimersByTime(350);

        expect(revalidate).not.toHaveBeenCalled();

        state = "idle";
        scheduler.onStateChange();
        vi.advanceTimersByTime(349);

        expect(revalidate).not.toHaveBeenCalled();

        vi.advanceTimersByTime(1);

        expect(revalidate).toHaveBeenCalledTimes(1);
        scheduler.dispose();
        vi.useRealTimers();
    });

    it("does not postpone a pending refresh when idle state notifications repeat", () => {
        vi.useFakeTimers();
        let state: "idle" | "loading" = "loading";
        const revalidate = vi.fn();
        const scheduler = createQueueRefreshScheduler({
            getState: () => state,
            revalidate,
            delayMs: 350,
        });

        scheduler.request();
        vi.advanceTimersByTime(350);
        expect(revalidate).not.toHaveBeenCalled();

        state = "idle";
        scheduler.onStateChange();
        vi.advanceTimersByTime(200);
        scheduler.onStateChange();
        vi.advanceTimersByTime(150);

        expect(revalidate).toHaveBeenCalledTimes(1);
        scheduler.dispose();
        vi.useRealTimers();
    });

    it("does not postpone a pending refresh when refresh requests repeat while idle", () => {
        vi.useFakeTimers();
        const revalidate = vi.fn();
        const scheduler = createQueueRefreshScheduler({
            getState: () => "idle",
            revalidate,
            delayMs: 350,
        });

        scheduler.request();
        vi.advanceTimersByTime(200);
        scheduler.request();
        vi.advanceTimersByTime(150);

        expect(revalidate).toHaveBeenCalledTimes(1);
        scheduler.dispose();
        vi.useRealTimers();
    });

    it("does not issue duplicate refreshes before the router reports loading", () => {
        vi.useFakeTimers();
        let state: "idle" | "loading" = "idle";
        const revalidate = vi.fn();
        const scheduler = createQueueRefreshScheduler({
            getState: () => state,
            revalidate,
            delayMs: 350,
        });

        scheduler.request();
        vi.advanceTimersByTime(350);
        expect(revalidate).toHaveBeenCalledTimes(1);

        scheduler.request();
        vi.advanceTimersByTime(350);
        expect(revalidate).toHaveBeenCalledTimes(1);

        state = "loading";
        scheduler.onStateChange();
        scheduler.request();
        vi.advanceTimersByTime(350);
        expect(revalidate).toHaveBeenCalledTimes(1);

        state = "idle";
        scheduler.onStateChange();
        vi.advanceTimersByTime(350);
        expect(revalidate).toHaveBeenCalledTimes(2);

        scheduler.dispose();
        vi.useRealTimers();
    });

    it("cancels pending refresh timers on dispose", () => {
        vi.useFakeTimers();
        const revalidate = vi.fn();
        const scheduler = createQueueRefreshScheduler({
            getState: () => "idle",
            revalidate,
            delayMs: 350,
        });

        scheduler.request();
        scheduler.dispose();
        vi.advanceTimersByTime(350);

        expect(revalidate).not.toHaveBeenCalled();
        vi.useRealTimers();
    });
});
