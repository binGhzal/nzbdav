/** @vitest-environment node */

import { createHash, randomBytes } from "node:crypto";
import { existsSync } from "node:fs";
import type { OutgoingHttpHeaders } from "node:http";
import net from "node:net";
import {
  mkdir,
  mkdtemp,
  readFile,
  rm,
  symlink,
  utimes,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  sanitizeDisposableBackendDiagnosticsForTest,
  startDisposableAspNetBackend,
  type DisposableAspNetBackend,
  validateDisposableAspNetReleaseArtifactForTest,
} from "./disposable-aspnet-backend";
import { requestLoopbackBounded } from "./bounded-http";

let backend: DisposableAspNetBackend | undefined;
let backendSetup: Promise<DisposableAspNetBackend> | undefined;
let setupAbort: AbortController | undefined;

afterEach(async () => {
  setupAbort?.abort();
  const resolvedBackend = backend ?? await backendSetup?.catch(() => undefined);
  await resolvedBackend?.stop();
  backend = undefined;
  backendSetup = undefined;
  setupAbort = undefined;
}, 130_000);

describe("disposable ASP.NET backend", () => {
  it("ignores hostile DOTNET_URLS and binds only its explicit loopback listener", async () => {
    const hostilePort = await reserveLoopbackPort();
    backendSetup = startDisposableAspNetBackend({
      dotnetUrlsForTest: `http://0.0.0.0:${hostilePort}`,
    });
    backend = await backendSetup;

    const health = await requestBoundedTest(`${backend.origin}/health`);
    expect(health.status).toBe(200);
    expect(await canConnectLoopback(hostilePort)).toBe(false);
  }, 130_000);

  it("rejects configured Kestrel endpoints before opening any server listener", async () => {
    backendSetup = startDisposableAspNetBackend({
      kestrelEndpointForTest: "http://0.0.0.0:8080",
    });

    await expect(backendSetup).rejects.toThrow("Disposable backend setup failed.");
    backendSetup = undefined;
  }, 130_000);

  it("redacts split secrets, capabilities, paths, and terminal controls before clipping", () => {
    const internalKey = `i${randomBytes(12).toString("hex")}`;
    const username = `u${randomBytes(8).toString("hex")}`;
    const password = `w${randomBytes(12).toString("hex")}`;
    const basic = Buffer.from(`${username}:${password}`, "utf8").toString("base64");
    const downloadKey = createHash("sha256")
      .update(`README_${internalKey}`)
      .digest("hex");
    const repositoryPath = join(tmpdir(), `repo-${randomBytes(8).toString("hex")}`);
    const executablePath = join(repositoryPath, "tools", "dotnet");
    const cases = [internalKey, username, password, basic, downloadKey, repositoryPath,
      executablePath];

    const results = cases.map((sensitiveValue) => {
      const splitAt = Math.max(1, Math.floor(sensitiveValue.length / 2));
      const output = sanitizeDisposableBackendDiagnosticsForTest({
        chunks: [
          { label: "stdout", value: `${"x".repeat(32 * 1024 - 8)}\u001b[31m${sensitiveValue.slice(0, splitAt)}` },
          { label: "stderr", value: `${sensitiveValue.slice(splitAt)}\u001b[0m\r\n` },
          { label: "stderr", value: `?downloadKey=${downloadKey}\u0000${repositoryPath}` },
        ],
        sensitiveValues: cases,
      });
      return {
        bounded: Buffer.byteLength(output, "utf8") <= 32 * 1024,
        noControl: !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(output),
        noFullValue: !output.includes(sensitiveValue),
        noPrefix: !output.includes(sensitiveValue.slice(0, Math.min(8, sensitiveValue.length))),
        noSuffix: !output.includes(sensitiveValue.slice(-Math.min(8, sensitiveValue.length))),
      };
    });

    expect(results).toEqual(cases.map(() => ({
      bounded: true,
      noControl: true,
      noFullValue: true,
      noPrefix: true,
      noSuffix: true,
    })));
  });

  it("drops a sensitive fragment left at the raw capture boundary after repeated redactions", () => {
    const sensitiveValue = `s${randomBytes(16).toString("hex")}`;
    const fragmentLength = 12;
    const rawCaptureLimit = (32 * 1024) + (8 * 1024);
    const repeatedValues = `${sensitiveValue}|`.repeat(1_000);
    const terminalControl = "\u001b[31m";
    const paddingLength = rawCaptureLimit
      - Buffer.byteLength(repeatedValues, "utf8")
      - Buffer.byteLength(terminalControl, "utf8")
      - fragmentLength;
    expect(paddingLength).toBeGreaterThan(0);

    const output = sanitizeDisposableBackendDiagnosticsForTest({
      chunks: [
        {
          label: "stdout",
          value: `${repeatedValues}${"x".repeat(paddingLength)}`
            + `${sensitiveValue.slice(0, 5)}${terminalControl}`,
        },
        {
          label: "stdout",
          value: `${sensitiveValue.slice(5, fragmentLength)}tail\u001b[0m`,
        },
      ],
      sensitiveValues: [sensitiveValue],
    });
    const fragment = sensitiveValue.slice(0, fragmentLength);

    expect({
      bounded: Buffer.byteLength(output, "utf8") <= 32 * 1024,
      noControl: !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(output),
      noFullValue: !output.includes(sensitiveValue),
      noFragment: !output.includes(fragment),
      noFragmentPrefix: !output.includes(fragment.slice(0, 8)),
      truncated: output.includes("<diagnostics-truncated>"),
    }).toEqual({
      bounded: true,
      noControl: true,
      noFullValue: true,
      noFragment: true,
      noFragmentPrefix: true,
      truncated: true,
    });
  });

  it("rejects stale, symlinked, and wrong-target backend artifacts", async () => {
    const repositoryRoot = await mkdtemp(join(tmpdir(), "pinrail-artifact-binding-"));
    const backendRoot = join(repositoryRoot, "backend");
    const exactTarget = join(backendRoot, "bin", "Release", "net10.0");
    const exactDll = join(exactTarget, "NzbWebDAV.dll");
    const programSource = join(backendRoot, "Program.cs");
    try {
      await mkdir(exactTarget, { recursive: true, mode: 0o700 });
      await writeFile(join(backendRoot, "NzbWebDAV.csproj"), "<TargetFramework>net10.0</TargetFramework>");
      await writeFile(programSource, "class Program {}\n");
      await writeFile(exactDll, "fixture artifact\n");
      const oldTime = new Date(Date.now() - 10_000);
      const currentTime = new Date();
      await utimes(join(backendRoot, "NzbWebDAV.csproj"), oldTime, oldTime);
      await utimes(programSource, oldTime, oldTime);
      await utimes(exactDll, currentTime, currentTime);

      const acceptedFresh = await acceptsArtifact(repositoryRoot);
      await utimes(programSource, new Date(Date.now() + 10_000), new Date(Date.now() + 10_000));
      const rejectedStale = !(await acceptsArtifact(repositoryRoot));

      await rm(exactDll);
      const realArtifact = join(exactTarget, "real.dll");
      await writeFile(realArtifact, "fixture artifact\n");
      await symlink(realArtifact, exactDll);
      const rejectedSymlink = !(await acceptsArtifact(repositoryRoot));

      await rm(exactDll);
      await mkdir(join(backendRoot, "bin", "Release", "net9.0"), { recursive: true });
      await writeFile(
        join(backendRoot, "bin", "Release", "net9.0", "NzbWebDAV.dll"),
        "wrong target\n",
      );
      const rejectedWrongTarget = !(await acceptsArtifact(repositoryRoot));

      expect({ acceptedFresh, rejectedStale, rejectedSymlink, rejectedWrongTarget }).toEqual({
        acceptedFresh: true,
        rejectedStale: true,
        rejectedSymlink: true,
        rejectedWrongTarget: true,
      });
    } finally {
      await rm(repositoryRoot, { recursive: true, force: true });
    }
  });

  it("aborts setup before handle return and removes the exact owned root", async () => {
    let ownedFixtureRoot: string | undefined;
    setupAbort = new AbortController();
    backendSetup = startDisposableAspNetBackend({
      signal: setupAbort.signal,
      onFixtureRoot(root) {
        ownedFixtureRoot = root;
      },
    });
    await vi.waitFor(async () => {
      const migrationDatabaseExists = ownedFixtureRoot !== undefined
        && existsSync(join(ownedFixtureRoot, "config", "db.sqlite"));
      expect(migrationDatabaseExists).toBe(true);
    }, { timeout: 10_000, interval: 10 });

    const abortStartedAt = Date.now();
    setupAbort.abort();
    let setupRejectedAsAborted = false;
    try {
      backend = await backendSetup;
    } catch (error) {
      setupRejectedAsAborted = error instanceof Error
        && error.message.startsWith("Disposable backend setup was aborted.")
        && !error.message.includes("cleanup failed");
    }
    expect({ resolved: backend !== undefined, setupRejectedAsAborted }).toEqual({
      resolved: false,
      setupRejectedAsAborted: true,
    });
    expect(Date.now() - abortStartedAt < 5_000).toBe(true);
    backendSetup = undefined;
    await vi.waitFor(async () => {
      expect(ownedFixtureRoot !== undefined && existsSync(ownedFixtureRoot)).toBe(false);
    }, { timeout: 2_000, interval: 10 });
  }, 20_000);

  it("migrates, authenticates protocol clients, streams signed content, and removes its owned root", async () => {
    setupAbort = new AbortController();
    backendSetup = startDisposableAspNetBackend({ signal: setupAbort.signal });
    backend = await backendSetup;
    const fixtureRoot = dirname(backend.configPath);
    const readme = await readFile(new URL(
      "../../../backend/WebDav/StaticFiles/root/README.md",
      import.meta.url,
    ));

    const health = await requestBoundedTest(`${backend.origin}/health`);
    expect(health.status).toBe(200);

    const version = await requestBoundedTest(
      `${backend.origin}/api?mode=version&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
    );
    expect(version.status).toBe(200);
    expect(JSON.parse(version.body.toString("utf8"))).toMatchObject({
      status: true,
      version: "4.5.1",
    });

    expect(backend.credentials.publicApiKey === backend.credentials.internalApiKey).toBe(false);
    const missingSabKey = await requestBoundedTest(`${backend.origin}/api?mode=get_cats`);
    expect(missingSabKey.status).toBe(401);
    const wrongSabKey = await requestBoundedTest(`${backend.origin}/api?mode=get_cats&apikey=wrong`);
    expect(wrongSabKey.status).toBe(401);
    const authenticatedSab = await requestBoundedTest(
      `${backend.origin}/api?mode=get_cats&apikey=${encodeURIComponent(backend.credentials.publicApiKey)}`,
    );
    expect(authenticatedSab.status).toBe(200);
    const categoryPayload = JSON.parse(authenticatedSab.body.toString("utf8")) as {
      categories?: unknown;
    };
    expect(Array.isArray(categoryPayload.categories)).toBe(true);

    const missingWebDav = await requestBoundedTest(`${backend.origin}/content`, {
      method: "PROPFIND",
      headers: { Depth: "0" },
    });
    expect(missingWebDav.status).toBe(401);

    const wrongWebDav = await requestBoundedTest(`${backend.origin}/content`, {
      method: "PROPFIND",
      headers: {
        Authorization: basicAuthorization(
          backend.credentials.webDavUsername,
          `${backend.credentials.webDavPassword}-wrong`,
        ),
        Depth: "0",
      },
    });
    expect(wrongWebDav.status).toBe(401);

    const validWebDav = await requestBoundedTest(`${backend.origin}/content`, {
      method: "PROPFIND",
      headers: {
        Authorization: basicAuthorization(
          backend.credentials.webDavUsername,
          backend.credentials.webDavPassword,
        ),
        Depth: "0",
      },
    });
    expect(validWebDav.status).toBe(207);

    const downloadKey = createHash("sha256")
      .update(`README_${backend.credentials.internalApiKey}`)
      .digest("hex");
    const signedReadmeUrl = `${backend.origin}/view/README?downloadKey=${downloadKey}`;

    const full = await requestBoundedTest(signedReadmeUrl);
    expect(full.status).toBe(200);
    expect(full.body).toEqual(readme);

    const head = await requestBoundedTest(signedReadmeUrl, { method: "HEAD" });
    expect(head.status).toBe(200);
    expect(head.headers["content-length"]).toBe(String(readme.byteLength));
    expect(head.body.byteLength).toBe(0);

    const range = await requestBoundedTest(signedReadmeUrl, {
      headers: { Range: "bytes=1-7" },
    });
    expect(range.status).toBe(206);
    expect(range.headers["content-range"]).toBe(`bytes 1-7/${readme.byteLength}`);
    expect(range.body).toEqual(readme.subarray(1, 8));

    expect(existsSync(backend.databasePath)).toBe(true);
    const exposedDiagnostics = backend.diagnostics;
    expect([
      backend.credentials.internalApiKey,
      backend.credentials.publicApiKey,
      backend.credentials.webDavUsername,
      backend.credentials.webDavPassword,
      downloadKey,
    ].map((secret) => exposedDiagnostics.includes(secret))).toEqual([
      false,
      false,
      false,
      false,
      false,
    ]);

    await backend.stop();
    await backend.stop();
    backend = undefined;
    expect(existsSync(fixtureRoot)).toBe(false);
  }, 130_000);
});

function basicAuthorization(username: string, password: string): string {
  return `Basic ${Buffer.from(`${username}:${password}`, "utf8").toString("base64")}`;
}

async function acceptsArtifact(repositoryRoot: string): Promise<boolean> {
  try {
    await validateDisposableAspNetReleaseArtifactForTest(repositoryRoot);
    return true;
  } catch {
    return false;
  }
}

async function reserveLoopbackPort(): Promise<number> {
  const server = net.createServer();
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen({ host: "127.0.0.1", port: 0, exclusive: true }, () => {
      server.off("error", reject);
      resolve();
    });
  });
  const address = server.address();
  if (!address || typeof address === "string") {
    server.close();
    throw new Error("Failed to reserve a hostile-listener test port.");
  }
  await new Promise<void>((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve());
  });
  return address.port;
}

function canConnectLoopback(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = net.createConnection({ host: "127.0.0.1", port });
    let settled = false;
    const finish = (connected: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      socket.destroy();
      resolve(connected);
    };
    const timeout = setTimeout(() => finish(false), 1_000);
    socket.once("connect", () => finish(true));
    socket.once("error", () => finish(false));
  });
}

function requestBoundedTest(
  input: string,
  options: Readonly<{ method?: string; headers?: OutgoingHttpHeaders }> = {},
) {
  const target = new URL(input);
  return requestLoopbackBounded(
    target.origin,
    `${target.pathname}${target.search}`,
    options,
    { timeoutMs: 5_000, maxResponseBytes: 1024 * 1024 },
  );
}
