import { defineConfig, devices } from "@playwright/test";

const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? "http://127.0.0.1:5173";
const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";
const reuseExistingServer = process.env.PLAYWRIGHT_REUSE_SERVER === "true";
const e2eSessionKey = "3".repeat(64);

export default defineConfig({
  testDir: "./e2e",
  workers: 1,
  timeout: 30_000,
  expect: {
    timeout: 5_000,
  },
  use: {
    baseURL,
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: process.env.PLAYWRIGHT_BASE_URL
    ? undefined
    : [
        {
          command: "tsx test/mock-backend.ts",
          url: `${mockBackendURL}/health`,
          reuseExistingServer,
          timeout: 120_000,
        },
        {
          command: `cross-env BACKEND_URL=${mockBackendURL} FRONTEND_BACKEND_API_KEY=e2e AUTH_MODE=local SESSION_KEY=${e2eSessionKey} SECURE_COOKIES=false ALLOW_INSECURE_COOKIES=true npm run dev`,
          url: `${baseURL}/healthz`,
          reuseExistingServer,
          timeout: 120_000,
        },
      ],
});
