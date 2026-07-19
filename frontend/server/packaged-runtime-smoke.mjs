import { spawn } from "node:child_process";
import net from "node:net";
import { UNSAFE_decodeViaTurboStream as decodeViaTurboStream } from "react-router";

const StartupTimeoutMs = 15_000;
const FixedFailure = "packaged_frontend_smoke_failed";

const port = await reserveLoopbackPort();
const child = spawn("node", ["dist-node/bootstrap.js"], {
  cwd: process.cwd(),
  detached: process.platform !== "win32",
  env: {
    ...process.env,
    ALLOW_INSECURE_COOKIES: "true",
    AUTH_MODE: "authentik-proxy",
    AUTHENTIK_APP_SLUG: "packaged-fixture",
    AUTHENTIK_TRUSTED_PROXY_CIDRS: "127.0.0.1/32",
    BACKEND_URL: "http://127.0.0.1:1",
    FRONTEND_BACKEND_API_KEY: "packaged-fixture",
    LISTEN_ADDRESS: "127.0.0.1",
    NODE_ENV: "production",
    NZBDAV_ENV_FILE: ".missing-packaged-runtime-smoke-env",
    PORT: String(port),
    SECURE_COOKIES: "false",
    SESSION_KEY: "b".repeat(64),
    URL_BASE: "",
  },
  stdio: ["ignore", "pipe", "pipe"],
});

let outputBytes = 0;
let ready = false;
/** @type {Promise<void>} */
const readySignal = new Promise((resolve, reject) => {
  const deadline = setTimeout(() => reject(new Error(FixedFailure)), StartupTimeoutMs);
  /** @param {Buffer} chunk */
  const consume = (chunk) => {
    outputBytes += chunk.byteLength;
    if (outputBytes > 4_096) {
      reject(new Error(FixedFailure));
      return;
    }
    if (!ready && chunk.toString("utf8").split(/\r?\n/u).includes("frontend_ready")) {
      ready = true;
      clearTimeout(deadline);
      resolve();
    }
  };
  child.stdout.on("data", consume);
  child.stderr.on("data", consume);
  child.once("error", () => {
    clearTimeout(deadline);
    reject(new Error(FixedFailure));
  });
  child.once("exit", () => {
    clearTimeout(deadline);
    if (!ready) reject(new Error(FixedFailure));
  });
});

try {
  await readySignal;
  const origin = `http://127.0.0.1:${port}`;
  const response = await fetch(`${origin}/healthz`, {
    signal: AbortSignal.timeout(5_000),
  });
  const body = await response.text();
  if (response.status !== 200 || body !== "ok") throw new Error(FixedFailure);
  await verifyReactRouterSuccessContract(origin);
  await verifyReactRouterFailureContract(origin);
} catch {
  process.exitCode = 1;
} finally {
  terminateChild("SIGTERM");
  if (child.exitCode === null && child.signalCode === null) {
    await Promise.race([
      new Promise((resolve) => child.once("exit", resolve)),
      new Promise((resolve) => setTimeout(resolve, 2_000)),
    ]);
  }
  if (child.exitCode === null && child.signalCode === null) terminateChild("SIGKILL");
  child.stdout.destroy();
  child.stderr.destroy();
}

/** @param {string} origin */
async function verifyReactRouterSuccessContract(origin) {
  const response = await fetch(`${origin}/health.data?_routes=root`, {
    headers: {
      Accept: "text/x-script",
      "X-Authentik-Meta-App": "packaged-fixture",
      "X-Authentik-Uid": "packaged-user-id",
      "X-Authentik-Username": "packaged-user",
    },
    signal: AbortSignal.timeout(5_000),
  });
  if (
    response.status !== 200
    || response.headers.get("content-type") !== "text/x-script; charset=utf-8"
    || response.headers.get("x-remix-response") !== "yes"
    || response.headers.get("x-correlation-id") !== null
    || response.headers.get("x-error-code") !== null
    || !response.body
  ) {
    throw new Error(FixedFailure);
  }
  const decoded = await decodeViaTurboStream(response.body, globalThis);
  await decoded.done;
  if (JSON.stringify(decoded.value) !== JSON.stringify({
    root: {
      data: {
        useLayout: true,
        isFrontendAuthDisabled: true,
      },
    },
  })) {
    throw new Error(FixedFailure);
  }
}

