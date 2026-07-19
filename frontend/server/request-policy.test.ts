import { describe, expect, it } from "vitest";
import {
  classifyBackendRequest,
  MAX_FRONTEND_ACTION_BODY_BYTES,
  validateFrontendActionBodyFraming,
} from "./request-policy";

const proxy = (
  lane: "ui-admin" | "ui-view" | "protocol-sab" | "protocol-arr" | "protocol-webdav",
  backendTarget: string,
  requiresFrontendPrincipal: boolean,
  injectInternalApiKey: boolean,
) => ({
  kind: "proxy" as const,
  lane,
  backendTarget,
  requiresFrontendPrincipal,
  injectInternalApiKey,
});

const reject = (status: 400 | 404 | 405, code: string, allow?: string[]) => ({
  kind: "reject" as const,
  status,
  code,
  ...(allow ? { allow } : {}),
});

describe("classifyBackendRequest", () => {
  describe("independently authenticated protocol API", () => {
    it.each(["GET", "POST"])("routes %s /protocol/api only to SAB", (method) => {
      expect(classifyBackendRequest(method, "/protocol/api?mode=queue&start=0"))
        .toEqual(proxy("protocol-sab", "/api?mode=queue&start=0", false, false));
    });

    it.each([
      ["GET", "/protocol/api/arr/validation", "/api/arr/validation"],
      ["GET", "/protocol/api/arr/search-nudges?limit=25", "/api/arr/search-nudges?limit=25"],
      ["GET", "/protocol/api/arr/correlations", "/api/arr/correlations"],
      ["POST", "/protocol/api/arr/events/sonarr", "/api/arr/events/sonarr"],
      ["POST", "/protocol/api/arr/events/radarr", "/api/arr/events/radarr"],
      ["POST", "/protocol/api/arr/events/lidarr", "/api/arr/events/lidarr"],
    ])("allows exact %s %s", (method, target, backendTarget) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(proxy("protocol-arr", backendTarget, false, false));
    });

    it.each([
      ["POST", "/protocol/api/arr/validation"],
      ["GET", "/protocol/api/arr/events/sonarr"],
    ])("returns 405 for a known protocol path with the wrong method", (method, target) => {
      expect(classifyBackendRequest(method, target)).toMatchObject({
        kind: "reject",
        status: 405,
        code: "method_not_allowed",
      });
    });

    it.each([
      "/protocol/api/arr/search-nudges/00000000-0000-0000-0000-000000000000/retry",
      "/protocol/api/arr/search-nudges/clear",
      "/protocol/api/arr/correlations/00000000-0000-0000-0000-000000000000",
      "/protocol/api/get-config",
      "/protocol/api/db.sqlite",
      "/protocol/api/unknown",
    ])("never exposes unapproved protocol API path %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(404, "route_not_found"));
    });

    it.each([
      ["GET", "/protocol/api/"],
      ["GET", "/protocol/api/arr/validation/"],
      ["POST", "/protocol/api/arr/events/sonarr/"],
    ])("rejects the non-exact trailing-slash API target %s %s", (method, target) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(reject(404, "route_not_found"));
    });
  });

  describe("path-scoped WebDAV", () => {
    it.each([
      ["PROPFIND", "/protocol/", "/"],
      ["OPTIONS", "/protocol", "/"],
      ["GET", "/protocol/README", "/README"],
      ["HEAD", "/protocol/.ids/a/b/c/file.mkv", "/.ids/a/b/c/file.mkv"],
      ["PROPFIND", "/protocol/content/Movies", "/content/Movies"],
      ["GET", "/protocol/content/My%20Movie/file.mkv", "/content/My%20Movie/file.mkv"],
      ["GET", "/protocol/content/%E6%98%A0%E7%94%BB/file.mkv", "/content/%E6%98%A0%E7%94%BB/file.mkv"],
      ["GET", "/protocol/content/encoded%C2%80name.mkv", "/content/encoded%C2%80name.mkv"],
    ])("preserves %s %s while stripping only /protocol", (method, target, backendTarget) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(proxy("protocol-webdav", backendTarget, false, false));
    });

    it.each([
      ["PUT", "/protocol/nzbs/movies/release.nzb", "/nzbs/movies/release.nzb"],
      ["DELETE", "/protocol/nzbs/movies/release.nzb", "/nzbs/movies/release.nzb"],
      ["DELETE", "/protocol/content/movies/Release/file.mkv", "/content/movies/Release/file.mkv"],
      ["DELETE", "/protocol/completed-symlinks/movies/Release/file.mkv.rclonelink", "/completed-symlinks/movies/Release/file.mkv.rclonelink"],
    ])("allows the approved semantic write %s %s", (method, target, backendTarget) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(proxy("protocol-webdav", backendTarget, false, false));
    });

    it.each([
      ["DELETE", "/protocol"],
      ["PUT", "/protocol/README"],
      ["DELETE", "/protocol/README"],
      ["PUT", "/protocol/content/movies/file.mkv"],
      ["PUT", "/protocol/nzbs/release.nzb"],
      ["PUT", "/protocol/nzbs/movies/nested/release.nzb"],
      ["DELETE", "/protocol/nzbs"],
      ["DELETE", "/protocol/nzbs/movies"],
      ["PUT", "/protocol/.ids/a/b/file.mkv"],
      ["PUT", "/protocol/completed-symlinks/movies/file.mkv.rclonelink"],
      ["DELETE", "/protocol/.ids/a/b/file.mkv"],
      ["DELETE", "/protocol/content"],
      ["DELETE", "/protocol/completed-symlinks"],
      ["COPY", "/protocol/content/movies/file.mkv"],
      ["MOVE", "/protocol/content/movies/file.mkv"],
      ["MKCOL", "/protocol/content/new"],
      ["PROPPATCH", "/protocol/content/movies"],
      ["LOCK", "/protocol/content/movies/file.mkv"],
      ["UNLOCK", "/protocol/content/movies/file.mkv"],
      ["POST", "/protocol/content/movies/file.mkv"],
      ["PATCH", "/protocol/content/movies/file.mkv"],
      ["TRACE", "/protocol/content/movies/file.mkv"],
    ])("rejects unsupported WebDAV mutation %s %s before proxying", (method, target) => {
      expect(classifyBackendRequest(method, target)).toMatchObject({
        kind: "reject",
        status: 405,
        code: "method_not_allowed",
      });
    });
  });

  describe("authenticated UI relays", () => {
    it.each([
      ["POST", "/api?mode=addfile&cat=movies&priority=0&pp=0"],
      ["POST", "/api?mode=pause"],
      ["POST", "/api?mode=resume"],
      ["POST", "/api?mode=queue&name=delete"],
      ["POST", "/api?mode=queue&name=priority&value=id&value2=1"],
      ["POST", "/api?mode=history&name=delete&del_completed_files=1"],
      ["GET", "/api/download-nzb?nzbBlobId=00000000-0000-0000-0000-000000000000"],
      ["GET", "/api/get-health-check-queue?pageSize=100"],
      ["POST", "/api/test-rclone-connection"],
      ["POST", "/api/test-usenet-connection"],
      ["POST", "/api/test-usenet-pipelining"],
      ["POST", "/api/test-arr-connection"],
      ["GET", "/api/maintenance/status"],
      ["POST", "/api/remove-unlinked-files"],
      ["POST", "/api/remove-unlinked-files/dry-run"],
      ["GET", "/api/remove-unlinked-files/audit"],
      ["GET", "/api/maintenance/status?kind=remove-unlinked-files"],
    ])("allows exact UI call %s %s only with frontend authority", (method, target) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(proxy("ui-admin", target, true, true));
    });

    it.each([
      ["GET", "/api?mode=pause"],
      ["GET", "/api?mode=queue&name=delete&value=id"],
      ["GET", "/api?mode=queue&name=priority&value=id&value2=1"],
      ["GET", "/api?mode=history&name=delete&value=id"],
    ])("requires POST for UI mutation %s %s", (method, target) => {
      expect(classifyBackendRequest(method, target)).toMatchObject({
        kind: "reject",
        status: 405,
        code: "method_not_allowed",
        allow: ["POST"],
      });
    });

    it.each([
      "/api/get-config",
      "/api/update-config",
      "/api/db.sqlite",
      "/api/repair/run",
      "/api/arr/search-nudges/00000000-0000-0000-0000-000000000000/retry",
      "/api/recreate-strm-files",
      "/api/convert-strm-to-symlinks",
      "/api/unknown",
      "/api/download-nzb?nzbBlobId=id&unexpected=true",
      "/api/get-health-check-queue?include-secrets=true",
      "/api/test-arr-connection?apikey=attacker",
      "/api/remove-unlinked-files?dryRun=true",
    ])("never exposes unlisted backend admin path %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(404, "route_not_found"));
    });

    it.each([
      ["GET", "/api"],
      ["POST", "/api"],
      ["GET", "/api?mode=version"],
      ["POST", "/api?mode=unknown"],
    ])("never treats direct UI SAB target %s %s as a protocol lane", (method, target) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(reject(404, "route_not_found"));
    });

    it.each(["GET", "HEAD"])("keeps %s /view behind the frontend principal", (method) => {
      const target = "/view/content/movies/file.mkv?downloadKey=capability";
      expect(classifyBackendRequest(method, target))
        .toEqual(proxy("ui-view", target, true, false));
    });

    it.each([
      "/api/download-nzb?nzbBlobId=one&nzbBlobId=two",
      "/api/maintenance/status?kind=one&kind=two",
      "/view/content/file.mkv?downloadKey=one&downloadKey=two",
    ])("rejects duplicate UI control query values in %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(400, "invalid_request_target"));
    });

    it.each([
      "/view?downloadKey=capability",
      "/view/?downloadKey=capability",
      "/view/content/file.mkv",
      "/view/content/file.mkv?extension=mkv",
      "/view/content/file.mkv?downloadKey=key&unexpected=true",
    ])("rejects missing or unapproved UI view capabilities in %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(404, "route_not_found"));
    });

    it("does not expose /view through /protocol", () => {
      expect(classifyBackendRequest("GET", "/protocol/view/.ids/id.mkv?downloadKey=capability"))
        .toEqual(reject(404, "route_not_found"));
    });
  });

  describe("normalization and default denial", () => {
    it.each([
      "/protocol/api%2Fget-config",
      "/protocol/api%252Fget-config",
      "/protocol/content%5Csecret",
      "/protocol/content/%2e%2e/secret",
      "/protocol/content/%252e%252e/secret",
      "/protocol/content/../secret",
      "/protocol//api",
      "/protocol/api/%",
      "/protocol/api/%00",
      "/protocol/api/line%0Abreak",
      "/protocol/content/encoded%7Fname.mkv",
      "/protocol/content/%25AB",
      "/%70rotocol/api",
    ])("returns stable 400 for ambiguous target %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(400, "invalid_request_target"));
    });

    it.each([
      "/protocol/apiary",
      "/protocol/contention/file",
      "/protocol/nzbs-old/file",
      "/protocol/.ids-backup/file",
      "/protocol/completed-symlinks.evil/file",
      "/protocol/API",
    ])("returns 404 without prefix confusion for %s", (target) => {
      expect(classifyBackendRequest("GET", target))
        .toEqual(reject(404, "route_not_found"));
    });

    it.each([
      ["GET", "/protocol/private/file"],
      ["PROPFIND", "/protocol/private"],
    ])("returns 404 for unknown WebDAV root %s %s", (method, target) => {
      expect(classifyBackendRequest(method, target))
        .toEqual(reject(404, "route_not_found"));
    });

    it.each([
      "/apiary",
      "/contention/file",
      "/nzbs-old/file",
      "/viewing/file",
      "/ordinary-ui-route",
    ])("never proxies ordinary frontend path %s", (target) => {
      expect(classifyBackendRequest("GET", target)).toEqual({ kind: "frontend" });
    });

    it.each([
      ["PROPFIND", "/.ids/a"],
      ["PUT", "/nzbs/movies/file.nzb"],
      ["DELETE", "/nzbs/movies/file.nzb"],
      ["DELETE", "/content/movie.mkv"],
      ["DELETE", "/completed-symlinks/a"],
    ])("never treats legacy root WebDAV surface %s %s as protocol ingress", (method, target) => {
      expect(classifyBackendRequest(method, target)).toEqual({ kind: "frontend" });
    });

    it.each(["TRACE", "CONNECT", "PATCH"])("rejects unsupported reserved-route method %s", (method) => {
      expect(classifyBackendRequest(method, "/protocol/api"))
        .toMatchObject({ kind: "reject", status: 405, code: "method_not_allowed" });
    });

    it("enforces the raw request-target length boundary", () => {
      const prefix = "/protocol/content/";
      const maximum = prefix + "a".repeat(8192 - prefix.length);
      const oversized = `${maximum}a`;

      expect(classifyBackendRequest("GET", maximum))
        .toEqual(proxy("protocol-webdav", maximum.slice("/protocol".length), false, false));
      expect(classifyBackendRequest("GET", oversized))
        .toEqual(reject(400, "invalid_request_target"));
    });
  });
});

