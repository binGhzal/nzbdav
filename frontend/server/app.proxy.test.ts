/** @vitest-environment node */

import http, { type IncomingHttpHeaders, type Server } from "node:http";
import { createRequire } from "node:module";
import net from "node:net";
import { inspect } from "node:util";
import express from "express";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  requestLoopbackBounded,
} from "./test-support/bounded-http";

vi.setConfig({ testTimeout: 30_000 });

const authentication = vi.hoisted(() => ({
  isAuthenticated: vi.fn(),
}));
const reactApplication = vi.hoisted(() => ({
  handler: vi.fn(),
}));

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: authentication.isAuthenticated,
}));

vi.mock("~/auth/auth-middleware.server", () => ({
  authMiddleware: async (
    req: express.Request,
    res: express.Response,
    next: express.NextFunction,
  ) => {
    if (await authentication.isAuthenticated(req)) {
      next();
      return;
    }
    res.sendStatus(401);
  },
}));

vi.mock("./react-router-handler", () => ({
  createPinrailRequestHandler: () => (
    req: express.Request,
    res: express.Response,
    next: express.NextFunction,
  ) => reactApplication.handler(req, res, next),
}));

type Capture = {
  method: string;
  url: string;
  headers: IncomingHttpHeaders;
  body: string;
  bodyBytes: Buffer;
  complete: boolean;
  aborted: boolean;
  errored: boolean;
  upstreamSocketClosed: boolean;
};

type FrontendFixture = {
  base: string;
  origin: string;
  server: Server;
};

type RawResponse = {
  status: number;
  headers: IncomingHttpHeaders;
  body: string;
};

type RequestOptions = {
  method?: string;
  headers?: http.OutgoingHttpHeaders;
  body?: string | Buffer;
};

const mountBases = ["", "/nzbdav", "/edge/apps/nzbdav"] as const;
const MAX_BACKEND_ATTEMPTS_PER_TEST = 16;
const MAX_BACKEND_REQUEST_BYTES = 4 * 1024 * 1024;
const MAX_FAILED_BACKEND_CONNECTIONS_PER_TEST = 4;
const PROXY_TIMEOUT_FIXTURE_MS = 250;
const captures: Capture[] = [];
const frontends: FrontendFixture[] = [];
const ownedServers: Server[] = [];
const originalBackendUrl = process.env.BACKEND_URL;
const originalInternalApiKey = process.env.FRONTEND_BACKEND_API_KEY;
const originalBackendProxyTimeout = process.env.BACKEND_PROXY_TIMEOUT_MS;
const originalDebug = process.env.DEBUG;
const debugControl = createRequire(import.meta.url)("debug") as {
  enable: (namespaces: string) => void;
};
let parseBackendProxyTimeout: (configured: string | undefined) => number;
let backendServer: Server;
let backendOrigin: string;
let failedProxyFrontend: FrontendFixture;
let failedBackendOrigin: string;
let backendAttemptCount = 0;
let backendFixtureFailed = false;
let failedBackendConnections = 0;
let failedBackendOverflow = false;

beforeAll(async () => {
  backendServer = trackServer(http.createServer((req, res) => {
    backendAttemptCount += 1;
    if (backendAttemptCount > MAX_BACKEND_ATTEMPTS_PER_TEST) {
      backendFixtureFailed = true;
      req.destroy();
      res.destroy();
      return;
    }

    const capture: Capture = {
      method: req.method ?? "",
      url: req.url ?? "",
      headers: req.headers,
      body: "",
      bodyBytes: Buffer.alloc(0),
      complete: false,
      aborted: false,
      errored: false,
      upstreamSocketClosed: false,
    };
    captures.push(capture);
    const body: Buffer[] = [];
    let bodyBytes = 0;
    let requestOverflow = false;
    req.on("data", (chunk: Buffer) => {
      if (requestOverflow) return;
      bodyBytes += chunk.byteLength;
      if (bodyBytes > MAX_BACKEND_REQUEST_BYTES) {
        requestOverflow = true;
        backendFixtureFailed = true;
        req.destroy();
        res.destroy();
        return;
      }
      body.push(chunk);
    });
    req.on("aborted", () => {
      capture.aborted = true;
    });
    req.on("error", () => {
      capture.errored = true;
    });
    req.on("end", () => {
      if (requestOverflow) return;
      capture.bodyBytes = Buffer.concat(body);
      capture.body = capture.bodyBytes.toString("utf8");
      capture.complete = true;
      if ((req.url ?? "") === "/api?mode=version&apikey=pre-header-stall") {
        req.socket.once("close", () => {
          capture.upstreamSocketClosed = true;
        });
        return;
      }
      if ((req.url ?? "") === "/content/post-header-idle-canary.mkv") {
        req.socket.once("close", () => {
          capture.upstreamSocketClosed = true;
        });
        res.statusCode = 200;
        res.setHeader("Content-Length", "8");
        res.setHeader("Content-Type", "application/octet-stream");
        res.flushHeaders();
        res.write("part");
        return;
      }
      if ((req.url ?? "") === "/content/periodic-stream-canary.mkv") {
        const chunks = ["one-", "two-", "three-", "four-", "done"];
        const payload = chunks.join("");
        let index = 0;
        res.statusCode = 200;
        res.setHeader("Content-Length", String(Buffer.byteLength(payload)));
        res.setHeader("Content-Type", "application/octet-stream");
        res.flushHeaders();
        res.write(chunks[index++]);
        const interval = setInterval(() => {
          if (index === chunks.length - 1) {
            clearInterval(interval);
            res.end(chunks[index]);
            return;
          }
          res.write(chunks[index++]);
        }, 75);
        res.once("close", () => clearInterval(interval));
        return;
      }
      if (req.headers["x-response-fixture"] === "private-hop-headers") {
        const payload = '{"run":"fixture"}';
        res.statusCode = 202;
        res.setHeader(
          "Location",
          "/api/maintenance/runs/00000000-0000-0000-0000-000000000001",
        );
        res.setHeader("Set-Cookie", [
          "private-session=fixture; HttpOnly; Path=/",
          "private-preference=fixture; Path=/",
        ]);
        res.setHeader("Content-Type", "application/json");
        res.setHeader("Content-Length", String(Buffer.byteLength(payload)));
        res.setHeader("X-Benign-Response", "preserved");
        res.end(payload);
        return;
      }
      if ((req.url ?? "") === "/content/partial-response-canary.mkv") {
        res.statusCode = 200;
        res.setHeader("Content-Type", "application/octet-stream");
        res.flushHeaders();
        res.write("part");
        setImmediate(() => res.destroy());
        return;
      }
      if ((req.url ?? "").startsWith("/view/")) {
        const payload = Buffer.from("0123456789abcdef", "utf8");
        res.statusCode = req.headers.range ? 206 : 200;
        res.setHeader("Accept-Ranges", "bytes");
        res.setHeader("Content-Length", req.headers.range ? 4 : payload.length);
        res.setHeader("Content-Type", "video/mp4");
        res.setHeader("ETag", '"media-fixture"');
        if (req.headers.range) res.setHeader("Content-Range", "bytes 4-7/16");
        if (req.method === "HEAD") res.end();
        else res.end(req.headers.range ? payload.subarray(4, 8) : payload);
        return;
      }
      res.statusCode = 204;
      res.end();
    });
  }));
  backendOrigin = await listen(backendServer);
  process.env.BACKEND_URL = backendOrigin;
  process.env.FRONTEND_BACKEND_API_KEY = "unit";
  process.env.BACKEND_PROXY_TIMEOUT_MS = String(PROXY_TIMEOUT_FIXTURE_MS);
  process.env.DEBUG = "http-proxy-middleware*";
  debugControl.enable(process.env.DEBUG);

  for (const base of mountBases) {
    vi.resetModules();
    const { app, readBackendProxyTimeout } = await import("./app");
    parseBackendProxyTimeout = readBackendProxyTimeout;
    const parent = express();
    parent.disable("x-powered-by");
    parent.use(base || "/", app);
    const server = trackServer(http.createServer(parent));
    frontends.push({ base, origin: await listen(server), server });
  }

  const resetServer = trackServer(http.createServer());
  resetServer.on("connection", (socket) => {
    failedBackendConnections += 1;
    if (failedBackendConnections > MAX_FAILED_BACKEND_CONNECTIONS_PER_TEST) {
      failedBackendOverflow = true;
    }
    socket.destroy();
  });
  failedBackendOrigin = await listen(resetServer);
  process.env.BACKEND_URL = failedBackendOrigin;
  vi.resetModules();
  const { app } = await import("./app");
  const parent = express();
  parent.disable("x-powered-by");
  parent.use("/nzbdav", app);
  const server = trackServer(http.createServer(parent));
  failedProxyFrontend = {
    base: "/nzbdav",
    origin: await listen(server),
    server,
  };
  process.env.BACKEND_URL = backendOrigin;
});

