import { ActionButton } from "../action-button/action-button"
import { memo, useCallback, useMemo, useState } from "react"
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal"
import type { PresentationQueueSlot } from "../../route"
import type { TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import { PageRow, PageTable } from "../page-table/page-table"
import { PageSection } from "../page-section/page-section"
import { EmptyQueue } from "../empty-queue/empty-queue"
import { SimpleDropdown } from "../simple-dropdown/simple-dropdown"
import styles from "../../route.module.css"
import { withUrlBase } from "~/utils/url-base"
import { WideViewport } from "../wide-viewport/wide-viewport"
import { ThinViewport } from "../thin-viewport/thin-viewport"
import { Pagination } from "../pagination/pagination"
import type { QueueSortField, QueueSortOrder, QueueStatusFilter } from "~/clients/backend-client.server"
import { getHttpErrorMessage, readJsonObjectOrEmpty } from "~/utils/http-response"

export type QueueTableProps = {
    queueSlots: PresentationQueueSlot[],
    totalQueueCount: number,
    queueStatusFilter: QueueStatusFilter,
    queueSort: QueueSortField,
    queueOrder: QueueSortOrder,
    isQueuePaused: boolean,
    queueStatusText: string,
    pageNumber: number,
    pageSize: number,
    pageSizeOptions: number[],
    categories: string[],
    manualCategoryRef: React.RefObject<string>,
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
    onPriorityChanged: (nzo_id: string, priority: string) => void,
    onUploadClicked?: () => void;
    onQueueStatusSelected: (status: QueueStatusFilter) => void;
    onQueueSortSelected: (sort: QueueSortField) => void;
    onPauseQueueChanged: (isPaused: boolean) => void;
    onPageSelected?: (page: number) => void;
    onPageSizeSelected?: (pageSize: number) => void;
}

export function QueueTable({
    queueSlots,
    totalQueueCount,
    queueStatusFilter,
    queueSort,
    queueOrder,
    isQueuePaused,
    queueStatusText,
    pageNumber,
    pageSize,
    pageSizeOptions,
    categories,
    manualCategoryRef,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
    onPriorityChanged,
    onUploadClicked,
    onQueueStatusSelected,
    onQueueSortSelected,
    onPauseQueueChanged,
    onPageSelected,
    onPageSizeSelected,
}: QueueTableProps) {
    const [pendingRemoval, setPendingRemoval] = useState<{ nzoIds: Set<string>, label: string } | null>(null);
    const [isPausingQueue, setIsPausingQueue] = useState(false);
    const [operationError, setOperationError] = useState<string | null>(null);
    const totalPages = Math.max(1, Math.ceil(totalQueueCount / pageSize));
    const selectedQueueIds = useMemo(
        () => new Set<string>(queueSlots.filter(x => !!x.isSelected).map(x => x.nzo_id)),
        [queueSlots]
    );
    var selectedCount = selectedQueueIds.size;
    var headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === queueSlots.length ? 'all' : 'some';

    // row events
    const onRowIsSelectedChanged = useCallback((id: string, isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>([id]), isSelected);
    }, [onIsSelectedChanged]);

    const onRowIsRemovingChanged = useCallback((id: string, isRemoving: boolean) => {
        onIsRemovingChanged(new Set<string>([id]), isRemoving);
    }, [onIsRemovingChanged]);

    const onRowRemoved = useCallback((id: string) => {
        onRemoved(new Set([id]));
    }, [onRemoved]);

    // table events
    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(queueSlots.map(x => x.nzo_id)), isSelected);
    }, [queueSlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setPendingRemoval({ nzoIds: selectedQueueIds, label: `${selectedCount} selected item(s)` });
    }, [selectedCount, selectedQueueIds, setPendingRemoval]);

    const onBulkRemove = useCallback((category: string | null, label: string) => {
        const nzoIds = new Set(queueSlots
            .filter(x => !x.isUploading && (category === null || x.cat.toLowerCase() === category))
            .map(x => x.nzo_id));

        if (nzoIds.size === 0) return;
        setPendingRemoval({ nzoIds, label: `${nzoIds.size} ${label} item(s)` });
    }, [queueSlots, setPendingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setPendingRemoval(null);
    }, [setPendingRemoval]);

    const onConfirmRemoval = useCallback(async () => {
        if (!pendingRemoval) return;

        const { uploadingIds, queuedIds } = getQueueRemovalIds(queueSlots, pendingRemoval.nzoIds);
        setPendingRemoval(null);
        setOperationError(null);
        onRemoved(uploadingIds);
        if (queuedIds.size === 0) return;

        onIsRemovingChanged(queuedIds, true);
        try {
            const url = withUrlBase(`/api?mode=queue&name=delete`);
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(queuedIds) }),
            });
            if (response.ok) {
                const data = await readJsonObjectOrEmpty(response);
                if (data.status === true) {
                    onRemoved(queuedIds);
                    return;
                }
                setOperationError(typeof data.error === "string" ? data.error : "Failed to remove queue items.");
            } else {
                setOperationError(`Failed to remove queue items: ${await getHttpErrorMessage(response)}`);
            }
        } catch (error) {
            setOperationError(`Failed to remove queue items: ${error instanceof Error ? error.message : "unknown error"}.`);
        }
        onIsRemovingChanged(queuedIds, false);
    }, [pendingRemoval, queueSlots, setPendingRemoval, setOperationError, onIsRemovingChanged, onRemoved]);

    const onPauseResumeQueue = useCallback(async () => {
        const nextPausedState = !isQueuePaused;
        setIsPausingQueue(true);
        setOperationError(null);
        try {
            const mode = nextPausedState ? "pause" : "resume";
            const response = await fetch(withUrlBase(`/api?mode=${mode}`));
            if (response.ok) {
                const data = await readJsonObjectOrEmpty(response);
                if (data.status === true) {
                    onPauseQueueChanged(nextPausedState);
                    setIsPausingQueue(false);
                    return;
                }
                setOperationError(typeof data.error === "string" ? data.error : `Failed to ${nextPausedState ? "pause" : "resume"} queue.`);
            } else {
                setOperationError(`Failed to ${nextPausedState ? "pause" : "resume"} queue: ${await getHttpErrorMessage(response)}`);
            }
        } catch (error) {
            setOperationError(`Failed to ${nextPausedState ? "pause" : "resume"} queue: ${error instanceof Error ? error.message : "unknown error"}.`);
        }
        setIsPausingQueue(false);
    }, [isQueuePaused, onPauseQueueChanged, setIsPausingQueue, setOperationError]);


    // view
    const categoryDropdown = useMemo(() => (
        <div title="Choose the category for manual nzb uploads.">
            <SimpleDropdown options={categories} valueRef={manualCategoryRef} ariaLabel="Manual upload category" />
        </div>
    ), [categories]);

    const sectionTitle = (
        <div className={styles.sectionTitle}>
            <h3 onClick={onUploadClicked} style={{ cursor: 'pointer' }}>
                Queue
            </h3>
            {headerCheckboxState !== 'none' &&
                <ActionButton type="delete" onClick={onRemove} />
            }
            {queueSlots.length > 0 &&
                <>
                    <ActionButton type="delete" text="All" onClick={() => onBulkRemove(null, "queue")} />
                    <ActionButton type="delete" text="TV" onClick={() => onBulkRemove("tv", "TV")} />
                    <ActionButton type="delete" text="Movies" onClick={() => onBulkRemove("movies", "movie")} />
                </>
            }
            <ActionButton
                type={isQueuePaused ? "resume" : "pause"}
                text={isQueuePaused ? "Resume" : "Pause"}
                disabled={isPausingQueue}
                onClick={onPauseResumeQueue} />
            <WideViewport width="450px">
                <div style={{ marginLeft: '10px' }}>
                    {categoryDropdown}
                </div>
            </WideViewport>
        </div>
    );

    const sectionSubTitle = (
        <ThinViewport width="450px">
            {categoryDropdown}
        </ThinViewport>
    );

    return (
        <PageSection title={sectionTitle} subTitle={sectionSubTitle} badgeText={`${queueStatusText} · ${totalQueueCount} item(s)`}>
            {operationError && <div className={styles.alert} role="alert">{operationError}</div>}
            <QueueFilters value={queueStatusFilter} onChange={onQueueStatusSelected} />
            {queueSlots?.length == 0 && totalQueueCount === 0 ? (
                <EmptyQueue onUploadClicked={onUploadClicked} />
            ) : (
                <PageTable
                    headerCheckboxState={headerCheckboxState}
                    onHeaderCheckboxChange={onSelectAll}
                    sortField={queueSort}
                    sortDirection={queueOrder}
                    onSortSelected={onQueueSortSelected}>
                    {queueSlots.map(slot =>
                        <QueueRow
                            key={slot.nzo_id}
                            slot={slot}
                            onIsSelectedChanged={onRowIsSelectedChanged}
                            onIsRemovingChanged={onRowIsRemovingChanged}
                            onRemoved={onRowRemoved}
                            onPriorityChanged={onPriorityChanged}
                        />
                    )}
                </PageTable>
            )}
            <Pagination
                pageNumber={pageNumber}
                totalPages={totalPages}
                pageSize={pageSize}
                pageSizeOptions={pageSizeOptions}
                onPageSelected={onPageSelected}
                onPageSizeSelected={onPageSizeSelected} />

            <ConfirmModal
                show={pendingRemoval !== null}
                title="Remove From Queue?"
                message={`${pendingRemoval?.label ?? ""} will be removed`}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </PageSection>
    );
}

