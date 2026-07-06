import { afterEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";
import { backendClient } from "~/clients/backend-client.server";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        updateConfig: vi.fn(async () => true),
    },
}));

describe("settings update action", () => {
    afterEach(() => {
        vi.clearAllMocks();
    });

    it("returns 400 when config form value is missing", async () => {
        const form = new FormData();

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        expect(backendClient.updateConfig).not.toHaveBeenCalled();
    });

    it("returns 400 when config form value is malformed JSON", async () => {
        const form = new FormData();
        form.append("config", "{bad-json");

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(400);
        expect(backendClient.updateConfig).not.toHaveBeenCalled();
    });

    it("updates config entries from valid JSON object values", async () => {
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
            "queue.max-concurrent-downloads": "12",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toEqual({
            config: {
                "webdav.user": "admin",
                "queue.max-concurrent-downloads": "12",
            },
        });
        expect(backendClient.updateConfig).toHaveBeenCalledWith([
            { configName: "webdav.user", configValue: "admin" },
            { configName: "queue.max-concurrent-downloads", configValue: "12" },
        ]);
    });

    it("returns 502 when backend config update fails", async () => {
        vi.mocked(backendClient.updateConfig).mockRejectedValueOnce(new Error("backend offline"));
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(502);
    });

    it("returns 502 when backend returns unsuccessful status", async () => {
        vi.mocked(backendClient.updateConfig).mockResolvedValueOnce(false);
        const form = new FormData();
        form.append("config", JSON.stringify({
            "webdav.user": "admin",
        }));

        const response = await action({
            request: new Request("https://example.test/settings/update", {
                method: "POST",
                body: form,
            }),
        } as never);

        expect(response).toBeInstanceOf(Response);
        expect((response as Response).status).toBe(502);
    });
});
