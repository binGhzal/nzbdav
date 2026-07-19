/** @vitest-environment node */

import { once } from "node:events";
import http, {
  type IncomingHttpHeaders,
  type IncomingMessage,
  type Server,
} from "node:http";
import { createRequire } from "node:module";
import net from "node:net";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";
import type { NextFunction, Request, RequestHandler, Response } from "express";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebSocketServer } from "ws";
import type { ProductionEntrypointFixture } from "./test-support/production-entrypoint-build";
import {
  closeHttpServerBounded,
  requestLoopbackBounded,
} from "./test-support/bounded-http";

type HttpResponse = {
  status: number;
  headers: IncomingHttpHeaders;
  body: Buffer;
};

type BackendCapture = {
  headers: IncomingHttpHeaders;
  method: string;
  url: string;
};

const harness = vi.hoisted(() => ({
  accessLogs: [] as string[],
  application: vi.fn(),
  authentication: vi.fn(),
  backendCaptures: [] as BackendCapture[],
  initializeWebsocketServer: vi.fn(),
  principal: vi.fn(),
  servers: [] as Server[],
  websocketConnections: 0,
}));
const serverEntrypointModule: string = "../server.ts";
const originalDebug = process.env.DEBUG;
const debugControl = createRequire(import.meta.url)("debug") as {
  enable: (namespaces: string) => void;
};

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

vi.mock("morgan", async (importOriginal) => {
  const actual = await importOriginal<typeof import("morgan")>();
  const realMorgan = (
    actual as typeof actual & { default?: typeof actual }
  ).default ?? actual;
  const wrapped = ((
    format: string,
    options?: Parameters<typeof realMorgan>[1],
  ) => realMorgan(format, {
    ...options,
    stream: {
      write(line: string) {
        harness.accessLogs.push(line);
      },
    },
  })) as typeof realMorgan;

  return { ...actual, default: wrapped };
});

vi.mock("../build/server/index.js", () => ({
  app: (req: Request, res: Response, next: NextFunction) => {
    return globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__!.app(req, res, next);
  },
  authenticateWebsocketUpgrade: (request: IncomingMessage) => {
    return globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__!
      .authenticateWebsocketUpgrade(request);
  },
  initializeWebsocketServer: (websocketServer: WebSocketServer) => {
    globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__!
      .initializeWebsocketServer(websocketServer);
  },
}));

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: harness.authentication,
}));

vi.mock("~/auth/auth-middleware.server", () => ({
  authMiddleware: async (req: Request, res: Response, next: NextFunction) => {
    if (await harness.authentication(req)) {
      next();
      return;
    }
    res.sendStatus(401);
  },
}));

vi.mock("./react-router-handler", () => ({
  createPinrailRequestHandler: () => (
    _req: Request,
    res: Response,
  ) => res.status(418).type("text/plain").send("application"),
}));

beforeEach(() => {
  vi.resetModules();
  vi.stubEnv("NODE_ENV", "production");
  vi.stubEnv("PORT", "0");
  vi.stubEnv("LISTEN_ADDRESS", "127.0.0.1");
  vi.stubEnv(
    "NZBDAV_ENV_FILE",
    fileURLToPath(new URL(".missing-production-entrypoint-test-env", import.meta.url)),
  );
  vi.stubEnv("BACKEND_URL", "http://backend.invalid:8080");
  vi.stubEnv("FRONTEND_BACKEND_API_KEY", "unit");
  harness.accessLogs.length = 0;
  harness.application.mockReset();
  harness.authentication.mockReset();
  harness.authentication.mockResolvedValue(false);
  harness.backendCaptures.length = 0;
  harness.initializeWebsocketServer.mockReset();
  harness.initializeWebsocketServer.mockImplementation((server: WebSocketServer) => {
    server.on("connection", () => {
      harness.websocketConnections += 1;
    });
  });
  harness.principal.mockReset();
  harness.principal.mockResolvedValue(true);
  harness.servers.length = 0;
  harness.websocketConnections = 0;

  globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__ = {
    app: ((req, res, next) => harness.application(req, res, next)) as RequestHandler,
    authenticateWebsocketUpgrade: harness.principal,
    initializeWebsocketServer: harness.initializeWebsocketServer,
  } satisfies ProductionEntrypointFixture;

  vi.spyOn(console, "log").mockImplementation(() => undefined);
  vi.spyOn(console, "error").mockImplementation(() => undefined);
});

