import { spawn } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { lstat, realpath } from "node:fs/promises";
import { dirname, isAbsolute, join, relative } from "node:path";
import type { DisposableAspNetBackend } from "./disposable-aspnet-backend";
import { requestLoopbackBounded } from "./bounded-http";

export const PINNED_RCLONE_IMAGE =
  "rclone/rclone@sha256:c61954aaa32328a5486715dd063a81c7879f5195ad3505cd362deddd509dc4a1";
const PINNED_RCLONE_VERSION = "1.74.4";
const PINNED_RCLONE_REVISION = "5bc93a2a7ab0ebd0a11352bc4968eabeffb18027";
const PROCESS_TIMEOUT_MS = 30_000;
const PROCESS_FORCE_SETTLE_MS = 5_000;
const PROCESS_OUTPUT_LIMIT = 1024 * 1024;
const DATABASE_TIMEOUT_MS = 10_000;
const CONTAINER_ABSENCE_TIMEOUT_MS = 10_000;
const CONTAINER_PREFIX = "pinrail-task2b-rclone-";
const OWNED_CONTAINER_PATTERN = /^pinrail-task2b-rclone-\d+-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
const LOCAL_DOCKER_SOCKET = "/var/run/docker.sock";
const LOCAL_DOCKER_HOST = `unix://${LOCAL_DOCKER_SOCKET}`;
const SAFE_PROCESS_ERROR = "Disposable protocol process failed.";
const SAFE_DATABASE_ERROR = "Disposable protocol database operation failed.";
const SAFE_SETUP_ERROR = "Disposable protocol setup failed.";

const MINIMAL_NZB = `<?xml version="1.0" encoding="utf-8"?>
<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
  <file poster="poster" date="1" subject="Example">
    <segments>
      <segment bytes="10" number="1">segment-1</segment>
    </segments>
  </file>
</nzb>
`;

