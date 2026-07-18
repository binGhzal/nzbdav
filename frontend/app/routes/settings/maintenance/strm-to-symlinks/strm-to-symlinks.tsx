import { Alert, Button, Form } from "react-bootstrap";
import styles from "./strm-to-symlinks.module.css";
import { useCallback, useEffect, useState } from "react";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { getWebsocketUrl, withUrlBase } from "~/utils/url-base";
import { startMaintenanceTask } from "../start-maintenance-task";
import { useMaintenanceRun } from "../use-maintenance-run";

const cleanupTaskTopic = { 'st2sy': 'state' };

type ConvertStrmToSymlinksProps = {
    savedConfig: Record<string, string>
};

export function ConvertStrmToSymlinks({ savedConfig }: ConvertStrmToSymlinksProps) {
    // stateful variables
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [requestError, setRequestError] = useState<string | null>(null);
    const { acceptRun, isActive, progressMessage, refresh } =
        useMaintenanceRun("convert-strm-to-symlinks");

    // derived variables
    const libraryDir = savedConfig["media.library-dir"];
    const isRunning = isFetching || isActive;
    const isRunButtonEnabled = !!libraryDir && !isRunning;
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
                "/api/convert-strm-to-symlinks",
                "convert STRM files to symlinks",
                setRequestError);
            if (run) acceptRun(run);
        } finally {
            setIsFetching(false);
        }
    }, [acceptRun]);

    return (
        <>
            {!libraryDir &&
                <Alert className={styles.alert} variant="warning">
                    Warning
                    <ul className={styles.list}>
                        <li className={styles["list-item"]}>
                            You must first configure the Library Directory setting before running this task.
                            Head over to the Repairs tab.
                        </li>
                    </ul>
                </Alert>
            }
            {libraryDir &&
                <Alert className={styles.alert} variant="danger">
                    <span style={{ fontWeight: 'bold' }}>Danger</span>
                    <ul className={styles.list}>
                        <li className={styles["list-item"]}>
                            Make a backup of your entire Library Dir prior to running this task
                        </li>
                        <li className={styles["list-item"]}>
                            Strm files will be deleted from `{libraryDir}` and will not be recoverable without a backup.
                        </li>
                    </ul>
                </Alert>
            }
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
                        This task will scan your organized media library for all *.strm files.
                        Every *.strm file that links to nzbdav media will be deleted and be replaced by a symlink.
                        The newly created symlinks will all point to the corresponding file within your rclone mount.
                    </Form.Text>
                </Form.Group>
            </div>
        </>
    );
}
