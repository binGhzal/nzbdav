import { withUrlBase } from "~/utils/url-base";
import { getHttpErrorMessage, readJsonObjectOrEmpty } from "~/utils/http-response";

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
    | "remove-unlinked-files-dry-run";

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
    let response: Response;
    try {
        response = await fetch(withUrlBase(path), { method: "POST" });
    } catch {
        setError(`Failed to start ${label}: request failed.`);
        return null;
    }

    if (response.status !== 202) {
        setError(`Failed to start ${label}: ${await getHttpErrorMessage(response)}.`);
        return null;
    }

    try {
        const body = await readJsonObjectOrEmpty<{ run?: unknown }>(response);
        if (!isMaintenanceRun(body.run)) {
            setError(`Failed to start ${label}: the server returned an invalid run response.`);
            return null;
        }

        setError(null);
        return body.run;
    } catch {
        setError(`Failed to start ${label}: the server returned an invalid run response.`);
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
