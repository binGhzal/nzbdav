import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RcloneSettings } from "./rclone";

describe("RcloneSettings", () => {
    afterEach(() => {
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("shows the backend error body when the connection test fails", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => new Response("server error", { status: 500 })));
        const config = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
        };

        render(<RcloneSettings config={config} setNewConfig={vi.fn()} />);

        fireEvent.click(screen.getByRole("button", { name: "Test Conn" }));

        await waitFor(() => {
            expect(screen.getByRole("alert").textContent).toContain("Rclone connection failed: server error");
        });
    });
});
