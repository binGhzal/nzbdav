import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { useCallback, useEffect, useRef, useState } from "react";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { Alert } from "react-bootstrap";
import { getWebsocketUrl } from "~/utils/url-base";
import { useNavigation, useRevalidator } from "react-router";
import { OperationsStatus } from "./components/operations-status/operations-status";
import { useHealthQueueTopUp } from "./health-queue-top-up";
import { createHealthRefreshController } from "./health-refresh-controller";

const healthRefreshIntervalMs = 5_000;

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
}

export async function loader({ request }: Route.LoaderArgs) {
    const enabledKey = 'repair.enable';
    const url = new URL(request.url);
    const arrFilters = {
        app: emptyToUndefined(url.searchParams.get("arr_app") ?? undefined),
        status: emptyToUndefined(url.searchParams.get("arr_status") ?? undefined),
        mode: emptyToUndefined(url.searchParams.get("arr_mode") ?? undefined),
        command: emptyToUndefined(url.searchParams.get("arr_command") ?? undefined),
        search: emptyToUndefined(url.searchParams.get("arr_search") ?? undefined),
    };
    const [queueData, historyData, config, repairStatus, fullStatus, arrValidation, arrNudges, arrCorrelations] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey]),
        loadOptional(() => backendClient.getRepairStatus()),
        loadOptional(() => backendClient.getFullStatus()),
        loadOptional(() => backendClient.getArrValidation()),
        loadOptional(() => backendClient.getArrSearchNudges({ limit: 25, ...arrFilters })),
        loadOptional(() => backendClient.getArrCorrelations({ limit: 25 }))
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
        arrFilters,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export async function action({ request }: Route.ActionArgs) {
    try {
        return await performHealthAction(request);
    } catch (error) {
        return Response.json({ error: getErrorMessage(error) }, { status: 502 });
    }
}

async function performHealthAction(request: Request) {
    const formData = await request.formData();
    const intent = formData.get("intent")?.toString();

    if (intent === "start") {
        await backendClient.startRepairRun();
        return { ok: true };
    }

    if (intent === "cancel") {
        const runId = formData.get("runId")?.toString();
        if (!runId) return badRequest("Repair run id is required.");
        await backendClient.cancelRepairRun(runId);
        return { ok: true };
    }

    if (intent === "clear") {
        await backendClient.clearRepairRuns();
        return { ok: true };
    }

    if (intent === "retry-arr-nudge") {
        const id = formData.get("id")?.toString();
        if (!id) return badRequest("ARR search nudge id is required.");
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
            artist_id: numberOrUndefined(formData.get("artist_id")?.toString()),
            album_id: numberOrUndefined(formData.get("album_id")?.toString()),
            release_title: emptyToUndefined(formData.get("release_title")?.toString()),
            category: emptyToUndefined(formData.get("category")?.toString()),
            quality: emptyToUndefined(formData.get("quality")?.toString()),
            manual_lock: formData.get("manual_lock") === "on",
            is_upgrade: formData.get("is_upgrade") === "on",
            is_duplicate: formData.get("is_duplicate") === "on"
        });
        return { ok: true };
    }

    if (intent === "delete-arr-correlation") {
        const id = formData.get("id")?.toString();
        if (!id) return badRequest("ARR correlation id is required.");
        await backendClient.deleteArrCorrelation(id);
        return { ok: true };
    }

    return badRequest("Unsupported repair action.");
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueItems, setQueueItems] = useState(loaderData.queueItems);
    const [uncheckedCount, setUncheckedCount] = useState(loaderData.uncheckedCount);
    const [websocketState, setWebsocketState] = useState<"connecting" | "connected" | "disconnected">("connecting");
    const navigation = useNavigation();
    const revalidator = useRevalidator();
    const navigationRef = useRef(navigation);
    const revalidatorRef = useRef(revalidator);
    const isActionSubmitting = navigation.state !== "idle" && navigation.formMethod?.toLowerCase() === "post";

    // effects
    useHealthQueueTopUp(queueItems, setQueueItems, setUncheckedCount);

    useEffect(() => {
        setHistoryStats(loaderData.historyStats);
        setQueueItems(loaderData.queueItems);
        setUncheckedCount(loaderData.uncheckedCount);
    }, [loaderData.historyStats, loaderData.queueItems, loaderData.uncheckedCount]);

    useEffect(() => {
        navigationRef.current = navigation;
        revalidatorRef.current = revalidator;
    }, [navigation, revalidator]);

    useEffect(() => {
        const refreshController = createHealthRefreshController({
            getVisibility: () => document.visibilityState,
            canRevalidate: () =>
                navigationRef.current.state === "idle"
                && revalidatorRef.current.state === "idle",
            revalidate: () => revalidatorRef.current.revalidate(),
            intervalMs: healthRefreshIntervalMs,
        });
        const onVisibilityChange = () => refreshController.visibilityChanged();

        refreshController.start();
        document.addEventListener("visibilitychange", onVisibilityChange);
        return () => {
            document.removeEventListener("visibilitychange", onVisibilityChange);
            refreshController.dispose();
        };
    }, []);

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
        return createReconnectingWebSocket({
            createSocket: () => new WebSocket(getWebsocketUrl()),
            onMessage: onWebsocketMessage,
            onOpen: socket => {
                setWebsocketState("connected");
                socket.send(JSON.stringify(topicSubscriptions));
            },
            onClose: () => setWebsocketState("disconnected"),
            onError: () => setWebsocketState("disconnected"),
        });
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
                    arrFilters={loaderData.arrFilters}
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

function badRequest(error: string) {
    return Response.json({ error }, { status: 400 });
}

function getErrorMessage(error: unknown) {
    return error instanceof Error ? error.message : String(error);
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
