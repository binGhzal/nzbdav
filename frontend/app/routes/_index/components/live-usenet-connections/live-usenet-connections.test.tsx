import { act, cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router";
import { LiveUsenetConnections } from "./live-usenet-connections";

describe("LiveUsenetConnections", () => {
    afterEach(() => {
        cleanup();
        FakeWebSocket.instances = [];
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("does not render infinite connection bar widths when max connections is zero", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);

        const { container } = render(
            <MemoryRouter>
                <LiveUsenetConnections />
            </MemoryRouter>
        );
        await openLastSocket();

        await act(async () => {
            FakeWebSocket.instances.at(-1)?.message("cxs", "0|0|0|4|0|1");
        });

        await waitFor(() => {
            const barFills = Array.from(container.querySelectorAll<HTMLElement>("[style]"));
            expect(barFills).toHaveLength(2);
            expect(barFills.map(x => x.style.width)).toEqual(["0%", "0%"]);
        });
    });

    it("clamps negative active connection widths from transient snapshots", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);

        const { container } = render(
            <MemoryRouter>
                <LiveUsenetConnections />
            </MemoryRouter>
        );
        await openLastSocket();

        await act(async () => {
            FakeWebSocket.instances.at(-1)?.message("cxs", "0|0|0|2|10|5");
        });

        await waitFor(() => {
            const barFills = Array.from(container.querySelectorAll<HTMLElement>("[style]"));
            expect(barFills).toHaveLength(2);
            expect(barFills.map(x => x.style.width)).toEqual(["20%", "0%"]);
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

    message(topic: string, message: string) {
        this.onmessage?.(new MessageEvent("message", {
            data: JSON.stringify({ Topic: topic, Message: message }),
        }));
    }
}
