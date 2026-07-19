import type express from "express";
import {
  createReadableStreamFromReadable,
  writeReadableStreamToWritable,
} from "@react-router/node";
import {
  createRequestHandler as createReactRouterRequestHandler,
  type AppLoadContext,
  type ServerBuild,
} from "react-router";
import {
  createPublicFailureEnvelope,
  INTERNAL_REQUEST_CORRELATION_HEADER,
  parsePublicFailureHeaders,
  type PublicFailureCode,
} from "../app/utils/public-failure.js";

type MaybePromise<T> = T | Promise<T>;

export type PinrailRequestHandlerOptions = Readonly<{
  build: ServerBuild | (() => Promise<ServerBuild>);
  getLoadContext?: (
    request: express.Request,
    response: express.Response,
  ) => MaybePromise<AppLoadContext>;
  mode?: string;
}>;

export function createPinrailRequestHandler({
  build,
  getLoadContext,
  mode = process.env.NODE_ENV,
}: PinrailRequestHandlerOptions): express.RequestHandler {
  const handleRequest = createReactRouterRequestHandler(build, mode);
  return async (request, response, next) => {
    try {
      const correlationId = createRequestCorrelationId();
      const reactRouterRequest = createReactRouterRequest(
        request,
        response,
        correlationId,
      );
      const loadContext = await getLoadContext?.(request, response);
      const reactRouterResponse = await handleRequest(reactRouterRequest, loadContext);
      const transformed = await transformReactRouterResponse(
        reactRouterRequest,
        reactRouterResponse,
        correlationId,
      );
      await sendReactRouterResponse(request, response, transformed);
    } catch (error) {
      next(error);
    }
  };
}

export function createRequestCorrelationId(): string {
  return crypto.randomUUID().replaceAll("-", "").toLowerCase();
}

export async function transformReactRouterResponse(
  request: Request,
  response: Response,
  correlationId: string,
): Promise<Response> {
  if (response.status < 400) return response;

  const existingIdentity = parsePublicFailureHeaders(response.headers);
  const code = existingIdentity?.correlation_id === correlationId
    ? existingIdentity.code
    : codeForStatus(response.status);
  if (isTrustedRouteOwnedDataFailure(request, response, existingIdentity, correlationId)) {
    const headers = createClosedFailureHeaders(response.headers, response.status, correlationId, code);
    headers.set("Content-Type", "text/x-script; charset=utf-8");
    headers.set("X-Remix-Response", "yes");
    return new Response(response.body, {
      status: response.status,
      headers,
    });
  }

  try {
    await response.body?.cancel();
  } catch {
    // The fixed replacement remains authoritative if the source already closed.
  }
  const envelope = createPublicFailureEnvelope(code, correlationId);
  const renderHtml = !isDataRequest(request) && acceptsHtml(request);
  const body = renderHtml
    ? `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Request failed</title></head><body><main><h1>${envelope.error}</h1><p>Code: ${envelope.code}</p><p>Correlation: ${envelope.correlation_id}</p></main></body></html>`
    : JSON.stringify(envelope);
  const bodyless = request.method === "HEAD";
  const headers = createClosedFailureHeaders(response.headers, response.status, correlationId, code);
  headers.set(
    "Content-Type",
    renderHtml ? "text/html; charset=utf-8" : "application/json; charset=utf-8",
  );
  headers.set("Content-Length", bodyless ? "0" : String(Buffer.byteLength(body, "utf8")));
  return new Response(bodyless ? null : body, {
    status: response.status,
    headers,
  });
}

function createReactRouterHeaders(requestHeaders: express.Request["headers"]): Headers {
  const headers = new Headers();
  for (const [key, values] of Object.entries(requestHeaders)) {
    if (values === undefined) continue;
    if (Array.isArray(values)) {
      for (const value of values) headers.append(key, value);
    } else {
      headers.set(key, values);
    }
  }
  return headers;
}

