import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HistoryEvents, QueueEvents } from "./events-controller";
import { initializeQueueHistoryWebsocket } from "./websocket-controller";

describe("initializeQueueHistoryWebsocket", () => {
    afterEach(() => {
        cleanup();
        FakeWebSocket.instances = [];
        vi.useRealTimers();
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("subscribes then requests one catch-up refresh on the initial and reconnect opens", () => {
        vi.useFakeTimers();
        vi.stubGlobal("WebSocket", FakeWebSocket);
        const requestQueueRefresh = vi.fn();
        renderHook(() => initializeQueueHistoryWebsocket(
            {} as QueueEvents,
            {} as HistoryEvents,
            requestQueueRefresh));

        expect(FakeWebSocket.instances).toHaveLength(1);
        act(() => FakeWebSocket.instances[0].open());

        expectSubscriptionBeforeRefresh(FakeWebSocket.instances[0], requestQueueRefresh, 1);

        act(() => FakeWebSocket.instances[0].disconnect());
        act(() => vi.advanceTimersByTime(1_000));
        expect(FakeWebSocket.instances).toHaveLength(2);

        act(() => FakeWebSocket.instances[1].open());

        expectSubscriptionBeforeRefresh(FakeWebSocket.instances[1], requestQueueRefresh, 2);
    });
});

function expectSubscriptionBeforeRefresh(
    socket: FakeWebSocket,
    requestQueueRefresh: ReturnType<typeof vi.fn>,
    expectedRefreshCount: number,
) {
    expect(socket.send).toHaveBeenCalledTimes(1);
    expect(JSON.parse(socket.send.mock.calls[0][0])).toEqual({
        qs: "state",
        qp: "state",
        qa: "event",
        qr: "event",
        ha: "event",
        hr: "event",
    });
    expect(requestQueueRefresh).toHaveBeenCalledTimes(expectedRefreshCount);
    expect(socket.send.mock.invocationCallOrder[0])
        .toBeLessThan(requestQueueRefresh.mock.invocationCallOrder[expectedRefreshCount - 1]);
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

    disconnect() {
        this.onclose?.(new CloseEvent("close"));
    }
}
