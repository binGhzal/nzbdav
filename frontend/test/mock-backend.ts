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
};

const port = Number.parseInt(process.env.MOCK_BACKEND_PORT ?? "5174", 10);
let queuePaused = false;
let requests: RecordedRequest[] = [];

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
        { configName: "webdav.user", configValue: "admin" },
        { configName: "usenet.max-download-connections", configValue: "15" },
        { configName: "usenet.adaptive-connections-enabled", configValue: "true" },
      ],
    });
    return;
  }

  if (url.pathname === "/api/update-config") {
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
