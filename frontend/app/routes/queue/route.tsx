import { useLocation, useNavigate } from "react-router";
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

const pageSize = 50;
export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const queuePage = getPageNumber(url.searchParams.get("queuePage"));
    const historyPage = getPageNumber(url.searchParams.get("historyPage"));
    const queuePromise = backendClient.getQueue((queuePage - 1) * pageSize, pageSize);
    const historyPromise = backendClient.getHistory((historyPage - 1) * pageSize, pageSize);
    const configPromise = backendClient.getConfig(["api.categories", "api.manual-category"])
    const queue = await queuePromise;
    const history = await historyPromise;
    const config = await configPromise;
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
        queuePage: queuePage,
        historyPage: historyPage,
        pageSize: pageSize,
        categories: categories,
        manualCategory: manualCategory,
    }
}

export default function Queue(props: Route.ComponentProps) {
    const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(props.loaderData.queueSlots);
    const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(props.loaderData.historySlots);
    const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
    const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
    const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
    const uploadQueueRef = useRef<UploadingFile[]>([]);
    const manualCategoryRef = useRef<string>(props.loaderData.manualCategory);
    const isUploadingRef = useRef(false);
    const combinedQueueSlots = [...uploadingFiles.map(file => file.queueSlot), ...queueSlots];
    const navigate = useNavigate();
    const location = useLocation();

    useEffect(() => {
        setQueueSlots(props.loaderData.queueSlots);
        setHistorySlots(props.loaderData.historySlots);
        setTotalQueueCount(props.loaderData.totalQueueCount);
        setTotalHistoryCount(props.loaderData.totalHistoryCount);
    }, [
        props.loaderData.queuePage,
        props.loaderData.historyPage,
        props.loaderData.queueSlots,
        props.loaderData.historySlots,
        props.loaderData.totalQueueCount,
        props.loaderData.totalHistoryCount,
    ]);

    const onPageSelected = useCallback((paramName: "queuePage" | "historyPage", page: number) => {
        const params = new URLSearchParams(location.search);
        if (page <= 1) params.delete(paramName);
        else params.set(paramName, String(page));
        navigate(`${location.pathname}${params.size > 0 ? `?${params.toString()}` : ""}`);
    }, [location.pathname, location.search, navigate]);

    // queue/history events
    const queueEvents = useQueueEvents(
        setUploadingFiles,
        setQueueSlots,
        setTotalQueueCount,
        uploadQueueRef,
        props.loaderData.queuePage,
        props.loaderData.pageSize);
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
                        totalQueueCount={totalQueueCount + uploadingFiles.length}
                        pageNumber={props.loaderData.queuePage}
                        pageSize={props.loaderData.pageSize}
                        categories={props.loaderData.categories}
                        manualCategoryRef={manualCategoryRef}
                        onIsSelectedChanged={queueEvents.onSelectQueueSlots}
                        onIsRemovingChanged={queueEvents.onRemovingQueueSlots}
                        onRemoved={queueEvents.onRemoveQueueSlots}
                        onPriorityChanged={queueEvents.onChangeQueueSlotPriority}
                        onUploadClicked={dropzone.open}
                        onPageSelected={page => onPageSelected("queuePage", page)}
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
                    onIsSelectedChanged={historyEvents.onSelectHistorySlots}
                    onIsRemovingChanged={historyEvents.onRemovingHistorySlots}
                    onRemoved={historyEvents.onRemoveHistorySlots}
                    onPageSelected={page => onPageSelected("historyPage", page)}
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
