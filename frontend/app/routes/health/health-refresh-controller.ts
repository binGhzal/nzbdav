export type HealthRefreshControllerOptions = {
    getVisibility: () => DocumentVisibilityState;
    canRevalidate: () => boolean;
    revalidate: () => Promise<void>;
    intervalMs: number;
};

export type HealthRefreshController = {
    start: () => void;
    request: () => Promise<boolean>;
    visibilityChanged: () => void;
    dispose: () => void;
};

export function createHealthRefreshController({
    getVisibility,
    canRevalidate,
    revalidate,
    intervalMs,
}: HealthRefreshControllerOptions): HealthRefreshController {
    let interval: ReturnType<typeof setInterval> | undefined;
    let inFlight = false;
    let disposed = false;

    const stopPolling = () => {
        if (interval === undefined) return;
        clearInterval(interval);
        interval = undefined;
    };

    const request = async () => {
        if (disposed || inFlight || getVisibility() !== "visible" || !canRevalidate())
            return false;

        inFlight = true;
        try {
            await revalidate();
            return true;
        } catch {
            return false;
        } finally {
            inFlight = false;
        }
    };

    const startPolling = () => {
        if (disposed || interval !== undefined || getVisibility() !== "visible") return;
        interval = setInterval(() => void request(), intervalMs);
    };

    return {
        start: startPolling,
        request,
        visibilityChanged: () => {
            if (getVisibility() !== "visible") {
                stopPolling();
                return;
            }

            startPolling();
            void request();
        },
        dispose: () => {
            disposed = true;
            stopPolling();
        },
    };
}
