import WebSocket, { type WebSocketServer } from 'ws';
import { isAuthenticated } from "../app/auth/authentication.server";
import type { IncomingMessage } from 'http';
import { createReconnectBackoff, parseTopicMessage } from "../app/utils/websocket-util";

type FrontendTopicSubscription = Record<string, string>;

function initializeWebsocketServer(wss: WebSocketServer) {
    // keep track of socket subscriptions
    const clients = new Set<WebSocket>();
    const websockets = new Map<WebSocket, FrontendTopicSubscription>();
    const subscriptions = new Map<string, Set<WebSocket>>();
    const lastMessage = new Map<string, string>();
    const backendWebsocket = initializeWebsocketClient(
        subscriptions,
        lastMessage,
        () => clients.size > 0
    );

    // authenticate new websocket sessions
    wss.on("connection", async (ws: WebSocket, request: IncomingMessage) => {
        try {
            // ensure user is logged in
            if (!await isAuthenticated(request)) {
                ws.close(1008, "Unauthorized");
                return;
            }

            clients.add(ws);
            backendWebsocket.ensureConnected();

            // handle topic subscription
            ws.onmessage = (event: WebSocket.MessageEvent) => {
                try {
                    const topics = parseFrontendTopicSubscription(event.data.toString());
                    removeSocketSubscriptions(ws, websockets.get(ws), subscriptions);
                    websockets.set(ws, topics);
                    for (const [topic, mode] of Object.entries(topics)) {
                        const topicSubscriptions = subscriptions.get(topic);
                        if (topicSubscriptions) topicSubscriptions.add(ws);
                        else subscriptions.set(topic, new Set<WebSocket>([ws]));
                        if (mode === 'state') {
                            const messageToSend = lastMessage.get(topic);
                            if (messageToSend) ws.send(messageToSend);
                        }
                    }
                } catch {
                    ws.close(1003, "Could not process topic subscription. If recently updated, try refreshing the page.");
                }
            };

            // unsubscribe from topics
            ws.onclose = () => {
                clients.delete(ws);
                removeSocketSubscriptions(ws, websockets.get(ws), subscriptions);
                websockets.delete(ws);
                backendWebsocket.disconnectIfIdle();
            };
        } catch (error) {
            console.error("Error authenticating websocket session:", error);
            ws.close(1011, "Internal server error");
            return;
        }
    });
}

function parseFrontendTopicSubscription(rawMessage: string): FrontendTopicSubscription {
    const topics = JSON.parse(rawMessage);
    if (!topics || typeof topics !== "object" || Array.isArray(topics)) {
        throw new Error("Expected websocket topic subscription object");
    }

    const normalized: FrontendTopicSubscription = {};
    for (const [topic, mode] of Object.entries(topics)) {
        if (!topic || typeof mode !== "string") {
            throw new Error("Expected websocket topic modes to be strings");
        }
        normalized[topic] = mode;
    }
    return normalized;
}

function removeSocketSubscriptions(
    ws: WebSocket,
    topics: FrontendTopicSubscription | undefined,
    subscriptions: Map<string, Set<WebSocket>>
) {
    if (!topics) return;

    for (const topic of Object.keys(topics)) {
        const topicSubscriptions = subscriptions.get(topic);
        if (!topicSubscriptions) continue;

        topicSubscriptions.delete(ws);
        if (topicSubscriptions.size === 0) subscriptions.delete(topic);
    }
}

export function initializeWebsocketClient(
    subscriptions: Map<string, Set<WebSocket>>,
    lastMessage: Map<string, string>,
    shouldStayConnected: () => boolean
) {
    let reconnectTimeout: NodeJS.Timeout | null = null;
    let socket: WebSocket | null = null;
    const url = getBackendWebsocketUrl();
    const reconnectBackoff = createReconnectBackoff();

    function connect() {
        if (!shouldStayConnected()) return;
        if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;

        const newSocket = new WebSocket(url);
        socket = newSocket;

        newSocket.on('error', (error: Error) => {
            if (!isCurrentSocket(newSocket)) return;
            console.error('WebSocket error:', error.message);
        });

        newSocket.onopen = () => {
            if (!isCurrentSocket(newSocket)) return;
            console.info("WebSocket connected");
            if (reconnectTimeout) {
                clearTimeout(reconnectTimeout);
                reconnectTimeout = null;
            }
            reconnectBackoff.reset();

            newSocket.send(Buffer.from(process.env.FRONTEND_BACKEND_API_KEY!, "utf-8"), { binary: false });
        };

        newSocket.onmessage = (event: WebSocket.MessageEvent) => {
            if (!isCurrentSocket(newSocket)) return;
            var rawMessage = event.data.toString();
            var topicMessage = parseTopicMessage(rawMessage);
            if (!topicMessage) {
                console.warn("Ignored invalid backend websocket payload");
                return;
            }
            var { topic } = topicMessage;
            lastMessage.set(topic, rawMessage);
            var subscribed = subscriptions.get(topic) || [];
            subscribed.forEach(client => {
                if (client.readyState === client.OPEN) {
                    client.send(rawMessage);
                }
            });
        };

        newSocket.onclose = (event: WebSocket.CloseEvent) => {
            if (!isCurrentSocket(newSocket)) return;
            console.info(`WebSocket closed (code: ${event.code}, reason: ${event.reason})`);
            socket = null;
            if (shouldStayConnected()) scheduleReconnect();
        };
    }

    function isCurrentSocket(candidate: WebSocket) {
        return socket === candidate;
    }

    function scheduleReconnect() {
        if (!shouldStayConnected()) return;
        if (reconnectTimeout) clearTimeout(reconnectTimeout);

        reconnectTimeout = setTimeout(() => {
            console.info(`WebSocket reconnecting...`);
            connect();
        }, reconnectBackoff.nextDelayMs());
    }

    function disconnectIfIdle() {
        if (shouldStayConnected()) return;
        if (reconnectTimeout) {
            clearTimeout(reconnectTimeout);
            reconnectTimeout = null;
        }
        if (socket) {
            const socketToClose = socket;
            socket = null;
            socketToClose.close(1000, "No frontend websocket clients");
        }
    }

    return {
        ensureConnected: connect,
        disconnectIfIdle,
    };
}

function getBackendWebsocketUrl() {
    const host = process.env.BACKEND_URL!;
    return `${host.replace(/\/$/, '')}/ws`.replace(/^http/, 'ws');
}

export const websocketServer = {
    initialize: initializeWebsocketServer
}
