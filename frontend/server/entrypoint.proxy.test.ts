/** @vitest-environment node */

import http, { type IncomingHttpHeaders, type Server } from "node:http";
import net from "node:net";
import { once } from "node:events";
import { fileURLToPath } from "node:url";
import { inspect } from "node:util";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  requestLoopbackBounded,
} from "./test-support/bounded-http";

type HttpResponse = {
  status: number;
  headers: IncomingHttpHeaders;
  body: string;
};

const harness = vi.hoisted(() => ({
  servers: [] as Server[],
  applicationRequests: [] as string[],
  websocketConnections: 0,
  principal: vi.fn(),
  backendFetch: vi.fn(),
}));
const serverEntrypointModule: string = "../server.ts";

vi.mock("http", async (importOriginal) => {
  const actual = await importOriginal<typeof import("node:http")>();
  const createServer: typeof actual.createServer = ((...args: Parameters<typeof actual.createServer>) => {
    const server = actual.createServer(...args);
    harness.servers.push(server);
    return server;
  }) as typeof actual.createServer;

  return {
    ...actual,
    createServer,
    default: { ...actual, createServer },
  };
});

vi.mock("vite", () => ({
  createServer: vi.fn(async () => ({
    middlewares: (
      _req: http.IncomingMessage,
      _res: http.ServerResponse,
      next: (error?: unknown) => void,
    ) => next(),
    ssrFixStacktrace: vi.fn(),
    ssrLoadModule: vi.fn(async () => ({
      app: (
        req: http.IncomingMessage,
        res: http.ServerResponse,
      ) => {
        harness.applicationRequests.push(req.url ?? "");
        res.statusCode = 418;
        res.setHeader("Content-Type", "text/plain");
        res.end("application");
      },
      authenticateWebsocketUpgrade: harness.principal,
      initializeWebsocketServer: (websocketServer: {
        on: (event: string, listener: () => void) => void;
      }) => {
        websocketServer.on("connection", () => {
          harness.websocketConnections += 1;
        });
      },
    })),
  })),
}));

beforeEach(() => {
  vi.resetModules();
  vi.stubEnv("NODE_ENV", "development");
  vi.stubEnv("PORT", "0");
  vi.stubEnv("LISTEN_ADDRESS", "127.0.0.1");
  vi.stubEnv(
    "NZBDAV_ENV_FILE",
    fileURLToPath(new URL(".missing-entrypoint-test-env", import.meta.url)),
  );
  vi.stubEnv("BACKEND_URL", "http://backend.invalid:8080");
  harness.servers.length = 0;
  harness.applicationRequests.length = 0;
  harness.websocketConnections = 0;
  harness.principal.mockReset();
  harness.principal.mockResolvedValue(true);
  harness.backendFetch.mockReset();
  harness.backendFetch.mockResolvedValue({ ok: true });
  vi.stubGlobal("fetch", harness.backendFetch);
  vi.spyOn(console, "log").mockImplementation(() => undefined);
});

afterEach(async () => {
  const failures: Error[] = [];
  try {
    const results = await Promise.allSettled(
      harness.servers.splice(0).map(closeServer),
    );
    for (const result of results) {
      if (result.status === "rejected") {
        failures.push(new Error("Entrypoint server cleanup failed."));
      }
    }
  } finally {
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Entrypoint fixture cleanup failed.");
  }
});