function createReactRouterRequest(
  request: express.Request,
  response: express.Response,
  correlationId: string,
): Request {
  const [, forwardedPortText] = request.app?.enabled("trust proxy")
    ? request.get("X-Forwarded-Host")?.split(":") ?? []
    : [];
  const [, hostPortText] = request.get("host")?.split(":") ?? [];
  const forwardedPort = Number.parseInt(forwardedPortText, 10);
  const hostPort = Number.parseInt(hostPortText, 10);
  const port = Number.isSafeInteger(forwardedPort)
    ? forwardedPort
    : Number.isSafeInteger(hostPort)
    ? hostPort
    : "";
  const hostname = request.hostname.split(/[\\/?#@]/u)[0] || "localhost";
  const resolvedHost = `${hostname}${port ? `:${port}` : ""}`;
  const url = new URL(`${request.protocol}://${resolvedHost}${request.originalUrl}`);
  let controller: AbortController | null = new AbortController();
  const headers = createReactRouterHeaders(request.headers);
  headers.set(INTERNAL_REQUEST_CORRELATION_HEADER, correlationId);
  const init: RequestInit & { duplex?: "half" } = {
    method: request.method,
    headers,
    signal: controller.signal,
  };
  response.on("finish", () => {
    controller = null;
  });
  response.on("close", () => controller?.abort());
  if (request.method !== "GET" && request.method !== "HEAD") {
    init.body = createReadableStreamFromReadable(request);
    init.duplex = "half";
  }
  return new Request(url.href, init);
}

async function sendReactRouterResponse(
  request: express.Request,
  response: express.Response,
  reactRouterResponse: Response,
): Promise<void> {
  if (response.destroyed || response.writableEnded) {
    await reactRouterResponse.body?.cancel();
    return;
  }
  response.statusMessage = reactRouterResponse.statusText;
  response.status(reactRouterResponse.status);
  for (const [key, value] of reactRouterResponse.headers.entries()) {
    response.append(key, value);
  }
  if (reactRouterResponse.headers.get("Content-Type")?.match(/text\/event-stream/iu)) {
    response.flushHeaders();
  }
  if (request.method === "HEAD") {
    await reactRouterResponse.body?.cancel();
    response.end();
    return;
  }
  if (!reactRouterResponse.body) {
    response.end();
    return;
  }
  try {
    await writeReadableStreamToWritable(reactRouterResponse.body, response);
  } catch (error) {
    if (!response.destroyed && !response.writableEnded) throw error;
  }
}

function isTrustedRouteOwnedDataFailure(
  request: Request,
  response: Response,
  identity: ReturnType<typeof parsePublicFailureHeaders>,
  correlationId: string,
): boolean {
  return isDataRequest(request)
    && response.headers.get("X-Remix-Response") === "yes"
    && identity?.correlation_id === correlationId;
}

function isDataRequest(request: Request): boolean {
  return new URL(request.url).pathname.endsWith(".data");
}

function acceptsHtml(request: Request): boolean {
  return (request.headers.get("Accept") ?? "")
    .split(",")
    .some((value) => value.split(";", 1)[0].trim().toLowerCase() === "text/html");
}

function createClosedFailureHeaders(
  source: Headers,
  status: number,
  correlationId: string,
  code: PublicFailureCode,
): Headers {
  const headers = new Headers({
    "X-Correlation-ID": correlationId,
    "X-Error-Code": code,
  });
  if (status === 405) copySafeAllow(source, headers);
  return headers;
}

const allowedFailureMethods = new Set([
  "DELETE",
  "GET",
  "HEAD",
  "OPTIONS",
  "PATCH",
  "POST",
  "PUT",
]);

function copySafeAllow(source: Headers, target: Headers): void {
  const value = source.get("Allow");
  if (value === null || value.length === 0 || value.length > 128) return;
  const methods = value.split(",").map((method) => method.trim());
  if (
    methods.length === 0
    || methods.length > allowedFailureMethods.size
    || new Set(methods).size !== methods.length
    || methods.some((method) => !allowedFailureMethods.has(method))
  ) return;
  target.set("Allow", methods.join(", "));
}

function codeForStatus(status: number): PublicFailureCode {
  if (status === 400) return "invalid_request";
  if (status === 404) return "route_not_found";
  if (status === 405) return "method_not_allowed";
  return "internal_error";
}
