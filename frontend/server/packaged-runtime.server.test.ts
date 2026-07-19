/** @vitest-environment node */

import { spawn, spawnSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { afterAll, describe, expect, it } from "vitest";

const frontendRoot = process.cwd();
const temporaryRoots: string[] = [];

afterAll(async () => {
  await Promise.all(temporaryRoots.splice(0).map(async path => await rm(path, {
    force: true,
    recursive: true,
  })));
});

describe.sequential("native packaged frontend runtime", () => {
  it("launches the compiled bootstrap directly from every shipped production path", async () => {
    const entrypoint = await readFile(resolve(frontendRoot, "../entrypoint.sh"), "utf8");
    const packagedSmoke = await readFile(
      resolve(frontendRoot, "server/packaged-runtime-smoke.mjs"),
      "utf8",
    );

    expect(entrypoint).toContain(
      'su-exec "$USER_NAME:$GROUP_NAME" node dist-node/bootstrap.js &',
    );
    expect(packagedSmoke).toContain(
      'spawn("node", ["dist-node/bootstrap.js"], {',
    );
    expect(entrypoint).not.toContain('npm run start');
    expect(packagedSmoke).not.toContain('spawn("npm", ["start"');
  });

  it("keeps the root Dockerfile as the sole supported packaged container surface", async () => {
    const dockerfile = await readFile(resolve(frontendRoot, "../Dockerfile"), "utf8");
    const retiredSurfaces = [
      resolve(frontendRoot, "Dockerfile"),
      resolve(frontendRoot, "../backend/Dockerfile"),
      resolve(frontendRoot, "../backend/entrypoint.sh"),
    ];

    expect(dockerfile).toContain("COPY --from=frontend-build /frontend/dist-node ./frontend/dist-node");
    expect(dockerfile).toContain('ENTRYPOINT ["/entrypoint.sh"]');
    expect(await existing(retiredSurfaces)).toEqual([]);
  });

  it("keeps typecheck no-emit and creates a complete fresh Node ESM tree", async () => {
    const sourceArtifacts = [
      resolve(frontendRoot, "server/process-output.js"),
      resolve(frontendRoot, "server/process-output.d.ts"),
    ];
    const staleOutput = resolve(frontendRoot, "dist-node/stale-owned-marker.js");
    await mkdir(resolve(frontendRoot, "dist-node"), { recursive: true });
    await writeFile(staleOutput, "stale", "utf8");

    expect(run("npm", ["run", "typecheck"]).status).toBe(0);
    expect(await existing(sourceArtifacts)).toEqual([]);
    expect(run("npm", ["run", "build:server"]).status).toBe(0);
    expect(await existing([staleOutput])).toEqual([]);

    const runtimeFiles = [
      "bootstrap.js",
      "server.js",
      "app/utils/public-failure.js",
      "server/debug-output.js",
      "server/public-failure-response.js",
      "server/react-router-handler.js",
      "server/request-policy.js",
      "server/websocket-policy.js",
    ];
    expect(await existing(runtimeFiles.map(path => resolve(frontendRoot, "dist-node", path))))
      .toEqual(runtimeFiles.map(path => resolve(frontendRoot, "dist-node", path)));

    for (const path of runtimeFiles.filter(path => path.endsWith(".js"))) {
      const source = await readFile(resolve(frontendRoot, "dist-node", path), "utf8");
      const relativeSpecifiers = [...source.matchAll(/(?:from\s+|import\()(["'])(\.\.?\/[^"']+)\1/gu)]
        .map(match => match[2]);
      expect(relativeSpecifiers.every(specifier => specifier.endsWith(".js"))).toBe(true);
    }
  }, 30_000);

  it("declares the directly required debug runtime at the locked version", async () => {
    const packageJson = JSON.parse(await readFile(resolve(frontendRoot, "package.json"), "utf8"));
    const lockfile = JSON.parse(await readFile(resolve(frontendRoot, "package-lock.json"), "utf8"));

    expect(packageJson.dependencies.debug).toBe("4.4.3");
    expect(lockfile.packages[""].dependencies.debug).toBe("4.4.3");
    expect(lockfile.packages["node_modules/debug"].version).toBe("4.4.3");
  });

  it("installs the fixed fatal boundary before importing the compiled server body", async () => {
    const root = await mkdtemp(join(tmpdir(), "pinrail-frontend-bootstrap-"));
    temporaryRoots.push(root);
    await writeFile(join(root, "package.json"), JSON.stringify({ type: "module" }), "utf8");
    await writeFile(
      join(root, "bootstrap.js"),
      await readFile(resolve(frontendRoot, "dist-node/bootstrap.js"), "utf8"),
      "utf8",
    );
    const hostile = "credential-marker|/private/runtime/path|http://user:pass@backend\r\n\u001b[31m";
    await writeFile(join(root, "server.js"), `throw new Error(${JSON.stringify(hostile)});\n`, "utf8");

    const result = await spawnBounded(process.execPath, [join(root, "bootstrap.js")], root);
    const output = `${result.stdout}${result.stderr}`;

    expect(result.exitCode).not.toBe(0);
    expect(output === "frontend_startup_failure code=startup_failed\n").toBe(true);
    expect(output.includes(hostile)).toBe(false);
  }, 15_000);

  it("starts the native packaged Node runtime and serves exact success and failure probes", () => {
    expect(run("npm", ["run", "build"]).status).toBe(0);
    expect(run("npm", ["run", "build:server"]).status).toBe(0);
    expect(run(process.execPath, ["server/packaged-runtime-smoke.mjs"]).status).toBe(0);
  }, 45_000);
});

async function existing(paths: string[]): Promise<string[]> {
  const results = await Promise.all(paths.map(async path => {
    try {
      await readFile(path);
      return path;
    } catch {
      return undefined;
    }
  }));
  return results.filter((path): path is string => path !== undefined);
}

function run(command: string, args: string[]) {
  return spawnSync(command, args, {
    cwd: frontendRoot,
    encoding: "utf8",
    stdio: "pipe",
    timeout: 40_000,
  });
}

function spawnBounded(command: string, args: string[], cwd: string): Promise<{
  exitCode: number | null;
  stderr: string;
  stdout: string;
}> {
  return new Promise((resolveResult, reject) => {
    const child = spawn(command, args, { cwd, stdio: ["ignore", "pipe", "pipe"] });
    const stdout: Buffer[] = [];
    const stderr: Buffer[] = [];
    const deadline = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error("Packaged bootstrap fixture exceeded its time bound."));
    }, 5_000);
    child.stdout.on("data", (chunk: Buffer) => stdout.push(chunk));
    child.stderr.on("data", (chunk: Buffer) => stderr.push(chunk));
    child.once("error", reject);
    child.once("exit", exitCode => {
      clearTimeout(deadline);
      resolveResult({
        exitCode,
        stderr: Buffer.concat(stderr).toString("utf8"),
        stdout: Buffer.concat(stdout).toString("utf8"),
      });
    });
  });
}