describe("production server entrypoint boundary", () => {
  it.each([
    "..",
    "/a/../b",
    "/a/./b",
    "/a//b",
    "a\\b",
    "/a%2Fb",
    "/a%25b",
    "/a%2eb",
    "/a%b",
    "/a?b",
    "/a#b",
    "/a\nb",
    "/føø",
    "/a\u0085b",
    "/a\u00a0b",
    "/:base",
    "/a*rest",
    "/a(b)",
    "/a+b",
    "/a!b",
    "/a[bc]",
    "/a{bc}",
  ])("rejects ambiguous URL_BASE=%j before binding", async (urlBase) => {
    vi.stubEnv("URL_BASE", urlBase);

    await expect(import(serverEntrypointModule)).rejects.toThrow(/URL_BASE/);
    expect(harness.servers).toHaveLength(0);
  });

  it("mounts URL_BASE with exact case", async () => {
    const origin = await startServer("/nzbdav");

    const exactCase = await request(origin, "/nzbdav/__prime");
    harness.applicationRequests.length = 0;
    const changedCase = await request(origin, "/NZBDAV/__prime");

    expect(exactCase.status).toBe(418);
    expect(changedCase.status).toBe(404);
    expect(harness.applicationRequests).toHaveLength(0);
  });

  it("accepts the exact maximum WebDAV URL_BASE byte length", async () => {
    vi.stubEnv("URL_BASE", `/${"a".repeat(8_182)}`);

    await expect(import(serverEntrypointModule)).resolves.toBeDefined();
    expect(harness.servers).toHaveLength(1);
  });

  it("rejects URL_BASE one byte beyond the WebDAV PathBase bound", async () => {
    vi.stubEnv("URL_BASE", `/${"a".repeat(8_183)}`);

    await expect(import(serverEntrypointModule)).rejects.toThrow(/URL_BASE/);
    expect(harness.servers).toHaveLength(0);
  });

  it.each(["", "/nzbdav", "/edge/apps/nzbdav"])(
    "freezes root and mounted health behavior for URL_BASE=%s",
    async (urlBase) => {
      const origin = await startServer(urlBase);

      const processGet = await request(origin, "/healthz");
      expect(processGet).toMatchObject({ status: 200, body: "ok" });
      expect(processGet.headers["content-type"]).toMatch(/^text\/plain\b/);

      const processHead = await request(origin, "/healthz", { method: "HEAD" });
      expect(processHead).toMatchObject({ status: 200, body: "" });

      const wrongProcessMethod = await request(origin, "/healthz", { method: "POST" });
      expectStableEntrypointFailure(
        wrongProcessMethod,
        405,
        "method_not_allowed",
        "GET, HEAD",
      );

      harness.backendFetch.mockClear();
      const backendGet = await request(origin, "/health", {
        headers: { Accept: "text/plain" },
      });
      expect(backendGet).toMatchObject({ status: 200, body: "Healthy" });
      expect(harness.backendFetch).toHaveBeenCalledOnce();
      expect(harness.backendFetch).toHaveBeenCalledWith(
        "http://backend.invalid:8080/health",
        expect.objectContaining({ signal: expect.any(AbortSignal) }),
      );

      harness.backendFetch.mockClear();
      const backendHead = await request(origin, "/health", {
        method: "HEAD",
        headers: { Accept: "text/plain" },
      });
      expect(backendHead).toMatchObject({ status: 200, body: "" });
      expect(harness.backendFetch).toHaveBeenCalledOnce();

      harness.backendFetch.mockClear();
      const wrongBackendMethod = await request(origin, "/health", {
        method: "POST",
        headers: { Accept: "text/plain" },
      });
      expectStableEntrypointFailure(
        wrongBackendMethod,
        405,
        "method_not_allowed",
        "GET, HEAD",
      );
      expect(harness.backendFetch).not.toHaveBeenCalled();

      harness.applicationRequests.length = 0;
      const wrongHtmlBackendMethod = await request(origin, "/health", {
        method: "POST",
        headers: { Accept: "text/html" },
      });
      expectStableEntrypointFailure(
        wrongHtmlBackendMethod,
        405,
        "method_not_allowed",
        "GET, HEAD",
      );
      expect(harness.backendFetch).not.toHaveBeenCalled();
      expect(harness.applicationRequests).toHaveLength(0);

      const browserPath = `${urlBase}/health` || "/health";
      const browserHealth = await request(origin, browserPath, {
        headers: { Accept: "text/html" },
      });
      expect(browserHealth).toMatchObject({ status: 418, body: "application" });
      expect(harness.applicationRequests).toContain("/health");
      expect(harness.backendFetch).not.toHaveBeenCalled();

      if (urlBase) {
        harness.applicationRequests.length = 0;
        const mountedTextHealth = await request(origin, `${urlBase}/health`, {
          headers: { Accept: "text/plain" },
        });
        const mountedHealthz = await request(origin, `${urlBase}/healthz`, {
          headers: { Accept: "text/plain" },
        });
        expect(mountedTextHealth).toMatchObject({ status: 418, body: "application" });
        expect(mountedHealthz).toMatchObject({ status: 418, body: "application" });
        expect(harness.applicationRequests).toEqual(["/health", "/healthz"]);
        expect(harness.backendFetch).not.toHaveBeenCalled();

        const bareBrowserHealth = await request(origin, "/health", {
          headers: { Accept: "text/html" },
        });
        expect(bareBrowserHealth.status).toBe(404);
        expect(harness.applicationRequests).not.toContain(`${urlBase}/health`);
      }
    },
  );

  it("accepts a proxy-facing matching Host and Origin authority", async () => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    const authority = "media.example.test:443";

    const response = await websocketHandshake(
      origin,
      "/nzbdav/ws",
      [
        ["Origin", `https://${authority}`],
        ["Forwarded", "for=192.0.2.10;proto=http;host=evil.invalid"],
        ["X-Forwarded-For", "192.0.2.10"],
        ["X-Forwarded-Host", "evil.invalid"],
        ["X-Forwarded-Port", "80"],
        ["X-Forwarded-Prefix", "/evil"],
        ["X-Forwarded-Proto", "http"],
        ["X-Forwarded-Server", "evil.invalid"],
      ],
      [authority],
    );

    expect(response.status).toBe(101);
    expect(harness.principal).toHaveBeenCalledOnce();
    expect(harness.websocketConnections).toBe(1);
  });

  it.each([
    [512, 101, 1],
    [513, 400, 0],
  ] as const)("enforces the %s-character WebSocket Origin boundary", async (length, status, principalCalls) => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    const originHeader = originOfLength(length);
    const authority = new URL(originHeader).host;

    const response = await websocketHandshake(
      origin,
      "/nzbdav/ws",
      [["Origin", originHeader]],
      [authority],
    );

    if (status === 101) expect(response.status).toBe(101);
    else expectStableEntrypointFailure(response, 400, "invalid_request_target");
    expect(harness.principal).toHaveBeenCalledTimes(principalCalls);
    expect(harness.websocketConnections).toBe(principalCalls);
  });

  it("bounds backend liveness failure responses", async () => {
    const origin = await startServer("/nzbdav");

    harness.backendFetch.mockResolvedValueOnce({ ok: false });
    const unhealthy = await request(origin, "/health", {
      headers: { Accept: "text/plain" },
    });
    expectStableEntrypointFailure(unhealthy, 503, "upstream_unavailable");

    harness.backendFetch.mockRejectedValueOnce(new Error("health-canary"));
    const unreachable = await request(origin, "/health", {
      headers: { Accept: "text/plain" },
    });
    expectStableEntrypointFailure(unreachable, 503, "upstream_unavailable");
    expect(unreachable.body).not.toContain("health-canary");
  });

  it("aborts a non-settling backend liveness request at the exact bound", async () => {
    const origin = await startServer("/nzbdav");
    let observedAbort = false;
    const timeoutSpy = vi.spyOn(AbortSignal, "timeout").mockImplementation((milliseconds) => {
      expect(milliseconds).toBe(3_000);
      const controller = new AbortController();
      setImmediate(() => controller.abort());
      return controller.signal;
    });
    harness.backendFetch.mockImplementationOnce((_input, init: RequestInit | undefined) => (
      new Promise((_resolve, reject) => {
        const signal = init?.signal;
        if (!(signal instanceof AbortSignal)) {
          reject(new Error("Missing liveness signal"));
          return;
        }
        signal.addEventListener("abort", () => {
          observedAbort = true;
          reject(new Error("Synthetic bounded liveness abort"));
        }, { once: true });
      })
    ));

    const response = await request(origin, "/health", {
      headers: { Accept: "text/plain" },
    });

    expectStableEntrypointFailure(response, 503, "upstream_unavailable");
    expect(observedAbort).toBe(true);
    expect(timeoutSpy).toHaveBeenCalledOnce();
    expect(timeoutSpy).toHaveBeenCalledWith(3_000);
  });

  it.each([
    ["/", ""],
    ["nzbdav/", "/nzbdav"],
    [" /edge/apps/nzbdav/// ", "/edge/apps/nzbdav"],
  ])("normalizes valid URL_BASE=%j to %s for HTTP and WebSocket paths", async (configured, normalized) => {
    const origin = await startServer(configured);
    if (normalized) {
      const root = await request(origin, "/");
      expect(root.status).toBe(302);
      expect(root.headers.location).toBe(`${normalized}/`);
    }

    await primeApplication(origin, normalized);
    const authority = new URL(origin).host;
    const response = await websocketHandshake(
      origin,
      `${normalized}/ws`,
      [["Origin", `http://${authority}`]],
    );

    expect(response.status).toBe(101);
    expect(harness.principal).toHaveBeenCalledOnce();
    expect(harness.websocketConnections).toBe(1);
  });

  it.each([
    ["", "http"],
    ["", "https"],
    ["/nzbdav", "http"],
    ["/nzbdav", "https"],
    ["/edge/apps/nzbdav", "http"],
    ["/edge/apps/nzbdav", "https"],
  ])(
    "authenticates a WebSocket-first exact upgrade before 101 for URL_BASE=%s Origin=%s",
    async (urlBase, originScheme) => {
      const origin = await startServer(urlBase);
      const authority = new URL(origin).host;
      const response = await websocketHandshake(
        origin,
        `${urlBase}/ws` || "/ws",
        [["Origin", `${originScheme}://${authority}`]],
      );

      expect(response.status).toBe(101);
      expect(harness.principal).toHaveBeenCalledOnce();
      expect(harness.principal.mock.calls[0][0]).toMatchObject({
        url: `${urlBase}/ws` || "/ws",
      });
      expect(harness.websocketConnections).toBe(1);
    },
  );

  it.each([
    ["trailing slash", "/nzbdav/ws/", 404],
    ["empty query", "/nzbdav/ws?", 404],
    ["query", "/nzbdav/ws?topic=queue", 404],
    ["protocol prefix", "/nzbdav/protocol/ws", 404],
    ["bare path under a base", "/ws", 404],
    ["duplicate base", "/nzbdav/nzbdav/ws", 404],
    ["case change", "/nzbdav/WS", 404],
    ["encoded path", "/nzbdav/%77s", 404],
    ["double-encoded path", "/nzbdav/%2577s", 404],
    ["prefix confusion", "/nzbdav/ws.evil", 404],
    ["absolute form", "http://foreign.invalid/nzbdav/ws", 400],
    ["asterisk form", "*", 400],
  ] as const)("rejects the %s WebSocket target before 101", async (_name, target, status) => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    const authority = new URL(origin).host;

    const response = await websocketHandshake(origin, target, [
      ["Origin", `http://${authority}`],
    ]);

    expectStableEntrypointFailure(
      response,
      status,
      status === 400 ? "invalid_request_target" : "route_not_found",
    );
    expect(harness.principal).not.toHaveBeenCalled();
    expect(harness.websocketConnections).toBe(0);
  });

  it("rejects a wrong-method exact WebSocket target before principal evaluation", async () => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    const authority = new URL(origin).host;

    const response = await websocketHandshake(
      origin,
      "/nzbdav/ws",
      [["Origin", `http://${authority}`]],
      undefined,
      "POST",
    );

    expectStableEntrypointFailure(response, 405, "method_not_allowed", "GET");
    expect(harness.principal).not.toHaveBeenCalled();
    expect(harness.websocketConnections).toBe(0);
  });

  it.each([
    ["missing", []],
    ["empty", [["Origin", ""]]],
    ["opaque", [["Origin", "null"]]],
    ["unsupported scheme", [["Origin", "ftp://{{authority}}"]]],
    ["foreign", [["Origin", "https://foreign.invalid"]]],
    ["credentials", [["Origin", "https://user@{{authority}}"]]],
    ["path", [["Origin", "https://{{authority}}/path"]]],
    ["trailing slash", [["Origin", "https://{{authority}}/"]]],
    ["query", [["Origin", "https://{{authority}}?query"]]],
    ["fragment", [["Origin", "https://{{authority}}#fragment"]]],
    ["malformed", [["Origin", "https://not valid.invalid"]]],
    ["repeated", [["Origin", "http://{{authority}}"], ["Origin", "http://{{authority}}"]]],
    [
      "forwarded spoof",
      [["Origin", "https://foreign.invalid"], ["Forwarded", "host=127.0.0.1"], ["X-Forwarded-Host", "127.0.0.1"]],
    ],
  ] as const)("rejects a %s WebSocket Origin before 101", async (_name, originHeaders) => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    const authority = new URL(origin).host;
    const headers = originHeaders.map(([name, value]) => [
      name,
      value.replaceAll("{{authority}}", authority),
    ] as const);

    const response = await websocketHandshake(origin, "/nzbdav/ws", headers);

    expectStableEntrypointFailure(response, 400, "invalid_request_target");
    expect(harness.principal).not.toHaveBeenCalled();
    expect(harness.websocketConnections).toBe(0);
  });

  it.each([
    ["missing", []],
    ["empty", [""]],
    ["repeated", ["media.example.test", "media.example.test"]],
    ["malformed whitespace", ["media example.test"]],
    ["credentials", ["user@media.example.test"]],
    ["path", ["media.example.test/path"]],
    ["query", ["media.example.test?query"]],
    ["fragment", ["media.example.test#fragment"]],
  ] as const)("rejects a %s WebSocket Host before principal evaluation", async (_name, hosts) => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");

    const response = await websocketHandshake(
      origin,
      "/nzbdav/ws",
      [["Origin", "https://media.example.test"]],
      [...hosts],
    );

    expectStableEntrypointFailure(response, 400, "invalid_request_target");
    expect(harness.principal).not.toHaveBeenCalled();
    expect(harness.websocketConnections).toBe(0);
  });

  it("rejects a WebSocket Origin whose authority differs from Host", async () => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");

    const response = await websocketHandshake(
      origin,
      "/nzbdav/ws",
      [["Origin", "https://origin.example.test"]],
      ["host.example.test"],
    );

    expectStableEntrypointFailure(response, 400, "invalid_request_target");
    expect(harness.principal).not.toHaveBeenCalled();
    expect(harness.websocketConnections).toBe(0);
  });

  it("rejects an absent principal before 101", async () => {
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    harness.principal.mockResolvedValue(false);
    const authority = new URL(origin).host;

    const response = await websocketHandshake(origin, "/nzbdav/ws", [
      ["Origin", `http://${authority}`],
    ]);

    expectStableEntrypointFailure(response, 401, "authentication_required");
    expect(harness.principal).toHaveBeenCalledOnce();
    expect(harness.websocketConnections).toBe(0);
  });

  it("fails closed before 101 when principal evaluation throws", async () => {
    const errorLog = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const warningLog = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const infoLog = vi.spyOn(console, "info").mockImplementation(() => undefined);
    const debugLog = vi.spyOn(console, "debug").mockImplementation(() => undefined);
    const standardOutput = vi.spyOn(process.stdout, "write")
      .mockImplementation((() => true) as typeof process.stdout.write);
    const standardError = vi.spyOn(process.stderr, "write")
      .mockImplementation((() => true) as typeof process.stderr.write);
    const origin = await startServer("/nzbdav");
    await primeApplication(origin, "/nzbdav");
    harness.principal.mockRejectedValue(new Error("principal-canary"));
    const authority = new URL(origin).host;

    const response = await websocketHandshake(origin, "/nzbdav/ws", [
      ["Origin", `http://${authority}`],
    ]);

    expectStableEntrypointFailure(response, 500, "internal_error");
    const output = [
      JSON.stringify(response.headers),
      response.body,
      ...[
        ...errorLog.mock.calls,
        ...warningLog.mock.calls,
        ...infoLog.mock.calls,
        ...debugLog.mock.calls,
        ...vi.mocked(console.log).mock.calls,
      ].flatMap((call) => call.map((value) => inspect(value, { depth: 8 }))),
      ...standardOutput.mock.calls.map((call) => renderProcessWrite(call[0])),
      ...standardError.mock.calls.map((call) => renderProcessWrite(call[0])),
    ].join(" ");
    expect(output).not.toContain("principal-canary");
    expect(harness.principal).toHaveBeenCalledOnce();
    expect(harness.websocketConnections).toBe(0);
  });
});

