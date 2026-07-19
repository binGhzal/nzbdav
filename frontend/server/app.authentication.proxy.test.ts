/** @vitest-environment node */

import http, { type IncomingHttpHeaders, type Server } from "node:http";
import express from "express";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  requestLoopbackBounded,
} from "./test-support/bounded-http";

vi.setConfig({ testTimeout: 15_000 });

vi.mock("./react-router-handler", () => ({
  createPinrailRequestHandler: () => (
    _req: express.Request,
    res: express.Response,
  ) => res.status(418).type("text/plain").send("frontend"),
}));

type Capture = {
  method: string;
  url: string;
  headers: IncomingHttpHeaders;
};

type AuthFixture = {
  origin: string;
  server: Server;
  cookie?: string;
};

type RawResponse = {
  status: number;
  headers: IncomingHttpHeaders;
  body: string;
};

const environmentNames = [
  "ALLOW_INSECURE_COOKIES",
  "AUTHENTIK_APP_SLUG",
  "AUTHENTIK_TRUSTED_PROXY_CIDRS",
  "AUTH_MODE",
  "BACKEND_URL",
  "DISABLE_FRONTEND_AUTH",
  "FRONTEND_BACKEND_API_KEY",
  "SECURE_COOKIES",
  "SESSION_KEY",
  "SESSION_KEY_PREVIOUS",
  "URL_BASE",
] as const;
const originalEnvironment = new Map<string, string | undefined>();
const MAX_BACKEND_ATTEMPTS_PER_TEST = 8;
const captures: Capture[] = [];
const ownedServers: Server[] = [];
const maximumAuthentikAppSlug = "a".repeat(256);
let backendServer: Server;
let backendOrigin: string;
let trustedAuthentik: AuthFixture;
let untrustedAuthentik: AuthFixture;
let local: AuthFixture;
let backendAttemptCount = 0;
let backendCaptureOverflow = false;

beforeAll(async () => {
  for (const name of environmentNames) originalEnvironment.set(name, process.env[name]);
  backendServer = trackServer(http.createServer((request, response) => {
    backendAttemptCount += 1;
    if (backendAttemptCount > MAX_BACKEND_ATTEMPTS_PER_TEST) {
      backendCaptureOverflow = true;
      request.destroy();
      response.destroy();
      return;
    }
    captures.push({
      method: request.method ?? "",
      url: request.url ?? "",
      headers: request.headers,
    });
    request.resume();
    response.statusCode = 204;
    response.end();
  }));
  backendOrigin = await listen(backendServer);

  trustedAuthentik = await createAuthentikFixture("127.0.0.1/32");
  untrustedAuthentik = await createAuthentikFixture("10.42.0.8/32");
  local = await createLocalFixture();
});

afterAll(async () => {
  const failures: Error[] = [];
  try {
    const results = await Promise.allSettled(
      ownedServers.splice(0).map((server) => close(server)),
    );
    for (const result of results) {
      if (result.status === "rejected") {
        failures.push(new Error("Authentication fixture server cleanup failed."));
      }
    }
  } finally {
    for (const [name, value] of originalEnvironment) restoreEnvironment(name, value);
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Authentication fixture cleanup failed.");
  }
});

beforeEach(() => {
  captures.length = 0;
  backendAttemptCount = 0;
  backendCaptureOverflow = false;
});

afterEach(async () => {
  await new Promise<void>((resolve) => setImmediate(resolve));
  if (backendCaptureOverflow || backendAttemptCount !== captures.length) {
    throw new Error("Authentication proxy capture overflowed.");
  }
});

