import { afterEach, describe, expect, test, vi } from "vitest";
import { backendClient } from "~/clients/backend-client.server";
import { getChangedConfig, loader, shouldBlockSettingsNavigation } from "./route";

vi.mock("~/clients/backend-client.server", () => ({
    backendClient: {
        getConfig: vi.fn(async () => []),
    },
}));

describe("getChangedConfig", () => {
    afterEach(() => {
        vi.clearAllMocks();
    });

    test("defaults adaptive download connections to off", async () => {
        const result = await loader({
            request: new Request("https://example.test/settings"),
        } as never);

        expect(result.config["usenet.adaptive-connections-enabled"]).toBe("false");
        expect(backendClient.getConfig).toHaveBeenCalled();
    });

    test("omits equivalent rclone values when other settings changed", () => {
        const savedConfig = {
            "webdav.user": "admin",
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
            "rclone.mount-dir": "/mnt/nzbdav",
        };
        const newConfig = {
            ...savedConfig,
            "webdav.user": "operator",
            "rclone.rc-enabled": "True",
            "rclone.host": " http://rclone:5572/ ",
            "rclone.user": "   ",
            "rclone.pass": " \t ",
            "rclone.mount-dir": "/mnt/nzbdav/",
        };

        expect(getChangedConfig(savedConfig, newConfig)).toEqual({
            "webdav.user": "operator",
        });
    });

    test("does not block settings tab-only navigation while dirty", () => {
        expect(shouldBlockSettingsNavigation(
            true,
            { pathname: "/settings", search: "" },
            { pathname: "/settings", search: "?tab=rclone" },
        )).toBe(false);
    });

    test("blocks navigation away from settings while dirty", () => {
        expect(shouldBlockSettingsNavigation(
            true,
            { pathname: "/settings", search: "?tab=rclone" },
            { pathname: "/queue", search: "" },
        )).toBe(true);
    });

    test("does not block when settings are clean", () => {
        expect(shouldBlockSettingsNavigation(
            false,
            { pathname: "/settings", search: "?tab=rclone" },
            { pathname: "/queue", search: "" },
        )).toBe(false);
    });
});
