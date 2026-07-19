/** @vitest-environment node */

import { once } from "node:events";
import http, { type IncomingHttpHeaders, type Server } from "node:http";
import net from "node:net";
import { fileURLToPath } from "node:url";
import express from "express";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  requestLoopbackBounded,
} from "./test-support/bounded-http";

type HandshakeResponse = Readonly<{
  status: number;
  headers: IncomingHttpHeaders;
}>;

const harness = vi.hoisted(() => ({
  servers: [] as Server[],
  websocketConnections: 0,
}));
const serverEntrypointModule: string = "../server.ts";
const authenticationEnvironmentNames = [
  "ALLOW_INSECURE_COOKIES",
  "AUTHENTIK_APP_SLUG",
  "AUTHENTIK_TRUSTED_PROXY_CIDRS",
  "AUTH_MODE",
  "DISABLE_FRONTEND_AUTH",
  "SECURE_COOKIES",
  "SESSION_KEY",
  "SESSION_KEY_PREVIOUS",
] as const;
const originalAuthenticationEnvironment = new Map(
  authenticationEnvironmentNames.map((name) => [name, process.env[name]]),
);

vi.mock("http", async (importOriginal) => {
  const actual = await importOriginal<typeof import("node:http")>();
  const createServer: typeof actual.createServer = ((
    ...args: Parameters<typeof actual.createServer>
  ) => {
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
    ssrLoadModule: vi.fn(async () => await import("./app")),
  })),
}));

vi.mock("./react-router-handler", () => ({
  createPinrailRequestHandler: () => (
    _req: express.Request,
    res: express.Response,
  ) => res.status(418).type("text/plain").send("frontend"),
}));

vi.mock("./websocket.server", () => ({
  websocketServer: {
    initialize(server: { on: (event: string, listener: () => void) => void }) {
      server.on("connection", () => {
        harness.websocketConnections += 1;
      });
    },
  },
}));

beforeEach(() => {
  vi.resetModules();
  vi.stubEnv("NODE_ENV", "development");
  vi.stubEnv("PORT", "0");
  vi.stubEnv("LISTEN_ADDRESS", "127.0.0.1");
  vi.stubEnv("URL_BASE", "/nzbdav");
  vi.stubEnv("BACKEND_URL", "http://127.0.0.1:1");
  vi.stubEnv("FRONTEND_BACKEND_API_KEY", "unit");
  vi.stubEnv(
    "NZBDAV_ENV_FILE",
    fileURLToPath(new URL(".missing-auth-entrypoint-test-env", import.meta.url)),
  );
  harness.servers.length = 0;
  harness.websocketConnections = 0;
  vi.spyOn(console, "log").mockImplementation(() => undefined);
});

afterEach(async () => {
  const failures: Error[] = [];
  try {
    const results = await Promise.allSettled(
      harness.servers.splice(0).map((server) => closeHttpServerBounded(server, 5_000)),
    );
    for (const result of results) {
      if (result.status === "rejected") {
        failures.push(new Error("Authentication entrypoint server cleanup failed."));
      }
    }
  } finally {
    try {
      vi.unstubAllEnvs();
    } finally {
      for (const [name, value] of originalAuthenticationEnvironment) {
        restoreEnvironment(name, value);
      }
      vi.restoreAllMocks();
    }
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Authentication entrypoint cleanup failed.");
  }
});

describe.sequential("real authentication at the raw WebSocket upgrade boundary", () => {
  it("accepts an actual signed local session before 101", async () => {
    const cookie = await configureLocalAuthentication();
    const origin = await startAndPrime(cookie ? { Cookie: cookie } : {}, 418);

    const response = await handshake(origin, { Cookie: cookie });

    expect(response.status).toBe(101);
    expect(harness.websocketConnections).toBe(1);
  });

  it.each([
    ["missing", undefined],
    ["malformed", "__session=malformed"],
  ] as const)("rejects a %s local session before 101", async (_name, cookie) => {
    await configureLocalAuthentication();
    const origin = await startAndPrime({}, 302);

    const response = await handshake(origin, cookie ? { Cookie: cookie } : {});

    expect(response.status).toBe(401);
    expect(harness.websocketConnections).toBe(0);
  });

  it("accepts an actual trusted Authentik principal before 101", async () => {
    configureAuthentikAuthentication("127.0.0.1/32");
    const headers = authentikHeaders();
    const origin = await startAndPrime(headers, 418);

    const response = await handshake(origin, headers);

    expect(response.status).toBe(101);
    expect(harness.websocketConnections).toBe(1);
  });

  it.each([
    ["missing application", { "x-authentik-meta-app": undefined }],
    ["empty application", { "x-authentik-meta-app": "" }],
    ["wrong application", { "x-authentik-meta-app": "other" }],
    ["malformed username", { "x-authentik-username": "user,other" }],
    ["missing UID", { "x-authentik-uid": undefined }],
  ] as const)("rejects Authentik %s before 101", async (_name, overrides) => {
    configureAuthentikAuthentication("127.0.0.1/32");
    const origin = await startAndPrime({}, 401);

    const response = await handshake(origin, authentikHeaders(overrides));

    expect(response.status).toBe(401);
    expect(harness.websocketConnections).toBe(0);
  });

  it("rejects an otherwise valid Authentik identity from an untrusted socket", async () => {
    configureAuthentikAuthentication("10.42.0.8/32");
    const origin = await startAndPrime({}, 401);

    const response = await handshake(origin, authentikHeaders({
      Forwarded: "for=10.42.0.8",
      "X-Forwarded-For": "10.42.0.8",
    }));

    expect(response.status).toBe(401);
    expect(harness.websocketConnections).toBe(0);
  });
});

