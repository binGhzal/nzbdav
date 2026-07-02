import { expect, test } from "@playwright/test";
import type { Locator, Page } from "@playwright/test";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
});

test("queue filters, page size, and pause controls update live UI and backend calls", async ({ page, request }) => {
  await page.goto("/queue");

  await expect(page.getByText("Downloading Release")).toBeVisible();
  await expect(page.getByText("Queued Release")).toBeVisible();

  const queueFilters = page.getByRole("group", { name: "Queue status filters" });
  await clickUntilUrlMatches(queueFilters.getByRole("button", { name: "Queued", exact: true }), page, /queueStatus=queued/);
  await expect(page.getByText("Queued Release")).toBeVisible();
  await expect(page.getByText("Downloading Release")).not.toBeVisible();

  await page.getByRole("button", { name: "Rows per page" }).first().click();
  await page.getByRole("option", { name: "100" }).click();
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

async function clickUntilUrlMatches(
  locator: Locator,
  page: Page,
  url: RegExp,
) {
  await expect(async () => {
    await locator.click();
    await expect(page).toHaveURL(url, { timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}
