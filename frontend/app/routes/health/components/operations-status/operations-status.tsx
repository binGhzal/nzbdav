import { Form } from "react-router";
import type {
    ArrCorrelationsResponse,
    ArrSearchNudgeCommandsResponse,
    ArrValidationResponse,
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
    arrValidation: ArrValidationResponse | null;
    arrValidationError: string | null;
    arrNudges: ArrSearchNudgeCommandsResponse | null;
    arrNudgesError: string | null;
    arrCorrelations: ArrCorrelationsResponse | null;
    arrCorrelationsError: string | null;
    websocketState: "connecting" | "connected" | "disconnected";
    isActionSubmitting: boolean;
}

export function OperationsStatus({
    fullStatus,
    fullStatusError,
    repairStatus,
    repairStatusError,
    arrValidation,
    arrValidationError,
    arrNudges,
    arrNudgesError,
    arrCorrelations,
    arrCorrelationsError,
    websocketState,
    isActionSubmitting,
}: OperationsStatusProps) {
    const activeRun = repairStatus?.active_run ?? null;
    const lastRun = repairStatus?.last_run ?? fullStatus?.repair_runs.last ?? null;
    const brokenFiles = repairStatus?.broken_files.length ?? fullStatus?.repair_runs.broken_files ?? 0;
    const hasActiveRun = activeRun?.status === "Running";
    const degradedMessages = getDegradedMessages(
        fullStatus,
        repairStatus,
        fullStatusError,
        repairStatusError,
        arrValidation,
        arrValidationError,
        arrNudgesError,
        arrCorrelationsError,
        websocketState);

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
                            max={Math.max(1, (fullStatus?.arr_search_nudge.planned ?? 0) + (fullStatus?.arr_search_nudge.failed ?? 0))}
                            ready={fullStatus?.arr_search_nudge.executed ?? 0}
                            retry={fullStatus?.arr_search_nudge.failed ?? 0}
                            state={(fullStatus?.arr_search_nudge.failed ?? 0) > 0 ? "retrying" : (fullStatus?.arr_search_nudge.planned ?? 0) > 0 ? "ready" : "idle"}
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

                <section className={styles.panel}>
                    <div className={styles.header}>
                        <h3 className={styles.title}>ARR Validation</h3>
                        <span className={styles.badge}>{arrValidation?.correlation_coverage_percent ?? 0}% correlated</span>
                    </div>
                    <div className={styles.metricGrid}>
                        <Metric label="Instances" value={arrValidation?.instance_count ?? 0} />
                        <Metric label="Queue" value={arrValidation?.queue_items ?? 0} />
                        <Metric label="Hints" value={arrValidation?.active_priority_hints ?? 0} />
                        <Metric label="Issues" value={arrValidation?.issues.length ?? 0} tone={arrValidation?.issues.length ? "warning" : undefined} />
                    </div>
                    {(arrValidation?.issues ?? []).slice(0, 5).map(issue =>
                        <div className={styles.mutedLine} key={issue.code}>
                            <span className={issue.severity === "error" ? styles.danger : styles.warning}>{issue.severity}</span>
                            {` ${issue.message}`}
                        </div>
                    )}
                </section>
            </div>

            <section className={styles.panel}>
                <div className={styles.header}>
                    <h3 className={styles.title}>ARR Search Commands</h3>
                    <span className={styles.badge}>{arrNudges?.commands.length ?? 0} recent</span>
                </div>
                <Form method="post" className={styles.actions}>
                    <button
                        className={styles.button}
                        name="intent"
                        value="clear-arr-failed-nudges"
                        type="submit"
                        disabled={isActionSubmitting || !(arrNudges?.commands.some(x => x.status === "failed"))}
                    >
                        Clear Failed
                    </button>
                </Form>
                <div className={styles.tableWrap}>
                    <table className={styles.table}>
                        <thead>
                            <tr>
                                <th>Created</th>
                                <th>App</th>
                                <th>Command</th>
                                <th>Status</th>
                                <th>Targets</th>
                                <th>Reason</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            {(arrNudges?.commands ?? []).map(command =>
                                <tr key={command.id}>
                                    <td>{formatDate(command.created_at)}</td>
                                    <td>{command.arr_app}</td>
                                    <td>{command.command_name}</td>
                                    <td className={command.status === "failed" ? styles.danger : undefined}>{command.status}</td>
                                    <td>{command.targets.join(", ")}</td>
                                    <td>{command.error ?? command.reasons.join(", ")}</td>
                                    <td>
                                        {command.status === "failed" &&
                                            <Form method="post">
                                                <input type="hidden" name="id" value={command.id} />
                                                <button
                                                    className={styles.button}
                                                    name="intent"
                                                    value="retry-arr-nudge"
                                                    type="submit"
                                                    disabled={isActionSubmitting}
                                                >
                                                    Retry
                                                </button>
                                            </Form>
                                        }
                                    </td>
                                </tr>
                            )}
                            {(!arrNudges || arrNudges.commands.length === 0) &&
                                <tr><td colSpan={7} className={styles.muted}>No ARR search commands recorded.</td></tr>
                            }
                        </tbody>
                    </table>
                </div>
            </section>

            <section className={styles.panel}>
                <div className={styles.header}>
                    <h3 className={styles.title}>ARR Correlations</h3>
                    <span className={styles.badge}>{arrCorrelations?.correlations.length ?? 0} recent</span>
                </div>
                <CorrelationForm isActionSubmitting={isActionSubmitting} />
                <div className={styles.tableWrap}>
                    <table className={styles.table}>
                        <thead>
                            <tr>
                                <th>ARR</th>
                                <th>Media</th>
                                <th>NZBDav</th>
                                <th>Title</th>
                                <th>Seen</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            {(arrCorrelations?.correlations ?? []).map(correlation =>
                                <tr key={correlation.id}>
                                    <td>{correlation.arr_app}</td>
                                    <td>{correlation.media_key ?? correlation.download_id ?? "unknown"}</td>
                                    <td>{correlation.queue_item_id ?? correlation.history_item_id ?? "unlinked"}</td>
                                    <td>{correlation.release_title ?? correlation.category ?? "unknown"}</td>
                                    <td>{formatDate(correlation.last_seen_at)}</td>
                                    <td>
                                        <Form method="post">
                                            <input type="hidden" name="id" value={correlation.id} />
                                            <button
                                                className={styles.button}
                                                name="intent"
                                                value="delete-arr-correlation"
                                                type="submit"
                                                disabled={isActionSubmitting}
                                            >
                                                Delete
                                            </button>
                                        </Form>
                                    </td>
                                </tr>
                            )}
                            {(!arrCorrelations || arrCorrelations.correlations.length === 0) &&
                                <tr><td colSpan={6} className={styles.muted}>No ARR correlations recorded.</td></tr>
                            }
                        </tbody>
                    </table>
                </div>
            </section>
        </div>
    );
}

