type RevalidationState = "idle" | "loading" | "submitting";

export type QueueRefreshSchedulerOptions = {
    getState: () => RevalidationState;
    revalidate: () => void;
    delayMs?: number;
};

export type QueueRefreshScheduler = {
    request: () => void;
    onStateChange: () => void;
    dispose: () => void;
};

export function createQueueRefreshScheduler({
    getState,
    revalidate,
    delayMs = 350,
}: QueueRefreshSchedulerOptions): QueueRefreshScheduler {
    let refreshTimeout: ReturnType<typeof setTimeout> | undefined;
    let pendingAfterCurrentRevalidation = false;
    let waitingForRevalidationToStart = false;

    function clearPendingTimeout() {
        if (refreshTimeout === undefined) return;
        clearTimeout(refreshTimeout);
        refreshTimeout = undefined;
    }

    function schedule() {
        clearPendingTimeout();
        refreshTimeout = setTimeout(() => {
            refreshTimeout = undefined;
            if (getState() !== "idle") {
                pendingAfterCurrentRevalidation = true;
                return;
            }

            pendingAfterCurrentRevalidation = false;
            waitingForRevalidationToStart = true;
            revalidate();
        }, delayMs);
    }

    return {
        request: () => {
            if (refreshTimeout !== undefined) return;
            if (waitingForRevalidationToStart && getState() === "idle") return;
            schedule();
        },
        onStateChange: () => {
            if (waitingForRevalidationToStart && getState() !== "idle") {
                waitingForRevalidationToStart = false;
            }

            if (!pendingAfterCurrentRevalidation || getState() !== "idle") return;
            if (refreshTimeout !== undefined) return;
            schedule();
        },
        dispose: () => {
            clearPendingTimeout();
            pendingAfterCurrentRevalidation = false;
            waitingForRevalidationToStart = false;
        },
    };
}
