import { useCallback, useEffect, useMemo, useRef } from "react";
import type { HistorySlot, QueueSlot, QueueSortField, QueueStatusFilter } from "~/clients/backend-client.server";
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
    pageSize: number,
    queueStatusFilter: QueueStatusFilter,
    queueSort: QueueSortField,
    onQueueNeedsRefresh?: () => void
) {
    const pendingPercentageUpdatesRef = useRef<Map<string, string>>(new Map());
    const percentageFlushTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

    const flushPercentageUpdates = useCallback(() => {
        percentageFlushTimerRef.current = undefined;
        if (pendingPercentageUpdatesRef.current.size === 0) return;

        const updates = pendingPercentageUpdatesRef.current;
        pendingPercentageUpdatesRef.current = new Map();
        setQueueSlots(slots => applyQueueSlotPercentageChanges(slots, updates));
    }, [setQueueSlots]);

    useEffect(() => () => {
        if (percentageFlushTimerRef.current !== undefined)
            clearTimeout(percentageFlushTimerRef.current);
        percentageFlushTimerRef.current = undefined;
        pendingPercentageUpdatesRef.current.clear();
    }, []);

    const onAddQueueSlot = useCallback((queueSlot: QueueSlot) => {
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || x.queueSlot.filename !== queueSlot.filename);
        setUploadingFiles(files => files.filter(f => f.queueSlot.filename !== queueSlot.filename));

        setQueueSlots(slots => {
            const result = applyQueueSlotAdd(slots, queueSlot, {
                pageNumber,
                pageSize,
                queueStatusFilter,
                queueSort,
            });
            if (result.totalCountDelta !== 0)
                setTotalQueueCount(count => Math.max(0, count + result.totalCountDelta));
            if (result.needsRefresh)
                onQueueNeedsRefresh?.();
            return result.slots;
        });
    }, [pageNumber, pageSize, queueStatusFilter, queueSort, setQueueSlots, setTotalQueueCount, onQueueNeedsRefresh]);

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
        setQueueSlots(slots => {
            const result = applyQueueSlotRemoval(slots, ids, uploadingIds);
            if (result.removedVisibleQueuedCount > 0) {
                setTotalQueueCount(count => Math.max(0, count - result.removedVisibleQueuedCount));
            }
            if (result.needsRefresh) {
                onQueueNeedsRefresh?.();
            }
            return result.slots;
        });
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id));
        setUploadingFiles(files => files.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id)));
    }, [setQueueSlots, setTotalQueueCount, onQueueNeedsRefresh]);

    const onChangeQueueSlotPriority = useCallback((id: string, priority: string) => {
        setQueueSlots(slots => {
            const result = applyQueueSlotPriorityChange(slots, id, priority, queueStatusFilter, queueSort);
            if (result.removedVisibleSlot)
                setTotalQueueCount(count => Math.max(0, count - 1));
            if (result.removedVisibleSlot || result.needsRefresh)
                onQueueNeedsRefresh?.();
            return result.slots;
        });
    }, [queueStatusFilter, queueSort, setQueueSlots, setTotalQueueCount, onQueueNeedsRefresh]);

    const onChangeQueueSlotStatus = useCallback((message: string) => {
        setQueueSlots(slots => {
            const result = applyQueueSlotStatusChange(slots, message, queueStatusFilter, queueSort);
            if (result.removedVisibleSlot || result.needsRefresh) {
                if (result.removedVisibleSlot)
                    setTotalQueueCount(count => Math.max(0, count - 1));
                onQueueNeedsRefresh?.();
            }
            return result.slots;
        });
    }, [queueStatusFilter, queueSort, setQueueSlots, setTotalQueueCount, onQueueNeedsRefresh]);

    const onChangeQueueSlotPercentage = useCallback((message: string) => {
        const parsed = parseQueueSlotPercentageMessage(message);
        if (!parsed) return;

        pendingPercentageUpdatesRef.current.set(parsed.nzo_id, parsed.true_percentage);
        if (percentageFlushTimerRef.current !== undefined) return;

        percentageFlushTimerRef.current = setTimeout(flushPercentageUpdates, 50);
    }, [flushPercentageUpdates]);

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
    pageSize: number,
    onHistoryNeedsRefresh?: () => void
) {
    const onAddHistorySlot = useCallback((historySlot: HistorySlot) => {
        setTotalHistoryCount(count => count + 1);
        if (pageNumber === 1) {
            setHistorySlots(slots => [historySlot, ...slots].slice(0, pageSize));
        } else {
            onHistoryNeedsRefresh?.();
        }
    }, [pageNumber, pageSize, setHistorySlots, setTotalHistoryCount, onHistoryNeedsRefresh]);

    const onSelectHistorySlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setHistorySlots]);

    const onRemovingHistorySlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setHistorySlots]);

    const onRemoveHistorySlots = useCallback((ids: Set<string>) => {
        setTotalHistoryCount(count => Math.max(0, count - ids.size));
        setHistorySlots(slots => {
            const nextSlots = slots.filter(x => !ids.has(x.nzo_id));
            if (nextSlots.length !== slots.length || pageNumber > 1) {
                onHistoryNeedsRefresh?.();
            }
            return nextSlots;
        });
    }, [pageNumber, setHistorySlots, setTotalHistoryCount, onHistoryNeedsRefresh]);

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
    return [...slots].sort((a, b) => {
        const priorityDelta = getEffectivePriorityWeight(b) - getEffectivePriorityWeight(a);
        if (priorityDelta !== 0) return priorityDelta;
        return getArrPriorityScore(b) - getArrPriorityScore(a);
    });
}

