import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { readJsonObjectOrEmpty } from "~/utils/http-response";
import { withUrlBase } from "~/utils/url-base";
import type { MaintenanceRun, MaintenanceRunKind } from "./start-maintenance-task";

type MaintenanceStatusResponse = {
    activeRun?: MaintenanceRun | null;
    lastRun?: MaintenanceRun | null;
};

const activeStatuses = new Set(["queued", "running", "cancellation-requested"]);

export function useMaintenanceRun(
    kind: MaintenanceRunKind,
    relatedKinds: MaintenanceRunKind[] = [kind],
    queryAllKinds = false,
) {
    const [activeRun, setActiveRun] = useState<MaintenanceRun | null>(null);
    const [lastRun, setLastRun] = useState<MaintenanceRun | null>(null);
    const refreshInFlight = useRef<{ key: string; request: Promise<void> } | null>(null);
    const refreshGeneration = useRef(0);
    const stateRevision = useRef(0);

    const refresh = useCallback(() => {
        const requestKey = queryAllKinds ? "all" : `kind:${kind}`;
        if (refreshInFlight.current?.key === requestKey)
            return refreshInFlight.current.request;

        const expectedGeneration = ++refreshGeneration.current;
        const expectedRevision = stateRevision.current;
        const request = (async () => {
            try {
                const query = queryAllKinds ? "" : `?kind=${encodeURIComponent(kind)}`;
                const response = await fetch(withUrlBase(`/api/maintenance/status${query}`), {
                    method: "GET",
                });
                if (!response.ok) return;
                const status = await readJsonObjectOrEmpty<MaintenanceStatusResponse>(response);
                if (refreshGeneration.current !== expectedGeneration
                    || stateRevision.current !== expectedRevision) return;
                setActiveRun(status.activeRun ?? null);
                setLastRun(status.lastRun ?? null);
            } catch {
                // Persisted state remains visible during a transient poll failure.
            }
        })();
        refreshInFlight.current = { key: requestKey, request };
        void request.finally(() => {
            if (refreshInFlight.current?.request === request) refreshInFlight.current = null;
        });
        return request;
    }, [kind, queryAllKinds]);

    useEffect(() => {
        void refresh();
        const timer = window.setInterval(() => void refresh(), 1000);
        return () => window.clearInterval(timer);
    }, [refresh]);

    const acceptRun = useCallback((run: MaintenanceRun) => {
        stateRevision.current += 1;
        setActiveRun(run);
        setLastRun(run);
    }, []);

    const isActive = !!activeRun && activeStatuses.has(activeRun.status);
    const ownsActiveRun = !!activeRun && relatedKinds.includes(activeRun.kind);
    const visibleRun = ownsActiveRun
        ? activeRun
        : lastRun && relatedKinds.includes(lastRun.kind) ? lastRun : null;
    const progressMessage = useMemo(() => {
        if (activeRun && !relatedKinds.includes(activeRun.kind) && activeStatuses.has(activeRun.status))
            return `Another maintenance task is ${activeRun.status}: ${activeRun.kind}.`;
        if (!visibleRun) return null;
        if (visibleRun.error) return `Failed: ${visibleRun.error}`;
        return visibleRun.message ?? visibleRun.status;
    }, [activeRun, relatedKinds, visibleRun]);

    return { acceptRun, isActive, progressMessage, refresh, visibleRun };
}
