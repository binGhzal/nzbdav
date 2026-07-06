import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const wsMock = vi.hoisted(() => {
    const instances: FakeBackendSocket[] = [];

    class FakeBackendSocket {
        static CONNECTING = 0;
        static OPEN = 1;
        static CLOSED = 3;

        CONNECTING = FakeBackendSocket.CONNECTING;
        OPEN = FakeBackendSocket.OPEN;
        CLOSED = FakeBackendSocket.CLOSED;
        readyState = FakeBackendSocket.CONNECTING;
        onopen: (() => void) | null = null;
        onmessage: ((event: { data: string }) => void) | null = null;
        onclose: ((event: { code: number; reason: string }) => void) | null = null;
        onerror: ((error: Error) => void) | null = null;
        send = vi.fn();
        close = vi.fn();

        constructor(public url: string) {
            instances.push(this);
        }

        on(event: "error", handler: (error: Error) => void) {
            if (event === "error") this.onerror = handler;
            return this;
        }
    }

    return { FakeBackendSocket, instances };
});

vi.mock("ws", () => ({ default: wsMock.FakeBackendSocket }));
vi.mock("../app/auth/authentication.server", () => ({
    isAuthenticated: vi.fn(async () => true),
}));

describe("initializeWebsocketClient", () => {
    beforeEach(() => {
        vi.useFakeTimers();
        vi.resetModules();
        wsMock.instances.length = 0;
        vi.stubEnv("BACKEND_URL", "http://backend:8080");
        vi.stubEnv("FRONTEND_BACKEND_API_KEY", "frontend-key");
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.unstubAllEnvs();
        vi.restoreAllMocks();
    });

    it("ignores messages from stale backend sockets after reconnecting", async () => {
        const { initializeWebsocketClient } = await import("./websocket.server");
        const frontendClient = {
            OPEN: 1,
            readyState: 1,
            send: vi.fn(),
        };
        const subscriptions = new Map<string, Set<any>>([
            ["qs", new Set([frontendClient])],
        ]);
        const lastMessage = new Map<string, string>();

        const bridge = initializeWebsocketClient(subscriptions, lastMessage, () => true);
        bridge.ensureConnected();
        const first = wsMock.instances[0];
        first.readyState = first.OPEN;
        first.onopen?.();
        first.readyState = first.CLOSED;
        first.onclose?.({ code: 1006, reason: "network drop" });

        vi.advanceTimersByTime(1000);
        expect(wsMock.instances).toHaveLength(2);
        const second = wsMock.instances[1];

        first.onmessage?.({
            data: JSON.stringify({ Topic: "qs", Message: "stale" }),
        });

        expect(frontendClient.send).not.toHaveBeenCalled();
        expect(lastMessage.has("qs")).toBe(false);

        second.onmessage?.({
            data: JSON.stringify({ Topic: "qs", Message: "fresh" }),
        });

        expect(frontendClient.send).toHaveBeenCalledTimes(1);
        expect(frontendClient.send).toHaveBeenCalledWith(JSON.stringify({
            Topic: "qs",
            Message: "fresh",
        }));
        expect(lastMessage.get("qs")).toBe(JSON.stringify({
            Topic: "qs",
            Message: "fresh",
        }));
    });
});

describe("websocketServer.initialize", () => {
    beforeEach(() => {
        vi.useFakeTimers();
        vi.resetModules();
        wsMock.instances.length = 0;
        vi.stubEnv("BACKEND_URL", "http://backend:8080");
        vi.stubEnv("FRONTEND_BACKEND_API_KEY", "frontend-key");
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.unstubAllEnvs();
        vi.restoreAllMocks();
    });

    it("removes stale topic subscriptions when a frontend socket resubscribes", async () => {
        const { websocketServer } = await import("./websocket.server");
        let connectionHandler: ((socket: any, request: any) => Promise<void>) | undefined;
        const wss = {
            on: vi.fn((event: string, handler: typeof connectionHandler) => {
                if (event === "connection") connectionHandler = handler;
            }),
        };
        const client = {
            OPEN: 1,
            readyState: 1,
            send: vi.fn(),
            close: vi.fn(),
            onmessage: undefined as ((event: { data: string }) => void) | undefined,
            onclose: undefined as (() => void) | undefined,
        };

        websocketServer.initialize(wss as any);
        await connectionHandler?.(client, {});
        client.onmessage?.({ data: JSON.stringify({ qs: "state" }) });
        const backend = wsMock.instances[0];
        backend.readyState = backend.OPEN;
        backend.onopen?.();

        backend.onmessage?.({
            data: JSON.stringify({ Topic: "qs", Message: "visible" }),
        });
        expect(client.send).toHaveBeenCalledTimes(1);

        client.onmessage?.({ data: JSON.stringify({ hs: "state" }) });
        backend.onmessage?.({
            data: JSON.stringify({ Topic: "qs", Message: "stale" }),
        });
        backend.onmessage?.({
            data: JSON.stringify({ Topic: "hs", Message: "current" }),
        });

        expect(client.send).toHaveBeenCalledTimes(2);
        expect(client.send).toHaveBeenLastCalledWith(JSON.stringify({
            Topic: "hs",
            Message: "current",
        }));
    });
});
