import { Alert, Button, Form } from "react-bootstrap";
import styles from "./recreate-strm-files.module.css";
import { useCallback, useEffect, useState } from "react";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { getWebsocketUrl } from "~/utils/url-base";
import { startMaintenanceTask } from "../start-maintenance-task";
import { useMaintenanceRun } from "../use-maintenance-run";

const cleanupTaskTopic = { 'crst': 'state' };

export function RecreateStrmFiles() {
    // stateful variables
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [requestError, setRequestError] = useState<string | null>(null);
    const { acceptRun, isActive, progressMessage, refresh } = useMaintenanceRun("recreate-strm-files");

    // derived variables
    const isRunning = isFetching || isActive;
    const isRunButtonEnabled = !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Running.." : '▶ Run Task';

    // effects
    useEffect(() => {
        return createReconnectingWebSocket({
            createSocket: () => new WebSocket(getWebsocketUrl()),
            onMessage: () => void refresh(),
            onOpen: socket => {
                socket.send(JSON.stringify(cleanupTaskTopic));
            },
            onClose: () => undefined,
        });
    }, [refresh]);

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        try {
            const run = await startMaintenanceTask(
                "/api/recreate-strm-files",
                "recreate STRM files",
                setRequestError);
            if (run) acceptRun(run);
        } finally {
            setIsFetching(false);
        }
    }, [acceptRun]);

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
                            {progressMessage}
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
