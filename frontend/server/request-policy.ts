export type BackendProxyLane =
  | "ui-admin"
  | "ui-view"
  | "protocol-sab"
  | "protocol-arr"
  | "protocol-webdav";

export type BackendRequestDecision =
  | {
      kind: "proxy";
      lane: BackendProxyLane;
      backendTarget: string;
      requiresFrontendPrincipal: boolean;
      injectInternalApiKey: boolean;
    }
  | { kind: "frontend" }
  | {
      kind: "reject";
      status: 400 | 404 | 405;
      code: "invalid_request_target" | "route_not_found" | "method_not_allowed";
      allow?: string[];
    };

export const MAX_FRONTEND_ACTION_BODY_BYTES = 16 * 1024;

export function validateFrontendActionBodyFraming(
  method: string,
  rawTarget: string,
  contentLengthValues: readonly string[],
  transferEncodingValues: readonly string[],
): boolean {
  const normalizedMethod = method.toUpperCase();
  if (normalizedMethod === "GET" || normalizedMethod === "HEAD") return true;
  if (classifyBackendRequest(normalizedMethod, rawTarget).kind !== "frontend") return true;
  if (transferEncodingValues.length !== 0 || contentLengthValues.length !== 1) return false;

  const value = contentLengthValues[0];
  if (!/^(?:0|[1-9][0-9]*)$/u.test(value)) return false;
  const length = Number(value);
  return Number.isSafeInteger(length) && length <= MAX_FRONTEND_ACTION_BODY_BYTES;
}

const READ_WEBDAV_METHODS = ["GET", "HEAD", "OPTIONS", "PROPFIND"] as const;
const UI_ROUTES = new Map<string, readonly string[]>([
  ["/api/download-nzb", ["GET"]],
  ["/api/get-health-check-queue", ["GET"]],
  ["/api/test-rclone-connection", ["POST"]],
  ["/api/test-usenet-connection", ["POST"]],
  ["/api/test-usenet-pipelining", ["POST"]],
  ["/api/test-arr-connection", ["POST"]],
  ["/api/maintenance/status", ["GET"]],
  ["/api/remove-unlinked-files", ["POST"]],
  ["/api/remove-unlinked-files/dry-run", ["POST"]],
  ["/api/remove-unlinked-files/audit", ["GET"]],
]);
const UI_ROUTE_QUERY_RULES = new Map<string, { required: readonly string[]; optional: readonly string[] }>([
  ["/api/download-nzb", { required: ["nzbBlobId"], optional: [] }],
  ["/api/get-health-check-queue", { required: [], optional: ["pageSize"] }],
  ["/api/maintenance/status", { required: [], optional: ["kind"] }],
]);
const RESERVED_FIRST_SEGMENTS = new Set([
  "protocol",
  "api",
  "view",
  ".ids",
  "nzbs",
  "content",
  "completed-symlinks",
]);
const PROTOCOL_ROOT_SEGMENTS = new Set([
  "api",
  "view",
  ".ids",
  "nzbs",
  "content",
  "completed-symlinks",
  "README",
]);

export function classifyBackendRequest(method: string, rawTarget: string): BackendRequestDecision {
  const normalizedMethod = method.toUpperCase();
  const parsed = parseRawTarget(rawTarget);
  if (!parsed) return reject(400, "invalid_request_target");

  const { rawPath, rawQuery, segments } = parsed;
  if (segments[0] === "protocol") {
    return classifyProtocolRequest(normalizedMethod, rawPath, rawQuery, segments.slice(1));
  }

  if (segments[0] === "api") {
    return classifyUiApiRequest(normalizedMethod, rawTarget, rawPath, rawQuery);
  }

  if (segments[0] === "view") {
    if (segments.length < 2) return reject(404, "route_not_found");
    const query = parseUniqueQuery(rawQuery);
    if (!query) return reject(400, "invalid_request_target");
    if (!hasRequiredAndOnlyQueryKeys(query, ["downloadKey"], ["extension", "download"])) {
      return reject(404, "route_not_found");
    }
    if (normalizedMethod !== "GET" && normalizedMethod !== "HEAD") {
      return reject(405, "method_not_allowed", ["GET", "HEAD"]);
    }
    return proxy("ui-view", rawTarget, true, false);
  }

  return { kind: "frontend" };
}

function classifyProtocolRequest(
  method: string,
  rawPath: string,
  rawQuery: string,
  segments: string[],
): BackendRequestDecision {
  const backendTarget = stripProtocolPrefix(rawPath) + rawQuery;

  if (segments[0] === "api" && rawPath.endsWith("/")) {
    return reject(404, "route_not_found");
  }

  if (segments.length === 1 && segments[0] === "api") {
    if (method === "GET" || method === "POST") {
      return proxy("protocol-sab", backendTarget, false, false);
    }
    return reject(405, "method_not_allowed", ["GET", "POST"]);
  }

  if (segments[0] === "api" && segments[1] === "arr") {
    return classifyProtocolArrRequest(method, backendTarget, segments.slice(2));
  }

  if (segments[0] === "view") return reject(404, "route_not_found");

  return classifyProtocolWebdavRequest(method, backendTarget, segments);
}