afterEach(async () => {
  const failures: Error[] = [];
  try {
    const results = await Promise.allSettled(
      harness.servers.splice(0).map(closeServer),
    );
    for (const result of results) {
      if (result.status === "rejected") {
        failures.push(new Error("Production entrypoint server cleanup failed."));
      }
    }
  } finally {
    delete globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__;
    debugControl.enable(originalDebug ?? "");
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Production entrypoint cleanup failed.");
  }
});

describe("production server entrypoint sanitization", () => {
  it("seals an outer pre-header Express failure", async () => {
    const hostile = "outer-terminal-canary|/private/runtime/path|credential-marker";
    harness.application.mockImplementation((
      _req: Request,
      _res: Response,
      next: NextFunction,
    ) => next(new Error(hostile)));
    const origin = await startProductionServer("/nzbdav");

    const response = await request(origin, "/nzbdav/__terminal-before-headers");

    expectStableProductionFailure(response, 500, "internal_error");
    expect(vi.mocked(console.error).mock.calls)
      .toEqual([["frontend_http_failure code=internal_error"]]);
    expect(JSON.stringify(vi.mocked(console.error).mock.calls)).not.toContain(hostile);
  });

  it("destroys an outer post-header Express failure", async () => {
    const hostile = "outer-post-header-canary|credential-marker";
    harness.application.mockImplementation((
      _req: Request,
      res: Response,
      next: NextFunction,
    ) => {
      res.status(200).type("text/plain").write("partial");
      next(new Error(hostile));
    });
    const origin = await startProductionServer("/nzbdav");

    await expect(request(origin, "/nzbdav/__terminal-after-headers"))
      .rejects.toThrow("Disposable HTTP request failed.");
    expect(vi.mocked(console.error).mock.calls)
      .toEqual([["frontend_http_failure code=internal_error"]]);
    expect(JSON.stringify(vi.mocked(console.error).mock.calls)).not.toContain(hostile);
  });

  it("fails closed on wildcard DEBUG before routing credential-bearing targets", async () => {
    const wildcardDebug = "*,-http-proxy-middleware*";
    const standardOutput = vi.spyOn(process.stdout, "write")
      .mockImplementation((() => true) as typeof process.stdout.write);
    const standardError = vi.spyOn(process.stderr, "write")
      .mockImplementation((() => true) as typeof process.stderr.write);
    vi.stubEnv("DEBUG", wildcardDebug);
    debugControl.enable(wildcardDebug);
    harness.application.mockImplementation((_req: Request, res: Response) => {
      res.status(404).type("text/plain").send("missing");
    });
    const origin = await startProductionServer("/nzbdav");

    const response = await request(
      origin,
      "/nzbdav/protocol/api?apikey=query-debug-canary&downloadKey=capability-debug-canary",
    );
    await new Promise<void>((resolve) => setImmediate(resolve));

    const output = [
      ...standardOutput.mock.calls.map((call) => renderProcessWrite(call[0])),
      ...standardError.mock.calls.map((call) => renderProcessWrite(call[0])),
    ].join(" ");
    expect(response.status).toBe(404);
    expect(output).not.toContain("query-debug-canary");
    expect(output).not.toContain("capability-debug-canary");
  });

  it("does not write query or capability values to the production access log", async () => {
    harness.application.mockImplementation((_req: Request, res: Response) => {
      res.status(404).type("text/plain").send("missing");
    });
    const origin = await startProductionServer("/nzbdav");
    const longPath = `/nzbdav/${"a".repeat(8_000)}`;

    const response = await request(
      origin,
      `${longPath}?apikey=query-canary&downloadKey=capability-canary`,
    );

    expect(response.status).toBe(404);
    await vi.waitFor(() => expect(harness.accessLogs).toHaveLength(1));
    const accessLog = harness.accessLogs[0];
    expect(accessLog.trim()).toBe("frontend_http_failure status=404");
    expect(accessLog).not.toContain("?");
    expect(accessLog).not.toContain("apikey");
    expect(accessLog).not.toContain("downloadKey");
    expect(accessLog).not.toContain("query-canary");
    expect(accessLog).not.toContain("capability-canary");
    expect(Buffer.byteLength(accessLog, "utf8")).toBeLessThanOrEqual(512);
  });

  it("does not render a request-controlled valid custom method in a failure log", async () => {
    harness.application.mockImplementation((_req: Request, res: Response) => {
      res.status(404).type("text/plain").send("missing");
    });
    const origin = await startProductionServer("/nzbdav");
    const methodCanary = "M-SEARCH";

    const response = await request(origin, "/nzbdav/missing", { method: methodCanary });

    expect(response.status).toBe(404);
    await vi.waitFor(() => expect(harness.accessLogs).toHaveLength(1));
    expect(harness.accessLogs[0].trim()).toBe("frontend_http_failure status=404");
    expect(harness.accessLogs[0]).not.toContain(methodCanary);
  });

  it.each(["", "/nzbdav", "/edge/apps/nzbdav"])(
    "keeps protocol responses byte-identical while ordinary UI text remains compressible under URL_BASE=%s",
    async (urlBase) => {
    const body = Buffer.from("compressible-entrypoint-body\n".repeat(256), "utf8");
    harness.application.mockImplementation((_req: Request, res: Response) => {
      res.statusCode = 200;
      res.setHeader("Content-Type", "text/plain; charset=utf-8");
      res.setHeader("Content-Length", String(body.length));
      res.end(body);
    });
    const origin = await startProductionServer(urlBase);

    const protocol = await request(origin, `${urlBase}/protocol/content/file.txt`, {
      headers: { "Accept-Encoding": "gzip" },
    });
    const ui = await request(origin, `${urlBase}/large-ui`, {
      headers: { "Accept-Encoding": "gzip" },
    });

    expect(ui.status).toBe(200);
    expect(ui.headers["content-encoding"]).toBe("gzip");
    expect(gunzipSync(ui.body)).toEqual(body);
    expect(protocol.status).toBe(200);
    expect(protocol.headers["content-encoding"]).toBeUndefined();
    expect(protocol.headers["content-length"]).toBe(String(body.length));
    expect(protocol.body).toEqual(body);
    },
  );

  it("does not let server compression preempt a bounded malformed-target rejection", async () => {
    harness.application.mockImplementation((_req: Request, res: Response) => {
      res.status(400).type("application/json").send({
        error: "invalid_request_target",
      });
    });
    const origin = await startProductionServer("/nzbdav");

    const response = await rawRequest(origin, "/nzbdav/protocol/%");

    expect(response.status).toBe(400);
    expect(response.headers["content-type"]).toMatch(/^application\/json\b/);
    expect(response.headers["content-encoding"]).toBeUndefined();
    expect(response.body.toString("utf8")).toBe('{"error":"invalid_request_target"}');
    expect(response.body.byteLength).toBeLessThanOrEqual(128);
  });

  it("composes the real application proxy under the production compression boundary", async () => {
    const backendBody = Buffer.from("captured-protocol-body\n".repeat(256), "utf8");
    const contentRange = `bytes 10-${backendBody.length + 9}/${backendBody.length + 100}`;
    const etag = '"fixture-etag"';
    const backend = http.createServer((req, res) => {
      harness.backendCaptures.push({
        headers: req.headers,
        method: req.method ?? "",
        url: req.url ?? "",
      });
      res.statusCode = 206;
      res.setHeader("Content-Type", "text/plain; charset=utf-8");
      res.setHeader("Content-Length", String(backendBody.length));
      res.setHeader("Content-Range", contentRange);
      res.setHeader("ETag", etag);
      res.end(backendBody);
    });
    harness.servers.push(backend);
    const backendOrigin = await listen(backend);
    vi.stubEnv("BACKEND_URL", backendOrigin);
    const { app } = await import("./app");
    harness.application.mockImplementation((req: Request, res: Response, next: NextFunction) => {
      return app(req, res, next);
    });
    const origin = await startProductionServer("/nzbdav");

    const response = await request(origin, "/nzbdav/protocol/content/file.txt", {
      headers: {
        Accept: "text/plain",
        "Accept-Encoding": "gzip",
        Authorization: "Basic fixture",
        "If-Range": etag,
        Range: "bytes=10-",
      },
    });

    expect(response.status).toBe(206);
    expect(harness.backendCaptures).toHaveLength(1);
    expect(harness.backendCaptures[0]).toMatchObject({
      method: "GET",
      url: "/content/file.txt",
    });
    expect(harness.backendCaptures[0].headers.authorization).toBe("Basic fixture");
    expect(harness.backendCaptures[0].headers.range).toBe("bytes=10-");
    expect(harness.backendCaptures[0].headers["if-range"]).toBe(etag);
    expect(harness.backendCaptures[0].headers["x-api-key"]).toBeUndefined();
    expect(response.headers["content-encoding"]).toBeUndefined();
    expect(response.headers["content-length"]).toBe(String(backendBody.length));
    expect(response.headers["content-range"]).toBe(contentRange);
    expect(response.headers.etag).toBe(etag);
    expect(response.body).toEqual(backendBody);
  });

  it("uses the built production principal callback before a raw WebSocket upgrade", async () => {
    const origin = await startProductionServer("/nzbdav");
    harness.principal.mockResolvedValue(false);

    const denied = await websocketHandshake(origin);
    await new Promise<void>((resolve) => setImmediate(resolve));
    const connectionsAfterDenial = harness.websocketConnections;

    harness.principal.mockResolvedValue(true);
    const allowed = await websocketHandshake(origin);
    await vi.waitFor(() => expect(harness.websocketConnections).toBeGreaterThanOrEqual(1));

    expect({
      denied: denied.status,
      connectionsAfterDenial,
      allowed: allowed.status,
      connectionsAfterAllowed: harness.websocketConnections,
      principalCalls: harness.principal.mock.calls.length,
    }).toEqual({
      denied: 401,
      connectionsAfterDenial: 0,
      allowed: 101,
      connectionsAfterAllowed: 1,
      principalCalls: 2,
    });
  });

  it("constructs the production WebSocketServer with the explicit receiver bounds", async () => {
    await startProductionServer("/nzbdav");

    expect(harness.initializeWebsocketServer).toHaveBeenCalledOnce();
    const websocketServer = harness.initializeWebsocketServer.mock.calls[0][0] as WebSocketServer & {
      options: Record<string, unknown>;
    };
    expect(websocketServer.options).toMatchObject({
      noServer: true,
      maxPayload: 16 * 1024,
      maxFragments: 16,
      maxBufferedChunks: 32,
    });
  });
});

