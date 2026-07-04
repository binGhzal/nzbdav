import { Form } from "react-router";
import type {
    FullStatusResponse,
    ProviderDiagnosticStatus,
    RepairRun,
    RepairStatusResponse,
    RepairWorkerQueue,
} from "~/clients/backend-client.server";
import styles from "./operations-status.module.css";

export type OperationsStatusProps = {
    fullStatus: FullStatusResponse | null;
    fullStatusError: string | null;
    repairStatus: RepairStatusResponse | null;
    repairStatusError: string | null;
    websocketState: "connecting" | "connected" | "disconnected";
    isActionSubmitting: boolean;
}

export function OperationsStatus({
    fullStatus,
    fullStatusError,
    repairStatus,
    repairStatusError,
    websocketState,
    isActionSubmitting,
}: OperationsStatusProps) {
    const activeRun = repairStatus?.active_run ?? null;
    const lastRun = repairStatus?.last_run ?? fullStatus?.repair_runs.last ?? null;
    const brokenFiles = repairStatus?.broken_files.length ?? fullStatus?.repair_runs.broken_files ?? 0;
    const hasActiveRun = activeRun?.status === "Running";
    const degradedMessages = getDegradedMessages(fullStatus, repairStatus, fullStatusError, repairStatusError, websocketState);

    return (
        <div className={styles.container}>
            {degradedMessages.length > 0 &&
                <div className={styles.bannerList}>
                    {degradedMessages.map(message =>
                        <div className={styles.banner} key={message}>{message}</div>
                    )}
                </div>
            }

            <div className={styles.grid}>
                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>Repair</h3>
                        <span className={styles.badge}>{activeRun?.stage ?? lastRun?.stage ?? "idle"}</span>
                    </div>
                    <div className={styles.metricGrid}>
                        <Metric label="Checked" value={lastRun?.checked ?? 0} />
                        <Metric label="Missing" value={lastRun?.missing ?? 0} tone={lastRun?.missing ? "danger" : undefined} />
                        <Metric label="Unknown" value={(lastRun?.unknown ?? 0) + (lastRun?.provider_errors ?? 0)} tone={lastRun?.provider_errors || lastRun?.unknown ? "warning" : undefined} />
                        <Metric label="Broken" value={brokenFiles} tone={brokenFiles ? "danger" : undefined} />
                    </div>
                    <RepairActions activeRun={activeRun} brokenFiles={brokenFiles} isActionSubmitting={isActionSubmitting} />
                </section>

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>Cache</h3>
                        <span className={styles.badge}>{fullStatus ? `${getCacheUsage(fullStatus)}% used` : "unknown"}</span>
                    </div>
                    <div className={styles.metricGrid}>
                        <Metric label="Bytes" value={fullStatus ? formatBytes(fullStatus.cache.bytes) : "unknown"} />
                        <Metric label="Hit Rate" value={fullStatus ? `${getCacheHitRate(fullStatus)}%` : "unknown"} />
                        <Metric label="Readers" value={fullStatus?.cache.active_readers ?? 0} />
                        <Metric label="Fetches" value={fullStatus?.cache.pending_fetches ?? 0} />
                    </div>
                </section>

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>Mount</h3>
                        <span className={styles.badge}>{fullStatus?.mount.state ?? "unknown"}</span>
                    </div>
                    <div className={styles.metricGrid}>
                        <Metric label="Type" value={fullStatus?.mount.type ?? "unknown"} />
                        <Metric label="Ready" value={fullStatus?.mount.ready ? "yes" : "no"} tone={fullStatus && !fullStatus.mount.ready ? "warning" : undefined} />
                        <Metric label="Active" value={fullStatus?.mount.active_operations ?? 0} />
                        <Metric label="Errors" value={fullStatus?.mount.fuse_errors ?? 0} tone={fullStatus?.mount.fuse_errors ? "danger" : undefined} />
                    </div>
                    {fullStatus?.mount.message &&
                        <div className={styles.mutedLine}>{fullStatus.mount.message}</div>
                    }
                    {fullStatus?.mount.directory &&
                        <div className={styles.mutedLine}>{fullStatus.mount.directory}</div>
                    }
                </section>

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>Providers</h3>
                        <span className={styles.badge}>{fullStatus?.provider_diagnostics.length ?? 0} configured</span>
                    </div>
                    <div className={styles.providerList}>
                        {(fullStatus?.provider_diagnostics ?? []).map(provider =>
                            <ProviderRow key={`${provider.host}:${provider.port}:${provider.priority}`} provider={provider} />
                        )}
                        {fullStatus && fullStatus.provider_diagnostics.length === 0 &&
                            <span className={styles.muted}>No providers reported.</span>
                        }
                    </div>
                </section>

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>Workers</h3>
                        <span className={styles.badge}>{fullStatus?.queue_status ?? "unknown"}</span>
                    </div>
                    <WorkerRows
                        verify={repairStatus?.verify_queue}
                        repair={repairStatus?.repair_queue}
                        fullStatus={fullStatus}
                    />
                </section>

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>ARR</h3>
                        <span className={styles.badge}>{fullStatus?.arr_prioritization.mode ?? "report"}</span>
                    </div>
                    <div className={styles.metricGrid}>
                        <Metric label="Correlated" value={fullStatus?.arr_prioritization.correlations ?? 0} />
                        <Metric label="Priority Hints" value={fullStatus?.arr_prioritization.active_hints ?? 0} />
                        <Metric label="Duplicates" value={fullStatus?.arr_prioritization.duplicates ?? 0} tone={fullStatus?.arr_prioritization.duplicates ? "warning" : undefined} />
                        <Metric label="Nudge Failures" value={fullStatus?.arr_search_nudge.failed ?? 0} tone={fullStatus?.arr_search_nudge.failed ? "danger" : undefined} />
                    </div>
                    <div className={styles.workerGrid}>
                        <WorkerRow
                            label="Search"
                            active={fullStatus?.arr_search_nudge.planned ?? 0}
                            ready={fullStatus?.arr_search_nudge.executed ?? 0}
                            retry={fullStatus?.arr_search_nudge.failed ?? 0}
                        />
                    </div>
                    {fullStatus?.arr_download_report.lifecycle_states.length ? (
                        <div className={styles.mutedLine}>
                            {fullStatus.arr_download_report.lifecycle_states
                                .map(x => `${x.state} ${x.count}`)
                                .join(" · ")}
                        </div>
                    ) : null}
                </section>
            </div>
        </div>
    );
}