function renderProcessWrite(value: unknown): string {
  if (typeof value === "string") return value;
  if (value instanceof Uint8Array) return Buffer.from(value).toString("utf8");
  return inspect(value, { depth: 8 });
}

function expectStableEntrypointFailure(
  response: HttpResponse,
  status: number,
  code: string,
  allow?: string,
): void {
  expect(response.status).toBe(status);
  expect(response.headers["content-type"]).toMatch(/^application\/json\b/u);
  const correlationId = response.headers["x-correlation-id"];
  expect(correlationId).toMatch(/^[0-9a-f]{32}$/u);
  expect(response.headers["x-error-code"]).toBe(code);
  expect(JSON.parse(response.body)).toEqual({
    status: false,
    error: entrypointFailureMessage(code),
    code,
    correlation_id: correlationId,
  });
  if (allow === undefined) expect(response.headers.allow).toBeUndefined();
  else expect(response.headers.allow).toBe(allow);
}

function entrypointFailureMessage(code: string): string {
  switch (code) {
    case "invalid_request_target": return "The request is invalid.";
    case "route_not_found": return "The requested route was not found.";
    case "method_not_allowed": return "The request method is not allowed.";
    case "authentication_required": return "Authentication is required.";
    case "upstream_unavailable": return "The backend is unavailable.";
    default: return "The request could not be completed.";
  }
}

