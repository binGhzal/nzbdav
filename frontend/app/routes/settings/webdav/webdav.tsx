import { Form, InputGroup } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
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
                    The maximum number of connections that will be used for downloading articles from your usenet provider(s).
                    Configure this to the minimum number of connections that will fully saturate your server's bandwidth.
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
                    Automatically lowers queue and article connection limits while the runtime is under memory pressure.
                </Form.Text>
            </Form.Group>
            <hr />
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
                    Number of NZBs to process at the same time. Use 0 for automatic sizing based on download connections.
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
                    Number of file processors to run inside each active NZB. Use 0 for automatic CPU-based sizing.
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
                    Per active queue job limit for decoded article files kept in temporary storage.
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
    return Number.isInteger(num) && num >= 0 && num <= 16;
}

function isValidFileProcessingConcurrency(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 64;
}

function isValidArticleCacheMaxMegabytes(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidMaxStreamingConnections(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 64;
}

function isValidMaxTotalStreamingConnections(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 128;
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}