const DATABASE_PROGRAM = String.raw`
import base64
import hashlib
import json
import os
import sqlite3
import sys

payload = json.load(sys.stdin)
config_path = os.path.realpath(payload["configPath"])
database_path = os.path.realpath(payload["databasePath"])

def require_descendant(path):
    resolved = os.path.realpath(path)
    if os.path.commonpath([config_path, resolved]) != config_path:
        raise RuntimeError("outside fixture")
    return resolved

require_descendant(database_path)

def blob_path(blob_id):
    file_name = blob_id.lower()
    compact = file_name.replace("-", "")
    return require_descendant(os.path.join(
        config_path, "blobs", compact[:2], compact[2:4], file_name))

def db_guid(value):
    return value.upper() if value is not None else None

def write_fixture_file(path, encoded):
    path = require_descendant(path)
    os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
    with open(path, "xb") as stream:
        stream.write(base64.b64decode(encoded, validate=True))

connection = sqlite3.connect(database_path, timeout=5, isolation_level=None)
try:
    connection.execute("PRAGMA foreign_keys=ON")
    connection.execute("PRAGMA busy_timeout=5000")
    action = payload["action"]
    if action == "seed":
        connection.execute("BEGIN IMMEDIATE")
        try:
            ids = payload["ids"]
            db_ids = {key: db_guid(value) for key, value in ids.items()}
            names = payload["names"]
            created_at = payload["createdAt"]
            ticks = payload["ticks"]
            content_root = "00000000-0000-0000-0000-000000000002"
            root_count = connection.execute(
                "SELECT COUNT(*) FROM DavItems WHERE Id = ?", (content_root,)).fetchone()[0]
            if root_count != 1:
                raise RuntimeError("missing root")

            category = connection.execute(
                "SELECT Id FROM DavItems WHERE ParentId = ? AND Name = ?",
                (content_root, "movies")).fetchone()
            category_id = category[0] if category else db_ids["category"]
            if category is None:
                connection.execute(
                    """INSERT INTO DavItems
                    (Id, CreatedAt, FileBlobId, FileSize, HistoryItemId, IdPrefix,
                     LastHealthCheck, Name, NextHealthCheck, ParentId, Path,
                     ReleaseDate, SubType, Type, NzbBlobId)
                    VALUES (?, ?, NULL, NULL, NULL, ?, NULL, ?, NULL, ?, ?, NULL, 101, 1, NULL)""",
                    (category_id, created_at, ids["category"][:5], "movies", content_root, "/content/movies"))

            connection.execute(
                """INSERT INTO DavItems
                (Id, CreatedAt, FileBlobId, FileSize, HistoryItemId, IdPrefix,
                 LastHealthCheck, Name, NextHealthCheck, ParentId, Path,
                 ReleaseDate, SubType, Type, NzbBlobId)
                VALUES (?, ?, ?, ?, ?, ?, NULL, ?, NULL, ?, ?, NULL, 203, 2, NULL)""",
                (db_ids["content"], created_at, db_ids["contentBlob"], payload["contentSize"],
                 db_ids["history"], ids["content"][:5], names["content"], content_root,
                 "/content/" + names["content"]))

            connection.execute(
                """INSERT INTO DavItems
                (Id, CreatedAt, FileBlobId, FileSize, HistoryItemId, IdPrefix,
                 LastHealthCheck, Name, NextHealthCheck, ParentId, Path,
                 ReleaseDate, SubType, Type, NzbBlobId)
                VALUES (?, ?, NULL, NULL, ?, ?, NULL, ?, NULL, ?, ?, NULL, 101, 1, NULL)""",
                (db_ids["mount"], created_at, db_ids["history"], ids["mount"][:5], names["mount"],
                 category_id, "/content/movies/" + names["mount"]))

            completed_path = "/content/movies/" + names["mount"] + "/" + names["completed"]
            connection.execute(
                """INSERT INTO DavItems
                (Id, CreatedAt, FileBlobId, FileSize, HistoryItemId, IdPrefix,
                 LastHealthCheck, Name, NextHealthCheck, ParentId, Path,
                 ReleaseDate, SubType, Type, NzbBlobId)
                VALUES (?, ?, ?, ?, ?, ?, NULL, ?, NULL, ?, ?, NULL, 203, 2, NULL)""",
                (db_ids["completed"], created_at, db_ids["completedBlob"], payload["completedSize"],
                 db_ids["history"], ids["completed"][:5], names["completed"], db_ids["mount"],
                 completed_path))

            connection.execute(
                """INSERT INTO HistoryItems
                (Id, CreatedAt, FileName, JobName, Category, DownloadStatus,
                 TotalSegmentBytes, DownloadTimeSeconds, FailMessage, DownloadDirId, NzbBlobId)
                VALUES (?, ?, ?, ?, 'movies', 1, ?, 1, NULL, ?, NULL)""",
                (db_ids["history"], created_at, names["history"], names["mount"],
                 payload["completedSize"], db_ids["mount"]))
            connection.execute(
                """INSERT INTO ImportReceipts
                (Id, DavItemId, HistoryItemId, State, CreatedAt, UpdatedAt,
                 ImportedAt, RemovedAt, Detail)
                VALUES (?, ?, ?, 0, ?, ?, NULL, NULL, NULL)""",
                (db_ids["receipt"], db_ids["completed"], db_ids["history"], ticks, ticks))
            connection.commit()
        except BaseException:
            connection.rollback()
            raise

        write_fixture_file(blob_path(ids["contentBlob"]), payload["contentBytes"])
        write_fixture_file(blob_path(ids["completedBlob"]), payload["completedBytes"])
        write_fixture_file(payload["uploadPath"], payload["uploadBytes"])
        print(json.dumps({"categoryId": category_id}, separators=(",", ":")))
    elif action == "snapshot":
        ids = payload["ids"]
        db_ids = {key: db_guid(value) for key, value in ids.items()}
        names = payload["names"]
        queue_id = db_guid(payload.get("queueId"))
        if queue_id is None:
            row = connection.execute(
                "SELECT Id FROM QueueItems WHERE Category = ? AND FileName = ?",
                ("movies", names["upload"])).fetchone()
            queue_id = row[0] if row else None

        def exists(sql, parameters):
            return connection.execute(sql, parameters).fetchone()[0] != 0

        queue_row = queue_id is not None and exists(
            "SELECT COUNT(*) FROM QueueItems WHERE Id = ?", (queue_id,))
        queue_contents = queue_id is not None and exists(
            "SELECT COUNT(*) FROM QueueNzbContents WHERE Id = ?", (queue_id,))
        queue_name = queue_id is not None and exists(
            "SELECT COUNT(*) FROM NzbNames WHERE Id = ? AND FileName = ?",
            (queue_id, names["upload"]))
        queue_intent = queue_id is not None and exists(
            "SELECT COUNT(*) FROM NzbBlobCleanupItems WHERE Id = ?", (queue_id,))

        receipt = connection.execute(
            """SELECT State, CreatedAt, UpdatedAt, ImportedAt, RemovedAt
            FROM ImportReceipts WHERE Id = ?""", (db_ids["receipt"],)).fetchone()
        result = {
            "content": {
                "row": exists("SELECT COUNT(*) FROM DavItems WHERE Id = ?", (db_ids["content"],)),
                "intent": exists("SELECT COUNT(*) FROM BlobCleanupItems WHERE Id = ?", (db_ids["contentBlob"],)),
                "blob": os.path.isfile(blob_path(ids["contentBlob"])),
            },
            "completed": {
                "row": exists("SELECT COUNT(*) FROM DavItems WHERE Id = ?", (db_ids["completed"],)),
                "receiptState": receipt[0] if receipt else None,
                "historyRow": exists(
                    "SELECT COUNT(*) FROM HistoryItems WHERE Id = ?", (db_ids["history"],)),
                "receiptAdvanced": bool(receipt and receipt[2] > receipt[1]),
                "receiptTerminalTimesNull": bool(
                    receipt and receipt[3] is None and receipt[4] is None),
                "blob": os.path.isfile(blob_path(ids["completedBlob"])),
            },
            "queue": {
                "id": queue_id,
                "row": bool(queue_row),
                "contents": bool(queue_contents),
                "name": bool(queue_name),
                "intent": bool(queue_intent),
                "blob": queue_id is not None and os.path.isfile(blob_path(queue_id)),
            },
        }
        print(json.dumps(result, separators=(",", ":")))
    else:
        raise RuntimeError("unknown action")
finally:
    connection.close()
`;

