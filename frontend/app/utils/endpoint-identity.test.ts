import { describe, expect, test } from "vitest";
import { areEndpointIdentitiesEquivalent } from "./endpoint-identity";

describe("areEndpointIdentitiesEquivalent", () => {
    test("folds scheme, host, effective port, and a root-only terminal slash", () => {
        expect(areEndpointIdentitiesEquivalent(
            " HTTP://RCLONE ",
            "http://rclone:80/",
        )).toBe(true);
    });

    test.each([
        ["http://rclone/Rclone", "http://rclone/rclone"],
        ["http://rclone/api?Token=Value", "http://rclone/api?Token=value"],
        ["http://rclone/Rclone", "http://rclone/Rclone/"],
    ])("preserves path, query, and non-root terminal slashes", (first, second) => {
        expect(areEndpointIdentitiesEquivalent(first, second)).toBe(false);
    });
});
