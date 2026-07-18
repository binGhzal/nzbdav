import { act, cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Health from "./route";
import type { HealthCheckQueueItem, HealthCheckStats } from "~/clients/backend-client.server";

const websocketHarness = vi.hoisted(() => ({
    onMessage: undefined as ((topic: string, message: string) => void) | undefined,
}));
const revalidatorHarness = vi.hoisted(() => ({
    state: "idle" as "idle" | "loading",
    revalidate: vi.fn(async () => undefined),
}));
const visibilityHarness = vi.hoisted(() => ({
    state: "visible" as DocumentVisibilityState,
}));

vi.mock("react-router", async importOriginal => {
    const actual = await importOriginal<typeof import("react-router")>();
    return {
        ...actual,
        useNavigation: () => ({ state: "idle", formMethod: undefined }),
        useRevalidator: () => revalidatorHarness,
    };
});

vi.mock("~/utils/websocket-util", () => ({
    createReconnectingWebSocket: vi.fn((options: {
        onMessage: (topic: string, message: string) => void,
    }) => {
        websocketHarness.onMessage = options.onMessage;
        return vi.fn();
    }),
}));

vi.mock("./health-queue-top-up", () => ({
    useHealthQueueTopUp: vi.fn(),
}));

vi.mock("./components/health-stats/health-stats", () => ({
    HealthStats: ({ stats }: { stats: HealthCheckStats[] }) =>
        <div data-testid="history-stats">{stats.reduce((total, stat) => total + stat.count, 0)}</div>,
}));

vi.mock("./components/operations-status/operations-status", () => ({
    OperationsStatus: () => null,
}));

describe("health queue progress events", () => {
    beforeEach(() => {
        websocketHarness.onMessage = undefined;
        revalidatorHarness.state = "idle";
        revalidatorHarness.revalidate.mockReset();
        revalidatorHarness.revalidate.mockResolvedValue(undefined);
        visibilityHarness.state = "visible";
        Object.defineProperty(document, "visibilityState", {
            configurable: true,
            get: () => visibilityHarness.state,
        });
    });

    afterEach(() => {
        cleanup();
        vi.clearAllMocks();
    });

    it("preserves every queue row and its order across progress updates", () => {
        renderHealth([
            healthItem("first", "First item"),
            healthItem("middle", "Middle item"),
            healthItem("last", "Last item"),
        ]);

        expect(websocketHarness.onMessage).toBeTypeOf("function");

        act(() => {
            websocketHarness.onMessage?.("hp", "middle|42");
            websocketHarness.onMessage?.("hp", "last|73");
        });

        const table = screen.getByRole("table");
        const dataRows = within(table).getAllByRole("row").slice(1);
        expect(dataRows.map(row => row.textContent)).toEqual([
            expect.stringContaining("First item"),
            expect.stringContaining("Middle item"),
            expect.stringContaining("Last item"),
        ]);
        expect(dataRows[0].textContent).not.toContain("42%");
        expect(dataRows[1].textContent).toContain("42%");
        expect(dataRows[2].textContent).toContain("73%");
    });

    it("revalidates loader data on an interval while the page is visible", async () => {
        vi.useFakeTimers();
        renderHealth([healthItem("first", "First item")]);

        await vi.advanceTimersByTimeAsync(5_000);

        expect(revalidatorHarness.revalidate).toHaveBeenCalledTimes(1);
        vi.useRealTimers();
    });

    it("reconciles queue, unchecked count, and history stats from refreshed loader data", () => {
        const initial = healthLoaderData(
            [healthItem("old", "Old item")],
            25,
            [{ result: 0, repairStatus: 0, count: 1 }] as HealthCheckStats[],
        );
        const refreshed = healthLoaderData(
            [healthItem("new", "New item")],
            1,
            [{ result: 0, repairStatus: 0, count: 7 }] as HealthCheckStats[],
        );
        const view = render(<Health {...initial} />);

        expect(screen.getByText(/You have ~25 files/)).toBeTruthy();
        expect(screen.getByTestId("history-stats").textContent).toBe("1");

        view.rerender(<Health {...refreshed} />);

        expect(screen.queryByText(/You have ~25 files/)).toBeNull();
        expect(screen.getByTestId("history-stats").textContent).toBe("7");
        const table = screen.getByRole("table");
        expect(within(table).getByText("New item")).toBeTruthy();
        expect(within(table).queryByText("Old item")).toBeNull();
    });
});

function renderHealth(queueItems: HealthCheckQueueItem[]) {
    return render(<Health {...healthLoaderData(queueItems)} />);
}

function healthLoaderData(
    queueItems: HealthCheckQueueItem[],
    uncheckedCount = queueItems.length,
    historyStats: HealthCheckStats[] = [],
) {
    return {
        loaderData: {
            uncheckedCount,
            queueItems,
            historyStats,
            historyItems: [],
            repairStatus: null,
            repairStatusError: null,
            fullStatus: null,
            fullStatusError: null,
            arrValidation: null,
            arrValidationError: null,
            arrNudges: null,
            arrNudgesError: null,
            arrCorrelations: null,
            arrCorrelationsError: null,
            arrFilters: {},
            isEnabled: true,
        },
    } as unknown as Parameters<typeof Health>[0];
}

function healthItem(id: string, name: string): HealthCheckQueueItem {
    return {
        id,
        name,
        path: `/library/${id}`,
        releaseDate: null,
        lastHealthCheck: null,
        nextHealthCheck: null,
        progress: 0,
    };
}