export type DisposableProtocolSeed = Readonly<{
  tag: string;
  names: Readonly<{
    content: string;
    mount: string;
    completed: string;
    history: string;
    upload: string;
  }>;
  ids: Readonly<{
    category: string;
    content: string;
    contentBlob: string;
    mount: string;
    completed: string;
    completedBlob: string;
    history: string;
    receipt: string;
  }>;
  uploadPath: string;
  uploadDigest: string;
  completedTargetDigest: string;
}>;

export type DisposableProtocolSnapshot = Readonly<{
  content: Readonly<{ row: boolean; intent: boolean; blob: boolean }>;
  completed: Readonly<{
    row: boolean;
    receiptState: number | null;
    historyRow: boolean;
    receiptAdvanced: boolean;
    receiptTerminalTimesNull: boolean;
    blob: boolean;
  }>;
  queue: Readonly<{
    id: string | null;
    row: boolean;
    contents: boolean;
    name: boolean;
    intent: boolean;
    blob: boolean;
  }>;
}>;

export type RcloneCredential = Readonly<{
  username: string;
  obscuredPassword: string;
}>;

export type RcloneResult = Readonly<{
  ok: boolean;
  outcome: "success" | "nonzero" | "timeout" | "output-limit" | "spawn-error" | "cleanup-error";
  stdout: Buffer;
}>;

type ProcessResult = Readonly<{
  code: number | null;
  outcome: RcloneResult["outcome"];
  stdout: Buffer;
}>;

export async function configureWritablePausedProtocolBackend(
  backend: DisposableAspNetBackend,
): Promise<void> {
  const writableBody = new URLSearchParams([
    ["webdav.enforce-readonly", "false"],
  ]).toString();
  const writable = await requestLoopbackBounded(
    backend.origin,
    "/api/update-config",
    {
      method: "POST",
      headers: {
        "content-length": Buffer.byteLength(writableBody),
        "content-type": "application/x-www-form-urlencoded",
        "x-api-key": backend.credentials.internalApiKey,
      },
      body: writableBody,
    },
  );
  if (writable.status !== 200) throw new Error(SAFE_SETUP_ERROR);

  const paused = await requestLoopbackBounded(
    backend.origin,
    "/api?mode=pause",
    { headers: { "x-api-key": backend.credentials.publicApiKey } },
  );
  if (paused.status !== 200) throw new Error(SAFE_SETUP_ERROR);
}