function classifyProtocolArrRequest(
  method: string,
  backendTarget: string,
  segments: string[],
): BackendRequestDecision {
  const readOnlyRoute =
    segments.length === 1 &&
    (segments[0] === "validation" || segments[0] === "search-nudges" || segments[0] === "correlations");
  if (readOnlyRoute) {
    if (method === "GET") return proxy("protocol-arr", backendTarget, false, false);
    return reject(405, "method_not_allowed", ["GET"]);
  }

  const eventRoute =
    segments.length === 2 &&
    segments[0] === "events" &&
    (segments[1] === "sonarr" || segments[1] === "radarr" || segments[1] === "lidarr");
  if (eventRoute) {
    if (method === "POST") return proxy("protocol-arr", backendTarget, false, false);
    return reject(405, "method_not_allowed", ["POST"]);
  }

  return reject(404, "route_not_found");
}

function classifyProtocolWebdavRequest(
  method: string,
  backendTarget: string,
  segments: string[],
): BackendRequestDecision {
  if (segments.length === 0) {
    return allowWebdavRead(method, backendTarget);
  }

  if (segments.length === 1 && segments[0] === "README") {
    return allowWebdavRead(method, backendTarget);
  }

  const root = segments[0];
  if (root !== ".ids" && root !== "nzbs" && root !== "content" && root !== "completed-symlinks") {
    return reject(404, "route_not_found");
  }

  if ((READ_WEBDAV_METHODS as readonly string[]).includes(method)) {
    return proxy("protocol-webdav", backendTarget, false, false);
  }

  if (method === "PUT" && root === "nzbs" && segments.length === 3) {
    return proxy("protocol-webdav", backendTarget, false, false);
  }

  const semanticDelete =
    method === "DELETE" &&
    ((root === "nzbs" && segments.length === 3) ||
      (root === "content" && segments.length >= 2) ||
      (root === "completed-symlinks" && segments.length >= 2));
  if (semanticDelete) {
    return proxy("protocol-webdav", backendTarget, false, false);
  }

  const allow: string[] = [...READ_WEBDAV_METHODS];
  if (root === "nzbs" && segments.length === 3) allow.push("PUT", "DELETE");
  if ((root === "content" || root === "completed-symlinks") && segments.length >= 2) allow.push("DELETE");
  return reject(405, "method_not_allowed", allow);
}

function allowWebdavRead(method: string, backendTarget: string): BackendRequestDecision {
  if ((READ_WEBDAV_METHODS as readonly string[]).includes(method)) {
    return proxy("protocol-webdav", backendTarget, false, false);
  }
  return reject(405, "method_not_allowed", [...READ_WEBDAV_METHODS]);
}

function classifyUiApiRequest(
  method: string,
  rawTarget: string,
  rawPath: string,
  rawQuery: string,
): BackendRequestDecision {
  if (rawPath === "/api") {
    const query = parseUniqueQuery(rawQuery);
    if (!query) return reject(400, "invalid_request_target");

    const mode = query.get("mode");
    const name = query.get("name");
    const allowed = allowedUiSabQuery(mode, name);
    if (!allowed || !hasOnlyQueryKeys(query, allowed)) return reject(404, "route_not_found");
    if (method !== "POST") return reject(405, "method_not_allowed", ["POST"]);
    return proxy("ui-admin", rawTarget, true, true);
  }

  const methods = UI_ROUTES.get(rawPath);
  if (!methods) return reject(404, "route_not_found");
  const query = parseUniqueQuery(rawQuery);
  if (!query) return reject(400, "invalid_request_target");
  const queryRule = UI_ROUTE_QUERY_RULES.get(rawPath) ?? { required: [], optional: [] };
  if (!hasRequiredAndOnlyQueryKeys(query, queryRule.required, queryRule.optional)) {
    return reject(404, "route_not_found");
  }
  if (!methods.includes(method)) return reject(405, "method_not_allowed", [...methods]);
  return proxy("ui-admin", rawTarget, true, true);
}

function allowedUiSabQuery(mode: string | undefined, name: string | undefined): readonly string[] | null {
  if (mode === "addfile" && name === undefined) return ["mode", "cat", "priority", "pp"];
  if ((mode === "pause" || mode === "resume") && name === undefined) return ["mode"];
  if (mode === "queue" && name === "delete") return ["mode", "name", "value"];
  if (mode === "queue" && name === "priority") return ["mode", "name", "value", "value2"];
  if (mode === "history" && name === "delete") {
    return ["mode", "name", "value", "del_completed_files"];
  }
  return null;
}

