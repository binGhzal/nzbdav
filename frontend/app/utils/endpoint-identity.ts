export function areEndpointIdentitiesEquivalent(
    first: string | undefined,
    second: string | undefined,
): boolean {
    const firstTrimmed = (first ?? "").trim();
    const secondTrimmed = (second ?? "").trim();
    if (firstTrimmed === secondTrimmed) return true;

    const firstIdentity = createEndpointIdentity(firstTrimmed);
    return firstIdentity !== null && firstIdentity === createEndpointIdentity(secondTrimmed);
}

function createEndpointIdentity(value: string | undefined): string | null {
    const trimmed = (value ?? "").trim();
    const schemeDelimiter = trimmed.indexOf("://");
    if (schemeDelimiter <= 0) return null;

    let endpoint: URL;
    try {
        endpoint = new URL(trimmed);
    } catch {
        return null;
    }
    if (!endpoint.hostname) return null;

    const authorityStart = schemeDelimiter + 3;
    const suffixStart = findSuffixStart(trimmed, authorityStart);
    const authority = trimmed.slice(authorityStart, suffixStart);
    if (authority.includes("\\")) return null;
    const userInfoSeparator = authority.lastIndexOf("@");
    const userInfo = userInfoSeparator < 0 ? "" : authority.slice(0, userInfoSeparator);

    const fragmentStart = trimmed.indexOf("#", suffixStart);
    let queryStart = trimmed.indexOf("?", suffixStart);
    if (fragmentStart >= 0 && queryStart > fragmentStart) queryStart = -1;
    const pathEnd = minNonNegative(trimmed.length, queryStart, fragmentStart);
    const rawPath = trimmed.slice(suffixStart, pathEnd);
    const queryEnd = fragmentStart >= 0 ? fragmentStart : trimmed.length;
    const query = queryStart >= 0 ? trimmed.slice(queryStart, queryEnd) : "";
    const fragment = fragmentStart >= 0 ? trimmed.slice(fragmentStart) : "";

    return JSON.stringify([
        endpoint.protocol.toLowerCase(),
        endpoint.hostname.toLowerCase(),
        endpoint.port || defaultPort(endpoint.protocol),
        userInfo,
        rawPath === "/" ? "" : rawPath,
        query,
        fragment,
    ]);
}

function findSuffixStart(value: string, authorityStart: number): number {
    for (let index = authorityStart; index < value.length; index += 1) {
        if (value[index] === "/" || value[index] === "?" || value[index] === "#") return index;
    }
    return value.length;
}

function minNonNegative(fallback: number, ...values: number[]): number {
    return values.reduce(
        (result, value) => value >= 0 && value < result ? value : result,
        fallback,
    );
}

function defaultPort(protocol: string): string {
    if (protocol.toLowerCase() === "http:") return "80";
    if (protocol.toLowerCase() === "https:") return "443";
    return "";
}