export async function createDisposableProtocolSeed(
  backend: DisposableAspNetBackend,
  tag: string,
): Promise<DisposableProtocolSeed> {
  if (!/^[a-z][a-z0-9-]{0,31}$/u.test(tag)) throw new Error(SAFE_SETUP_ERROR);
  const ids = Object.freeze({
    category: randomUUID(),
    content: randomUUID(),
    contentBlob: randomUUID(),
    mount: randomUUID(),
    completed: randomUUID(),
    completedBlob: randomUUID(),
    history: randomUUID(),
    receipt: randomUUID(),
  });
  const names = Object.freeze({
    content: `${tag}-delete.bin`,
    mount: `${tag}-completed`,
    completed: `${tag}-link.bin`,
    history: `${tag}.nzb`,
    upload: `${tag}-upload.nzb`,
  });
  const uploadPath = join(backend.configPath, "protocol-client", names.upload);
  const contentBytes = Buffer.from(`content-${tag}`, "utf8");
  const completedBytes = Buffer.from(`completed-${tag}`, "utf8");
  const completedTarget = Buffer.from(join(
    dirname(backend.configPath),
    "mount",
    ".ids",
    ...ids.completed.slice(0, 5),
    ids.completed,
  ), "utf8");
  const now = new Date();
  const payload = {
    action: "seed",
    configPath: backend.configPath,
    databasePath: backend.databasePath,
    ids,
    names,
    createdAt: now.toISOString().replace("T", " ").replace("Z", ""),
    ticks: BigInt(now.getTime()) * 10_000n + 621_355_968_000_000_000n,
    contentBytes: contentBytes.toString("base64"),
    completedBytes: completedBytes.toString("base64"),
    uploadBytes: Buffer.from(MINIMAL_NZB, "utf8").toString("base64"),
    contentSize: contentBytes.byteLength,
    completedSize: completedBytes.byteLength,
    uploadPath,
  };
  await runDatabase(payload);
  return Object.freeze({
    tag,
    ids,
    names,
    uploadPath,
    uploadDigest: digest(MINIMAL_NZB),
    completedTargetDigest: digest(completedTarget),
  });
}

export async function readDisposableProtocolSnapshot(
  backend: DisposableAspNetBackend,
  seed: DisposableProtocolSeed,
  queueId?: string | null,
): Promise<DisposableProtocolSnapshot> {
  const result = await runDatabase({
    action: "snapshot",
    configPath: backend.configPath,
    databasePath: backend.databasePath,
    ids: seed.ids,
    names: seed.names,
    queueId: queueId ?? null,
  });
  return parseDisposableProtocolSnapshot(result);
}

export async function waitForDisposableProtocolSnapshot(
  backend: DisposableAspNetBackend,
  seed: DisposableProtocolSeed,
  queueId: string | null | undefined,
  predicate: (snapshot: DisposableProtocolSnapshot) => boolean,
  timeoutMs = 25_000,
): Promise<DisposableProtocolSnapshot> {
  const deadline = Date.now() + timeoutMs;
  let snapshot = await readDisposableProtocolSnapshot(backend, seed, queueId);
  while (!predicate(snapshot) && Date.now() < deadline) {
    await delay(250);
    snapshot = await readDisposableProtocolSnapshot(backend, seed, queueId);
  }
  return snapshot;
}

export async function validatePinnedRcloneImage(): Promise<void> {
  await assertLocalDockerSocket();
  const result = await runProcess(
    "docker",
    dockerArgs(["image", "inspect", "--format", "{{json .Config.Labels}}", PINNED_RCLONE_IMAGE]),
    { environment: localDockerEnvironment(), timeoutMs: 10_000 },
  );
  if (result.code !== 0 || result.outcome !== "success") throw new Error(SAFE_SETUP_ERROR);
  try {
    const labels = JSON.parse(result.stdout.toString("utf8")) as Record<string, unknown>;
    if (
      labels["org.opencontainers.image.version"] !== PINNED_RCLONE_VERSION
      || labels["org.opencontainers.image.revision"] !== PINNED_RCLONE_REVISION
    ) throw new Error(SAFE_SETUP_ERROR);
  } catch {
    throw new Error(SAFE_SETUP_ERROR);
  }
}