function hasOnlyQueryKeys(query: Map<string, string>, allowed: readonly string[]): boolean {
  const allowedSet = new Set(allowed);
  return [...query.keys()].every((key) => allowedSet.has(key));
}

function hasRequiredAndOnlyQueryKeys(
  query: Map<string, string>,
  required: readonly string[],
  optional: readonly string[],
): boolean {
  if (required.some((key) => !query.get(key))) return false;
  return hasOnlyQueryKeys(query, [...required, ...optional]);
}

function parseUniqueQuery(rawQuery: string): Map<string, string> | null {
  const result = new Map<string, string>();
  const queryText = rawQuery.startsWith("?") ? rawQuery.slice(1) : rawQuery;
  if (!queryText) return result;

  const query = new URLSearchParams(queryText);
  for (const [key, value] of query) {
    if (result.has(key)) return null;
    result.set(key, value);
  }
  return result;
}

function parseRawTarget(rawTarget: string): { rawPath: string; rawQuery: string; segments: string[] } | null {
  if (
    rawTarget.length === 0 ||
    rawTarget.length > 8192 ||
    !rawTarget.startsWith("/") ||
    rawTarget.includes("#") ||
    rawTarget.includes("\\") ||
    hasControlCharacter(rawTarget)
  ) {
    return null;
  }

  const queryOffset = rawTarget.indexOf("?");
  const rawPath = queryOffset === -1 ? rawTarget : rawTarget.slice(0, queryOffset);
  const rawQuery = queryOffset === -1 ? "" : rawTarget.slice(queryOffset);
  if (!hasValidPercentEncoding(rawPath) || !hasValidPercentEncoding(rawQuery)) return null;
  if (rawPath !== "/" && rawPath.includes("//")) return null;

  const pathWithoutEdges = rawPath.replace(/^\//, "").replace(/\/$/, "");
  const rawSegments = pathWithoutEdges ? pathWithoutEdges.split("/") : [];
  const decodedSegments: string[] = [];
  for (const rawSegment of rawSegments) {
    const decoded = decodeAndValidateSegment(rawSegment);
    if (decoded === null) return null;
    decodedSegments.push(decoded);
  }

  if (rawSegments.length > 0 && rawSegments[0] !== decodedSegments[0] &&
      RESERVED_FIRST_SEGMENTS.has(decodedSegments[0].toLowerCase())) {
    return null;
  }
  if (rawSegments[0] === "protocol" && rawSegments.length > 1 &&
      rawSegments[1] !== decodedSegments[1] && PROTOCOL_ROOT_SEGMENTS.has(decodedSegments[1])) {
    return null;
  }

  return { rawPath, rawQuery, segments: rawSegments };
}

function decodeAndValidateSegment(rawSegment: string): string | null {
  let candidate = rawSegment;
  for (let depth = 0; depth < 5; depth += 1) {
    let decoded: string;
    try {
      decoded = decodeURIComponent(candidate);
    } catch {
      return null;
    }

    if (
      decoded === "." ||
      decoded === ".." ||
      decoded.includes("/") ||
      decoded.includes("\\") ||
      hasControlCharacter(decoded)
    ) {
      return null;
    }

    if (decoded === candidate || !/%[0-9a-fA-F]{2}/.test(decoded)) return decoded;
    candidate = decoded;
  }
  return null;
}

function hasValidPercentEncoding(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    if (value[index] !== "%") continue;
    if (index + 2 >= value.length || !isHex(value[index + 1]) || !isHex(value[index + 2])) return false;
    index += 2;
  }
  return true;
}

function isHex(value: string): boolean {
  return /^[0-9a-fA-F]$/.test(value);
}

function hasControlCharacter(value: string): boolean {
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code <= 0x1f || code === 0x7f) return true;
  }
  return false;
}

function stripProtocolPrefix(rawPath: string): string {
  const stripped = rawPath.slice("/protocol".length);
  return stripped === "" ? "/" : stripped;
}

function proxy(
  lane: BackendProxyLane,
  backendTarget: string,
  requiresFrontendPrincipal: boolean,
  injectInternalApiKey: boolean,
): BackendRequestDecision {
  return { kind: "proxy", lane, backendTarget, requiresFrontendPrincipal, injectInternalApiKey };
}

function reject(
  status: 400 | 404 | 405,
  code: "invalid_request_target" | "route_not_found" | "method_not_allowed",
  allow?: string[],
): BackendRequestDecision {
  return { kind: "reject", status, code, ...(allow ? { allow } : {}) };
}
