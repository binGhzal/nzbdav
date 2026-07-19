/** @vitest-environment node */

import http, { type Server } from "node:http";
import { createHash, timingSafeEqual } from "node:crypto";
import { readFile } from "node:fs/promises";
import express from "express";
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  closeHttpServerBounded,
  relayHttpRequestBounded,
  withAbsoluteDeadline,
} from "./test-support/bounded-http";
import {
  configureWritablePausedProtocolBackend,
  createDisposableProtocolSeed,
  digest,
  obscureRclonePassword,
  readDisposableProtocolSnapshot,
  runPinnedRclone,
  validatePinnedRcloneImage,
  waitForDisposableProtocolSnapshot,
  type DisposableProtocolSeed,
  type DisposableProtocolSnapshot,
  type RcloneCredential,
  type RcloneResult,
} from "./test-support/disposable-protocol-state";
import {
  startDisposableAspNetBackend,
  type DisposableAspNetBackend,
} from "./test-support/disposable-aspnet-backend";

const authentication = vi.hoisted(() => ({
  isAuthenticated: vi.fn(),
}));

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: authentication.isAuthenticated,
}));

vi.mock("~/auth/auth-middleware.server", () => ({
  authMiddleware: async (
    req: express.Request,
    res: express.Response,
    next: express.NextFunction,
  ) => {
    if (await authentication.isAuthenticated(req)) {
      next();
      return;
    }
    res.sendStatus(401);
  },
}));

vi.mock("./react-router-handler", () => ({
  createPinrailRequestHandler: () => (
    _req: express.Request,
    res: express.Response,
  ) => res.status(418).type("text/plain").send("frontend"),
}));

type RelayHit = {
  method: string;
  pathname: string;
  status: number;
  authorization: "missing" | "valid" | "valid-user-other-password" | "other-user";
};

type RelayFixture = Readonly<{
  origin: string;
  server: Server;
  hits: RelayHit[];
  didOverflow: () => boolean;
  reset: () => void;
}>;

type FrontendFixture = Readonly<{
  origin: string;
  server: Server;
}>;

type CapturedRclone = Readonly<{
  result: RcloneResult;
  hits: readonly RelayHit[];
}>;

type TraceSummary = Readonly<{
  methods: Readonly<Record<string, number>>;
  statuses: Readonly<Record<string, number>>;
  authorizations: Readonly<Record<string, number>>;
  operationShapes: Readonly<Record<string, number>>;
  operationStatuses: Readonly<Record<string, number>>;
  forbidden: number;
  unapproved: number;
}>;

const originalBackendUrl = process.env.BACKEND_URL;
const originalInternalApiKey = process.env.FRONTEND_BACKEND_API_KEY;
const ownedServers: Server[] = [];
const forbiddenMethods = new Set([
  "COPY",
  "LOCK",
  "MKCOL",
  "MOVE",
  "PROPPATCH",
  "UNLOCK",
]);

let backend: DisposableAspNetBackend;
let relay: RelayFixture;
let frontend: FrontendFixture;
let canary: DisposableProtocolSeed;
let contract: DisposableProtocolSeed;
let credential: RcloneCredential;
let wrongCredential: RcloneCredential;
let readme: Buffer;

beforeAll(async () => {
  await validatePinnedRcloneImage();
  backend = await startDisposableAspNetBackend();
  await configureWritablePausedProtocolBackend(backend);
  [canary, contract] = await Promise.all([
    createDisposableProtocolSeed(backend, "canary"),
    createDisposableProtocolSeed(backend, "contract"),
  ]);
  [credential, wrongCredential, readme] = await Promise.all([
    obscureRclonePassword(backend.credentials.webDavPassword).then((obscuredPassword) =>
      Object.freeze({ username: backend.credentials.webDavUsername, obscuredPassword })),
    obscureRclonePassword("wrong-passphrase").then((obscuredPassword) =>
      Object.freeze({ username: "wrong-user", obscuredPassword })),
    readFile(new URL("../../backend/WebDav/StaticFiles/root/README.md", import.meta.url)),
  ]);
  relay = await startCountingRelay(backend);

  process.env.BACKEND_URL = relay.origin;
  process.env.FRONTEND_BACKEND_API_KEY = backend.credentials.internalApiKey;
  vi.resetModules();
  const { app } = await import("./app");
  const parent = express();
  parent.disable("x-powered-by");
  parent.use("/nzbdav", app);
  const server = trackServer(http.createServer(parent));
  frontend = Object.freeze({ origin: await listenLoopback(server), server });
}, 300_000);

