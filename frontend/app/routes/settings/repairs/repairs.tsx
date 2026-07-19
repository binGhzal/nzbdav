import { Alert, Form } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";
import { parseArrConfig } from "../arrs/arr-config";

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"];
    const arrConfig = parseArrConfig(config["arr.instances"]);
    const areArrInstancesConfigured =
        (arrConfig.RadarrInstances?.length ?? 0) > 0 ||
        (arrConfig.SonarrInstances?.length ?? 0) > 0 ||
        (arrConfig.LidarrInstances?.length ?? 0) > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    var helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy linked items will trigger Arr remove-and-search when NZBDav can match the library item; otherwise NZBDav leaves the link in place and marks action needed."
        : "When enabled, usenet items will be continuously monitored for health. This setting can only be enabled once your Library-Directory and an Arr instance are configured.";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    label={`Enable Background Repairs`}
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })} />
                <Form.Text id="enable-repairs-help" muted>
                    {helpText}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="healthcheck-concurrency-input">Health Check Concurrency</Form.Label>
                <Form.Control
                    {...className([styles.input, !isPositiveInteger(config["repair.healthcheck-concurrency"]) && styles.error])}
                    type="text"
                    id="healthcheck-concurrency-input"
                    aria-describedby="healthcheck-concurrency-help"
                    placeholder="50"
                    value={config["repair.healthcheck-concurrency"]}
                    onChange={e => setNewConfig({ ...config, "repair.healthcheck-concurrency": e.target.value })} />
                <Form.Text id="healthcheck-concurrency-help" muted>
                    The maximum number of concurrent NNTP connections used for health check STAT commands.
                    Lower values reduce connection pressure on usenet providers during health checks.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="library-dir-input">Library Directory</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <Form.Text id="library-dir-help" muted>
                    The path to your organized media library that contains your imported symlinks.
                    Make sure this path is visible to your NzbDAV container.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["repair.healthcheck-concurrency"] !== newConfig["repair.healthcheck-concurrency"]
        || config["media.library-dir"] !== newConfig["media.library-dir"];
}

export function isRepairsSettingsValid(newConfig: Record<string, string>) {
    return isPositiveInteger(newConfig["repair.healthcheck-concurrency"]);
}
