import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ArrsSettings } from "./arrs";
import { serializeArrConfig } from "./arr-config";

describe("ArrsSettings", () => {
    afterEach(() => {
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("shows the backend error body when an ARR connection test fails", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 503 })));
        const config = {
            "arr.instances": serializeArrConfig({
                RadarrInstances: [{ Host: "http://radarr:7878", ApiKey: "api-key" }],
                SonarrInstances: [],
                LidarrInstances: [],
                QueueRules: [],
                Prioritization: {
                    Enabled: false,
                    Mode: "report",
                    RecomputeIntervalSeconds: 300,
                    MaxAutomaticPriority: 1,
                },
                SearchNudge: {
                    Enabled: false,
                    Mode: "report",
                    IntervalSeconds: 1800,
                    CooldownSeconds: 21600,
                    MaxCommandsPerHour: 20,
                    SonarrBatchSize: 10,
                    RadarrBatchSize: 5,
                    ConcurrentCommandsPerInstance: 1,
                },
            }),
        };

        render(<ArrsSettings config={config} setNewConfig={vi.fn()} />);

        fireEvent.click(screen.getByRole("button", { name: "Test Conn" }));

        await waitFor(() => {
            expect(screen.getByRole("alert").textContent).toContain("Radarr connection failed: server error");
        });
    });
});