function RepairActions({
    activeRun,
    brokenFiles,
    isActionSubmitting,
}: {
    activeRun: RepairRun | null;
    brokenFiles: number;
    isActionSubmitting: boolean;
}) {
    return (
        <Form method="post" className={styles.actions}>
            {activeRun &&
                <input type="hidden" name="runId" value={activeRun.id} />
            }
            <button
                className={styles.button}
                name="intent"
                value="start"
                type="submit"
                disabled={isActionSubmitting || activeRun?.status === "Running"}
            >
                Run Now
            </button>
            <button
                className={styles.button}
                name="intent"
                value="cancel"
                type="submit"
                disabled={isActionSubmitting || activeRun?.status !== "Running"}
            >
                Cancel
            </button>
            <button
                className={styles.button}
                name="intent"
                value="clear"
                type="submit"
                disabled={isActionSubmitting || brokenFiles === 0 || activeRun?.status === "Running"}
            >
                Clear
            </button>
        </Form>
    );
}

function WorkerRows({
    verify,
    repair,
    fullStatus,
}: {
    verify: RepairWorkerQueue | undefined;
    repair: RepairWorkerQueue | undefined;
    fullStatus: FullStatusResponse | null;
}) {
    return (
        <div className={styles.workerGrid}>
            <WorkerRow label="Download" active={fullStatus?.worker_queues.download_active ?? 0} ready={fullStatus?.worker_queues.download_ready ?? 0} retry={fullStatus?.worker_queues.download_retry ?? 0} />
            <WorkerRow label="Verify" active={verify?.leased ?? fullStatus?.worker_queues.verify_active ?? 0} ready={verify?.ready ?? fullStatus?.worker_queues.verify_ready ?? 0} retry={verify?.retry ?? fullStatus?.worker_queues.verify_retry ?? 0} />
            <WorkerRow label="Repair" active={repair?.leased ?? fullStatus?.worker_queues.repair_active ?? 0} ready={repair?.ready ?? fullStatus?.worker_queues.repair_ready ?? 0} retry={repair?.retry ?? fullStatus?.worker_queues.repair_retry ?? 0} />
        </div>
    );
}