function getEffectivePriorityWeight(slot: PresentationQueueSlot): number {
    const manual = priorityWeights[slot.priority] ?? 0;
    const hint = slot.arr_priority;
    if (!hint?.apply_to_scheduling) return manual;
    return Math.max(manual, priorityWeights[hint.effective_priority] ?? manual);
}

function getArrPriorityScore(slot: PresentationQueueSlot): number {
    return slot.arr_priority?.apply_to_scheduling ? slot.arr_priority.score : 0;
}

export type QueueSlotAddOptions = {
    pageNumber: number;
    pageSize: number;
    queueStatusFilter: QueueStatusFilter;
    queueSort: QueueSortField;
};

export type QueueSlotAddResult = {
    slots: PresentationQueueSlot[];
    totalCountDelta: number;
    needsRefresh: boolean;
};

export function applyQueueSlotAdd(
    slots: PresentationQueueSlot[],
    queueSlot: QueueSlot,
    options: QueueSlotAddOptions
): QueueSlotAddResult {
    if (!matchesQueueStatus(queueSlot, options.queueStatusFilter)) {
        return { slots, totalCountDelta: 0, needsRefresh: false };
    }

    const existingIndex = slots.findIndex(slot => slot.nzo_id === queueSlot.nzo_id);
    const totalCountDelta = existingIndex >= 0 ? 0 : 1;
    if (existingIndex >= 0) {
        const nextSlots = slots.slice();
        nextSlots[existingIndex] = queueSlot;
        return {
            slots: options.queueSort === "priority"
                ? sortQueueSlots(nextSlots).slice(0, options.pageSize)
                : nextSlots,
            totalCountDelta,
            needsRefresh: false,
        };
    }

    if (options.pageNumber !== 1) {
        return { slots, totalCountDelta, needsRefresh: true };
    }

    const nextSlots = [...slots, queueSlot];
    return {
        slots: sortQueueSlots(nextSlots).slice(0, options.pageSize),
        totalCountDelta,
        needsRefresh: options.queueSort !== "priority",
    };
}

export type QueueSlotStatusChangeResult = {
    slots: PresentationQueueSlot[];
    removedVisibleSlot: boolean;
    changedVisibleSlot: boolean;
    needsRefresh: boolean;
};

export type QueueSlotPriorityChangeResult = {
    slots: PresentationQueueSlot[];
    removedVisibleSlot: boolean;
    changedVisibleSlot: boolean;
    needsRefresh: boolean;
};

export type QueueSlotRemovalResult = {
    slots: PresentationQueueSlot[];
    queuedIds: Set<string>;
    removedVisibleQueuedCount: number;
    needsRefresh: boolean;
};

export function applyQueueSlotRemoval(
    slots: PresentationQueueSlot[],
    ids: Set<string>,
    uploadingIds: Set<string>
): QueueSlotRemovalResult {
    const queuedIds = new Set([...ids].filter(id => !uploadingIds.has(id)));
    let removedVisibleQueuedCount = 0;
    const nextSlots = slots.filter(slot => {
        if (!ids.has(slot.nzo_id)) return true;
        if (queuedIds.has(slot.nzo_id)) removedVisibleQueuedCount++;
        return false;
    });

    return {
        slots: nextSlots,
        queuedIds,
        removedVisibleQueuedCount,
        needsRefresh: queuedIds.size > 0,
    };
}

