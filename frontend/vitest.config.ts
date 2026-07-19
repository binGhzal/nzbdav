import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

const serverBuildPath = fileURLToPath(new URL("./build/server/index.js", import.meta.url));
const productionEntrypointTestPath = fileURLToPath(
  new URL("./server/entrypoint.production.test.ts", import.meta.url),
);

export default defineConfig({
  define: {
    __URL_BASE__: JSON.stringify("/nzbdav"),
  },
  plugins: [
    {
      name: "pinrail-production-entrypoint-test-build",
      enforce: "pre",
      resolveId(id, importer) {
        const normalizedImporter = importer?.split("?", 1)[0];
        if (
          normalizedImporter === productionEntrypointTestPath
          && id.split("?", 1)[0] === "../build/server/index.js"
        ) {
          return serverBuildPath;
        }
      },
    },
  ],
  resolve: {
    alias: {
      "~": fileURLToPath(new URL("./app", import.meta.url)),
    },
  },
  test: {
    environment: "jsdom",
    environmentOptions: {
      jsdom: {
        url: "https://media.example.test/nzbdav/queue",
      },
    },
    include: ["app/**/*.test.ts", "app/**/*.test.tsx", "server/**/*.test.ts"],
    setupFiles: ["./test/setup.ts"],
  },
});
