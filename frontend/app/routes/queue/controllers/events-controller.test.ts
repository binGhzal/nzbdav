import { act, renderHook } from "@testing-library/react";
import { useCallback, useRef, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
    applyQueueSlotAdd,
    applyQueueSlotPercentageChange,
    applyQueueSlotPriorityChange,
    applyQueueSlotRemoval,
    applyQueueSlotStatusChange,
    useHistoryEvents,
    useQueueEvents,
} from "./events-controller";
import type { PresentationHistorySlot, PresentationQueueSlot, UploadingFile } from "../route";
import type { QueueSortField, QueueStatusFilter } from "~/clients/backend-client.server";

describe("queue event reducers", () => {
    afterEach(() => {
        vi.useRealTimers();
    });

    it("keeps the same slot array for percentage events outside the visible page", () => {
        const slots = [queueSlot("visible", "Downloading", "10")];

        const next = applyQueueSlotPercentageChange(slots, "hidden|50");

        expect(next).toBe(slots);
    });

    it("keeps the same slot array for unchanged percentage events", () => {
        const slots = [queueSlot("visible", "Downloading", "10")];

        const next = applyQueueSlotPercentageChange(slots, "visible|10");

        expect(next).toBe(slots);
    });

    it("updates only the matching percentage slot", () => {
        const slots = [
            queueSlot("first", "Downloading", "10"),
            queueSlot("second", "Downloading", "20"),
        ];

        const next = applyQueueSlotPercentageChange(slots, "second|35");

        expect(next).not.toBe(slots);
        expect(next[0]).toBe(slots[0]);
        expect(next[1]).toEqual({ ...slots[1], true_percentage: "35" });
    });

    it("updates percentage without mapping every visible slot", () => {
        const slots = new ThrowingMapArray(
            queueSlot("first", "Downloading", "10"),
            queueSlot("second", "Downloading", "20"),
        ) as PresentationQueueSlot[];

        const next = applyQueueSlotPercentageChange(slots, "second|35");

        expect(next).not.toBe(slots);
        expect(next[0]).toBe(slots[0]);
        expect(next[1]).toEqual({ ...slots[1], true_percentage: "35" });
    });

    it("keeps status events outside the visible page as no-ops", () => {
        const slots = [queueSlot("visible", "Queued", "0")];

        const result = applyQueueSlotStatusChange(slots, "hidden|Downloading", "all");

        expect(result.slots).toBe(slots);
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(false);
        expect(result.needsRefresh).toBe(false);
    });

    it("requests refresh when a hidden item moves into the active status tab", () => {
        const slots = [queueSlot("visible", "Downloading", "10")];

        const result = applyQueueSlotStatusChange(slots, "hidden|Downloading", "downloading");

        expect(result.slots).toBe(slots);
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(false);
        expect(result.needsRefresh).toBe(true);
    });

    it("does not request refresh when a hidden status event still does not match the active tab", () => {
        const slots = [queueSlot("visible", "Downloading", "10")];

        const result = applyQueueSlotStatusChange(slots, "hidden|Queued", "downloading");

        expect(result.slots).toBe(slots);
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(false);
        expect(result.needsRefresh).toBe(false);
    });

    it("updates visible status without iterating the full page", () => {
        const slots = new ThrowingIteratorArray(
            queueSlot("first", "Queued", "0"),
            queueSlot("second", "Downloading", "20"),
        ) as PresentationQueueSlot[];

        const result = applyQueueSlotStatusChange(slots, "second|Verifying", "all");

        expect(result.slots).not.toBe(slots);
        expect(result.slots[0]).toBe(slots[0]);
        expect(result.slots[1]).toEqual({ ...slots[1], status: "Verifying" });
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(true);
        expect(result.needsRefresh).toBe(false);
    });

    it("requests refresh when a visible status change affects status-sorted order", () => {
        const slots = [
            queueSlot("first", "Queued", "0"),
            queueSlot("second", "Downloading", "20"),
        ];

        const result = applyQueueSlotStatusChange(slots, "second|Verifying", "all", "status");

        expect(result.slots[1]).toEqual({ ...slots[1], status: "Verifying" });
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(true);
        expect(result.needsRefresh).toBe(true);
    });

    it("updates visible priority without mapping every visible slot", () => {
        const slots = new ThrowingMapArray(
            queueSlot("first", "Queued", "0"),
            queueSlot("second", "Queued", "20"),
        ) as PresentationQueueSlot[];

        const result = applyQueueSlotPriorityChange(slots, "second", "High", "all", "name");

        expect(result.slots).not.toBe(slots);
        expect(result.slots[0]).toBe(slots[0]);
        expect(result.slots[1]).toEqual({ ...slots[1], priority: "High" });
        expect(result.removedVisibleSlot).toBe(false);
        expect(result.changedVisibleSlot).toBe(true);
        expect(result.needsRefresh).toBe(false);
    });

    it("removes visible priority changes that no longer match the active status tab", () => {
        const slots = [
            queueSlot("first", "Queued", "0"),
            queueSlot("second", "Queued", "20"),
        ];

        const result = applyQueueSlotPriorityChange(slots, "second", "Paused", "queued", "priority");

        expect(result.slots.map(slot => slot.nzo_id)).toEqual(["first"]);
        expect(result.removedVisibleSlot).toBe(true);
        expect(result.changedVisibleSlot).toBe(true);
        expect(result.needsRefresh).toBe(false);
    });

    it("replaces duplicate added queue slots without inflating the total count", () => {
        const slots = [queueSlot("duplicate", "Queued", "0")];
        const incoming = {
            ...queueSlot("duplicate", "Downloading", "25"),
            filename: "updated.nzb",
        };

        const result = applyQueueSlotAdd(slots, incoming, {
            pageNumber: 1,
            pageSize: 50,
            queueStatusFilter: "all",
            queueSort: "priority",
        });

        expect(result.slots).toHaveLength(1);
        expect(result.slots[0]).toEqual(incoming);
        expect(result.totalCountDelta).toBe(0);
        expect(result.needsRefresh).toBe(false);
    });

    it("updates visible duplicate queue slots on later pages without requesting a refresh", () => {
        const slots = [
            queueSlot("first", "Queued", "0"),
            queueSlot("duplicate", "Queued", "10"),
        ];
        const incoming = {
            ...queueSlot("duplicate", "Downloading", "45"),
            filename: "updated.nzb",
        };

        const result = applyQueueSlotAdd(slots, incoming, {
            pageNumber: 2,
            pageSize: 50,
            queueStatusFilter: "all",
            queueSort: "priority",
        });

        expect(result.slots).not.toBe(slots);
        expect(result.slots[0]).toBe(slots[0]);
        expect(result.slots[1]).toEqual(incoming);
        expect(result.totalCountDelta).toBe(0);
        expect(result.needsRefresh).toBe(false);
    });

    it("adds matching queue slots to the default-sorted first page without requesting a full refresh", () => {
        const slots = [queueSlot("visible", "Queued", "0")];
        const incoming = queueSlot("new", "Downloading", "10");

        const result = applyQueueSlotAdd(slots, incoming, {
            pageNumber: 1,
            pageSize: 50,
            queueStatusFilter: "all",
            queueSort: "priority",
        });

        expect(result.slots.map(slot => slot.nzo_id)).toContain("new");
        expect(result.totalCountDelta).toBe(1);
        expect(result.needsRefresh).toBe(false);
    });

    it("requests refresh for matching first-page adds when the active sort is server-defined", () => {
        const slots = [queueSlot("visible", "Queued", "0")];
        const incoming = queueSlot("new", "Downloading", "10");

        const result = applyQueueSlotAdd(slots, incoming, {
            pageNumber: 1,
            pageSize: 50,
            queueStatusFilter: "all",
            queueSort: "name",
        });

        expect(result.slots.map(slot => slot.nzo_id)).toContain("new");
        expect(result.totalCountDelta).toBe(1);
        expect(result.needsRefresh).toBe(true);
    });

    it("ignores added queue slots that do not match the active status tab", () => {
        const slots = [queueSlot("visible", "Downloading", "0")];
        const incoming = queueSlot("queued", "Queued", "0");

        const result = applyQueueSlotAdd(slots, incoming, {
            pageNumber: 1,
            pageSize: 50,
            queueStatusFilter: "downloading",
            queueSort: "priority",
        });

        expect(result.slots).toBe(slots);
        expect(result.totalCountDelta).toBe(0);
        expect(result.needsRefresh).toBe(false);
    });

    it("removes visible queue slots without counting uploading items against backend totals", () => {
        const slots = [
            queueSlot("queued", "Queued", "0"),
            { ...queueSlot("uploading", "uploading", "0"), isUploading: true },
            queueSlot("other", "Downloading", "10"),
        ];

        const result = applyQueueSlotRemoval(
            slots,
            new Set(["queued", "uploading", "hidden"]),
            new Set(["uploading"]));

        expect(result.slots.map(slot => slot.nzo_id)).toEqual(["other"]);
        expect(result.queuedIds).toEqual(new Set(["queued", "hidden"]));
        expect(result.removedVisibleQueuedCount).toBe(1);
        expect(result.needsRefresh).toBe(true);
    });

    it("uses the latest queue sort when handling websocket status events", () => {
        const { result, rerender } = renderHook(
            ({ queueSort }) => useQueueEventsHarness(queueSort),
            { initialProps: { queueSort: "priority" as QueueSortField } });

        rerender({ queueSort: "status" });

        act(() => result.current.events.onChangeQueueSlotStatus("second|Verifying"));

        expect(result.current.slots[1].status).toBe("Verifying");
        expect(result.current.refreshCount).toBe(1);
    });

    it("coalesces websocket percentage bursts into one queue state update", () => {
        vi.useFakeTimers();
        const { result } = renderHook(() => useQueueEventsHarness("priority"));

        act(() => {
            result.current.events.onChangeQueueSlotPercentage("first|10");
            result.current.events.onChangeQueueSlotPercentage("first|15");
            result.current.events.onChangeQueueSlotPercentage("second|35");
        });

        expect(result.current.slotUpdateCount).toBe(0);
        expect(result.current.slots.map(slot => slot.true_percentage)).toEqual(["0", "20"]);

        act(() => {
            vi.advanceTimersByTime(50);
        });

        expect(result.current.slotUpdateCount).toBe(1);
        expect(result.current.slots.map(slot => slot.true_percentage)).toEqual(["15", "35"]);
    });

    it("does not repeat queue-add count or refresh effects when React replays the slot updater", () => {
        const { result } = renderHook(() => useQueueEventsHarness(
            "name",
            { replayStateUpdaters: true }));

        act(() => result.current.events.onAddQueueSlot(queueSlot("third", "Queued", "0")));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["first", "second", "third"]);
        expect(result.current.totalCount).toBe(3);
        expect(result.current.refreshCount).toBe(1);
    });

    it("does not repeat queue-removal count or refresh effects when React replays the slot updater", () => {
        const { result } = renderHook(() => useQueueEventsHarness(
            "priority",
            { replayStateUpdaters: true }));

        act(() => result.current.events.onRemoveQueueSlots(new Set(["first"])));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["second"]);
        expect(result.current.totalCount).toBe(1);
        expect(result.current.refreshCount).toBe(1);
    });

    it("does not repeat priority count or refresh effects when React replays the slot updater", () => {
        const { result } = renderHook(() => useQueueEventsHarness(
            "priority",
            { queueStatusFilter: "queued", replayStateUpdaters: true }));

        act(() => result.current.events.onChangeQueueSlotPriority("first", "Paused"));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["second"]);
        expect(result.current.totalCount).toBe(1);
        expect(result.current.refreshCount).toBe(1);
    });

    it("does not repeat status count or refresh effects when React replays the slot updater", () => {
        const { result } = renderHook(() => useQueueEventsHarness(
            "priority",
            { queueStatusFilter: "queued", replayStateUpdaters: true }));

        act(() => result.current.events.onChangeQueueSlotStatus("first|Downloading"));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["second"]);
        expect(result.current.totalCount).toBe(1);
        expect(result.current.refreshCount).toBe(1);
    });

    it("requests refresh when a history add shifts a later page", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(2));

        act(() => result.current.events.onAddHistorySlot(historySlot("new")));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["first", "second"]);
        expect(result.current.totalCount).toBe(11);
        expect(result.current.refreshCount).toBe(1);
    });

    it("replaces an existing history slot without inflating the total count", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(1));
        const incoming = {
            ...historySlot("first"),
            name: "canonical-history-name.nzb",
            status: "Failed",
        };

        act(() => result.current.events.onAddHistorySlot(incoming));

        expect(result.current.slots).toHaveLength(2);
        expect(result.current.slots[0]).toEqual(incoming);
        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["first", "second"]);
        expect(result.current.totalCount).toBe(10);
        expect(result.current.refreshCount).toBe(0);
    });

    it("counts and refreshes a repeated off-page history add only once", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(2));
        const firstDelivery = historySlot("repeated");
        const repeatedDelivery = {
            ...firstDelivery,
            name: "canonical-history-name.nzb",
            status: "Failed",
        };

        act(() => result.current.events.onAddHistorySlot(firstDelivery));
        act(() => result.current.events.onAddHistorySlot(repeatedDelivery));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["first", "second"]);
        expect(result.current.totalCount).toBe(11);
        expect(result.current.refreshCount).toBe(1);
    });

    it("does not repeat history count or refresh effects when React replays a state updater", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(2, true));

        act(() => result.current.events.onAddHistorySlot(historySlot("replayed")));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["first", "second"]);
        expect(result.current.totalCount).toBe(11);
        expect(result.current.refreshCount).toBe(1);
    });

    it("keeps one canonical slot when a new history add is delivered repeatedly", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(1));
        const firstDelivery = historySlot("repeated");
        const repeatedDelivery = {
            ...firstDelivery,
            name: "canonical-history-name.nzb",
            status: "Failed",
        };

        act(() => {
            result.current.events.onAddHistorySlot(firstDelivery);
            result.current.events.onAddHistorySlot(repeatedDelivery);
        });

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["repeated", "first"]);
        expect(result.current.slots[0]).toEqual(repeatedDelivery);
        expect(result.current.totalCount).toBe(11);
        expect(result.current.refreshCount).toBe(0);
    });

    it("requests refresh when a visible history removal leaves the page incomplete", () => {
        const { result } = renderHook(() => useHistoryEventsHarness(1));

        act(() => result.current.events.onRemoveHistorySlots(new Set(["first"])));

        expect(result.current.slots.map(slot => slot.nzo_id)).toEqual(["second"]);
        expect(result.current.totalCount).toBe(9);
        expect(result.current.refreshCount).toBe(1);
    });
});