function renderProcessWrite(value: unknown): string {
  if (typeof value === "string") return value;
  if (value instanceof Uint8Array) return Buffer.from(value).toString("utf8");
  return String(value);
}

function expectStableProductionFailure(
  response: HttpResponse,
  status: number,
  code: string,
): void {
  expect(response.status).toBe(status);
  expect(response.headers["content-type"]).toMatch(/^application\/json\b/u);
  const correlationId = response.headers["x-correlation-id"];
  expect(correlationId).toMatch(/^[0-9a-f]{32}$/u);
  expect(response.headers["x-error-code"]).toBe(code);
  expect(JSON.parse(response.body.toString("utf8"))).toEqual({
    status: false,
    error: "The request could not be completed.",
    code,
    correlation_id: correlationId,
  });
}

async function startProductionServer(urlBase: string): Promise<string> {
  vi.stubEnv("URL_BASE", urlBase);
  await import(serverEntrypointModule);
  const server = harness.servers.at(-1);
  if (!server) throw new Error("Production entrypoint did not create a server");
  if (!server.listening) await once(server, "listening");
  return originOf(server);
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
    { timeoutMs: 5_000, maxResponseBytes: 4 * 1024 * 1024 },
  ).then((response) => ({
    status: response.status,
    headers: response.headers,
    body: response.body,
  }));
}

