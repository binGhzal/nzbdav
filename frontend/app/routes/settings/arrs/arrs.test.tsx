import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ArrsSettings } from "./arrs";
import { serializeArrConfig } from "./arr-config";

describe("ArrsSettings", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("rejects an arbitrary backend error body when an ARR connection test fails", async () => {
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
            expect(screen.getByRole("alert").textContent).toBe("Radarr connection failed: HTTP 503");
        });
    });

    it("renders a stable ARR failure envelope", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: false, error: "The request could not be completed.", code: "internal_error",
            correlation_id: "0123456789abcdef0123456789abcdef",
        }, { status: 500 })));
        const config = { "arr.instances": serializeArrConfig({
            RadarrInstances: [{ Host: "http://radarr:7878", ApiKey: "api-key" }],
            SonarrInstances: [], LidarrInstances: [], QueueRules: [],
            Prioritization: { Enabled: false, Mode: "report", RecomputeIntervalSeconds: 300, MaxAutomaticPriority: 1 },
            SearchNudge: { Enabled: false, Mode: "report", IntervalSeconds: 1800, CooldownSeconds: 21600, MaxCommandsPerHour: 20, SonarrBatchSize: 10, RadarrBatchSize: 5, ConcurrentCommandsPerInstance: 1 },
        }) };

        render(<ArrsSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByRole("button", { name: "Test Conn" }));

        await waitFor(() => expect(screen.getByRole("alert").textContent).toBe(
            "Radarr connection failed: The request could not be completed. (0123456789abcdef0123456789abcdef)"));
    });

    it("submits the application type with the saved API-key marker", async () => {
        let submittedType: FormDataEntryValue | null = null;
        let submittedApiKey: FormDataEntryValue | null = null;
        const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
            const form = init.body as FormData;
            submittedType = form.get("type");
            submittedApiKey = form.get("apiKey");
            return Response.json({ status: true, connected: true });
        });
        vi.stubGlobal("fetch", fetchMock);
        const config = {
            "arr.instances": serializeArrConfig({
                RadarrInstances: [{
                    Host: "http://radarr:7878",
                    ApiKey: "__NZBDAV_REDACTED__",
                }],
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

        await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
        expect(submittedType).toBe("radarr");
        expect(submittedApiKey).toBe("__NZBDAV_REDACTED__");
    });
});
