import type { IncomingMessage, ServerResponse } from "node:http";
import type { Socket } from "node:net";
import "react-router";
import express from "express";
import { createProxyMiddleware, proxyEventsPlugin } from "http-proxy-middleware";
import { authMiddleware } from "~/auth/auth-middleware.server";
import { isAuthenticated } from "~/auth/authentication.server";
import {
  createPublicFailureEnvelope,
  INTERNAL_REQUEST_CORRELATION_HEADER,
  type PublicFailureCode,
} from "../app/utils/public-failure";
import {
  classifyBackendRequest,
  validateFrontendActionBodyFraming,
  type BackendProxyLane,
  type BackendRequestDecision,
} from "./request-policy";
import { disableUnsafeRuntimeDebugOutput } from "./debug-output";
import {
  handleExpressTerminalFailure,
  sendExpressPublicFailure,
} from "./public-failure-response.js";
import { websocketServer } from "./websocket.server";
import { createPinrailRequestHandler } from "./react-router-handler";

declare module "react-router" {
  interface AppLoadContext {
    VALUE_FROM_EXPRESS: string;
  }
}

const MAX_API_KEY_LENGTH = 512;
const MAX_WEBDAV_HEADER_LENGTH = 8192;
const INTERNAL_WEBDAV_PATH_BASE_HEADER = "x-pinrail-webdav-path-base";
const DEFAULT_BACKEND_PROXY_TIMEOUT_MS = 30_000;
const MIN_BACKEND_PROXY_TIMEOUT_MS = 100;
const MAX_BACKEND_PROXY_TIMEOUT_MS = 300_000;
const backendOrigin = readBackendOrigin(process.env.BACKEND_URL);
const backendProxyTimeoutMs = readBackendProxyTimeout(
  process.env.BACKEND_PROXY_TIMEOUT_MS,
);
disableUnsafeRuntimeDebugOutput();
const browserAuthorityHeaders = [
  "authorization",
  "cookie",
  "forwarded",
  "origin",
  "proxy-authorization",
  "referer",
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
  INTERNAL_WEBDAV_PATH_BASE_HEADER,
  INTERNAL_REQUEST_CORRELATION_HEADER,
] as const;
const hopByHopHeaders = [
  "connection",
  "keep-alive",
  "proxy-connection",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
] as const;
const securitySensitiveConnectionOptions = new Set([
  "authorization",
  "content-length",
  "content-type",
  "destination",
  "host",
  "range",
  "x-api-key",
  INTERNAL_WEBDAV_PATH_BASE_HEADER,
  INTERNAL_REQUEST_CORRELATION_HEADER,
]);

export const app = express();
app.disable("x-powered-by");
export const authenticateWebsocketUpgrade = isAuthenticated;
export const initializeWebsocketServer = websocketServer.initialize;

const forwardToBackend = createProxyMiddleware({
  target: backendOrigin,
  changeOrigin: true,
  ejectPlugins: true,
  plugins: [proxyEventsPlugin],
  proxyTimeout: backendProxyTimeoutMs,
  on: {
    error: (_error, _req, response) => {
      if (isHttpResponse(response)) {
        if (response.headersSent) {
          response.destroy();
          return;
        }

        const envelope = createPublicFailureEnvelope("upstream_unavailable");
        const body = JSON.stringify(envelope);
        response.statusCode = 502;
        response.setHeader("Content-Type", "application/json; charset=utf-8");
        response.setHeader("X-Correlation-ID", envelope.correlation_id);
        response.setHeader("X-Error-Code", envelope.code);
        response.setHeader("Content-Length", String(Buffer.byteLength(body)));
        response.end(body);
        return;
      }

      response.destroy();
    },
    proxyRes: (proxyResponse, _request, response) => {
      delete proxyResponse.headers.location;
      delete proxyResponse.headers["set-cookie"];

      const abortDownstream = () => {
        if (!response.writableEnded && !response.destroyed) response.destroy();
      };
      proxyResponse.once("aborted", abortDownstream);
      proxyResponse.once("error", abortDownstream);
      proxyResponse.once("close", () => {
        if (!proxyResponse.complete) abortDownstream();
      });
      response.once("close", () => {
        if (!response.writableEnded) proxyResponse.destroy();
      });
    },
  },
});

