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

    test.each([
        "http://rclone:5572/rclone",
        "http://rclone:5572/Rclone/",
        "http://rclone:5572/Rclone?Token=value",
    ])("detects a meaningful endpoint path or query change", (changedHost) => {
        const savedConfig = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572/Rclone?Token=Value",
            "rclone.user": "",
            "rclone.pass": "",
        };

        expect(isRcloneSettingsUpdated(savedConfig, {
            ...savedConfig,
            "rclone.host": changedHost,
        })).toBe(true);
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

    test("normalizes the optional VFS selector and detects meaningful changes", () => {
        const savedConfig = {
            "rclone.rc-enabled": "true",
            "rclone.host": "http://rclone:5572",
            "rclone.user": "",
            "rclone.pass": "",
            "rclone.fs": "nzbdav:",
        };

        expect(isRcloneSettingsUpdated(savedConfig, {
            ...savedConfig,
            "rclone.fs": "  nzbdav:  ",
        })).toBe(false);
        expect(isRcloneSettingsUpdated(savedConfig, {
            ...savedConfig,
            "rclone.fs": "another:",
        })).toBe(true);
    });
});
