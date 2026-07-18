import type { IncomingMessage } from "node:http";
import { BlockList, isIP } from "node:net";
import { createCookieSessionStorage } from "react-router";
import { backendClient } from "~/clients/backend-client.server";

type AuthMode = "local" | "authentik-proxy";

type User = {
  username: string;
};

type AuthentikConfiguration = {
  appSlug: string;
  trustedProxies: BlockList;
};

const sessionKeyPattern = /^[0-9a-fA-F]{64}$/;
const oneYear = 60 * 60 * 24 * 365;

function readAuthMode(): AuthMode {
  const configuredMode = process.env.AUTH_MODE ?? "local";
  if (configuredMode === "local" || configuredMode === "authentik-proxy") {
    return configuredMode;
  }

  throw new Error("AUTH_MODE must be either local or authentik-proxy.");
}

function requireSessionKey(name: "SESSION_KEY" | "SESSION_KEY_PREVIOUS"): string {
  const value = process.env[name];
  if (!value || !sessionKeyPattern.test(value)) {
    throw new Error(`${name} must be exactly 64 hexadecimal characters.`);
  }
  return value;
}

function readSecureCookiePolicy(): boolean {
  const configuredValue = process.env.SECURE_COOKIES;
  if (configuredValue === undefined || configuredValue === "true") return true;
  if (configuredValue !== "false") {
    throw new Error("SECURE_COOKIES must be either true or false.");
  }
  if (process.env.ALLOW_INSECURE_COOKIES !== "true") {
    throw new Error(
      "SECURE_COOKIES=false requires ALLOW_INSECURE_COOKIES=true for explicit local development.",
    );
  }
  return false;
}

function readAuthentikConfiguration(): AuthentikConfiguration {
  const appSlug = process.env.AUTHENTIK_APP_SLUG;
  if (!appSlug || appSlug !== appSlug.trim()) {
    throw new Error("AUTHENTIK_APP_SLUG is required in authentik-proxy mode.");
  }

  const configuredCidrs = process.env.AUTHENTIK_TRUSTED_PROXY_CIDRS;
  if (!configuredCidrs) {
    throw new Error("AUTHENTIK_TRUSTED_PROXY_CIDRS is required in authentik-proxy mode.");
  }

  const trustedProxies = new BlockList();
  const cidrs = configuredCidrs.split(",").map((value) => value.trim());
  if (cidrs.some((value) => value.length === 0)) {
    throw new Error("AUTHENTIK_TRUSTED_PROXY_CIDRS contains an empty CIDR entry.");
  }

  for (const cidr of cidrs) {
    const separator = cidr.lastIndexOf("/");
    const address = separator < 0 ? "" : cidr.slice(0, separator);
    const prefixText = separator < 0 ? "" : cidr.slice(separator + 1);
    const version = isIP(address);
    const prefix = Number(prefixText);
    const maxPrefix = version === 4 ? 32 : version === 6 ? 128 : -1;
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > maxPrefix) {
      throw new Error(`AUTHENTIK_TRUSTED_PROXY_CIDRS contains invalid CIDR: ${cidr}`);
    }

    trustedProxies.addSubnet(address, prefix, version === 4 ? "ipv4" : "ipv6");
  }

  return { appSlug, trustedProxies };
}

function normalizeRemoteAddress(address: string | undefined): string | undefined {
  if (!address) return undefined;
  const mappedIpv4Prefix = "::ffff:";
  if (address.toLowerCase().startsWith(mappedIpv4Prefix)) {
    const mappedAddress = address.slice(mappedIpv4Prefix.length);
    if (isIP(mappedAddress) === 4) return mappedAddress;
  }
  return address;
}

function isTrustedProxy(request: IncomingMessage, configuration: AuthentikConfiguration): boolean {
  const address = normalizeRemoteAddress(request.socket.remoteAddress);
  if (!address) return false;
  const version = isIP(address);
  if (version === 4) return configuration.trustedProxies.check(address, "ipv4");
  if (version === 6) return configuration.trustedProxies.check(address, "ipv6");
  return false;
}

