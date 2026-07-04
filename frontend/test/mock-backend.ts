import http from "node:http";
import { URL } from "node:url";
import { WebSocketServer } from "ws";

type RecordedRequest = {
  method: string;
  path: string;
  query: Record<string, string>;
  body: string;
};

type QueueSlot = {
  nzo_id: string;
  priority: string;
  filename: string;
  cat: string;
  percentage: string;
  true_percentage: string;
  status: string;
  mb: string;
  mbleft: string;
  arr_priority?: {
    score: number;
    effective_priority: string;
    apply_to_scheduling: boolean;
    reasons: string[];
    source: string;
    stale_reason: string | null;
  };
};

const port = Number.parseInt(process.env.MOCK_BACKEND_PORT ?? "5174", 10);
let queuePaused = false;
let requests: RecordedRequest[] = [];
let repairRunStatus: "Running" | "Cancelled" = "Running";
let repairBrokenFilesCleared = false;
let arrFailedNudgesCleared = false;
let arrCorrelationDeleted = false;

const queueSlots: QueueSlot[] = [
  createQueueSlot("11111111-1111-1111-1111-111111111111", "Downloading Release", "movies", "Downloading", "45", "20"),
  createQueueSlot("22222222-2222-2222-2222-222222222222", "Queued Release", "tv", "Queued", "0", "10"),
  createQueueSlot("33333333-3333-3333-3333-333333333333", "Paused Release", "tv", "Paused", "0", "15", "Paused"),
];

const historySlots = [
  {
    nzo_id: "44444444-4444-4444-4444-444444444444",
    nzb_name: "Completed Release.nzb",
    name: "Completed Release",
    category: "movies",
    status: "Completed",
    bytes: 1_073_741_824,
    storage: "/downloads/movies/Completed Release",
    download_time: 42,
    fail_message: "",
    nzb_blob_id: "55555555-5555-5555-5555-555555555555",
  },
];

const healthQueueItems = [
  {
    id: "66666666-6666-6666-6666-666666666666",
    name: "Unverified Movie.mkv",
    path: "/content/Unverified Movie.mkv",
    releaseDate: null,
    lastHealthCheck: null,
    nextHealthCheck: null,
    progress: 25,
  },
];

const healthHistoryItems = [
  {
    id: "77777777-7777-7777-7777-777777777777",
    createdAt: "2026-07-02T09:00:00Z",
    davItemId: "88888888-8888-8888-8888-888888888888",
    path: "/content/Healthy Movie.mkv",
    result: 0,
    repairStatus: 0,
    message: null,
  },
];

function createQueueSlot(
  id: string,
  filename: string,
  category: string,
  status: string,
  percentage: string,
  mb: string,
  priority = "Normal",
): QueueSlot {
  return {
    nzo_id: id,
    priority,
    filename,
    cat: category,
    percentage,
    true_percentage: percentage,
    status,
    mb,
    mbleft: mb,
    arr_priority: {
      score: status === "Queued" ? 180 : 0,
      effective_priority: status === "Queued" ? "High" : priority,
      apply_to_scheduling: false,
      reasons: status === "Queued" ? ["arr-correlated"] : [],
      source: "arr-report",
      stale_reason: null,
    },
  };
}

function writeJson(res: http.ServerResponse, value: unknown, status = 200) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(value));
}

function writeText(res: http.ServerResponse, value: string, status = 200) {
  res.writeHead(status, { "content-type": "text/plain" });
  res.end(value);
}

async function readBody(req: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }

  return Buffer.concat(chunks).toString("utf8");
}

function getQuery(url: URL): Record<string, string> {
  return Object.fromEntries(url.searchParams.entries());
}

function recordRequest(req: http.IncomingMessage, url: URL, body: string) {
  requests.push({
    method: req.method ?? "GET",
    path: url.pathname,
    query: getQuery(url),
    body,
  });
}

