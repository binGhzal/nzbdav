import { withUrlBase } from "~/utils/url-base";
import { readJsonObjectOrEmpty } from "~/utils/http-response";

export type MaintenanceRunStatus =
    "queued"
    | "running"
    | "completed"
    | "failed"
    | "cancellation-requested"
    | "cancelled"
    | "interrupted";

export type MaintenanceRunKind =
    "remove-unlinked-files"
    | "remove-unlinked-files-dry-run"
    | "convert-strm-to-symlinks"
    | "recreate-strm-files";

export type MaintenanceRun = {
    id: string;
    kind: MaintenanceRunKind;
    status: MaintenanceRunStatus;
    requestedBy: string;
    createdAt: string;
    startedAt: string | null;
    updatedAt: string;
    completedAt: string | null;
    cancellationRequestedAt: string | null;
    progressCurrent: number;
    progressTotal: number | null;
    message: string | null;
    error: string | null;
};

export async function startMaintenanceTask(
    path: string,
    label: string,
    setError: (message: string | null) => void,
) {
    try {
        const response = await fetch(withUrlBase(path), { method: "POST" });
        const body = await readJsonObjectOrEmpty<{ run?: unknown, error?: unknown }>(response);
        if (response.status !== 202) {
            const detail = typeof body.error === "string" && body.error.trim()
                ? `: ${body.error.trim().replace(/\.$/, "")}.`
                : ` (${response.status}).`;
            setError(`Failed to start ${label}${detail}`);
            return null;
        }

        if (!isMaintenanceRun(body.run)) {
            setError(`Failed to start ${label}: the server returned an invalid run response.`);
            return null;
        }

        setError(null);
        return body.run;
    } catch (error) {
        const message = error instanceof Error && error.message ? error.message : "unknown error";
        setError(`Failed to start ${label}: ${message}.`);
        return null;
    }
}

function isMaintenanceRun(value: unknown): value is MaintenanceRun {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const run = value as Record<string, unknown>;
    return typeof run.id === "string"
        && typeof run.kind === "string"
        && typeof run.status === "string";
}
