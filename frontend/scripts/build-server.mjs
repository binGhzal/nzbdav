import { spawnSync } from "node:child_process";
import { rm } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const frontendRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const outputPath = join(frontendRoot, "dist-node");
const compilerPath = join(
  frontendRoot,
  "node_modules",
  ".bin",
  process.platform === "win32" ? "tsc.cmd" : "tsc",
);

await rm(outputPath, { force: true, recursive: true });
const result = spawnSync(compilerPath, ["-p", "tsconfig.node.build.json"], {
  cwd: frontendRoot,
  stdio: "inherit",
});

if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