export function applyQueueSlotPriorityChange(
    slots: PresentationQueueSlot[],
    id: string,
    priority: string,
    queueStatusFilter: QueueStatusFilter,
    queueSort: QueueSortField
): QueueSlotPriorityChangeResult {
    if (!id || priority === undefined) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh: false };
    }

    const index = slots.findIndex(slot => slot.nzo_id === id);
    if (index < 0) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh: true };
    }

    const slot = slots[index];
    const updatedSlot = slot.priority === priority ? slot : { ...slot, priority };
    const shouldKeep = matchesQueueStatus(updatedSlot, queueStatusFilter);
    if (!shouldKeep) {
        const nextSlots = slots.slice(0, index).concat(slots.slice(index + 1));
        return { slots: nextSlots, removedVisibleSlot: true, changedVisibleSlot: true, needsRefresh: false };
    }

    if (updatedSlot === slot) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh: false };
    }

    const nextSlots = slots.slice();
    nextSlots[index] = updatedSlot;
    return {
        slots: queueSort === "priority" ? sortQueueSlots(nextSlots) : nextSlots,
        removedVisibleSlot: false,
        changedVisibleSlot: true,
        needsRefresh: false
    };
}

export function applyQueueSlotStatusChange(
    slots: PresentationQueueSlot[],
    message: string,
    queueStatusFilter: QueueStatusFilter,
    queueSort: QueueSortField = "priority"
): QueueSlotStatusChangeResult {
    const [nzo_id, status] = message.split('|');
    if (!nzo_id || status === undefined) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh: false };
    }

    const index = slots.findIndex(slot => slot.nzo_id === nzo_id);
    const needsRefresh = index < 0
        && queueStatusFilter !== "all"
        && matchesQueueStatus({ status, priority: "Normal" }, queueStatusFilter);

    if (index < 0) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh };
    }

    const slot = slots[index];
    const updatedSlot = slot.status === status ? slot : { ...slot, status };
    const shouldKeep = matchesQueueStatus(updatedSlot, queueStatusFilter);
    if (!shouldKeep) {
        const nextSlots = slots.slice(0, index).concat(slots.slice(index + 1));
        return { slots: nextSlots, removedVisibleSlot: true, changedVisibleSlot: true, needsRefresh: false };
    }

    if (updatedSlot === slot) {
        return { slots, removedVisibleSlot: false, changedVisibleSlot: false, needsRefresh: false };
    }

    const nextSlots = slots.slice();
    nextSlots[index] = updatedSlot;
    return {
        slots: nextSlots,
        removedVisibleSlot: false,
        changedVisibleSlot: true,
        needsRefresh: queueSort === "status",
    };
}

export function applyQueueSlotPercentageChange(
    slots: PresentationQueueSlot[],
    message: string
): PresentationQueueSlot[] {
    const parsed = parseQueueSlotPercentageMessage(message);
    if (!parsed) return slots;

    return applyQueueSlotPercentageChanges(slots, new Map([[parsed.nzo_id, parsed.true_percentage]]));
}

export function applyQueueSlotPercentageChanges(
    slots: PresentationQueueSlot[],
    updates: ReadonlyMap<string, string>
): PresentationQueueSlot[] {
    if (updates.size === 0) return slots;

    let nextSlots: PresentationQueueSlot[] | undefined;
    for (let index = 0; index < slots.length; index++) {
        const slot = slots[index];
        const true_percentage = updates.get(slot.nzo_id);
        if (true_percentage === undefined || slot.true_percentage === true_percentage) continue;

        nextSlots ??= slots.slice();
        nextSlots[index] = { ...slot, true_percentage };
    }

    return nextSlots ?? slots;
}

function matchesQueueStatus(slot: Pick<QueueSlot, "status" | "priority">, filter: QueueStatusFilter): boolean {
    if (filter === "all") return true;

    const status = slot.status?.toLowerCase();
    const priority = slot.priority?.toLowerCase();
    if (filter === "paused") return status === "paused" || priority === "paused";
    if (filter === "downloading") return status === "downloading";
    if (filter === "verifying") return status === "verifying";
    if (filter === "repairing") return status === "repairing";
    if (filter === "moving") return status === "moving";
    if (filter === "queued") return status === "queued" && priority !== "paused";
    return true;
}

function parseQueueSlotPercentageMessage(message: string): { nzo_id: string, true_percentage: string } | null {
    const [nzo_id, true_percentage] = message.split('|');
    if (!nzo_id || true_percentage === undefined) return null;
    return { nzo_id, true_percentage };
}
