import fs from "node:fs";
import compression from "compression";
import express from "express";
import morgan from "morgan";
import http from "http";
import { WebSocketServer } from "ws";
import { disableUnsafeRuntimeDebugOutput } from "./server/debug-output.js";
import {
  handleExpressTerminalFailure,
  sendExpressPublicFailure,
  serializePublicFailure,
} from "./server/public-failure-response.js";
import { classifyBackendRequest } from "./server/request-policy.js";
import { FRONTEND_WEBSOCKET_SERVER_OPTIONS } from "./server/websocket-policy.js";

loadDotEnvFile();
disableUnsafeRuntimeDebugOutput();

// Short-circuit the type-checking of the built output.
const BUILD_PATH = "../build/server/index.js";
const DEVELOPMENT = process.env.NODE_ENV === "development";
const PORT = Number.parseInt(process.env.PORT || "3000");
const HOST = process.env.LISTEN_ADDRESS || "0.0.0.0";
const MAX_WEBDAV_PATH_BASE_BYTES = 8_192;
const LITERAL_URL_BASE_SEGMENT = /^[A-Za-z0-9._~-]+$/u;

// URL_BASE controls the sub-path the app is mounted under. See app/utils/url-base.ts
// for the canonical normalization rules. The Vite build also bakes this value into the
// client bundle; the runtime env var here mounts middleware at the matching prefix so
// both ends agree.
function normalizeUrlBase(raw: string | undefined): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "";
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  const normalized = withLeading.replace(/\/+$/, "");
  const segments = normalized.slice(1).split("/");
  if (
    segments.some((segment) => (
      segment === "."
      || segment === ".."
      || !LITERAL_URL_BASE_SEGMENT.test(segment)
    ))
    || Buffer.byteLength(`${normalized}/protocol`, "utf8")
      > MAX_WEBDAV_PATH_BASE_BYTES
  ) {
    throw new Error("URL_BASE must be a literal origin path made from nonempty segments.");
  }
  return normalized;
}
const URL_BASE = normalizeUrlBase(process.env.URL_BASE);

function loadDotEnvFile() {
  const candidates = process.env.NZBDAV_ENV_FILE
    ? [process.env.NZBDAV_ENV_FILE]
    : [".env", "../.env"];
  const envPath = candidates.find((candidate) => fs.existsSync(candidate));
  if (!envPath) return;

  for (const rawLine of fs.readFileSync(envPath, "utf8").split(/\r?\n/)) {
    let line = rawLine.trim();
    if (!line || line.startsWith("#")) continue;
    if (line.startsWith("export ")) line = line.slice("export ".length).trimStart();

    const separator = line.indexOf("=");
    if (separator <= 0) continue;

    const key = line.slice(0, separator).trim();
    if (!key || process.env[key] !== undefined) continue;

    process.env[key] = unquoteEnvValue(line.slice(separator + 1).trim());
  }
}

function unquoteEnvValue(value: string) {
  if (value.length < 2) return value;
  const quote = value[0];
  if ((quote !== "\"" && quote !== "'") || value[value.length - 1] !== quote) return value;
  const unquoted = value.slice(1, -1);
  return quote === "\"" ? unquoted.replace(/\\n/g, "\n").replace(/\\"/g, "\"") : unquoted;
}

// Initialize the express app
const app = express();
app.disable("x-powered-by");
app.enable("case sensitive routing");

app.get("/healthz", (_req, res) => {
  res.status(200).type("text/plain").send("ok");
});
app.all("/healthz", (req, res, next) => {
  if (req.method === "GET" || req.method === "HEAD") {
    next();
    return;
  }
  sendExpressPublicFailure(req, res, 405, "method_not_allowed", {
    allow: "GET, HEAD",
  });
});

// /health is a liveness probe for non-browser callers, while browser
// navigations pass through to the React Health route when URL_BASE is empty.
// Probes do not send text/html Accept headers; real page loads do.
app.get("/health", async (req, res, next) => {
  if ((req.headers.accept ?? "").includes("text/html")) {
    next();
    return;
  }

  try {
    const backendUrl = process.env.BACKEND_URL || "http://localhost:8080";
    const r = await fetch(`${backendUrl}/health`, { signal: AbortSignal.timeout(3000) });
    if (r.ok) {
      res.status(200).type("text/plain").send("Healthy");
      return;
    }
    sendExpressPublicFailure(req, res, 503, "upstream_unavailable");
  } catch {
    sendExpressPublicFailure(req, res, 503, "upstream_unavailable");
  }
});
app.all("/health", (req, res, next) => {
  if (req.method === "GET" || req.method === "HEAD") {
    next();
    return;
  }
  sendExpressPublicFailure(req, res, 405, "method_not_allowed", {
    allow: "GET, HEAD",
  });
});

