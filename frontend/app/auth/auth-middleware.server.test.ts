import type express from "express";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const authenticationEnvironmentKeys = [
  "AUTH_MODE",
  "AUTHENTIK_APP_SLUG",
  "AUTHENTIK_TRUSTED_PROXY_CIDRS",
  "ALLOW_INSECURE_COOKIES",
  "DISABLE_FRONTEND_AUTH",
  "SECURE_COOKIES",
  "SESSION_KEY",
  "SESSION_KEY_PREVIOUS",
  "URL_BASE",
] as const;

const originalEnvironment = new Map(
  authenticationEnvironmentKeys.map((key) => [key, process.env[key]]),
);
const localSessionKey = "0123456789abcdef".repeat(4);

function restoreEnvironment() {
  for (const key of authenticationEnvironmentKeys) {
    const value = originalEnvironment.get(key);
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
}

function setAuthentikEnvironment() {
  process.env.AUTH_MODE = "authentik-proxy";
  process.env.AUTHENTIK_APP_SLUG = "nzbdav";
  process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS = "10.42.0.8/32";
}

function setLocalEnvironment() {
  process.env.AUTH_MODE = "local";
  process.env.SESSION_KEY = localSessionKey;
  process.env.SECURE_COOKIES = "true";
}

function authentikRequest(path: string): express.Request {
  return {
    headers: {
      "x-authentik-username": "suhail",
      "x-authentik-uid": "synthetic-authentik-user-id",
      "x-authentik-meta-app": "nzbdav",
    },
    method: "GET",
    path,
    socket: { remoteAddress: "10.42.0.8" },
  } as unknown as express.Request;
}

function responseDouble(): express.Response {
  return {
    redirect: vi.fn(),
    sendStatus: vi.fn(),
  } as unknown as express.Response;
}

describe("authentication middleware mode boundary", () => {
  beforeEach(() => {
    vi.resetModules();
    for (const key of authenticationEnvironmentKeys) delete process.env[key];
  });

  afterEach(() => {
    restoreEnvironment();
    vi.resetModules();
  });

  it.each(["/login", "/login.data", "/onboarding", "/onboarding.data", "/logout"])(
    "disables the local auth endpoint %s in Authentik proxy mode",
    async (path) => {
      setAuthentikEnvironment();
      const { authMiddleware } = await import("./auth-middleware.server");
      const request = authentikRequest(path);
      const response = responseDouble();
      const next = vi.fn();

      await authMiddleware(request, response, next);

      expect(response.sendStatus).toHaveBeenCalledWith(404);
      expect(response.redirect).not.toHaveBeenCalled();
      expect(next).not.toHaveBeenCalled();
    },
  );

  it("fails closed without redirecting to local login when Authentik identity is absent", async () => {
    setAuthentikEnvironment();
    const { authMiddleware } = await import("./auth-middleware.server");
    const request = authentikRequest("/queue");
    request.headers = {};
    const response = responseDouble();
    const next = vi.fn();

    await authMiddleware(request, response, next);

    expect(response.sendStatus).toHaveBeenCalledWith(401);
    expect(response.redirect).not.toHaveBeenCalled();
    expect(next).not.toHaveBeenCalled();
  });

  it("allows a valid Authentik identity to reach a protected UI route", async () => {
    setAuthentikEnvironment();
    const { authMiddleware } = await import("./auth-middleware.server");
    const response = responseDouble();
    const next = vi.fn();

    await authMiddleware(authentikRequest("/queue"), response, next);

    expect(next).toHaveBeenCalledOnce();
    expect(response.sendStatus).not.toHaveBeenCalled();
    expect(response.redirect).not.toHaveBeenCalled();
  });

  it("preserves public login and protected-route redirects in local mode", async () => {
    setLocalEnvironment();
    process.env.URL_BASE = "/nzbdav/";
    const { authMiddleware } = await import("./auth-middleware.server");

    const publicResponse = responseDouble();
    const publicNext = vi.fn();
    await authMiddleware(authentikRequest("/login"), publicResponse, publicNext);
    expect(publicNext).toHaveBeenCalledOnce();

    const protectedResponse = responseDouble();
    const protectedNext = vi.fn();
    const protectedRequest = authentikRequest("/queue");
    protectedRequest.headers = {};
    await authMiddleware(protectedRequest, protectedResponse, protectedNext);
    expect(protectedResponse.redirect).toHaveBeenCalledWith(302, "/nzbdav/login");
    expect(protectedResponse.sendStatus).not.toHaveBeenCalled();
    expect(protectedNext).not.toHaveBeenCalled();
  });
});
