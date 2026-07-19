import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { constants as fsConstants } from "node:fs";
import { access, chmod, lstat, mkdir, mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import net from "node:net";
import { basename, delimiter, dirname, extname, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

const FIXTURE_PREFIX = "pinrail-aspnet-backend-";
const MAX_DIAGNOSTIC_BYTES = 32 * 1024;
const MAX_RAW_DIAGNOSTIC_BYTES = MAX_DIAGNOSTIC_BYTES + (8 * 1024);
const MAINTENANCE_TIMEOUT_MS = 45_000;
const STARTUP_TIMEOUT_MS = 45_000;
const LOOPBACK_REQUEST_TIMEOUT_MS = 5_000;
const GRACEFUL_STOP_TIMEOUT_MS = 10_000;
const ABORT_STOP_TIMEOUT_MS = 1_000;
const FORCED_STOP_TIMEOUT_MS = 2_000;
const SAFE_PATH = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
const EXACT_TARGET_FRAMEWORK = "net10.0";
const BUILD_INPUT_EXTENSIONS = new Set([
  ".cs", ".csproj", ".json", ".md", ".props", ".resx", ".sql", ".targets", ".txt",
]);

export type DisposableAspNetBackendCredentials = Readonly<{
  internalApiKey: string;
  publicApiKey: string;
  webDavUsername: string;
  webDavPassword: string;
}>;

export type DisposableAspNetBackend = Readonly<{
  origin: string;
  credentials: DisposableAspNetBackendCredentials;
  configPath: string;
  databasePath: string;
  readonly diagnostics: string;
  stop: () => Promise<void>;
}>;

export type DisposableAspNetBackendStartOptions = Readonly<{
  signal?: AbortSignal;
  onFixtureRoot?: (root: string) => void;
  dotnetUrlsForTest?: string;
  kestrelEndpointForTest?: string;
}>;

type ChildOutcome = {
  code: number | null;
  signal: NodeJS.Signals | null;
  error?: Error;
};

type ManagedChild = {
  child: ChildProcessWithoutNullStreams;
  exit: Promise<ChildOutcome>;
  outcome?: ChildOutcome;
  stdout: () => string;
};

export async function startDisposableAspNetBackend(
  options: DisposableAspNetBackendStartOptions = {},
): Promise<DisposableAspNetBackend> {
  throwIfSetupAborted(options.signal);
  const fixtureRoot = await createFixtureRoot();
  const configPath = join(fixtureRoot, "config");
  const privateTempPath = join(fixtureRoot, "tmp");
  const mountPath = join(fixtureRoot, "mount");
  const databasePath = join(configPath, "db.sqlite");
  const credentials = Object.freeze({
    internalApiKey: syntheticCredential("i", 8),
    publicApiKey: syntheticCredential("p", 6),
    webDavUsername: syntheticCredential("u", 4),
    webDavPassword: syntheticCredential("w", 8),
  });
  const diagnostics = new SanitizedDiagnostics(
    [fixtureRoot, ...credentialRepresentations(credentials)],
  );
  let server: ManagedChild | undefined;

  try {
    options.onFixtureRoot?.(fixtureRoot);
    throwIfSetupAborted(options.signal);
    await Promise.all([
      mkdir(configPath, { mode: 0o700 }),
      mkdir(privateTempPath, { mode: 0o700 }),
      mkdir(mountPath, { mode: 0o700 }),
    ]);

    const repositoryRoot = fileURLToPath(new URL("../../../", import.meta.url));
    diagnostics.register(repositoryRoot);
    const { backendRoot, releaseRoot, dllPath } = await resolveReleaseBackend(repositoryRoot);
    diagnostics.register(repositoryRoot, backendRoot, releaseRoot, dllPath);
    const dotnetPath = await resolveExecutable("dotnet");
    diagnostics.register(dotnetPath, dirname(dotnetPath));
    throwIfSetupAborted(options.signal);
    const baseEnvironment = createChildEnvironment({
      configPath,
      credentials,
      fixtureRoot,
      mountPath,
      privateTempPath,
    });

    await runBoundedProcess({
      args: [dllPath, "--db-migration"],
      command: dotnetPath,
      cwd: backendRoot,
      diagnostics,
      environment: baseEnvironment,
      label: "migration",
      signal: options.signal,
      timeoutMs: MAINTENANCE_TIMEOUT_MS,
    });
    throwIfSetupAborted(options.signal);

    const port = await reserveLoopbackPort();
    throwIfSetupAborted(options.signal);
    const origin = `http://127.0.0.1:${port}`;
    server = spawnManaged({
      args: [dllPath],
      command: dotnetPath,
      cwd: backendRoot,
      diagnostics,
      environment: {
        ...baseEnvironment,
        ASPNETCORE_URLS: origin,
        ...(options.dotnetUrlsForTest === undefined
          ? {}
          : { DOTNET_URLS: options.dotnetUrlsForTest }),
        ...(options.kestrelEndpointForTest === undefined
          ? {}
          : { Kestrel__Endpoints__Public__Url: options.kestrelEndpointForTest }),
      },
      label: "server",
    });
    server.child.stdin.end();
    await waitForHealth(origin, server, diagnostics, options.signal);
    await updatePublicApiKey(origin, credentials, options.signal);
    throwIfSetupAborted(options.signal);

    let stopPromise: Promise<void> | undefined;
    const stop = (): Promise<void> => {
      stopPromise ??= (async () => {
        try {
          if (server) {
            await terminateManagedChild(server);
          }
        } finally {
          await removeFixtureRoot(fixtureRoot);
        }
      })();
      return stopPromise;
    };

    return Object.freeze({
      origin,
      credentials,
      configPath,
      databasePath,
      get diagnostics() {
        return diagnostics.render();
      },
      stop,
    });
  } catch (error) {
    let cleanupFailed = false;
    if (server) {
      try {
        await terminateManagedChild(
          server,
          options.signal?.aborted ? ABORT_STOP_TIMEOUT_MS : GRACEFUL_STOP_TIMEOUT_MS,
        );
      } catch (stopError) {
        cleanupFailed = true;
        diagnostics.append("fixture-stop-error", safeErrorMessage(stopError));
      }
    }
    try {
      await removeFixtureRoot(fixtureRoot);
    } catch (cleanupError) {
      cleanupFailed = true;
      diagnostics.append("fixture-cleanup-error", safeErrorMessage(cleanupError));
    }
    diagnostics.append("fixture-error", safeErrorMessage(error));
    const summary = cleanupFailed
      ? "Disposable backend setup cleanup failed."
      : options.signal?.aborted
      ? "Disposable backend setup was aborted."
      : "Disposable backend setup failed.";
    throw new Error(
      `${summary}\n${diagnostics.render()}`.trim(),
    );
  }
}

async function createFixtureRoot(): Promise<string> {
  const root = await mkdtemp(join(resolve(tmpdir()), FIXTURE_PREFIX));
  await chmod(root, 0o700);
  return root;
}

async function resolveReleaseBackend(
  repositoryRoot = fileURLToPath(new URL("../../../", import.meta.url)),
): Promise<{
  repositoryRoot: string;
  backendRoot: string;
  releaseRoot: string;
  dllPath: string;
}> {
  const backendRoot = join(repositoryRoot, "backend");
  const releaseRoot = join(backendRoot, "bin", "Release");
  const dllPath = join(releaseRoot, EXACT_TARGET_FRAMEWORK, "NzbWebDAV.dll");
  let dllMetadata;
  try {
    dllMetadata = await lstat(dllPath);
  } catch {
    throw new Error("The exact net10.0 backend Release DLL is absent. Run the Release build first.");
  }
  if (!dllMetadata.isFile() || dllMetadata.isSymbolicLink()) {
    throw new Error("The exact net10.0 backend Release DLL must be a regular non-symlink file.");
  }

  const projectPath = join(backendRoot, "NzbWebDAV.csproj");
  const project = await readFile(projectPath, "utf8")
    .catch(() => { throw new Error("The backend project file is unavailable."); });
  if (!new RegExp(`<TargetFramework>\\s*${EXACT_TARGET_FRAMEWORK}\\s*</TargetFramework>`, "u")
    .test(project)) {
    throw new Error("The backend project does not declare the exact net10.0 target.");
  }

  const buildInputs = await collectBackendBuildInputs(backendRoot);
  for (const repositoryInput of [
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "NuGet.Config",
  ]) {
    const path = join(repositoryRoot, repositoryInput);
    try {
      const metadata = await lstat(path);
      if (metadata.isFile()) buildInputs.push(path);
    } catch (error) {
      if (!isMissingPathError(error)) throw error;
    }
  }
  for (const input of buildInputs) {
    const metadata = await lstat(input);
    if (metadata.mtimeMs > dllMetadata.mtimeMs) {
      throw new Error("The exact backend Release DLL is stale relative to current build inputs.");
    }
  }
  if (buildInputs.length === 0) {
    throw new Error(
      "No backend build inputs were found for Release artifact freshness validation.",
    );
  }
  return { repositoryRoot, backendRoot, releaseRoot, dllPath };
}

async function collectBackendBuildInputs(backendRoot: string): Promise<string[]> {
  const inputs: string[] = [];
  const visit = async (directory: string): Promise<void> => {
    const entries = await readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.name === "bin" || entry.name === "obj") continue;
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        await visit(path);
      } else if (entry.isFile() && BUILD_INPUT_EXTENSIONS.has(extname(entry.name).toLowerCase())) {
        inputs.push(path);
      }
    }
  };
  await visit(backendRoot);
  return inputs;
}