// All app middleware goes on a sub-router so it inherits the URL_BASE prefix without
// requiring per-middleware path arithmetic. Inside the router, `req.path` is already
// stripped of URL_BASE — existing path-prefix checks (`/api`, `/nzbs`, etc.) work
// unchanged.
const router = express.Router({ caseSensitive: true });

const compressFrontendResponse = compression();
router.use((req, res, next) => {
  // Freeze the decision before the proxy rewrites req.url for the private hop.
  // Classification consumes the raw target and never decodes malformed input.
  if (classifyBackendRequest(req.method, req.url).kind !== "frontend") {
    next();
    return;
  }
  compressFrontendResponse(req, res, next);
});

type ServerModule = {
  app: express.RequestHandler;
  authenticateWebsocketUpgrade: (request: http.IncomingMessage) => Promise<boolean>;
  initializeWebsocketServer: (websocketServer: WebSocketServer) => void;
};

// Initialize the websocket server as soon as both it and the server module are ready.
let _serverModule: ServerModule | null = null;
let _websocketServer: WebSocketServer | null = null;
const setWebsocketServer = (websocketServer: WebSocketServer) => {
  if (_websocketServer != null) return;
  if (_serverModule != null) _serverModule.initializeWebsocketServer(websocketServer);
  _websocketServer = websocketServer;
};
const setServerModule = (serverModule: ServerModule) => {
  if (_serverModule != null) return;
  if (_websocketServer != null) serverModule.initializeWebsocketServer(_websocketServer);
  _serverModule = serverModule;
};
let loadServerModule: () => Promise<ServerModule>;

// Handle development vs production
if (DEVELOPMENT) {
  const viteDevServer = await import("vite").then((vite) =>
    vite.createServer({
      server: { middlewareMode: true },
    }),
  );
  loadServerModule = async () => {
    if (_serverModule) return _serverModule;
    const loaded = await viteDevServer.ssrLoadModule("./server/app.ts") as ServerModule;
    setServerModule(loaded);
    return loaded;
  };
  router.use(viteDevServer.middlewares);
  router.use(async (req, res, next) => {
    try {
      const serverModule = await loadServerModule();
      return await serverModule.app(req, res, next);
    } catch (error) {
      if (typeof error === "object" && error instanceof Error) {
        viteDevServer.ssrFixStacktrace(error);
      }
      next(error);
    }
  });
} else {
  router.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  router.use(morgan((tokens, req, res) => {
    const status = tokens.status(req, res);
    return status && /^[1-5][0-9]{2}$/u.test(status)
      ? `frontend_http_failure status=${status}`
      : "frontend_http_failure";
  }, {
    skip: (req, res) => {
      return res.statusCode < 400
        || req.url === "/favicon.ico";
    },
  }));
  router.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH) as ServerModule;
  loadServerModule = async () => serverModule;
  router.use(serverModule.app);
  setServerModule(serverModule);
}

// Mount the router. When URL_BASE is empty we mount at root (no prefix). Otherwise we
// mount under URL_BASE and redirect the bare host root so users hitting `/` land in
// the right place.
if (URL_BASE) {
  app.get("/", (_req, res) => res.redirect(`${URL_BASE}/`));
  app.use(URL_BASE, router);
} else {
  app.use(router);
}

app.use((request, response) => {
  sendExpressPublicFailure(request, response, 404, "route_not_found");
});
app.use(handleExpressTerminalFailure);

// Upgrade only the exact authenticated browser websocket before HTTP 101.
const server = http.createServer(app);
const frontendWebsocketServer = new WebSocketServer(FRONTEND_WEBSOCKET_SERVER_OPTIONS);
setWebsocketServer(frontendWebsocketServer);
server.on("upgrade", (request, socket, head) => {
  void handleWebsocketUpgrade(request, socket, head);
});

// Begin listening for connections
server.listen(PORT, HOST, () => {
  console.log("frontend_ready");
});

