import { describe, expect, test } from "vitest";
import { isSabnzbdSettingsUpdated } from "./sabnzbd";

describe("isSabnzbdSettingsUpdated", () => {
    test("does not mark equivalent rclone mount directory values as updated", () => {
        const savedConfig = {
            "rclone.mount-dir": "/mnt/nzbdav",
        };
        const newConfig = {
            ...savedConfig,
            "rclone.mount-dir": "/mnt/nzbdav/",
        };

        expect(isSabnzbdSettingsUpdated(savedConfig, newConfig)).toBe(false);
    });

    test("does not mark whitespace around rclone mount directory as updated", () => {
        const savedConfig = {
            "rclone.mount-dir": "/mnt/nzbdav",
        };
        const newConfig = {
            ...savedConfig,
            "rclone.mount-dir": " /mnt/nzbdav/ ",
        };

        expect(isSabnzbdSettingsUpdated(savedConfig, newConfig)).toBe(false);
    });
});