describe("bounded WebSocket handshake response parsing", () => {
  it("preserves repeated response headers for redaction inspection", () => {
    const response = parseBoundedHandshakeResponse(Buffer.from([
      "HTTP/1.1 500 Internal Server Error",
      "Content-Length: 0",
      "X-Fixture: first",
      "X-Fixture: second",
      "",
      "",
    ].join("\r\n")), true);

    expect(response?.headers["x-fixture"]).toEqual(["first", "second"]);
  });

  it("rejects repeated response framing headers", () => {
    const raw = Buffer.from([
      "HTTP/1.1 500 Internal Server Error",
      "Content-Length: 0",
      "Content-Length: 0",
      "",
      "",
    ].join("\r\n"));

    expect(() => parseBoundedHandshakeResponse(raw, true)).toThrow();
  });

  it("rejects bytes after the declared response length", () => {
    const raw = Buffer.from([
      "HTTP/1.1 500 Internal Server Error",
      "Content-Length: 2",
      "",
      "{}trailing",
    ].join("\r\n"));

    expect(() => parseBoundedHandshakeResponse(raw, true)).toThrow();
  });

  it("rejects bytes after the terminating response chunk", () => {
    const raw = Buffer.from([
      "HTTP/1.1 500 Internal Server Error",
      "Transfer-Encoding: chunked",
      "",
      "2",
      "{}",
      "0",
      "",
      "trailing",
    ].join("\r\n"));

    expect(() => parseBoundedHandshakeResponse(raw, true)).toThrow();
  });
});

