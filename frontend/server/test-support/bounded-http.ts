import http, {
  type ClientRequest,
  type IncomingHttpHeaders,
  type IncomingMessage,
  type OutgoingHttpHeaders,
  type Server,
  type ServerResponse,
} from "node:http";

const DEFAULT_TIMEOUT_MS = 10_000;
const DEFAULT_MAX_RESPONSE_BYTES = 2 * 1024 * 1024;
const SAFE_REQUEST_ERROR = "Disposable HTTP request failed.";
const SAFE_RELAY_ERROR = "Disposable HTTP relay failed.";
const SAFE_CLOSE_ERROR = "Disposable HTTP server cleanup failed.";

export type BoundedHttpResponse = Readonly<{
  status: number;
  headers: IncomingHttpHeaders;
  body: Buffer;
}>;

export type BoundedHttpRequestOptions = Readonly<{
  method?: string;
  headers?: OutgoingHttpHeaders;
  body?: string | Buffer;
}>;

export type BoundedHttpLimits = Readonly<{
  timeoutMs?: number;
  maxResponseBytes?: number;
}>;

export function requestLoopbackBounded(
  origin: string,
  path: string,
  options: BoundedHttpRequestOptions = {},
  limits: BoundedHttpLimits = {},
): Promise<BoundedHttpResponse> {
  let target: URL;
  try {
    target = new URL(origin);
    assertLoopbackHttpTarget(target);
  } catch {
    return Promise.reject(new Error(SAFE_REQUEST_ERROR));
  }
  const timeoutMs = positiveLimit(limits.timeoutMs, DEFAULT_TIMEOUT_MS);
  const maxResponseBytes = positiveLimit(
    limits.maxResponseBytes,
    DEFAULT_MAX_RESPONSE_BYTES,
  );

  return new Promise((resolve, reject) => {
    let request: ClientRequest | undefined;
    let response: IncomingMessage | undefined;
    let deadline: NodeJS.Timeout | undefined;
    let settled = false;
    let responseBytes = 0;
    const chunks: Buffer[] = [];
    const fail = () => {
      if (settled) return;
      settled = true;
      if (deadline) clearTimeout(deadline);
      response?.destroy();
      request?.destroy();
      reject(new Error(SAFE_REQUEST_ERROR));
    };
    const succeed = () => {
      if (settled || !response) return;
      settled = true;
      if (deadline) clearTimeout(deadline);
      resolve(Object.freeze({
        status: response.statusCode ?? 0,
        headers: response.headers,
        body: Buffer.concat(chunks, responseBytes),
      }));
    };
    deadline = setTimeout(fail, timeoutMs);
    try {
      request = http.request({
        hostname: target.hostname,
        port: target.port,
        method: options.method ?? "GET",
        path,
        headers: options.headers,
      }, (incoming) => {
        response = incoming;
        incoming.once("aborted", fail);
        incoming.once("error", fail);
        incoming.once("close", () => {
          if (!incoming.complete) fail();
        });
        incoming.on("data", (chunk: Buffer) => {
          if (settled) return;
          responseBytes += chunk.byteLength;
          if (responseBytes > maxResponseBytes) {
            fail();
            return;
          }
          chunks.push(chunk);
        });
        incoming.once("end", succeed);
      });
      request.once("error", fail);
      request.end(options.body);
    } catch {
      fail();
    }
  });
}

export function relayHttpRequestBounded(
  request: IncomingMessage,
  response: ServerResponse,
  targetOrigin: string,
  limits: BoundedHttpLimits = {},
): void {
  let target: URL;
  try {
    target = new URL(targetOrigin);
    assertLoopbackHttpTarget(target);
  } catch {
    writeSafeBadGateway(response);
    return;
  }
  const timeoutMs = positiveLimit(limits.timeoutMs, DEFAULT_TIMEOUT_MS);
  const maxResponseBytes = positiveLimit(
    limits.maxResponseBytes,
    DEFAULT_MAX_RESPONSE_BYTES,
  );
  let upstreamResponse: IncomingMessage | undefined;
  let upstream: ClientRequest | undefined;
  let deadline: NodeJS.Timeout | undefined;
  let responseBytes = 0;
  let settled = false;

  const finish = () => {
    if (settled) return;
    settled = true;
    if (deadline) clearTimeout(deadline);
  };
  const fail = () => {
    if (settled) return;
    settled = true;
    if (deadline) clearTimeout(deadline);
    try {
      if (upstream) request.unpipe(upstream);
    } catch {
      // Cleanup remains best-effort and must not surface request details.
    }
    try {
      upstreamResponse?.destroy();
      upstream?.destroy();
    } catch {
      // Cleanup remains best-effort and must not surface request details.
    }
    if (response.destroyed) return;
    try {
      if (response.headersSent) {
        response.destroy();
        return;
      }
      response.writeHead(502);
      response.end();
    } catch {
      response.destroy();
    }
  };
  deadline = setTimeout(fail, timeoutMs);
  try {
    upstream = http.request({
      hostname: target.hostname,
      port: target.port,
      method: request.method,
      path: request.url,
      headers: request.headers,
    }, (incoming) => {
      if (settled) {
        incoming.destroy();
        return;
      }
      upstreamResponse = incoming;
      const declaredLength = parseDeclaredLength(incoming.headers["content-length"]);
      if (declaredLength !== undefined && declaredLength > maxResponseBytes) {
        fail();
        return;
      }
      try {
        response.writeHead(incoming.statusCode ?? 502, incoming.headers);
      } catch {
        fail();
        return;
      }
      incoming.once("aborted", fail);
      incoming.once("error", fail);
      incoming.once("close", () => {
        if (!incoming.complete) fail();
      });
      incoming.on("data", (chunk: Buffer) => {
        if (settled) return;
        responseBytes += chunk.byteLength;
        if (responseBytes > maxResponseBytes) {
          fail();
          return;
        }
        try {
          if (!response.write(chunk)) {
            incoming.pause();
            response.once("drain", () => {
              if (!settled) incoming.resume();
            });
          }
        } catch {
          fail();
        }
      });
      incoming.once("end", () => {
        if (settled) return;
        try {
          response.end(finish);
        } catch {
          fail();
        }
      });
    });
    upstream.once("error", fail);
    request.once("aborted", fail);
    request.once("error", fail);
    response.once("close", () => {
      if (!response.writableFinished) fail();
    });
    request.pipe(upstream);
  } catch {
    fail();
  }
}

