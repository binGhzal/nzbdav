import { Alert, Button, Form } from "react-bootstrap";
import styles from "./recreate-strm-files.module.css";
import { useCallback, useEffect, useState } from "react";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { getWebsocketUrl } from "~/utils/url-base";
import { startMaintenanceTask } from "../start-maintenance-task";

const cleanupTaskTopic = { 'crst': 'state' };

export function RecreateStrmFiles() {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [requestError, setRequestError] = useState<string | null>(null);

    // derived variables
    const isFinished = progress?.startsWith("Done") || progress?.startsWith("Failed");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Running.." : '▶ Run Task';

    // effects
    useEffect(() => {
        return createReconnectingWebSocket({
            createSocket: () => new WebSocket(getWebsocketUrl()),
            onMessage: (_, message) => setProgress(message),
            onOpen: socket => {
                setConnected(true);
                socket.send(JSON.stringify(cleanupTaskTopic));
            },
            onClose: () => {
                setConnected(false);
                setProgress(null);
            },
        });
    }, [setProgress, setConnected]);

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        try {
            await startMaintenanceTask("/api/recreate-strm-files", "recreate STRM files", setRequestError);
        } finally {
            setIsFetching(false);
        }
    }, [setIsFetching, setRequestError]);

    return (
        <>
            {requestError &&
                <Alert className={styles.alert} variant="danger">
                    {requestError}
                </Alert>
            }
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
