import { Button, Form, InputGroup, Spinner } from "react-bootstrap";
import styles from "./rclone.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";
import { withUrlBase } from "~/utils/url-base";
import { getHttpErrorMessage, readJsonObjectOrEmpty } from "~/utils/http-response";
import { areEndpointIdentitiesEquivalent } from "~/utils/endpoint-identity";

type RcloneSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RcloneSettings({ config, setNewConfig }: RcloneSettingsProps) {
    const [connectionState, setConnectionState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const [connectionError, setConnectionError] = useState<string | null>(null);

    useEffect(() => {
        setConnectionState('idle');
        setConnectionError(null);
    }, [config["rclone.host"], config["rclone.user"], config["rclone.pass"], config["rclone.fs"]]);

    const testConnection = useCallback(async () => {
        const host = config["rclone.host"];
        if (!host?.trim()) {
            return;
        }

        setConnectionState('testing');
        setConnectionError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('user', config["rclone.user"] ?? '');
            formData.append('pass', config["rclone.pass"] ?? '');
            formData.append('fs', config["rclone.fs"] ?? '');

            const response = await fetch(withUrlBase('/api/test-rclone-connection'), {
                method: 'POST',
                body: formData
            });

            if (!response.ok) {
                setConnectionState('error');
                setConnectionError(`Rclone connection failed: ${await getHttpErrorMessage(response)}`);
                return;
            }

            const result = await readJsonObjectOrEmpty(response);

            if (result.status && result.connected) {
                setConnectionState('success');
                setConnectionError(null);
            } else {
                setConnectionState('error');
                setConnectionError("Rclone connection failed.");
            }
        } catch (error) {
            setConnectionState('error');
            setConnectionError(`Rclone connection failed: ${error instanceof Error ? error.message : "unknown error"}.`);
        }
    }, [config]);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="rclone-rc-enabled-checkbox"
                    aria-describedby="rclone-rc-enabled-help"
                    label={`Enable Rclone RC Server Notifications`}
                    checked={config["rclone.rc-enabled"] === "true"}
                    onChange={e => setNewConfig({ ...config, "rclone.rc-enabled": "" + e.target.checked })} />
                <Form.Text id="rclone-rc-enabled-help" muted>
                    When enabled, nzbdav will automatically notify your rclone mount via the RC API whenever files are added or removed on the webdav. This allows setting a high dir-cache-time setting on Rclone.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-host-input">Rclone Server Host</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        type="text"
                        id="rclone-host-input"
                        aria-describedby="rclone-host-help"
                        placeholder="http://localhost:5572"
                        value={config["rclone.host"]}
                        onChange={e => setNewConfig({ ...config, "rclone.host": e.target.value })} />
                    {config["rclone.host"]?.trim() && (
                        <Button
                            variant={connectionState === 'success' ? 'success' :
                                connectionState === 'error' ? 'danger' : 'secondary'}
                            onClick={testConnection}
                            disabled={connectionState === 'testing'}
                            className={styles.testButton}
                        >
                            {
                                connectionState === 'testing' ? (
                                    <Spinner animation="border" size="sm" />
                                ) : connectionState === 'success' ? (
                                    '✓'
                                ) : connectionState === 'error' ? (
                                    '✗'
                                ) : (
                                    'Test Conn'
                                )
                            }
                        </Button>
                    )}
                </InputGroup>
                <Form.Text id="rclone-host-help" muted>
                    The host address of the rclone RC API.
                </Form.Text>
                {connectionError && <div className={styles.alert} role="alert">{connectionError}</div>}
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-fs-input">Rclone VFS Selector</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="rclone-fs-input"
                    aria-describedby="rclone-fs-help"
                    placeholder="nzbdav:"
                    value={config["rclone.fs"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "rclone.fs": e.target.value })} />
                <Form.Text id="rclone-fs-help" muted>
                    Optional for a single VFS; required when the RC server has more than one active VFS. Use the name returned by rclone&apos;s vfs/list command.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-user-input">Rclone Server User</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="rclone-user-input"
                    aria-describedby="rclone-user-help"
                    value={config["rclone.user"]}
                    onChange={e => setNewConfig({ ...config, "rclone.user": e.target.value })} />
                <Form.Text id="rclone-user-help" muted>
                    The username for authenticating to the rclone RC API. This field is optional.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-pass-input">Rclone Server Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="rclone-pass-input"
                    aria-describedby="rclone-pass-help"
                    value={config["rclone.pass"]}
                    onChange={e => setNewConfig({ ...config, "rclone.pass": e.target.value })} />
                <Form.Text id="rclone-pass-help" muted>
                    The password for authenticating to the rclone RC API. This field is optional.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isRcloneSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return normalizeBoolean(config["rclone.rc-enabled"]) !== normalizeBoolean(newConfig["rclone.rc-enabled"])
        || !areEndpointIdentitiesEquivalent(config["rclone.host"], newConfig["rclone.host"])
        || normalizeOptionalCredential(config["rclone.user"]) !== normalizeOptionalCredential(newConfig["rclone.user"])
        || normalizeOptionalCredential(config["rclone.pass"]) !== normalizeOptionalCredential(newConfig["rclone.pass"])
        || normalizeOptionalSelector(config["rclone.fs"]) !== normalizeOptionalSelector(newConfig["rclone.fs"]);
}

function normalizeBoolean(value: string | undefined): string {
    return (value ?? "").toLowerCase();
}

function normalizeOptionalCredential(value: string | undefined): string {
    return (value ?? "").trim() === "" ? "" : value ?? "";
}

function normalizeOptionalSelector(value: string | undefined): string {
    return (value ?? "").trim();
}