function rawRequest(origin: string, target: string): Promise<HttpResponse> {
  const url = new URL(origin);
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: url.hostname, port: Number(url.port) });
    const chunks: Buffer[] = [];
    let settled = false;
    const timeout = setTimeout(() => {
      finish(new Error("Disposable raw request timed out"));
    }, 2_000);

    const finish = (error?: Error, value?: HttpResponse) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      if (error) {
        reject(error);
        return;
      }
      if (value) resolve(value);
      else reject(new Error("Disposable raw request failed"));
    };
    const complete = (eof: boolean) => {
      if (settled) return;
      try {
        const parsed = parseRawResponse(Buffer.concat(chunks), eof);
        if (parsed) finish(undefined, parsed);
      } catch (parseError) {
        finish(parseError instanceof Error
          ? parseError
          : new Error("Disposable raw response was invalid"));
      }
    };

    socket.once("error", finish);
    socket.once("connect", () => {
      socket.write([
        `GET ${target} HTTP/1.1`,
        `Host: ${url.host}`,
        "Accept: application/json",
        "Accept-Encoding: gzip",
        "Connection: close",
        "",
        "",
      ].join("\r\n"));
    });
    let responseBytes = 0;
    socket.on("data", (chunk) => {
      responseBytes += chunk.byteLength;
      if (responseBytes > 64 * 1024) {
        finish(new Error("Disposable raw response exceeded its bound"));
        return;
      }
      chunks.push(chunk);
      complete(false);
    });
    socket.once("end", () => complete(true));
    socket.once("close", () => complete(true));
  });
}