export async function validateDisposableAspNetReleaseArtifactForTest(
  repositoryRoot: string,
): Promise<void> {
  await resolveReleaseBackend(repositoryRoot);
}

async function resolveExecutable(name: string): Promise<string> {
  const searchPath = process.env.PATH;
  if (!searchPath) throw new Error(`Cannot resolve ${name} because the test process has no PATH.`);
  for (const directory of searchPath.split(delimiter)) {
    if (!directory) continue;
    const candidate = join(directory, name);
    try {
      await access(candidate, fsConstants.X_OK);
      return candidate;
    } catch (error) {
      if (!isMissingOrInaccessiblePathError(error)) throw error;
    }
  }
  throw new Error(`Required local executable ${name} was not found.`);
}

function createChildEnvironment(options: {
  configPath: string;
  credentials: DisposableAspNetBackendCredentials;
  fixtureRoot: string;
  mountPath: string;
  privateTempPath: string;
}): NodeJS.ProcessEnv {
  return {
    ASPNETCORE_ENVIRONMENT: "Production",
    CONFIG_PATH: options.configPath,
    DOTNET_ENVIRONMENT: "Production",
    DOTNET_NOLOGO: "1",
    FRONTEND_BACKEND_API_KEY: options.credentials.internalApiKey,
    LANG: "C.UTF-8",
    LOG_LEVEL: "Warning",
    MOUNT_DIRECTORY: options.mountPath,
    MOUNT_TYPE: "none",
    NZBDAV_DATABASE_PROVIDER: "sqlite",
    NZBDAV_ENV_FILE: join(options.fixtureRoot, "missing.env"),
    NZBDAV_ROLE: "all",
    NZBDAV_VERSION: "test-fixture",
    PATH: SAFE_PATH,
    TEMP: options.privateTempPath,
    TMP: options.privateTempPath,
    TMPDIR: options.privateTempPath,
    TZ: "Etc/UTC",
    WEBDAV_PASSWORD: options.credentials.webDavPassword,
    WEBDAV_USER: options.credentials.webDavUsername,
  };
}

