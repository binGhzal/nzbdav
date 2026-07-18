#!/usr/bin/env python3
"""Validate a deployed NZBDav ARR integration before enabling apply modes."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


DEFAULT_OUTPUT_DIR = Path("artifacts/arr-validation")
SECRET_KEY_PARTS = ("api_key", "apikey", "password", "token", "secret", "authorization")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("NZBDAV_BASE_URL"), help="NZBDav base URL")
    parser.add_argument("--api-key", default=os.environ.get("NZBDAV_API_KEY"), help="NZBDav API key")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    parser.add_argument("--min-correlation", type=int, default=90)
    parser.add_argument(
        "--low-correlation-reason",
        default=os.environ.get("NZBDAV_LOW_CORRELATION_REASON"),
        help="Required explanation when active queue correlation is below the threshold",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if not args.base_url:
        raise SystemExit("NZBDAV_BASE_URL or --base-url is required.")
    if not args.api_key:
        raise SystemExit("NZBDAV_API_KEY or --api-key is required.")

    documents = fetch_documents(args.base_url, args.api_key)
    checks = validate_documents(
        documents,
        min_correlation=args.min_correlation,
        low_correlation_reason=args.low_correlation_reason,
    )
    artifact = {
        "generated_at": utc_now(),
        "base_url": redact_url(args.base_url),
        "min_correlation": args.min_correlation,
        "low_correlation_reason": args.low_correlation_reason,
        "checks": checks,
        "documents": redact(documents),
    }
    args.output_dir.mkdir(parents=True, exist_ok=True)
    artifact_path = args.output_dir / f"arr-report-validation-{timestamp()}.json"
    artifact_path.write_text(json.dumps(artifact, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    failed = [check for check in checks if not check["passed"]]
    print(f"Wrote ARR validation artifact: {artifact_path}")
    if failed:
        for check in failed:
            print(f"FAIL: {check['name']} - {check['message']}", file=sys.stderr)
        return 1
    print("ARR report-mode validation passed.")
    return 0


def fetch_documents(base_url: str, api_key: str) -> dict[str, Any]:
    return {
        "validation": request_json(base_url, "/api/arr/validation", api_key),
        "search_nudges": request_json(base_url, "/api/arr/search-nudges?limit=500", api_key),
        "correlations": request_json(base_url, "/api/arr/correlations?limit=500", api_key),
        "fullstatus": request_json(base_url, "/api?mode=fullstatus", api_key),
    }


def request_json(base_url: str, path: str, api_key: str) -> Any:
    request = urllib.request.Request(join_url(base_url, path), headers={"X-Api-Key": api_key})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{path} returned HTTP {error.code}: {body}") from error


def validate_documents(
    documents: dict[str, Any],
    *,
    min_correlation: int,
    low_correlation_reason: str | None,
) -> list[dict[str, Any]]:
    validation = documents.get("validation", {})
    configured_apps = {
        str(app).strip().lower()
        for app in validation.get("configured_apps", [])
    }
    search_mode = str(validation.get("search_nudge_mode") or "report").strip().lower()
    duplicate_behavior = str(validation.get("duplicate_nzb_behavior") or "increment").strip().lower()
    queue_items = int(validation.get("queue_items") or 0)
    correlation = int(validation.get("correlation_coverage_percent") or 0)
    validation_errors = [
        issue for issue in validation.get("issues", [])
        if str(issue.get("severity", "")).lower() == "error"
    ]
    failed_nudges = int(nested(validation, "search_nudges", "failed") or 0)
    failed_command_rows = [
        command for command in documents.get("search_nudges", {}).get("commands", [])
        if command.get("status") == "failed"
    ]
    has_sonarr = "sonarr" in configured_apps
    has_radarr = "radarr" in configured_apps

    checks = [
        check("sonarr configured", has_sonarr, "At least one Sonarr instance must be configured."),
        check("radarr configured", has_radarr, "At least one Radarr instance must be configured."),
        check("search nudge report mode", search_mode == "report", f"Search nudge mode is {search_mode!r}."),
        check(
            "duplicate rejection disabled",
            duplicate_behavior != "reject",
            "Hard duplicate rejection must stay disabled during report-mode validation.",
        ),
        check(
            "validation has no errors",
            len(validation_errors) == 0,
            f"{len(validation_errors)} validation error(s) returned.",
            {"errors": validation_errors},
        ),
        check(
            "no failed search nudges",
            failed_nudges == 0 and len(failed_command_rows) == 0,
            f"{failed_nudges} failed summary nudges and {len(failed_command_rows)} failed command row(s).",
        ),
    ]
    coverage_ok = queue_items == 0 or correlation >= min_correlation or bool(low_correlation_reason)
    checks.append(check(
        "active queue correlation coverage",
        coverage_ok,
        f"Coverage is {correlation}% for {queue_items} active queue item(s).",
        {"coverage_percent": correlation, "queue_items": queue_items, "reason": low_correlation_reason},
    ))
    return checks


def check(name: str, passed: bool, message: str, details: dict[str, Any] | None = None) -> dict[str, Any]:
    return {"name": name, "passed": passed, "message": message, "details": details or {}}


def nested(document: Any, *keys: str) -> Any:
    current = document
    for key in keys:
        if not isinstance(current, dict):
            return None
        if key in current:
            current = current[key]
            continue
        lowered = key[:1].lower() + key[1:]
        current = current.get(lowered)
    return current


def join_url(base_url: str, path: str) -> str:
    return urllib.parse.urljoin(base_url.rstrip("/") + "/", path.lstrip("/"))


def redact(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            key: "***REDACTED***" if is_secret_key(key) else redact(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact(item) for item in value]
    if isinstance(value, str):
        stripped = value.strip()
        if stripped.startswith("{") or stripped.startswith("["):
            try:
                return redact(json.loads(stripped))
            except json.JSONDecodeError:
                pass
        return redact_url(value)
    return value


def is_secret_key(key: str) -> bool:
    normalized = key.replace("-", "_").lower()
    return any(part in normalized for part in SECRET_KEY_PARTS)


def redact_url(value: str) -> str:
    parsed = urllib.parse.urlparse(value)
    if not parsed.scheme:
        return value
    netloc = parsed.hostname or ""
    if parsed.port:
        netloc = f"{netloc}:{parsed.port}"
    return urllib.parse.urlunparse(parsed._replace(netloc=netloc, query="", fragment=""))


def timestamp() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


if __name__ == "__main__":
    raise SystemExit(main())