describe("validateFrontendActionBodyFraming", () => {
  it.each(["GET", "HEAD"])("does not impose action framing on %s", (method) => {
    expect(validateFrontendActionBodyFraming(method, "/settings/update", [], ["chunked"]))
      .toBe(true);
  });

  it.each([
    ["POST", "/protocol/api", [], ["chunked"]],
    ["POST", "/api/test-arr-connection", [], ["chunked"]],
    ["PUT", "/protocol/nzbs/movies/release.nzb", [], ["chunked"]],
    ["DELETE", "/protocol/content/movies/release.mkv", [], []],
  ])("does not alter backend or protocol framing for %s %s", (method, target, lengths, encodings) => {
    expect(validateFrontendActionBodyFraming(method, target, lengths, encodings)).toBe(true);
  });

  it.each([
    ["POST", "/login", "0"],
    ["POST", "/onboarding", "128"],
    ["POST", "/settings/update", String(MAX_FRONTEND_ACTION_BODY_BYTES)],
    ["DELETE", "/ordinary-ui-resource", "1"],
  ])("accepts one bounded strict Content-Length for %s %s", (method, target, length) => {
    expect(validateFrontendActionBodyFraming(method, target, [length], [])).toBe(true);
  });

  it.each([
    [[], []],
    [["1", "1"], []],
    [["1"], ["chunked"]],
    [["1"], ["identity"]],
    [[""], []],
    [["01"], []],
    [["+1"], []],
    [[" 1"], []],
    [["1 "], []],
    [["1.0"], []],
    [["9007199254740992"], []],
    [[String(MAX_FRONTEND_ACTION_BODY_BYTES + 1)], []],
  ])("rejects ambiguous or unbounded frontend action framing %#", (lengths, encodings) => {
    expect(validateFrontendActionBodyFraming("POST", "/settings/update", lengths, encodings))
      .toBe(false);
  });
});