function websocketHandshake(origin: string): Promise<Pick<HttpResponse, "status" | "headers">> {
  const url = new URL(origin);
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: url.hostname, port: Number(url.port) });
    let response = Buffer.alloc(0);
    let settled = false;
    const timeout = setTimeout(
      () => finish(new Error("Production WebSocket handshake timed out")),
      2_000,
    );
    const finish = (
      error?: Error,
      value?: Pick<HttpResponse, "status" | "headers">,
    ) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      if (error) reject(error);
      else if (value) resolve(value);
      else reject(new Error("Production WebSocket handshake failed"));
    };

    socket.once("error", (error) => finish(error));
    socket.once("connect", () => {
      socket.write([
        "GET /nzbdav/ws HTTP/1.1",
        `Host: ${url.host}`,
        "Connection: Upgrade",
        "Upgrade: websocket",
        "Sec-WebSocket-Version: 13",
        `Sec-WebSocket-Key: ${Buffer.alloc(16, 3).toString("base64")}`,
        `Origin: http://${url.host}`,
        "",
        "",
      ].join("\r\n"));
    });
    socket.on("data", (chunk) => {
      if (response.byteLength + chunk.byteLength > 64 * 1024) {
        finish(new Error("Production WebSocket response exceeded its bound"));
        return;
      }
      response = Buffer.concat([response, chunk]);
      const headerEnd = response.indexOf("\r\n\r\n");
      if (headerEnd < 0) return;
      const [statusLine, ...headerLines] = response
        .subarray(0, headerEnd)
        .toString("utf8")
        .split("\r\n");
      const match = /^HTTP\/1\.1 (\d{3})\b/u.exec(statusLine);
      if (!match) {
        finish(new Error("Production WebSocket response had an invalid status"));
        return;
      }
      const headers: IncomingHttpHeaders = {};
      for (const line of headerLines) {
        const separator = line.indexOf(":");
        if (separator < 0) continue;
        headers[line.slice(0, separator).toLowerCase()] = line.slice(separator + 1).trim();
      }
      finish(undefined, { status: Number(match[1]), headers });
    });
  });
}

