#!/usr/bin/env python3
"""Benchmark NZBDav WebDAV/rclone scenarios and evaluate a future DFS gate."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import math
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any


DEFAULT_OUTPUT_DIR = Path("artifacts/benchmarks")
DEFAULT_RUNS = 5
DEFAULT_SEEK_COUNT = 5
DEFAULT_SEQUENTIAL_BYTES = 64 * 1024 * 1024
DEFAULT_TIMEOUT_SECONDS = 30.0


@dataclass(frozen=True)
class HttpResult:
    status: int | None
    elapsed_ms: float | None
    first_byte_ms: float | None
    bytes_read: int
    headers: dict[str, str]
    error: str | None = None


def percentile(values: list[float], percentile_value: float) -> float | None:
    if not values:
        return None

    if percentile_value < 0 or percentile_value > 100:
        raise ValueError("percentile must be between 0 and 100")

    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]

    rank = (percentile_value / 100) * (len(ordered) - 1)
    lower = math.floor(rank)
    upper = math.ceil(rank)
    if lower == upper:
        return ordered[int(rank)]

    weight = rank - lower
    return ordered[lower] * (1 - weight) + ordered[upper] * weight


def summarize_latency(samples: list[float]) -> dict[str, Any]:
    return {
        "samples": [round(sample, 3) for sample in samples],
        "count": len(samples),
        "p50": round_value(percentile(samples, 50)),
        "p95": round_value(percentile(samples, 95)),
        "min": round_value(min(samples) if samples else None),
        "max": round_value(max(samples) if samples else None),
    }


def summarize_throughput(samples: list[float]) -> dict[str, Any]:
    return {
        "samples": [round(sample, 3) for sample in samples],
        "count": len(samples),
        "p50": round_value(percentile(samples, 50)),
        "p95": round_value(percentile(samples, 95)),
        "min": round_value(min(samples) if samples else None),
        "max": round_value(max(samples) if samples else None),
    }


def round_value(value: float | None) -> float | None:
    if value is None:
        return None
    return round(value, 3)


def join_url(base_url: str, path: str) -> str:
    if urllib.parse.urlparse(path).scheme:
        return path
    return urllib.parse.urljoin(base_url.rstrip("/") + "/", path.lstrip("/"))


def redact_path(value: str) -> str:
    parsed = urllib.parse.urlparse(value)
    if parsed.scheme:
        return redact_url(value)
    return urllib.parse.urlunparse(parsed._replace(query="", fragment=""))


def auth_headers(user: str | None, password: str | None) -> dict[str, str]:
    if not user:
        return {}
    token = base64.b64encode(f"{user}:{password or ''}".encode("utf-8")).decode("ascii")
    return {"Authorization": f"Basic {token}"}


def request_url(
    url: str,
    *,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
    max_bytes: int | None = None,
    read_body: bool = True,
) -> HttpResult:
    request = urllib.request.Request(url, method=method, headers=headers or {})
    started = time.perf_counter()
    bytes_read = 0
    first_byte_ms: float | None = None
    response_headers: dict[str, str] = {}

    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            response_headers = {key.lower(): value for key, value in response.headers.items()}
            if read_body:
                while True:
                    chunk_size = 64 * 1024
                    if max_bytes is not None:
                        remaining = max_bytes - bytes_read
                        if remaining <= 0:
                            break
                        chunk_size = min(chunk_size, remaining)

                    chunk = response.read(chunk_size)
                    if first_byte_ms is None:
                        first_byte_ms = (time.perf_counter() - started) * 1000
                    if not chunk:
                        break
                    bytes_read += len(chunk)
            elapsed_ms = (time.perf_counter() - started) * 1000
            return HttpResult(
                status=response.status,
                elapsed_ms=elapsed_ms,
                first_byte_ms=first_byte_ms,
                bytes_read=bytes_read,
                headers=response_headers,
            )
    except urllib.error.HTTPError as error:
        elapsed_ms = (time.perf_counter() - started) * 1000
        return HttpResult(
            status=error.code,
            elapsed_ms=elapsed_ms,
            first_byte_ms=None,
            bytes_read=0,
            headers={key.lower(): value for key, value in error.headers.items()},
            error=str(error),
        )
    except Exception as error:  # best-effort benchmark harness
        elapsed_ms = (time.perf_counter() - started) * 1000
        return HttpResult(
            status=None,
            elapsed_ms=elapsed_ms,
            first_byte_ms=None,
            bytes_read=0,
            headers=response_headers,
            error=str(error),
        )


def json_request(
    url: str,
    *,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
) -> tuple[dict[str, Any] | None, HttpResult]:
    request = urllib.request.Request(url, method=method, headers=headers or {})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            body = response.read(2 * 1024 * 1024)
            elapsed_ms = (time.perf_counter() - started) * 1000
            result = HttpResult(
                status=response.status,
                elapsed_ms=elapsed_ms,
                first_byte_ms=elapsed_ms if body else None,
                bytes_read=len(body),
                headers={key.lower(): value for key, value in response.headers.items()},
            )
            return json.loads(body.decode("utf-8")), result
    except urllib.error.HTTPError as error:
        elapsed_ms = (time.perf_counter() - started) * 1000
        return None, HttpResult(
            status=error.code,
            elapsed_ms=elapsed_ms,
            first_byte_ms=None,
            bytes_read=0,
            headers={key.lower(): value for key, value in error.headers.items()},
            error=str(error),
        )
    except Exception as error:  # best-effort benchmark harness
        elapsed_ms = (time.perf_counter() - started) * 1000
        return None, HttpResult(
            status=None,
            elapsed_ms=elapsed_ms,
            first_byte_ms=None,
            bytes_read=0,
            headers={},
            error=str(error),
        )


def add_check(checks: list[dict[str, Any]], name: str, passed: bool, details: dict[str, Any] | None = None) -> None:
    checks.append({"name": name, "passed": passed, "details": details or {}})


def get_content_length(base_url: str, path: str, headers: dict[str, str], timeout_seconds: float) -> int | None:
    result = request_url(
        join_url(base_url, path),
        method="HEAD",
        headers=headers,
        timeout_seconds=timeout_seconds,
        read_body=False,
    )
    value = result.headers.get("content-length")
    if not value:
        return None
    try:
        return int(value)
    except ValueError:
        return None


def build_seek_offsets(content_length: int | None, seek_count: int, configured_offsets: list[int]) -> list[int]:
    if configured_offsets:
        return configured_offsets[:seek_count]
    if content_length and content_length > seek_count + 1:
        return [
            max(0, min(content_length - 1, int(content_length * fraction / (seek_count + 1))))
            for fraction in range(1, seek_count + 1)
        ]
    return [1024 * 1024 * index for index in range(1, seek_count + 1)]


def content_range_starts_at(headers: dict[str, str], offset: int) -> bool:
    content_range = headers.get("content-range")
    if not content_range:
        return False
    match = re.match(r"^bytes\s+(\d+)-\d+/(\d+|\*)$", content_range.strip(), re.IGNORECASE)
    return bool(match and int(match.group(1)) == offset)


def is_valid_seek_response(result: HttpResult, offset: int) -> bool:
    return result.status == 206 and result.bytes_read > 0 and content_range_starts_at(result.headers, offset)


def measure_first_byte(
    base_url: str,
    path: str,
    headers: dict[str, str],
    timeout_seconds: float,
) -> HttpResult:
    request_headers = dict(headers)
    request_headers["Range"] = "bytes=0-0"
    return request_url(
        join_url(base_url, path),
        headers=request_headers,
        timeout_seconds=timeout_seconds,
        max_bytes=1,
    )


def measure_seek(
    base_url: str,
    path: str,
    offset: int,
    headers: dict[str, str],
    timeout_seconds: float,
) -> HttpResult:
    request_headers = dict(headers)
    request_headers["Range"] = f"bytes={offset}-{offset}"
    return request_url(
        join_url(base_url, path),
        headers=request_headers,
        timeout_seconds=timeout_seconds,
        max_bytes=1,
    )


def measure_sequential(
    base_url: str,
    path: str,
    byte_count: int,
    headers: dict[str, str],
    timeout_seconds: float,
) -> tuple[HttpResult, float | None]:
    request_headers = dict(headers)
    request_headers["Range"] = f"bytes=0-{byte_count - 1}"
    result = request_url(
        join_url(base_url, path),
        headers=request_headers,
        timeout_seconds=timeout_seconds,
        max_bytes=byte_count,
    )
    if not result.elapsed_ms or result.elapsed_ms <= 0 or result.bytes_read <= 0:
        return result, None
    mib = result.bytes_read / (1024 * 1024)
    seconds = result.elapsed_ms / 1000
    return result, mib / seconds


def nzbdav_status_snapshot(base_url: str, api_key: str | None, timeout_seconds: float) -> dict[str, Any] | None:
    query = {"mode": "fullstatus"}
    if api_key:
        query["apikey"] = api_key
    url = join_url(base_url, "api") + "?" + urllib.parse.urlencode(query)
    payload, result = json_request(url, timeout_seconds=timeout_seconds)
    if payload is None:
        return {"available": False, "status": result.status, "error": result.error}
    status = payload.get("status", {})
    return {
        "available": True,
        "managed_memory_bytes": status.get("managed_memory_bytes"),
        "working_set_bytes": status.get("working_set_bytes"),
        "process_cpu_cores": status.get("process_cpu_cores"),
        "active_streams": status.get("active_streams"),
        "total_streams_opened": status.get("total_streams_opened"),
        "rclone_invalidations": status.get("rclone_invalidations"),
        "provider_diagnostics": status.get("provider_diagnostics"),
        "worker_queues": status.get("worker_queues"),
        "version": status.get("version"),
    }


def rclone_rc_snapshot(
    rc_url: str | None,
    headers: dict[str, str],
    timeout_seconds: float,
) -> dict[str, Any] | None:
    if not rc_url:
        return None

    snapshot: dict[str, Any] = {"available": False}
    for endpoint in ("core/version", "vfs/stats"):
        url = join_url(rc_url, endpoint)
        payload, result = json_request(url, method="POST", headers=headers, timeout_seconds=timeout_seconds)
        snapshot[endpoint.replace("/", "_")] = payload or {"status": result.status, "error": result.error}
        if payload is not None:
            snapshot["available"] = True
    return snapshot


def process_snapshot(pid: int | None, name: str) -> dict[str, Any] | None:
    if not pid:
        return None
    try:
        output = subprocess.check_output(
            ["ps", "-o", "%cpu=", "-o", "rss=", "-p", str(pid)],
            text=True,
            stderr=subprocess.DEVNULL,
        ).strip()
    except Exception as error:
        return {"name": name, "pid": pid, "available": False, "error": str(error)}

    if not output:
        return {"name": name, "pid": pid, "available": False, "error": "process not found"}

    parts = output.split()
    if len(parts) < 2:
        return {"name": name, "pid": pid, "available": False, "error": f"unexpected ps output: {output}"}

    return {
        "name": name,
        "pid": pid,
        "available": True,
        "cpu_percent": float(parts[0]),
        "rss_bytes": int(parts[1]) * 1024,
    }


def summarize_resources(snapshots: list[dict[str, Any]]) -> dict[str, Any]:
    summary: dict[str, Any] = {"sources": {}, "total": {}}
    total_cpu_samples: list[float] = []
    total_rss_samples: list[float] = []

    source_values: dict[str, dict[str, list[float]]] = {}
    for snapshot in snapshots:
        for source, values in snapshot.items():
            if not isinstance(values, dict) or not values.get("available"):
                continue

            source_values.setdefault(source, {"cpu": [], "rss": []})
            cpu = values.get("process_cpu_cores")
            if cpu is None and values.get("cpu_percent") is not None:
                cpu = float(values["cpu_percent"]) / 100
            rss = values.get("working_set_bytes") or values.get("rss_bytes")

            if isinstance(cpu, (int, float)):
                source_values[source]["cpu"].append(float(cpu))
            if isinstance(rss, (int, float)):
                source_values[source]["rss"].append(float(rss))

    for source, values in source_values.items():
        source_summary: dict[str, Any] = {}
        if values["cpu"]:
            source_summary["cpu_cores_max"] = round_value(max(values["cpu"]))
            source_summary["cpu_cores_p95"] = round_value(percentile(values["cpu"], 95))
            total_cpu_samples.append(max(values["cpu"]))
        if values["rss"]:
            source_summary["rss_bytes_max"] = int(max(values["rss"]))
            source_summary["rss_bytes_p95"] = int(percentile(values["rss"], 95) or 0)
            total_rss_samples.append(max(values["rss"]))
        summary["sources"][source] = source_summary

    if total_cpu_samples:
        summary["total"]["cpu_cores_max"] = round_value(sum(total_cpu_samples))
    if total_rss_samples:
        summary["total"]["rss_bytes_max"] = int(sum(total_rss_samples))

    return summary


def checks_passed(checks: list[dict[str, Any]]) -> bool:
    return bool(checks) and all(check.get("passed") is True for check in checks)


def run_benchmark(args: argparse.Namespace) -> dict[str, Any]:
    headers = auth_headers(args.webdav_user, args.webdav_pass)
    rc_headers = auth_headers(args.rclone_rc_user, args.rclone_rc_pass)
    paths = args.paths
    checks: list[dict[str, Any]] = []
    first_byte_samples: list[float] = []
    seek_samples: list[float] = []
    throughput_samples: list[float] = []
    operation_samples: list[dict[str, Any]] = []
    resource_snapshots: list[dict[str, Any]] = []

    if not paths:
        raise SystemExit("At least one benchmark path is required")

    for path in paths:
        head_result = request_url(
            join_url(args.base_url, path),
            method="HEAD",
            headers=headers,
            timeout_seconds=args.timeout_seconds,
            read_body=False,
        )
        add_check(
            checks,
            f"HEAD {redact_path(path)}",
            head_result.status is not None and 200 <= head_result.status < 400,
            {"status": head_result.status, "error": head_result.error},
        )

    status_before = nzbdav_status_snapshot(args.base_url, args.api_key, args.timeout_seconds)
    rc_before = rclone_rc_snapshot(args.rclone_rc_url, rc_headers, args.timeout_seconds)
    resource_snapshots.append(
        {
            "nzbdav_status": status_before or {"available": False},
            "rclone_rc": rc_before or {"available": False},
            "nzbdav_process": process_snapshot(args.nzbdav_pid, "nzbdav") or {"available": False},
            "rclone_process": process_snapshot(args.rclone_pid, "rclone") or {"available": False},
            "captured_at": utc_now(),
        }
    )
    if status_before and (status_before.get("available") or args.api_key):
        add_check(checks, "NZBDav fullstatus", status_before.get("available") is True, status_before)
    if args.rclone_rc_url:
        add_check(checks, "rclone RC", bool(rc_before and rc_before.get("available")), rc_before)

    for run_index in range(args.runs):
        for path in paths:
            content_length = get_content_length(args.base_url, path, headers, args.timeout_seconds)
            first_byte = measure_first_byte(args.base_url, path, headers, args.timeout_seconds)
            if first_byte.first_byte_ms is not None and first_byte.bytes_read > 0:
                first_byte_samples.append(first_byte.first_byte_ms)
            add_check(
                checks,
                f"first-byte range {redact_path(path)} run {run_index + 1}",
                first_byte.status in (200, 206) and first_byte.bytes_read > 0,
                {"status": first_byte.status, "bytes_read": first_byte.bytes_read, "error": first_byte.error},
            )
            operation_samples.append(operation_record("first_byte", path, first_byte, run_index))

            for offset in build_seek_offsets(content_length, args.seek_count, args.seek_offsets):
                seek = measure_seek(args.base_url, path, offset, headers, args.timeout_seconds)
                record_seek_result(checks, seek_samples, operation_samples, path, offset, seek, run_index)

            sequential, throughput = measure_sequential(
                args.base_url,
                path,
                args.sequential_bytes,
                headers,
                args.timeout_seconds,
            )
            if throughput is not None:
                throughput_samples.append(throughput)
            add_check(
                checks,
                f"sequential read {redact_path(path)} run {run_index + 1}",
                sequential.status in (200, 206) and sequential.bytes_read > 0,
                {
                    "status": sequential.status,
                    "bytes_read": sequential.bytes_read,
                    "throughput_mib_s": round_value(throughput),
                    "error": sequential.error,
                },
            )
            operation_samples.append(
                operation_record("sequential", path, sequential, run_index, {"throughput_mib_s": throughput})
            )

        resource_snapshots.append(
            {
                "nzbdav_status": nzbdav_status_snapshot(args.base_url, args.api_key, args.timeout_seconds)
                or {"available": False},
                "rclone_rc": rclone_rc_snapshot(args.rclone_rc_url, rc_headers, args.timeout_seconds)
                or {"available": False},
                "nzbdav_process": process_snapshot(args.nzbdav_pid, "nzbdav") or {"available": False},
                "rclone_process": process_snapshot(args.rclone_pid, "rclone") or {"available": False},
                "captured_at": utc_now(),
            }
        )

    for path in args.fail_closed_paths:
        result = request_url(
            join_url(args.base_url, path),
            method="HEAD",
            headers=headers,
            timeout_seconds=args.timeout_seconds,
            read_body=False,
        )
        add_check(
            checks,
            f"fail-closed path {redact_path(path)}",
            result.status is None or result.status >= 400,
            {"status": result.status, "error": result.error},
        )

    document = {
        "schema_version": 1,
        "generated_at": utc_now(),
        "scenario": args.scenario,
        "inputs": {
            "base_url": redact_url(args.base_url),
            "paths": [redact_path(path) for path in paths],
            "runs": args.runs,
            "seek_count": args.seek_count,
            "seek_offsets": args.seek_offsets,
            "sequential_bytes": args.sequential_bytes,
            "rclone_rc_url_configured": bool(args.rclone_rc_url),
            "nzbdav_pid_configured": bool(args.nzbdav_pid),
            "rclone_pid_configured": bool(args.rclone_pid),
            "fail_closed_paths": [redact_path(path) for path in args.fail_closed_paths],
        },
        "metrics": {
            "first_byte_latency_ms": summarize_latency(first_byte_samples),
            "seek_latency_ms": summarize_latency(seek_samples),
            "sequential_throughput_mib_s": summarize_throughput(throughput_samples),
        },
        "checks": {"passed": checks_passed(checks), "items": checks},
        "resources": {
            "snapshots": resource_snapshots,
            "summary": summarize_resources(resource_snapshots),
        },
        "samples": operation_samples,
    }

    return document


def record_seek_result(
    checks: list[dict[str, Any]],
    seek_samples: list[float],
    operation_samples: list[dict[str, Any]],
    path: str,
    offset: int,
    seek: HttpResult,
    run_index: int,
) -> None:
    seek_valid = is_valid_seek_response(seek, offset)
    if seek_valid and seek.first_byte_ms is not None:
        seek_samples.append(seek.first_byte_ms)
    add_check(
        checks,
        f"seek range {redact_path(path)} offset {offset} run {run_index + 1}",
        seek_valid,
        {
            "status": seek.status,
            "bytes_read": seek.bytes_read,
            "offset": offset,
            "content_range": seek.headers.get("content-range"),
            "error": seek.error,
        },
    )
    operation_samples.append(operation_record("seek", path, seek, run_index, {"offset": offset}))


def operation_record(
    operation: str,
    path: str,
    result: HttpResult,
    run_index: int,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    record = {
        "operation": operation,
        "path": redact_path(path),
        "run": run_index + 1,
        "status": result.status,
        "elapsed_ms": round_value(result.elapsed_ms),
        "first_byte_ms": round_value(result.first_byte_ms),
        "bytes_read": result.bytes_read,
        "error": result.error,
    }
    if extra:
        record.update(extra)
    return record


def get_nested(document: dict[str, Any], path: list[str]) -> Any:
    value: Any = document
    for part in path:
        if not isinstance(value, dict) or part not in value:
            return None
        value = value[part]
    return value


def evaluate_acceptance(baseline: dict[str, Any], candidate: dict[str, Any]) -> dict[str, Any]:
    baseline_seek = get_nested(baseline, ["metrics", "seek_latency_ms", "p95"])
    candidate_seek = get_nested(candidate, ["metrics", "seek_latency_ms", "p95"])
    baseline_cpu = get_nested(baseline, ["resources", "summary", "total", "cpu_cores_max"])
    candidate_cpu = get_nested(candidate, ["resources", "summary", "total", "cpu_cores_max"])
    baseline_rss = get_nested(baseline, ["resources", "summary", "total", "rss_bytes_max"])
    candidate_rss = get_nested(candidate, ["resources", "summary", "total", "rss_bytes_max"])
    baseline_checks_passed = get_nested(baseline, ["checks", "passed"]) is True
    candidate_checks_passed = get_nested(candidate, ["checks", "passed"]) is True

    rules = [
        comparable_inputs_rule(baseline, candidate),
        {
            "name": "baseline correctness checks pass",
            "passed": baseline_checks_passed,
            "baseline": baseline_checks_passed,
            "candidate": None,
            "threshold": True,
            "detail": "baseline checks.passed must be true",
        },
        improvement_rule(
            "p95 seek latency improves at least 20%",
            baseline_seek,
            candidate_seek,
            lower_is_better=True,
            max_ratio=0.80,
            missing_is_failure=True,
        ),
        resource_rule("CPU not worse by more than 10%", baseline_cpu, candidate_cpu),
        resource_rule("RSS not worse by more than 10%", baseline_rss, candidate_rss),
        {
            "name": "correctness and fail-closed checks pass",
            "passed": candidate_checks_passed,
            "baseline": None,
            "candidate": candidate_checks_passed,
            "threshold": True,
            "detail": "candidate checks.passed must be true",
        },
    ]

    accepted = all(rule["passed"] for rule in rules)
    return {
        "schema_version": 1,
        "generated_at": utc_now(),
        "accepted": accepted,
        "baseline_scenario": baseline.get("scenario"),
        "candidate_scenario": candidate.get("scenario"),
        "rules": rules,
    }


def comparable_inputs_rule(baseline: dict[str, Any], candidate: dict[str, Any]) -> dict[str, Any]:
    fields = ["paths", "seek_count", "seek_offsets", "sequential_bytes", "runs", "fail_closed_paths"]
    mismatches = []
    for field in fields:
        baseline_value = get_nested(baseline, ["inputs", field])
        candidate_value = get_nested(candidate, ["inputs", field])
        if baseline_value != candidate_value:
            mismatches.append({"field": field, "baseline": baseline_value, "candidate": candidate_value})

    return {
        "name": "benchmark inputs are comparable",
        "passed": not mismatches,
        "baseline": {field: get_nested(baseline, ["inputs", field]) for field in fields},
        "candidate": {field: get_nested(candidate, ["inputs", field]) for field in fields},
        "threshold": "exact match",
        "detail": "paths, seek_count, seek_offsets, sequential_bytes, runs, and fail_closed_paths must match",
        "mismatches": mismatches,
    }


def improvement_rule(
    name: str,
    baseline_value: float | int | None,
    candidate_value: float | int | None,
    *,
    lower_is_better: bool,
    max_ratio: float,
    missing_is_failure: bool,
) -> dict[str, Any]:
    if baseline_value is None or candidate_value is None:
        return {
            "name": name,
            "passed": not missing_is_failure,
            "baseline": baseline_value,
            "candidate": candidate_value,
            "threshold": max_ratio,
            "detail": "missing metric",
        }
    if baseline_value <= 0:
        return {
            "name": name,
            "passed": False,
            "baseline": baseline_value,
            "candidate": candidate_value,
            "threshold": max_ratio,
            "detail": "baseline must be greater than zero",
        }

    ratio = candidate_value / baseline_value
    passed = ratio <= max_ratio if lower_is_better else ratio >= max_ratio
    return {
        "name": name,
        "passed": passed,
        "baseline": baseline_value,
        "candidate": candidate_value,
        "ratio": round_value(ratio),
        "threshold": max_ratio,
        "detail": f"candidate/baseline ratio must be <= {max_ratio}",
    }


def resource_rule(name: str, baseline_value: float | int | None, candidate_value: float | int | None) -> dict[str, Any]:
    if baseline_value is None or candidate_value is None:
        return {
            "name": name,
            "passed": True,
            "baseline": baseline_value,
            "candidate": candidate_value,
            "threshold": 1.10,
            "detail": "metric unavailable; resource comparison is best-effort",
        }
    if baseline_value <= 0:
        return {
            "name": name,
            "passed": False,
            "baseline": baseline_value,
            "candidate": candidate_value,
            "threshold": 1.10,
            "detail": "baseline must be greater than zero",
        }

    ratio = candidate_value / baseline_value
    return {
        "name": name,
        "passed": ratio <= 1.10,
        "baseline": baseline_value,
        "candidate": candidate_value,
        "ratio": round_value(ratio),
        "threshold": 1.10,
        "detail": "candidate/baseline ratio must be <= 1.10",
    }


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def redact_url(url: str) -> str:
    parsed = urllib.parse.urlparse(url)
    netloc = parsed.hostname or ""
    if parsed.port:
        netloc += f":{parsed.port}"
    return urllib.parse.urlunparse(parsed._replace(netloc=netloc, query="", fragment=""))


def parse_csv_ints(value: str | None) -> list[int]:
    if not value:
        return []
    return [int(part.strip()) for part in value.split(",") if part.strip()]


def parse_paths(values: list[str], env_value: str | None) -> list[str]:
    paths: list[str] = []
    for value in values:
        paths.extend(part.strip() for part in value.split(",") if part.strip())
    if env_value:
        paths.extend(part.strip() for part in env_value.split(",") if part.strip())
    return paths


def parse_optional_int(value: str | None) -> int | None:
    if not value:
        return None
    return int(value)


def write_json(document: dict[str, Any], output: Path | None, output_dir: Path, prefix: str) -> Path:
    if output is None:
        output_dir.mkdir(parents=True, exist_ok=True)
        timestamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        output = output_dir / f"{prefix}-{timestamp}.json"
    else:
        output.parent.mkdir(parents=True, exist_ok=True)

    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return output


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    run_parser = subparsers.add_parser("run", help="run a benchmark scenario")
    run_parser.add_argument("--scenario", default=os.getenv("NZBDAV_BENCH_SCENARIO", "rclone"))
    run_parser.add_argument("--base-url", default=os.getenv("NZBDAV_BENCH_BASE_URL"), required=not os.getenv("NZBDAV_BENCH_BASE_URL"))
    run_parser.add_argument("--path", action="append", default=[], help="WebDAV path or absolute URL; repeat or comma-separate")
    run_parser.add_argument("--webdav-user", default=os.getenv("NZBDAV_BENCH_WEBDAV_USER"))
    run_parser.add_argument("--webdav-pass", default=os.getenv("NZBDAV_BENCH_WEBDAV_PASS"))
    run_parser.add_argument("--api-key", default=os.getenv("NZBDAV_BENCH_API_KEY"))
    run_parser.add_argument("--rclone-rc-url", default=os.getenv("RCLONE_RC_URL") or os.getenv("NZBDAV_BENCH_RCLONE_RC_URL"))
    run_parser.add_argument("--rclone-rc-user", default=os.getenv("RCLONE_RC_USER") or os.getenv("NZBDAV_BENCH_RCLONE_RC_USER"))
    run_parser.add_argument("--rclone-rc-pass", default=os.getenv("RCLONE_RC_PASS") or os.getenv("NZBDAV_BENCH_RCLONE_RC_PASS"))
    run_parser.add_argument("--nzbdav-pid", type=int, default=parse_optional_int(os.getenv("NZBDAV_BENCH_NZBDAV_PID")))
    run_parser.add_argument("--rclone-pid", type=int, default=parse_optional_int(os.getenv("NZBDAV_BENCH_RCLONE_PID")))
    run_parser.add_argument("--runs", type=int, default=int(os.getenv("NZBDAV_BENCH_RUNS", DEFAULT_RUNS)))
    run_parser.add_argument("--seek-count", type=int, default=int(os.getenv("NZBDAV_BENCH_SEEK_COUNT", DEFAULT_SEEK_COUNT)))
    run_parser.add_argument("--seek-offsets", default=os.getenv("NZBDAV_BENCH_SEEK_OFFSETS"))
    run_parser.add_argument(
        "--sequential-bytes",
        type=int,
        default=int(os.getenv("NZBDAV_BENCH_SEQUENTIAL_BYTES", DEFAULT_SEQUENTIAL_BYTES)),
    )
    run_parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=float(os.getenv("NZBDAV_BENCH_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS)),
    )
    run_parser.add_argument("--fail-closed-path", action="append", default=[])
    run_parser.add_argument("--output-dir", type=Path, default=Path(os.getenv("NZBDAV_BENCH_OUTPUT_DIR", DEFAULT_OUTPUT_DIR)))
    run_parser.add_argument("--output", type=Path)

    evaluate_parser = subparsers.add_parser("evaluate", help="evaluate DFS/native candidate evidence against rclone baseline")
    evaluate_parser.add_argument("--baseline", type=Path, required=True)
    evaluate_parser.add_argument("--candidate", type=Path, required=True)
    evaluate_parser.add_argument("--output-dir", type=Path, default=Path(os.getenv("NZBDAV_BENCH_OUTPUT_DIR", DEFAULT_OUTPUT_DIR)))
    evaluate_parser.add_argument("--output", type=Path)
    evaluate_parser.add_argument("--gate", action="store_true", help="exit non-zero when acceptance fails")

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command == "run":
        args.paths = parse_paths(args.path, os.getenv("NZBDAV_BENCH_PATHS"))
        args.seek_offsets = parse_csv_ints(args.seek_offsets)
        args.fail_closed_paths = parse_paths(args.fail_closed_path, os.getenv("NZBDAV_BENCH_FAIL_CLOSED_PATHS"))
        document = run_benchmark(args)
        output = write_json(document, args.output, args.output_dir, args.scenario)
        print(output)
        return 0 if document["checks"]["passed"] else 2

    if args.command == "evaluate":
        evaluation = evaluate_acceptance(load_json(args.baseline), load_json(args.candidate))
        output = write_json(evaluation, args.output, args.output_dir, "evaluation")
        print(output)
        if args.gate and not evaluation["accepted"]:
            return 1
        return 0

    parser.error(f"unknown command: {args.command}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
