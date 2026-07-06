import { withUrlBase } from "~/utils/url-base";

export async function startMaintenanceTask(
    path: string,
    label: string,
    setError: (message: string | null) => void,
) {
    try {
        const response = await fetch(withUrlBase(path));
        if (!response.ok) {
            setError(`Failed to start ${label} (${response.status}).`);
            return false;
        }

        setError(null);
        return true;
    } catch (error) {
        const message = error instanceof Error && error.message ? error.message : "unknown error";
        setError(`Failed to start ${label}: ${message}.`);
        return false;
    }
}
