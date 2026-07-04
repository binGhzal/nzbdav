import { expect, test } from "@playwright/test";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
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
  await expect(page.getByText("Rclone invalidations are waiting to drain.")).toBeVisible();
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

  await page.getByRole("button", { name: "Retry" }).click();
  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string }) =>
      entry.method === "POST" && entry.path === "/api/arr/search-nudges/nudge-2/retry"
    )).toBe(true);
  }).toPass();

  await page.getByPlaceholder("NZBDav nzo_id").fill("11111111-1111-1111-1111-111111111111");
  await page.getByPlaceholder("ARR host").fill("http://sonarr:8989");
  await page.getByPlaceholder("ARR download id").fill("11111111-1111-1111-1111-111111111111");
  await page.getByPlaceholder("Series id").fill("456");
  await page.getByPlaceholder("Episode id").fill("123");
  await page.getByPlaceholder("Release title").fill("Manual Release");
  await page.getByRole("button", { name: "Save" }).click();

  await expect(async () => {
    const response = await request.get(`${mockBackendURL}/__e2e/requests`);
    const { requests } = await response.json();
    expect(requests.some((entry: { path: string; method: string }) =>
      entry.method === "POST" && entry.path === "/api/arr/correlations"
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