function parseDisposableProtocolSnapshot(value: unknown): DisposableProtocolSnapshot {
  if (!isRecord(value) || !hasExactKeys(value, ["completed", "content", "queue"])) {
    throw new Error(SAFE_DATABASE_ERROR);
  }
  const content = value.content;
  const completed = value.completed;
  const queue = value.queue;
  if (
    !isRecord(content)
    || !hasExactKeys(content, ["blob", "intent", "row"])
    || !areBooleans(content, ["blob", "intent", "row"])
    || !isRecord(completed)
    || !hasExactKeys(completed, [
      "blob",
      "historyRow",
      "receiptAdvanced",
      "receiptState",
      "receiptTerminalTimesNull",
      "row",
    ])
    || !areBooleans(completed, [
      "blob",
      "historyRow",
      "receiptAdvanced",
      "receiptTerminalTimesNull",
      "row",
    ])
    || !(completed.receiptState === null || Number.isSafeInteger(completed.receiptState))
    || !isRecord(queue)
    || !hasExactKeys(queue, ["blob", "contents", "id", "intent", "name", "row"])
    || !areBooleans(queue, ["blob", "contents", "intent", "name", "row"])
    || !(queue.id === null || typeof queue.id === "string")
  ) {
    throw new Error(SAFE_DATABASE_ERROR);
  }
  return Object.freeze({
    content: Object.freeze({
      row: content.row,
      intent: content.intent,
      blob: content.blob,
    }),
    completed: Object.freeze({
      row: completed.row,
      receiptState: completed.receiptState,
      historyRow: completed.historyRow,
      receiptAdvanced: completed.receiptAdvanced,
      receiptTerminalTimesNull: completed.receiptTerminalTimesNull,
      blob: completed.blob,
    }),
    queue: Object.freeze({
      id: queue.id,
      row: queue.row,
      contents: queue.contents,
      name: queue.name,
      intent: queue.intent,
      blob: queue.blob,
    }),
  }) as DisposableProtocolSnapshot;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  return actual.length === expected.length
    && actual.every((key, index) => key === expected[index]);
}

function areBooleans(value: Record<string, unknown>, keys: readonly string[]): boolean {
  return keys.every((key) => typeof value[key] === "boolean");
}

export async function obscureRclonePassword(password: string): Promise<string> {
  const result = await runContainer(["obscure", "-"], {
    interactive: true,
    input: Buffer.from(password, "utf8"),
  });
  const obscured = result.stdout.toString("utf8").trim();
  if (
    result.outcome !== "success"
    || result.code !== 0
    || obscured.length < 16
    || obscured.length > 2048
    || /\s/u.test(obscured)
  ) throw new Error(SAFE_SETUP_ERROR);
  return obscured;
}

export async function runPinnedRclone(options: Readonly<{
  remoteUrl: string;
  args: readonly string[];
  credential?: RcloneCredential;
  fixtureConfigPath?: string;
  uploadPath?: string;
}>): Promise<RcloneResult> {
  assertLoopbackRemote(options.remoteUrl);
  const remoteEnvironment: NodeJS.ProcessEnv = {
    RCLONE_CONFIG: "/dev/null",
    RCLONE_CONFIG_GATE_TYPE: "webdav",
    RCLONE_CONFIG_GATE_URL: options.remoteUrl,
    RCLONE_CONFIG_GATE_VENDOR: "other",
  };
  if (options.credential) {
    remoteEnvironment.RCLONE_CONFIG_GATE_USER = options.credential.username;
    remoteEnvironment.RCLONE_CONFIG_GATE_PASS = options.credential.obscuredPassword;
  }
  const mounts: string[] = [];
  if (options.uploadPath !== undefined) {
    if (!options.fixtureConfigPath) throw new Error(SAFE_SETUP_ERROR);
    const [root, upload] = await Promise.all([
      realpath(options.fixtureConfigPath),
      realpath(options.uploadPath),
    ]);
    const nested = relative(root, upload);
    const metadata = await lstat(upload);
    if (
      nested === ""
      || nested.startsWith("..")
      || isAbsolute(nested)
      || !metadata.isFile()
      || metadata.isSymbolicLink()
      || upload.includes(",")
      || upload.includes("\n")
    ) throw new Error(SAFE_SETUP_ERROR);
    mounts.push(`--mount=type=bind,src=${upload},dst=/fixture/input.nzb,readonly`);
  }
  const globals = [
    "--contimeout=5s",
    "--timeout=10s",
    "--retries=1",
    "--low-level-retries=1",
  ];
  const result = await runContainer([...globals, ...options.args], {
    environment: remoteEnvironment,
    mounts,
  });
  return Object.freeze({
    ok: result.outcome === "success" && result.code === 0,
    outcome: result.outcome,
    stdout: result.outcome === "success" && result.code === 0
      ? result.stdout
      : Buffer.alloc(0),
  });
}

