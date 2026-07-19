import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RemoveUnlinkedFiles } from "./remove-unlinked-files/remove-unlinked-files";

describe("maintenance task actions", () => {
    afterEach(() => {
        cleanup();
        FakeWebSocket.instances = [];
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("shows an error when removing unlinked files fails to start", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 502 })));

        render(<RemoveUnlinkedFiles savedConfig={{ "media.library-dir": "/library" }} />);
        await openLastSocket();

        fireEvent.click(screen.getByRole("button", { name: "▶ Run Task" }));

        await waitFor(() => {
            expect(screen.getByText("Failed to start remove unlinked files: HTTP 502.")).toBeTruthy();
        });
    });

    it("restores a persisted running state without waiting for a websocket message", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
            const url = input.toString();
            if (url.includes("/api/maintenance/status")) {
                return Response.json({
                    activeRun: maintenanceRun({
                        kind: "remove-unlinked-files",
                        status: "running",
                        message: "Checking symlink 7.",
                        progressCurrent: 6,
                    }),
                    lastRun: null,
                });
            }
            return new Response("unexpected request", { status: 500 });
        }));

        render(<RemoveUnlinkedFiles savedConfig={{ "media.library-dir": "/library" }} />);

        await waitFor(() => {
            expect(screen.getByText(/Checking symlink 7/)).toBeTruthy();
        });
        expect(screen.getByRole("button", { name: "⌛ Running.." })).toHaveProperty("disabled", true);
    });
});

function maintenanceRun(overrides: Record<string, unknown>) {
    return {
        id: "7344633b-faf0-4612-8644-c89958eb00f0",
        kind: "remove-unlinked-files",
        status: "running",
        requestedBy: "manual",
        createdAt: "2026-07-12T08:00:00Z",
        startedAt: "2026-07-12T08:00:00Z",
        updatedAt: "2026-07-12T08:00:01Z",
        completedAt: null,
        cancellationRequestedAt: null,
        progressCurrent: 0,
        progressTotal: null,
        message: null,
        error: null,
        ...overrides,
    };
}

async function openLastSocket() {
    const socket = FakeWebSocket.instances.at(-1);
    if (!socket) throw new Error("No websocket was created.");
    await act(async () => socket.open());
}

class FakeWebSocket {
    static instances: FakeWebSocket[] = [];

    onmessage: ((event: MessageEvent) => void) | null = null;
    onopen: ((event: Event) => void) | null = null;
    onclose: ((event: CloseEvent) => void) | null = null;
    onerror: ((event: Event) => void) | null = null;

    send = vi.fn();
    close = vi.fn();

    constructor() {
        FakeWebSocket.instances.push(this);
    }

    open() {
        this.onopen?.(new Event("open"));
    }
}
