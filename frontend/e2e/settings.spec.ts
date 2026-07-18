import { expect, test } from "@playwright/test";
import type { Locator, Page } from "@playwright/test";
import { authenticateE2E } from "./auth";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
  await authenticateE2E(page.context().request);
});

test("settings tabs load by URL and save changed values", async ({ page, request }) => {
  await page.goto("/settings?tab=rclone");

  await expect(page.getByRole("tab", { name: "Rclone Server" })).toHaveAttribute("aria-selected", "true");

  await clickUntilUrlMatches(page.getByRole("tab", { name: "WebDAV" }), page, /tab=webdav/);
  await expect(page.getByRole("tab", { name: "WebDAV" })).toHaveAttribute("aria-selected", "true");

  await page.getByLabel("Max Download Connections").fill("200");
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.getByRole("button", { name: /Saved/ })).toBeVisible();

  const response = await request.get(`${mockBackendURL}/__e2e/requests`);
  const { requests } = await response.json();
  const updateRequest = requests.find((entry: { path: string }) => entry.path === "/api/update-config");

  expect(updateRequest?.body).toContain('name="usenet.max-download-connections"');
  expect(updateRequest?.body).toContain("200");
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