async function configureLocalAuthentication(): Promise<string> {
  vi.stubEnv("AUTH_MODE", "local");
  vi.stubEnv("SESSION_KEY", "b".repeat(64));
  vi.stubEnv("SECURE_COOKIES", "false");
  vi.stubEnv("ALLOW_INSECURE_COOKIES", "true");
  delete process.env.SESSION_KEY_PREVIOUS;
  delete process.env.AUTHENTIK_APP_SLUG;
  delete process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS;
  const authentication = await import("~/auth/authentication.server");
  const session = await authentication.setSessionUser(
    new Request("http://127.0.0.1/nzbdav/"),
    "fixture-user",
  );
  const cookie = new Headers(session.headers).get("set-cookie")?.split(";", 1)[0];
  if (!cookie) throw new Error("Local WebSocket fixture did not create a session");
  return cookie;
}

function configureAuthentikAuthentication(trustedCidrs: string): void {
  vi.stubEnv("AUTH_MODE", "authentik-proxy");
  vi.stubEnv("AUTHENTIK_APP_SLUG", "nzbdav");
  vi.stubEnv("AUTHENTIK_TRUSTED_PROXY_CIDRS", trustedCidrs);
  delete process.env.SESSION_KEY;
  delete process.env.SESSION_KEY_PREVIOUS;
  delete process.env.SECURE_COOKIES;
  delete process.env.ALLOW_INSECURE_COOKIES;
}

function authentikHeaders(
  overrides: Record<string, string | undefined> = {},
): http.OutgoingHttpHeaders {
  const headers: Record<string, string | undefined> = {
    "x-authentik-meta-app": "nzbdav",
    "x-authentik-uid": "uid",
    "x-authentik-username": "user",
    ...overrides,
  };
  for (const [name, value] of Object.entries(headers)) {
    if (value === undefined) delete headers[name];
  }
  return headers;
}

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

async function startAndPrime(
  headers: http.OutgoingHttpHeaders,
  expectedStatus: 302 | 401 | 418,
): Promise<string> {
  await import(serverEntrypointModule);
  const server = harness.servers.at(-1);
  if (!server) throw new Error("Authentication entrypoint fixture did not create a server");
  if (!server.listening) await once(server, "listening");
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Authentication entrypoint fixture did not bind loopback");
  }
  const origin = `http://127.0.0.1:${address.port}`;
  const prime = await requestLoopbackBounded(origin, "/nzbdav/__prime", { headers }, {
    timeoutMs: 5_000,
    maxResponseBytes: 64 * 1024,
  });
  expect(prime.status).toBe(expectedStatus);
  return origin;
}

function handshake(
  origin: string,
  headers: http.OutgoingHttpHeaders,
): Promise<HandshakeResponse> {
  const target = new URL(origin);
  const extraHeaders = Object.entries(headers).flatMap(([name, value]) => {
    if (value === undefined) return [];
    return (Array.isArray(value) ? value : [value]).map((entry) => [name, String(entry)] as const);
  });
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({
      host: target.hostname,
      port: Number(target.port),
    });
    let response = Buffer.alloc(0);
    let settled = false;
    const finish = (error?: Error, value?: HandshakeResponse) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      if (error) reject(error);
      else if (value) resolve(value);
      else reject(new Error("Authentication WebSocket handshake failed"));
    };
    const timeout = setTimeout(
      () => finish(new Error("Authentication WebSocket handshake timed out")),
      2_000,
    );
    socket.once("error", (error) => finish(error));
    socket.once("connect", () => {
      socket.write([
        "GET /nzbdav/ws HTTP/1.1",
        `Host: ${target.host}`,
        "Connection: Upgrade",
        "Upgrade: websocket",
        "Sec-WebSocket-Version: 13",
        `Sec-WebSocket-Key: ${Buffer.alloc(16, 2).toString("base64")}`,
        `Origin: http://${target.host}`,
        ...extraHeaders.map(([name, value]) => `${name}: ${value}`),
        "",
        "",
      ].join("\r\n"));
    });
    socket.on("data", (chunk) => {
      if (response.byteLength + chunk.byteLength > 64 * 1024) {
        finish(new Error("Authentication WebSocket response exceeded its bound"));
        return;
      }
      response = Buffer.concat([response, chunk]);
      const headerEnd = response.indexOf("\r\n\r\n");
      if (headerEnd < 0) return;
      const lines = response.subarray(0, headerEnd).toString("utf8").split("\r\n");
      const match = /^HTTP\/1\.1 (\d{3})\b/u.exec(lines.shift() ?? "");
      if (!match) {
        finish(new Error("Authentication WebSocket response had an invalid status"));
        return;
      }
      const responseHeaders: IncomingHttpHeaders = {};
      for (const line of lines) {
        const separator = line.indexOf(":");
        if (separator < 0) continue;
        responseHeaders[line.slice(0, separator).toLowerCase()] = line.slice(separator + 1).trim();
      }
      finish(undefined, { status: Number(match[1]), headers: responseHeaders });
    });
  });
}
