import {
    fallbackHttpFailure,
    readPublicFailureBody,
    renderPublicFailure,
    resolvePublicFailureEnvelope,
} from "./public-failure";

export async function getHttpErrorMessage(response: Response): Promise<string> {
    const text = (await readPublicFailureBody(response))?.trim() ?? "";
    const envelope = resolvePublicFailureEnvelope(text, response.headers);
    return envelope ? renderPublicFailure(envelope) : fallbackHttpFailure(response.status);
}

export type HttpActionResult = {
    success: boolean;
    data: Record<string, unknown>;
    error: string;
};

// Queue/history mutation endpoints have a deliberately tiny `{ status: true }`
// success contract. This helper must not be used for general response payloads.
export async function readHttpActionResult(response: Response): Promise<HttpActionResult> {
    const text = (await readPublicFailureBody(response))?.trim() ?? "";
    const envelope = resolvePublicFailureEnvelope(text, response.headers);
    let data: Record<string, unknown> = {};
    try {
        const value: unknown = JSON.parse(text);
        if (isRecord(value)) data = value;
    } catch {
        // Invalid and oversized action bodies have a fixed status-only fallback.
    }
    return {
        success: response.ok && data.status === true,
        data,
        error: envelope ? renderPublicFailure(envelope) : fallbackHttpFailure(response.status),
    };
}

export async function readJsonObjectOrEmpty<T extends Record<string, unknown> = Record<string, unknown>>(
    response: Response
): Promise<T> {
    try {
        const data = await response.json();
        return isRecord(data) ? data as T : {} as T;
    } catch {
        return {} as T;
    }
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
