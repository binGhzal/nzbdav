import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { QueueRow, QueueTable, type QueueTableProps } from "./queue-table";
import type { PresentationQueueSlot } from "../../route";

describe("QueueRow", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
    });

    it("formats queue slot size from SAB megabytes", () => {
        render(
            <table>
                <tbody>
                    <QueueRow
                        slot={queueSlot("11111111-1111-1111-1111-111111111111")}
                        onIsSelectedChanged={vi.fn()}
                        onIsRemovingChanged={vi.fn()}
                        onRemoved={vi.fn()}
                        onPriorityChanged={vi.fn()} />
                </tbody>
            </table>
        );

        expect(screen.getAllByText("1 MB")).not.toHaveLength(0);
        expect(screen.queryByText("NaN B")).toBeNull();
    });

    it("shows the backend error body when row removal fails", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => new Response("queue delete failed", { status: 500 })));
        const onIsRemovingChanged = vi.fn();

        render(
            <table>
                <tbody>
                    <QueueRow
                        slot={queueSlot("11111111-1111-1111-1111-111111111111")}
                        onIsSelectedChanged={vi.fn()}
                        onIsRemovingChanged={onIsRemovingChanged}
                        onRemoved={vi.fn()}
                        onPriorityChanged={vi.fn()} />
                </tbody>
            </table>
        );

        fireEvent.click(screen.getByRole("button", { name: "Delete" }));
        fireEvent.click(screen.getByRole("button", { name: "Confirm Removal" }));

        const alert = await screen.findByRole("alert");
        expect(alert.textContent).toContain("Failed to remove queue item: queue delete failed");
        expect(onIsRemovingChanged).toHaveBeenLastCalledWith(
            "11111111-1111-1111-1111-111111111111",
            false);
        expect(fetch).toHaveBeenCalledWith(
            "/nzbdav/api?mode=queue&name=delete&value=11111111-1111-1111-1111-111111111111");
    });

    it("does not offer queue mutations for an automatic repair row", () => {
        const slot = {
            ...queueSlot("11111111-1111-1111-1111-111111111111"),
            status: "Repairing",
            can_manage: false,
        } as PresentationQueueSlot;

        render(
            <table>
                <tbody>
                    <QueueRow
                        slot={slot}
                        onIsSelectedChanged={vi.fn()}
                        onIsRemovingChanged={vi.fn()}
                        onRemoved={vi.fn()}
                        onPriorityChanged={vi.fn()} />
                </tbody>
            </table>
        );

        expect(screen.queryByLabelText("Priority for Example.nzb")).toBeNull();
        expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
        expect(screen.queryByRole("checkbox")).toBeNull();
        expect(screen.getByText("Automatic")).toBeInTheDocument();
    });
});

describe("QueueTable controls", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
    });

    it("exposes the selected queue status as a pressed filter button", () => {
        renderQueueTable({ queueStatusFilter: "verifying" });

        const filters = screen.getByRole("group", { name: "Queue status filters" });
        expect(within(filters).getByRole("button", { name: "Verifying" }))
            .toHaveAttribute("aria-pressed", "true");
        expect(within(filters).getByRole("button", { name: "All" }))
            .toHaveAttribute("aria-pressed", "false");
        expect(within(filters).getByRole("button", { name: "Queued" }))
            .toHaveAttribute("aria-pressed", "false");
    });

    it("uses a visible upload button instead of making the Queue heading clickable", () => {
        const onUploadClicked = vi.fn();
        renderQueueTable({ onUploadClicked });

        const heading = screen.getByRole("heading", { level: 3, name: "Queue" });
        fireEvent.click(heading);
        expect(onUploadClicked).not.toHaveBeenCalled();

        fireEvent.click(screen.getByRole("button", { name: "Upload NZB" }));
        expect(onUploadClicked).toHaveBeenCalledTimes(1);
    });

    it("labels bulk deletion as visible scope when only the current page is selected", () => {
        renderQueueTable({
            queueSlots: [
                queueSlot("11111111-1111-1111-1111-111111111111"),
                queueSlot("22222222-2222-2222-2222-222222222222"),
            ],
            totalQueueCount: 100,
        });

        expect(screen.getByRole("button", { name: "Delete visible queue items" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Delete visible TV queue items" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Delete visible movie queue items" })).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Delete All" })).toBeNull();
    });
});

function queueSlot(nzoId: string): PresentationQueueSlot {
    return {
        nzo_id: nzoId,
        filename: "Example.nzb",
        cat: "movies",
        status: "Queued",
        priority: "Normal",
        mb: "1.00",
        mbleft: "1.00",
        percentage: "0",
        true_percentage: "0",
        isSelected: false,
        isRemoving: false,
    } as PresentationQueueSlot;
}

function renderQueueTable(overrides: Partial<QueueTableProps> = {}) {
    const props: QueueTableProps = {
        queueSlots: [],
        totalQueueCount: 0,
        queueStatusFilter: "all",
        queueSort: "priority",
        queueOrder: "desc",
        isQueuePaused: false,
        queueStatusText: "Idle",
        pageNumber: 1,
        pageSize: 50,
        pageSizeOptions: [25, 50, 100],
        categories: ["movies"],
        manualCategoryRef: { current: "movies" },
        onIsSelectedChanged: vi.fn(),
        onIsRemovingChanged: vi.fn(),
        onRemoved: vi.fn(),
        onPriorityChanged: vi.fn(),
        onUploadClicked: vi.fn(),
        onQueueStatusSelected: vi.fn(),
        onQueueSortSelected: vi.fn(),
        onPauseQueueChanged: vi.fn(),
        onPageSelected: vi.fn(),
        onPageSizeSelected: vi.fn(),
        ...overrides,
    };

    render(<QueueTable {...props} />);
}