async function updatePublicApiKey(
  origin: string,
  credentials: DisposableAspNetBackendCredentials,
  signal?: AbortSignal,
): Promise<void> {
  const body = new URLSearchParams([["api.key", credentials.publicApiKey]]).toString();
  const response = await boundedFetch(`${origin}/api/update-config`, {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
      "x-api-key": credentials.internalApiKey,
    },
    body,
  }, signal, LOOPBACK_REQUEST_TIMEOUT_MS);
  try {
    if (response.status !== 200) {
      throw new Error("The fixture-only public API-key update was rejected.");
    }
  } finally {
    await response.body?.cancel().catch(() => undefined);
  }
}

async function boundedFetch(
  input: string,
  init: RequestInit,
  setupSignal: AbortSignal | undefined,
  timeoutMs: number,
): Promise<Response> {
  throwIfSetupAborted(setupSignal);
  const timeoutSignal = AbortSignal.timeout(timeoutMs);
  const signal = setupSignal
    ? AbortSignal.any([setupSignal, timeoutSignal])
    : timeoutSignal;
  return fetch(input, {
    ...init,
    signal,
  });
}

async function runBoundedProcess(options: {
  args: string[];
  command: string;
  cwd: string;
  diagnostics: SanitizedDiagnostics;
  environment: NodeJS.ProcessEnv;
  input?: string;
  label: string;
  signal?: AbortSignal;
  timeoutMs: number;
}): Promise<{ stdout: string }> {
  const managed = spawnManaged(options);
  managed.child.stdin.on("error", () => undefined);
  managed.child.stdin.end(options.input);
  const result = await waitForSetupOutcome(managed, options.timeoutMs, options.signal);
  if (result.kind === "aborted") {
    await terminateManagedChild(managed, ABORT_STOP_TIMEOUT_MS);
    throw setupAbortError();
  }
  if (result.kind === "timeout") {
    await terminateManagedChild(managed);
    throw new Error(`${options.label} exceeded its bounded timeout.`);
  }
  const outcome = result.outcome;
  if (outcome.error) {
    throw new Error(`${options.label} could not start: ${outcome.error.message}`);
  }
  if (outcome.code !== 0) {
    throw new Error(
      `${options.label} exited unsuccessfully (code=${outcome.code}, signal=${outcome.signal ?? "none"}).`,
    );
  }
  return { stdout: managed.stdout() };
}

