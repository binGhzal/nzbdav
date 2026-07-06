import { afterEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";
import { backendClient } from "~/clients/backend-client.server";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        isOnboarding: vi.fn(async () => true),
        createAccount: vi.fn(async () => true),
    },
}));

vi.mock("~/auth/authentication.server", () => ({
    isAuthenticated: vi.fn(async () => false),
    setSessionUser: vi.fn(async () => ({})),
}));

describe("onboarding action", () => {
    afterEach(() => {
        vi.clearAllMocks();
    });

    it("rejects password confirmation mismatches before creating the account", async () => {
        const form = new FormData();
        form.append("username", "admin");
        form.append("password", "secret-a");
        form.append("confirmPassword", "secret-b");

        const response = await action({
            request: new Request("https://example.test/onboarding", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toEqual({ error: "passwords must match" });
        expect(backendClient.createAccount).not.toHaveBeenCalled();
    });
});