afterAll(async () => {
  let cleanupFailed = false;
  try {
    const closes = await Promise.allSettled([
      ...ownedServers.splice(0).map((server) => closeHttpServerBounded(server)),
    ]);
    cleanupFailed = closes.some((result) => result.status === "rejected");
  } finally {
    try {
      if (backend) {
        await withAbsoluteDeadline(
          backend.stop(),
          15_000,
          "Disposable backend cleanup failed.",
        );
      }
    } catch {
      cleanupFailed = true;
    } finally {
      restoreEnvironment("BACKEND_URL", originalBackendUrl);
      restoreEnvironment("FRONTEND_BACKEND_API_KEY", originalInternalApiKey);
    }
  }
  if (cleanupFailed) throw new Error("Disposable protocol cleanup failed.");
}, 30_000);

beforeEach(() => {
  relay?.reset();
  authentication.isAuthenticated.mockReset();
  authentication.isAuthenticated.mockResolvedValue(true);
});

describe.sequential("pinned rclone production protocol contract", () => {
  it("proves the pinned client tolerates one denied parent probe and preserves state", async () => {
    const allHits: RelayHit[] = [];
    const root = await runCaptured(relay.origin, ["lsf", "gate:", "--max-depth=1"]);
    allHits.push(...root.hits);
    const rootEntries = lines(root.result.stdout);

    const readmeRange = await runCaptured(relay.origin, [
      "cat",
      "gate:README",
      "--offset=7",
      "--count=23",
    ]);
    allHits.push(...readmeRange.hits);

    const upload = await runCaptured(relay.origin, [
      "copyto",
      "--no-check-dest",
      "/fixture/input.nzb",
      `gate:nzbs/movies/${canary.names.upload}`,
    ], credential, "success", canary);
    allHits.push(...upload.hits);
    const uploadedState = await waitForDisposableProtocolSnapshot(
      backend,
      canary,
      null,
      queueStateIsPresent,
      10_000,
    );

    const queueList = await runCaptured(relay.origin, [
      "lsf",
      "gate:nzbs/movies",
      "--max-depth=1",
    ]);
    allHits.push(...queueList.hits);
    const queueRead = await runCaptured(relay.origin, [
      "cat",
      `gate:nzbs/movies/${canary.names.upload}`,
    ]);
    allHits.push(...queueRead.hits);
    const queueDelete = await runCaptured(relay.origin, [
      "deletefile",
      `gate:nzbs/movies/${canary.names.upload}`,
    ]);
    allHits.push(...queueDelete.hits);
    const deletedQueueState = await waitForQueueCleanup(canary, uploadedState.queue.id);

    const contentDelete = await runCaptured(relay.origin, [
      "deletefile",
      `gate:content/${canary.names.content}`,
    ]);
    allHits.push(...contentDelete.hits);
    const committedContentDeletion = await readDisposableProtocolSnapshot(backend, canary);
    const deletedContentState = await waitForDisposableProtocolSnapshot(
      backend,
      canary,
      null,
      (snapshot) => snapshot.content.row === false
        && snapshot.content.intent === false
        && snapshot.content.blob === false,
    );

    const completedBase = `completed-symlinks/movies/${canary.names.mount}`;
    const completedList = await runCaptured(relay.origin, [
      "--links",
      "lsf",
      `gate:${completedBase}`,
      "--max-depth=1",
    ]);
    allHits.push(...completedList.hits);
    const observedLine = lines(completedList.result.stdout)
      .find((entry) => entry.includes(canary.names.completed));
    const completedRemoteName = `${canary.names.completed}.rclonelink`;

    const completedCat = await runCaptured(relay.origin, [
      "--links",
      "cat",
      `gate:${completedBase}/${completedRemoteName}`,
    ]);
    allHits.push(...completedCat.hits);
    const completedDelete = await runCaptured(relay.origin, [
      "--links",
      "deletefile",
      `gate:${completedBase}/${completedRemoteName}`,
    ]);
    allHits.push(...completedDelete.hits);
    const completedState = await readDisposableProtocolSnapshot(backend, canary);

    const missing = await runCaptured(
      relay.origin,
      ["lsf", "gate:", "--max-depth=1"],
      null,
      "nonzero",
    );
    allHits.push(...missing.hits);
    const wrong = await runCaptured(
      relay.origin,
      ["lsf", "gate:", "--max-depth=1"],
      wrongCredential,
      "nonzero",
    );
    allHits.push(...wrong.hits);

    const summary = summarizeTrace(allHits);
    const deniedParentProbes = allHits.filter((hit) => hit.method === "MKCOL");

    expect({
      imageAndClient: [
        root.result.ok,
        readmeRange.result.ok,
        upload.result.ok,
        queueList.result.ok,
        queueRead.result.ok,
        queueDelete.result.ok,
        contentDelete.result.ok,
        completedList.result.ok,
        completedCat.result.ok,
        completedDelete.result.ok,
      ].every(Boolean),
      rootEntries: sameStrings(rootEntries, [
        ".ids/",
        "README",
        "completed-symlinks/",
        "content/",
        "nzbs/",
      ]),
      readmeRange: digest(readmeRange.result.stdout) === digest(readme.subarray(7, 30)),
      uploadState: queueStateIsPresent(uploadedState),
      queueListed: lines(queueList.result.stdout).includes(canary.names.upload),
      queueRead: digest(queueRead.result.stdout) === canary.uploadDigest,
      queueDeleteStatus: queueDelete.hits.some((hit) =>
        hit.method === "DELETE" && hit.status === 204),
      queueCleanup: queueStateIsAbsent(deletedQueueState),
      contentCleanup: deletedContentState.content.row === false
        && deletedContentState.content.intent === false
        && deletedContentState.content.blob === false,
      contentDeletionCommitted: !committedContentDeletion.content.row
        && (committedContentDeletion.content.intent || !committedContentDeletion.content.blob),
      completedListed: observedLine === completedRemoteName,
      completedTarget: digest(completedCat.result.stdout) === canary.completedTargetDigest,
      completedClaim: completedState.completed.row
        && completedState.completed.receiptState === 1
        && completedState.completed.historyRow
        && completedState.completed.receiptAdvanced
        && completedState.completed.receiptTerminalTimesNull
        && completedState.completed.blob,
      missingBasic: !missing.result.ok && missing.hits.some((hit) => hit.status === 401),
      wrongBasic: !wrong.result.ok && wrong.hits.some((hit) => hit.status === 401),
      contentDeleteStatus: contentDelete.hits.some((hit) =>
        hit.method === "DELETE" && hit.status === 200),
      completedDeleteStatus: completedDelete.hits.some((hit) =>
        hit.method === "DELETE" && hit.status === 204),
      deniedParentProbe: deniedParentProbes.length === 1
        && deniedParentProbes[0].status >= 400,
      onlyExpectedClientProbe: summary.forbidden === 1
        && summary.unapproved === 1
        && summary.operationShapes["MKCOL /nzbs/<segment>"] === 1,
    }).toEqual({
      imageAndClient: true,
      rootEntries: true,
      readmeRange: true,
      uploadState: true,
      queueListed: true,
      queueRead: true,
      queueDeleteStatus: true,
      queueCleanup: true,
      contentCleanup: true,
      contentDeletionCommitted: true,
      completedListed: true,
      completedTarget: true,
      completedClaim: true,
      missingBasic: true,
      wrongBasic: true,
      contentDeleteStatus: true,
      completedDeleteStatus: true,
      deniedParentProbe: true,
      onlyExpectedClientProbe: true,
    });
  }, 720_000);

  it("lists the exact root through mount-relative protocol ingress", async () => {
    const operation = await runCaptured(frontendProtocolUrl(), [
      "lsf",
      "gate:",
      "--max-depth=1",
    ]);
    expect({
      ok: operation.result.ok,
      entries: sameStrings(lines(operation.result.stdout), [
        ".ids/",
        "README",
        "completed-symlinks/",
        "content/",
        "nzbs/",
      ]),
      trace: summarizeTrace(operation.hits).unapproved === 0,
    }).toEqual({ ok: true, entries: true, trace: true });
  }, 90_000);

  it("reads an exact README offset and count through protocol ingress", async () => {
    const operation = await runCaptured(frontendProtocolUrl(), [
      "cat",
      "gate:README",
      "--offset=7",
      "--count=23",
    ]);
    expect({
      ok: operation.result.ok,
      bytes: digest(operation.result.stdout) === digest(readme.subarray(7, 30)),
      trace: summarizeTrace(operation.hits).unapproved === 0,
    }).toEqual({ ok: true, bytes: true, trace: true });
  }, 90_000);

  it("uploads, lists, removes, and eventually cleans one NZB through protocol ingress", async () => {
    const upload = await runCaptured(frontendProtocolUrl(), [
      "copyto",
      "--no-check-dest",
      "/fixture/input.nzb",
      `gate:nzbs/movies/${contract.names.upload}`,
    ], credential, "success", contract);
    const uploadedState = await waitForDisposableProtocolSnapshot(
      backend,
      contract,
      null,
      queueStateIsPresent,
      10_000,
    );
    const listing = await runCaptured(frontendProtocolUrl(), [
      "lsf",
      "gate:nzbs/movies",
      "--max-depth=1",
    ]);
    const content = await runCaptured(frontendProtocolUrl(), [
      "cat",
      `gate:nzbs/movies/${contract.names.upload}`,
    ]);
    const removal = await runCaptured(frontendProtocolUrl(), [
      "deletefile",
      `gate:nzbs/movies/${contract.names.upload}`,
    ]);
    const cleaned = await waitForQueueCleanup(contract, uploadedState.queue.id);
    const hits = [...upload.hits, ...listing.hits, ...content.hits, ...removal.hits];

    expect({
      commands: upload.result.ok && listing.result.ok && content.result.ok && removal.result.ok,
      accepted: queueStateIsPresent(uploadedState),
      listed: lines(listing.result.stdout).includes(contract.names.upload),
      bytes: digest(content.result.stdout) === contract.uploadDigest,
      status: removal.hits.some((hit) => hit.method === "DELETE" && hit.status === 204),
      cleaned: queueStateIsAbsent(cleaned),
      trace: summarizeTrace(hits).unapproved === 0,
    }).toEqual({
      commands: true,
      accepted: true,
      listed: true,
      bytes: true,
      status: true,
      cleaned: true,
      trace: true,
    });
  }, 300_000);

  it("returns 200 and eventually cleans operator-enabled content through protocol ingress", async () => {
    const operation = await runCaptured(frontendProtocolUrl(), [
      "deletefile",
      `gate:content/${contract.names.content}`,
    ]);
    const committed = await readDisposableProtocolSnapshot(backend, contract);
    const cleaned = await waitForDisposableProtocolSnapshot(
      backend,
      contract,
      null,
      (snapshot) => snapshot.content.row === false
        && snapshot.content.intent === false
        && snapshot.content.blob === false,
    );
    expect({
      ok: operation.result.ok,
      status: operation.hits.some((hit) => hit.method === "DELETE" && hit.status === 200),
      committed: committed.content.row === false
        && (committed.content.intent === true || committed.content.blob === false),
      row: cleaned.content.row,
      intent: cleaned.content.intent,
      blob: cleaned.content.blob,
      trace: summarizeTrace(operation.hits).unapproved === 0,
    }).toEqual({
      ok: true,
      status: true,
      committed: true,
      row: false,
      intent: false,
      blob: false,
      trace: true,
    });
  }, 135_000);

  it("lists, reads, and acknowledges a completed link without deleting content", async () => {
    const base = `completed-symlinks/movies/${contract.names.mount}`;
    const remoteName = `${contract.names.completed}.rclonelink`;
    const listing = await runCaptured(frontendProtocolUrl(), [
      "--links",
      "lsf",
      `gate:${base}`,
      "--max-depth=1",
    ]);
    const content = await runCaptured(frontendProtocolUrl(), [
      "--links",
      "cat",
      `gate:${base}/${remoteName}`,
    ]);
    const removal = await runCaptured(frontendProtocolUrl(), [
      "--links",
      "deletefile",
      `gate:${base}/${remoteName}`,
    ]);
    const state = await readDisposableProtocolSnapshot(backend, contract);
    const hits = [...listing.hits, ...content.hits, ...removal.hits];

    expect({
      commands: listing.result.ok && content.result.ok && removal.result.ok,
      listed: lines(listing.result.stdout).includes(remoteName),
      bytes: digest(content.result.stdout) === contract.completedTargetDigest,
      status: removal.hits.some((hit) => hit.method === "DELETE" && hit.status === 204),
      receipt: state.completed.receiptState,
      history: state.completed.historyRow,
      receiptAdvanced: state.completed.receiptAdvanced,
      terminalTimesNull: state.completed.receiptTerminalTimesNull,
      row: state.completed.row,
      blob: state.completed.blob,
      trace: summarizeTrace(hits).unapproved === 0,
    }).toEqual({
      commands: true,
      listed: true,
      bytes: true,
      status: true,
      receipt: 1,
      history: true,
      receiptAdvanced: true,
      terminalTimesNull: true,
      row: true,
      blob: true,
      trace: true,
    });
  }, 180_000);

  it("preserves backend 401 for missing and wrong WebDAV Basic credentials", async () => {
    const missing = await runCaptured(
      frontendProtocolUrl(),
      ["lsf", "gate:", "--max-depth=1"],
      null,
      "nonzero",
    );
    const wrong = await runCaptured(
      frontendProtocolUrl(),
      ["lsf", "gate:", "--max-depth=1"],
      wrongCredential,
      "nonzero",
    );
    expect({
      missingNonzero: !missing.result.ok,
      missing401: missing.hits.some((hit) => hit.status === 401),
      wrongNonzero: !wrong.result.ok,
      wrong401: wrong.hits.some((hit) => hit.status === 401),
      trace: summarizeTrace([...missing.hits, ...wrong.hits]).unapproved === 0,
    }).toEqual({
      missingNonzero: true,
      missing401: true,
      wrongNonzero: true,
      wrong401: true,
      trace: true,
    });
  }, 135_000);
});