function readAuthentikHeader(request: IncomingMessage, name: string): string | undefined {
  const value = request.headers[name];
  if (typeof value !== "string" || value.length === 0 || value.length > 256) return undefined;
  if (value.includes(",") || value !== value.trim() || /[\u0000-\u001f\u007f]/u.test(value)) {
    return undefined;
  }
  return value;
}

function isAuthentikIdentityValid(
  request: IncomingMessage,
  configuration: AuthentikConfiguration,
): boolean {
  if (!isTrustedProxy(request, configuration)) return false;
  const username = readAuthentikHeader(request, "x-authentik-username");
  const uid = readAuthentikHeader(request, "x-authentik-uid");
  const application = readAuthentikHeader(request, "x-authentik-meta-app");
  return Boolean(username && uid && application === configuration.appSlug);
}

if (process.env.DISABLE_FRONTEND_AUTH !== undefined) {
  throw new Error("DISABLE_FRONTEND_AUTH is not supported; select behavior with AUTH_MODE.");
}

export const AUTH_MODE = readAuthMode();
export const IS_FRONTEND_AUTH_DISABLED = AUTH_MODE === "authentik-proxy";

const authentikConfiguration = AUTH_MODE === "authentik-proxy"
  ? readAuthentikConfiguration()
  : undefined;

const sessionStorage = AUTH_MODE === "local"
  ? (() => {
      const currentKey = requireSessionKey("SESSION_KEY");
      const previousKey = process.env.SESSION_KEY_PREVIOUS === undefined
        ? undefined
        : requireSessionKey("SESSION_KEY_PREVIOUS");
      if (previousKey === currentKey) {
        throw new Error("SESSION_KEY_PREVIOUS must be different from SESSION_KEY.");
      }

      return createCookieSessionStorage({
        cookie: {
          name: "__session",
          httpOnly: true,
          path: "/",
          sameSite: "strict",
          secrets: previousKey ? [currentKey, previousKey] : [currentKey],
          secure: readSecureCookiePolicy(),
          maxAge: oneYear,
        },
      });
    })()
  : undefined;

function requireLocalSessionStorage() {
  if (!sessionStorage) {
    throw new Error("Local session operations are disabled in authentik-proxy mode.");
  }
  return sessionStorage;
}

function isUser(value: unknown): value is User {
  if (!value || typeof value !== "object") return false;
  const username = (value as { username?: unknown }).username;
  return typeof username === "string" && username.length > 0;
}

export async function isAuthenticated(request: Request | IncomingMessage): Promise<boolean> {
  if (AUTH_MODE === "authentik-proxy") {
    return !(request instanceof Request)
      && isAuthentikIdentityValid(request, authentikConfiguration!);
  }

  const cookieHeader = request instanceof Request
    ? request.headers.get("cookie")
    : request.headers.cookie;
  if (!cookieHeader) return false;

  try {
    const session = await requireLocalSessionStorage().getSession(cookieHeader);
    return isUser(session.get("user"));
  } catch {
    return false;
  }
}

export async function login(request: Request): Promise<ResponseInit> {
  const storage = requireLocalSessionStorage();
  const user = await authenticate(request);
  const session = await storage.getSession(request.headers.get("cookie"));
  session.set("user", user);
  return { headers: { "Set-Cookie": await storage.commitSession(session) } };
}

export async function logout(request: Request): Promise<ResponseInit> {
  const storage = requireLocalSessionStorage();
  const session = await storage.getSession(request.headers.get("cookie"));
  session.unset("user");
  return { headers: { "Set-Cookie": await storage.commitSession(session) } };
}

export async function setSessionUser(request: Request, username: string): Promise<ResponseInit> {
  const storage = requireLocalSessionStorage();
  const session = await storage.getSession(request.headers.get("cookie"));
  session.set("user", { username });
  return { headers: { "Set-Cookie": await storage.commitSession(session) } };
}

async function authenticate(request: Request): Promise<User> {
  const formData = await request.formData();
  const username = formData.get("username")?.toString();
  const password = formData.get("password")?.toString();
  if (!username || !password) throw new Error("username and password required");
  if (await backendClient.authenticate(username, password)) return { username };
  throw new Error("Invalid credentials");
}