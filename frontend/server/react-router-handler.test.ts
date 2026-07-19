/** @vitest-environment node */

import { describe, expect, it, vi } from "vitest";
import {
  createRequestCorrelationId,
  transformReactRouterResponse,
} from "./react-router-handler";

const correlationId = "a".repeat(32);
const hostileHeaderCanary = "credential-marker|private-runtime-path|provider-response";

describe("transformReactRouterResponse", () => {
  it("returns successful responses by identity", async () => {
    const response = new Response("ok", {
      status: 200,
      headers: { "X-Fixture": "preserved" },
    });

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/", { headers: { Accept: "text/html" } }),
      response,
      correlationId,
    );

    expect(transformed).toBe(response);
    expect(transformed.headers.get("x-correlation-id")).toBeNull();
    expect(transformed.headers.get("x-error-code")).toBeNull();
  });

  it.each([
    [400, "invalid_request"],
    [404, "route_not_found"],
    [405, "method_not_allowed"],
    [418, "internal_error"],
    [500, "internal_error"],
    [502, "internal_error"],
  ] as const)("maps HTML status %s to %s without reading the hostile body", async (status, code) => {
    const hostile = "credential-marker|/private/runtime/path|provider-response";
    const response = new Response(hostile, {
      status,
      headers: {
        ...(status === 405 ? { Allow: "GET, HEAD" } : {}),
        "Content-Encoding": "gzip",
        "Content-Length": String(Buffer.byteLength(hostile)),
        "Content-Range": "bytes 0-1/2",
        "Content-Type": `text/html; boundary=${hostileHeaderCanary}`,
        ETag: hostileHeaderCanary,
        "Set-Cookie": `private=${hostileHeaderCanary}`,
        "X-Fixture": hostileHeaderCanary,
      },
    });
    const body = response.body!;
    const cancel = vi.spyOn(body, "cancel");
    const getReader = vi.spyOn(body, "getReader");

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/missing", { headers: { Accept: "text/html" } }),
      response,
      correlationId,
    );
    const text = await transformed.text();

    expect(cancel).toHaveBeenCalledTimes(1);
    expect(getReader).not.toHaveBeenCalled();
    expect(Buffer.byteLength(text, "utf8")).toBeLessThanOrEqual(512);
    expect(text).not.toContain(hostile);
    expect(text).toContain(code);
    expect(text).toContain(correlationId);
    expect(transformed.headers.get("x-correlation-id")).toBe(correlationId);
    expect(transformed.headers.get("x-error-code")).toBe(code);
    expect(transformed.headers.get("content-type")).toBe("text/html; charset=utf-8");
    expect(transformed.headers.get("content-encoding")).toBeNull();
    expect(transformed.headers.get("content-length")).toBe(String(Buffer.byteLength(text)));
    expect(transformed.headers.get("content-range")).toBeNull();
    expect(transformed.headers.get("etag")).toBeNull();
    expect(JSON.stringify(Object.fromEntries(transformed.headers))).not.toContain(hostileHeaderCanary);
    expect(transformed.headers.get("set-cookie")).toBeNull();
    expect(transformed.headers.get("x-fixture")).toBeNull();
    expect(transformed.headers.get("allow")).toBe(status === 405 ? "GET, HEAD" : null);
  });

  it("returns a zero-body bounded HTML failure for HEAD", async () => {
    const response = new Response("hostile", {
      status: 404,
      headers: { "Content-Type": "text/html; charset=utf-8" },
    });
    const cancel = vi.spyOn(response.body!, "cancel");

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/missing", {
        method: "HEAD",
        headers: { Accept: "text/html" },
      }),
      response,
      correlationId,
    );

    expect(cancel).toHaveBeenCalledTimes(1);
    expect(transformed.body).toBeNull();
    expect(transformed.headers.get("content-length")).toBe("0");
    expect(transformed.headers.get("x-error-code")).toBe("route_not_found");
  });

  it("preserves only a request-bound route-owned Single Fetch failure by stream identity", async () => {
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode("opaque-body"));
        controller.close();
      },
    });
    const response = new Response(stream, {
      status: 502,
      statusText: hostileHeaderCanary,
      headers: {
        Allow: "GET, POST",
        "Content-Length": "11",
        "Content-Type": `text/x-script; charset=utf-8; marker=${hostileHeaderCanary}`,
        "Set-Cookie": `private=${hostileHeaderCanary}`,
        "X-Correlation-ID": correlationId,
        "X-Error-Code": "upstream_unavailable",
        "X-Fixture": hostileHeaderCanary,
        "X-Remix-Response": "yes",
      },
    });

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/settings/update.data", {
        headers: { Accept: "text/x-script" },
      }),
      response,
      correlationId,
    );

    expect(transformed.body).toBe(stream);
    expect(transformed.headers.get("content-type")).toBe("text/x-script; charset=utf-8");
    expect(transformed.headers.get("content-length")).toBeNull();
    expect(transformed.headers.get("allow")).toBeNull();
    expect(JSON.stringify(Object.fromEntries(transformed.headers))).not.toContain(hostileHeaderCanary);
    expect(transformed.headers.get("set-cookie")).toBeNull();
    expect(transformed.headers.get("x-fixture")).toBeNull();
    expect(transformed.headers.get("x-correlation-id")).toBe(correlationId);
    expect(transformed.headers.get("x-error-code")).toBe("upstream_unavailable");
    expect(transformed.headers.get("x-remix-response")).toBe("yes");
    expect(transformed.statusText).not.toContain(hostileHeaderCanary);
    expect(await transformed.text()).toBe("opaque-body");
  });

  it.each([
    ["missing route identity", { "X-Remix-Response": "yes" }, "internal_error", "The request could not be completed."],
    ["non-exact Single Fetch marker", {
      "X-Correlation-ID": correlationId,
      "X-Error-Code": "upstream_unavailable",
      "X-Remix-Response": "YES",
    }, "upstream_unavailable", "The backend is unavailable."],
    ["missing Single Fetch marker", {
      "X-Correlation-ID": correlationId,
      "X-Error-Code": "upstream_unavailable",
    }, "upstream_unavailable", "The backend is unavailable."],
    ["mismatched route identity", {
      "X-Correlation-ID": "b".repeat(32),
      "X-Error-Code": "upstream_unavailable",
      "X-Remix-Response": "yes",
    }, "internal_error", "The request could not be completed."],
  ] as const)("replaces an untrusted .data failure without reading it: %s", async (
    _label,
    identityHeaders,
    expectedCode,
    expectedMessage,
  ) => {
    const hostile = "credential-marker|/missing.data|provider-response";
    const response = new Response(hostile, {
      status: 502,
      statusText: hostileHeaderCanary,
      headers: {
        Allow: `GET, ${hostileHeaderCanary}`,
        "Content-Type": `text/x-script; marker=${hostileHeaderCanary}`,
        "Set-Cookie": `private=${hostileHeaderCanary}`,
        "X-Fixture": hostileHeaderCanary,
        ...identityHeaders,
      },
    });
    const cancel = vi.spyOn(response.body!, "cancel");
    const getReader = vi.spyOn(response.body!, "getReader");

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/missing.data", {
        headers: { Accept: "text/x-script" },
      }),
      response,
      correlationId,
    );
    const body = await transformed.text();

    expect(cancel).toHaveBeenCalledTimes(1);
    expect(getReader).not.toHaveBeenCalled();
    expect(transformed.headers.get("x-error-code")).toBe(expectedCode);
    expect(transformed.headers.get("x-correlation-id")).toBe(correlationId);
    expect(transformed.headers.get("x-remix-response")).toBeNull();
    expect(transformed.headers.get("content-type")).toBe("application/json; charset=utf-8");
    expect(transformed.headers.get("content-length")).toBe(String(Buffer.byteLength(body)));
    expect(transformed.headers.get("allow")).toBeNull();
    expect(transformed.headers.get("set-cookie")).toBeNull();
    expect(transformed.headers.get("x-fixture")).toBeNull();
    expect(transformed.statusText).not.toContain(hostileHeaderCanary);
    expect(body).not.toContain(hostile);
    expect(body).not.toContain("/missing.data");
    expect(JSON.parse(body)).toEqual({
      status: false,
      error: expectedMessage,
      code: expectedCode,
      correlation_id: correlationId,
    });
  });

  it("replaces every non-.data resource failure with a fixed JSON envelope", async () => {
    const response = new Response("opaque provider diagnostic", {
      status: 502,
      headers: {
        "Content-Type": "application/octet-stream",
        "X-Correlation-ID": correlationId,
        "X-Error-Code": "upstream_unavailable",
        "X-Remix-Response": "yes",
      },
    });
    const cancel = vi.spyOn(response.body!, "cancel");

    const transformed = await transformReactRouterResponse(
      new Request("https://example.test/resource", {
        headers: { Accept: "application/octet-stream" },
      }),
      response,
      correlationId,
    );
    const body = await transformed.text();

    expect(cancel).toHaveBeenCalledTimes(1);
    expect(transformed.headers.get("content-type")).toBe("application/json; charset=utf-8");
    expect(transformed.headers.get("x-remix-response")).toBeNull();
    expect(transformed.headers.get("x-error-code")).toBe("upstream_unavailable");
    expect(body).not.toContain("provider diagnostic");
    expect(JSON.parse(body).code).toBe("upstream_unavailable");
  });

  it("creates distinct lower-hex request correlations", () => {
    const first = createRequestCorrelationId();
    const second = createRequestCorrelationId();

    expect(first).toMatch(/^[0-9a-f]{32}$/u);
    expect(second).toMatch(/^[0-9a-f]{32}$/u);
    expect(second).not.toBe(first);
  });
});
