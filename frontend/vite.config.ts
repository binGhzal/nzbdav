import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const appDirectory = fileURLToPath(new URL("./app", import.meta.url));

// Mirror of normalizeBasename in react-router.config.ts. Produces:
//   - `viteBase`: Vite's `base` option form, "/" or "/path/" (trailing slash required).
//   - `token`: value baked into `__URL_BASE__` for browser code, "" or "/path" (no slash).
function normalize(raw: string | undefined): { viteBase: string; token: string } {
  if (!raw) return { viteBase: "/", token: "" };
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return { viteBase: "/", token: "" };
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  const withoutTrailing = withLeading.replace(/\/+$/, "");
  return { viteBase: `${withoutTrailing}/`, token: withoutTrailing };
}

export default defineConfig(({ isSsrBuild }) => {
  const { viteBase, token } = normalize(process.env.URL_BASE);
  return {
    base: viteBase,
    server: {
      allowedHosts: [".net"],
    },
    resolve: {
      alias: [
        { find: /^~\//, replacement: `${appDirectory}/` },
      ],
    },
    build: {
      rollupOptions: isSsrBuild ? { input: "./server/app.ts" } : undefined,
    },
    css: {
      preprocessorOptions: {
        scss: {
          quietDeps: true,
          silenceDeprecations: ["import", "global-builtin", "color-functions", "if-function"],
        },
      },
    },
    define: {
      __URL_BASE__: JSON.stringify(token),
    },
    plugins: [
      tailwindcss(),
      reactRouter(),
    ],
  };
});