function spawnManaged(options: {
  args: string[];
  command: string;
  cwd: string;
  diagnostics: SanitizedDiagnostics;
  environment: NodeJS.ProcessEnv;
  label: string;
}): ManagedChild {
  const child = spawn(options.command, options.args, {
    cwd: options.cwd,
    env: options.environment,
    stdio: ["pipe", "pipe", "pipe"],
  });
  const stdoutChunks: Buffer[] = [];
  let stdoutBytes = 0;
  child.stdout.on("data", (chunk: Buffer) => {
    options.diagnostics.append(`${options.label}:stdout`, chunk);
    if (stdoutBytes >= 4_096) return;
    const accepted = chunk.subarray(0, 4_096 - stdoutBytes);
    stdoutChunks.push(accepted);
    stdoutBytes += accepted.byteLength;
  });
  child.stderr.on("data", (chunk: Buffer) => {
    options.diagnostics.append(`${options.label}:stderr`, chunk);
  });

  let managed!: ManagedChild;
  const exit = new Promise<ChildOutcome>((resolveExit) => {
    let settled = false;
    let spawnError: Error | undefined;
    const settle = (outcome: ChildOutcome) => {
      if (settled) return;
      settled = true;
      managed.outcome = outcome;
      resolveExit(outcome);
    };
    child.once("error", (error) => {
      spawnError = error;
    });
    child.once("close", (code, signal) => settle({ code, signal, error: spawnError }));
  });
  managed = {
    child,
    exit,
    stdout: () => Buffer.concat(stdoutChunks).toString("utf8"),
  };
  return managed;
}

async function waitForHealth(
  origin: string,
  server: ManagedChild,
  diagnostics: SanitizedDiagnostics,
  signal?: AbortSignal,
): Promise<void> {
  const deadline = Date.now() + STARTUP_TIMEOUT_MS;
  while (Date.now() < deadline) {
    throwIfSetupAborted(signal);
    if (server.outcome) {
      throw new Error(
        `Backend exited before health became ready (code=${server.outcome.code}, signal=${server.outcome.signal ?? "none"}).`,
      );
    }
    try {
      const response = await boundedFetch(`${origin}/health`, {}, signal, 1_000);
      if (response.status === 200) {
        await response.body?.cancel().catch(() => undefined);
        return;
      }
      await response.body?.cancel().catch(() => undefined);
    } catch (error) {
      if (signal?.aborted) throw setupAbortError();
      // A refused connection is expected while Kestrel is binding.
    }
    await delay(100, signal);
  }
  diagnostics.append("fixture", "Backend health polling reached its bounded timeout.");
  throw new Error("Backend did not return HTTP 200 from /health before the startup deadline.");
}

async function reserveLoopbackPort(): Promise<number> {
  const reservation = net.createServer();
  await new Promise<void>((resolveListen, rejectListen) => {
    reservation.once("error", rejectListen);
    reservation.listen({ host: "127.0.0.1", port: 0, exclusive: true }, () => {
      reservation.off("error", rejectListen);
      resolveListen();
    });
  });
  const address = reservation.address();
  if (!address || typeof address === "string") {
    reservation.close();
    throw new Error("Failed to reserve a unique loopback port for the backend fixture.");
  }
  await new Promise<void>((resolveClose, rejectClose) => {
    reservation.close((error) => error ? rejectClose(error) : resolveClose());
  });
  return address.port;
}

