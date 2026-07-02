export function receiveMessage(
    onMessage: (topic: string, message: string) => void
): (event: MessageEvent) => void {
    return (event) => {
        try {
            const parsed = JSON.parse(event.data);
            if (typeof parsed?.Topic !== "string" || typeof parsed?.Message !== "string") {
                console.warn("Ignored malformed websocket message", parsed);
                return;
            }

            onMessage(parsed.Topic, parsed.Message);
        } catch (error) {
            console.warn("Ignored invalid websocket payload", error);
        }
    }
}