async function runCaptured(
  remoteUrl: string,
  args: readonly string[],
  selectedCredential: RcloneCredential | null | undefined = credential,
  expectedOutcome: "success" | "nonzero" = "success",
  uploadSeed?: DisposableProtocolSeed,
): Promise<CapturedRclone> {
  relay.reset();
  const result = await runPinnedRclone({
    remoteUrl,
    args,
    credential: selectedCredential ?? undefined,
    fixtureConfigPath: uploadSeed ? backend.configPath : undefined,
    uploadPath: uploadSeed?.uploadPath,
  });
  await eventLoopTurn();
  const hits = relay.hits.map((hit) => Object.freeze({ ...hit }));
  const overflowed = relay.didOverflow();
  relay.reset();
  if (overflowed) throw new Error("Disposable protocol trace exceeded its bounded capture.");
  if (
    result.outcome !== expectedOutcome
    || result.ok !== (expectedOutcome === "success")
  ) throw new Error("Disposable rclone command returned an unexpected outcome.");
  return Object.freeze({ result, hits });
}

async function waitForQueueCleanup(
  seed: DisposableProtocolSeed,
  queueId: string | null,
): Promise<DisposableProtocolSnapshot> {
  return await waitForDisposableProtocolSnapshot(
    backend,
    seed,
    queueId,
    (snapshot) => queueStateIsAbsent(snapshot),
  );
}

