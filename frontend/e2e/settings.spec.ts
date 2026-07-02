import { expect, test } from "@playwright/test";

const mockBackendURL = process.env.PLAYWRIGHT_BACKEND_URL ?? "http://127.0.0.1:5174";

test.beforeEach(async ({ request }) => {
  await request.post(`${mockBackendURL}/__e2e/reset`);
});

test("settings tabs load by URL and save changed values", async ({ page, request }) => {
  await page.goto("/settings?tab=rclone");

  await expect(page.getByRole("tab", { name: "Rclone Server" })).toHaveAttribute("aria-selected", "true");

  await page.getByRole("tab", { name: "WebDAV" }).click();
  await expect(page).toHaveURL(/tab=webdav/);
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
