export type TopicMessage = {
    topic: string;
    message: string;
};

export function parseTopicMessage(data: unknown): TopicMessage | null {
    try {
        const parsed = JSON.parse(String(data));
        if (typeof parsed?.Topic !== "string" || parsed.Topic.length === 0) return null;
        if (typeof parsed?.Message !== "string" || parsed.Message.length === 0) return null;
        return {
            topic: parsed.Topic,
            message: parsed.Message,
        };
    } catch {
        return null;
    }
}

export function receiveMessage(
    onMessage: (topic: string, message: string) => void
): (event: MessageEvent) => void {
    return (event) => {
        const parsed = parseTopicMessage(event.data);
        if (!parsed) {
            console.warn("Ignored invalid websocket payload");
            return;
        }

        onMessage(parsed.topic, parsed.message);
    }
}

export type ReconnectBackoffOptions = {
    initialDelayMs?: number;
    maxDelayMs?: number;
};

export function createReconnectBackoff(options: ReconnectBackoffOptions = {}) {
    const initialDelayMs = Math.max(1, options.initialDelayMs ?? 1000);
    const maxDelayMs = Math.max(initialDelayMs, options.maxDelayMs ?? 30000);
    let currentDelayMs = initialDelayMs;

    return {
        nextDelayMs() {
            const delayMs = currentDelayMs;
            currentDelayMs = Math.min(currentDelayMs * 2, maxDelayMs);
            return delayMs;
        },
        reset() {
            currentDelayMs = initialDelayMs;
        },
    };
}

export type ReconnectingWebSocketOptions = {
    createSocket: () => WebSocket;
    onMessage: (topic: string, message: string) => void;
    onOpen?: (socket: WebSocket) => void;
    onClose?: (event: CloseEvent, socket: WebSocket) => void;
    onError?: (event: Event, socket: WebSocket) => void;
    backoff?: ReturnType<typeof createReconnectBackoff>;
};

export function createReconnectingWebSocket(options: ReconnectingWebSocketOptions): () => void {
    const reconnectBackoff = options.backoff ?? createReconnectBackoff();
    let currentSocket: WebSocket | undefined;
    let disposed = false;
    let reconnectTimer: ReturnType<typeof setTimeout> | undefined;

    function clearReconnectTimer() {
        if (reconnectTimer === undefined) return;
        clearTimeout(reconnectTimer);
        reconnectTimer = undefined;
    }

    function isCurrent(socket: WebSocket) {
        return !disposed && socket === currentSocket;
    }

    function connect() {
        const socket = options.createSocket();
        currentSocket = socket;
        const messageHandler = receiveMessage(options.onMessage);
        socket.onmessage = event => {
            if (!isCurrent(socket)) return;
            messageHandler(event);
        };
        socket.onopen = () => {
            if (!isCurrent(socket)) return;
            reconnectBackoff.reset();
            options.onOpen?.(socket);
        };
        socket.onclose = event => {
            if (!isCurrent(socket)) return;
            options.onClose?.(event, socket);
            clearReconnectTimer();
            reconnectTimer = setTimeout(connect, reconnectBackoff.nextDelayMs());
        };
        socket.onerror = event => {
            if (!isCurrent(socket)) return;
            options.onError?.(event, socket);
            socket.close();
        };
    }

    connect();

    return () => {
        disposed = true;
        clearReconnectTimer();
        const socket = currentSocket;
        currentSocket = undefined;
        socket?.close();
    };
}
