import { expect, test } from "@playwright/test";
import { authenticateE2E } from "./auth";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
  await authenticateE2E(page.context().request);
});

test("queue filters, page size, and pause controls update live UI and backend calls", async ({ page, request }) => {
  await page.goto("/queue");

  await expect(page.getByText("Downloading Release")).toBeVisible();
  await expect(page.getByText("Queued Release")).toBeVisible();

  await page.goto("/queue?queueStatus=queued");
  await expect(page).toHaveURL(/queueStatus=queued/);
  await expect(page.getByText("Queued Release")).toBeVisible();
  await expect(page.getByText("Downloading Release")).not.toBeVisible();

  await page.goto("/queue?queueStatus=queued&pageSize=100");
  await expect(page).toHaveURL(/pageSize=100/);

  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await expect(page.getByRole("button", { name: "Resume", exact: true })).toBeVisible();

  const response = await request.get(`${mockBackendURL}/__e2e/requests`);
  const { requests } = await response.json();

  expect(requests.some((entry: { query: Record<string, string> }) =>
    entry.query.mode === "queue" && entry.query.status === "queued"
  )).toBe(true);
  expect(requests.some((entry: { query: Record<string, string> }) =>
    entry.query.mode === "queue" && entry.query.limit === "100"
  )).toBe(true);
  expect(requests.some((entry: { query: Record<string, string> }) =>
    entry.query.mode === "pause"
  )).toBe(true);
});
