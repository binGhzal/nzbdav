/** @vitest-environment node */

import { createHash, timingSafeEqual } from "node:crypto";
import http, {
  type OutgoingHttpHeaders,
  type Server,
} from "node:http";
import { readFile } from "node:fs/promises";
import express from "express";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  startDisposableAspNetBackend,
  type DisposableAspNetBackend,
} from "./test-support/disposable-aspnet-backend";
import {
  closeHttpServerBounded,
  relayHttpRequestBounded,
  requestLoopbackBounded,
  withAbsoluteDeadline,
  type BoundedHttpResponse,
} from "./test-support/bounded-http";

vi.setConfig({ testTimeout: 150_000 });

const authentication = vi.hoisted(() => ({
  isAuthenticated: vi.fn(),
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
    _req: express.Request,
    res: express.Response,
  ) => res.status(418).type("text/plain").send("frontend"),
}));

type SecretClass = "missing" | "internal" | "public" | "valid-basic" | "other";

type RelayHit = Readonly<{
  method: string;
  pathname: string;
  apiKey: SecretClass;
  authorization: SecretClass;
  hasApiKeyQuery: boolean;
  hasDownloadKeyQuery: boolean;
  hasRange: boolean;
}>;

type RelayFixture = Readonly<{
  origin: string;
  server: Server;
  hits: RelayHit[];
  reset: () => void;
  assertHealthy: () => void;
}>;

type FrontendFixture = Readonly<{
  base: string;
  origin: string;
  server: Server;
}>;

type RequestOptions = Readonly<{
  method?: string;
  headers?: OutgoingHttpHeaders;
  body?: string | Buffer;
}>;

const mountBases = ["", "/nzbdav", "/edge/apps/nzbdav"] as const;
const MAX_RELAY_ATTEMPTS_PER_TEST = 16;
const originalBackendUrl = process.env.BACKEND_URL;
const originalInternalApiKey = process.env.FRONTEND_BACKEND_API_KEY;
const frontends: FrontendFixture[] = [];
const ownedServers: Server[] = [];
let backend: DisposableAspNetBackend;
let relay: RelayFixture;
let readme: Buffer;
let signedReadmePath: string;

beforeAll(async () => {
  backend = await startDisposableAspNetBackend();
  relay = await startCountingRelay(backend);
  readme = await readFile(new URL(
    "../../backend/WebDav/StaticFiles/root/README.md",
    import.meta.url,
  ));
  signedReadmePath = `/view/README?downloadKey=${signedDownloadKey(backend)}`;

  process.env.BACKEND_URL = relay.origin;
  process.env.FRONTEND_BACKEND_API_KEY = backend.credentials.internalApiKey;

  for (const base of mountBases) {
    vi.resetModules();
    const { app } = await import("./app");
    const parent = express();
    parent.disable("x-powered-by");
    parent.use(base || "/", app);
    const server = trackServer(http.createServer(parent));
    frontends.push({ base, origin: await listen(server), server });
  }
}, 130_000);

afterAll(async () => {
  const cleanupFailures: Error[] = [];
  try {
    const closeResults = await Promise.allSettled(
      ownedServers.splice(0).map((server) => closeHttpServerBounded(server, 5_000)),
    );
    for (const result of closeResults) {
      if (result.status === "rejected") {
        cleanupFailures.push(new Error("Disposable HTTP server cleanup failed."));
      }
    }
  } finally {
    try {
      if (backend) {
        await withAbsoluteDeadline(
          backend.stop(),
          15_000,
          "Disposable backend cleanup failed.",
        );
      }
    } catch {
      cleanupFailures.push(new Error("Disposable backend cleanup failed."));
    } finally {
      restoreEnvironment("BACKEND_URL", originalBackendUrl);
      restoreEnvironment("FRONTEND_BACKEND_API_KEY", originalInternalApiKey);
    }
  }
  if (cleanupFailures.length > 0) {
    throw new AggregateError(cleanupFailures, "Disposable composition fixture cleanup failed.");
  }
}, 30_000);