function parseRawResponse(raw: Buffer, eof: boolean): HttpResponse | undefined {
  const headerEnd = raw.indexOf("\r\n\r\n");
  if (headerEnd < 0) {
    if (eof) throw new Error("Disposable raw response had no complete headers");
    return undefined;
  }
  const headerLines = raw.subarray(0, headerEnd).toString("utf8").split("\r\n");
  const statusMatch = /^HTTP\/1\.1 (\d{3})\b/.exec(headerLines.shift() ?? "");
  if (!statusMatch) throw new Error("Disposable raw response had no valid status line");
  const headers: IncomingHttpHeaders = {};
  for (const line of headerLines) {
    const separator = line.indexOf(":");
    if (separator < 0) throw new Error("Disposable raw response had an invalid header");
    const name = line.slice(0, separator).toLowerCase();
    const value = line.slice(separator + 1).trim();
    const existing = headers[name];
    headers[name] = existing === undefined
      ? value
      : Array.isArray(existing)
      ? [...existing, value]
      : [existing, value];
  }
  const status = Number(statusMatch[1]);
  const encodedBody = raw.subarray(headerEnd + 4);
  const declaredLength = headers["content-length"];
  const transferEncoding = headers["transfer-encoding"];
  if (declaredLength !== undefined && transferEncoding !== undefined) {
    throw new Error("Disposable raw response had ambiguous framing");
  }
  if (declaredLength !== undefined) {
    if (Array.isArray(declaredLength) || !/^\d+$/u.test(declaredLength)) {
      throw new Error("Disposable raw response had an invalid length");
    }
    const length = Number(declaredLength);
    if (!Number.isSafeInteger(length) || length > 64 * 1024) {
      throw new Error("Disposable raw response exceeded its length bound");
    }
    if (encodedBody.byteLength < length) {
      if (eof) throw new Error("Disposable raw response was truncated");
      return undefined;
    }
    if (encodedBody.byteLength > length) {
      throw new Error("Disposable raw response exceeded its declared length");
    }
    return { status, headers, body: encodedBody };
  }
  if (transferEncoding !== undefined) {
    if (
      Array.isArray(transferEncoding)
      || transferEncoding.split(",").at(-1)?.trim().toLowerCase() !== "chunked"
    ) {
      throw new Error("Disposable raw response had an invalid transfer encoding");
    }
    const decoded = decodeCompleteRawChunkedBody(encodedBody);
    if (!decoded) {
      if (eof) throw new Error("Disposable raw response was truncated");
      return undefined;
    }
    if (decoded.consumed !== encodedBody.byteLength) {
      throw new Error("Disposable raw response had trailing framed bytes");
    }
    return { status, headers, body: decoded.body };
  }
  if (!eof) return undefined;
  return { status, headers, body: encodedBody };
}

function decodeCompleteRawChunkedBody(
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
    if (!match) throw new Error("Disposable raw response had an invalid chunk");
    const size = Number.parseInt(match[1], 16);
    if (!Number.isSafeInteger(size) || size > 64 * 1024 - total) {
      throw new Error("Disposable raw response exceeded its chunk bound");
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
      throw new Error("Disposable raw response had an invalid chunk terminator");
    }
    chunks.push(encoded.subarray(offset, offset + size));
    total += size;
    offset += size + 2;
  }
}

function listen(server: Server): Promise<string> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      resolve(originOf(server));
    });
  });
}

function originOf(server: Server): string {
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Disposable server did not bind a TCP port");
  }
  return `http://127.0.0.1:${address.port}`;
}

function closeServer(server: Server): Promise<void> {
  return closeHttpServerBounded(server, 5_000);
}
