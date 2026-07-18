import { expect, test } from "@playwright/test";
import { authenticateE2E } from "./auth";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
  await authenticateE2E(page.context().request);
});

test("health page renders repair, cache, mount, provider, and worker diagnostics", async ({ page, request }) => {
  await page.goto("/health");

  await expect(page.getByRole("heading", { name: "Repair" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Cache" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Mount" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Providers" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Workers" })).toBeVisible();
  await expect(page.getByText("usenet.example.test:563")).toBeVisible();
  await expect(page.getByText("/mnt/nzbdav")).toBeVisible();
  await expect(page.getByText("25% used")).toBeVisible();
  await expect(page.getByText("Rclone invalidations are waiting to drain.")).not.toBeVisible();
  await expect(page.getByText("Unverified Movie.mkv", { exact: true })).toBeVisible();
  await expect(page.getByText("active 2/2")).toBeVisible();

  await page.getByRole("button", { name: "Cancel" }).click();

  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string }) =>
      entry.method === "POST" && entry.path === "/api/repair/run/run-1/cancel"
    )).toBe(true);
  }).toPass();
});

test("health page wires ARR operation commands and manual correlations", async ({ page, request }) => {
  await page.goto("/health");

  await expect(page.getByRole("heading", { name: "ARR Validation" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "ARR Search Commands" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "ARR Correlations" })).toBeVisible();
  await expect(page.getByText("ARR timeout")).toBeVisible();
  await expect(page.getByText("Downloading Release")).toBeVisible();
  await expect(page.getByText("manual")).toBeVisible();
  await expect(page.getByText("locked")).toBeVisible();

  const commandSection = page.locator("section").filter({ has: page.getByRole("heading", { name: "ARR Search Commands" }) });
  await commandSection.locator("details summary").first().click();
  await expect(page.getByText("Score 300")).toBeVisible();
  await expect(page.getByText("Command id 42")).toBeVisible();

  await commandSection.locator('select[name="arr_app"]').selectOption("sonarr");
  await commandSection.locator('select[name="arr_status"]').selectOption("failed");
  await commandSection.locator('select[name="arr_mode"]').selectOption("apply");
  await commandSection.locator('select[name="arr_command"]').selectOption("EpisodeSearch");
  await commandSection.getByPlaceholder("Search commands").fill("timeout");
  await commandSection.getByRole("button", { name: "Filter" }).click();
  await expect(page.getByText("ARR timeout")).toBeVisible();
  await expect(page.getByText("MoviesSearch")).not.toBeVisible();
  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string; query: Record<string, string> }) =>
      entry.method === "GET"
      && entry.path === "/api/arr/search-nudges"
      && entry.query.app === "sonarr"
      && entry.query.status === "failed"
      && entry.query.mode === "apply"
      && entry.query.command === "EpisodeSearch"
      && entry.query.search === "timeout"
    )).toBe(true);
  }).toPass();

  await page.getByRole("button", { name: "Retry" }).click();
  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string }) =>
      entry.method === "POST" && entry.path === "/api/arr/search-nudges/nudge-2/retry"
    )).toBe(true);
  }).toPass();

  await page.getByRole("button", { name: "Edit" }).click();
  await expect(page.getByPlaceholder("Release title")).toHaveValue("Downloading Release");
  await expect(page.getByLabel("Lock")).toBeChecked();
  await page.getByPlaceholder("Release title").fill("Manual Release");
  await page.getByRole("button", { name: "Save Edit" }).click();

  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string; body: string }) =>
      entry.method === "POST"
      && entry.path === "/api/arr/correlations"
      && entry.body.includes('"id":"corr-1"')
      && entry.body.includes('"manual_lock":true')
    )).toBe(true);
  }).toPass();

  await page.getByRole("button", { name: "Delete" }).click();
  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string }) =>
      entry.method === "DELETE" && entry.path === "/api/arr/correlations/corr-1"
    )).toBe(true);
  }).toPass();
});