async function terminateManagedChild(
  managed: ManagedChild,
  gracefulTimeoutMs = GRACEFUL_STOP_TIMEOUT_MS,
): Promise<void> {
  if (managed.outcome) return;
  managed.child.kill("SIGTERM");
  if (await waitForOutcome(managed, gracefulTimeoutMs)) return;
  managed.child.kill("SIGKILL");
  if (await waitForOutcome(managed, FORCED_STOP_TIMEOUT_MS)) return;
  throw new Error("Disposable backend child did not exit after bounded SIGKILL fallback.");
}

async function waitForSetupOutcome(
  managed: ManagedChild,
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<
  | { kind: "outcome"; outcome: ChildOutcome }
  | { kind: "timeout" }
  | { kind: "aborted" }
> {
  if (managed.outcome) return { kind: "outcome", outcome: managed.outcome };
  if (signal?.aborted) return { kind: "aborted" };
  let timeout: NodeJS.Timeout | undefined;
  let abortListener: (() => void) | undefined;
  try {
    return await Promise.race([
      managed.exit.then((outcome) => ({ kind: "outcome", outcome }) as const),
      new Promise<{ kind: "timeout" }>((resolveTimeout) => {
        timeout = setTimeout(() => resolveTimeout({ kind: "timeout" }), timeoutMs);
      }),
      new Promise<{ kind: "aborted" }>((resolveAbort) => {
        if (!signal) return;
        abortListener = () => resolveAbort({ kind: "aborted" });
        signal.addEventListener("abort", abortListener, { once: true });
      }),
    ]);
  } finally {
    if (timeout) clearTimeout(timeout);
    if (signal && abortListener) signal.removeEventListener("abort", abortListener);
  }
}

async function waitForOutcome(
  managed: ManagedChild,
  timeoutMs: number,
): Promise<ChildOutcome | undefined> {
  if (managed.outcome) return managed.outcome;
  let timeout: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      managed.exit,
      new Promise<undefined>((resolveTimeout) => {
        timeout = setTimeout(() => resolveTimeout(undefined), timeoutMs);
      }),
    ]);
  } finally {
    if (timeout) clearTimeout(timeout);
  }
}

async function removeFixtureRoot(root: string): Promise<void> {
  assertGuardedFixtureRoot(root);
  try {
    const metadata = await lstat(root);
    if (!metadata.isDirectory() || metadata.isSymbolicLink()) {
      throw new Error("Refusing to clean a fixture root that is not a real directory.");
    }
  } catch (error) {
    if (isMissingPathError(error)) return;
    throw error;
  }
  await rm(root, { recursive: true, force: false, maxRetries: 2, retryDelay: 50 });
}

function assertGuardedFixtureRoot(root: string): void {
  const normalized = resolve(root);
  const expectedParent = resolve(tmpdir());
  const name = basename(normalized);
  if (
    dirname(normalized) !== expectedParent
    || !name.startsWith(FIXTURE_PREFIX)
    || name.length !== FIXTURE_PREFIX.length + 6
  ) {
    throw new Error("Refusing to clean an unguarded disposable backend path.");
  }
}

class SanitizedDiagnostics {
  readonly #records: Buffer[] = [];
  readonly #sensitiveValues = new Set<string>();
  #bytes = 0;
  #truncated = false;

  constructor(sensitiveValues: string[] = []) {
    this.register(...sensitiveValues);
  }

  register(...sensitiveValues: string[]): void {
    for (const value of sensitiveValues) {
      if (value.length > 0) this.#sensitiveValues.add(value);
    }
  }

  append(_label: string, value: Buffer | string): void {
    if (this.#bytes >= MAX_RAW_DIAGNOSTIC_BYTES) {
      this.#truncated = true;
      return;
    }
    const record = Buffer.isBuffer(value) ? value : Buffer.from(value, "utf8");
    const remaining = MAX_RAW_DIAGNOSTIC_BYTES - this.#bytes;
    const accepted = record.subarray(0, remaining);
    this.#records.push(accepted);
    this.#bytes += accepted.byteLength;
    if (record.byteLength > remaining) {
      this.#truncated = true;
    }
  }

