import { describe, expect, test } from "vitest";
import { isRcloneSettingsUpdated } from "./rclone";

describe("isRcloneSettingsUpdated", () => {
    test("does not mark equivalent rclone host values as updated", () => {
        const savedConfig = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
        };
        const newConfig = {
            ...savedConfig,
            "rclone.host": " http://rclone:5572/ ",
        };

        expect(isRcloneSettingsUpdated(savedConfig, newConfig)).toBe(false);
    });

    test("does not mark whitespace-only optional credentials as updated", () => {
        const savedConfig = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
        };
        const newConfig = {
            ...savedConfig,
            "rclone.user": "   ",
            "rclone.pass": " \t ",
        };

        expect(isRcloneSettingsUpdated(savedConfig, newConfig)).toBe(false);
    });
});