export function getQueueRemovalIds(queueSlots: PresentationQueueSlot[], selectedIds: Set<string>) {
    const uploadingIds = new Set<string>();
    const queuedIds = new Set<string>();

    for (const slot of queueSlots) {
        if (!selectedIds.has(slot.nzo_id)) continue;
        if (slot.isUploading) uploadingIds.add(slot.nzo_id);
        else queuedIds.add(slot.nzo_id);
    }

    return { uploadingIds, queuedIds };
}

function QueueFilters({
    value,
    onChange,
}: {
    value: QueueStatusFilter,
    onChange: (status: QueueStatusFilter) => void,
}) {
    const filters: Array<{ value: QueueStatusFilter, label: string }> = [
        { value: "all", label: "All" },
        { value: "downloading", label: "Downloading" },
        { value: "verifying", label: "Verifying" },
        { value: "repairing", label: "Repairing" },
        { value: "moving", label: "Moving" },
        { value: "queued", label: "Queued" },
        { value: "paused", label: "Paused" },
    ];

    return (
        <div className={styles.queueFilters} role="group" aria-label="Queue status filters">
            {filters.map(filter => (
                <button
                    key={filter.value}
                    type="button"
                    className={value === filter.value ? styles.queueFilterActive : styles.queueFilter}
                    onClick={() => onChange(filter.value)}
                >
                    {filter.label}
                </button>
            ))}
        </div>
    )
}

