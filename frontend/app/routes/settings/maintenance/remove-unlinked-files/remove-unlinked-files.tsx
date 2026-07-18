import { Alert, Button, Form } from "react-bootstrap";
import styles from "./remove-unlinked-files.module.css"
import { useCallback, useEffect, useState } from "react";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { getWebsocketUrl, withUrlBase } from "~/utils/url-base";
import { startMaintenanceTask } from "../start-maintenance-task";
import { useMaintenanceRun } from "../use-maintenance-run";

const cleanupTaskTopic = { 'ctp': 'state' };

type RemoveUnlinkedFilesProps = {
    savedConfig: Record<string, string>
};

export function RemoveUnlinkedFiles({ savedConfig }: RemoveUnlinkedFilesProps) {
    // stateful variables
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [requestError, setRequestError] = useState<string | null>(null);
    const { acceptRun, isActive, progressMessage: durableProgress, refresh, visibleRun } =
        useMaintenanceRun(
            "remove-unlinked-files",
            ["remove-unlinked-files", "remove-unlinked-files-dry-run"],
            true);
    const progressMessage = durableProgress?.replace('Dry Run - ', '');

    // derived variables
    const libraryDir = savedConfig["media.library-dir"];
    const isDone = visibleRun?.status === "completed" && progressMessage?.startsWith("Done");
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
                "/api/remove-unlinked-files",
                "remove unlinked files",
                setRequestError);
            if (run) acceptRun(run);
        } finally {
            setIsFetching(false);
        }
    }, [acceptRun]);

    const onDryRun = useCallback(async () => {
        setIsFetching(true);
        try {
            const run = await startMaintenanceTask(
                "/api/remove-unlinked-files/dry-run",
                "remove unlinked files dry run",
                setRequestError);
            if (run) acceptRun(run);
        } finally {
            setIsFetching(false);
        }
    }, [acceptRun]);

    // view
    const dryRunButton =
        <Button
            className={styles["dryrun-button"]}
            disabled={!isRunButtonEnabled}
            onClick={onDryRun}
            variant="secondary"
            size="sm"
        >
            perform a dry-run
        </Button>;

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
                            Make a backup of your NzbDAV database prior to running this task
                        </li>
                        <li className={styles["list-item"]}>
                            Files will be removed from the webdav and will not be recoverable without a backup
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
                            {durableProgress}
                            {isDone && <>
                                &nbsp;<a href={withUrlBase("/api/remove-unlinked-files/audit")}>Audit.</a>
                            </>}
                        </div>
                    </div>
                    <Form.Text id="cleanup-task-progress-help" muted>
                        <br />
                        This task will scan your organized media library for all symlinked or *.strm linked files.
                        Any file on the webdav that is not pointed to by your library will be deleted.
                        If you would like to see what would be deleted without running the task, you can {dryRunButton}.
                        The dry-run will not delete anything.
                        <br />
                        <br />
                        Note: Files still present in the History table will not be removed when running this task.
                        It is assumed that files still present in the History table have not yet been imported by Arrs
                        and they are expected to not yet have a corresponding symlink/strm in the Library folder.
                        These files will remain intact until Arrs have a chance to process them and remove them from the
                        History table.
                    </Form.Text>
                </Form.Group>
            </div>
        </>
    );
}