async function handleWebsocketUpgrade(
  request: http.IncomingMessage,
  socket: import("node:stream").Duplex,
  head: Buffer,
): Promise<void> {
  const exactTarget = `${URL_BASE}/ws` || "/ws";
  const rawTarget = request.url ?? "";
  if (!rawTarget.startsWith("/") || /[\\#\u0000-\u001f\u007f]/u.test(rawTarget)) {
    rejectWebsocketUpgrade(request, socket, 400, "invalid_request_target");
    return;
  }
  if (rawTarget !== exactTarget) {
    rejectWebsocketUpgrade(request, socket, 404, "route_not_found");
    return;
  }
  if (request.method !== "GET") {
    rejectWebsocketUpgrade(request, socket, 405, "method_not_allowed", "GET");
    return;
  }

  const hostValues = rawHeaderValues(request, "host");
  const originValues = rawHeaderValues(request, "origin");
  if (
    hostValues.length !== 1
    || originValues.length !== 1
    || !isValidHostAuthority(hostValues[0])
    || !isValidWebsocketOrigin(originValues[0], hostValues[0])
  ) {
    rejectWebsocketUpgrade(request, socket, 400, "invalid_request_target");
    return;
  }

  let authenticated = false;
  try {
    authenticated = await (await loadServerModule()).authenticateWebsocketUpgrade(request);
  } catch {
    rejectWebsocketUpgrade(request, socket, 500, "internal_error");
    return;
  }
  if (!authenticated) {
    rejectWebsocketUpgrade(request, socket, 401, "authentication_required");
    return;
  }

  try {
    frontendWebsocketServer.handleUpgrade(request, socket, head, (websocket) => {
      frontendWebsocketServer.emit("connection", websocket, request);
    });
  } catch {
    rejectWebsocketUpgrade(request, socket, 400, "invalid_request_target");
  }
}

function isValidHostAuthority(value: string): boolean {
  if (
    value.length === 0
    || value.length > 512
    || /[\s/?#@\u0000-\u001f\u007f]/u.test(value)
  ) {
    return false;
  }

  try {
    const parsed = new URL(`http://${value}`);
    return parsed.host.length > 0
      && parsed.username.length === 0
      && parsed.password.length === 0
      && parsed.pathname === "/"
      && parsed.search.length === 0
      && parsed.hash.length === 0;
  } catch {
    return false;
  }
}

function isValidWebsocketOrigin(value: string, host: string): boolean {
  if (value.length === 0 || value.length > 512 || /[\u0000-\u001f\u007f]/u.test(value)) {
    return false;
  }
  const match = /^(https?):\/\/([^/?#]+)$/u.exec(value);
  if (!match || match[2].toLowerCase() !== host.toLowerCase()) return false;

  try {
    const parsed = new URL(value);
    return parsed.username.length === 0
      && parsed.password.length === 0
      && parsed.pathname === "/"
      && parsed.search.length === 0
      && parsed.hash.length === 0;
  } catch {
    return false;
  }
}

function rawHeaderValues(request: http.IncomingMessage, name: string): string[] {
  const normalizedName = name.toLowerCase();
  const values: string[] = [];
  for (let index = 0; index + 1 < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index].toLowerCase() === normalizedName) {
      values.push(request.rawHeaders[index + 1]);
    }
  }
  if (values.length > 0) return values;

  const parsed = request.headers[normalizedName];
  if (Array.isArray(parsed)) return parsed;
  return parsed === undefined ? [] : [parsed];
}

function rejectWebsocketUpgrade(
  request: http.IncomingMessage,
  socket: import("node:stream").Duplex,
  status: 400 | 401 | 404 | 405 | 500,
  code: "authentication_required"
    | "internal_error"
    | "invalid_request_target"
    | "method_not_allowed"
    | "route_not_found",
  allow?: string,
): void {
  const reason = {
    400: "Bad Request",
    401: "Unauthorized",
    404: "Not Found",
    405: "Method Not Allowed",
    500: "Internal Server Error",
  }[status];
  const { body, envelope } = serializePublicFailure(code);
  const bodyless = request.method === "HEAD";
  const headers = [
    `HTTP/1.1 ${status} ${reason}`,
    "Connection: close",
    "Content-Type: application/json; charset=utf-8",
    `Content-Length: ${bodyless ? 0 : Buffer.byteLength(body, "utf8")}`,
    `X-Correlation-ID: ${envelope.correlation_id}`,
    `X-Error-Code: ${envelope.code}`,
    ...(allow ? [`Allow: ${allow}`] : []),
    "",
    bodyless ? "" : body,
  ];
  socket.end(headers.join("\r\n"));
}
