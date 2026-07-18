import type { IncomingMessage } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const currentKey = "1".repeat(64);
const nextKey = "2".repeat(64);
const authenticationEnvironmentKeys = [
  "AUTH_MODE",
  "AUTHENTIK_APP_SLUG",
  "AUTHENTIK_TRUSTED_PROXY_CIDRS",
  "ALLOW_INSECURE_COOKIES",
  "DISABLE_FRONTEND_AUTH",
  "SECURE_COOKIES",
  "SESSION_KEY",
  "SESSION_KEY_PREVIOUS",
] as const;

const originalEnvironment = new Map(
  authenticationEnvironmentKeys.map((key) => [key, process.env[key]]),
);

function restoreEnvironment() {
  for (const key of authenticationEnvironmentKeys) {
    const value = originalEnvironment.get(key);
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
}

function setLocalEnvironment(key = currentKey) {
  process.env.AUTH_MODE = "local";
  process.env.SESSION_KEY = key;
  process.env.SECURE_COOKIES = "true";
}

function setAuthentikEnvironment() {
  process.env.AUTH_MODE = "authentik-proxy";
  process.env.AUTHENTIK_APP_SLUG = "nzbdav";
  process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS = "10.42.0.8/32,2001:db8:42::/64";
}

function cookieFrom(responseInit: ResponseInit): string {
  const setCookie = new Headers(responseInit.headers).get("Set-Cookie");
  expect(setCookie).toBeTruthy();
  return setCookie!.split(";", 1)[0];
}

function incomingRequest(
  remoteAddress: string,
  headers: IncomingMessage["headers"] = {},
): IncomingMessage {
  return {
    headers,
    socket: { remoteAddress },
  } as unknown as IncomingMessage;
}

describe("frontend authentication startup contract", () => {
  beforeEach(() => {
    vi.resetModules();
    for (const key of authenticationEnvironmentKeys) delete process.env[key];
  });

  afterEach(() => {
    restoreEnvironment();
    vi.resetModules();
  });

  it("defaults to local mode and rejects a missing session key", async () => {
    await expect(import("./authentication.server"))
      .rejects.toThrow(/SESSION_KEY.*64 hexadecimal/i);
  });

  it.each([
    "short",
    "g".repeat(64),
    "replace-with-random-hex",
    currentKey.slice(0, 63),
    `${currentKey}00`,
  ])("rejects malformed local session key material: %s", async (key) => {
    process.env.AUTH_MODE = "local";
    process.env.SESSION_KEY = key;
    process.env.SECURE_COOKIES = "true";

    await expect(import("./authentication.server"))
      .rejects.toThrow(/SESSION_KEY.*64 hexadecimal/i);
  });

  it("rejects unsupported authentication modes instead of downgrading to local", async () => {
    process.env.AUTH_MODE = "auto";
    process.env.SESSION_KEY = currentKey;
    process.env.SECURE_COOKIES = "true";

    await expect(import("./authentication.server"))
      .rejects.toThrow(/AUTH_MODE.*local.*authentik-proxy/i);
  });

  it("uses secure cookies by default and preserves a local session across restart", async () => {
    process.env.SESSION_KEY = currentKey;

    const firstRuntime = await import("./authentication.server");
    const responseInit = await firstRuntime.setSessionUser(
      new Request("https://nzbdav.example.test/"),
      "admin",
    );
    const setCookie = new Headers(responseInit.headers).get("Set-Cookie")!;
    expect(setCookie).toMatch(/; Secure(?:;|$)/i);
    const cookie = cookieFrom(responseInit);

    vi.resetModules();
    const restartedRuntime = await import("./authentication.server");
    expect(await restartedRuntime.isAuthenticated(
      new Request("https://nzbdav.example.test/", {
        headers: { Cookie: cookie },
      }),
    )).toBe(true);
  });

  it("requires an explicit insecure-development opt-in for non-secure cookies", async () => {
    process.env.SESSION_KEY = currentKey;
    process.env.SECURE_COOKIES = "false";

    await expect(import("./authentication.server"))
      .rejects.toThrow(/ALLOW_INSECURE_COOKIES/i);

    vi.resetModules();
    process.env.ALLOW_INSECURE_COOKIES = "true";
    const runtime = await import("./authentication.server");
    const responseInit = await runtime.setSessionUser(
      new Request("http://127.0.0.1:3000/"),
      "developer",
    );
    expect(new Headers(responseInit.headers).get("Set-Cookie"))
      .not.toMatch(/; Secure(?:;|$)/i);
  });

  it("supports exactly one previous key for controlled rotation", async () => {
    setLocalEnvironment(currentKey);
    const oldRuntime = await import("./authentication.server");
    const oldCookie = cookieFrom(await oldRuntime.setSessionUser(
      new Request("https://nzbdav.example.test/"),
      "admin",
    ));

    vi.resetModules();
    setLocalEnvironment(nextKey);
    process.env.SESSION_KEY_PREVIOUS = currentKey;
    const rotatingRuntime = await import("./authentication.server");
    expect(await rotatingRuntime.isAuthenticated(
      new Request("https://nzbdav.example.test/", {
        headers: { Cookie: oldCookie },
      }),
    )).toBe(true);

    vi.resetModules();
    delete process.env.SESSION_KEY_PREVIOUS;
    const rotatedRuntime = await import("./authentication.server");
    expect(await rotatedRuntime.isAuthenticated(
      new Request("https://nzbdav.example.test/", {
        headers: { Cookie: oldCookie },
      }),
    )).toBe(false);
  });

  it("rejects malformed or redundant previous-key configuration", async () => {
    setLocalEnvironment(currentKey);
    process.env.SESSION_KEY_PREVIOUS = "not-hex";
    await expect(import("./authentication.server"))
      .rejects.toThrow(/SESSION_KEY_PREVIOUS.*64 hexadecimal/i);

    vi.resetModules();
    setLocalEnvironment(currentKey);
    process.env.SESSION_KEY_PREVIOUS = currentKey;
    await expect(import("./authentication.server"))
      .rejects.toThrow(/SESSION_KEY_PREVIOUS.*different/i);
  });

  it("rejects malformed local cookies without throwing or authenticating", async () => {
    setLocalEnvironment();
    const runtime = await import("./authentication.server");

    await expect(runtime.isAuthenticated(
      new Request("https://nzbdav.example.test/", {
        headers: { Cookie: "__session=not-a-valid-signed-session" },
      }),
    )).resolves.toBe(false);
  });

  it("requires complete Authentik proxy startup configuration", async () => {
    process.env.AUTH_MODE = "authentik-proxy";
    await expect(import("./authentication.server"))
      .rejects.toThrow(/AUTHENTIK_APP_SLUG/i);

    vi.resetModules();
    process.env.AUTHENTIK_APP_SLUG = "nzbdav";
    await expect(import("./authentication.server"))
      .rejects.toThrow(/AUTHENTIK_TRUSTED_PROXY_CIDRS/i);
  });

  it("accepts only the expected Authentik application from a trusted outpost source", async () => {
    setAuthentikEnvironment();
    const runtime = await import("./authentication.server");
    const validHeaders = {
      "x-authentik-username": "suhail",
      "x-authentik-uid": "synthetic-authentik-user-id",
      "x-authentik-meta-app": "nzbdav",
    };

    await expect(runtime.isAuthenticated(incomingRequest("10.42.0.8", validHeaders)))
      .resolves.toBe(true);
    await expect(runtime.isAuthenticated(incomingRequest("2001:db8:42::9", validHeaders)))
      .resolves.toBe(true);
    await expect(runtime.isAuthenticated(incomingRequest("10.42.0.9", validHeaders)))
      .resolves.toBe(false);
    await expect(runtime.isAuthenticated(incomingRequest("10.42.0.8", {
      ...validHeaders,
      "x-authentik-meta-app": "another-app",
    }))).resolves.toBe(false);
    await expect(runtime.isAuthenticated(incomingRequest("10.42.0.8", {
      ...validHeaders,
      "x-authentik-username": "",
    }))).resolves.toBe(false);
  });

  it("rejects conflicting Authentik identity headers from an otherwise trusted outpost", async () => {
    setAuthentikEnvironment();
    const runtime = await import("./authentication.server");

    await expect(runtime.isAuthenticated(incomingRequest("10.42.0.8", {
      "x-authentik-username": "suhail, attacker",
      "x-authentik-uid": "synthetic-authentik-user-id",
      "x-authentik-meta-app": "nzbdav",
    }))).resolves.toBe(false);
  });

  it("disables local sessions in Authentik mode and rejects the legacy auth-disable switch", async () => {
    setAuthentikEnvironment();
    process.env.SESSION_KEY = currentKey;
    const runtime = await import("./authentication.server");

    await expect(runtime.isAuthenticated(new Request("https://nzbdav.example.test/")))
      .resolves.toBe(false);
    await expect(runtime.setSessionUser(
      new Request("https://nzbdav.example.test/"),
      "admin",
    )).rejects.toThrow(/local session.*disabled.*authentik-proxy/i);

    vi.resetModules();
    setAuthentikEnvironment();
    process.env.DISABLE_FRONTEND_AUTH = "true";
    await expect(import("./authentication.server"))
      .rejects.toThrow(/DISABLE_FRONTEND_AUTH.*AUTH_MODE/i);
  });
});
