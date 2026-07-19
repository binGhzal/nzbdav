import { useEffect } from "react";
import { withUrlBase } from "~/utils/url-base";

type HealthQueueResponse<T> = {
    items: T[];
    uncheckedCount: number;
};

export function useHealthQueueTopUp<T>(
    queueItems: readonly T[],
    setQueueItems: (value: React.SetStateAction<T[]>) => void,
    setUncheckedCount: (value: React.SetStateAction<number>) => void,
    minVisibleItems = 15,
    pageSize = 30
) {
    const visibleCount = queueItems.length;

    useEffect(() => {
        if (visibleCount >= minVisibleItems) return;

        let disposed = false;
        const abortController = new AbortController();
        const refetchData = async () => {
            try {
                const response = await fetch(
                    withUrlBase(`/api/get-health-check-queue?pageSize=${pageSize}`),
                    { signal: abortController.signal });
                if (disposed || !response.ok) return;

                const healthCheckQueue = await response.json() as HealthQueueResponse<T>;
                if (disposed) return;

                setQueueItems(healthCheckQueue.items);
                setUncheckedCount(healthCheckQueue.uncheckedCount);
            } catch {
                if (!abortController.signal.aborted)
                    console.warn("Failed to refresh health check queue");
            }
        };

        refetchData();

        return () => {
            disposed = true;
            abortController.abort();
        };
    }, [visibleCount, minVisibleItems, pageSize, setQueueItems, setUncheckedCount]);
}
