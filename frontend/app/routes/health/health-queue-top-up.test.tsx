import { act, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useHealthQueueTopUp } from "./health-queue-top-up";

describe("useHealthQueueTopUp", () => {
    afterEach(() => {
        vi.restoreAllMocks();
    });

    it("does not refetch repeatedly when a short queue response replaces the array", async () => {
        const fetchMock = vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({
                items: [{ id: "replacement" }],
                uncheckedCount: 7,
            }),
        });
        vi.stubGlobal("fetch", fetchMock);

        render(<HealthQueueTopUpHarness />);

        await waitFor(() => expect(screen.getByTestId("state").textContent).toBe("replacement:7"));
        await act(async () => {
            await Promise.resolve();
            await Promise.resolve();
        });

        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(fetchMock).toHaveBeenCalledWith(
            "/nzbdav/api/get-health-check-queue?pageSize=30",
            expect.anything());
    });
});

function HealthQueueTopUpHarness() {
    const [queueItems, setQueueItems] = useState([{ id: "initial" }]);
    const [uncheckedCount, setUncheckedCount] = useState(1);
    useHealthQueueTopUp(queueItems, setQueueItems, setUncheckedCount);

    return <div data-testid="state">{queueItems[0]?.id}:{uncheckedCount}</div>;
}
