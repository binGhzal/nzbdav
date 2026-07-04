import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { Alert } from "react-bootstrap";
import { getWebsocketUrl, withUrlBase } from "~/utils/url-base";
import { useNavigation } from "react-router";
import { OperationsStatus } from "./components/operations-status/operations-status";

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
}

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config, repairStatus, fullStatus, arrValidation, arrNudges, arrCorrelations] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey]),
        loadOptional(() => backendClient.getRepairStatus()),
        loadOptional(() => backendClient.getFullStatus()),
        loadOptional(() => backendClient.getArrValidation()),
        loadOptional(() => backendClient.getArrSearchNudges(25)),
        loadOptional(() => backendClient.getArrCorrelations(25))
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        queueItems: queueData.items,
        historyStats: historyData.stats,
        historyItems: historyData.items,
        repairStatus: repairStatus.data,
        repairStatusError: repairStatus.error,
        fullStatus: fullStatus.data,
        fullStatusError: fullStatus.error,
        arrValidation: arrValidation.data,
        arrValidationError: arrValidation.error,
        arrNudges: arrNudges.data,
        arrNudgesError: arrNudges.error,
        arrCorrelations: arrCorrelations.data,
        arrCorrelationsError: arrCorrelations.error,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const intent = formData.get("intent")?.toString();

    if (intent === "start") {
        await backendClient.startRepairRun();
        return { ok: true };
    }

    if (intent === "cancel") {
        const runId = formData.get("runId")?.toString();
        if (!runId) throw new Error("Repair run id is required.");
        await backendClient.cancelRepairRun(runId);
        return { ok: true };
    }

    if (intent === "clear") {
        await backendClient.clearRepairRuns();
        return { ok: true };
    }

    if (intent === "retry-arr-nudge") {
        const id = formData.get("id")?.toString();
        if (!id) throw new Error("ARR search nudge id is required.");
        await backendClient.retryArrSearchNudge(id);
        return { ok: true };
    }

    if (intent === "clear-arr-failed-nudges") {
        await backendClient.clearArrSearchNudges("failed");
        return { ok: true };
    }

    if (intent === "save-arr-correlation") {
        await backendClient.saveArrCorrelation({
            id: emptyToUndefined(formData.get("id")?.toString()),
            nzo_id: emptyToUndefined(formData.get("nzo_id")?.toString()),
            arr_app: emptyToUndefined(formData.get("arr_app")?.toString()),
            instance_key: emptyToUndefined(formData.get("instance_key")?.toString()),
            instance_host: emptyToUndefined(formData.get("instance_host")?.toString()),
            download_id: emptyToUndefined(formData.get("download_id")?.toString()),
            movie_id: numberOrUndefined(formData.get("movie_id")?.toString()),
            series_id: numberOrUndefined(formData.get("series_id")?.toString()),
            episode_id: numberOrUndefined(formData.get("episode_id")?.toString()),
            season_number: numberOrUndefined(formData.get("season_number")?.toString()),
            release_title: emptyToUndefined(formData.get("release_title")?.toString()),
            category: emptyToUndefined(formData.get("category")?.toString()),
            quality: emptyToUndefined(formData.get("quality")?.toString()),
            is_duplicate: formData.get("is_duplicate") === "on"
        });
        return { ok: true };
    }

    if (intent === "delete-arr-correlation") {
        const id = formData.get("id")?.toString();
        if (!id) throw new Error("ARR correlation id is required.");
        await backendClient.deleteArrCorrelation(id);
        return { ok: true };
    }

    throw new Error("Unsupported repair action.");
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueItems, setQueueItems] = useState(loaderData.queueItems);
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [websocketState, setWebsocketState] = useState<"connecting" | "connected" | "disconnected">("connecting");
    const navigation = useNavigation();
    const isActionSubmitting = navigation.state !== "idle" && navigation.formMethod?.toLowerCase() === "post";

    // effects
    useEffect(() => {
        if (queueItems.length >= 15) return;
        const refetchData = async () => {
            var response = await fetch(withUrlBase('/api/get-health-check-queue?pageSize=30'));
            if (response.ok) {
                const healthCheckQueue = await response.json();
                setQueueItems(healthCheckQueue.items);
                setUncheckedCount(healthCheckQueue.uncheckedCount);
            }
        };
        refetchData();
    }, [queueItems, setQueueItems])

    // events
    const onHealthItemStatus = useCallback(async (message: string) => {
        const [davItemId, healthResult, repairAction] = message.split('|');
        setQueueItems(x => x.filter(item => item.id !== davItemId));
        setUncheckedCount(x => Math.max(0, x - 1));
        setHistoryStats(x => {
            const healthResultNum = Number(healthResult);
            const repairActionNum = Number(repairAction);

            // attempt to find and update a matching statistic
            let updated = false;
            const newStats = x.map(stat => {
                if (stat.result === healthResultNum && stat.repairStatus === repairActionNum) {
                    updated = true;
                    return { ...stat, count: stat.count + 1 };
                }
                return stat;
            });

            // if no statistic was updated, add a new one
            if (!updated) {
                return [
                    ...x,
                    {
                        result: healthResultNum,
                        repairStatus: repairActionNum,
                        count: 1
                    }
                ];
            }

            // if an update occurred, return the modified array
            return newStats;
        });
    }, [setQueueItems, setHistoryStats]);

    const onHealthItemProgress = useCallback((message: string) => {
        const [davItemId, progress] = message.split('|');
        if (progress === "done") return;
        setQueueItems(queueItems => {
            var index = queueItems.findIndex(x => x.id === davItemId);
            if (index === -1) return queueItems;
            return queueItems
                .filter((_, i) => i >= index)
                .map(item => item.id === davItemId
                    ? { ...item, progress: Number(progress) }
                    : item
                )
        });
    }, [setQueueItems]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress
    ]);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(getWebsocketUrl());
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => {
                setWebsocketState("connected");
                ws.send(JSON.stringify(topicSubscriptions));
            }
            ws.onclose = () => {
                if (disposed) return;
                setWebsocketState("disconnected");
                setTimeout(() => connect(), 1000);
            };
            ws.onerror = () => {
                setWebsocketState("disconnected");
                ws.close()
            };
            return () => { disposed = true; ws.close(); }
        }

        return connect();
    }, [onWebsocketMessage]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <OperationsStatus
                    fullStatus={loaderData.fullStatus}
                    fullStatusError={loaderData.fullStatusError}
                    repairStatus={loaderData.repairStatus}
                    repairStatusError={loaderData.repairStatusError}
                    arrValidation={loaderData.arrValidation}
                    arrValidationError={loaderData.arrValidationError}
                    arrNudges={loaderData.arrNudges}
                    arrNudgesError={loaderData.arrNudgesError}
                    arrCorrelations={loaderData.arrCorrelations}
                    arrCorrelationsError={loaderData.arrCorrelationsError}
                    websocketState={websocketState}
                    isActionSubmitting={isActionSubmitting}
                />
            </div>
            <div className={styles.section}>
                <HealthStats stats={historyStats} />
            </div>
            {isEnabled && uncheckedCount > 20 &&
                <Alert className={styles.alert} variant={'warning'}>
                    <b>Attention</b>
                    <ul className={styles.list}>
                        <li className={styles.listItem}>
                            You have ~{uncheckedCount} files whose health has never been determined.
                        </li>
                        <li className={styles.listItem}>
                            The queue will run an initial health check of these files.
                        </li>
                        <li className={styles.listItem}>
                            Under normal operation, health checks will occur much less frequently.
                        </li>
                    </ul>
                </Alert>
            }
            <div className={styles.section}>
                <HealthTable isEnabled={isEnabled} healthCheckItems={queueItems.filter((_, index) => index < 10)} />
            </div>
        </div>
    );
}

function emptyToUndefined(value: string | undefined) {
    return value && value.trim() ? value.trim() : undefined;
}

function numberOrUndefined(value: string | undefined) {
    if (!value || !value.trim()) return undefined;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
}

async function loadOptional<T>(load: () => Promise<T>): Promise<{ data: T | null; error: string | null }> {
    try {
        return { data: await load(), error: null };
    } catch (error) {
        return {
            data: null,
            error: error instanceof Error ? error.message : String(error)
        };
    }
}