export function digest(value: Buffer | string): string {
  return createHash("sha256").update(value).digest("hex");
}

async function runDatabase(payload: Record<string, unknown>): Promise<unknown> {
  const serialized = JSON.stringify(payload, (_key, value) =>
    typeof value === "bigint" ? value.toString() : value);
  const result = await runProcess(
    "/usr/bin/python3",
    ["-I", "-c", DATABASE_PROGRAM],
    { input: Buffer.from(serialized, "utf8"), timeoutMs: DATABASE_TIMEOUT_MS },
  );
  if (result.code !== 0 || result.outcome !== "success") throw new Error(SAFE_DATABASE_ERROR);
  try {
    return JSON.parse(result.stdout.toString("utf8")) as unknown;
  } catch {
    throw new Error(SAFE_DATABASE_ERROR);
  }
}

async function runContainer(
  commandArgs: readonly string[],
  options: Readonly<{
    environment?: NodeJS.ProcessEnv;
    input?: Buffer;
    interactive?: boolean;
    mounts?: readonly string[];
  }> = {},
): Promise<ProcessResult> {
  await assertLocalDockerSocket();
  const name = `${CONTAINER_PREFIX}${process.pid}-${randomUUID()}`;
  const environmentNames = Object.keys(options.environment ?? {}).sort();
  const args = dockerArgs([
    "run",
    "--rm",
    "--pull=never",
    "--network=host",
    "--name",
    name,
    ...(options.interactive ? ["--interactive"] : []),
    ...(options.mounts ?? []),
    ...environmentNames.flatMap((key) => ["-e", key]),
    PINNED_RCLONE_IMAGE,
    ...commandArgs,
  ]);
  const result = await runProcess("docker", args, {
    containerName: name,
    environment: localDockerEnvironment(options.environment),
    input: options.input,
    timeoutMs: PROCESS_TIMEOUT_MS,
  });
  await assertContainerAbsent(name);
  return result;
}