export function closeHttpServerBounded(
  server: Server,
  timeoutMs = 5_000,
): Promise<void> {
  const boundedTimeoutMs = positiveLimit(timeoutMs, 5_000);
  if (!server.listening) {
    server.closeAllConnections();
    return waitForConnectionsToDrain(server, boundedTimeoutMs);
  }
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (failed: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(forceTimer);
      clearTimeout(deadline);
      if (failed) reject(new Error(SAFE_CLOSE_ERROR));
      else resolve();
    };
    const forceTimer = setTimeout(() => {
      server.closeAllConnections();
    }, Math.max(1, Math.floor(boundedTimeoutMs / 2)));
    const deadline = setTimeout(() => {
      server.closeAllConnections();
      settle(true);
    }, boundedTimeoutMs);
    try {
      server.close((error) => settle(error !== undefined));
      server.closeIdleConnections();
    } catch {
      settle(true);
    }
  });
}

export async function withAbsoluteDeadline<T>(
  operation: Promise<T>,
  timeoutMs: number,
  safeErrorMessage: string,
): Promise<T> {
  let deadline: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      operation.catch(() => Promise.reject(new Error(safeErrorMessage))),
      new Promise<never>((_resolve, reject) => {
        deadline = setTimeout(() => reject(new Error(safeErrorMessage)), timeoutMs);
      }),
    ]);
  } finally {
    if (deadline) clearTimeout(deadline);
  }
}

function waitForConnectionsToDrain(server: Server, timeoutMs: number): Promise<void> {
  return new Promise((resolve, reject) => {
    let settled = false;
    let pollTimer: NodeJS.Timeout | undefined;
    const settle = (failed: boolean) => {
      if (settled) return;
      settled = true;
      if (pollTimer) clearTimeout(pollTimer);
      clearTimeout(deadline);
      if (failed) reject(new Error(SAFE_CLOSE_ERROR));
      else resolve();
    };
    const poll = () => {
      server.closeAllConnections();
      server.getConnections((error, count) => {
        if (settled) return;
        if (error) {
          settle(true);
          return;
        }
        if (count === 0) {
          settle(false);
          return;
        }
        pollTimer = setTimeout(poll, 10);
      });
    };
    const deadline = setTimeout(() => {
      server.closeAllConnections();
      settle(true);
    }, timeoutMs);
    poll();
  });
}

function assertLoopbackHttpTarget(target: URL): void {
  if (
    target.protocol !== "http:"
    || target.hostname !== "127.0.0.1"
    || !isExplicitPort(target.port)
    || target.username !== ""
    || target.password !== ""
    || target.search !== ""
    || target.hash !== ""
  ) {
    throw new Error(SAFE_RELAY_ERROR);
  }
}

function isExplicitPort(value: string): boolean {
  if (!/^\d{1,5}$/u.test(value)) return false;
  const port = Number(value);
  return Number.isSafeInteger(port) && port >= 1 && port <= 65_535;
}

function positiveLimit(value: number | undefined, fallback: number): number {
  return Number.isSafeInteger(value) && (value ?? 0) > 0 ? value as number : fallback;
}

function parseDeclaredLength(value: string | undefined): number | undefined {
  if (value === undefined || !/^\d+$/u.test(value)) return undefined;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : undefined;
}

function writeSafeBadGateway(response: ServerResponse): void {
  if (response.destroyed) return;
  response.writeHead(502);
  response.end();
}
