import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
    FRONTEND_WEBSOCKET_SERVER_OPTIONS,
    MAX_FRONTEND_TOPIC_KEY_BYTES,
    MAX_FRONTEND_TOPIC_MESSAGE_BYTES,
    MAX_FRONTEND_TOPICS,
} from "./websocket-policy";

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

const hostileDiagnostic = [
    "credential-marker",
    "/private/runtime/path",
    "https://user:password@example.invalid/provider",
    "provider-response",
    "\r\n\u001b[31m",
].join("|");

function renderConsoleCalls(calls: unknown[][]): string {
    return calls
        .flatMap(call => call.map(value => value instanceof Error
            ? `${value.message}\n${value.stack ?? ""}`
            : String(value)))
        .join("\n");
}

vi.mock("ws", () => ({ default: wsMock.FakeBackendSocket }));

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

    it("logs only stable backend error and close events while reconnecting", async () => {
        const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined);
        const infoSpy = vi.spyOn(console, "info").mockImplementation(() => undefined);
        const { initializeWebsocketClient } = await import("./websocket.server");
        const bridge = initializeWebsocketClient(new Map(), new Map(), () => true);

        bridge.ensureConnected();
        const first = wsMock.instances[0];
        first.onerror?.(new Error(hostileDiagnostic));
        first.readyState = first.CLOSED;
        first.onclose?.({ code: 1006, reason: hostileDiagnostic });
        vi.advanceTimersByTime(1000);

        const rendered = renderConsoleCalls([...errorSpy.mock.calls, ...infoSpy.mock.calls]);
        expect(rendered.includes(hostileDiagnostic)).toBe(false);
        expect(errorSpy.mock.calls[0]?.length).toBe(1);
        expect(errorSpy.mock.calls[0]?.[0]).toBe("Backend WebSocket transport error");
        expect(infoSpy.mock.calls.some(call => call[0] === "Backend WebSocket closed" && call[1] === 1006)).toBe(true);
        expect(wsMock.instances).toHaveLength(2);
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

    it("accepts an immediate subscription on an already authenticated connection", async () => {
        const { websocketServer } = await import("./websocket.server");
        let connectionHandler: ((socket: any, request: any) => void | Promise<void>) | undefined;
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
        const pendingConnection = connectionHandler?.(client, {});
        const messageHandlerWasInstalledSynchronously = typeof client.onmessage === "function";
        client.onmessage?.({ data: JSON.stringify({ qs: "state" }) });
        await pendingConnection;

        const backend = wsMock.instances[0];
        backend.readyState = backend.OPEN;
        backend.onmessage?.({
            data: JSON.stringify({ Topic: "qs", Message: "immediate" }),
        });

        expect(messageHandlerWasInstalledSynchronously).toBe(true);
        expect(client.send).toHaveBeenCalledWith(JSON.stringify({
            Topic: "qs",
            Message: "immediate",
        }));
    });

    it("observes an immediate close on an already authenticated connection", async () => {
        const { websocketServer } = await import("./websocket.server");
        let connectionHandler: ((socket: any, request: any) => void | Promise<void>) | undefined;
        const wss = {
            on: vi.fn((event: string, handler: typeof connectionHandler) => {
                if (event === "connection") connectionHandler = handler;
            }),
        };
        const client = {
            close: vi.fn(),
            onmessage: undefined as ((event: { data: string }) => void) | undefined,
            onclose: undefined as (() => void) | undefined,
        };

        websocketServer.initialize(wss as any);
        const pendingConnection = connectionHandler?.(client, {});
        const closeHandlerWasInstalledSynchronously = typeof client.onclose === "function";
        client.onclose?.();
        await pendingConnection;

        expect(closeHandlerWasInstalledSynchronously).toBe(true);
        expect(wsMock.instances).toHaveLength(1);
        expect(wsMock.instances[0].close).toHaveBeenCalledWith(1000, "No frontend websocket clients");
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

        client.onmessage?.({ data: JSON.stringify({ hs: "event" }) });
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

    it.each([
        ["oversized message", "x".repeat(MAX_FRONTEND_TOPIC_MESSAGE_BYTES + 1)],
        ["multibyte oversized message", "é".repeat(Math.floor(MAX_FRONTEND_TOPIC_MESSAGE_BYTES / 2) + 1)],
        ["too many topics", JSON.stringify(Object.fromEntries(
            Array.from({ length: MAX_FRONTEND_TOPICS + 1 }, (_, index) => [`t${index}`, "state"]),
        ))],
        ["empty topic", JSON.stringify({ "": "state" })],
        ["oversized topic", JSON.stringify({ ["t".repeat(MAX_FRONTEND_TOPIC_KEY_BYTES + 1)]: "state" })],
        ["unsupported mode", JSON.stringify({ qs: "snapshot" })],
        ["unknown short topic", JSON.stringify({ zz: "state" })],
        ["retired STRM topic", JSON.stringify({ stp: "state" })],
        ["state topic with event mode", JSON.stringify({ qs: "event" })],
        ["event topic with state mode", JSON.stringify({ qa: "state" })],
        ["health event topic with state mode", JSON.stringify({ hs: "state" })],
        ["non-object payload", JSON.stringify(["qs", "state"])],
    ])("closes invalid frontend topic subscription: %s", async (_label, rawMessage) => {
        const { websocketServer } = await import("./websocket.server");
        let connectionHandler: ((socket: any, request: any) => Promise<void>) | undefined;
        const wss = {
            on: vi.fn((event: string, handler: typeof connectionHandler) => {
                if (event === "connection") connectionHandler = handler;
            }),
        };
        const client = {
            close: vi.fn(),
            onmessage: undefined as ((event: { data: string }) => void) | undefined,
            onclose: undefined as (() => void) | undefined,
        };

        websocketServer.initialize(wss as any);
        await connectionHandler?.(client, {});
        client.onmessage?.({ data: rawMessage });

        expect(client.close).toHaveBeenCalledWith(
            1003,
            "Could not process topic subscription. If recently updated, try refreshing the page.",
        );
    });

    it("enforces the UTF-8 message byte bound before parsing an otherwise valid JSON value", async () => {
        const { parseFrontendTopicSubscription } = await import("./websocket.server");
        const rawMessage = JSON.stringify({ qs: "é".repeat(MAX_FRONTEND_TOPIC_MESSAGE_BYTES / 2) });

        expect(Buffer.byteLength(rawMessage, "utf8")).toBeGreaterThan(MAX_FRONTEND_TOPIC_MESSAGE_BYTES);
        expect(() => parseFrontendTopicSubscription(rawMessage)).toThrow(
            "Websocket topic subscription exceeded its byte bound",
        );
    });

    it("accepts the exact closed frontend topic and mode contract", async () => {
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
            close: vi.fn(),
            send: vi.fn(),
            onmessage: undefined as ((event: { data: string }) => void) | undefined,
            onclose: undefined as (() => void) | undefined,
        };
        const topics = {
            cxs: "state",
            ctp: "state",
            qs: "state",
            qp: "state",
            hs: "event",
            hp: "event",
            qa: "event",
            qr: "event",
            ha: "event",
            hr: "event",
            uftbmp: "state",
        };

        websocketServer.initialize(wss as any);
        await connectionHandler?.(client, {});
        client.onmessage?.({ data: JSON.stringify(topics) });

        expect(client.close).not.toHaveBeenCalled();
        expect(MAX_FRONTEND_TOPICS).toBe(Object.keys(topics).length);
    });
});

describe("frontend websocket transport bounds", () => {
    it("uses explicit small receiver limits instead of ws defaults", () => {
        expect(FRONTEND_WEBSOCKET_SERVER_OPTIONS).toEqual({
            noServer: true,
            maxPayload: 16 * 1024,
            maxFragments: 16,
            maxBufferedChunks: 32,
        });
    });
});