describe("real frontend principal decisions at the proxy boundary", () => {
  it("accepts the exact 256-character Authentik application boundary from the actual trusted socket", async () => {
    const response = await requestAdmin(trustedAuthentik, authentikHeaders());

    expect(response.status).toBe(204);
    expect(captures).toHaveLength(1);
    expect(captures[0]).toMatchObject({ method: "POST", url: "/api/test-arr-connection" });
    expect(captures[0].headers["x-api-key"]).toBe("unit");
  });

  it("accepts the inclusive 256-character Authentik identity boundary", async () => {
    const response = await requestAdmin(trustedAuthentik, authentikHeaders({
      "x-authentik-uid": "u".repeat(256),
      "x-authentik-username": "n".repeat(256),
    }));

    expect(response.status).toBe(204);
    expect(captures).toHaveLength(1);
    expect(captures[0].headers["x-api-key"]).toBe("unit");
  });

  it("does not trust spoofed forwarding headers from an untrusted socket", async () => {
    const response = await requestAdmin(untrustedAuthentik, authentikHeaders({
      Forwarded: "for=10.42.0.8",
      "X-Forwarded-For": "10.42.0.8",
      "x-api-key": "pub",
    }));

    expect(response.status).toBe(401);
    expect(captures).toHaveLength(0);
  });

  it.each(["trusted", "untrusted"] as const)(
    "preserves independent WebDAV authority without an Authentik principal on a %s socket",
    async (socketKind) => {
      const fixture = socketKind === "trusted" ? trustedAuthentik : untrustedAuthentik;
      const response = await requestProtocol(fixture, authentikHeaders({
        ...(socketKind === "trusted" ? { "x-authentik-meta-app": "other" } : {}),
        Authorization: "Basic fixture",
        Cookie: "__session=client",
        Forwarded: "for=10.42.0.8",
        "X-Forwarded-For": "10.42.0.8",
        "x-api-key": "pub",
      }));

      expect(response.status).toBe(204);
      expect(captures).toHaveLength(1);
      expect(captures[0]).toMatchObject({ method: "GET", url: "/README" });
      expect(captures[0].headers.authorization).toBe("Basic fixture");
      expect(captures[0].headers["x-api-key"]).toBeUndefined();
      expect(captures[0].headers.cookie).toBeUndefined();
      expect(captures[0].headers.forwarded).toBeUndefined();
      expect(captures[0].headers["x-forwarded-for"]).toBeUndefined();
      expect(captures[0].headers["x-authentik-meta-app"]).toBeUndefined();
      expect(captures[0].headers["x-authentik-uid"]).toBeUndefined();
      expect(captures[0].headers["x-authentik-username"]).toBeUndefined();
      expect(JSON.stringify(captures[0])).not.toContain("unit");
    },
  );

  it.each([
    ["missing application", { "x-authentik-meta-app": undefined }],
    ["empty application", { "x-authentik-meta-app": "" }],
    ["wrong application", { "x-authentik-meta-app": "other" }],
    ["case-changed application", { "x-authentik-meta-app": maximumAuthentikAppSlug.toUpperCase() }],
    ["missing username", { "x-authentik-username": undefined }],
    ["empty username", { "x-authentik-username": "" }],
    ["missing UID", { "x-authentik-uid": undefined }],
    ["empty UID", { "x-authentik-uid": "" }],
    ["comma username", { "x-authentik-username": "user,other" }],
    ["comma UID", { "x-authentik-uid": "uid,other" }],
    ["comma application", { "x-authentik-meta-app": "nzbdav,other" }],
    ["oversized username", { "x-authentik-username": "n".repeat(257) }],
    ["oversized UID", { "x-authentik-uid": "u".repeat(257) }],
    ["oversized application", { "x-authentik-meta-app": "a".repeat(257) }],
  ] as const)("rejects %s before upstream", async (_name, overrides) => {
    const response = await requestAdmin(trustedAuthentik, authentikHeaders({
      ...overrides,
      "x-api-key": "pub",
    }));

    expect(response.status).toBe(401);
    expect(captures).toHaveLength(0);
  });

  it.each([
    ["username", "x-authentik-username"],
    ["UID", "x-authentik-uid"],
    ["application", "x-authentik-meta-app"],
  ] as const)("rejects repeated Authentik %s headers", async (_name, headerName) => {
    const headers = authentikHeaders({ "x-api-key": "pub" });
    headers[headerName] = headerName === "x-authentik-meta-app"
      ? [maximumAuthentikAppSlug, maximumAuthentikAppSlug]
      : ["fixture", "fixture"];
    const response = await requestAdmin(trustedAuthentik, headers);

    expect(response.status).toBe(401);
    expect(captures).toHaveLength(0);
  });

  it("accepts a real signed local session and replaces a client key", async () => {
    const response = await requestAdmin(local, {
      Cookie: local.cookie!,
      "x-api-key": "pub",
    });

    expect(response.status).toBe(204);
    expect(captures).toHaveLength(1);
    expect(captures[0].headers["x-api-key"]).toBe("unit");
    expect(captures[0].headers.cookie).toBeUndefined();
  });

  it.each([
    ["missing local session", undefined],
    ["malformed local session", "__session=malformed"],
  ])("rejects a %s before upstream", async (_name, cookie) => {
    const response = await requestAdmin(local, {
      ...(cookie ? { Cookie: cookie } : {}),
      "x-api-key": "pub",
    });

    expect(response.status).toBe(401);
    expect(captures).toHaveLength(0);
  });
});

