const MaxErrorBodyLength = 500;

export async function getHttpErrorMessage(response: Response): Promise<string> {
    const text = (await readTextSafely(response)).trim();
    if (text.length === 0)
        return formatStatus(response);

    const parsed = parseJsonObject(text);
    const structuredMessage = getStructuredErrorMessage(parsed);
    if (structuredMessage)
        return structuredMessage;

    return text.length > MaxErrorBodyLength
        ? `${text.slice(0, MaxErrorBodyLength)}...`
        : text;
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

function formatStatus(response: Response): string {
    return response.statusText
        ? `${response.status} ${response.statusText}`
        : `${response.status}`;
}

async function readTextSafely(response: Response): Promise<string> {
    try {
        return await response.text();
    } catch {
        return "";
    }
}

function parseJsonObject(text: string): Record<string, unknown> | null {
    try {
        const data = JSON.parse(text);
        return isRecord(data) ? data : null;
    } catch {
        return null;
    }
}

function getStructuredErrorMessage(data: Record<string, unknown> | null): string | null {
    if (!data) return null;
    for (const key of ["error", "message", "detail"]) {
        const value = data[key];
        if (typeof value === "string" && value.trim())
            return value.trim();
    }

    return null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