/** @param {string} origin */
async function verifyReactRouterFailureContract(origin) {
  const hostileCorrelation = "f".repeat(32);
  const authenticatedHeaders = {
    "X-Authentik-Meta-App": "packaged-fixture",
    "X-Authentik-Uid": "packaged-user-id",
    "X-Authentik-Username": "packaged-user",
  };

  const unknownDocument = await fetch(`${origin}/missing`, {
    headers: {
      ...authenticatedHeaders,
      Accept: "text/html",
      "X-Pinrail-Request-Correlation": hostileCorrelation,
    },
    redirect: "manual",
    signal: AbortSignal.timeout(5_000),
  });
  const unknownIdentity = await expectHtmlFailure(unknownDocument, 404, "route_not_found");
  if (unknownIdentity === hostileCorrelation) throw new Error(FixedFailure);

  await expectHtmlFailure(await fetch(`${origin}/health`, {
    headers: { ...authenticatedHeaders, Accept: "text/html" },
    redirect: "manual",
    signal: AbortSignal.timeout(5_000),
  }), 500, "internal_error");

  await expectHtmlFailure(await fetch(`${origin}/missing`, {
    method: "POST",
    headers: {
      ...authenticatedHeaders,
      Accept: "text/html",
      "Content-Type": "application/x-www-form-urlencoded",
      Origin: "http://cross-origin.invalid",
    },
    body: "fixture=1",
    redirect: "manual",
    signal: AbortSignal.timeout(5_000),
  }), 400, "invalid_request");

  await expectFixedJsonFailure(await fetch(`${origin}/missing.data`, {
    headers: { ...authenticatedHeaders, Accept: "text/x-script" },
    signal: AbortSignal.timeout(5_000),
  }), 404, "route_not_found", ["/missing.data"]);

  await expectFixedJsonFailure(await fetch(`${origin}/settings.data`, {
    headers: { ...authenticatedHeaders, Accept: "text/x-script" },
    signal: AbortSignal.timeout(5_000),
  }), 500, "internal_error");

  await expectFixedJsonFailure(await fetch(`${origin}/settings.data`, {
    method: "POST",
    headers: {
      ...authenticatedHeaders,
      Accept: "text/x-script",
      "Content-Type": "application/x-www-form-urlencoded",
      Origin: origin,
    },
    body: "fixture=1",
    signal: AbortSignal.timeout(5_000),
  }), 405, "method_not_allowed");

  await expectFixedJsonFailure(await fetch(`${origin}/settings/update.data`, {
    method: "POST",
    headers: {
      ...authenticatedHeaders,
      Accept: "text/x-script",
      "Content-Type": "application/x-www-form-urlencoded",
      Origin: "http://cross-origin.invalid",
    },
    body: "config=%7B%7D",
    signal: AbortSignal.timeout(5_000),
  }), 400, "invalid_request");

  await expectTurboFailure(await fetch(`${origin}/settings/update.data`, {
    method: "POST",
    headers: {
      ...authenticatedHeaders,
      Accept: "text/x-script",
      "Content-Type": "application/x-www-form-urlencoded",
      Origin: origin,
      "X-Pinrail-Request-Correlation": hostileCorrelation,
    },
    body: "config=",
    signal: AbortSignal.timeout(5_000),
  }), 400, "invalid_request", [hostileCorrelation]);

  const head = await fetch(`${origin}/missing`, {
    method: "HEAD",
    headers: { ...authenticatedHeaders, Accept: "text/html" },
    signal: AbortSignal.timeout(5_000),
  });
  expectFailureIdentity(head, 404, "route_not_found");
  if (head.headers.get("content-length") !== "0" || await head.text() !== "") {
    throw new Error(FixedFailure);
  }

  const manifestTarget = `/__manifest?paths=/${"a".repeat(7_700)}`;
  await expectHtmlFailure(await fetch(`${origin}${manifestTarget}`, {
    headers: { Accept: "text/html" },
    signal: AbortSignal.timeout(5_000),
  }), 400, "invalid_request");
}

