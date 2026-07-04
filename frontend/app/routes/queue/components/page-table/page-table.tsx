import { Badge, Table } from "react-bootstrap";
import styles from "./page-table.module.css";
import type { ReactNode } from "react";
import { TriCheckbox, type TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { Truncate } from "../truncate/truncate";
import { StatusBadge } from "../status-badge/status-badge";
import { formatFileSize } from "~/utils/file-size";
import { classNames } from "~/utils/styling";
import type { QueueSortField, QueueSortOrder } from "~/clients/backend-client.server";

export type PageTableProps = {
    children?: ReactNode,
    headerCheckboxState: TriCheckboxState,
    onHeaderCheckboxChange: (isChecked: boolean) => void,
    footer?: ReactNode,
    sortField?: QueueSortField,
    sortDirection?: QueueSortOrder,
    onSortSelected?: (field: QueueSortField) => void,
}

export function PageTable({
    children,
    headerCheckboxState,
    onHeaderCheckboxChange,
    footer,
    sortField,
    sortDirection = "desc",
    onSortSelected,
}: PageTableProps) {
    return (
        <div className={styles.tableContainer}>
            <Table className={styles.table}>
                <thead>
                    <tr>
                        <th>
                            <TriCheckbox state={headerCheckboxState} onChange={onHeaderCheckboxChange}>
                                <SortHeaderButton
                                    field="name"
                                    label="Name"
                                    sortField={sortField}
                                    sortDirection={sortDirection}
                                    onSortSelected={onSortSelected} />
                            </TriCheckbox>
                        </th>
                        <th className={styles.desktop}>
                            <SortHeaderButton
                                field="category"
                                label="Category"
                                sortField={sortField}
                                sortDirection={sortDirection}
                                onSortSelected={onSortSelected} />
                        </th>
                        <th className={styles.desktop}>
                            <SortHeaderButton
                                field="status"
                                label="Status"
                                sortField={sortField}
                                sortDirection={sortDirection}
                                onSortSelected={onSortSelected} />
                        </th>
                        <th className={styles.desktop}>
                            <SortHeaderButton
                                field="size"
                                label="Size"
                                sortField={sortField}
                                sortDirection={sortDirection}
                                onSortSelected={onSortSelected} />
                        </th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {children}
                </tbody>
            </Table>
            {footer &&
                <div className={styles.footer}>{footer}</div>
            }
        </div>
    );
}

function SortHeaderButton({
    field,
    label,
    sortField,
    sortDirection,
    onSortSelected,
}: {
    field: QueueSortField,
    label: string,
    sortField?: QueueSortField,
    sortDirection: QueueSortOrder,
    onSortSelected?: (field: QueueSortField) => void,
}) {
    const isActive = sortField === field;
    if (!onSortSelected) return <>{label}</>;

    return (
        <button
            type="button"
            className={isActive ? styles.sortButtonActive : styles.sortButton}
            onClick={event => {
                event.stopPropagation();
                onSortSelected(field);
            }}
            aria-label={`Sort by ${label}`}>
            <span>{label}</span>
            <span className={styles.sortIndicator} aria-hidden="true">
                {isActive ? (sortDirection === "asc" ? "^" : "v") : ""}
            </span>
        </button>
    );
}

export type PageRowProps = {
    isUploading?: boolean,
    isSelected: boolean,
    isRemoving: boolean,
    name: string,
    category: string,
    status: string,
    percentage?: string,
    error?: string,
    meta?: ReactNode,
    fileSizeBytes: number,
    actions: ReactNode,
    onRowSelectionChanged: (isSelected: boolean) => void
}
export function PageRow(props: PageRowProps) {
    const rowStyles = [
        props.isRemoving && styles.removing,
        props.isUploading && styles.uploading
    ];

    return (
        <tr className={classNames(rowStyles)}>
            <td>
                <TriCheckbox state={props.isSelected} onChange={props.onRowSelectionChanged}>
                    <Truncate>{props.name}</Truncate>
                    {props.meta &&
                        <div className={styles.metaLine}>{props.meta}</div>
                    }
                    <div className={styles.mobile}>
                        <div className={styles.badges}>
                            <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
                            <CategoryBadge category={props.category} />
                        </div>
                        <div>{formatFileSize(props.fileSizeBytes)}</div>
                    </div>
                </TriCheckbox>
            </td>
            <td className={styles.desktop}>
                <CategoryBadge category={props.category} />
            </td>
            <td className={styles.desktop}>
                <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
            </td>
            <td className={styles.desktop}>
                {formatFileSize(props.fileSizeBytes)}
            </td>
            <td>
                <div className={styles.actions}>
                    {props.actions}
                </div>
            </td>
        </tr>
    );
}

export function CategoryBadge({ category }: { category: string }) {
    const categoryLower = category?.toLowerCase();
    let variant = 'secondary';
    if (categoryLower === 'movies') variant = 'primary';
    if (categoryLower === 'tv') variant = 'info';
    return <Badge bg={variant} style={{ width: '85px' }}>{categoryLower}</Badge>
}
