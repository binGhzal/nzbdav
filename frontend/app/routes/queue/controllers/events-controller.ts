import { useCallback, useMemo } from "react";
import type { HistorySlot, QueueSlot } from "~/clients/backend-client.server";
import type { PresentationHistorySlot, PresentationQueueSlot, UploadingFile } from "../route";

export type QueueEvents = {
    onAddQueueSlot: (queueSlot: QueueSlot) => void,
    onSelectQueueSlots: (ids: Set<string>, isSelected: boolean) => void,
    onRemovingQueueSlots: (ids: Set<string>, isRemoving: boolean) => void,
    onRemoveQueueSlots: (ids: Set<string>) => void,
    onChangeQueueSlotPriority: (id: string, priority: string) => void,
    onChangeQueueSlotStatus: (message: string) => void,
    onChangeQueueSlotPercentage: (message: string) => void
};

export type HistoryEvents = {
    onAddHistorySlot: (historySlot: HistorySlot) => void,
    onSelectHistorySlots: (ids: Set<string>, isSelected: boolean) => void,
    onRemovingHistorySlots: (ids: Set<string>, isRemoving: boolean) => void,
    onRemoveHistorySlots: (ids: Set<string>) => void
};

export function useQueueEvents(
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
    setQueueSlots: (value: React.SetStateAction<PresentationQueueSlot[]>) => void,
    setTotalQueueCount: (value: React.SetStateAction<number>) => void,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    pageNumber: number,
    pageSize: number
) {
    const onAddQueueSlot = useCallback((queueSlot: QueueSlot) => {
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || x.queueSlot.filename !== queueSlot.filename);
        setUploadingFiles(files => files.filter(f => f.queueSlot.filename !== queueSlot.filename));
        setTotalQueueCount(count => count + 1);
        if (pageNumber === 1) {
            setQueueSlots(slots => sortQueueSlots([...slots, queueSlot]).slice(0, pageSize));
        }
    }, [pageNumber, pageSize, setQueueSlots, setTotalQueueCount]);

    const onSelectQueueSlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setUploadingFiles(files => files.map(x => ids.has(x.queueSlot.nzo_id) ? { ...x, queueSlot: { ...x.queueSlot, isSelected } } : x));
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setQueueSlots]);

    const onRemovingQueueSlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setQueueSlots]);

    const onRemoveQueueSlots = useCallback((ids: Set<string>) => {
        const uploadingIds = new Set(uploadQueueRef.current
            .filter(x => x.queueSlot.status === "uploading")
            .map(x => x.queueSlot.nzo_id));
        const queuedIds = new Set([...ids].filter(id => !uploadingIds.has(id)));
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id));
        setUploadingFiles(files => files.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id)));
        if (queuedIds.size > 0) {
            setTotalQueueCount(count => Math.max(0, count - queuedIds.size));
        }
        setQueueSlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setQueueSlots, setTotalQueueCount]);

    const onChangeQueueSlotPriority = useCallback((id: string, priority: string) => {
        setQueueSlots(slots => sortQueueSlots(
            slots.map(x => x.nzo_id === id ? { ...x, priority } : x)
        ));
    }, [setQueueSlots]);

    const onChangeQueueSlotStatus = useCallback((message: string) => {
        const [nzo_id, status] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, status } : x));
    }, [setQueueSlots]);

    const onChangeQueueSlotPercentage = useCallback((message: string) => {
        const [nzo_id, true_percentage] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, true_percentage } : x));
    }, [setQueueSlots]);

    return memoize({
        onAddQueueSlot,
        onSelectQueueSlots,
        onRemovingQueueSlots,
        onRemoveQueueSlots,
        onChangeQueueSlotPriority,
        onChangeQueueSlotStatus,
        onChangeQueueSlotPercentage
    });
}

export function useHistoryEvents(
    setHistorySlots: (value: React.SetStateAction<PresentationHistorySlot[]>) => void,
    setTotalHistoryCount: (value: React.SetStateAction<number>) => void,
    pageNumber: number,
    pageSize: number
) {
    const onAddHistorySlot = useCallback((historySlot: HistorySlot) => {
        setTotalHistoryCount(count => count + 1);
        if (pageNumber === 1) {
            setHistorySlots(slots => [historySlot, ...slots].slice(0, pageSize));
        }
    }, [pageNumber, pageSize, setHistorySlots, setTotalHistoryCount]);

    const onSelectHistorySlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setHistorySlots]);

    const onRemovingHistorySlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setHistorySlots]);

    const onRemoveHistorySlots = useCallback((ids: Set<string>) => {
        setTotalHistoryCount(count => Math.max(0, count - ids.size));
        setHistorySlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setHistorySlots, setTotalHistoryCount]);

    return memoize({
        onAddHistorySlot,
        onSelectHistorySlots,
        onRemovingHistorySlots,
        onRemoveHistorySlots
    });
}

function memoize<T extends Record<string, unknown>>(object: T): T {
    // eslint-disable-next-line react-hooks/exhaustive-deps
    return useMemo(() => object, Object.values(object));
}

const priorityWeights: Record<string, number> = {
    Force: 2,
    High: 1,
    Normal: 0,
    Low: -1,
    Paused: -2,
    Duplicate: -3,
    Default: -100
};

function sortQueueSlots(slots: PresentationQueueSlot[]): PresentationQueueSlot[] {
    return [...slots].sort((a, b) =>
        (priorityWeights[b.priority] ?? 0) - (priorityWeights[a.priority] ?? 0)
    );
}