beforeEach(() => {
  relay.reset();
  authentication.isAuthenticated.mockReset();
  authentication.isAuthenticated.mockResolvedValue(true);
});

afterEach(async () => {
  await new Promise<void>((resolve) => setImmediate(resolve));
  relay.assertHealthy();
});

describe("real ASP.NET production proxy boundary", () => {
  it.each(mountBases)(
    "injects the internal key for an authenticated UI control under URL_BASE=%s",
    async (base) => {
      const response = await requestFrontend(base, "/api/get-health-check-queue");

      expect(response.status).toBe(200);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expectSingleHit("GET", "/api/get-health-check-queue", {
        apiKey: "internal",
        authorization: "missing",
      });
    },
  );

  it.each(mountBases)(
    "preserves every accepted SAB get_cats carrier under URL_BASE=%s",
    async (base) => {
      const cases: readonly (RequestOptionsWithPath & {
        relayExpected: Partial<Omit<RelayHit, "method" | "pathname">>;
      })[] = [
        {
          path: "/protocol/api?mode=get_cats",
          headers: { "x-api-key": backend.credentials.publicApiKey },
          relayExpected: { apiKey: "public", hasApiKeyQuery: false },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: "/protocol/api",
          method: "POST",
          headers: { "content-type": "application/x-www-form-urlencoded" },
          body: new URLSearchParams({
            mode: "get_cats",
            apikey: backend.credentials.publicApiKey,
          }).toString(),
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          headers: { "x-api-key": backend.credentials.publicApiKey },
          relayExpected: { apiKey: "public", hasApiKeyQuery: true },
        },
      ];

      for (const fixture of cases) {
        resetRelay();
        const response = await requestFrontend(base, fixture.path, fixture);
        expect(response.status).toBe(200);
        expectSingleHit(fixture.method ?? "GET", "/api", fixture.relayExpected);
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "lets the sealed backend decide every malformed SAB carrier shape under URL_BASE=%s",
    async (base) => {
      const cases: readonly (RequestOptionsWithPath & {
        expected: number;
        relayExpected: Partial<Omit<RelayHit, "method" | "pathname">>;
      })[] = [
        {
          path: "/protocol/api?mode=get_cats",
          expected: 401,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          headers: { "x-api-key": "invalid" },
          expected: 401,
          relayExpected: { apiKey: "other", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats&apikey=invalid",
          headers: { "x-api-key": backend.credentials.publicApiKey },
          expected: 400,
          relayExpected: { apiKey: "public", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}&apikey=invalid`,
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apiKey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: `/protocol/api?mode=get_cats&APIKEY=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats&apikey=",
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${"x".repeat(513)}`,
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([
            ["apikey", backend.credentials.publicApiKey],
            ["apikey", backend.credentials.publicApiKey],
          ]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([
            ["apikey", backend.credentials.publicApiKey],
            ["apikey", "invalid"],
          ]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([["apiKey", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([["APIKEY", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([["apikey", ""]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders(),
          body: formBody([["apikey", "x".repeat(513)]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders({ "x-api-key": backend.credentials.publicApiKey }),
          body: formBody([["apikey", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "public", hasApiKeyQuery: false },
        },
        {
          path: "/protocol/api?mode=get_cats",
          method: "POST",
          headers: formHeaders({ "x-api-key": backend.credentials.publicApiKey }),
          body: formBody([["apikey", "invalid"]]),
          expected: 400,
          relayExpected: { apiKey: "public", hasApiKeyQuery: false },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          method: "POST",
          headers: formHeaders(),
          body: formBody([["apikey", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          method: "POST",
          headers: formHeaders(),
          body: formBody([["apikey", "invalid"]]),
          expected: 400,
          relayExpected: { apiKey: "missing", hasApiKeyQuery: true },
        },
        {
          path: `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          method: "POST",
          headers: formHeaders({ "x-api-key": backend.credentials.publicApiKey }),
          body: formBody([["apikey", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "public", hasApiKeyQuery: true },
        },
        {
          path: "/protocol/api?mode=get_cats&apikey=invalid",
          method: "POST",
          headers: formHeaders({ "x-api-key": backend.credentials.publicApiKey }),
          body: formBody([["apikey", backend.credentials.publicApiKey]]),
          expected: 400,
          relayExpected: { apiKey: "public", hasApiKeyQuery: true },
        },
      ];

      for (const fixture of cases) {
        resetRelay();
        const response = await requestFrontend(base, fixture.path, fixture);
        expect(response.status).toBe(fixture.expected);
        expectSingleHit(fixture.method ?? "GET", "/api", fixture.relayExpected);
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "rejects a Connection-nominated conflicting public carrier before ASP.NET under URL_BASE=%s",
    async (base) => {
      const response = await requestFrontend(
        base,
        `/protocol/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
        {
          headers: {
            Connection: "x-api-key",
            "x-api-key": "invalid",
          },
        },
      );

      expect(response.status).toBe(400);
      expect(relay.hits).toHaveLength(0);
    },
    240_000,
  );

  it.each(mountBases)(
    "routes exact public ARR reads and keeps report mode under URL_BASE=%s",
    async (base) => {
      const routes = [
        ["/protocol/api/arr/validation", "/api/arr/validation"],
        ["/protocol/api/arr/search-nudges?limit=1", "/api/arr/search-nudges"],
        ["/protocol/api/arr/correlations?limit=1", "/api/arr/correlations"],
      ] as const;

      for (const [path, backendPath] of routes) {
        resetRelay();
        const response = await requestFrontend(base, path, {
          headers: { "x-api-key": backend.credentials.publicApiKey },
        });
        expect(response.status).toBe(200);
        expectSingleHit("GET", backendPath, {
          apiKey: "public",
          hasApiKeyQuery: false,
        });
        if (backendPath === "/api/arr/validation") {
          const payload = JSON.parse(response.body.toString("utf8")) as {
            search_nudge_mode?: unknown;
          };
          expect(payload.search_nudge_mode).toBe("report");
        }
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "preserves query-only public authority for exact ARR reads under URL_BASE=%s",
    async (base) => {
      const routes = [
        ["/protocol/api/arr/validation", "/api/arr/validation"],
        ["/protocol/api/arr/search-nudges?limit=1", "/api/arr/search-nudges"],
        ["/protocol/api/arr/correlations?limit=1", "/api/arr/correlations"],
      ] as const;

      for (const [path, backendPath] of routes) {
        resetRelay();
        const separator = path.includes("?") ? "&" : "?";
        const response = await requestFrontend(
          base,
          `${path}${separator}apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
        );
        expect(response.status).toBe(200);
        expectSingleHit("GET", backendPath, {
          apiKey: "missing",
          hasApiKeyQuery: true,
        });
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "accepts no-op Test events for every public ARR kind under URL_BASE=%s",
    async (base) => {
      for (const app of ["sonarr", "radarr", "lidarr"] as const) {
        resetRelay();
        const response = await requestFrontend(base, `/protocol/api/arr/events/${app}`, {
          method: "POST",
          headers: {
            "content-type": "application/json",
            "x-api-key": backend.credentials.publicApiKey,
          },
          body: JSON.stringify({ eventType: "Test" }),
        });
        expect(response.status).toBe(200);
        expectSingleHit("POST", `/api/arr/events/${app}`, {
          apiKey: "public",
          hasApiKeyQuery: false,
        });
        const payload = JSON.parse(response.body.toString("utf8")) as {
          correlation?: unknown;
          event_type?: unknown;
        };
        expect(payload.event_type).toBe("Test");
        expect(payload.correlation).toBeNull();
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "preserves query-only public authority for every ARR Test event under URL_BASE=%s",
    async (base) => {
      for (const app of ["sonarr", "radarr", "lidarr"] as const) {
        resetRelay();
        const response = await requestFrontend(
          base,
          `/protocol/api/arr/events/${app}?apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
          {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ eventType: "Test" }),
          },
        );
        expect(response.status).toBe(200);
        expectSingleHit("POST", `/api/arr/events/${app}`, {
          apiKey: "missing",
          hasApiKeyQuery: true,
        });
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "preserves independent WebDAV Basic authentication under URL_BASE=%s",
    async (base) => {
      const cases: readonly (RequestOptions & { expected: number; authorization: SecretClass })[] = [
        { expected: 401, authorization: "missing" },
        {
          headers: { Authorization: basicAuthorization("invalid", "invalid") },
          expected: 401,
          authorization: "other",
        },
        {
          headers: { Authorization: validBasicAuthorization() },
          expected: 207,
          authorization: "valid-basic",
        },
      ];

      for (const fixture of cases) {
        resetRelay();
        const response = await requestFrontend(base, "/protocol/content", {
          method: "PROPFIND",
          headers: { Depth: "0", ...fixture.headers },
        });
        expect(response.status).toBe(fixture.expected);
        expectSingleHit("PROPFIND", "/content", {
          apiKey: "missing",
          authorization: fixture.authorization,
        });
        if (fixture.expected === 207) {
          expect(response.body.toString("utf8")).toContain(`${base}/protocol/content`);
        }
      }
    },
    240_000,
  );

  it.each(mountBases)(
    "streams authenticated WebDAV README GET, HEAD, and range under URL_BASE=%s",
    async (base) => {
      const authorization = validBasicAuthorization();

      const full = await requestFrontend(base, "/protocol/README", {
        headers: { Authorization: authorization },
      });
      expect(full.status).toBe(200);
      expect(full.body).toEqual(readme);
      expectSingleHit("GET", "/README", {
        apiKey: "missing",
        authorization: "valid-basic",
      });

      resetRelay();
      const head = await requestFrontend(base, "/protocol/README", {
        method: "HEAD",
        headers: { Authorization: authorization },
      });
      expect(head.status).toBe(200);
      expect(head.headers["content-length"]).toBe(String(readme.byteLength));
      expect(head.body.byteLength).toBe(0);
      expectSingleHit("HEAD", "/README", {
        apiKey: "missing",
        authorization: "valid-basic",
      });

      resetRelay();
      const range = await requestFrontend(base, "/protocol/README", {
        headers: { Authorization: authorization, Range: "bytes=1-7" },
      });
      expect(range.status).toBe(206);
      expect(range.headers["content-range"]).toBe(`bytes 1-7/${readme.byteLength}`);
      expect(range.body).toEqual(readme.subarray(1, 8));
      expectSingleHit("GET", "/README", {
        apiKey: "missing",
        authorization: "valid-basic",
        hasRange: true,
      });
    },
    240_000,
  );

  it.each(mountBases)(
    "streams signed principal-only README GET, HEAD, and range under URL_BASE=%s",
    async (base) => {
      const full = await requestFrontend(base, signedReadmePath);
      expect(full.status).toBe(200);
      expect(full.body).toEqual(readme);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expectSingleHit("GET", "/view/README", {
        apiKey: "missing",
        authorization: "missing",
        hasDownloadKeyQuery: true,
      });

      resetRelay();
      const head = await requestFrontend(base, signedReadmePath, { method: "HEAD" });
      expect(head.status).toBe(200);
      expect(head.headers["content-length"]).toBe(String(readme.byteLength));
      expect(head.body.byteLength).toBe(0);
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expectSingleHit("HEAD", "/view/README", {
        apiKey: "missing",
        authorization: "missing",
        hasDownloadKeyQuery: true,
      });

      resetRelay();
      const range = await requestFrontend(base, signedReadmePath, {
        headers: { Range: "bytes=1-7" },
      });
      expect(range.status).toBe(206);
      expect(range.body).toEqual(readme.subarray(1, 8));
      expect(authentication.isAuthenticated).toHaveBeenCalledOnce();
      expectSingleHit("GET", "/view/README", {
        apiKey: "missing",
        authorization: "missing",
        hasDownloadKeyQuery: true,
        hasRange: true,
      });
    },
    240_000,
  );

  it("replaces client authority on UI admin relays and denies anonymous UI access", async () => {
    const base = "/edge/apps/nzbdav";
    const replaced = await requestFrontend(base, "/api/get-health-check-queue", {
      headers: { "x-api-key": backend.credentials.publicApiKey },
    });
    expect(replaced.status).toBe(200);
    expectSingleHit("GET", "/api/get-health-check-queue", { apiKey: "internal" });

    resetRelay();
    authentication.isAuthenticated.mockResolvedValue(false);
    const denied = await requestFrontend(base, "/api/get-health-check-queue", {
      headers: { "x-api-key": backend.credentials.publicApiKey },
    });
    expect(denied.status).toBe(401);
    expect(relay.hits).toHaveLength(0);
  });

  it("makes zero upstream calls for excluded ARR and UI-admin routes", async () => {
    const cases = [
      ["POST", "/protocol/api/arr/search-nudges/00000000-0000-0000-0000-000000000000/retry", 404, undefined],
      ["POST", "/protocol/api/arr/search-nudges/clear", 404, undefined],
      ["POST", "/protocol/api/arr/correlations", 405, "GET"],
      ["GET", "/protocol/api/get-config", 404, undefined],
      ["GET", "/api/get-config", 404, undefined],
      ["GET", "/api/db.sqlite", 404, undefined],
      ["POST", "/api/repair/run", 404, undefined],
    ] as const;

    for (const [method, path, expectedStatus, allow] of cases) {
      resetRelay();
      const response = await requestFrontend("/nzbdav", path, {
        method,
        headers: { "x-api-key": backend.credentials.publicApiKey },
      });
      expect(response.status).toBe(expectedStatus);
      expect(response.headers.allow).toBe(allow);
      expect(relay.hits).toHaveLength(0);
    }
  });

  it("makes zero upstream calls for forbidden WebDAV methods and Destination", async () => {
    const cases: readonly (RequestOptionsWithPath & { expected: number })[] = [
      {
        path: "/protocol/content/README",
        method: "COPY",
        headers: { Authorization: validBasicAuthorization() },
        expected: 405,
      },
      {
        path: "/protocol/content/README",
        method: "PUT",
        headers: { Authorization: validBasicAuthorization() },
        expected: 405,
      },
      {
        path: "/protocol/README",
        headers: {
          Authorization: validBasicAuthorization(),
          Destination: "/nzbdav/protocol/content/other",
        },
        expected: 400,
      },
    ];

    for (const fixture of cases) {
      resetRelay();
      const response = await requestFrontend("/nzbdav", fixture.path, fixture);
      expect(response.status).toBe(fixture.expected);
      expect(relay.hits).toHaveLength(0);
    }
  });

  it("denies anonymous signed media and every protocol view target before upstream", async () => {
    authentication.isAuthenticated.mockResolvedValue(false);
    const anonymous = await requestFrontend("/nzbdav", signedReadmePath);
    expect(anonymous.status).toBe(401);
    expect(relay.hits).toHaveLength(0);

    resetRelay();
    const protocolView = await requestFrontend(
      "/nzbdav",
      signedReadmePath.replace("/view/", "/protocol/view/"),
    );
    expect(protocolView.status).toBe(404);
    expect(relay.hits).toHaveLength(0);
  });
});

type RequestOptionsWithPath = RequestOptions & Readonly<{ path: string }>;

function expectSingleHit(
  method: string,
  pathname: string,
  expected: Partial<Omit<RelayHit, "method" | "pathname">> = {},
): void {
  expect(relay.hits).toHaveLength(1);
  const hit = relay.hits[0];
  expect(hit?.method).toBe(method);
  expect(hit?.pathname).toBe(pathname);
  for (const [key, value] of Object.entries(expected)) {
    expect(hit?.[key as keyof RelayHit], key).toBe(value);
  }
}

function resetRelay(): void {
  relay.reset();
  authentication.isAuthenticated.mockClear();
}

async function startCountingRelay(
  target: DisposableAspNetBackend,
): Promise<RelayFixture> {
  const hits: RelayHit[] = [];
  let attemptCount = 0;
  let overflowed = false;
  const targetUrl = new URL(target.origin);
  const validBasic = basicAuthorization(
    target.credentials.webDavUsername,
    target.credentials.webDavPassword,
  );
  const server = trackServer(http.createServer((req, res) => {
    attemptCount += 1;
    if (attemptCount > MAX_RELAY_ATTEMPTS_PER_TEST) {
      overflowed = true;
      req.destroy();
      res.destroy();
      return;
    }
    const parsed = new URL(req.url ?? "/", target.origin);
    hits.push(Object.freeze({
      method: req.method ?? "",
      pathname: parsed.pathname,
      apiKey: classifyApiKey(req.headers["x-api-key"], target),
      authorization: classifyAuthorization(req.headers.authorization, validBasic),
      hasApiKeyQuery: parsed.searchParams.has("apikey"),
      hasDownloadKeyQuery: parsed.searchParams.has("downloadKey"),
      hasRange: req.headers.range !== undefined,
    }));

    relayHttpRequestBounded(req, res, targetUrl.origin);
  }));
  return Object.freeze({
    hits,
    origin: await listen(server),
    server,
    reset() {
      hits.length = 0;
      attemptCount = 0;
      overflowed = false;
    },
    assertHealthy() {
      if (overflowed || attemptCount !== hits.length) {
        throw new Error("ASP.NET relay capture overflowed.");
      }
    },
  });
}

function trackServer(server: Server): Server {
  ownedServers.push(server);
  return server;
}

function classifyApiKey(
  value: string | string[] | undefined,
  target: DisposableAspNetBackend,
): SecretClass {
  if (value === undefined) return "missing";
  if (Array.isArray(value)) return "other";
  if (sameSecret(value, target.credentials.internalApiKey)) return "internal";
  if (sameSecret(value, target.credentials.publicApiKey)) return "public";
  return "other";
}

function classifyAuthorization(
  value: string | undefined,
  validBasic: string,
): SecretClass {
  if (value === undefined) return "missing";
  return sameSecret(value, validBasic) ? "valid-basic" : "other";
}

function sameSecret(left: string, right: string): boolean {
  const leftDigest = createHash("sha256").update(left).digest();
  const rightDigest = createHash("sha256").update(right).digest();
  return timingSafeEqual(leftDigest, rightDigest);
}

function signedDownloadKey(target: DisposableAspNetBackend): string {
  return createHash("sha256")
    .update(`README_${target.credentials.internalApiKey}`)
    .digest("hex");
}

function validBasicAuthorization(): string {
  return basicAuthorization(
    backend.credentials.webDavUsername,
    backend.credentials.webDavPassword,
  );
}

function basicAuthorization(username: string, password: string): string {
  return `Basic ${Buffer.from(`${username}:${password}`, "utf8").toString("base64")}`;
}

function formHeaders(
  overrides: OutgoingHttpHeaders = {},
): OutgoingHttpHeaders {
  return {
    "content-type": "application/x-www-form-urlencoded",
    ...overrides,
  };
}

function formBody(entries: readonly (readonly [string, string])[]): string {
  const form = new URLSearchParams();
  for (const [name, value] of entries) form.append(name, value);
  return form.toString();
}

function requestFrontend(
  base: string,
  path: string,
  options: RequestOptions = {},
): Promise<BoundedHttpResponse> {
  const fixture = frontends.find((candidate) => candidate.base === base);
  if (!fixture) throw new Error("Disposable frontend fixture is unavailable.");
  return requestLoopbackBounded(fixture.origin, `${base}${path}`, options);
}

function listen(server: Server): Promise<string> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("Disposable server did not bind a loopback port."));
        return;
      }
      resolve(`http://127.0.0.1:${address.port}`);
    });
  });
}

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}