async function startServer(urlBase: string): Promise<string> {
  vi.stubEnv("URL_BASE", urlBase);
  await import(serverEntrypointModule);
  const server = harness.servers.at(-1);
  if (!server) throw new Error("Production entrypoint did not create a server");
  if (!server.listening) await once(server, "listening");
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Production entrypoint did not bind a disposable TCP port");
  }
  return `http://127.0.0.1:${address.port}`;
}

async function primeApplication(origin: string, urlBase: string): Promise<void> {
  const response = await request(origin, `${urlBase}/__prime` || "/__prime");
  expect(response.status).toBe(418);
}

function request(
  origin: string,
  path: string,
  options: { method?: string; headers?: http.OutgoingHttpHeaders } = {},
): Promise<HttpResponse> {
  return requestLoopbackBounded(
    origin,
    path,
    options,
    { timeoutMs: 5_000, maxResponseBytes: 64 * 1024 },
  ).then((response) => ({
    status: response.status,
    headers: response.headers,
    body: response.body.toString("utf8"),
  }));
}

function websocketHandshake(
  origin: string,
  target: string,
  extraHeaders: readonly (readonly [string, string])[],
  hostHeaders?: readonly string[],
  method = "GET",
): Promise<HttpResponse> {
  const url = new URL(origin);
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: url.hostname, port: Number(url.port) });
    let response = Buffer.alloc(0);
    let settled = false;
    const timeout = setTimeout(() => {
      finish(new Error("Disposable WebSocket handshake timed out"));
    }, 2_000);

    const finish = (error?: Error, value?: HttpResponse) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      if (error) reject(error);
      else if (value) resolve(value);
      else reject(new Error("Disposable WebSocket handshake failed"));
    };
    const complete = (eof: boolean) => {
      if (settled) return;
      try {
        const parsed = parseBoundedHandshakeResponse(response, eof);
        if (parsed) finish(undefined, parsed);
      } catch {
        finish(new Error("Invalid disposable handshake response"));
      }
    };
    socket.once("error", (error) => {
      finish(error);
    });
    socket.once("end", () => complete(true));
    socket.once("close", () => complete(true));
    socket.once("connect", () => {
      const lines = [
        `${method} ${target} HTTP/1.1`,
        ...(hostHeaders ?? [url.host]).map((value) => `Host: ${value}`),
        "Connection: Upgrade",
        "Upgrade: websocket",
        "Sec-WebSocket-Version: 13",
        `Sec-WebSocket-Key: ${Buffer.alloc(16, 1).toString("base64")}`,
        ...extraHeaders.map(([name, value]) => `${name}: ${value}`),
        "",
        "",
      ];
      socket.write(lines.join("\r\n"));
    });
    socket.on("data", (chunk) => {
      if (response.byteLength + chunk.byteLength > 64 * 1024) {
        finish(new Error("Disposable WebSocket response exceeded its bound"));
        return;
      }
      response = Buffer.concat([response, chunk]);
      complete(false);
    });
  });
}

