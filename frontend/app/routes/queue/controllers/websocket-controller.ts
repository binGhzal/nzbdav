import { useCallback, useEffect } from "react";
import type { HistoryEvents, QueueEvents } from "./events-controller";
import { receiveMessage } from "~/utils/websocket-util";
import { getWebsocketUrl } from "~/utils/url-base";
import type { HistorySlot, QueueSlot } from "~/clients/backend-client.server";

const topicNames = {
    queueItemStatus: 'qs',
    queueItemPercentage: 'qp',
    queueItemAdded: 'qa',
    queueItemRemoved: 'qr',
    historyItemAdded: 'ha',
    historyItemRemoved: 'hr',
};

const topicSubscriptions = {
    [topicNames.queueItemStatus]: 'state',
    [topicNames.queueItemPercentage]: 'state',
    [topicNames.queueItemAdded]: 'event',
    [topicNames.queueItemRemoved]: 'event',
    [topicNames.historyItemAdded]: 'event',
    [topicNames.historyItemRemoved]: 'event',
};

export function initializeQueueHistoryWebsocket(
    queueEvents: QueueEvents,
    historyEvents: HistoryEvents,
) {
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.queueItemAdded) {
            const queueSlot = parseJsonMessage<QueueSlot>(message);
            if (queueSlot) queueEvents.onAddQueueSlot(queueSlot);
        }
        else if (topic == topicNames.queueItemRemoved)
            queueEvents.onRemoveQueueSlots(new Set<string>(message.split(',')));
        else if (topic == topicNames.queueItemStatus)
            queueEvents.onChangeQueueSlotStatus(message);
        else if (topic == topicNames.queueItemPercentage)
            queueEvents.onChangeQueueSlotPercentage(message);
        else if (topic == topicNames.historyItemAdded) {
            const historySlot = parseJsonMessage<HistorySlot>(message);
            if (historySlot) historyEvents.onAddHistorySlot(historySlot);
        }
        else if (topic == topicNames.historyItemRemoved)
            historyEvents.onRemoveHistorySlots(new Set<string>(message.split(',')));
    }, [
        queueEvents,
        historyEvents,
    ]);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let reconnectDelayMs = 1000;
        let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
        function connect() {
            ws = new WebSocket(getWebsocketUrl());
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => {
                reconnectDelayMs = 1000;
                ws.send(JSON.stringify(topicSubscriptions));
            }
            ws.onclose = () => {
                if (disposed) return;
                reconnectTimer = setTimeout(() => connect(), reconnectDelayMs);
                reconnectDelayMs = Math.min(reconnectDelayMs * 2, 30000);
            };
            ws.onerror = () => { ws.close() };
            return () => {
                disposed = true;
                if (reconnectTimer) clearTimeout(reconnectTimer);
                ws.close();
            }
        }

        return connect();
    }, [onWebsocketMessage]);
}

function parseJsonMessage<T>(message: string): T | null {
    try {
        return JSON.parse(message) as T;
    } catch (error) {
        console.warn("Ignored invalid websocket message body", error);
        return null;
    }
}