async function createAuthentikFixture(trustedCidrs: string): Promise<AuthFixture> {
  configureSharedEnvironment();
  process.env.AUTH_MODE = "authentik-proxy";
  process.env.AUTHENTIK_APP_SLUG = maximumAuthentikAppSlug;
  process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS = trustedCidrs;
  delete process.env.SESSION_KEY;
  delete process.env.SESSION_KEY_PREVIOUS;
  delete process.env.SECURE_COOKIES;
  delete process.env.ALLOW_INSECURE_COOKIES;
  vi.resetModules();
  const { app } = await import("./app");
  return startFrontend(app);
}

async function createLocalFixture(): Promise<AuthFixture> {
  configureSharedEnvironment();
  process.env.AUTH_MODE = "local";
  process.env.SESSION_KEY = "a".repeat(64);
  process.env.SECURE_COOKIES = "false";
  process.env.ALLOW_INSECURE_COOKIES = "true";
  delete process.env.SESSION_KEY_PREVIOUS;
  delete process.env.AUTHENTIK_APP_SLUG;
  delete process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS;
  vi.resetModules();
  const authentication = await import("~/auth/authentication.server");
  const session = await authentication.setSessionUser(
    new Request("http://127.0.0.1/nzbdav/"),
    "fixture-user",
  );
  const cookie = new Headers(session.headers).get("set-cookie")?.split(";", 1)[0];
  if (!cookie) throw new Error("Local fixture did not create a signed session cookie");
  const { app } = await import("./app");
  return { ...await startFrontend(app), cookie };
}

function configureSharedEnvironment(): void {
  process.env.BACKEND_URL = backendOrigin;
  process.env.FRONTEND_BACKEND_API_KEY = "unit";
  process.env.URL_BASE = "/nzbdav";
  delete process.env.DISABLE_FRONTEND_AUTH;
}

async function startFrontend(app: express.Express): Promise<AuthFixture> {
  const parent = express();
  parent.disable("x-powered-by");
  parent.use("/nzbdav", app);
  const server = trackServer(http.createServer(parent));
  return { origin: await listen(server), server };
}

function trackServer(server: Server): Server {
  ownedServers.push(server);
  return server;
}

function authentikHeaders(
  overrides: Record<string, string | string[] | undefined> = {},
): http.OutgoingHttpHeaders {
  const headers: Record<string, string | string[] | undefined> = {
    "x-authentik-meta-app": maximumAuthentikAppSlug,
    "x-authentik-uid": "uid",
    "x-authentik-username": "user",
    ...overrides,
  };
  for (const [name, value] of Object.entries(headers)) {
    if (value === undefined) delete headers[name];
  }
  return headers;
}

function requestAdmin(
  fixture: AuthFixture,
  headers: http.OutgoingHttpHeaders,
): Promise<RawResponse> {
  return requestFixture(
    fixture,
    "/nzbdav/api/test-arr-connection",
    "POST",
    headers,
  );
}

function requestProtocol(
  fixture: AuthFixture,
  headers: http.OutgoingHttpHeaders,
): Promise<RawResponse> {
  return requestFixture(fixture, "/nzbdav/protocol/README", "GET", headers);
}

async function requestFixture(
  fixture: AuthFixture,
  path: string,
  method: string,
  headers: http.OutgoingHttpHeaders,
): Promise<RawResponse> {
  const response = await requestLoopbackBounded(
    fixture.origin,
    path,
    { method, headers },
    { timeoutMs: 5_000, maxResponseBytes: 64 * 1024 },
  );
  return {
    status: response.status,
    headers: response.headers,
    body: response.body.toString("utf8"),
  };
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

function close(server: Server): Promise<void> {
  return closeHttpServerBounded(server, 5_000);
}

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}
