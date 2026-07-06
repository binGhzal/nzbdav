import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RecreateStrmFiles } from "./recreate-strm-files/recreate-strm-files";
import { RemoveUnlinkedFiles } from "./remove-unlinked-files/remove-unlinked-files";
import { ConvertStrmToSymlinks } from "./strm-to-symlinks/strm-to-symlinks";

describe("maintenance task actions", () => {
    afterEach(() => {
        cleanup();
        FakeWebSocket.instances = [];
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("shows an error when recreating STRM files fails to start", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 500 })));

        render(<RecreateStrmFiles />);
        await openLastSocket();

        fireEvent.click(screen.getByRole("button", { name: "▶ Run Task" }));

        await waitFor(() => {
            expect(screen.getByText("Failed to start recreate STRM files (500).")).toBeTruthy();
        });
    });

    it("shows an error when removing unlinked files fails to start", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 502 })));

        render(<RemoveUnlinkedFiles savedConfig={{ "media.library-dir": "/library" }} />);
        await openLastSocket();

        fireEvent.click(screen.getByRole("button", { name: "▶ Run Task" }));

        await waitFor(() => {
            expect(screen.getByText("Failed to start remove unlinked files (502).")).toBeTruthy();
        });
    });

    it("shows an error when converting STRM files to symlinks fails to start", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 503 })));

        render(<ConvertStrmToSymlinks savedConfig={{ "media.library-dir": "/library" }} />);
        await openLastSocket();

        fireEvent.click(screen.getByRole("button", { name: "▶ Run Task" }));

        await waitFor(() => {
            expect(screen.getByText("Failed to start convert STRM files to symlinks (503).")).toBeTruthy();
        });
    });
});

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
