import { describe, expect, it } from "vitest";
import { getQueueRemovalIds } from "./queue-table";
import type { PresentationQueueSlot } from "../../route";

describe("getQueueRemovalIds", () => {
    it("separates local uploads from backend queue items", () => {
        const slots = [
            queueSlot("uploading", true),
            queueSlot("queued", false),
        ];

        const result = getQueueRemovalIds(slots, new Set(["uploading", "queued", "hidden"]));

        expect(result.uploadingIds).toEqual(new Set(["uploading"]));
        expect(result.queuedIds).toEqual(new Set(["queued"]));
    });

    it("returns no backend queue ids when only local uploads are selected", () => {
        const slots = [queueSlot("uploading", true)];

        const result = getQueueRemovalIds(slots, new Set(["uploading"]));

        expect(result.uploadingIds).toEqual(new Set(["uploading"]));
        expect(result.queuedIds).toEqual(new Set());
    });
});

function queueSlot(nzo_id: string, isUploading: boolean): PresentationQueueSlot {
    return {
        nzo_id,
        filename: `${nzo_id}.nzb`,
        status: isUploading ? "uploading" : "Queued",
        priority: "Normal",
        true_percentage: "0",
        isUploading,
    } as PresentationQueueSlot;
}