async function runProcess(
  command: string,
  args: readonly string[],
  options: Readonly<{
    containerName?: string;
    environment?: NodeJS.ProcessEnv;
    input?: Buffer;
    timeoutMs?: number;
  }> = {},
): Promise<ProcessResult> {
  return await new Promise<ProcessResult>((resolve) => {
    const child = spawn(command, [...args], {
      env: options.environment ?? process.env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    const chunks: Buffer[] = [];
    let bytes = 0;
    let settled = false;
    let cleanupFailed = false;
    let forcedOutcome: ProcessResult["outcome"] | undefined;
    let forceDeadline: NodeJS.Timeout | undefined;
    const settle = (
      code: number | null,
      defaultOutcome: ProcessResult["outcome"],
    ) => {
      if (settled) return;
      settled = true;
      clearTimeout(deadline);
      if (forceDeadline) clearTimeout(forceDeadline);
      resolve(Object.freeze({
        code,
        outcome: cleanupFailed
          ? "cleanup-error"
          : forcedOutcome ?? defaultOutcome,
        stdout: forcedOutcome ? Buffer.alloc(0) : Buffer.concat(chunks, bytes),
      }));
    };
    const stop = (outcome: ProcessResult["outcome"]) => {
      if (forcedOutcome) return;
      forcedOutcome = outcome;
      forceDeadline = setTimeout(() => {
        if (options.containerName) cleanupFailed = true;
        child.kill("SIGKILL");
        child.stdin.destroy();
        child.stdout.destroy();
        child.stderr.destroy();
        child.unref();
        settle(null, outcome);
      }, PROCESS_FORCE_SETTLE_MS);
      forceDeadline.unref();
      if (options.containerName) {
        void removeOwnedContainer(options.containerName)
          .catch(() => {
            cleanupFailed = true;
          })
          .finally(() => child.kill("SIGKILL"));
      } else {
        child.kill("SIGKILL");
      }
    };
    const deadline = setTimeout(() => stop("timeout"), options.timeoutMs ?? PROCESS_TIMEOUT_MS);
    deadline.unref();
    child.stdout.on("data", (chunk: Buffer) => {
      if (forcedOutcome) return;
      bytes += chunk.byteLength;
      if (bytes > PROCESS_OUTPUT_LIMIT) {
        stop("output-limit");
        return;
      }
      chunks.push(chunk);
    });
    child.stderr.resume();
    child.once("error", () => {
      settle(null, "spawn-error");
    });
    child.once("close", (code) => {
      settle(code, code === 0 ? "success" : "nonzero");
    });
    child.stdin.once("error", () => undefined);
    child.stdin.end(options.input);
  }).catch(() => {
    throw new Error(SAFE_PROCESS_ERROR);
  });
}

async function removeOwnedContainer(name: string): Promise<void> {
  assertOwnedContainerName(name);
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const removed = await runProcess(
      "docker",
      dockerArgs(["container", "rm", "--force", name]),
      { environment: localDockerEnvironment(), timeoutMs: 3_000 },
    );
    if (removed.code === 0 && removed.outcome === "success") {
      await assertContainerAbsent(name);
      return;
    }
    if (await ownedContainerIsAbsent(name)) return;
    await delay(50);
  }
  throw new Error(SAFE_SETUP_ERROR);
}

async function assertContainerAbsent(name: string): Promise<void> {
  assertOwnedContainerName(name);
  const deadline = Date.now() + CONTAINER_ABSENCE_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const remainingMs = deadline - Date.now();
    if (await ownedContainerIsAbsent(name, Math.min(3_000, remainingMs))) return;
    const delayMs = Math.min(50, deadline - Date.now());
    if (delayMs > 0) await delay(delayMs);
  }
  throw new Error(SAFE_SETUP_ERROR);
}

async function ownedContainerIsAbsent(name: string, timeoutMs = 3_000): Promise<boolean> {
  assertOwnedContainerName(name);
  const listed = await runProcess(
    "docker",
    dockerArgs([
      "container",
      "ls",
      "--all",
      "--quiet",
      "--no-trunc",
      "--filter",
      `name=^/${name}$`,
    ]),
    { environment: localDockerEnvironment(), timeoutMs },
  );
  if (listed.code !== 0 || listed.outcome !== "success") {
    throw new Error(SAFE_SETUP_ERROR);
  }
  return listed.stdout.toString("utf8").trim() === "";
}

function assertOwnedContainerName(name: string): void {
  if (!OWNED_CONTAINER_PATTERN.test(name)) throw new Error(SAFE_SETUP_ERROR);
}

async function assertLocalDockerSocket(): Promise<void> {
  try {
    const metadata = await lstat(LOCAL_DOCKER_SOCKET);
    if (!metadata.isSocket() || metadata.isSymbolicLink()) throw new Error(SAFE_SETUP_ERROR);
  } catch {
    throw new Error(SAFE_SETUP_ERROR);
  }
}

function dockerArgs(args: readonly string[]): string[] {
  return ["--host", LOCAL_DOCKER_HOST, ...args];
}

function localDockerEnvironment(overrides: NodeJS.ProcessEnv = {}): NodeJS.ProcessEnv {
  const environment = { ...process.env, ...overrides };
  delete environment.DOCKER_HOST;
  delete environment.DOCKER_CONTEXT;
  delete environment.DOCKER_TLS_VERIFY;
  delete environment.DOCKER_CERT_PATH;
  return environment;
}

function assertLoopbackRemote(value: string): void {
  try {
    const target = new URL(value);
    if (
      target.protocol !== "http:"
      || target.hostname !== "127.0.0.1"
      || !isExplicitPort(target.port)
      || target.username !== ""
      || target.password !== ""
      || target.search !== ""
      || target.hash !== ""
    ) throw new Error(SAFE_SETUP_ERROR);
  } catch {
    throw new Error(SAFE_SETUP_ERROR);
  }
}

function isExplicitPort(value: string): boolean {
  if (!/^\d{1,5}$/u.test(value)) return false;
  const port = Number(value);
  return Number.isSafeInteger(port) && port >= 1 && port <= 65_535;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, milliseconds);
    timer.unref();
  });
}
