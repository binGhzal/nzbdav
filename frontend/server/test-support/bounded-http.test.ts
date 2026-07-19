/** @vitest-environment node */

import http, { IncomingMessage, ServerResponse, type Server } from "node:http";
import { Socket } from "node:net";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  relayHttpRequestBounded,
  requestLoopbackBounded,
} from "./bounded-http";

const servers: Server[] = [];

afterEach(async () => {
  const results = await Promise.allSettled(
    servers.splice(0).map((server) => closeHttpServerBounded(server, 500)),
  );
  if (results.some((result) => result.status === "rejected")) {
    throw new Error("Bounded HTTP fixture cleanup failed.");
  }
});

describe("bounded disposable HTTP transport", () => {
  it.each([
    "http://localhost:1234",
    "http://127.0.0.1",
    "http://127.0.0.1:1234?query",
    "http://127.0.0.1:1234/#fragment",
    "https://127.0.0.1:1234",
  ])("rejects non-owned target shape %s before opening a request", async (origin) => {
    const result = await requestLoopbackBounded(origin, "/", {}, {
      timeoutMs: 10,
      maxResponseBytes: 64,
    }).then(
      () => ({ rejected: false, safe: false }),
      (error: unknown) => ({
        rejected: true,
        safe: error instanceof Error && error.message === "Disposable HTTP request failed.",
      }),
    );

    expect(result).toEqual({ rejected: true, safe: true });
  });

  it("contains synchronous malformed request and relay construction failures", async () => {
    vi.useFakeTimers();
    try {
      const requestResult = await requestLoopbackBounded(
        "http://127.0.0.1:1",
        "/malformed path",
        {},
        { timeoutMs: 5, maxResponseBytes: 64 },
      ).then(
        () => ({ rejected: false, safe: false }),
        (error: unknown) => ({
          rejected: true,
          safe: error instanceof Error && error.message === "Disposable HTTP request failed.",
        }),
      );

      const socket = new Socket();
      const relayRequest = new IncomingMessage(socket);
      relayRequest.method = "GET";
      relayRequest.url = "/malformed path";
      const relayResponse = new ServerResponse(relayRequest);
      let relayThrew = false;
      try {
        relayHttpRequestBounded(
          relayRequest,
          relayResponse,
          "http://127.0.0.1:1",
          { timeoutMs: 5, maxResponseBytes: 64 },
        );
      } catch {
        relayThrew = true;
      }

      let delayedThrow = false;
      try {
        await vi.advanceTimersByTimeAsync(10);
      } catch {
        delayedThrow = true;
      } finally {
        socket.destroy();
      }

      expect({
        requestRejected: requestResult.rejected,
        requestSafe: requestResult.safe,
        relayThrew,
        relayStatus: relayResponse.statusCode,
        delayedThrow,
      }).toEqual({
        requestRejected: true,
        requestSafe: true,
        relayThrew: false,
        relayStatus: 502,
        delayedThrow: false,
      });
    } finally {
      vi.useRealTimers();
    }
  });

  it("uses an absolute deadline even while response bytes keep arriving", async () => {
    const server = track(http.createServer((_request, response) => {
      response.writeHead(200, { "content-type": "application/octet-stream" });
      const interval = setInterval(() => response.write("x"), 5);
      response.once("close", () => clearInterval(interval));
    }));
    const origin = await listen(server);
    const startedAt = Date.now();
    const result = await requestResult(origin, { timeoutMs: 80, maxResponseBytes: 1_024 });

    expect({
      rejected: result.rejected,
      safeMessage: result.message === "Disposable HTTP request failed.",
      bounded: Date.now() - startedAt < 1_000,
    }).toEqual({ rejected: true, safeMessage: true, bounded: true });
  });

  it("rejects over-limit and truncated responses without returning a partial body", async () => {
    const oversized = track(http.createServer((_request, response) => {
      response.end(Buffer.alloc(65, 0x78));
    }));
    const oversizedOrigin = await listen(oversized);
    const overLimit = await requestResult(oversizedOrigin, {
      timeoutMs: 500,
      maxResponseBytes: 64,
    });

    const truncated = track(http.createServer((_request, response) => {
      response.writeHead(200, { "content-length": "16" });
      response.write("short");
      response.destroy();
    }));
    const truncatedOrigin = await listen(truncated);
    const incomplete = await requestResult(truncatedOrigin, {
      timeoutMs: 500,
      maxResponseBytes: 64,
    });

    expect({
      overLimitRejected: overLimit.rejected,
      overLimitSafe: overLimit.message === "Disposable HTTP request failed.",
      truncatedRejected: incomplete.rejected,
      truncatedSafe: incomplete.message === "Disposable HTTP request failed.",
    }).toEqual({
      overLimitRejected: true,
      overLimitSafe: true,
      truncatedRejected: true,
      truncatedSafe: true,
    });
  });

  it("cancels a streaming upstream when the downstream client aborts", async () => {
    let observeCancellation: (() => void) | undefined;
    const cancellation = new Promise<void>((resolve) => {
      observeCancellation = resolve;
    });
    const upstream = track(http.createServer((request, response) => {
      const interval = setInterval(() => response.write("chunk"), 5);
      const markCancelled = () => {
        clearInterval(interval);
        observeCancellation?.();
      };
      request.once("aborted", markCancelled);
      response.once("close", () => {
        if (!response.writableFinished) markCancelled();
      });
    }));
    const upstreamOrigin = await listen(upstream);
    const relay = track(http.createServer((request, response) => {
      relayHttpRequestBounded(request, response, upstreamOrigin, {
        timeoutMs: 1_000,
        maxResponseBytes: 1_024,
      });
    }));
    const relayOrigin = new URL(await listen(relay));

    await new Promise<void>((resolve) => {
      const client = http.get({
        hostname: relayOrigin.hostname,
        port: relayOrigin.port,
        path: "/stream",
      }, (response) => {
        response.once("data", () => {
          response.destroy();
          resolve();
        });
        response.once("error", () => resolve());
      });
      client.once("error", () => resolve());
    });
    await expect(Promise.race([
      cancellation.then(() => true),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 500)),
    ])).resolves.toBe(true);
  });

  it("returns a bounded generic 502 when the owned upstream resets the connection", async () => {
    const unavailable = track(http.createServer());
    unavailable.on("connection", (socket) => socket.destroy());
    const unavailableOrigin = await listen(unavailable);
    const relay = track(http.createServer((request, response) => {
      relayHttpRequestBounded(request, response, unavailableOrigin, {
        timeoutMs: 500,
        maxResponseBytes: 64,
      });
    }));
    const relayOrigin = await listen(relay);

    const response = await requestLoopbackBounded(relayOrigin, "/unavailable", {}, {
      timeoutMs: 1_000,
      maxResponseBytes: 64,
    });

    expect({ status: response.status, bodyBytes: response.body.byteLength }).toEqual({
      status: 502,
      bodyBytes: 0,
    });
  });

  it("forcibly closes a server with an active response inside its absolute bound", async () => {
    const server = track(http.createServer((_request, response) => {
      response.writeHead(200);
      response.write("held");
    }));
    const origin = new URL(await listen(server));
    let observeResponseClose: (() => void) | undefined;
    const responseClosed = new Promise<void>((resolve) => {
      observeResponseClose = resolve;
    });
    const responseStarted = new Promise<void>((resolve) => {
      const client = http.get({
        hostname: origin.hostname,
        port: origin.port,
        path: "/held",
      }, (response) => {
        response.once("data", () => resolve());
        response.once("close", () => observeResponseClose?.());
        response.once("error", () => undefined);
      });
      client.once("error", () => undefined);
    });
    await responseStarted;

    const startedAt = Date.now();
    await closeHttpServerBounded(server, 100);
    const closedWithinBound = await Promise.race([
      responseClosed.then(() => true),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 200)),
    ]);
    server.closeAllConnections();

    expect({
      bounded: Date.now() - startedAt < 1_000,
      closedWithinBound,
      listening: server.listening,
    }).toEqual({ bounded: true, closedWithinBound: true, listening: false });
  });

  it("finishes draining connections when another close already stopped listening", async () => {
    const server = track(http.createServer((_request, response) => {
      response.writeHead(200);
      response.write("held");
    }));
    const origin = new URL(await listen(server));
    let observeResponseClose: (() => void) | undefined;
    const responseClosed = new Promise<void>((resolve) => {
      observeResponseClose = resolve;
    });
    const responseStarted = new Promise<void>((resolve) => {
      const client = http.get({
        hostname: origin.hostname,
        port: origin.port,
        path: "/held",
      }, (response) => {
        response.once("data", () => resolve());
        response.once("close", () => observeResponseClose?.());
        response.once("error", () => undefined);
      });
      client.once("error", () => undefined);
    });
    await responseStarted;
    server.close();

    await closeHttpServerBounded(server, 100);
    const closedWithinBound = await Promise.race([
      responseClosed.then(() => true),
      new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 200)),
    ]);
    server.closeAllConnections();

    expect({ listening: server.listening, closedWithinBound }).toEqual({
      listening: false,
      closedWithinBound: true,
    });
  });
});

function track(server: Server): Server {
  servers.push(server);
  return server;
}

function listen(server: Server): Promise<string> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("Disposable test server did not bind."));
        return;
      }
      resolve(`http://127.0.0.1:${address.port}`);
    });
  });
}

async function requestResult(
  origin: string,
  bounds: Readonly<{ timeoutMs: number; maxResponseBytes: number }>,
): Promise<Readonly<{ rejected: boolean; message?: string }>> {
  try {
    await requestLoopbackBounded(origin, "/stream", {}, bounds);
    return { rejected: false };
  } catch (error) {
    return {
      rejected: true,
      message: error instanceof Error ? error.message : undefined,
    };
  }
}
