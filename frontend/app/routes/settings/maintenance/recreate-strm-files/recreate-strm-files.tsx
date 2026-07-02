import { Button, Form } from "react-bootstrap";
import styles from "./recreate-strm-files.module.css";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { getWebsocketUrl, withUrlBase } from "~/utils/url-base";

const cleanupTaskTopic = { 'crst': 'state' };

export function RecreateStrmFiles() {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);

    // derived variables
    const isFinished = progress?.startsWith("Done") || progress?.startsWith("Failed");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Running.." : '▶ Run Task';

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let reconnectDelayMs = 1000;
        let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
        function connect() {
            ws = new WebSocket(getWebsocketUrl());
            ws.onmessage = receiveMessage((_, message) => setProgress(message));
            ws.onopen = () => {
                reconnectDelayMs = 1000;
                setConnected(true);
                ws.send(JSON.stringify(cleanupTaskTopic));
            }
            ws.onclose = () => {
                setConnected(false);
                setProgress(null);
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
    }, [setProgress, setConnected]);

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        await fetch(withUrlBase("/api/recreate-strm-files"));
        setIsFetching(false);
    }, [setIsFetching]);

    return (
        <>
            <div className={styles.task}>
                <Form.Group>
                    <div className={styles.run}>
                        <Button
                            className={styles["run-button"]}
                            variant={runButtonVariant}
                            onClick={onRun}
                            disabled={!isRunButtonEnabled}
                        >
                            {runButtonLabel}
                        </Button>
                        <div className={styles["task-progress"]}>
                            {progress}
                        </div>
                    </div>
                    <Form.Text id="cleanup-task-progress-help" muted>
                        <br />
                        This task will recreate *.strm files for all available media using the current settings.
                    </Form.Text>
                </Form.Group>
            </div>
        </>
    );
}