type QueueRowProps = {
    slot: PresentationQueueSlot
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void,
    onPriorityChanged: (nzo_id: string, priority: string) => void
}

const priorityOptions = ["Force", "High", "Normal", "Low", "Paused"];
const priorityValues: Record<string, string> = {
    Force: "2",
    High: "1",
    Normal: "0",
    Low: "-1",
    Paused: "-2"
};

export const QueueRow = memo(({
    slot,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
    onPriorityChanged
}: QueueRowProps) => {
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [isChangingPriority, setIsChangingPriority] = useState(false);
    const [operationError, setOperationError] = useState<string | null>(null);
    const isActivelyUploading = slot.isUploading && slot.status == "uploading";

    // events
    const onRemove = useCallback(() => {
        // immediately remove uploading items, without need of confirmation.
        if (slot.isUploading) {
            onRemoved(slot.nzo_id);
            return;
        }

        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async () => {
        if (slot.isUploading) return;
        setIsConfirmingRemoval(false);
        setOperationError(null);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = withUrlBase('/api?mode=queue&name=delete')
                + `&value=${encodeURIComponent(slot.nzo_id)}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await readJsonObjectOrEmpty(response);
                if (data.status === true) {
                    onRemoved(slot.nzo_id);
                    return;
                }
                setOperationError(typeof data.error === "string" ? data.error : "Failed to remove queue item.");
            } else {
                setOperationError(`Failed to remove queue item: ${await getHttpErrorMessage(response)}`);
            }
        } catch (error) {
            setOperationError(`Failed to remove queue item: ${error instanceof Error ? error.message : "unknown error"}.`);
        }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, setOperationError, onIsRemovingChanged, onRemoved]);

    const onChangePriority = useCallback(async (priority: string) => {
        if (slot.isUploading || priority === slot.priority) return;

        setOperationError(null);
        setIsChangingPriority(true);
        try {
            const url = withUrlBase('/api?mode=queue&name=priority')
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&value2=${encodeURIComponent(priorityValues[priority] ?? "0")}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await readJsonObjectOrEmpty(response);
                if (data.status === true) {
                    onPriorityChanged(slot.nzo_id, priority);
                    setIsChangingPriority(false);
                    return;
                }
                setOperationError(typeof data.error === "string" ? data.error : "Failed to change queue item priority.");
            } else {
                setOperationError(`Failed to change queue item priority: ${await getHttpErrorMessage(response)}`);
            }
        } catch (error) {
            setOperationError(`Failed to change queue item priority: ${error instanceof Error ? error.message : "unknown error"}.`);
        }
        setIsChangingPriority(false);
    }, [slot.isUploading, slot.priority, slot.nzo_id, onPriorityChanged, setIsChangingPriority, setOperationError]);

    // view
    const priority = priorityOptions.includes(slot.priority) ? slot.priority : "Normal";
    const arrPriority = slot.arr_priority;
    const arrMeta = arrPriority
        ? [
            `ARR ${arrPriority.apply_to_scheduling ? "apply" : "report"} score ${arrPriority.score}`,
            arrPriority.effective_priority ? `effective ${arrPriority.effective_priority}` : null,
            arrPriority.reasons?.length ? arrPriority.reasons.slice(0, 3).join(", ") : arrPriority.stale_reason
        ].filter(Boolean).join(" · ")
        : null;
    const actions = (
        <>
            {!slot.isUploading &&
                <div style={{ minWidth: "92px", opacity: isChangingPriority ? 0.5 : 1 }}>
                    <SimpleDropdown
                        type="bordered"
                        options={priorityOptions}
                        value={priority}
                        onChange={onChangePriority}
                        ariaLabel={`Priority for ${slot.filename}`} />
                </div>
            }
            <ActionButton type="delete" disabled={!!slot.isRemoving || isActivelyUploading} onClick={onRemove} />
        </>
    );

    return (
        <>
            <PageRow
                isUploading={!!slot.isUploading}
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.filename}
                category={slot.cat}
                status={slot.status}
                percentage={slot.true_percentage}
                fileSizeBytes={Number(slot.mb) * 1024 * 1024}
                meta={arrMeta}
                actions={actions}
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
                error={slot.error}
            />
            {operationError &&
                <tr>
                    <td colSpan={5}>
                        <div className={styles.alert} role="alert">{operationError}</div>
                    </td>
                </tr>
            }
            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From Queue?"
                message={slot.filename}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    )
});
