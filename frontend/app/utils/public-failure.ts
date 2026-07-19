const MaximumEnvelopeBytes = 512;
export const INTERNAL_REQUEST_CORRELATION_HEADER = "x-pinrail-request-correlation";
const correlationPattern = /^[0-9a-f]{32}$/u;

const messages = {
    invalid_request: "The request is invalid.",
    authentication_required: "Authentication is required.",
    internal_error: "The request could not be completed.",
    client_closed_request: "The client closed the request.",
    content_unavailable: "The requested content is unavailable.",
    content_temporarily_unavailable: "The requested content is temporarily unavailable.",
    content_range_unavailable: "The requested content range is unavailable.",
    resource_not_found: "The requested resource was not found.",
    maintenance_run_active: "A maintenance run is already active.",
    endpoint_disabled: "This endpoint is disabled.",
    connection_timeout: "The connection test timed out.",
    arr_connection_failure: "ARR connection test failed.",
    usenet_connection_failure: "Usenet connection test failed.",
    rclone_connection_failure: "Rclone connection test failed.",
    invalid_request_target: "The request is invalid.",
    route_not_found: "The requested route was not found.",
    method_not_allowed: "The request method is not allowed.",
    upstream_unavailable: "The backend is unavailable.",
} as const;

export type PublicFailureCode = keyof typeof messages;

export type PublicFailureEnvelope = {
    status: false;
    error: string;
    code: PublicFailureCode;
    correlation_id: string;
};

export function createPublicFailureEnvelope(
    code: PublicFailureCode,
    correlationId: string = createPublicFailureCorrelationId(),
): PublicFailureEnvelope {
    const normalizedCorrelationId = correlationPattern.test(correlationId)
        ? correlationId
        : createPublicFailureCorrelationId();
    return {
        status: false,
        error: messages[code],
        code,
        correlation_id: normalizedCorrelationId,
    };
}

export function createPublicFailureResponse(
    request: Pick<Request, "headers">,
    status: number,
    code: PublicFailureCode,
): Response {
    const correlationId = request.headers.get(INTERNAL_REQUEST_CORRELATION_HEADER) ?? "";
    const envelope = createPublicFailureEnvelope(code, correlationId);
    const body = JSON.stringify(envelope);
    return new Response(body, {
        status,
        headers: {
            "Content-Length": String(new TextEncoder().encode(body).byteLength),
            "Content-Type": "application/json; charset=utf-8",
            "X-Correlation-ID": envelope.correlation_id,
            "X-Error-Code": envelope.code,
        },
    });
}

export function parsePublicFailureEnvelope(text: string): PublicFailureEnvelope | null {
    if (new TextEncoder().encode(text).byteLength > MaximumEnvelopeBytes) return null;
    let value: unknown;
    try {
        value = JSON.parse(text);
    } catch {
        return null;
    }
    if (!isRecord(value)) return null;
    const keys = Object.keys(value).sort();
    if (keys.join(",") !== "code,correlation_id,error,status") return null;
    if (value.status !== false || typeof value.code !== "string") return null;
    if (!isPublicFailureCode(value.code)) return null;
    if (value.error !== messages[value.code]) return null;
    if (typeof value.correlation_id !== "string" || !correlationPattern.test(value.correlation_id)) return null;
    return value as PublicFailureEnvelope;
}

export function parseBoundedJsonObject(text: string): Record<string, unknown> | null {
    if (new TextEncoder().encode(text).byteLength > MaximumEnvelopeBytes) return null;
    try {
        const value: unknown = JSON.parse(text);
        return isRecord(value) ? value : null;
    } catch {
        return null;
    }
}

export function parsePublicFailureHeaders(headers: Pick<Headers, "get">): PublicFailureEnvelope | null {
    const code = headers.get("x-error-code");
    const correlationId = headers.get("x-correlation-id");
    if (typeof code !== "string" || !isPublicFailureCode(code)) return null;
    if (typeof correlationId !== "string" || !correlationPattern.test(correlationId)) return null;
    return {
        status: false,
        error: messages[code],
        code,
        correlation_id: correlationId,
    };
}

export function projectPublicFailureIdentityHeaders(headers: Pick<Headers, "get">): Headers {
    const identity = parsePublicFailureHeaders(headers);
    if (!identity) return new Headers();
    return new Headers({
        "X-Correlation-ID": identity.correlation_id,
        "X-Error-Code": identity.code,
    });
}

export function resolvePublicFailureEnvelope(
    body: string,
    headers: Pick<Headers, "get">,
): PublicFailureEnvelope | null {
    const bodyEnvelope = parsePublicFailureEnvelope(body);
    const headerEnvelope = parsePublicFailureHeaders(headers);
    if (bodyEnvelope && headerEnvelope) {
        if (bodyEnvelope.code !== headerEnvelope.code
            || bodyEnvelope.correlation_id !== headerEnvelope.correlation_id) return null;
        return bodyEnvelope;
    }
    return bodyEnvelope ?? headerEnvelope;
}

export async function readPublicFailureBody(response: Response): Promise<string | null> {
    const declaredLength = response.headers.get("content-length");
    if (declaredLength && /^\d+$/u.test(declaredLength) && Number(declaredLength) > MaximumEnvelopeBytes) {
        try {
            await response.body?.cancel();
        } catch {
            // The fixed status fallback remains authoritative if cancellation races closure.
        }
        return null;
    }
    if (!response.body) return "";

    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            total += value.byteLength;
            if (total > MaximumEnvelopeBytes) {
                await reader.cancel();
                return null;
            }
            chunks.push(value);
        }
    } catch {
        try {
            await reader.cancel();
        } catch {
            // The source already failed; the bounded fallback remains authoritative.
        }
        return null;
    }

    const bytes = new Uint8Array(total);
    let offset = 0;
    for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.byteLength;
    }
    try {
        return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    } catch {
        return null;
    }
}

export function renderPublicFailure(envelope: PublicFailureEnvelope): string {
    return `${envelope.error} (${envelope.correlation_id})`;
}

export function fallbackHttpFailure(status: number): string {
    return `HTTP ${status}`;
}

export function isPublicFailureCode(value: string): value is PublicFailureCode {
    return Object.prototype.hasOwnProperty.call(messages, value);
}

function createPublicFailureCorrelationId(): string {
    return crypto.randomUUID().replaceAll("-", "").toLowerCase();
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
