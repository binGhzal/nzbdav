import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createElement } from "react";
import type { ArrImportCommandStatus, CriticalPathStatus, FullStatusResponse } from "~/clients/backend-client.server";
import { ArrImportMetrics, CriticalPathPanel, getDegradedMessages, RcloneInvalidationMetrics } from "./operations-status";

afterEach(cleanup);

describe("getDegradedMessages", () => {
    it("does not report ordinary pending rclone invalidations as degradation", () => {
        const messages = getDegradedMessages(
            fullStatus({
                pending: 3,
                ready: 2,
                failed: 0,
                oldest_pending_age_seconds: 2,
                last_successful_configured_call_at: "2026-07-12T08:59:59Z",
            }),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "connected",
        );

        expect(messages).not.toContain("Rclone invalidations are waiting to drain.");
    });

    it("reports an impossible visibility-fence configuration immediately", () => {
        const messages = degradedMessages(fullStatus({ remote_control_enabled: false }));

        expect(messages).toContain("Rclone remote control is disabled while the rclone visibility fence is required.");
    });

    it("reports a pending whole-cache visibility fence even without path rows", () => {
        const status = fullStatus({
            pending: 0,
            ready: 0,
            whole_cache_visibility_fence_pending: true,
        });

        const messages = degradedMessages(status);
        render(createElement(RcloneInvalidationMetrics, { status: status.rclone_invalidations }));

        expect(messages).toContain("Rclone whole-cache visibility fence is pending.");
        expect(screen.getByText("Whole-cache Fence")).toBeTruthy();
        expect(screen.getByText("pending")).toBeTruthy();
    });

    it("reports a missing configured rclone host immediately", () => {
        const messages = degradedMessages(fullStatus({ host_configured: false }));

        expect(messages).toContain("Rclone remote control has no configured host while the visibility fence is required.");
    });

    it("does not report a momentary invalidation before successful-call evidence exists", () => {
        const messages = degradedMessages(fullStatus({
            pending: 1,
            ready: 1,
            oldest_pending_age_seconds: 2,
            last_successful_configured_call_at: null,
        }));

        expect(messages).not.toContain("Rclone has no successful configured remote-control call for the pending visibility fence.");
    });

    it("reports aged invalidations with no successful configured-call evidence", () => {
        const messages = degradedMessages(fullStatus({
            pending: 1,
            ready: 1,
            oldest_pending_age_seconds: 12,
            last_successful_configured_call_at: null,
        }));

        expect(messages).toContain("Rclone has no successful configured remote-control call for the pending visibility fence.");
    });

    it("reports an aged invalidation backlog after configured calls have succeeded", () => {
        const messages = degradedMessages(fullStatus({
            pending: 2,
            ready: 2,
            oldest_pending_age_seconds: 12,
        }));

        expect(messages).toContain("Rclone invalidations have been pending for more than 10 seconds.");
    });

    it("does not degrade on parked rclone proof rows while another mount owns visibility", () => {
        const messages = degradedMessages(fullStatus({
            visibility_fence_required: false,
            pending: 1,
            ready: 1,
            oldest_pending_age_seconds: 12,
            last_successful_configured_call_at: null,
        }));

        expect(messages).not.toContain("Rclone invalidations have been pending for more than 10 seconds.");
        expect(messages).not.toContain("Rclone has no successful configured remote-control call for the pending visibility fence.");
    });

    it("reports configured-call failures without echoing the backend error", () => {
        const messages = degradedMessages(fullStatus({ runtime_last_error: "secret-shaped provider detail" }));

        expect(messages).toContain("The latest configured rclone remote-control call failed.");
        expect(messages.join(" ")).not.toContain("secret-shaped provider detail");
    });

    it("still reports invalidations that have failed", () => {
        const messages = getDegradedMessages(
            fullStatus({ pending: 1, ready: 1, failed: 1 }),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "connected",
        );

        expect(messages).toContain("Rclone invalidation failures need attention.");
    });
});

describe("RcloneInvalidationMetrics", () => {
    it("labels due work as due instead of ambiguously calling it ready", () => {
        render(createElement(RcloneInvalidationMetrics, {
            status: fullStatus({ pending: 3, ready: 2 }).rclone_invalidations,
        }));

        expect(screen.getByText("Due Invalidations")).toBeTruthy();
        expect(screen.queryByText("Ready")).toBeNull();
        expect(screen.getByText("Oldest Pending")).toBeTruthy();
        expect(screen.getByText("RC Evidence")).toBeTruthy();
    });
});

describe("CriticalPathPanel", () => {
    it("shows p95/p99 stage latency and failures without inventing samples", () => {
        const status = Object.fromEntries([
            "add_file_blob_write",
            "add_file_nzb_scan",
            "add_file_atomic_commit",
            "queue_parse",
            "queue_first_segment_discovery",
            "queue_par2_discovery",
            "queue_processors",
            "queue_completion",
        ].map((name, index) => [name, {
            count: index === 7 ? 12 : 4,
            failures: name === "queue_processors" ? 2 : 0,
            latency_samples: name === "queue_par2_discovery" ? 0 : 4,
            p95_ms: 10 + index,
            p99_ms: 20 + index,
        }])) as unknown as CriticalPathStatus;

        render(createElement(CriticalPathPanel, { status }));

        expect(screen.getByText("Critical Path")).toBeTruthy();
        expect(screen.getByText("12 completions")).toBeTruthy();
        expect(screen.getByText("14/24 ms")).toBeTruthy();
        expect(screen.getByText("no samples")).toBeTruthy();
        expect(screen.getByText("Processors · 2 failures")).toBeTruthy();
    });
});

describe("ArrImportMetrics", () => {
    it("renders quarantined imports and their reason as danger state", () => {
        const status: ArrImportCommandStatus = {
            pending: 0,
            waiting_for_invalidation: 0,
            executing: 0,
            retry: 0,
            dispatched: 3,
            no_route: 0,
            quarantined: 2,
            oldest_active_age_seconds: null,
            last_error: "confirmed missing articles",
            last_quarantine_reason: "automatic repair disabled after confirmed missing articles",
        };

        render(createElement(ArrImportMetrics, { status }));

        expect(screen.getByText("Import Quarantine")).toBeTruthy();
        expect(screen.getByText("2").className).toContain("danger");
        expect(screen.getByText(status.last_quarantine_reason!)).toBeTruthy();
    });
});

function degradedMessages(status: FullStatusResponse) {
    return getDegradedMessages(status, null, null, null, null, null, null, null, "connected");
}

function fullStatus(
    rcloneInvalidations: Partial<FullStatusResponse["rclone_invalidations"]> = {},
) {
    return {
        rclone_invalidations: {
            pending: 0,
            ready: 0,
            failed: 0,
            max_attempts: 8,
            last_error: null,
            oldest_pending_age_seconds: null,
            whole_cache_visibility_fence_pending: false,
            visibility_fence_required: true,
            remote_control_enabled: true,
            host_configured: true,
            last_attempt_at: "2026-07-12T09:00:00Z",
            last_successful_configured_call_at: "2026-07-12T09:00:00Z",
            runtime_last_error: null,
            ...rcloneInvalidations,
        },
        mount: {
            enabled: false,
            ready: true,
            fuse_errors: 0,
        },
        arr_prioritization: {
            stale_correlations: 0,
            duplicates: 0,
        },
        arr_search_nudge: {
            failed: 0,
        },
    } as FullStatusResponse;
}