  render(): string {
    const sensitiveValues = [...this.#sensitiveValues]
      .sort((left, right) => right.length - left.length);
    const longestSensitiveValue = sensitiveValues
      .reduce((longest, value) => Math.max(longest, value.length), 0);
    let stream = stripTerminalControls(Buffer.concat(this.#records).toString("utf8"));
    for (const sensitiveValue of sensitiveValues) {
      stream = stream.replaceAll(
        sensitiveValue,
        "#".repeat(Math.max(1, sensitiveValue.length)),
      );
    }
    if (this.#truncated && longestSensitiveValue > 0) {
      stream = stream.slice(0, Math.max(0, stream.length - longestSensitiveValue));
    }
    let output = `[fixture] ${stream}`;
    output = output
      .replace(/([?&](?:apikey|downloadKey)=)[^&\s]*/giu, "$1<redacted>")
      .replace(/(authorization\s*[:=]\s*basic\s+)[^\s,;]*/giu, "$1<redacted>");
    if (this.#truncated) output += "\n<diagnostics-truncated>";
    return boundUtf8(output.trim(), MAX_DIAGNOSTIC_BYTES);
  }
}

function credentialRepresentations(credentials: DisposableAspNetBackendCredentials): string[] {
  const basicPayload = Buffer.from(
    `${credentials.webDavUsername}:${credentials.webDavPassword}`,
    "utf8",
  ).toString("base64");
  return [
    credentials.internalApiKey,
    credentials.publicApiKey,
    credentials.webDavUsername,
    credentials.webDavPassword,
    `${credentials.webDavUsername}:${credentials.webDavPassword}`,
    basicPayload,
    readmeDownloadKey(credentials.internalApiKey),
  ];
}

function readmeDownloadKey(internalApiKey: string): string {
  return createHash("sha256")
    .update(`README_${internalApiKey}`)
    .digest("hex");
}

function syntheticCredential(prefix: string, randomByteCount: number): string {
  return `${prefix}${randomBytes(randomByteCount).toString("hex")}`;
}

function safeErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Disposable backend setup failed.";
}

function isMissingPathError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error && error.code === "ENOENT";
}

function isMissingOrInaccessiblePathError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error
    && "code" in error
    && (error.code === "ENOENT" || error.code === "EACCES");
}

function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(setupAbortError());
  return new Promise((resolveDelay, rejectDelay) => {
    let timeout: NodeJS.Timeout;
    const abortListener = () => {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", abortListener);
      rejectDelay(setupAbortError());
    };
    timeout = setTimeout(() => {
      signal?.removeEventListener("abort", abortListener);
      resolveDelay();
    }, milliseconds);
    signal?.addEventListener("abort", abortListener, { once: true });
  });
}

function boundUtf8(value: string, maxBytes: number): string {
  if (Buffer.byteLength(value, "utf8") <= maxBytes) return value;
  const marker = "\n<diagnostics-truncated>";
  const available = maxBytes - Buffer.byteLength(marker, "utf8");
  let prefix = Buffer.from(value, "utf8").subarray(0, available).toString("utf8");
  while (Buffer.byteLength(prefix, "utf8") > available) {
    prefix = prefix.slice(0, -1);
  }
  return `${prefix}${marker}`;
}

function stripTerminalControls(value: string): string {
  return value
    .replace(/\u001b\][^\u0007]*(?:\u0007|\u001b\\)/gu, "")
    .replace(/\u001b\[[0-?]*[ -/]*[@-~]/gu, "")
    .replace(/\r/gu, "\n")
    .replace(/[\u0000-\u0009\u000b\u000c\u000e-\u001f\u007f-\u009f]/gu, "");
}

function throwIfSetupAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw setupAbortError();
}

function setupAbortError(): Error {
  const error = new Error("Disposable backend setup was aborted.");
  error.name = "AbortError";
  return error;
}

export function sanitizeDisposableBackendDiagnosticsForTest(options: {
  chunks: Array<{ label: string; value: Buffer | string }>;
  sensitiveValues: string[];
}): string {
  const diagnostics = new SanitizedDiagnostics(options.sensitiveValues);
  for (const chunk of options.chunks) diagnostics.append(chunk.label, chunk.value);
  return diagnostics.render();
}