afterAll(async () => {
  const failures: Error[] = [];
  try {
    const results = await Promise.allSettled(
      ownedServers.splice(0).map((server) => close(server)),
    );
    for (const result of results) {
      if (result.status === "rejected") {
        failures.push(new Error("Disposable proxy server cleanup failed."));
      }
    }
  } finally {
    restoreEnvironment("BACKEND_URL", originalBackendUrl);
    restoreEnvironment("FRONTEND_BACKEND_API_KEY", originalInternalApiKey);
    restoreEnvironment("BACKEND_PROXY_TIMEOUT_MS", originalBackendProxyTimeout);
    restoreEnvironment("DEBUG", originalDebug);
    debugControl.enable(originalDebug ?? "");
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Disposable proxy cleanup failed.");
  }
});

beforeEach(() => {
  captures.length = 0;
  backendAttemptCount = 0;
  backendFixtureFailed = false;
  failedBackendConnections = 0;
  failedBackendOverflow = false;
  authentication.isAuthenticated.mockReset();
  authentication.isAuthenticated.mockResolvedValue(true);
  reactApplication.handler.mockReset();
  reactApplication.handler.mockImplementation((
    _req: express.Request,
    res: express.Response,
  ) => res.status(418).type("text/plain").send("frontend"));
});

afterEach(async () => {
  await waitForEventLoopTurn();
  if (
    backendFixtureFailed
    || failedBackendOverflow
    || backendAttemptCount !== captures.length
    || captures.some((capture) => !capture.complete || capture.aborted || capture.errored)
  ) {
    throw new Error("Disposable proxy oracle failed.");
  }
});