app.use(async (request, response, next) => {
  const decision = classifyBackendRequest(request.method, request.url);
  if (decision.kind === "frontend") {
    if (!validateFrontendActionBodyFraming(
      request.method,
      request.url,
      rawHeaderValues(request, "content-length"),
      rawHeaderValues(request, "transfer-encoding"),
    )) {
      sendExpressPublicFailure(request, response, 400, "invalid_request");
      return;
    }
    next();
    return;
  }

  if (decision.kind === "reject") {
    sendPolicyRejection(response, decision);
    return;
  }

  const webDavPathBase = decision.lane === "protocol-webdav"
    ? `${request.baseUrl}/protocol`
    : undefined;
  const rewrittenIf = validateAndRewriteSecurityHeaders(
    request,
    decision.lane,
    webDavPathBase,
  );
  if (rewrittenIf === false) {
    sendPolicyRejection(response, {
      kind: "reject",
      status: 400,
      code: "invalid_request_target",
    });
    return;
  }

  if (decision.requiresFrontendPrincipal) {
    let authenticated = false;
    try {
      authenticated = await isAuthenticated(request);
    } catch {
      sendPublicFailure(response, 500, "internal_error");
      return;
    }
    if (!authenticated) {
      sendPublicFailure(response, 401, "authentication_required");
      return;
    }
  }

  sanitizePrivateHopHeaders(request, decision.lane);
  if (rewrittenIf !== undefined) request.headers.if = rewrittenIf;
  if (webDavPathBase !== undefined) {
    request.headers[INTERNAL_WEBDAV_PATH_BASE_HEADER] = Buffer
      .from(webDavPathBase, "utf8")
      .toString("base64url");
  }
  if (decision.injectInternalApiKey) {
    request.headers["x-api-key"] = process.env.FRONTEND_BACKEND_API_KEY ?? "";
  }
  request.url = decision.backendTarget;
  await forwardToBackend(request, response);
});

// Require authentication for every React Router route. The independently
// authenticated protocol ingress above never reaches this middleware.
app.use(authMiddleware);

app.use(
  createPinrailRequestHandler({
    build: () => import("virtual:react-router/server-build"),
    getLoadContext() {
      return {
        VALUE_FROM_EXPRESS: "Hello from Express",
      };
    },
  }),
);

app.use((request, response) => {
  sendExpressPublicFailure(request, response, 404, "route_not_found");
});
app.use(handleExpressTerminalFailure);

function readBackendOrigin(configured: string | undefined): string {
  const value = configured ?? "http://localhost:8080";
  const parsed = new URL(value);
  if (
    (parsed.protocol !== "http:" && parsed.protocol !== "https:")
    || parsed.username.length > 0
    || parsed.password.length > 0
    || parsed.pathname !== "/"
    || parsed.search.length > 0
    || parsed.hash.length > 0
  ) {
    throw new Error("BACKEND_URL must be an HTTP origin without credentials or a path.");
  }
  return parsed.origin;
}

export function readBackendProxyTimeout(configured: string | undefined): number {
  if (configured === undefined || configured.length === 0) {
    return DEFAULT_BACKEND_PROXY_TIMEOUT_MS;
  }
  if (!/^[1-9]\d*$/u.test(configured)) {
    throw new Error("BACKEND_PROXY_TIMEOUT_MS must be a bounded integer.");
  }
  const milliseconds = Number(configured);
  if (
    !Number.isSafeInteger(milliseconds)
    || milliseconds < MIN_BACKEND_PROXY_TIMEOUT_MS
    || milliseconds > MAX_BACKEND_PROXY_TIMEOUT_MS
  ) {
    throw new Error("BACKEND_PROXY_TIMEOUT_MS must be a bounded integer.");
  }
  return milliseconds;
}

function validateAndRewriteSecurityHeaders(
  request: express.Request,
  lane: BackendProxyLane,
  webDavPathBase: string | undefined,
): string | undefined | false {
  if (hasUnsafeConnectionOption(request)) return false;

  const apiKeyValues = rawHeaderValues(request, "x-api-key");
  if (apiKeyValues.length > 1) return false;
  if (apiKeyValues.length === 1) {
    const value = apiKeyValues[0];
    if (value.length > MAX_API_KEY_LENGTH) return false;
    if ((lane === "protocol-sab" || lane === "protocol-arr") && value.length === 0) {
      return false;
    }
  }

  const authorizationValues = rawHeaderValues(request, "authorization");
  if (authorizationValues.length > 1) return false;
  if (authorizationValues.some((value) => value.length > MAX_WEBDAV_HEADER_LENGTH)) {
    return false;
  }

  const ifValues = rawHeaderValues(request, "if");
  if (ifValues.length > 1) return false;
  if (ifValues.some((value) => value.length > MAX_WEBDAV_HEADER_LENGTH)) return false;

  const destinationValues = rawHeaderValues(request, "destination");
  if (lane === "protocol-webdav" && destinationValues.length > 0) return false;

  if (lane !== "protocol-webdav" || ifValues.length === 0) return undefined;
  if (webDavPathBase === undefined) return false;
  return rewriteWebDavIfHeader(request, ifValues[0], webDavPathBase);
}

