import { useCallback, useEffect } from "react";
import type { HistoryEvents, QueueEvents } from "./events-controller";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
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
        return createReconnectingWebSocket({
            createSocket: () => new WebSocket(getWebsocketUrl()),
            onMessage: onWebsocketMessage,
            onOpen: socket => socket.send(JSON.stringify(topicSubscriptions)),
        });
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