describe("production backend proxy boundary", () => {
  describe("bounded proxy timeout configuration", () => {
    it.each([
      [undefined, 30_000],
      ["", 30_000],
      ["100", 100],
      ["300000", 300_000],
    ] as const)("accepts BACKEND_PROXY_TIMEOUT_MS=%s", (configured, expected) => {
      expect(parseBackendProxyTimeout(configured)).toBe(expected);
    });

    it.each([
      "0",
      "99",
      "300001",
      "-100",
      "+100",
      "100.0",
      "1e3",
      " 100",
      "100 ",
      "9007199254740993",
    ])("rejects BACKEND_PROXY_TIMEOUT_MS=%j", (configured) => {
      expect(() => parseBackendProxyTimeout(configured)).toThrow(/bounded integer/u);
    });
  });

  describe("independently authenticated protocol APIs", () => {
    it.each(mountBases)("routes exact SAB ingress under URL_BASE=%s", async (base) => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const response = await requestRaw(base, "/protocol/api?mode=version&apikey=pub");

      expect(response.status).toBe(204);
      expect(authentication.isAuthenticated).not.toHaveBeenCalled();
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({
        method: "GET",
        url: "/api?mode=version&apikey=pub",
      });
      expect(captures[0].headers["x-api-key"]).toBeUndefined();
    });

    const arrRoutes = [
      ["GET", "/protocol/api/arr/validation", "/api/arr/validation"],
      ["GET", "/protocol/api/arr/search-nudges?limit=25", "/api/arr/search-nudges?limit=25"],
      ["GET", "/protocol/api/arr/correlations", "/api/arr/correlations"],
      ["POST", "/protocol/api/arr/events/sonarr", "/api/arr/events/sonarr"],
      ["POST", "/protocol/api/arr/events/radarr", "/api/arr/events/radarr"],
      ["POST", "/protocol/api/arr/events/lidarr", "/api/arr/events/lidarr"],
    ] as const;

    for (const base of mountBases) {
      it.each(arrRoutes)(
        `routes exact %s %s under URL_BASE=${base || "<empty>"}`,
        async (method, path, backendTarget) => {
          authentication.isAuthenticated.mockResolvedValue(false);
          const response = await requestRaw(base, path, {
            method,
            headers: method === "POST"
              ? { "content-type": "application/json", "content-length": "2" }
              : undefined,
            body: method === "POST" ? "{}" : undefined,
          });

          expect(response.status).toBe(204);
          expect(authentication.isAuthenticated).not.toHaveBeenCalled();
          expect(captures).toHaveLength(1);
          expect(captures[0]).toMatchObject({ method, url: backendTarget });
        },
      );
    }

    it.each([
      ["POST", "/protocol/api/arr/validation", "GET"],
      ["GET", "/protocol/api/arr/events/sonarr", "POST"],
      ["GET", "/protocol/api/", undefined],
      ["GET", "/protocol/api/arr/validation/", undefined],
      ["POST", "/protocol/api/arr/events/sonarr/", undefined],
      ["GET", "/protocol/api/get-config", undefined],
      ["GET", "/protocol/api/arr/search-nudges/id/retry", undefined],
      ["GET", "/protocol/api/db.sqlite", undefined],
    ] as const)("rejects protocol negative %s %s", async (method, path, allow) => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const response = await requestRaw("/nzbdav", path, { method });

      if (allow) expectStableReject(response, 405, "method_not_allowed", allow);
      else expectStableReject(response, 404, "route_not_found");
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["header", "/protocol/api?mode=version", { "X-Api-Key": "pub" }, undefined],
      ["query", "/protocol/api?mode=version&apikey=pub", undefined, undefined],
      [
        "form",
        "/protocol/api?mode=addfile",
        { "content-type": "application/x-www-form-urlencoded", "content-length": "20" },
        "apikey=pub&name=file",
      ],
      [
        "equal header plus query",
        "/protocol/api?mode=version&apikey=pub",
        { "x-api-key": "pub" },
        undefined,
      ],
    ] as const)("preserves the %s carrier without internal injection", async (_name, path, headers, body) => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const response = await requestRaw("/nzbdav", path, {
        method: body ? "POST" : "GET",
        headers,
        body,
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].url).toBe(path.slice("/protocol".length));
      const suppliedHeaders = headers as http.OutgoingHttpHeaders | undefined;
      expect(captures[0].headers["x-api-key"]).toBe(
        suppliedHeaders?.["x-api-key"] ?? suppliedHeaders?.["X-Api-Key"],
      );
      if (body) {
        expect(captures[0].body).toBe(body);
        expect(captures[0].headers["content-type"]).toBe(suppliedHeaders?.["content-type"]);
        expect(captures[0].headers["content-length"]).toBe(suppliedHeaders?.["content-length"]);
      }
      expect(JSON.stringify(captures[0])).not.toContain("unit");
    });

    it("accepts the exact 512-character public header boundary", async () => {
      const value = "x".repeat(512);
      const response = await requestRaw("/nzbdav", "/protocol/api?mode=version", {
        headers: { "x-api-key": value },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers["x-api-key"]).toBe(value);
    });

    it("streams a multipart SAB add-file request without inspecting its body", async () => {
      const boundary = "pinrail-fixture";
      const body = [
        `--${boundary}`,
        'Content-Disposition: form-data; name="name"; filename="release.nzb"',
        "Content-Type: application/x-nzb",
        "",
        "fixture-nzb-body",
        `--${boundary}--`,
        "",
      ].join("\r\n");
      const path = "/protocol/api?mode=addfile&apikey=pub&cat=movies";
      const response = await requestRaw("/nzbdav", path, {
        method: "POST",
        headers: {
          "content-type": `multipart/form-data; boundary=${boundary}`,
          "content-length": String(Buffer.byteLength(body)),
        },
        body,
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].url).toBe(path.slice("/protocol".length));
      expect(captures[0].body).toBe(body);
      expect(captures[0].headers["content-type"]).toBe(
        `multipart/form-data; boundary=${boundary}`,
      );
      expect(captures[0].headers["content-length"]).toBe(String(Buffer.byteLength(body)));
      expect(captures[0].headers["x-api-key"]).toBeUndefined();
    });

    it.each([
      ["empty", { "x-api-key": "" }],
      ["repeated", { "x-api-key": ["pub", "pub"] as string[] }],
      ["oversized", { "x-api-key": "x".repeat(513) }],
    ] as const)("rejects a structurally invalid %s public header before upstream", async (_name, headers) => {
      const response = await requestRaw("/nzbdav", "/protocol/api?mode=version", { headers });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each([
      [
        "public API-key carrier",
        "/protocol/api?mode=version&apikey=one",
        {
          headers: {
            Connection: "x-api-key",
            "x-api-key": "two",
          },
        },
      ],
      [
        "form carrier content type",
        "/protocol/api?mode=addfile&apikey=one",
        {
          method: "POST",
          headers: {
            Connection: "content-type",
            "content-type": "application/x-www-form-urlencoded",
          },
          body: "apikey=two&name=file",
        },
      ],
      [
        "WebDAV authorization",
        "/protocol/content/file.mkv",
        {
          method: "PROPFIND",
          headers: {
            Connection: "authorization",
            Authorization: "Basic connection-option-canary",
          },
        },
      ],
      [
        "WebDAV conditional",
        "/protocol/content/file.mkv",
        {
          method: "DELETE",
          headers: {
            Connection: "if-match",
            "If-Match": '"connection-option-etag"',
          },
        },
      ],
      [
        "media range",
        "/protocol/content/file.mkv",
        {
          headers: {
            Connection: "range",
            Range: "bytes=1-2",
          },
        },
      ],
    ] as const)("rejects a Connection option that nominates %s before upstream", async (
      _name,
      path,
      options,
    ) => {
      const response = await requestRaw("/nzbdav", path, options);

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["repeated query", "/protocol/api?mode=version&apikey=one&apikey=two", undefined, undefined],
      ["noncanonical query", "/protocol/api?mode=version&apiKey=pub", undefined, undefined],
      ["unequal header/query", "/protocol/api?mode=version&apikey=one", { "x-api-key": "two" }, undefined],
      [
        "header/form conflict",
        "/protocol/api?mode=addfile",
        { "x-api-key": "one", "content-type": "application/x-www-form-urlencoded", "content-length": "20" },
        "apikey=two&name=file",
      ],
    ] as const)("preserves %s for the sealed backend parser to reject", async (_name, path, headers, body) => {
      const response = await requestRaw("/nzbdav", path, {
        method: body ? "POST" : "GET",
        headers,
        body,
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].url).toBe(path.slice("/protocol".length));
      const suppliedHeaders = headers as http.OutgoingHttpHeaders | undefined;
      expect(captures[0].headers["x-api-key"]).toBe(suppliedHeaders?.["x-api-key"]);
      expect(captures[0].bodyBytes).toEqual(Buffer.from(body ?? ""));
      if (body) {
        expect(captures[0].headers["content-type"]).toBe(suppliedHeaders?.["content-type"]);
        expect(captures[0].headers["content-length"]).toBe(suppliedHeaders?.["content-length"]);
      }
      expect(JSON.stringify(captures[0])).not.toContain("unit");
    });

    it.each(["sab", "arr"] as const)("strips browser authority on the %s lane", async (lane) => {
      const path = lane === "sab"
        ? "/protocol/api?mode=version"
        : "/protocol/api/arr/validation";
      const response = await requestRaw("/nzbdav", path, {
        headers: securityHeaders({ "x-api-key": "pub" }),
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers["x-api-key"]).toBe("pub");
      expectSensitiveHeadersStripped(captures[0].headers, ["x-api-key"]);
    });
  });

  describe("principal-protected UI relays", () => {
    it.each(mountBases)("seals an inner pre-header Express failure under URL_BASE=%s", async (base) => {
      const hostile = "inner-terminal-canary|/private/runtime/path|credential-marker";
      authentication.isAuthenticated.mockRejectedValueOnce(new Error(hostile));
      const errorLog = vi.spyOn(console, "error").mockImplementation(() => undefined);
      errorLog.mockClear();

      const response = await requestRaw(base, "/health", {
        headers: { Accept: "text/html" },
      });

      expectStableReject(response, 500, "internal_error");
      expect(errorLog.mock.calls).toEqual([["frontend_http_failure code=internal_error"]]);
      expect(JSON.stringify(errorLog.mock.calls)).not.toContain(hostile);
    });

    it.each(mountBases)("destroys an inner post-header Express failure under URL_BASE=%s", async (base) => {
      const hostile = "inner-post-header-canary|credential-marker";
      const errorLog = vi.spyOn(console, "error").mockImplementation(() => undefined);
      errorLog.mockClear();
      reactApplication.handler.mockImplementation((
        _req: express.Request,
        res: express.Response,
        next: express.NextFunction,
      ) => {
        res.status(200).type("text/plain").write("partial");
        next(new Error(hostile));
      });

      await expect(requestRaw(base, "/__terminal-after-headers"))
        .rejects.toThrow("Disposable HTTP request failed.");
      expect(errorLog.mock.calls).toEqual([["frontend_http_failure code=internal_error"]]);
      expect(JSON.stringify(errorLog.mock.calls)).not.toContain(hostile);
    });

    it.each(mountBases)("keeps the browser health route principal-protected under URL_BASE=%s", async (base) => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const denied = await requestRaw(base, "/health", {
        headers: { Accept: "text/html" },
      });
      expect(denied.status).toBe(401);
      expect(captures).toHaveLength(0);

      authentication.isAuthenticated.mockResolvedValue(true);
      const allowed = await requestRaw(base, "/health", {
        headers: { Accept: "text/html" },
      });
      expect(allowed.status).toBe(418);
      expect(captures).toHaveLength(0);
    });

    it.each(mountBases)("requires a principal before UI admin proxying under URL_BASE=%s", async (base) => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const response = await requestRaw(base, "/api/test-arr-connection", {
        method: "POST",
        headers: { "x-api-key": "pub" },
      });

      expectStableReject(response, 401, "authentication_required");
      expect(captures).toHaveLength(0);
    });

    it.each(mountBases)("uses a stable failure when principal evaluation throws under URL_BASE=%s", async (base) => {
      const hostile = [
        "credential-marker",
        "/private/runtime/path",
        "https://user:password@example.invalid/provider",
        "provider-response",
      ].join("|");
      authentication.isAuthenticated.mockRejectedValueOnce(new Error(hostile));
      const errorLog = vi.spyOn(console, "error").mockImplementation(() => undefined);

      const response = await requestRaw(base, "/api/test-arr-connection", {
        method: "POST",
        headers: { "x-api-key": "pub" },
      });

      expectStableReject(response, 500, "internal_error");
      expect(captures).toHaveLength(0);
      const rendered = errorLog.mock.calls
        .flatMap(call => call.map(value => value instanceof Error ? value.message : String(value)))
        .join("\n");
      expect(rendered.includes(hostile)).toBe(false);
    });

    it.each(mountBases)("replaces client authority on UI admin proxying under URL_BASE=%s", async (base) => {
      const response = await requestRaw(base, "/api/test-arr-connection", {
        method: "POST",
        headers: securityHeaders({ "x-api-key": "pub" }),
      });

      expect(response.status).toBe(204);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expect(captures).toHaveLength(1);
      expect(captures[0].headers["x-api-key"]).toBe("unit");
      expectSensitiveHeadersStripped(captures[0].headers, ["x-api-key"]);
    });

    it("streams an authenticated UI add-file body byte-for-byte and denies it without a principal", async () => {
      const boundary = "pinrail-ui-add-file";
      const body = Buffer.concat([
        Buffer.from([
          `--${boundary}`,
          'Content-Disposition: form-data; name="name"; filename="release.nzb"',
          "Content-Type: application/x-nzb",
          "",
          "",
        ].join("\r\n"), "utf8"),
        Buffer.from([0x4e, 0x5a, 0x42, 0x00, 0xff]),
        Buffer.from(`\r\n--${boundary}--\r\n`, "utf8"),
      ]);
      const path = "/api?mode=addfile&cat=movies&priority=0&pp=0";
      const headers = securityHeaders({
        "content-type": `multipart/form-data; boundary=${boundary}`,
        "content-length": String(body.length),
      });

      const allowed = await requestRaw("/nzbdav", path, {
        method: "POST",
        headers,
        body,
      });

      expect(allowed.status).toBe(204);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({ method: "POST", url: path });
      expect(captures[0].bodyBytes).toEqual(body);
      expect(captures[0].headers["content-type"]).toBe(
        `multipart/form-data; boundary=${boundary}`,
      );
      expect(captures[0].headers["content-length"]).toBe(String(body.length));
      expect(captures[0].headers["x-api-key"]).toBe("unit");
      expectSensitiveHeadersStripped(captures[0].headers, ["x-api-key"]);

      resetBackendCaptureOracle();
      authentication.isAuthenticated.mockReset();
      authentication.isAuthenticated.mockResolvedValue(false);
      const denied = await requestRaw("/nzbdav", path, {
        method: "POST",
        headers,
        body,
      });

      expect(denied.status).toBe(401);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["queue", "/api?mode=queue&name=delete", '{"ids":["queue-fixture"]}'],
      [
        "history",
        "/api?mode=history&name=delete&del_completed_files=1",
        '{"ids":["history-fixture"]}',
      ],
    ] as const)(
      "streams an authenticated bulk %s JSON body and makes zero upstream calls without a principal",
      async (_name, path, body) => {
        const headers = securityHeaders({
          "content-type": "application/json",
          "content-length": String(Buffer.byteLength(body)),
        });
        const allowed = await requestRaw("/nzbdav", path, {
          method: "POST",
          headers,
          body,
        });

        expect(allowed.status).toBe(204);
        expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
        expect(captures).toHaveLength(1);
        expect(captures[0]).toMatchObject({ method: "POST", url: path, body });
        expect(captures[0].bodyBytes).toEqual(Buffer.from(body));
        expect(captures[0].headers["content-type"]).toBe("application/json");
        expect(captures[0].headers["content-length"]).toBe(
          String(Buffer.byteLength(body)),
        );
        expect(captures[0].headers["x-api-key"]).toBe("unit");
        expectSensitiveHeadersStripped(captures[0].headers, ["x-api-key"]);

        resetBackendCaptureOracle();
        authentication.isAuthenticated.mockReset();
        authentication.isAuthenticated.mockResolvedValue(false);
        const denied = await requestRaw("/nzbdav", path, {
          method: "POST",
          headers,
          body,
        });

        expect(denied.status).toBe(401);
        expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
        expect(captures).toHaveLength(0);
      },
    );

    it.each([
      ["ui-admin", "POST", "/api/remove-unlinked-files", "/api/remove-unlinked-files", {}],
      [
        "ui-view",
        "GET",
        "/view/content/file.mkv?downloadKey=cap",
        "/view/content/file.mkv?downloadKey=cap",
        {},
      ],
      [
        "protocol-sab",
        "GET",
        "/protocol/api?mode=version&apikey=pub",
        "/api?mode=version&apikey=pub",
        {},
      ],
      [
        "protocol-arr",
        "GET",
        "/protocol/api/arr/validation",
        "/api/arr/validation",
        { "x-api-key": "pub" },
      ],
      [
        "protocol-webdav",
        "GET",
        "/protocol/content/file.mkv",
        "/content/file.mkv",
        { Authorization: "Basic fixture" },
      ],
    ] as const)(
      "strips successful private-hop Location and Set-Cookie on the %s lane under a nested URL_BASE",
      async (_lane, method, path, backendTarget, laneHeaders) => {
        const response = await requestRaw("/edge/apps/nzbdav", path, {
          method,
          headers: {
            ...laneHeaders,
            "x-response-fixture": "private-hop-headers",
          },
        });

        expect(response).toMatchObject({ status: 202, body: '{"run":"fixture"}' });
        expect(response.headers.location).toBeUndefined();
        expect(response.headers["set-cookie"]).toBeUndefined();
        expect(response.headers["x-benign-response"]).toBe("preserved");
        expect(captures).toHaveLength(1);
        expect(captures[0]).toMatchObject({
          method,
          url: backendTarget,
        });
      },
    );

    it.each([
      ["GET", "/api?mode=addfile&cat=movies&priority=0&pp=0", "POST"],
      ["POST", "/api/download-nzb?nzbBlobId=id", "GET"],
      ["POST", "/api/get-health-check-queue", "GET"],
      ["GET", "/api/test-rclone-connection", "POST"],
      ["GET", "/api/test-usenet-connection", "POST"],
      ["GET", "/api/test-usenet-pipelining", "POST"],
      ["GET", "/api/test-arr-connection", "POST"],
      ["POST", "/api/maintenance/status", "GET"],
      ["GET", "/api/remove-unlinked-files", "POST"],
      ["GET", "/api/remove-unlinked-files/dry-run", "POST"],
      ["POST", "/api/remove-unlinked-files/audit", "GET"],
      ["POST", "/view/content/movie.mkv?downloadKey=cap", "GET, HEAD"],
    ] as const)(
      "returns the exact Allow value for UI wrong-method %s %s",
      async (method, path, allow) => {
        const response = await requestRaw("/nzbdav", path, { method });

        expectStableReject(response, 405, "method_not_allowed", allow);
        expect(captures).toHaveLength(0);
      },
    );

    it.each([
      ["GET", "/api", 404],
      ["POST", "/api", 404],
      ["GET", "/api?mode=version", 404],
      ["POST", "/api?mode=unknown", 404],
      ["POST", "/api?mode=addfile&cat=movies&priority=0&pp=0&unexpected=true", 404],
      ["POST", "/api?mode=addfile&cat=movies&priority=0&pp=0&mode=addfile", 400],
      ["GET", "/api/download-nzb", 404],
      ["GET", "/api/download-nzb?nzbBlobId=", 404],
      ["GET", "/api/download-nzb?nzbBlobId=id&unexpected=true", 404],
      ["GET", "/api/download-nzb?nzbBlobId=one&nzbBlobId=two", 400],
      ["GET", "/api/get-health-check-queue?unexpected=true", 404],
      ["GET", "/api/get-health-check-queue?pageSize=1&pageSize=2", 400],
      ["POST", "/api/test-rclone-connection?unexpected=true", 404],
      ["POST", "/api/test-usenet-connection?unexpected=true", 404],
      ["POST", "/api/test-usenet-pipelining?unexpected=true", 404],
      ["POST", "/api/test-arr-connection?unexpected=true", 404],
      ["GET", "/api/maintenance/status?unexpected=true", 404],
      ["GET", "/api/maintenance/status?kind=one&kind=two", 400],
      ["POST", "/api/remove-unlinked-files?unexpected=true", 404],
      ["POST", "/api/remove-unlinked-files/dry-run?unexpected=true", 404],
      ["GET", "/api/remove-unlinked-files/audit?unexpected=true", 404],
      ["GET", "/view?downloadKey=cap", 404],
      ["GET", "/view/?downloadKey=cap", 404],
      ["GET", "/view/content/movie.mkv", 404],
      ["GET", "/view/content/movie.mkv?downloadKey=", 404],
      ["GET", "/view/content/movie.mkv?downloadKey=cap&unexpected=true", 404],
      ["GET", "/view/content/movie.mkv?downloadKey=one&downloadKey=two", 400],
      ["GET", "/view/content/movie.mkv?downloadKey=cap&extension=one&extension=two", 400],
      ["GET", "/view/content/movie.mkv?downloadKey=cap&download=true&download=false", 400],
    ] as const)(
      "rejects the complete UI query negative %s %s before upstream",
      async (method, path, status) => {
        const response = await requestRaw("/nzbdav", path, { method });

        expectStableReject(
          response,
          status,
          status === 400 ? "invalid_request_target" : "route_not_found",
        );
        expect(captures).toHaveLength(0);
      },
    );

    it.each([
      ["GET", "/api?mode=pause"],
      ["GET", "/api?mode=resume"],
      ["GET", "/api?mode=queue&name=delete&value=id"],
      ["GET", "/api?mode=queue&name=priority&value=id&value2=1"],
      ["GET", "/api?mode=history&name=delete&value=id"],
    ] as const)("rejects obsolete browser mutation %s %s", async (method, path) => {
      const response = await requestRaw("/nzbdav", path, { method });

      expectStableReject(response, 405, "method_not_allowed", "POST");
      expect(captures).toHaveLength(0);
    });

    it.each([
      "/api/get-config",
      "/api/db.sqlite",
      "/api/repair/run",
      "/api/test-arr-connection/",
      "/api/test-arr-connection?apikey=pub",
      "/protocol/view/content/file.mkv?downloadKey=cap",
    ])("rejects unapproved admin target %s before upstream", async (path) => {
      const response = await requestRaw("/nzbdav", path, {
        method: path.includes("test-arr") ? "POST" : "GET",
      });

      expectStableReject(response, 404, "route_not_found");
      expect(captures).toHaveLength(0);
    });

    it.each(mountBases)("preserves signed GET and HEAD streaming under URL_BASE=%s", async (base) => {
      const path = "/view/content/movie.mkv?downloadKey=cap&extension=mkv&download=true";
      for (const method of ["GET", "HEAD"] as const) {
        resetBackendCaptureOracle();
        authentication.isAuthenticated.mockClear();
        const response = await requestRaw(base, path, {
          method,
          headers: securityHeaders({
            Range: "bytes=4-7",
            "If-Range": '"media-fixture"',
            "x-api-key": "pub",
          }),
        });

        expect(response.status).toBe(206);
        expect(response.headers["accept-ranges"]).toBe("bytes");
        expect(response.headers["content-range"]).toBe("bytes 4-7/16");
        expect(response.headers["content-length"]).toBe("4");
        expect(response.headers.etag).toBe('"media-fixture"');
        expect(response.body).toBe(method === "GET" ? "4567" : "");
        expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
        expect(captures).toHaveLength(1);
        expect(captures[0]).toMatchObject({ method, url: path });
        expect(captures[0].headers.range).toBe("bytes=4-7");
        expect(captures[0].headers["if-range"]).toBe('"media-fixture"');
        expectSensitiveHeadersStripped(captures[0].headers);
      }
    });

    it("rejects signed media before upstream when the principal is absent", async () => {
      authentication.isAuthenticated.mockResolvedValue(false);
      const response = await requestRaw("/nzbdav", "/view/content/movie.mkv?downloadKey=cap");

      expect(response.status).toBe(401);
      expect(captures).toHaveLength(0);
    });
  });

  describe("independently authenticated WebDAV", () => {
    const readPaths = [
      ["/protocol", "/"],
      ["/protocol/README", "/README"],
      ["/protocol/.ids/a/b/file.mkv", "/.ids/a/b/file.mkv"],
      ["/protocol/nzbs/movies/release.nzb", "/nzbs/movies/release.nzb"],
      ["/protocol/content/movies/file.mkv", "/content/movies/file.mkv"],
      [
        "/protocol/completed-symlinks/movies/file.mkv.rclonelink",
        "/completed-symlinks/movies/file.mkv.rclonelink",
      ],
    ] as const;

    for (const base of mountBases) {
      for (const method of ["GET", "HEAD", "OPTIONS", "PROPFIND"] as const) {
        it.each(readPaths)(
          `preserves ${method} %s under URL_BASE=${base || "<empty>"}`,
          async (path, backendTarget) => {
            authentication.isAuthenticated.mockResolvedValue(false);
            const response = await requestRaw(base, path, {
              method,
              headers: { Authorization: "Basic fixture", Depth: "1" },
            });

            expect(response.status).toBe(204);
            expect(authentication.isAuthenticated).not.toHaveBeenCalled();
            expect(captures).toHaveLength(1);
            expect(captures[0]).toMatchObject({ method, url: backendTarget });
            expect(captures[0].headers.authorization).toBe("Basic fixture");
            expect(captures[0].headers.depth).toBe("1");
            expect(captures[0].headers["x-pinrail-webdav-path-base"]).toBe(
              encodedWebDavPathBase(base),
            );
          },
        );
      }
    }

    it.each(mountBases)(
      "overwrites client WebDAV PathBase metadata under URL_BASE=%s",
      async (base) => {
        const response = await requestRaw(base, "/protocol/README", {
          method: "PROPFIND",
          headers: {
            Authorization: "Basic fixture",
            "x-pinrail-webdav-path-base": ["attacker-one", "attacker-two"],
          },
        });

        expect(response.status).toBe(204);
        expect(captures).toHaveLength(1);
        expect(captures[0].headers["x-pinrail-webdav-path-base"]).toBe(
          encodedWebDavPathBase(base),
        );
      },
    );

    it("removes client WebDAV PathBase metadata from every non-WebDAV lane", async () => {
      const response = await requestRaw("/nzbdav", "/protocol/api?mode=version", {
        headers: {
          "x-api-key": "pub",
          "x-pinrail-webdav-path-base": "attacker",
        },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers["x-pinrail-webdav-path-base"]).toBeUndefined();
    });

    it.each([
      ["GET", "/protocol/private/file"],
      ["PROPFIND", "/protocol/private"],
    ] as const)("rejects unknown WebDAV root %s %s before upstream", async (method, path) => {
      const response = await requestRaw("/nzbdav", path, { method });

      expectStableReject(response, 404, "route_not_found");
      expect(captures).toHaveLength(0);
    });

    it("aborts a downstream response when the private upstream body is truncated", async () => {
      await expect(requestRaw(
        "/nzbdav",
        "/protocol/content/partial-response-canary.mkv",
        { headers: { Authorization: "Basic fixture" } },
      )).rejects.toThrow("Disposable HTTP request failed.");
      await waitForEventLoopTurn();

      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({
        method: "GET",
        url: "/content/partial-response-canary.mkv",
        complete: true,
        aborted: false,
        errored: false,
      });
    });

    it("returns one bounded generic 502 and closes a pre-header stalled upstream", async () => {
      const startedAt = Date.now();
      const response = await requestRaw(
        "/nzbdav",
        "/protocol/api?mode=version&apikey=pre-header-stall",
      );
      const elapsedMs = Date.now() - startedAt;

      expectStableReject(response, 502, "upstream_unavailable");
      expect(elapsedMs).toBeLessThan(1_500);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({
        method: "GET",
        url: "/api?mode=version&apikey=pre-header-stall",
        complete: true,
      });
      expect(backendAttemptCount).toBe(1);
      await waitFor(() => captures[0].upstreamSocketClosed);
    });

    it("aborts both sides of a post-header idle upstream without fabricating completion", async () => {
      const startedAt = Date.now();
      await expect(requestRaw(
        "/nzbdav",
        "/protocol/content/post-header-idle-canary.mkv",
        { headers: { Authorization: "Basic fixture" } },
      )).rejects.toThrow("Disposable HTTP request failed.");
      const elapsedMs = Date.now() - startedAt;

      expect(elapsedMs).toBeLessThan(1_500);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({
        method: "GET",
        url: "/content/post-header-idle-canary.mkv",
        complete: true,
      });
      expect(backendAttemptCount).toBe(1);
      await waitFor(() => captures[0].upstreamSocketClosed);
    });

    it("keeps a legitimate periodic upstream stream alive across the total timeout window", async () => {
      const startedAt = Date.now();
      const response = await requestRaw(
        "/nzbdav",
        "/protocol/content/periodic-stream-canary.mkv",
        { headers: { Authorization: "Basic fixture" } },
      );
      const elapsedMs = Date.now() - startedAt;

      expect(response).toMatchObject({ status: 200, body: "one-two-three-four-done" });
      expect(elapsedMs).toBeGreaterThanOrEqual(PROXY_TIMEOUT_FIXTURE_MS);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({
        method: "GET",
        url: "/content/periodic-stream-canary.mkv",
        complete: true,
      });
      expect(backendAttemptCount).toBe(1);
    });

    it.each(mountBases)("streams exact NZB writes under URL_BASE=%s", async (base) => {
      const body = Buffer.from([0x4e, 0x5a, 0x42, 0x00, 0xff]);
      const response = await requestRaw(base, "/protocol/nzbs/movies/release.nzb", {
        method: "PUT",
        headers: {
          Authorization: "Basic fixture",
          "content-length": String(body.length),
          "content-type": "application/octet-stream",
        },
        body,
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({ method: "PUT", url: "/nzbs/movies/release.nzb" });
      expect(captures[0].bodyBytes).toEqual(body);
      expect(captures[0].headers["content-length"]).toBe(String(body.length));
    });

    it.each([
      ["DELETE", "/protocol/nzbs/movies/release.nzb", "/nzbs/movies/release.nzb"],
      ["DELETE", "/protocol/content/movies/file.mkv", "/content/movies/file.mkv"],
      [
        "DELETE",
        "/protocol/completed-symlinks/movies/file.mkv.rclonelink",
        "/completed-symlinks/movies/file.mkv.rclonelink",
      ],
    ] as const)("allows semantic WebDAV write %s %s", async (method, path, backendTarget) => {
      const response = await requestRaw("/edge/apps/nzbdav", path, {
        method,
        headers: { Authorization: "Basic fixture" },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({ method, url: backendTarget });
    });

    it.each([
      ["DELETE", "/protocol", "GET, HEAD, OPTIONS, PROPFIND"],
      ["PUT", "/protocol/README", "GET, HEAD, OPTIONS, PROPFIND"],
      ["DELETE", "/protocol/README", "GET, HEAD, OPTIONS, PROPFIND"],
      ["PUT", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["PUT", "/protocol/nzbs/movies/nested/release.nzb", "GET, HEAD, OPTIONS, PROPFIND"],
      ["DELETE", "/protocol/nzbs", "GET, HEAD, OPTIONS, PROPFIND"],
      ["DELETE", "/protocol/nzbs/movies", "GET, HEAD, OPTIONS, PROPFIND"],
      ["PUT", "/protocol/.ids/a/file.mkv", "GET, HEAD, OPTIONS, PROPFIND"],
      ["PUT", "/protocol/completed-symlinks/movies/file.mkv.rclonelink", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["DELETE", "/protocol/.ids/a/file.mkv", "GET, HEAD, OPTIONS, PROPFIND"],
      ["DELETE", "/protocol/content", "GET, HEAD, OPTIONS, PROPFIND"],
      ["DELETE", "/protocol/completed-symlinks", "GET, HEAD, OPTIONS, PROPFIND"],
      ["COPY", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["MOVE", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["MKCOL", "/protocol/content/new", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["PROPPATCH", "/protocol/content/movies", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["LOCK", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["UNLOCK", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["POST", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["PATCH", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
      ["TRACE", "/protocol/content/movies/file.mkv", "GET, HEAD, OPTIONS, PROPFIND, DELETE"],
    ] as const)("rejects unsupported WebDAV mutation %s %s", async (method, path, allow) => {
      const response = await requestRaw("/nzbdav", path, { method });

      expectStableReject(response, 405, "method_not_allowed", allow);
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["GET", "/protocol/content/file.mkv"],
      ["PROPFIND", "/protocol/content"],
      ["PUT", "/protocol/nzbs/movies/release.nzb"],
      ["DELETE", "/protocol/content/file.mkv"],
    ] as const)("rejects Destination on allowed %s %s", async (method, path) => {
      const response = await requestRaw("/nzbdav", path, {
        method,
        headers: { Destination: target("/nzbdav", "/protocol/content/other.mkv") },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["empty", ""],
      ["repeated", ["http://one.invalid/a", "http://two.invalid/b"] as string[]],
      ["oversized", `http://example.invalid/${"x".repeat(8193)}`],
    ] as const)("rejects %s Destination values", async (_name, destination) => {
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Destination: destination },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("rewrites every bounded same-listener WebDAV tagged resource", async () => {
      const source = target("/edge/apps/nzbdav", "/protocol/content/source.mkv");
      const other = target("/edge/apps/nzbdav", "/protocol/content/other.mkv");
      const response = await requestRaw("/edge/apps/nzbdav", "/protocol/content/source.mkv", {
        method: "DELETE",
        headers: {
          Authorization: "Basic fixture",
          If: `<${source}> ([\"fixture-a\"]) <${other}> ([\"fixture-b\"])`,
        },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.if).toBe(
        `<${backendOrigin}/edge/apps/nzbdav/protocol/content/source.mkv> ([\"fixture-a\"]) `
        + `<${backendOrigin}/edge/apps/nzbdav/protocol/content/other.mkv> ([\"fixture-b\"])`,
      );
    });

    it.each(mountBases)(
      "rewrites a same-listener WebDAV tagged resource under URL_BASE=%s",
      async (base) => {
        const source = target(base, "/protocol/content/source.mkv");
        const response = await requestRaw(base, "/protocol/content/source.mkv", {
          method: "DELETE",
          headers: {
            Authorization: "Basic fixture",
            If: `<${source}> (["fixture-etag"])`,
          },
        });

        expect(response.status).toBe(204);
        expect(captures).toHaveLength(1);
        expect(captures[0].headers.if).toBe(
          `<${backendOrigin}${webDavPathBase(base)}/content/source.mkv> (["fixture-etag"])`,
        );
      },
    );

    it("preserves an untagged WebDAV If condition", async () => {
      const condition = '(["fixture-etag"])';
      const response = await requestRaw("/nzbdav", "/protocol/content/source.mkv", {
        headers: { Authorization: "Basic fixture", If: condition },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.if).toBe(condition);
    });

    it.each([
      ["foreign authority", "<http://example.invalid/protocol/content/file.mkv> ([\"x\"])", undefined],
      ["wrong URL_BASE", "same-origin-wrong-base", undefined],
      ["wrong namespace", "same-origin-api", undefined],
      ["encoded separator", "same-origin-encoded", undefined],
      ["double-encoded separator", "same-origin-double", undefined],
      ["malformed URI", "<not a uri> ([\"x\"])", undefined],
      ["mixed valid and foreign", "mixed", undefined],
    ] as const)("rejects a %s tagged If condition", async (_name, fixture, repeated) => {
      const origin = frontends.find((candidate) => candidate.base === "/nzbdav")!.origin;
      const valid = `${origin}/nzbdav/protocol/content/file.mkv`;
      const values: Record<string, string> = {
        "same-origin-wrong-base": `<${origin}/protocol/content/file.mkv> ([\"x\"])`,
        "same-origin-api": `<${origin}/nzbdav/protocol/api> ([\"x\"])`,
        "same-origin-encoded": `<${origin}/nzbdav/protocol/content%2Ffile.mkv> ([\"x\"])`,
        "same-origin-double": `<${origin}/nzbdav/protocol/content%252Ffile.mkv> ([\"x\"])`,
        mixed: `<${valid}> ([\"x\"]) <http://example.invalid/protocol/content/file.mkv> ([\"y\"])`,
      };
      const header = repeated ?? values[fixture] ?? fixture;
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: header },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("preserves and rewrites a valid 8192-character tagged If header", async () => {
      const source = target("/nzbdav", "/protocol/content/file.mkv");
      const condition = taggedIfOfLength(source, 8192);
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: condition },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.if).toBe(
        condition.replace(source, `${backendOrigin}/nzbdav/protocol/content/file.mkv`),
      );
    });

    it("preserves a valid 8192-character untagged If header", async () => {
      const condition = untaggedIfOfLength(8192);
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: condition },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.if).toBe(condition);
    });

    it("rejects a valid 8193-character tagged If header before upstream", async () => {
      const source = target("/nzbdav", "/protocol/content/file.mkv");
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: taggedIfOfLength(source, 8193) },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("rejects a valid 8193-character untagged If header before upstream", async () => {
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: untaggedIfOfLength(8193) },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("rejects two individually valid tagged If header fields", async () => {
      const source = target("/nzbdav", "/protocol/content/file.mkv");
      const condition = `<${source}> ([\"fixture-etag\"])`;
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: [condition, condition] },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("rejects two individually valid untagged If header fields", async () => {
      const condition = '(["fixture-etag"])';
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: "Basic fixture", If: [condition, condition] },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each(["missing", "repeated", "malformed"] as const)(
      "rejects a raw %s Host for a tagged WebDAV If condition before upstream",
      async (fixture) => {
        const frontend = frontends.find((candidate) => candidate.base === "/nzbdav");
        if (!frontend) throw new Error("Missing nested frontend fixture");
        const authority = new URL(frontend.origin).host;
        const source = `${frontend.origin}/nzbdav/protocol/content/file.mkv`;
        const hostLines = fixture === "missing"
          ? []
          : fixture === "repeated"
            ? [`Host: ${authority}`, `Host: ${authority}`]
            : ["Host: malformed host"];
        const response = await requestRawHttp(
          "/nzbdav",
          "DELETE /nzbdav/protocol/content/file.mkv HTTP/1.1",
          [
            ...hostLines,
            "Authorization: Basic fixture",
            `If: <${source}> ([\"fixture-etag\"])`,
            "Connection: close",
          ],
        );

        expect(response.status).toBe(400);
        expect(captures).toHaveLength(0);
      },
    );

    it("preserves only WebDAV authority and semantic headers", async () => {
      const response = await requestRaw("/nzbdav", "/protocol/content/Movies", {
        method: "PROPFIND",
        headers: securityHeaders({
          Authorization: "Basic fixture",
          DAV: "1, 2",
          Depth: "1",
          Range: "bytes=0-9",
          "x-api-key": "pub",
        }),
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.authorization).toBe("Basic fixture");
      expect(captures[0].headers.dav).toBe("1, 2");
      expect(captures[0].headers.depth).toBe("1");
      expect(captures[0].headers.range).toBe("bytes=0-9");
      expectSensitiveHeadersStripped(captures[0].headers, [
        "authorization",
        "x-pinrail-webdav-path-base",
      ]);
    });

    it.each([undefined, "", "Bearer fixture"])(
      "does not invent WebDAV Basic authority when Authorization=%s",
      async (authorization) => {
        const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
          headers: authorization === undefined ? undefined : { Authorization: authorization },
        });

        expect(response.status).toBe(204);
        expect(captures).toHaveLength(1);
        expect(captures[0].headers.authorization).toBe(authorization);
      },
    );

    it("preserves the 8192-character WebDAV Authorization boundary", async () => {
      const authorization = `Basic ${"x".repeat(8192 - "Basic ".length)}`;
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: authorization },
      });

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0].headers.authorization).toBe(authorization);
    });

    it.each([
      ["repeated", ["Basic one", "Basic two"] as string[]],
      ["oversized", `Basic ${"x".repeat(8193 - "Basic ".length)}`],
    ] as const)("rejects a %s WebDAV Authorization header before upstream", async (_name, authorization) => {
      const response = await requestRaw("/nzbdav", "/protocol/content/file.mkv", {
        headers: { Authorization: authorization },
      });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });
  });

  describe("frontend action framing", () => {
    it.each(mountBases)("lets bounded login and onboarding actions reach React Router under URL_BASE=%s", async (base) => {
      for (const path of ["/login", "/onboarding"]) {
        const body = "username=u&password=p";
        const response = await requestRaw(base, path, {
          method: "POST",
          headers: {
            "content-length": String(Buffer.byteLength(body)),
            "content-type": "application/x-www-form-urlencoded",
          },
          body,
        });
        expect(response.status).toBe(418);
      }

      expect(authentication.isAuthenticated).toHaveBeenCalledTimes(2);
      expect(reactApplication.handler).toHaveBeenCalledTimes(2);
      expect(captures).toHaveLength(0);
    });

    it("accepts the exact frontend action body boundary", async () => {
      const body = Buffer.alloc(16 * 1024, 0x61);
      const response = await requestRaw("/nzbdav", "/settings/update", {
        method: "POST",
        headers: {
          "content-length": String(body.byteLength),
          "content-type": "application/octet-stream",
        },
        body,
      });

      expect(response.status).toBe(418);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expect(reactApplication.handler).toHaveBeenCalledOnce();
      expect(captures).toHaveLength(0);
    });

    for (const base of mountBases) {
      it.each([
        ["missing Content-Length", [], ""],
        ["chunked Transfer-Encoding", ["Transfer-Encoding: chunked"], "1\r\nx\r\n0\r\n\r\n"],
        ["noncanonical Content-Length", ["Content-Length: 01"], "x"],
        ["overlimit Content-Length", [`Content-Length: ${16 * 1024 + 1}`], "x".repeat(16 * 1024 + 1)],
      ] as const)(`rejects %s before auth/RR/upstream under URL_BASE=${base || "<empty>"}`, async (
        _label,
        framingHeaders,
        rawBody,
      ) => {
        const fixture = frontends.find((candidate) => candidate.base === base)!;
        const authority = new URL(fixture.origin).host;
        const target = `${base}/settings/update` || "/settings/update";
        const response = await requestRawHttp(base, `POST ${target} HTTP/1.1`, [
          `Host: ${authority}`,
          "Content-Type: application/octet-stream",
          ...framingHeaders,
          "Connection: close",
        ], rawBody);

        expectStableReject(response, 400, "invalid_request");
        expect(authentication.isAuthenticated).not.toHaveBeenCalled();
        expect(reactApplication.handler).not.toHaveBeenCalled();
        expect(captures).toHaveLength(0);
      });
    }

    it.each([
      ["duplicate Content-Length", ["Content-Length: 1", "Content-Length: 1"], "x"],
      ["Content-Length plus Transfer-Encoding", ["Content-Length: 1", "Transfer-Encoding: chunked"], "0\r\n\r\n"],
    ] as const)("distinguishes Node parser-owned rejection for %s", async (_label, framingHeaders, rawBody) => {
      const fixture = frontends.find((candidate) => candidate.base === "/nzbdav")!;
      const authority = new URL(fixture.origin).host;
      const response = await requestRawHttp(
        "/nzbdav",
        "POST /nzbdav/settings/update HTTP/1.1",
        [`Host: ${authority}`, ...framingHeaders, "Connection: close"],
        rawBody,
      );

      expect(response.status).toBe(400);
      expect(response.headers["x-error-code"]).toBeUndefined();
      expect(response.headers["x-correlation-id"]).toBeUndefined();
      expect(authentication.isAuthenticated).not.toHaveBeenCalled();
      expect(reactApplication.handler).not.toHaveBeenCalled();
      expect(captures).toHaveLength(0);
    });
  });

  describe("raw target denial and frontend isolation", () => {
    it.each([
      ["absolute form", "GET", "http://example.invalid/protocol/api"],
      ["asterisk form", "OPTIONS", "*"],
    ] as const)("rejects a %s request target before upstream", async (_name, method, path) => {
      const response = await requestRaw("", path, { method });

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["empty", "GET  HTTP/1.1"],
      ["control byte", "GET /nzbdav/protocol/api\u0001 HTTP/1.1"],
    ])("makes zero upstream calls for a parser-rejected %s target", async (_name, requestLine) => {
      const response = await requestRawRequestLine("/nzbdav", requestLine);

      expect(response.status).toBe(400);
      expect(captures).toHaveLength(0);
    });

    it.each([
      "/protocol/api%2Fget-config",
      "/protocol/api%252Fget-config",
      "/protocol/content/%2e%2e/secret",
      "/protocol/content/%252e%252e/secret",
      "/protocol/content/../secret",
      "/protocol//api",
      "/protocol/api/%",
      "/protocol/api/%00",
      "/protocol/content\\secret",
      "/protocol/api#fragment",
      "/api%2Fget-config",
    ])("rejects ambiguous raw target %s before upstream", async (path) => {
      const response = await requestRaw("/nzbdav", path);

      expectStableReject(response, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it("enforces the 8192-character mount-relative target boundary", async () => {
      const prefix = "/protocol/content/";
      const maximum = prefix + "a".repeat(8192 - prefix.length);
      const accepted = await requestRaw("/nzbdav", maximum);
      expect(accepted.status).toBe(204);
      expect(captures).toHaveLength(1);

      resetBackendCaptureOracle();
      const rejected = await requestRaw("/nzbdav", `${maximum}a`);
      expectStableReject(rejected, 400, "invalid_request_target");
      expect(captures).toHaveLength(0);
    });

    it.each([
      "/apiary",
      "/viewing/file",
      "/contention/file",
      "/.ids-backup/file",
      "/nzbs-old/file",
      "/completed-symlinks.evil/file",
    ])("does not proxy prefix-confused frontend route %s", async (path) => {
      const response = await requestRaw("/nzbdav", path);

      expect(response.status).toBe(418);
      expect(captures).toHaveLength(0);
    });

    it.each([
      ["GET", "/.ids/a", 418],
      ["PROPFIND", "/.ids/a", 418],
      ["PUT", "/nzbs/movies/file.nzb", 418],
      ["DELETE", "/nzbs/movies/file.nzb", 400],
      ["DELETE", "/content/movie.mkv", 400],
      ["DELETE", "/completed-symlinks/a", 400],
    ] as const)(
      "does not retain legacy root WebDAV surface %s %s",
      async (method, path, expectedStatus) => {
        const response = await requestRaw("/nzbdav", path, {
          method,
          headers: { Authorization: "Basic fixture" },
        });

        if (expectedStatus === 400) expectStableReject(response, 400, "invalid_request");
        else expect(response.status).toBe(expectedStatus);
        expect(captures).toHaveLength(0);
      },
    );

    it.each([
      ["PROPFIND", 418],
      ["OPTIONS", 400],
    ] as const)("never proxies unrelated %s", async (method, expectedStatus) => {
      const response = await requestRaw("/nzbdav", "/ordinary-ui-route", { method });

      if (expectedStatus === 400) expectStableReject(response, 400, "invalid_request");
      else expect(response.status).toBe(expectedStatus);
      expect(captures).toHaveLength(0);
    });

    it.each([
      [
        "independently authenticated protocol",
        "/protocol/api?mode=version&apikey=proxy-canary",
        { headers: { "x-test-canary": "header-canary" } },
        ["proxy-canary", "header-canary"],
      ],
      [
        "principal-protected UI admin",
        "/api/test-arr-connection",
        {
          method: "POST",
          headers: securityHeaders({
            Authorization: "Bearer authorization-canary",
            Cookie: "session=cookie-canary",
            Forwarded: "for=forwarded-standard-for-canary;host=forwarded-canary.invalid",
            "Proxy-Authorization": "Basic proxy-authorization-canary",
            "content-type": "application/json",
            "content-length": "2",
            "x-api-key": "client-api-key-canary",
            "x-authentik-email": "email-canary@example.invalid",
            "x-authentik-groups": "groups-canary",
            "x-authentik-meta-app": "app-canary",
            "x-authentik-name": "name-canary",
            "x-authentik-uid": "uid-canary",
            "x-authentik-username": "username-canary",
            "x-forwarded-for": "forwarded-for-canary",
            "x-forwarded-host": "forwarded-host-canary.invalid",
            "x-forwarded-port": "forwarded-port-canary",
            "x-forwarded-prefix": "/forwarded-prefix-canary",
            "x-forwarded-proto": "forwarded-proto-canary",
            "x-forwarded-server": "forwarded-server-canary.invalid",
            "x-test-canary": "header-canary",
          }),
          body: "{}",
        },
        [
          "authorization-canary",
          "cookie-canary",
          "forwarded-standard-for-canary",
          "forwarded-canary",
          "proxy-authorization-canary",
          "client-api-key-canary",
          "email-canary",
          "groups-canary",
          "app-canary",
          "name-canary",
          "uid-canary",
          "username-canary",
          "forwarded-for-canary",
          "forwarded-host-canary",
          "forwarded-port-canary",
          "forwarded-prefix-canary",
          "forwarded-proto-canary",
          "forwarded-server-canary",
          "header-canary",
        ],
      ],
      [
        "independently authenticated WebDAV",
        "/protocol/content/webdav-path-canary.mkv",
        { headers: { Authorization: "Basic webdav-authorization-canary" } },
        ["webdav-path-canary", "webdav-authorization-canary"],
      ],
    ] as const)("returns a bounded redacted proxy failure for %s", async (
      _name,
      path,
      options,
      canaries,
    ) => {
      const errorLog = vi.spyOn(console, "error").mockImplementation(() => undefined);
      const warningLog = vi.spyOn(console, "warn").mockImplementation(() => undefined);
      const infoLog = vi.spyOn(console, "info").mockImplementation(() => undefined);
      const debugLog = vi.spyOn(console, "debug").mockImplementation(() => undefined);
      const standardLog = vi.spyOn(console, "log").mockImplementation(() => undefined);
      const stderrWrite = vi.spyOn(process.stderr, "write")
        .mockImplementation((() => true) as typeof process.stderr.write);
      const stdoutWrite = vi.spyOn(process.stdout, "write")
        .mockImplementation((() => true) as typeof process.stdout.write);
      try {
        const response = await requestFailedProxy(path, options);
        await waitForEventLoopTurn();

        expectStableReject(response, 502, "upstream_unavailable");
        expect(failedBackendConnections).toBe(1);
        expect(failedBackendOverflow).toBe(false);
        const output = [
          response.body,
          inspect(response.headers, { depth: 8 }),
          ...[
            ...errorLog.mock.calls,
            ...warningLog.mock.calls,
            ...infoLog.mock.calls,
            ...debugLog.mock.calls,
            ...standardLog.mock.calls,
          ]
            .flatMap((call) => call.map((value) => inspect(value, { depth: 8 }))),
          ...stderrWrite.mock.calls.map((call) => renderProcessWrite(call[0])),
          ...stdoutWrite.mock.calls.map((call) => renderProcessWrite(call[0])),
        ].join(" ");
        for (const canary of canaries) expect(output).not.toContain(canary);
        expect(output).not.toContain("unit");
        expect(output).not.toContain(new URL(failedBackendOrigin).host);
      } finally {
        errorLog.mockRestore();
        warningLog.mockRestore();
        infoLog.mockRestore();
        debugLog.mockRestore();
        standardLog.mockRestore();
        stderrWrite.mockRestore();
        stdoutWrite.mockRestore();
      }
    });
  });
});

function target(base: string, path: string): string {
  const fixture = frontends.find((candidate) => candidate.base === base);
  if (!fixture) throw new Error(`Missing frontend fixture for ${base}`);
  return `${fixture.origin}${base}${path}`;
}

function webDavPathBase(base: string): string {
  return `${base}/protocol`;
}

function encodedWebDavPathBase(base: string): string {
  return Buffer.from(webDavPathBase(base), "utf8").toString("base64url");
}

function renderProcessWrite(value: unknown): string {
  if (typeof value === "string") return value;
  if (value instanceof Uint8Array) return Buffer.from(value).toString("utf8");
  return inspect(value, { depth: 8 });
}

function requestRaw(
  base: string,
  path: string,
  options: RequestOptions = {},
): Promise<RawResponse> {
  const fixture = frontends.find((candidate) => candidate.base === base);
  if (!fixture) throw new Error(`Missing frontend fixture for ${base}`);
  return requestFixture(fixture, path, options);
}

function requestFailedProxy(path: string, options: RequestOptions = {}): Promise<RawResponse> {
  return requestFixture(failedProxyFrontend, path, options);
}

function requestFixture(
  fixture: FrontendFixture,
  path: string,
  options: RequestOptions = {},
): Promise<RawResponse> {
  return requestLoopbackBounded(
    fixture.origin,
    `${fixture.base}${path}`,
    options,
    { timeoutMs: 5_000, maxResponseBytes: 4 * 1024 * 1024 },
  ).then((response) => ({
    status: response.status,
    headers: response.headers,
    body: response.body.toString("utf8"),
  }));
}

function requestRawRequestLine(base: string, requestLine: string): Promise<RawResponse> {
  const fixture = frontends.find((candidate) => candidate.base === base);
  if (!fixture) throw new Error(`Missing frontend fixture for ${base}`);
  const origin = new URL(fixture.origin);
  return requestRawHttp(base, requestLine, [
    `Host: ${origin.host}`,
    "Connection: close",
  ]);
}

function requestRawHttp(
  base: string,
  requestLine: string,
  headerLines: readonly string[],
  rawBody = "",
): Promise<RawResponse> {
  const fixture = frontends.find((candidate) => candidate.base === base);
  if (!fixture) throw new Error(`Missing frontend fixture for ${base}`);
  const origin = new URL(fixture.origin);
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: origin.hostname, port: Number(origin.port) });
    let response = Buffer.alloc(0);
    let settled = false;
    const finish = (error?: Error, value?: RawResponse) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      if (error) reject(error);
      else if (value) resolve(value);
      else reject(new Error("Raw disposable request failed"));
    };
    const timeout = setTimeout(() => {
      finish(new Error("Raw disposable request timed out"));
    }, 2_000);
    socket.once("error", (error) => {
      finish(error);
    });
    socket.once("connect", () => {
      socket.write([requestLine, ...headerLines, "", rawBody].join("\r\n"));
    });
    socket.on("data", (chunk) => {
      if (response.byteLength + chunk.byteLength > 64 * 1024) {
        finish(new Error("Raw disposable response exceeded its bound"));
        return;
      }
      response = Buffer.concat([response, chunk]);
    });
    socket.once("end", () => {
      const text = response.toString("utf8");
      const [head, body = ""] = text.split("\r\n\r\n", 2);
      const [statusLine, ...headerLines] = head.split("\r\n");
      const match = /^HTTP\/1\.1 (\d{3})\b/.exec(statusLine);
      if (!match) {
        finish(new Error("Invalid raw disposable response"));
        return;
      }
      const headers: IncomingHttpHeaders = {};
      for (const line of headerLines) {
        const separator = line.indexOf(":");
        if (separator < 0) continue;
        headers[line.slice(0, separator).toLowerCase()] = line.slice(separator + 1).trim();
      }
      finish(undefined, { status: Number(match[1]), headers, body });
    });
  });
}

function listen(server: Server): Promise<string> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("Disposable server did not bind a TCP port"));
        return;
      }
      resolve(`http://127.0.0.1:${address.port}`);
    });
  });
}

function trackServer(server: Server): Server {
  ownedServers.push(server);
  return server;
}

function close(server: Server): Promise<void> {
  return closeHttpServerBounded(server, 5_000);
}

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

function waitForEventLoopTurn(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve));
}

async function waitFor(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 1_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("Disposable condition timed out");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

function resetBackendCaptureOracle(): void {
  if (
    backendAttemptCount !== captures.length
    || captures.some((capture) => !capture.complete || capture.aborted || capture.errored)
  ) {
    throw new Error("Disposable proxy oracle failed before reset.");
  }
  captures.length = 0;
  backendAttemptCount = 0;
}

function securityHeaders(
  overrides: http.OutgoingHttpHeaders = {},
): http.OutgoingHttpHeaders {
  return {
    Authorization: "Bearer browser",
    Cookie: "session=browser",
    Forwarded: "for=192.0.2.10;proto=https;host=evil.invalid",
    "Proxy-Authorization": "Basic proxy",
    "x-api-key": "pub",
    "x-authentik-email": "browser@example.invalid",
    "x-authentik-groups": "operators",
    "x-authentik-meta-app": "nzbdav",
    "x-authentik-name": "Browser User",
    "x-authentik-uid": "browser",
    "x-authentik-username": "browser",
    "x-forwarded-for": "192.0.2.10",
    "x-forwarded-host": "evil.invalid",
    "x-forwarded-port": "443",
    "x-forwarded-prefix": "/evil",
    "x-forwarded-proto": "https",
    "x-forwarded-server": "evil.invalid",
    Connection: "keep-alive, x-hop-by-hop",
    "x-hop-by-hop": "header-canary",
    ...overrides,
  };
}

const sensitiveHeaders = [
  "authorization",
  "cookie",
  "forwarded",
  "proxy-authorization",
  "x-api-key",
  "x-authentik-email",
  "x-authentik-groups",
  "x-authentik-meta-app",
  "x-authentik-name",
  "x-authentik-uid",
  "x-authentik-username",
  "x-forwarded-for",
  "x-forwarded-host",
  "x-forwarded-port",
  "x-forwarded-prefix",
  "x-forwarded-proto",
  "x-forwarded-server",
  "x-hop-by-hop",
  "x-pinrail-webdav-path-base",
] as const;

function expectSensitiveHeadersStripped(
  headers: IncomingHttpHeaders,
  preserved: readonly string[] = [],
): void {
  const preservedSet = new Set(preserved);
  for (const name of sensitiveHeaders) {
    if (!preservedSet.has(name)) expect(headers[name], name).toBeUndefined();
  }
  expect(headers.connection).not.toContain("x-hop-by-hop");
}

function expectStableReject(
  response: RawResponse,
  status: number,
  code: string,
  allow?: string,
): void {
  expect(response.status).toBe(status);
  expect(response.headers["content-type"]).toMatch(/^application\/json\b/);
  expect(response.body.length).toBeLessThanOrEqual(256);
  const correlationId = response.headers["x-correlation-id"];
  expect(correlationId).toMatch(/^[0-9a-f]{32}$/u);
  expect(JSON.parse(response.body)).toEqual({
    status: false,
    error: safeProxyFailureMessage(code),
    code,
    correlation_id: correlationId,
  });
  if (allow === undefined) expect(response.headers.allow).toBeUndefined();
  else expect(response.headers.allow).toBe(allow);
}

function safeProxyFailureMessage(code: string): string {
  switch (code) {
    case "invalid_request": return "The request is invalid.";
    case "invalid_request_target": return "The request is invalid.";
    case "route_not_found": return "The requested route was not found.";
    case "method_not_allowed": return "The request method is not allowed.";
    case "upstream_unavailable": return "The backend is unavailable.";
    case "authentication_required": return "Authentication is required.";
    default: return "The request could not be completed.";
  }
}

function taggedIfOfLength(resource: string, length: number): string {
  const prefix = `<${resource}> ([\"`;
  const suffix = `\"])`;
  const payloadLength = length - prefix.length - suffix.length;
  if (payloadLength < 1) throw new Error("Tagged If fixture length is too small");
  return `${prefix}${"x".repeat(payloadLength)}${suffix}`;
}

function untaggedIfOfLength(length: number): string {
  const prefix = '(["';
  const suffix = '"])';
  const payloadLength = length - prefix.length - suffix.length;
  if (payloadLength < 1) throw new Error("Untagged If fixture length is too small");
  return `${prefix}${"x".repeat(payloadLength)}${suffix}`;
}
