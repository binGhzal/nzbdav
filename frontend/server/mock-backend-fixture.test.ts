import { spawn, type ChildProcess } from "node:child_process";
import net from "node:net";
import { resolve } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { WebSocket } from "ws";

const frontendRoot = process.cwd();
const mockBackendPath = resolve(frontendRoot, "test/mock-backend.ts");
const tsxCliPath = resolve(frontendRoot, "node_modules/tsx/dist/cli.mjs");

let mockBackend: ChildProcess | undefined;

afterEach(async () => {
  if (mockBackend) {
    await stopProcess(mockBackend);
    mockBackend = undefined;
  }
});

describe("mock backend websocket fixture", () => {
  it("publishes connection state through the fixture /ws endpoint", async () => {
    const port = await reservePort();
    mockBackend = spawn(process.execPath, [tsxCliPath, mockBackendPath], {
      cwd: frontendRoot,
      env: {
        ...process.env,
        MOCK_BACKEND_PORT: String(port),
      },
      stdio: ["ignore", "pipe", "pipe"],
    });

    await waitForReady(mockBackend, `Mock backend listening on http://127.0.0.1:${port}`);

    const socket = new WebSocket(`ws://127.0.0.1:${port}/ws`);
    try {
      await waitForOpen(socket);
      const stateMessage = waitForMessage(socket);

      socket.send("fixture-authentication-frame");

      await expect(stateMessage).resolves.toBe(JSON.stringify({
        Topic: "cxs",
        Message: "0|4|2|4|10|2",
      }));
    } finally {
      socket.terminate();
    }
  });
});

async function reservePort(): Promise<number> {
  const server = net.createServer();
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });

  const address = server.address();
  if (!address || typeof address === "string") {
    server.close();
    throw new Error("Failed to reserve a TCP port for the mock backend");
  }

  await new Promise<void>((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve());
  });
  return address.port;
}

function waitForReady(child: ChildProcess, marker: string): Promise<void> {
  return new Promise((resolve, reject) => {
    let output = "";
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error(`Mock backend did not become ready. Output:\n${output}`));
    }, 5_000);

    const onData = (chunk: Buffer) => {
      output += chunk.toString("utf8");
      if (output.includes(marker)) {
        cleanup();
        resolve();
      }
    };
    const onError = (error: Error) => {
      cleanup();
      reject(error);
    };
    const onExit = (code: number | null, signal: NodeJS.Signals | null) => {
      cleanup();
      reject(new Error(`Mock backend exited before readiness (code=${code}, signal=${signal}). Output:\n${output}`));
    };
    const cleanup = () => {
      clearTimeout(timeout);
      child.stdout?.off("data", onData);
      child.stderr?.off("data", onData);
      child.off("error", onError);
      child.off("exit", onExit);
    };

    child.stdout?.on("data", onData);
    child.stderr?.on("data", onData);
    child.once("error", onError);
    child.once("exit", onExit);
  });
}

function waitForOpen(socket: WebSocket): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.once("open", resolve);
    socket.once("error", reject);
  });
}

function waitForMessage(socket: WebSocket): Promise<string> {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      reject(new Error("Mock backend did not publish websocket connection state"));
    }, 2_000);

    socket.once("message", (data) => {
      clearTimeout(timeout);
      resolve(data.toString());
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
  });
}

function stopProcess(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const timeout = setTimeout(() => {
      child.kill("SIGKILL");
    }, 2_000);
    child.once("exit", () => {
      clearTimeout(timeout);
      resolve();
    });
    child.kill("SIGTERM");
  });
}
