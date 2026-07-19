import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RcloneSettings } from "./rclone";

describe("RcloneSettings", () => {
    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("rejects an arbitrary backend error body when the connection test fails", async () => {
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
            expect(screen.getByRole("alert").textContent).toBe("Rclone connection failed: HTTP 500");
        });
    });

    it("renders a stable backend failure envelope", async () => {
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: false, error: "The request could not be completed.", code: "internal_error",
            correlation_id: "0123456789abcdef0123456789abcdef",
        }, { status: 500 })));
        const config = { "rclone.rc-enabled": "true", "rclone.host": "http://rclone:5572", "rclone.user": "", "rclone.pass": "" };

        render(<RcloneSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByRole("button", { name: "Test Conn" }));

        await waitFor(() => expect(screen.getByRole("alert").textContent).toBe(
            "Rclone connection failed: The request could not be completed. (0123456789abcdef0123456789abcdef)"));
    });

    it("offers an optional VFS selector for shared RC servers", () => {
        const config = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
            "rclone.fs": "nzbdav:",
        };

        render(<RcloneSettings config={config} setNewConfig={vi.fn()} />);

        expect((screen.getByLabelText("Rclone VFS Selector") as HTMLInputElement).value).toBe("nzbdav:");
        expect(screen.getByText(/required when the RC server has more than one active VFS/i)).toBeTruthy();
    });

    it("includes the VFS selector when testing the connection", async () => {
        let submittedFs: FormDataEntryValue | null = null;
        let submittedPassword: FormDataEntryValue | null = null;
        const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
            const form = init.body as FormData;
            submittedFs = form.get("fs");
            submittedPassword = form.get("pass");
            return Response.json({ status: true, connected: true });
        });
        vi.stubGlobal("fetch", fetchMock);
        const config = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "__NZBDAV_REDACTED__",
            "rclone.fs": "nzbdav:",
        };

        render(<RcloneSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByRole("button", { name: "Test Conn" }));

        await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
        expect(submittedFs).toBe("nzbdav:");
        expect(submittedPassword).toBe("__NZBDAV_REDACTED__");
    });
});