function parseBoundedHandshakeResponse(
  raw: Buffer,
  eof: boolean,
): HttpResponse | undefined {
  const headerEnd = raw.indexOf("\r\n\r\n");
  if (headerEnd < 0) {
    if (eof) throw new Error("incomplete");
    return undefined;
  }
  const [statusLine, ...headerLines] = raw
    .subarray(0, headerEnd)
    .toString("utf8")
    .split("\r\n");
  const match = /^HTTP\/1\.1 (\d{3})\b/u.exec(statusLine);
  if (!match) throw new Error("status");
  const headers: IncomingHttpHeaders = {};
  for (const line of headerLines) {
    const separator = line.indexOf(":");
    if (separator < 0) throw new Error("header");
    const name = line.slice(0, separator).toLowerCase();
    const value = line.slice(separator + 1).trim();
    const existing = headers[name];
    headers[name] = existing === undefined
      ? value
      : Array.isArray(existing)
        ? [...existing, value]
        : [existing, value];
  }
  const status = Number(match[1]);
  const encodedBody = raw.subarray(headerEnd + 4);
  if (status === 101) return { status, headers, body: "" };

  const declaredLength = headers["content-length"];
  const transferEncoding = headers["transfer-encoding"];
  if (declaredLength !== undefined && transferEncoding !== undefined) {
    throw new Error("framing");
  }
  if (declaredLength !== undefined) {
    if (Array.isArray(declaredLength) || !/^\d+$/u.test(declaredLength)) {
      throw new Error("length");
    }
    const length = Number(declaredLength);
    if (!Number.isSafeInteger(length) || length > 64 * 1024) throw new Error("length");
    if (encodedBody.byteLength < length) {
      if (eof) throw new Error("truncated");
      return undefined;
    }
    if (encodedBody.byteLength > length) throw new Error("trailing");
    if (!eof) return undefined;
    return {
      status,
      headers,
      body: encodedBody.subarray(0, length).toString("utf8"),
    };
  }
  if (transferEncoding !== undefined) {
    if (Array.isArray(transferEncoding)
      || transferEncoding.split(",").at(-1)?.trim().toLowerCase() !== "chunked") {
      throw new Error("encoding");
    }
    const decoded = decodeCompleteChunkedBody(encodedBody);
    if (!decoded) {
      if (eof) throw new Error("truncated");
      return undefined;
    }
    if (decoded.consumed !== encodedBody.byteLength) throw new Error("trailing");
    if (!eof) return undefined;
    return { status, headers, body: decoded.body.toString("utf8") };
  }
  if (!eof) return undefined;
  return { status, headers, body: encodedBody.toString("utf8") };
}

