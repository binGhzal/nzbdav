import { expect, test } from "@playwright/test";

test("frontend server exposes a local health probe", async ({ request }) => {
  const response = await request.get("/healthz");

  expect(response.ok()).toBe(true);
  expect(await response.text()).toBe("ok");
});
