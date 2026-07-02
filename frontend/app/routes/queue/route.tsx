import { useLocation, useNavigate, useRevalidator } from "react-router";
import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { backendClient, type HistorySlot, type QueueSlot } from "~/clients/backend-client.server";
import { HistoryTable } from "./components/history-table/history-table";
import { QueueTable } from "./components/queue-table/queue-table";
import { useCallback, useEffect, useState, useRef } from "react";
import { useHistoryEvents, useQueueEvents } from "./controllers/events-controller";
import { initializeQueueHistoryWebsocket } from "./controllers/websocket-controller";
import { initializeUploadController } from "./controllers/nzb-upload-controller";
import { useQueueDropzone } from "./controllers/dropzone-controller";

const pageSizeOptions = [25, 50, 100, 200];
const defaultPageSize = 50;
export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const queuePage = getPageNumber(url.searchParams.get("queuePage"));
    const historyPage = getPageNumber(url.searchParams.get("historyPage"));
    const pageSize = getPageSize(url.searchParams.get("pageSize"));
    const queueStatus = getQueueStatus(url.searchParams.get("queueStatus"));
    const queuePromise = backendClient.getQueue({
        start: (queuePage - 1) * pageSize,
        limit: pageSize,
        status: queueStatus,
    });
    const historyPromise = backendClient.getHistory((historyPage - 1) * pageSize, pageSize);
    const configPromise = backendClient.getConfig(["api.categories", "api.manual-category"])
    const [queue, history, config] = await Promise.all([queuePromise, historyPromise, configPromise]);
    const categoriesValue = config
        .find(x => x.configName === "api.categories")
        ?.configValue ?? "uncategorized,audio,software,tv,movies";
    const manualCategory = config
        .find(x => x.configName === "api.manual-category")
        ?.configValue ?? "uncategorized";
    let categories = categoriesValue.split(',').map(x => x.trim());
    if (!categories.includes(manualCategory)) {
        categories = [manualCategory, ...categories];
    }

    return {
        queueSlots: queue?.slots || [],
        historySlots: history?.slots || [],
        totalQueueCount: queue?.noofslots || 0,
        totalHistoryCount: history?.noofslots || 0,
        queueStatus: queueStatus,
        queuePaused: queue?.paused || false,
        queueStatusText: queue?.status || "Idle",
        queuePage: queuePage,
        historyPage: historyPage,
        pageSize: pageSize,
        pageSizeOptions: pageSizeOptions,
        categories: categories,
        manualCategory: manualCategory,
    }
}

