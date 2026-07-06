import { afterEach, describe, expect, it, vi } from "vitest";
import { backendClient } from "~/clients/backend-client.server";
import { loader } from "./route";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        getQueue: vi.fn(async () => ({
            slots: [],
            noofslots: 0,
            paused: false,
            status: "Idle",
        })),
        getHistory: vi.fn(async () => ({
            slots: [],
            noofslots: 0,
        })),
        getConfig: vi.fn(async () => []),
    },
}));

describe("queue route loader", () => {
    afterEach(() => {
        vi.clearAllMocks();
    });

    it("accepts the backend-supported 1000 row page size", async () => {
        const result = await loader({
            request: new Request("https://example.test/queue?pageSize=1000"),
        } as never);

        expect(result.pageSize).toBe(1000);
        expect(result.pageSizeOptions).toContain(1000);
        expect(backendClient.getQueue).toHaveBeenCalledWith(expect.objectContaining({
            start: 0,
            limit: 1000,
        }));
        expect(backendClient.getHistory).toHaveBeenCalledWith(0, 1000);
    });
});
