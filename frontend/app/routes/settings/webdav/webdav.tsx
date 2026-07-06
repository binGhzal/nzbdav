import { Form, InputGroup } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction, useState } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

const MAX_CONCURRENT_QUEUE_DOWNLOADS = 128;
const MAX_STREAMING_CONNECTIONS = 256;
const MAX_TOTAL_STREAMING_CONNECTIONS = 512;

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    const [showAdvancedThroughput, setShowAdvancedThroughput] = useState(false);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-download-connections-input">Max Download Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && styles.error])}
                    type="text"
                    id="max-download-connections-input"
                    aria-describedby="max-download-connections-help"
                    placeholder="15"
                    value={config["usenet.max-download-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                <Form.Text id="max-download-connections-help" muted>
                    Manual fallback for article downloads when adaptive sizing is off or provider capacity is unavailable.
                    With adaptive sizing on, NZBDav uses pooled provider capacity and runtime pressure instead.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="adaptive-connections-enabled-checkbox"
                    aria-describedby="adaptive-connections-enabled-help"
                    label={`Adaptive Download Connections`}
                    checked={config["usenet.adaptive-connections-enabled"] === "true"}
                    onChange={e => setNewConfig({ ...config, "usenet.adaptive-connections-enabled": "" + e.target.checked })} />
                <Form.Text id="adaptive-connections-enabled-help" muted>
                    Automatically sizes queue and streaming article connections from runtime pressure and provider capacity. Off by default.
                    Max Download Connections and queue fields below are manual fallbacks when adaptive sizing is off.
                </Form.Text>
            </Form.Group>
            <hr />
            <button
                type="button"
                className={styles.advancedToggle}
                aria-expanded={showAdvancedThroughput}
                onClick={() => setShowAdvancedThroughput(value => !value)}>
                {showAdvancedThroughput ? "Hide Advanced Throughput" : "Show Advanced Throughput"}
            </button>
            {showAdvancedThroughput && (
                <div className={styles.advancedSection}>
                    <Form.Group>
                        <Form.Label htmlFor="max-concurrent-queue-downloads-input">Concurrent Queue Downloads</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidMaxConcurrentQueueDownloads(config["queue.max-concurrent-downloads"]) && styles.error])}
                            type="text"
                            id="max-concurrent-queue-downloads-input"
                            aria-describedby="max-concurrent-queue-downloads-help"
                            placeholder="0"
                            value={config["queue.max-concurrent-downloads"]}
                            onChange={e => setNewConfig({ ...config, "queue.max-concurrent-downloads": e.target.value })} />
                        <Form.Text id="max-concurrent-queue-downloads-help" muted>
                            Maximum number of NZBs to process at the same time. Use 0 for automatic sizing.
                            Positive values are hard caps, including when adaptive sizing is enabled.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="max-concurrent-verify-input">Concurrent Verify Jobs</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidMaxConcurrentQueueDownloads(config["queue.max-concurrent-verify"]) && styles.error])}
                            type="text"
                            id="max-concurrent-verify-input"
                            aria-describedby="max-concurrent-verify-help"
                            placeholder="0"
                            value={config["queue.max-concurrent-verify"]}
                            onChange={e => setNewConfig({ ...config, "queue.max-concurrent-verify": e.target.value })} />
                        <Form.Text id="max-concurrent-verify-help" muted>
                            Maximum number of background verify jobs to run at once. Use 0 for automatic sizing.
                            Background verify uses the repair/check budget; fresh post-download verify can use the download check budget.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="max-concurrent-repair-input">Concurrent Repair Jobs</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidMaxConcurrentQueueDownloads(config["queue.max-concurrent-repair"]) && styles.error])}
                            type="text"
                            id="max-concurrent-repair-input"
                            aria-describedby="max-concurrent-repair-help"
                            placeholder="0"
                            value={config["queue.max-concurrent-repair"]}
                            onChange={e => setNewConfig({ ...config, "queue.max-concurrent-repair": e.target.value })} />
                        <Form.Text id="max-concurrent-repair-help" muted>
                            Maximum number of background repair jobs to run at once. Use 0 for automatic sizing.
                            This is independent from download and verify worker limits.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="file-processing-concurrency-input">File Processing Concurrency Per NZB</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidFileProcessingConcurrency(config["queue.file-processing-concurrency"]) && styles.error])}
                            type="text"
                            id="file-processing-concurrency-input"
                            aria-describedby="file-processing-concurrency-help"
                            placeholder="0"
                            value={config["queue.file-processing-concurrency"]}
                            onChange={e => setNewConfig({ ...config, "queue.file-processing-concurrency": e.target.value })} />
                        <Form.Text id="file-processing-concurrency-help" muted>
                            Number of file processors to run inside each active NZB. Use 0 for automatic sizing. When adaptive sizing is on, NZBDav sizes this from available download connections.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="article-cache-max-megabytes-input">Temporary Article Cache Limit</Form.Label>
                        <InputGroup className={styles.input}>
                            <Form.Control
                                className={!isValidArticleCacheMaxMegabytes(config["usenet.article-cache-max-megabytes"]) ? styles.error : undefined}
                                type="text"
                                id="article-cache-max-megabytes-input"
                                aria-describedby="article-cache-max-megabytes-help"
                                placeholder="256"
                                value={config["usenet.article-cache-max-megabytes"]}
                                onChange={e => setNewConfig({ ...config, "usenet.article-cache-max-megabytes": e.target.value })} />
                            <InputGroup.Text>MB</InputGroup.Text>
                        </InputGroup>
                        <Form.Text id="article-cache-max-megabytes-help" muted>
                            Total temporary decoded-article cache budget shared by active queue downloads.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="max-streaming-connections-input">Max Streaming Connections Per Stream</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidMaxStreamingConnections(config["usenet.max-streaming-connections"]) && styles.error])}
                            type="text"
                            id="max-streaming-connections-input"
                            aria-describedby="max-streaming-connections-help"
                            placeholder="0"
                            value={config["usenet.max-streaming-connections"]}
                            onChange={e => setNewConfig({ ...config, "usenet.max-streaming-connections": e.target.value })} />
                        <Form.Text id="max-streaming-connections-help" muted>
                            Maximum article connections one WebDAV stream can use. Use 0 for automatic sizing from the buffer and global connection limits.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="max-total-streaming-connections-input">Max Total Streaming Connections</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidMaxTotalStreamingConnections(config["usenet.max-total-streaming-connections"]) && styles.error])}
                            type="text"
                            id="max-total-streaming-connections-input"
                            aria-describedby="max-total-streaming-connections-help"
                            placeholder="0"
                            value={config["usenet.max-total-streaming-connections"]}
                            onChange={e => setNewConfig({ ...config, "usenet.max-total-streaming-connections": e.target.value })} />
                        <Form.Text id="max-total-streaming-connections-help" muted>
                            Total article connections shared by all WebDAV streams. Use 0 for automatic CPU-based sizing.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</Form.Label>
                        <InputGroup className={styles.input}>
                            <Form.Control
                                className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? styles.error : undefined}
                                type="text"
                                id="streaming-priority-input"
                                aria-describedby="streaming-priority-help"
                                placeholder="80"
                                value={config["usenet.streaming-priority"]}
                                onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                            <InputGroup.Text>%</InputGroup.Text>
                        </InputGroup>
                        <Form.Text id="streaming-priority-help" muted>
                            When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="article-buffer-size-input">Article Buffer Size</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && styles.error])}
                            type="text"
                            id="article-buffer-size-input"
                            aria-describedby="article-buffer-size-help"
                            placeholder="40"
                            value={config["usenet.article-buffer-size"]}
                            onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                        <Form.Text id="article-buffer-size-help" muted>
                            The number of articles to buffer ahead, per stream, when reading from the webdav.
                        </Form.Text>
                    </Form.Group>
                </div>
            )}
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.adaptive-connections-enabled"] !== newConfig["usenet.adaptive-connections-enabled"]
        || config["queue.max-concurrent-downloads"] !== newConfig["queue.max-concurrent-downloads"]
        || config["queue.max-concurrent-verify"] !== newConfig["queue.max-concurrent-verify"]
        || config["queue.max-concurrent-repair"] !== newConfig["queue.max-concurrent-repair"]
        || config["queue.file-processing-concurrency"] !== newConfig["queue.file-processing-concurrency"]
        || config["usenet.article-cache-max-megabytes"] !== newConfig["usenet.article-cache-max-megabytes"]
        || config["usenet.max-streaming-connections"] !== newConfig["usenet.max-streaming-connections"]
        || config["usenet.max-total-streaming-connections"] !== newConfig["usenet.max-total-streaming-connections"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidMaxConcurrentQueueDownloads(newConfig["queue.max-concurrent-downloads"])
        && isValidMaxConcurrentQueueDownloads(newConfig["queue.max-concurrent-verify"])
        && isValidMaxConcurrentQueueDownloads(newConfig["queue.max-concurrent-repair"])
        && isValidFileProcessingConcurrency(newConfig["queue.file-processing-concurrency"])
        && isValidArticleCacheMaxMegabytes(newConfig["usenet.article-cache-max-megabytes"])
        && isValidMaxStreamingConnections(newConfig["usenet.max-streaming-connections"])
        && isValidMaxTotalStreamingConnections(newConfig["usenet.max-total-streaming-connections"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"]);
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidMaxDownloadConnections(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidMaxConcurrentQueueDownloads(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= MAX_CONCURRENT_QUEUE_DOWNLOADS;
}

function isValidFileProcessingConcurrency(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 256;
}

function isValidArticleCacheMaxMegabytes(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidMaxStreamingConnections(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= MAX_STREAMING_CONNECTIONS;
}

function isValidMaxTotalStreamingConnections(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= MAX_TOTAL_STREAMING_CONNECTIONS;
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}
