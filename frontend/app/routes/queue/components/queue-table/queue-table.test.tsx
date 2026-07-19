import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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

    it("uses a fixed fallback instead of a hostile backend body when row removal fails", async () => {
        const hostile = `queue-delete-secret\r\n\u001b[31m${"x".repeat(1024)}`;
        vi.stubGlobal("fetch", vi.fn(async () => new Response(hostile, { status: 500 })));
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
        expect(alert.textContent).toBe("Failed to remove queue item: HTTP 500");
        expect(alert.textContent).not.toContain("queue-delete-secret");
        expect(onIsRemovingChanged).toHaveBeenLastCalledWith(
            "11111111-1111-1111-1111-111111111111",
            false);
        expect(fetch).toHaveBeenCalledOnce();
        expect(fetch).toHaveBeenCalledWith(
            "/nzbdav/api?mode=queue&name=delete&value=11111111-1111-1111-1111-111111111111",
            { method: "POST" });
    });

    it("renders an exact stable failure envelope returned with HTTP 200", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: false,
            error: "The request is invalid.",
            code: "invalid_request",
            correlation_id: "0123456789abcdef0123456789abcdef",
        })));

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

        fireEvent.click(screen.getByRole("button", { name: "Delete" }));
        fireEvent.click(screen.getByRole("button", { name: "Confirm Removal" }));

        expect((await screen.findByRole("alert")).textContent).toBe(
            "Failed to remove queue item: The request is invalid. (0123456789abcdef0123456789abcdef)");
    });

    it("uses POST for a single queue priority mutation", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({ status: true })));
        const onPriorityChanged = vi.fn();

        render(
            <table>
                <tbody>
                    <QueueRow
                        slot={queueSlot("11111111-1111-1111-1111-111111111111")}
                        onIsSelectedChanged={vi.fn()}
                        onIsRemovingChanged={vi.fn()}
                        onRemoved={vi.fn()}
                        onPriorityChanged={onPriorityChanged} />
                </tbody>
            </table>
        );

        fireEvent.change(screen.getByRole("combobox", { name: "Priority for Example.nzb" }), {
            target: { value: "High" },
        });

        await waitFor(() => expect(onPriorityChanged).toHaveBeenCalledWith(
            "11111111-1111-1111-1111-111111111111",
            "High"));
        expect(fetch).toHaveBeenCalledOnce();
        expect(fetch).toHaveBeenCalledWith(
            "/nzbdav/api?mode=queue&name=priority&value=11111111-1111-1111-1111-111111111111&value2=1",
            { method: "POST" });
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

    it.each([
        ["pause", false, "Pause", true],
        ["resume", true, "Resume", false],
    ] as const)("uses POST to %s the queue", async (mode, isQueuePaused, button, expectedPaused) => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({ status: true })));
        const onPauseQueueChanged = vi.fn();
        renderQueueTable({ isQueuePaused, onPauseQueueChanged });

        fireEvent.click(screen.getByRole("button", { name: button }));

        await waitFor(() => expect(onPauseQueueChanged).toHaveBeenCalledWith(expectedPaused));
        expect(fetch).toHaveBeenCalledOnce();
        expect(fetch).toHaveBeenCalledWith(
            `/nzbdav/api?mode=${mode}`,
            { method: "POST" });
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