function CorrelationForm({ isActionSubmitting }: { isActionSubmitting: boolean }) {
    return (
        <Form method="post" className={styles.correlationForm}>
            <input name="nzo_id" placeholder="NZBDav nzo_id" className={styles.input} />
            <select name="arr_app" className={styles.input} defaultValue="sonarr">
                <option value="sonarr">Sonarr</option>
                <option value="radarr">Radarr</option>
                <option value="lidarr">Lidarr</option>
            </select>
            <input name="instance_host" placeholder="ARR host" className={styles.input} />
            <input name="download_id" placeholder="ARR download id" className={styles.input} />
            <input name="movie_id" placeholder="Movie id" className={styles.input} />
            <input name="series_id" placeholder="Series id" className={styles.input} />
            <input name="episode_id" placeholder="Episode id" className={styles.input} />
            <input name="release_title" placeholder="Release title" className={styles.input} />
            <label className={styles.checkboxLabel}>
                <input type="checkbox" name="is_duplicate" />
                Duplicate
            </label>
            <button className={styles.button} name="intent" value="save-arr-correlation" type="submit" disabled={isActionSubmitting}>
                Save
            </button>
        </Form>
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
            <WorkerRow
                label="Download"
                active={fullStatus?.worker_queues.download_active ?? 0}
                max={fullStatus?.worker_queues.download_max ?? fullStatus?.max_queue_workers ?? 0}
                ready={fullStatus?.worker_queues.download_ready ?? 0}
                retry={fullStatus?.worker_queues.download_retry ?? 0}
                state={fullStatus?.worker_queues.download_state ?? "unknown"}
            />
            <WorkerRow
                label="Verify"
                active={verify?.leased ?? fullStatus?.worker_queues.verify_active ?? 0}
                max={verify?.max ?? fullStatus?.worker_queues.verify_max ?? fullStatus?.max_verify_workers ?? 0}
                ready={verify?.ready ?? fullStatus?.worker_queues.verify_ready ?? 0}
                retry={verify?.retry ?? fullStatus?.worker_queues.verify_retry ?? 0}
                state={verify?.state ?? fullStatus?.worker_queues.verify_state ?? "unknown"}
            />
            <WorkerRow
                label="Repair"
                active={repair?.leased ?? fullStatus?.worker_queues.repair_active ?? 0}
                max={repair?.max ?? fullStatus?.worker_queues.repair_max ?? fullStatus?.max_repair_workers ?? 0}
                ready={repair?.ready ?? fullStatus?.worker_queues.repair_ready ?? 0}
                retry={repair?.retry ?? fullStatus?.worker_queues.repair_retry ?? 0}
                state={repair?.state ?? fullStatus?.worker_queues.repair_state ?? "unknown"}
            />
        </div>
    );
}