function hasUnsafeConnectionOption(request: express.Request): boolean {
  for (const value of rawHeaderValues(request, "connection")) {
    for (const token of value.split(",")) {
      const headerName = token.trim().toLowerCase();
      if (
        !/^[!#$%&'*+.^_`|~0-9a-z-]+$/u.test(headerName)
        || securitySensitiveConnectionOptions.has(headerName)
        || headerName === "if"
        || headerName.startsWith("if-")
      ) {
        return true;
      }
    }
  }
  return false;
}

function rewriteWebDavIfHeader(
  request: express.Request,
  value: string,
  webDavPathBase: string,
): string | false {
  let invalid = false;
  let matchedAngles = 0;
  const rewritten = value.replace(/<([^<>]*)>/gu, (_match, resource: string) => {
    matchedAngles += 2;
    const target = rewriteTaggedWebDavResource(request, resource, webDavPathBase);
    if (!target) {
      invalid = true;
      return "";
    }
    return `<${target}>`;
  });

  const totalAngles = [...value].filter((character) => character === "<" || character === ">").length;
  return invalid || matchedAngles !== totalAngles ? false : rewritten;
}

function rewriteTaggedWebDavResource(
  request: express.Request,
  resource: string,
  webDavPathBase: string,
): string | undefined {
  let parsed: URL;
  try {
    parsed = new URL(resource);
  } catch {
    return undefined;
  }

  if (
    (parsed.protocol !== "http:" && parsed.protocol !== "https:")
    || parsed.username.length > 0
    || parsed.password.length > 0
    || parsed.search.length > 0
    || parsed.hash.length > 0
  ) {
    return undefined;
  }

  const authorityMatch = /^(?:https?):\/\/([^/?#]+)(\/.*)?$/u.exec(resource);
  const hostValues = rawHeaderValues(request, "host");
  if (
    !authorityMatch
    || hostValues.length !== 1
    || authorityMatch[1].toLowerCase() !== hostValues[0].toLowerCase()
  ) {
    return undefined;
  }

  const mountBase = request.baseUrl;
  if (mountBase && !parsed.pathname.startsWith(`${mountBase}/`)) return undefined;
  const mountRelativeTarget = mountBase ? parsed.pathname.slice(mountBase.length) : parsed.pathname;
  const decision = classifyBackendRequest("GET", mountRelativeTarget);
  if (decision.kind !== "proxy" || decision.lane !== "protocol-webdav") return undefined;

  return new URL(`${webDavPathBase}${decision.backendTarget}`, `${backendOrigin}/`).toString();
}

function sanitizePrivateHopHeaders(request: express.Request, lane: BackendProxyLane): void {
  for (const value of rawHeaderValues(request, "connection")) {
    for (const token of value.split(",")) {
      const headerName = token.trim().toLowerCase();
      if (/^[!#$%&'*+.^_`|~0-9a-z-]+$/u.test(headerName)) delete request.headers[headerName];
    }
  }
  for (const name of hopByHopHeaders) delete request.headers[name];

  for (const name of browserAuthorityHeaders) {
    if (name === "authorization" && lane === "protocol-webdav") continue;
    delete request.headers[name];
  }

  if (lane !== "protocol-sab" && lane !== "protocol-arr") {
    delete request.headers["x-api-key"];
  }
  if (lane !== "protocol-webdav") {
    delete request.headers.if;
    delete request.headers.destination;
  }
}

function rawHeaderValues(request: IncomingMessage, name: string): string[] {
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

function sendPolicyRejection(
  response: express.Response,
  decision: Extract<BackendRequestDecision, { kind: "reject" }>,
): void {
  if (decision.allow) response.setHeader("Allow", decision.allow.join(", "));
  sendPublicFailure(response, decision.status, decision.code);
}

function sendPublicFailure(
  response: express.Response,
  status: number,
  code: PublicFailureCode,
): void {
  const envelope = createPublicFailureEnvelope(code);
  response.setHeader("X-Correlation-ID", envelope.correlation_id);
  response.setHeader("X-Error-Code", envelope.code);
  response.status(status).type("application/json").send(envelope);
}

function isHttpResponse(value: ServerResponse | Socket): value is ServerResponse {
  return "setHeader" in value && typeof value.setHeader === "function";
}