function queueStateIsPresent(snapshot: DisposableProtocolSnapshot): boolean {
  return snapshot.queue.id !== null
    && snapshot.queue.row === true
    && snapshot.queue.name === true
    && snapshot.queue.blob === true
    && snapshot.queue.intent === false;
}

function queueStateIsAbsent(snapshot: DisposableProtocolSnapshot): boolean {
  return snapshot.queue.row === false
    && snapshot.queue.contents === false
    && snapshot.queue.name === false
    && snapshot.queue.intent === false
    && snapshot.queue.blob === false;
}

function summarizeTrace(hits: readonly RelayHit[]): TraceSummary {
  const methods: Record<string, number> = {};
  const statuses: Record<string, number> = {};
  const authorizations: Record<string, number> = {};
  const operationShapes: Record<string, number> = {};
  const operationStatuses: Record<string, number> = {};
  let forbidden = 0;
  let unapproved = 0;
  for (const hit of hits) {
    methods[hit.method] = (methods[hit.method] ?? 0) + 1;
    statuses[String(hit.status)] = (statuses[String(hit.status)] ?? 0) + 1;
    authorizations[hit.authorization] = (authorizations[hit.authorization] ?? 0) + 1;
    const operationShape = `${hit.method} ${tracePathShape(hit.pathname)}`;
    operationShapes[operationShape] = (operationShapes[operationShape] ?? 0) + 1;
    const operationStatus = `${operationShape} ${hit.status}`;
    operationStatuses[operationStatus] = (operationStatuses[operationStatus] ?? 0) + 1;
    if (forbiddenMethods.has(hit.method)) forbidden += 1;
    if (!isApprovedHit(hit)) unapproved += 1;
  }
  return Object.freeze({
    methods,
    statuses,
    authorizations,
    operationShapes,
    operationStatuses,
    forbidden,
    unapproved,
  });
}

