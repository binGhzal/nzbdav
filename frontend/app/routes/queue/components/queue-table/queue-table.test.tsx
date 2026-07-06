import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { QueueRow } from "./queue-table";
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
