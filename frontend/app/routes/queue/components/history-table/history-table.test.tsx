import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { HistoryRow } from "./history-table";
import type { PresentationHistorySlot } from "../../route";

describe("HistoryRow", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
    });

    it("uses a fixed fallback instead of an arbitrary backend body when row removal fails", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => new Response("history-delete-secret", { status: 500 })));
        const onIsRemovingChanged = vi.fn();

        render(
            <MemoryRouter>
                <table>
                    <tbody>
                        <HistoryRow
                            slot={historySlot("22222222-2222-2222-2222-222222222222")}
                            onIsSelectedChanged={vi.fn()}
                            onIsRemovingChanged={onIsRemovingChanged}
                            onRemoved={vi.fn()} />
                    </tbody>
                </table>
            </MemoryRouter>
        );

        fireEvent.click(screen.getByRole("button", { name: "More actions" }));
        fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
        fireEvent.click(screen.getByRole("button", { name: "Confirm Removal" }));

        const alert = await screen.findByText("Failed to remove history item: HTTP 500");
        expect(alert.textContent).not.toContain("history-delete-secret");
        expect(onIsRemovingChanged).toHaveBeenLastCalledWith(
            "22222222-2222-2222-2222-222222222222",
            false);
        expect(fetch).toHaveBeenCalledOnce();
        expect(fetch).toHaveBeenCalledWith(
            "/nzbdav/api?mode=history&name=delete&value=22222222-2222-2222-2222-222222222222&del_completed_files=0",
            { method: "POST" });
    });

    it("uses POST when a completed item also deletes mounted files", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({ status: true })));
        const slot = {
            ...historySlot("22222222-2222-2222-2222-222222222222"),
            fail_message: "",
        } as PresentationHistorySlot;
        const onRemoved = vi.fn();

        render(
            <MemoryRouter>
                <table>
                    <tbody>
                        <HistoryRow
                            slot={slot}
                            onIsSelectedChanged={vi.fn()}
                            onIsRemovingChanged={vi.fn()}
                            onRemoved={onRemoved} />
                    </tbody>
                </table>
            </MemoryRouter>
        );

        fireEvent.click(screen.getByRole("button", { name: "More actions" }));
        fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
        fireEvent.click(screen.getByLabelText("Delete mounted files"));
        fireEvent.click(screen.getByRole("button", { name: "Confirm Removal" }));

        await waitFor(() => expect(onRemoved).toHaveBeenCalledWith(slot.nzo_id));
        expect(fetch).toHaveBeenCalledOnce();
        expect(fetch).toHaveBeenCalledWith(
            "/nzbdav/api?mode=history&name=delete&value=22222222-2222-2222-2222-222222222222&del_completed_files=1",
            { method: "POST" });
    });
});

function historySlot(nzoId: string): PresentationHistorySlot {
    return {
        nzo_id: nzoId,
        nzb_name: "Example.nzb",
        name: "Example",
        category: "movies",
        status: "Failed",
        bytes: 1024,
        storage: "",
        download_time: 1,
        fail_message: "missing file",
        isSelected: false,
        isRemoving: false,
    } as PresentationHistorySlot;
}
