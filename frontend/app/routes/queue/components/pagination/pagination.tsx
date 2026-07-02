import styles from "./pagination.module.css";
import { SimpleDropdown } from "../simple-dropdown/simple-dropdown";
import { memo } from "react";

export type PaginationProps = {
    pageNumber: number,
    totalPages: number,
    pageSize: number,
    pageSizeOptions: number[],
    onPageSelected?: (page: number) => void,
    onPageSizeSelected?: (pageSize: number) => void,
}

export const Pagination = memo(({
    pageNumber,
    totalPages,
    pageSize,
    pageSizeOptions,
    onPageSelected,
    onPageSizeSelected,
}: PaginationProps) => {
    const handlePageClick = (page: number, e: React.MouseEvent) => {
        e.preventDefault();
        if (onPageSelected && page !== pageNumber && page >= 1 && page <= totalPages) {
            onPageSelected(page);
        }
    };

    const handleDropdownChange = (value: string) => {
        const page = parseInt(value, 10);
        if (onPageSelected && !isNaN(page)) {
            onPageSelected(page);
        }
    };

    const handlePageSizeChange = (value: string) => {
        const nextPageSize = parseInt(value, 10);
        if (onPageSizeSelected && !isNaN(nextPageSize) && nextPageSize !== pageSize) {
            onPageSizeSelected(nextPageSize);
        }
    };

    const pageOptions = Array.from({ length: totalPages }, (_, i) => String(i + 1));
    const pageSizeOptionValues = pageSizeOptions.map(String);

    return (
        <div className={styles.pagination}>
            {pageNumber > 1 ? (
                <a
                    href="#"
                    className={styles.navLink}
                    onClick={(e) => handlePageClick(pageNumber - 1, e)}
                >
                    &laquo; Prev
                </a>
            ) : (
                <span className={styles.navLinkDisabled}>&laquo; Prev</span>
            )}

            <div className={styles.pageSelector}>
                <span className={styles.pageText}>Page</span>
                <SimpleDropdown
                    type={'bordered'}
                    options={pageOptions}
                    value={String(pageNumber)}
                    onChange={handleDropdownChange}
                    ariaLabel="Page number"
                />
                <span className={styles.pageText}>of {totalPages}</span>
            </div>

            <div className={styles.pageSelector}>
                <span className={styles.pageText}>Rows</span>
                <SimpleDropdown
                    type={'bordered'}
                    options={pageSizeOptionValues}
                    value={String(pageSize)}
                    onChange={handlePageSizeChange}
                    ariaLabel="Rows per page"
                />
            </div>

            {pageNumber < totalPages ? (
                <a
                    href="#"
                    className={styles.navLink}
                    onClick={(e) => handlePageClick(pageNumber + 1, e)}
                >
                    Next &raquo;
                </a>
            ) : (
                <span className={styles.navLinkDisabled}>Next &raquo;</span>
            )}
        </div>
    );
});