function decodeCompleteChunkedBody(
  encoded: Buffer,
): { body: Buffer; consumed: number } | undefined {
  const chunks: Buffer[] = [];
  let offset = 0;
  let total = 0;
  while (true) {
    const lineEnd = encoded.indexOf("\r\n", offset);
    if (lineEnd < 0) return undefined;
    const sizeLine = encoded.subarray(offset, lineEnd).toString("ascii");
    const match = /^([0-9a-f]+)(?:;[^\r\n]*)?$/iu.exec(sizeLine);
    if (!match) throw new Error("chunk");
    const size = Number.parseInt(match[1], 16);
    if (!Number.isSafeInteger(size) || size > 64 * 1024 - total) {
      throw new Error("chunk");
    }
    offset = lineEnd + 2;
    if (size === 0) {
      if (encoded.byteLength < offset + 2) return undefined;
      if (encoded.subarray(offset, offset + 2).equals(Buffer.from("\r\n"))) {
        return { body: Buffer.concat(chunks, total), consumed: offset + 2 };
      }
      const trailersEnd = encoded.indexOf("\r\n\r\n", offset);
      return trailersEnd < 0
        ? undefined
        : { body: Buffer.concat(chunks, total), consumed: trailersEnd + 4 };
    }
    if (encoded.byteLength < offset + size + 2) return undefined;
    if (!encoded.subarray(offset + size, offset + size + 2).equals(Buffer.from("\r\n"))) {
      throw new Error("chunk");
    }
    chunks.push(encoded.subarray(offset, offset + size));
    total += size;
    offset += size + 2;
  }
}

function closeServer(server: Server): Promise<void> {
  return closeHttpServerBounded(server, 5_000);
}

function originOfLength(length: number): string {
  const prefix = "https://";
  if (length <= prefix.length) throw new Error("Origin fixture length is too small");
  return `${prefix}${"a".repeat(length - prefix.length)}`;
}