function WorkerRow({ label, active, max, ready, retry, state }: { label: string; active: number; max: number; ready: number; retry: number; state: string }) {
    return (
        <div className={styles.workerRow}>
            <span className={styles.workerLabel}>{label}</span>
            <span>{state}</span>
            <span>active {active}/{max}</span>
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
    arrValidation: ArrValidationResponse | null,
    arrValidationError: string | null,
    arrNudgesError: string | null,
    arrCorrelationsError: string | null,
    websocketState: "connecting" | "connected" | "disconnected"
): string[] {
    const messages: string[] = [];
    if (websocketState === "disconnected") messages.push("Live health updates are disconnected.");
    if (fullStatusError) messages.push(fullStatusError);
    if (repairStatusError) messages.push(repairStatusError);
    if (arrValidationError) messages.push(arrValidationError);
    if (arrNudgesError) messages.push(arrNudgesError);
    if (arrCorrelationsError) messages.push(arrCorrelationsError);
    if (fullStatus?.rclone_invalidations.failed) messages.push("Rclone invalidation failures need attention.");
    if (fullStatus?.rclone_invalidations.pending || fullStatus?.rclone_invalidations.ready) messages.push("Rclone invalidations are waiting to drain.");
    if (fullStatus?.mount.enabled && !fullStatus.mount.ready) messages.push("Mounted filesystem is not ready.");
    if (fullStatus?.mount.fuse_errors) messages.push("Mounted filesystem reported FUSE errors.");
    if (repairStatus?.broken_files.length) messages.push("Repair has broken files requiring operator review.");
    if (repairStatus?.verify_queue.quarantined || repairStatus?.repair_queue.quarantined) messages.push("Repair or verify jobs are quarantined.");
    if (fullStatus?.arr_prioritization.stale_correlations) messages.push("ARR queue correlations are stale.");
    if (fullStatus?.arr_prioritization.duplicates) messages.push("ARR duplicate download requests were detected.");
    if (fullStatus?.arr_search_nudge.failed) messages.push("ARR search nudge commands have failed.");
    for (const issue of arrValidation?.issues ?? []) {
        if (issue.severity === "error") messages.push(issue.message);
    }
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

function formatDate(value: string | null) {
    if (!value) return "unknown";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    return date.toLocaleString();
}
