import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  define: {
    __URL_BASE__: JSON.stringify("/nzbdav"),
  },
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
    include: ["app/**/*.test.ts", "app/**/*.test.tsx"],
    setupFiles: ["./test/setup.ts"],
  },
});
