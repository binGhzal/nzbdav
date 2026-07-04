class BackendClient {
    public async isOnboarding(): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/is-onboarding";

        const response = await fetch(url, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            }
        });

        if (!response.ok) {
            throw new Error(`Failed to fetch onboarding status: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.isOnboarding;
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/create-account";

        const response = await fetch(url, {
            method: "POST",
            headers: {
                "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || ""
            },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to create account: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.status;
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/authenticate";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("username", username);
                form.append("password", password);
                form.append("type", "admin");
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to authenticate: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.authenticated;
    }

    public async getQueue(options: QueueRequestOptions): Promise<QueueResponse> {
        const params = new URLSearchParams({
            mode: "queue",
            start: String(options.start),
            limit: String(options.limit),
        });
        if (options.status && options.status !== "all") {
            params.set("status", options.status);
        }
        if (options.sort && options.sort !== "priority") {
            params.set("sort", options.sort);
        }
        if (options.order && options.order !== "desc") {
            params.set("order", options.order);
        }

        const url = process.env.BACKEND_URL + `/api?${params.toString()}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get queue: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.queue;
    }

    public async getHistory(start: number, limit: number): Promise<HistoryResponse> {
        const url = process.env.BACKEND_URL + `/api?mode=history&start=${start}&pageSize=${limit}`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, { headers: { "x-api-key": apiKey } });
        if (!response.ok) {
            throw new Error(`Failed to get history: ${(await response.json()).error}`);
        }

        const data = await response.json();
        return data.history;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        var config = await this.getConfig(["api.manual-category"]);
        var category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const url = process.env.BACKEND_URL + `/api?mode=addfile&cat=${category}&priority=0&pp=0`;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("nzbFile", nzbFile, nzbFile.name);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to add nzb file: ${(await response.json()).error}`);
        }
        const data = await response.json();
        if (!data.nzo_ids || data.nzo_ids.length != 1) {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return data.nzo_ids[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const url = process.env.BACKEND_URL + "/api/list-webdav-directory";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                form.append("directory", directory);
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to list webdav directory: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.items;
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const url = process.env.BACKEND_URL + "/api/get-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const key of keys) {
                    form.append("config-keys", key);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to get config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.configItems || [];
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const url = process.env.BACKEND_URL + "/api/update-config";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey },
            body: (() => {
                const form = new FormData();
                for (const item of configItems) {
                    form.append(item.configName, item.configValue);
                }
                return form;
            })()
        });

        if (!response.ok) {
            throw new Error(`Failed to update config items: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.status;
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-queue";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check queue: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        let url = process.env.BACKEND_URL + "/api/get-health-check-history";

        if (pageSize !== undefined) {
            url += `?pageSize=${pageSize}`;
        }

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get health check history: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data;
    }

    public async getFullStatus(): Promise<FullStatusResponse> {
        const url = process.env.BACKEND_URL + "/api?mode=fullstatus";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get full status: ${(await response.json()).error}`);
        }
        const data = await response.json();
        return data.status;
    }

    public async getRepairStatus(): Promise<RepairStatusResponse> {
        const url = process.env.BACKEND_URL + "/api/repair/status";

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "GET",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to get repair status: ${(await response.json()).error}`);
        }
        return await response.json();
    }

    public async startRepairRun(): Promise<RepairRunEnvelope> {
        return await this.postRepairAction<RepairRunEnvelope>("/api/repair/run", "start repair run");
    }

    public async cancelRepairRun(id: string): Promise<RepairRunEnvelope> {
        return await this.postRepairAction<RepairRunEnvelope>(`/api/repair/run/${id}/cancel`, "cancel repair run");
    }

    public async clearRepairRuns(): Promise<{ status: boolean }> {
        return await this.postRepairAction<{ status: boolean }>("/api/repair/clear", "clear repair runs");
    }

    private async postRepairAction<T>(path: string, description: string): Promise<T> {
        const url = process.env.BACKEND_URL + path;

        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        const response = await fetch(url, {
            method: "POST",
            headers: { "x-api-key": apiKey }
        });

        if (!response.ok) {
            throw new Error(`Failed to ${description}: ${(await response.json()).error}`);
        }
        return await response.json();
    }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
    slots: QueueSlot[],
    noofslots: number,
    noofslots_total: number,
    status: string,
    paused: boolean,
    paused_all: boolean,
    start: number,
    limit: number,
}

export type QueueRequestOptions = {
    start: number,
    limit: number,
    status?: QueueStatusFilter,
    sort?: QueueSortField,
    order?: QueueSortOrder,
}

export type QueueSortField = "priority" | "name" | "category" | "status" | "size" | "created";
export type QueueSortOrder = "asc" | "desc";
export type QueueStatusFilter = "all" | "downloading" | "verifying" | "repairing" | "moving" | "queued" | "paused";

export type QueueSlot = {
    nzo_id: string,
    priority: string,
    filename: string,
    cat: string,
    percentage: string,
    true_percentage: string,
    status: string,
    mb: string,
    mbleft: string,
    arr_priority?: ArrPriorityInfo | null,
}

export type ArrPriorityInfo = {
    score: number,
    effective_priority: string,
    apply_to_scheduling: boolean,
    reasons: string[],
    source: string,
    stale_reason: string | null,
}

export type HistoryResponse = {
    slots: HistorySlot[],
    noofslots: number,
    noofslots_total: number,
    start: number,
    limit: number,
}

export type HistorySlot = {
    nzo_id: string,
    nzb_name: string,
    name: string,
    category: string,
    status: string,
    bytes: number,
    storage: string,
    download_time: number,
    fail_message: string,
    nzb_blob_id?: string,
}