export default function Queue(props: Route.ComponentProps) {
    const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(props.loaderData.queueSlots);
    const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(props.loaderData.historySlots);
    const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
    const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
    const [queuePaused, setQueuePaused] = useState(props.loaderData.queuePaused);
    const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
    const uploadQueueRef = useRef<UploadingFile[]>([]);
    const manualCategoryRef = useRef<string>(props.loaderData.manualCategory);
    const isUploadingRef = useRef(false);
    const combinedQueueSlots = [
        ...(props.loaderData.queueStatus === "all" ? uploadingFiles.map(file => file.queueSlot) : []),
        ...queueSlots
    ];
    const navigate = useNavigate();
    const location = useLocation();
    const revalidator = useRevalidator();
    const queueRefreshTimeoutRef = useRef<number | undefined>(undefined);

    useEffect(() => {
        setQueueSlots(props.loaderData.queueSlots);
        setHistorySlots(props.loaderData.historySlots);
        setTotalQueueCount(props.loaderData.totalQueueCount);
        setTotalHistoryCount(props.loaderData.totalHistoryCount);
        setQueuePaused(props.loaderData.queuePaused);
    }, [
        props.loaderData.queuePage,
        props.loaderData.historyPage,
        props.loaderData.queueStatus,
        props.loaderData.pageSize,
        props.loaderData.queueSlots,
        props.loaderData.historySlots,
        props.loaderData.totalQueueCount,
        props.loaderData.totalHistoryCount,
        props.loaderData.queuePaused,
    ]);

    const updateSearchParams = useCallback((updates: Record<string, string | null>) => {
        const params = new URLSearchParams(location.search);
        for (const [name, value] of Object.entries(updates)) {
            if (!value) params.delete(name);
            else params.set(name, value);
        }
        navigate(`${location.pathname}${params.size > 0 ? `?${params.toString()}` : ""}`);
    }, [location.pathname, location.search, navigate]);

    const onPageSelected = useCallback((paramName: "queuePage" | "historyPage", page: number) => {
        updateSearchParams({ [paramName]: page <= 1 ? null : String(page) });
    }, [updateSearchParams]);

    const onPageSizeSelected = useCallback((pageSize: number) => {
        updateSearchParams({
            pageSize: pageSize === defaultPageSize ? null : String(pageSize),
            queuePage: null,
            historyPage: null,
        });
    }, [updateSearchParams]);

    const onQueueStatusSelected = useCallback((queueStatus: QueueStatusFilter) => {
        updateSearchParams({
            queueStatus: queueStatus === "all" ? null : queueStatus,
            queuePage: null,
        });
    }, [updateSearchParams]);

    const requestQueueRefresh = useCallback(() => {
        window.clearTimeout(queueRefreshTimeoutRef.current);
        queueRefreshTimeoutRef.current = window.setTimeout(() => revalidator.revalidate(), 350);
    }, [revalidator]);

    useEffect(() => () => window.clearTimeout(queueRefreshTimeoutRef.current), []);

    // queue/history events
    const queueEvents = useQueueEvents(
        setUploadingFiles,
        setQueueSlots,
        setTotalQueueCount,
        uploadQueueRef,
        props.loaderData.queuePage,
        props.loaderData.pageSize,
        props.loaderData.queueStatus,
        requestQueueRefresh);
    const historyEvents = useHistoryEvents(
        setHistorySlots,
        setTotalHistoryCount,
        props.loaderData.historyPage,
        props.loaderData.pageSize);

    // websocket
    initializeQueueHistoryWebsocket(queueEvents, historyEvents);

    // uploads
    const dropzone = useQueueDropzone(setUploadingFiles, uploadQueueRef, manualCategoryRef);
    initializeUploadController(isUploadingRef, uploadQueueRef, uploadingFiles, setUploadingFiles);

    // view
    return (
        <div className={styles.container}>

            {/* queue */}
            <div className={styles.queueContainer}>
                <div className={styles.dropzone} {...dropzone.getRootProps()}>
                    {dropzone.isDragActive && <div className={styles.activeDropzone} />}
                    <input {...dropzone.getInputProps()} />
                    <QueueTable
                        queueSlots={combinedQueueSlots}
                        totalQueueCount={totalQueueCount + (props.loaderData.queueStatus === "all" ? uploadingFiles.length : 0)}
                        queueStatusFilter={props.loaderData.queueStatus}
                        isQueuePaused={queuePaused}
                        queueStatusText={props.loaderData.queueStatusText}
                        pageNumber={props.loaderData.queuePage}
                        pageSize={props.loaderData.pageSize}
                        pageSizeOptions={props.loaderData.pageSizeOptions}
                        categories={props.loaderData.categories}
                        manualCategoryRef={manualCategoryRef}
                        onIsSelectedChanged={queueEvents.onSelectQueueSlots}
                        onIsRemovingChanged={queueEvents.onRemovingQueueSlots}
                        onRemoved={queueEvents.onRemoveQueueSlots}
                        onPriorityChanged={queueEvents.onChangeQueueSlotPriority}
                        onUploadClicked={dropzone.open}
                        onQueueStatusSelected={onQueueStatusSelected}
                        onPauseQueueChanged={setQueuePaused}
                        onPageSelected={page => onPageSelected("queuePage", page)}
                        onPageSizeSelected={onPageSizeSelected}
                    />
                </div>
            </div>

            {/* history */}
            {totalHistoryCount > 0 &&
                <HistoryTable
                    historySlots={historySlots}
                    totalHistoryCount={totalHistoryCount}
                    pageNumber={props.loaderData.historyPage}
                    pageSize={props.loaderData.pageSize}
                    pageSizeOptions={props.loaderData.pageSizeOptions}
                    onIsSelectedChanged={historyEvents.onSelectHistorySlots}
                    onIsRemovingChanged={historyEvents.onRemovingHistorySlots}
                    onRemoved={historyEvents.onRemoveHistorySlots}
                    onPageSelected={page => onPageSelected("historyPage", page)}
                    onPageSizeSelected={onPageSizeSelected}
                />
            }
        </div >
    );
}

export type PresentationHistorySlot = HistorySlot & {
    isSelected?: boolean,
    isRemoving?: boolean,
}

export type PresentationQueueSlot = QueueSlot & {
    isUploading?: boolean,
    isSelected?: boolean,
    isRemoving?: boolean,
    error?: string,
}

export type UploadingFile = {
    file: File,
    queueSlot: PresentationQueueSlot,
}

function getPageNumber(value: string | null): number {
    if (value === null) return 1;
    const parsed = Number(value);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : 1;
}

function getPageSize(value: string | null): number {
    const parsed = Number(value);
    return pageSizeOptions.includes(parsed) ? parsed : defaultPageSize;
}

function getQueueStatus(value: string | null): QueueStatusFilter {
    if (value === "downloading" || value === "queued" || value === "paused") return value;
    return "all";
}

export type QueueStatusFilter = "all" | "downloading" | "queued" | "paused";