function WorkerRow({ label, active, ready, retry }: { label: string; active: number; ready: number; retry: number }) {
    return (
        <div className={styles.workerRow}>
            <span className={styles.workerLabel}>{label}</span>
            <span>active {active}</span>
            <span>ready {ready}</span>
            <span>retry {retry}</span>
        </div>
    );
}

function ProviderRow({ provider }: { provider: ProviderDiagnosticStatus }) {
    return (
        <div className={styles.providerRow}>
            <div>
                <div className={styles.providerName}>{provider.host}:{provider.port}</div>
                <div className={styles.muted}>{provider.type} priority {provider.priority}</div>
            </div>
            <div className={styles.providerMeta}>
                <span>{provider.max_connections} connections</span>
                <span>{provider.ssl ? "SSL" : "Plain"}</span>
                <span>{provider.stat_pipelining_enabled ? "Pipelined" : "Serial"}</span>
            </div>
        </div>
    );
}

function Metric({ label, value, tone }: { label: string; value: string | number; tone?: "warning" | "danger" }) {
    return (
        <div className={styles.metric}>
            <span className={`${styles.metricValue} ${tone ? styles[tone] : ""}`}>{value}</span>
            <span className={styles.metricLabel}>{label}</span>
        </div>
    );
}

function getDegradedMessages(
    fullStatus: FullStatusResponse | null,
    repairStatus: RepairStatusResponse | null,
    fullStatusError: string | null,
    repairStatusError: string | null,
    websocketState: "connecting" | "connected" | "disconnected"
): string[] {
    const messages: string[] = [];
    if (websocketState === "disconnected") messages.push("Live health updates are disconnected.");
    if (fullStatusError) messages.push(fullStatusError);
    if (repairStatusError) messages.push(repairStatusError);
    if (fullStatus?.rclone_invalidations.failed) messages.push("Rclone invalidation failures need attention.");
    if (fullStatus?.rclone_invalidations.pending || fullStatus?.rclone_invalidations.ready) messages.push("Rclone invalidations are waiting to drain.");
    if (fullStatus?.mount.enabled && !fullStatus.mount.ready) messages.push("Mounted filesystem is not ready.");
    if (fullStatus?.mount.fuse_errors) messages.push("Mounted filesystem reported FUSE errors.");
    if (repairStatus?.broken_files.length) messages.push("Repair has broken files requiring operator review.");
    if (repairStatus?.verify_queue.quarantined || repairStatus?.repair_queue.quarantined) messages.push("Repair or verify jobs are quarantined.");
    if (fullStatus?.arr_prioritization.stale_correlations) messages.push("ARR queue correlations are stale.");
    if (fullStatus?.arr_prioritization.duplicates) messages.push("ARR duplicate download requests were detected.");
    if (fullStatus?.arr_search_nudge.failed) messages.push("ARR search nudge commands have failed.");
    return messages;
}

function getCacheUsage(fullStatus: FullStatusResponse) {
    if (fullStatus.cache.max_bytes <= 0) return 0;
    return Math.round((fullStatus.cache.bytes / fullStatus.cache.max_bytes) * 100);
}

function getCacheHitRate(fullStatus: FullStatusResponse) {
    const total = fullStatus.cache.hits + fullStatus.cache.misses;
    if (total <= 0) return 0;
    return Math.round((fullStatus.cache.hits / total) * 100);
}

function formatBytes(value: number) {
    if (value < 1024) return `${value} B`;
    const units = ["KiB", "MiB", "GiB", "TiB"];
    let current = value / 1024;
    for (const unit of units) {
        if (current < 1024) return `${current.toFixed(current >= 10 ? 0 : 1)} ${unit}`;
        current /= 1024;
    }
    return `${current.toFixed(1)} PiB`;
}
