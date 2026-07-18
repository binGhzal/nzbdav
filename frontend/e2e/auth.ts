import type { APIRequestContext } from "@playwright/test";

export async function authenticateE2E(request: APIRequestContext): Promise<void> {
  const response = await request.post("/login", {
    form: {
      username: "e2e-user",
      password: "synthetic-e2e-password",
    },
  });
  if (!response.ok()) {
    throw new Error(`E2E local login failed with HTTP ${response.status()}.`);
  }
}