function queueSlot(nzo_id: string, status: string, true_percentage: string): PresentationQueueSlot {
    return {
        nzo_id,
        filename: `${nzo_id}.nzb`,
        status,
        priority: "Normal",
        true_percentage,
    } as PresentationQueueSlot;
}

function historySlot(nzo_id: string): PresentationHistorySlot {
    return {
        nzo_id,
        name: `${nzo_id}.nzb`,
        status: "Completed",
    } as PresentationHistorySlot;
}

class ThrowingMapArray<T> extends Array<T> {
    override map<U>(): U[] {
        throw new Error("Progress updates should not map the full visible page.");
    }
}

function useQueueEventsHarness(
    queueSort: QueueSortField,
    {
        queueStatusFilter = "all",
        replayStateUpdaters = false,
    }: {
        queueStatusFilter?: QueueStatusFilter;
        replayStateUpdaters?: boolean;
    } = {},
) {
    const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
    const [slots, setSlots] = useState<PresentationQueueSlot[]>([
        queueSlot("first", "Queued", "0"),
        queueSlot("second", "Downloading", "20"),
    ]);
    const [totalCount, setTotalQueueCount] = useState(2);
    const uploadQueueRef = useRef<UploadingFile[]>([]);
    const refreshCountRef = useRef(0);
    const slotUpdateCountRef = useRef(0);
    const onNeedsRefresh = useCallback(() => {
        refreshCountRef.current++;
    }, []);
    const setSlotsCounting = useCallback((value: React.SetStateAction<PresentationQueueSlot[]>) => {
        slotUpdateCountRef.current++;
        if (!replayStateUpdaters || typeof value !== "function") {
            setSlots(value);
            return;
        }

        setSlots(currentSlots => {
            value(currentSlots);
            return value(currentSlots);
        });
    }, [replayStateUpdaters]);

    const events = useQueueEvents(
        setUploadingFiles,
        slots,
        setSlotsCounting,
        setTotalQueueCount,
        uploadQueueRef,
        1,
        50,
        queueStatusFilter,
        queueSort,
        onNeedsRefresh);

    return {
        events,
        slots,
        totalCount,
        uploadingFiles,
        refreshCount: refreshCountRef.current,
        slotUpdateCount: slotUpdateCountRef.current,
    };
}

function useHistoryEventsHarness(pageNumber: number, replayStateUpdaters = false) {
    const [slots, setSlots] = useState<PresentationHistorySlot[]>([
        historySlot("first"),
        historySlot("second"),
    ]);
    const [totalCount, setTotalCount] = useState(10);
    const refreshCountRef = useRef(0);
    const onNeedsRefresh = useCallback(() => {
        refreshCountRef.current++;
    }, []);
    const setSlotsWithOptionalReplay = useCallback((value: React.SetStateAction<PresentationHistorySlot[]>) => {
        if (!replayStateUpdaters || typeof value !== "function") {
            setSlots(value);
            return;
        }

        setSlots(currentSlots => {
            value(currentSlots);
            return value(currentSlots);
        });
    }, [replayStateUpdaters]);

    const events = useHistoryEvents(
        slots,
        setSlotsWithOptionalReplay,
        setTotalCount,
        pageNumber,
        2,
        onNeedsRefresh);

    return {
        events,
        slots,
        totalCount,
        refreshCount: refreshCountRef.current,
    };
}

class ThrowingIteratorArray<T> extends Array<T> {
    override [Symbol.iterator](): ArrayIterator<T> {
        throw new Error("Status updates should not iterate the full visible page.");
    }
}