/**
 * @param {Response} response
 * @param {number} status
 * @param {"internal_error"|"invalid_request"|"route_not_found"} code
 */
async function expectHtmlFailure(response, status, code) {
  const correlationId = expectFailureIdentity(response, status, code);
  if (response.headers.get("content-type") !== "text/html; charset=utf-8") {
    throw new Error(FixedFailure);
  }
  const text = await response.text();
  if (
    Buffer.byteLength(text, "utf8") > 512
    || !text.includes(code)
    || !text.includes(correlationId)
    || text.includes("<script")
    || text.includes("__reactRouter")
  ) {
    throw new Error(FixedFailure);
  }
  return correlationId;
}

/**
 * @param {Response} response
 * @param {number} status
 * @param {"invalid_request"|"upstream_unavailable"} code
 * @param {string[]} [forbidden]
 */
async function expectTurboFailure(response, status, code, forbidden = []) {
  const correlationId = expectFailureIdentity(response, status, code);
  if (
    response.headers.get("x-remix-response") !== "yes"
    || response.headers.get("content-type") !== "text/x-script; charset=utf-8"
    || !response.body
  ) {
    throw new Error(FixedFailure);
  }
  const decoded = await decodeViaTurboStream(response.body, globalThis);
  await decoded.done;
  const expectedMessages = {
    invalid_request: "The request is invalid.",
    upstream_unavailable: "The backend is unavailable.",
  };
  const serialized = JSON.stringify(decoded.value);
  if (
    serialized !== JSON.stringify({
      data: {
        status: false,
        error: expectedMessages[code],
        code,
        correlation_id: correlationId,
      },
    })
    || forbidden.some((marker) => serialized.includes(marker))
  ) {
    throw new Error(FixedFailure);
  }
}

/**
 * @param {Response} response
 * @param {number} status
 * @param {"internal_error"|"invalid_request"|"method_not_allowed"|"route_not_found"} code
 * @param {string[]} [forbidden]
 */
async function expectFixedJsonFailure(response, status, code, forbidden = []) {
  const correlationId = expectFailureIdentity(response, status, code);
  if (
    response.headers.get("x-remix-response") !== null
    || response.headers.get("content-type") !== "application/json; charset=utf-8"
  ) {
    throw new Error(FixedFailure);
  }
  const text = await response.text();
  if (
    Buffer.byteLength(text, "utf8") > 512
    || response.headers.get("content-length") !== String(Buffer.byteLength(text, "utf8"))
    || forbidden.some((marker) => text.includes(marker))
  ) {
    throw new Error(FixedFailure);
  }
  const expectedMessages = {
    internal_error: "The request could not be completed.",
    invalid_request: "The request is invalid.",
    method_not_allowed: "The request method is not allowed.",
    route_not_found: "The requested route was not found.",
  };
  if (JSON.stringify(JSON.parse(text)) !== JSON.stringify({
    status: false,
    error: expectedMessages[code],
    code,
    correlation_id: correlationId,
  })) {
    throw new Error(FixedFailure);
  }
}

/**
 * @param {Response} response
 * @param {number} status
 * @param {string} code
 */
function expectFailureIdentity(response, status, code) {
  const correlationId = response.headers.get("x-correlation-id");
  if (
    response.status !== status
    || response.headers.get("x-error-code") !== code
    || !correlationId
    || !/^[0-9a-f]{32}$/u.test(correlationId)
  ) {
    throw new Error(FixedFailure);
  }
  return correlationId;
}

/** @param {NodeJS.Signals} signal */
function terminateChild(signal) {
  try {
    if (process.platform !== "win32" && child.pid !== undefined) {
      process.kill(-child.pid, signal);
    } else {
      child.kill(signal);
    }
  } catch {
    // The isolated child group already exited.
  }
}

/** @returns {Promise<number>} */
function reserveLoopbackPort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        server.close();
        reject(new Error(FixedFailure));
        return;
      }
      server.close((error) => error ? reject(new Error(FixedFailure)) : resolve(address.port));
    });
  });
}