function tracePathShape(pathname: string): string {
  const segments = pathname.split("/").filter(Boolean);
  if (segments.length === 0) return "/";
  if (segments[0] === "README") return "/README";
  if ([".ids", "nzbs", "content", "completed-symlinks"].includes(segments[0])) {
    return `/${segments[0]}${segments.slice(1).map(() => "/<segment>").join("")}`;
  }
  return `/<other>${segments.slice(1).map(() => "/<segment>").join("")}`;
}

function diagnosticClasses(diagnostics: string): string[] {
  return [...new Set(diagnostics.match(/[A-Za-z][A-Za-z0-9.]*Exception/gu) ?? [])].sort();
}

function isApprovedHit(hit: RelayHit): boolean {
  const segments = hit.pathname.split("/").filter(Boolean);
  const root = segments[0];
  if (["GET", "HEAD", "OPTIONS", "PROPFIND"].includes(hit.method)) {
    return hit.pathname === "/"
      || hit.pathname === "/README"
      || root === ".ids"
      || root === "nzbs"
      || root === "content"
      || root === "completed-symlinks";
  }
  if (hit.method === "PUT") {
    return root === "nzbs" && segments.length === 3;
  }
  if (hit.method === "DELETE") {
    return (root === "nzbs" && segments.length === 3)
      || ((root === "content" || root === "completed-symlinks") && segments.length >= 2);
  }
  return false;
}