export type DirectoryItem = {
    name: string,
    isDirectory: boolean,
    size: number | null | undefined,
    nzbBlobId?: string,
}

export type ConfigItem = {
    configName: string,
    configValue: string,
}

export type TestUsenetConnectionRequest = {
    host: string,
    port: string,
    useSsl: string,
    user: string,
    pass: string
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number,
    items: HealthCheckQueueItem[]
}

export type HealthCheckQueueItem = {
    id: string,
    name: string,
    path: string,
    releaseDate: string | null,
    lastHealthCheck: string | null,
    nextHealthCheck: string | null,
    progress: number,
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[],
    items: HealthCheckResult[]
}

export type HealthCheckStats = {
    result: HealthResult,
    repairStatus: RepairAction,
    count: number
}

export type HealthCheckResult = {
    id: string,
    createdAt: string,
    davItemId: string,
    path: string,
    result: HealthResult,
    repairStatus: RepairAction,
    message: string | null
}

export enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

export enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}

export type FullStatusResponse = {
    paused: boolean,
    queue_status: string,
    jobs: number,
    jobs_active: number,
    max_queue_workers: number,
    max_download_connections: number,
    adaptive_max_download_connections: number,
    queue_file_processing_concurrency: number,
    healthcheck_concurrency: number,
    max_streaming_connections: number,
    max_total_streaming_connections: number,
    active_streams: number,
    rclone_invalidations: RcloneInvalidationStatus,
    cache: CacheStatus,
    mount: MountDiagnosticStatus,
    provider_diagnostics: ProviderDiagnosticStatus[],
    worker_queues: WorkerQueueStatus,
    repair_runs: RepairRunsStatus,
    arr_prioritization: ArrPrioritizationStatus,
    arr_search_nudge: ArrSearchNudgeStatus,
    arr_download_report: ArrDownloadReportStatus,
    total_streams_opened: number,
    managed_memory_bytes: number,
    working_set_bytes: number,
    gc_memory_load_percent: number,
    process_cpu_cores: number,
    threadpool_threads: number,
    threadpool_pending_work_items: number,
}

export type RcloneInvalidationStatus = {
    pending: number,
    ready: number,
    failed: number,
    max_attempts: number,
    last_error: string | null,
}

export type CacheStatus = {
    bytes: number,
    max_bytes: number,
    hits: number,
    misses: number,
    evictions: number,
    files: number,
    active_readers: number,
    pending_fetches: number,
}

export type MountDiagnosticStatus = {
    type: string,
    directory: string,
    enabled: boolean,
    ready: boolean,
    state: string,
    message: string | null,
    fuse_errors: number,
    active_operations: number,
    waiting_operations: number,
    last_invalidation_at: string | null,
    updated_at: string,
    cache: CacheStatus | null,
}

export type ProviderDiagnosticStatus = {
    name: string,
    host: string,
    port: number,
    type: string,
    priority: number,
    max_connections: number,
    ssl: boolean,
    stat_pipelining_enabled: boolean,
}

export type WorkerQueueStatus = {
    download_active: number,
    download_waiting: number,
    download_ready: number,
    download_retry: number,
    download_quarantined: number,
    verify_active: number,
    verify_ready: number,
    verify_retry: number,
    verify_quarantined: number,
    repair_active: number,
    repair_action_needed: number,
    repair_ready: number,
    repair_retry: number,
    repair_quarantined: number,
}

export type RepairRunsStatus = {
    active: RepairRunSummaryStatus | null,
    last: RepairRunSummaryStatus | null,
    broken_files: number,
    next_due_at: string | null,
}

export type ArrPrioritizationStatus = {
    enabled: boolean,
    mode: string,
    correlations: number,
    stale_correlations: number,
    duplicates: number,
    active_hints: number,
    stale_hints: number,
}

export type ArrSearchNudgeStatus = {
    enabled: boolean,
    mode: string,
    planned: number,
    executed: number,
    failed: number,
    last_command_at: string | null,
}

export type ArrDownloadReportStatus = {
    lifecycle_states: Array<{ state: string, count: number }>,
}

export type RepairRunSummaryStatus = {
    id: string,
    status: string,
    stage: string,
    started_at: string,
    completed_at: string | null,
    total: number,
    checked: number,
    missing: number,
    provider_errors: number,
    unknown: number,
    repaired: number,
    deleted: number,
    action_needed: number,
    broken_files: number,
}

export type RepairStatusResponse = {
    active_run: RepairRun | null,
    last_run: RepairRun | null,
    broken_files: RepairBrokenFile[],
    verify_queue: RepairWorkerQueue,
    repair_queue: RepairWorkerQueue,
}

export type RepairRunEnvelope = {
    run: RepairRun,
}

export type RepairRun = {
    id: string,
    status: string,
    stage: string,
    started_at: string,
    updated_at: string,
    completed_at: string | null,
    cancelled_at: string | null,
    next_due_at: string | null,
    total: number,
    checked: number,
    missing: number,
    provider_errors: number,
    unknown: number,
    repaired: number,
    deleted: number,
    action_needed: number,
    broken_files: number,
    message: string | null,
}

export type RepairBrokenFile = {
    id: string,
    repair_run_id: string,
    dav_item_id: string,
    path: string,
    reason: string,
    created_at: string,
}

export type RepairWorkerQueue = {
    pending: number,
    retry: number,
    leased: number,
    ready: number,
    quarantined: number,
    completed: number,
    cancelled: number,
    total: number,
}
