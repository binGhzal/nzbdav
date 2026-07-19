import { ActionButton } from "../action-button/action-button"
import { useCallback, useState } from "react"
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal"
import { Link } from "react-router"
import { withUrlBase } from "~/utils/url-base"
import { type TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import type { PresentationHistorySlot } from "../../route"
import { getLeafDirectoryName } from "~/utils/path"
import { PageRow, PageTable } from "../page-table/page-table"
import styles from "../../route.module.css"
import { PageSection } from "../page-section/page-section"
import { DropdownOptions } from "~/routes/explore/dropdown-options/dropdown-options"
import { ExportNzb, Remove } from "~/routes/explore/item-menu/item-menu"
import { Pagination } from "../pagination/pagination"
import { readHttpActionResult } from "~/utils/http-response"

export type HistoryTableProps = {
    historySlots: PresentationHistorySlot[],
    totalHistoryCount: number,
    pageNumber: number,
    pageSize: number,
    pageSizeOptions: number[],
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
    onPageSelected?: (page: number) => void,
    onPageSizeSelected?: (pageSize: number) => void,
}

export function HistoryTable({
    historySlots,
    totalHistoryCount,
    pageNumber,
    pageSize,
    pageSizeOptions,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
    onPageSelected,
    onPageSizeSelected,
}: HistoryTableProps) {
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [operationError, setOperationError] = useState<string | null>(null);
    const totalPages = Math.max(1, Math.ceil(totalHistoryCount / pageSize));
    var selectedCount = historySlots.filter(x => !!x.isSelected).length;
    var headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === historySlots.length ? 'all' : 'some';

    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(historySlots.map(x => x.nzo_id)), isSelected);
    }, [historySlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        var nzo_ids = new Set<string>(historySlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        setIsConfirmingRemoval(false);
        setOperationError(null);
        onIsRemovingChanged(nzo_ids, true);
        try {
            const url = withUrlBase(`/api?mode=history&name=delete&del_completed_files=${deleteCompletedFiles ? 1 : 0}`);
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(nzo_ids) }),
            });
            const result = await readHttpActionResult(response);
            if (result.success) {
                onRemoved(nzo_ids);
                return;
            }
            setOperationError(`Failed to remove history items: ${result.error}`);
        } catch {
            setOperationError("Failed to remove history items: request failed.");
        }
        onIsRemovingChanged(nzo_ids, false);
    }, [historySlots, setIsConfirmingRemoval, setOperationError, onIsRemovingChanged, onRemoved]);

    var sectionTitle = (
        <div className={styles.sectionTitle}>
            <h3>History</h3>
            {headerCheckboxState !== 'none' &&
                <ActionButton type="delete" onClick={onRemove} />
            }
        </div>
    );

    return (
        <PageSection title={sectionTitle} badgeText={`${totalHistoryCount} item(s)`}>
            {operationError && <div className={styles.alert} role="alert">{operationError}</div>}
            <PageTable headerCheckboxState={headerCheckboxState} onHeaderCheckboxChange={onSelectAll}>
                {historySlots.map(slot =>
                    <HistoryRow
                        key={slot.nzo_id}
                        slot={slot}
                        onIsSelectedChanged={(id, isSelected) => onIsSelectedChanged(new Set<string>([id]), isSelected)}
                        onIsRemovingChanged={(id, isRemoving) => onIsRemovingChanged(new Set<string>([id]), isRemoving)}
                        onRemoved={(id) => onRemoved(new Set([id]))}
                    />
                )}
            </PageTable>
            <Pagination
                pageNumber={pageNumber}
                totalPages={totalPages}
                pageSize={pageSize}
                pageSizeOptions={pageSizeOptions}
                onPageSelected={onPageSelected}
                onPageSizeSelected={onPageSizeSelected} />

            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={`${selectedCount} item(s) will be removed`}
                checkboxMessage="Delete mounted files"
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </PageSection>
    );
}


type HistoryRowProps = {
    slot: PresentationHistorySlot,
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void
}

export function HistoryRow({ slot, onIsSelectedChanged, onIsRemovingChanged, onRemoved }: HistoryRowProps) {
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [operationError, setOperationError] = useState<string | null>(null);

    // events
    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        setIsConfirmingRemoval(false);
        setOperationError(null);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = withUrlBase('/api?mode=history&name=delete')
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url, { method: 'POST' });
            const result = await readHttpActionResult(response);
            if (result.success) {
                onRemoved(slot.nzo_id);
                return;
            }
            setOperationError(`Failed to remove history item: ${result.error}`);
        } catch {
            setOperationError("Failed to remove history item: request failed.");
        }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, setOperationError, onIsRemovingChanged, onRemoved]);

    // view
    return (
        <>
            <PageRow
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.name}
                category={slot.category}
                status={slot.status}
                error={slot.fail_message}
                fileSizeBytes={slot.bytes}
                actions={<Actions slot={slot} onRemove={onRemove} />}
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
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
                title="Remove From History?"
                message={slot.nzb_name}
                checkboxMessage={!slot.fail_message ? "Delete mounted files" : undefined}
                errorMessage={slot.fail_message}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    )
}

export function Actions({ slot, onRemove }: { slot: PresentationHistorySlot, onRemove: () => void }) {
    const [isMenuOpen, setIsMenuOpen] = useState(false);

    // determine explore action link url
    var downloadFolder = slot.storage && getLeafDirectoryName(slot.storage);
    const encodedCategory = downloadFolder && encodeURIComponent(slot.category);
    const encodedDownloadFolder = downloadFolder && encodeURIComponent(downloadFolder);
    var folderLink = downloadFolder && `/explore/content/${encodedCategory}/${encodedDownloadFolder}`;

    // determine nzb download URL
    var nzbDownloadUrl = slot.nzb_blob_id
        ? withUrlBase(`/api/download-nzb?nzbBlobId=${slot.nzb_blob_id}`)
        : null;

    // determine whether explore action should be disabled
    var isFolderDisabled = !downloadFolder || !!slot.isRemoving || !!slot.fail_message;

    const onMenuClick = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        setIsMenuOpen(x => !x);
    }, []);

    const onRemoveSelected = useCallback(() => {
        setIsMenuOpen(false);
        onRemove?.();
    }, [onRemove]);

    return (
        <>
            {!isFolderDisabled &&
                <Link to={folderLink} >
                    <ActionButton type="explore" />
                </Link>
            }
            {isFolderDisabled &&
                <ActionButton type="explore" disabled />
            }
            <div style={{ position: "relative" }}>
                <ActionButton
                    type="menu"
                    disabled={!!slot.isRemoving}
                    selected={isMenuOpen}
                    onClick={onMenuClick} />
                <DropdownOptions
                    style={{ marginTop: "5px" }}
                    isOpen={isMenuOpen}
                    onClose={() => setIsMenuOpen(false)}
                    options={[
                        !!nzbDownloadUrl ? { option: <ExportNzb />, linkTo: nzbDownloadUrl } : undefined,
                        { option: <Remove />, onSelect: onRemoveSelected, variant: "danger" },
                    ]} />
            </div>
        </>
    );
}