function getQueueResponse(url: URL) {
  const statusFilter = url.searchParams.get("status");
  const start = Number.parseInt(url.searchParams.get("start") ?? "0", 10);
  const limit = Number.parseInt(url.searchParams.get("limit") ?? "50", 10);
  const filteredSlots = queueSlots.filter((slot) => {
    if (!statusFilter || statusFilter === "all") return true;
    return slot.status.toLowerCase() === statusFilter;
  });
  const slots = filteredSlots.slice(start, start + limit);

  return {
    queue: {
      slots,
      noofslots: filteredSlots.length,
      noofslots_total: queueSlots.length,
      status: slots.some((slot) => slot.status === "Downloading") ? "Downloading" : "Idle",
      paused: queuePaused,
      paused_all: queuePaused,
      start,
      limit,
    },
  };
}

function getHistoryResponse(url: URL) {
  const start = Number.parseInt(url.searchParams.get("start") ?? "0", 10);
  const limit = Number.parseInt(url.searchParams.get("pageSize") ?? "50", 10);
  return {
    history: {
      slots: historySlots.slice(start, start + limit),
      noofslots: historySlots.length,
      noofslots_total: historySlots.length,
      start,
      limit,
    },
  };
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "127.0.0.1"}`);

  if (url.pathname === "/health") {
    writeText(res, "Healthy");
    return;
  }

  if (url.pathname === "/__e2e/reset") {
    requests = [];
    queuePaused = false;
    repairRunStatus = "Running";
    repairBrokenFilesCleared = false;
    writeJson(res, { status: true });
    return;
  }

  if (url.pathname === "/__e2e/requests") {
    writeJson(res, { requests });
    return;
  }

  const body = req.method === "POST" || req.method === "PUT" ? await readBody(req) : "";
  recordRequest(req, url, body);

  if (url.pathname === "/api/is-onboarding") {
    writeJson(res, { isOnboarding: false });
    return;
  }

  if (url.pathname === "/api/authenticate") {
    writeJson(res, { authenticated: true });
    return;
  }

  if (url.pathname === "/api/get-config") {
    writeJson(res, {
      configItems: [
        { configName: "api.categories", configValue: "movies,tv" },
        { configName: "api.manual-category", configValue: "movies" },
        { configName: "repair.enable", configValue: "true" },
        { configName: "webdav.user", configValue: "admin" },
        { configName: "usenet.max-download-connections", configValue: "15" },
        { configName: "usenet.adaptive-connections-enabled", configValue: "true" },
        { configName: "queue.max-concurrent-downloads", configValue: "4" },
        { configName: "queue.max-concurrent-verify", configValue: "2" },
        { configName: "queue.max-concurrent-repair", configValue: "1" },
      ],
    });
    return;
  }

  if (url.pathname === "/api/update-config") {
    writeJson(res, { status: true });
    return;
  }

  if (url.pathname === "/api/get-health-check-queue") {
    writeJson(res, {
      uncheckedCount: 42,
      items: healthQueueItems,
    });
    return;
  }

  if (url.pathname === "/api/get-health-check-history") {
    writeJson(res, {
      stats: [
        { result: 0, repairStatus: 0, count: 12 },
        { result: 1, repairStatus: 1, count: 2 },
      ],
      items: healthHistoryItems,
    });
    return;
  }

  if (url.pathname === "/api/repair/status") {
    writeJson(res, getRepairStatusResponse());
    return;
  }

  if (url.pathname === "/api/repair/run" && req.method === "POST") {
    repairRunStatus = "Running";
    writeJson(res, { run: getRepairRun() });
    return;
  }

  if (url.pathname === "/api/repair/run/run-1/cancel" && req.method === "POST") {
    repairRunStatus = "Cancelled";
    writeJson(res, { run: getRepairRun() });
    return;
  }

  if (url.pathname === "/api/repair/clear" && req.method === "POST") {
    repairBrokenFilesCleared = true;
    writeJson(res, { status: true });
    return;
  }

  if (url.pathname === "/api/arr/validation") {
    writeJson(res, getArrValidationResponse());
    return;
  }

  if (url.pathname === "/api/arr/search-nudges") {
    writeJson(res, getArrSearchNudgesResponse());
    return;
  }

  if (url.pathname === "/api/arr/search-nudges/nudge-2/retry" && req.method === "POST") {
    arrFailedNudgesCleared = true;
    writeJson(res, {
      id: "nudge-2",
      arr_app: "sonarr",
      instance_key: "sonarr:test",
      instance_host: "http://sonarr:8989",
      command_name: "EpisodeSearch",
      command_id: null,
      targets: [123],
      mode: "apply",
      status: "planned",
      score: 250,
      reasons: ["recently-aired"],
      error: null,
      created_at: "2026-07-02T08:11:00Z",
      completed_at: null,
      next_allowed_at: "2026-07-02T08:11:00Z",
    });
    return;
  }

  if (url.pathname === "/api/arr/search-nudges/clear" && req.method === "POST") {
    arrFailedNudgesCleared = true;
    writeJson(res, { status: true, deleted: 1 });
    return;
  }

  if (url.pathname === "/api/arr/correlations" && req.method === "GET") {
    writeJson(res, getArrCorrelationsResponse());
    return;
  }

  if (url.pathname === "/api/arr/correlations" && req.method === "POST") {
    writeJson(res, {
      correlation: {
        id: "corr-new",
        queue_item_id: "11111111-1111-1111-1111-111111111111",
        history_item_id: null,
        arr_app: "sonarr",
        instance_key: "sonarr:test",
        instance_host: "http://sonarr:8989",
        download_id: "11111111-1111-1111-1111-111111111111",
        media_key: "sonarr:episode:123",
        movie_id: null,
        series_id: 456,
        episode_id: 123,
        season_number: 1,
        artist_id: null,
        album_id: null,
        release_title: "Manual Release",
        category: "tv",
        quality: "HD",
        status: "manual",
        is_upgrade: false,
        is_duplicate: false,
        last_seen_at: "2026-07-02T08:14:00Z",
      },
    });
    return;
  }

  if (url.pathname === "/api/arr/correlations/corr-1" && req.method === "DELETE") {
    arrCorrelationDeleted = true;
    writeJson(res, { status: true });
    return;
  }

  if (url.pathname === "/api") {
    const mode = url.searchParams.get("mode");
    if (mode === "queue" && !url.searchParams.get("name")) {
      writeJson(res, getQueueResponse(url));
      return;
    }

    if (mode === "history") {
      writeJson(res, getHistoryResponse(url));
      return;
    }

    if (mode === "fullstatus") {
      writeJson(res, getFullStatusResponse());
      return;
    }

    if (mode === "pause") {
      queuePaused = true;
      writeJson(res, { status: true });
      return;
    }

    if (mode === "resume") {
      queuePaused = false;
      writeJson(res, { status: true });
      return;
    }

    if (mode === "queue") {
      writeJson(res, { status: true });
      return;
    }
  }

  writeJson(res, { error: `Unhandled mock backend request: ${req.method} ${url.pathname}${url.search}` }, 404);
});

function getRepairRun() {
  return {
    id: "run-1",
    status: repairRunStatus,
    stage: repairRunStatus === "Running" ? "checking" : "cancelled",
    started_at: "2026-07-02T08:00:00Z",
    updated_at: "2026-07-02T08:05:00Z",
    completed_at: null,
    cancelled_at: repairRunStatus === "Cancelled" ? "2026-07-02T08:10:00Z" : null,
    next_due_at: "2026-07-02T08:15:00Z",
    total: 20,
    checked: 8,
    missing: 1,
    provider_errors: 2,
    unknown: 1,
    repaired: 3,
    deleted: 0,
    action_needed: 1,
    broken_files: repairBrokenFilesCleared ? 0 : 1,
    message: null,
  };
}

function getRepairStatusResponse() {
  const brokenFiles = repairBrokenFilesCleared
    ? []
    : [
        {
          id: "broken-1",
          repair_run_id: "run-1",
          dav_item_id: "99999999-9999-9999-9999-999999999999",
          path: "/content/Broken Movie.mkv",
          reason: "Missing articles on all providers.",
          created_at: "2026-07-02T08:04:00Z",
        },
      ];

  return {
    active_run: repairRunStatus === "Running" ? getRepairRun() : null,
    last_run: getRepairRun(),
    broken_files: brokenFiles,
    verify_queue: {
      max: 2,
      state: "saturated",
      pending: 3,
      retry: 1,
      leased: 2,
      ready: 3,
      quarantined: 0,
      completed: 8,
      cancelled: 0,
      total: 14,
    },
    repair_queue: {
      max: 1,
      state: "active",
      pending: 1,
      retry: 0,
      leased: 1,
      ready: 1,
      quarantined: 0,
      completed: 3,
      cancelled: 0,
      total: 5,
    },
  };
}

function getFullStatusResponse() {
  return {
    status: {
      paused: false,
      queue_status: "Downloading",
      jobs: 2,
      jobs_active: 1,
      max_queue_workers: 4,
      max_verify_workers: 2,
      max_repair_workers: 1,
      max_download_connections: 40,
      adaptive_max_download_connections: 32,
      queue_file_processing_concurrency: 4,
      healthcheck_concurrency: 8,
      max_streaming_connections: 8,
      max_total_streaming_connections: 32,
      active_streams: 2,
      rclone_invalidations: {
        pending: 1,
        ready: 0,
        failed: 0,
        max_attempts: 8,
        last_error: null,
      },
      cache: {
        bytes: 17_179_869_184,
        max_bytes: 68_719_476_736,
        hits: 7,
        misses: 1,
        evictions: 2,
        files: 5,
        active_readers: 2,
        pending_fetches: 1,
      },
      mount: {
        type: "rclone",
        directory: "/mnt/nzbdav",
        enabled: false,
        ready: true,
        state: "external",
        message: "rclone mount is managed outside NZBDav",
        fuse_errors: 0,
        active_operations: 0,
        waiting_operations: 0,
        last_invalidation_at: null,
        updated_at: "2026-07-02T08:05:00Z",
        cache: null,
      },
      provider_diagnostics: [
        {
          name: "provider-1",
          host: "usenet.example.test",
          port: 563,
          type: "Primary",
          priority: 0,
          max_connections: 40,
          ssl: true,
          stat_pipelining_enabled: true,
        },
      ],
      worker_queues: {
        download_max: 4,
        download_state: "active",
        download_active: 1,
        download_waiting: 2,
        download_ready: 2,
        download_retry: 0,
        download_quarantined: 0,
        verify_max: 2,
        verify_state: "saturated",
        verify_active: 2,
        verify_ready: 3,
        verify_retry: 1,
        verify_quarantined: 0,
        repair_max: 1,
        repair_state: "active",
        repair_active: 1,
        repair_action_needed: 1,
        repair_ready: 1,
        repair_retry: 0,
        repair_quarantined: 0,
      },
      repair_runs: {
        active: repairRunStatus === "Running" ? getRepairRun() : null,
        last: getRepairRun(),
        broken_files: repairBrokenFilesCleared ? 0 : 1,
        next_due_at: "2026-07-02T08:15:00Z",
      },
      arr_prioritization: {
        enabled: true,
        mode: "report",
        correlations: 2,
        stale_correlations: 0,
        duplicates: 0,
        active_hints: 1,
        stale_hints: 0,
      },
      arr_search_nudge: {
        enabled: true,
        mode: "report",
        planned: 1,
        executed: 0,
        failed: 0,
        last_command_at: "2026-07-02T08:10:00Z",
      },
      arr_download_report: {
        lifecycle_states: [
          { state: "Queued", count: 1 },
          { state: "Downloading", count: 1 },
        ],
      },
      total_streams_opened: 12,
      managed_memory_bytes: 268_435_456,
      working_set_bytes: 536_870_912,
      gc_memory_load_percent: 22,
      process_cpu_cores: 1.5,
      threadpool_threads: 18,
      threadpool_pending_work_items: 3,
    },
  };
}

function getArrValidationResponse() {
  return {
    generated_at: "2026-07-02T08:12:00Z",
    instance_count: 2,
    queue_items: 3,
    history_items: 1,
    correlations: 2,
    stale_correlations: 0,
    correlation_coverage_percent: 67,
    active_priority_hints: 1,
    duplicates: 0,
    search_nudges: {
      planned: 1,
      executed: 1,
      failed: arrFailedNudgesCleared ? 0 : 1,
      last_command_at: "2026-07-02T08:11:00Z",
    },
    lifecycle_states: [
      { state: "Queued", count: 2 },
      { state: "Imported", count: 1 },
    ],
    issues: arrFailedNudgesCleared
      ? []
      : [{ severity: "warning", code: "search_nudge_failures", message: "One or more ARR search nudge commands failed." }],
  };
}

function getArrSearchNudgesResponse() {
  const commands = [
    {
      id: "nudge-1",
      arr_app: "radarr",
      instance_key: "radarr:test",
      instance_host: "http://radarr:7878",
      command_name: "MoviesSearch",
      command_id: 42,
      targets: [99],
      mode: "apply",
      status: "executed",
      score: 300,
      reasons: ["collection-completion"],
      error: null,
      created_at: "2026-07-02T08:10:00Z",
      completed_at: "2026-07-02T08:10:02Z",
      next_allowed_at: "2026-07-02T14:10:00Z",
    },
  ];
  if (!arrFailedNudgesCleared) {
    commands.push({
      id: "nudge-2",
      arr_app: "sonarr",
      instance_key: "sonarr:test",
      instance_host: "http://sonarr:8989",
      command_name: "EpisodeSearch",
      command_id: null,
      targets: [123],
      mode: "apply",
      status: "failed",
      score: 250,
      reasons: ["recently-aired"],
      error: "ARR timeout",
      created_at: "2026-07-02T08:11:00Z",
      completed_at: "2026-07-02T08:11:30Z",
      next_allowed_at: "2026-07-02T14:11:00Z",
    });
  }
  return { commands };
}

function getArrCorrelationsResponse() {
  const correlations = arrCorrelationDeleted
    ? []
    : [
        {
          id: "corr-1",
          queue_item_id: "11111111-1111-1111-1111-111111111111",
          history_item_id: null,
          arr_app: "sonarr",
          instance_key: "sonarr:test",
          instance_host: "http://sonarr:8989",
          download_id: "11111111-1111-1111-1111-111111111111",
          media_key: "sonarr:episode:123",
          movie_id: null,
          series_id: 456,
          episode_id: 123,
          season_number: 1,
          artist_id: null,
          album_id: null,
          release_title: "Downloading Release",
          category: "tv",
          quality: "HD",
          status: "downloading",
          is_upgrade: false,
          is_duplicate: false,
          last_seen_at: "2026-07-02T08:10:00Z",
        },
      ];
  return { correlations };
}

const websocketServer = new WebSocketServer({ noServer: true });
websocketServer.on("connection", (socket) => {
  socket.on("message", () => {
    // The frontend sends the backend API key as the first frame. No mock response is needed.
  });
});

server.on("upgrade", (req, socket, head) => {
  const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "127.0.0.1"}`);
  if (url.pathname !== "/ws") {
    socket.destroy();
    return;
  }

  websocketServer.handleUpgrade(req, socket, head, (websocket) => {
    websocketServer.emit("connection", websocket, req);
  });
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Mock backend listening on http://127.0.0.1:${port}`);
});