function lines(value: Buffer): string[] {
  return value.toString("utf8").split(/\r?\n/u).filter((line) => line.length > 0);
}

function sameStrings(actual: readonly string[], expected: readonly string[]): boolean {
  return JSON.stringify([...actual].sort()) === JSON.stringify([...expected].sort());
}

function frontendProtocolUrl(): string {
  return `${frontend.origin}/nzbdav/protocol`;
}

async function startCountingRelay(target: DisposableAspNetBackend): Promise<RelayFixture> {
  const hits: RelayHit[] = [];
  let overflow = false;
  let attemptCount = 0;
  const validBasic = `Basic ${Buffer.from(
    `${target.credentials.webDavUsername}:${target.credentials.webDavPassword}`,
    "utf8",
  ).toString("base64")}`;
  const server = trackServer(http.createServer((request, response) => {
    attemptCount += 1;
    if (attemptCount > 512) {
      overflow = true;
      request.destroy();
      response.destroy();
      return;
    }
    const parsed = new URL(request.url ?? "/", target.origin);
    const hit: RelayHit = {
      method: request.method ?? "",
      pathname: parsed.pathname,
      status: 0,
      authorization: classifyAuthorization(
        request.headers.authorization,
        validBasic,
        target.credentials.webDavUsername,
        target.credentials.webDavPassword,
      ),
    };
    hits.push(hit);
    response.once("finish", () => {
      hit.status = response.statusCode;
    });
    relayHttpRequestBounded(request, response, target.origin, {
      maxResponseBytes: 4 * 1024 * 1024,
      timeoutMs: 15_000,
    });
  }));
  return Object.freeze({
    origin: await listenLoopback(server),
    server,
    hits,
    didOverflow: () => overflow,
    reset: () => {
      hits.length = 0;
      overflow = false;
      attemptCount = 0;
    },
  });
}

function trackServer(server: Server): Server {
  ownedServers.push(server);
  return server;
}

function classifyAuthorization(
  value: string | undefined,
  validBasic: string,
  validUsername: string,
  validPassword: string,
): RelayHit["authorization"] {
  if (value === undefined) return "missing";
  if (sameSecret(value, validBasic)) return "valid";
  if (!value.startsWith("Basic ")) return "other-user";
  let decoded: string;
  try {
    decoded = Buffer.from(value.slice("Basic ".length), "base64").toString("utf8");
  } catch {
    return "other-user";
  }
  const separator = decoded.indexOf(":");
  if (separator < 0) return "other-user";
  const username = decoded.slice(0, separator);
  const password = decoded.slice(separator + 1);
  return sameSecret(username, validUsername) && !sameSecret(password, validPassword)
    ? "valid-user-other-password"
    : "other-user";
}

function sameSecret(leftValue: string, rightValue: string): boolean {
  const left = createHash("sha256").update(leftValue).digest();
  const right = createHash("sha256").update(rightValue).digest();
  return timingSafeEqual(left, right);
}

function listenLoopback(server: Server): Promise<string> {
  return new Promise((resolve, reject) => {
    const fail = () => reject(new Error("Disposable protocol listener failed."));
    server.once("error", fail);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", fail);
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("Disposable protocol listener failed."));
        return;
      }
      resolve(`http://127.0.0.1:${address.port}`);
    });
  });
}

function eventLoopTurn(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve));
}

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}
