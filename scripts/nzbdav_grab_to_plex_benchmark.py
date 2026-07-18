#!/usr/bin/env python3
"""Measure one NZBDav addfile through ARR import and exact Plex metadata readiness."""

from __future__ import annotations

import argparse
import datetime as dt
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import sys
import time
from typing import Any, Iterable, Mapping, Protocol
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET


RUN_KIND = "nzbdav-grab-to-plex-run"
RUN_SCHEMA_VERSION = 2
AGGREGATE_SCHEMA_VERSION = 1
PRIMARY_REQUEST_START_METRIC = "sab_request_start_to_plex_basic_metadata_ready_ms"
PRIMARY_SAB_ACCEPTED_METRIC = "sab_accepted_to_plex_basic_metadata_ready_ms"
DEFAULT_OUTPUT_DIR = Path("artifacts/grab-to-plex")
MAX_RESPONSE_BYTES = 32 * 1024 * 1024
IMPORTED_EVENT_TYPES = {"downloadfolderimported"}
FAILED_EVENT_TYPES = {"downloadfailed", "downloadfolderimportfailed"}


@dataclass(frozen=True)
class HttpRequest:
    method: str
    url: str
    headers: dict[str, str]
    body: bytes | None = None
    timeout_seconds: float = 10.0


@dataclass(frozen=True)
class HttpResponse:
    status: int
    headers: dict[str, str]
    body: bytes


class Transport(Protocol):
    def send(self, request: HttpRequest) -> HttpResponse: ...


class Clock(Protocol):
    def monotonic(self) -> float: ...
    def utcnow(self) -> dt.datetime: ...
    def sleep(self, seconds: float) -> None: ...


class SystemClock:
    def monotonic(self) -> float:
        return time.monotonic()

    def utcnow(self) -> dt.datetime:
        return dt.datetime.now(dt.timezone.utc)

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)


class UrllibTransport:
    def send(self, request: HttpRequest) -> HttpResponse:
        url_request = urllib.request.Request(
            request.url,
            data=request.body,
            headers=request.headers,
            method=request.method,
        )
        try:
            with urllib.request.urlopen(url_request, timeout=request.timeout_seconds) as response:
                body = read_bounded(response)
                return HttpResponse(
                    status=response.status,
                    headers={key.lower(): value for key, value in response.headers.items()},
                    body=body,
                )
        except urllib.error.HTTPError as error:
            body = read_bounded(error)
            return HttpResponse(
                status=error.code,
                headers={key.lower(): value for key, value in error.headers.items()},
                body=body,
            )
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise RuntimeError("HTTP transport failed") from error


def read_bounded(source: Any) -> bytes:
    body = source.read(MAX_RESPONSE_BYTES + 1)
    if len(body) > MAX_RESPONSE_BYTES:
        raise RuntimeError("HTTP response exceeded the safety limit")
    return body


@dataclass(frozen=True)
class RunConfig:
    nzbdav_base_url: str
    nzbdav_api_key: str
    nzb_path: Path
    category: str
    arr_kind: str
    arr_base_url: str
    arr_api_key: str
    plex_base_url: str
    plex_token: str
    plex_section_id: str
    plex_final_files: tuple[str, ...]
    basic_fields: tuple[str, ...]
    rich_fields: tuple[str, ...] = ()
    require_rich_metadata: bool = False
    poll_interval_seconds: float = 0.25
    timeout_seconds: float = 120.0
    rich_timeout_seconds: float = 0.0
    http_timeout_seconds: float = 10.0
    plex_page_size: int = 200
    allow_existing_plex_item: bool = False
    force_arr_refresh: bool = False
    force_plex_scan: bool = False


class BenchmarkFailure(RuntimeError):
    def __init__(self, code: str, message: str, *, timed_out: bool = False):
        super().__init__(message)
        self.code = code
        self.safe_message = message
        self.timed_out = timed_out


class StageRecorder:
    def __init__(self, clock: Clock):
        self.clock = clock
        self.origin = clock.monotonic()
        self.stages: dict[str, dict[str, Any]] = {}

    def mark(self, name: str) -> dict[str, Any]:
        if name in self.stages:
            return self.stages[name]
        monotonic = self.clock.monotonic()
        timestamp = {
            "utc": self.clock.utcnow().astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
            "monotonic_seconds": round(monotonic, 6),
            "elapsed_ms": round((monotonic - self.origin) * 1000, 3),
        }
        self.stages[name] = timestamp
        return timestamp


def default_basic_fields(arr_kind: str) -> tuple[str, ...]:
    if arr_kind == "sonarr":
        return ("ratingKey", "title", "grandparentTitle", "parentIndex", "index")
    if arr_kind == "radarr":
        return ("ratingKey", "title", "year")
    raise ValueError("arr_kind must be sonarr or radarr")


class BenchmarkRunner:
    PLEX_OBSERVATION_MAX_PAGES_PER_POLL = 3

    def __init__(self, config: RunConfig, *, transport: Transport, clock: Clock | None = None):
        self.config = config
        self.transport = transport
        self.clock = clock or SystemClock()
        self.recorder = StageRecorder(self.clock)
        self.arr_refresh_mode = "forced" if config.force_arr_refresh else "observe"
        self.plex_scan_mode = "forced" if config.force_plex_scan else "observe"
        self.measurement = {
            "valid": True,
            "isolated": True,
            "production_representative": not config.force_arr_refresh and not config.force_plex_scan,
            "observation_model": "serial_phase_polling",
            "intermediate_stage_timestamps_are_upper_bounds": True,
            "primary_origins": {
                "request_start": "nzbdav_addfile_request_started",
                "sab_accepted": "nzbdav_addfile_accepted",
            },
        }
        self.observations: dict[str, Any] = {
            "nzbdav": {"queue_transitions": []},
            "arr": {
                "refresh_mode": self.arr_refresh_mode,
                "queue_transitions": [],
                "command_transitions": [],
                "history_event_types": [],
                "history_import_observed": False,
                "nzbdav_receipt_imported_observed": False,
                "nzbdav_receipt_measurement": "not_measured",
            },
            "plex": {
                "scan_mode": self.plex_scan_mode,
                "observer": {
                    "strategy": "recently_added_bounded_window",
                    "sort": "addedAt:desc",
                    "max_pages_per_poll": self.PLEX_OBSERVATION_MAX_PAGES_PER_POLL,
                    "max_items_per_poll": (
                        self.PLEX_OBSERVATION_MAX_PAGES_PER_POLL * config.plex_page_size
                    ),
                    "requests": 0,
                    "pages": 0,
                    "window_exhaustions": 0,
                },
                "scan": {"accepted": False, "paths": []},
                "visibility": {"exact_part_files": list(config.plex_final_files), "visible": False},
                "basic_metadata": {"fields": list(config.basic_fields), "ready": False},
                "rich_metadata": {
                    "requested": bool(config.rich_fields),
                    "required": config.require_rich_metadata,
                    "fields": list(config.rich_fields),
                    "ready": None if not config.rich_fields else False,
                },
            },
        }
        self._nzb_info: dict[str, Any] = {}

    def run(self, *, dry_run: bool = False) -> dict[str, Any]:
        self.recorder.mark("benchmark_started")
        artifact = self._base_artifact(dry_run)
        try:
            checks = self._preflight()
            artifact["checks"] = checks
            failures = [check for check in checks if check["severity"] == "error" and not check["passed"]]
            if failures:
                first = failures[0]
                raise BenchmarkFailure(first["code"], first["message"])

            if dry_run:
                artifact["status"] = "validated"
                self.recorder.mark("validation_completed")
                return self._finish_artifact(artifact)

            self.recorder.mark("nzbdav_addfile_request_started")
            nzo_id = self._submit_nzb()
            artifact["run"]["nzo_id"] = nzo_id
            self.recorder.mark("nzbdav_addfile_accepted")
            deadline = (
                self.recorder.stages["nzbdav_addfile_request_started"]["monotonic_seconds"]
                + self.config.timeout_seconds
            )

            self._wait_for_nzbdav_completion(nzo_id, deadline)
            command_id = self._start_arr_refresh() if self.config.force_arr_refresh else None
            artifact["run"]["arr_command_id"] = command_id
            self._wait_for_arr_import(nzo_id, command_id, deadline)
            if self.config.force_plex_scan:
                self._start_plex_scan()
            matches = self._wait_for_plex_basic_metadata(deadline)
            self._observe_rich_metadata(matches)

            artifact["status"] = "completed"
            self.recorder.mark("benchmark_completed")
        except BenchmarkFailure as error:
            artifact["status"] = "timed_out" if error.timed_out else "failed"
            artifact["error"] = {"code": error.code, "message": error.safe_message}
            self.recorder.mark("benchmark_failed")
        return self._finish_artifact(artifact)

    def _base_artifact(self, dry_run: bool) -> dict[str, Any]:
        return {
            "schema_version": RUN_SCHEMA_VERSION,
            "kind": RUN_KIND,
            "status": "running",
            "dry_run": dry_run,
            "measurement": self.measurement,
            "run": {"nzo_id": None, "arr_command_id": None},
            "inputs": {
                "arr_kind": self.config.arr_kind,
                "arr_refresh_mode": self.arr_refresh_mode,
                "plex_scan_mode": self.plex_scan_mode,
                "category": self.config.category,
                "nzbdav_base_url": redact_url(self.config.nzbdav_base_url),
                "arr_base_url": redact_url(self.config.arr_base_url),
                "plex_base_url": redact_url(self.config.plex_base_url),
                "plex_section_id": self.config.plex_section_id,
                "plex_final_files": list(self.config.plex_final_files),
                "basic_metadata_fields": list(self.config.basic_fields),
                "rich_metadata_fields": list(self.config.rich_fields),
                "require_rich_metadata": self.config.require_rich_metadata,
                "poll_interval_seconds": self.config.poll_interval_seconds,
                "timeout_seconds": self.config.timeout_seconds,
                "rich_timeout_seconds": self.config.rich_timeout_seconds,
            },
            "checks": [],
            "stages": self.recorder.stages,
            "durations_ms": {},
            "observations": self.observations,
            "error": None,
        }

    def _finish_artifact(self, artifact: dict[str, Any]) -> dict[str, Any]:
        artifact["inputs"]["nzb"] = self._nzb_info
        artifact["stages"] = self.recorder.stages
        artifact["durations_ms"] = stage_durations(self.recorder.stages)
        return artifact

    def _preflight(self) -> list[dict[str, Any]]:
        checks: list[dict[str, Any]] = []
        try:
            validate_config(self.config)
            checks.append(check("configuration", True, "configuration_valid", "Configuration is valid."))
        except (ValueError, OSError) as error:
            checks.append(check("configuration", False, "configuration_invalid", str(error)))
            return checks

        try:
            self._nzb_info = inspect_nzb(self.config.nzb_path)
            checks.append(check("NZB input", True, "nzb_valid", "NZB input is readable and structurally valid."))
        except (ValueError, OSError, ET.ParseError):
            checks.append(check("NZB input", False, "nzb_invalid", "NZB input is unreadable or structurally invalid."))
            return checks

        try:
            full_status_document = self._request_json(
                "GET",
                self.config.nzbdav_base_url,
                "/api",
                query={"mode": "fullstatus", "output": "json"},
                headers=self._nzbdav_headers(),
                expected=(200,),
                operation="NZBDav authenticated full-status probe",
            )
            full_status = full_status_document.get("status")
            if not isinstance(full_status, dict):
                raise BenchmarkFailure(
                    "nzbdav_fullstatus_invalid",
                    "NZBDav full-status response did not contain a status object.",
                )
            checks.append(check(
                "NZBDav connectivity and authentication",
                True,
                "nzbdav_reachable",
                "NZBDav authenticated full-status probe succeeded.",
            ))
            checks.extend(self._full_status_preflight_checks(full_status))
        except BenchmarkFailure:
            checks.append(check(
                "NZBDav connectivity and authentication",
                False,
                "nzbdav_unreachable",
                "NZBDav authenticated status probe failed.",
            ))

        try:
            arr_validation = self._request_json(
                "GET",
                self.config.nzbdav_base_url,
                "/api/arr/validation",
                headers=self._nzbdav_headers(),
                expected=(200,),
                operation="NZBDav ARR validation probe",
            )
            instance_count = parse_int(arr_validation.get("instance_count"))
            has_instance = instance_count is not None and instance_count > 0
            checks.append(check(
                "NZBDav ARR instance routing",
                has_instance,
                "arr_instance_present" if has_instance else "arr_instance_missing",
                "NZBDav reports at least one ARR instance." if has_instance
                else "NZBDav reports no ARR instances; the grab cannot be observed through ARR import.",
            ))
            issues = arr_validation.get("issues")
            issues = issues if isinstance(issues, list) else []
            error_issues = [
                issue for issue in issues
                if isinstance(issue, dict) and str(issue.get("severity") or "").casefold() == "error"
            ]
            checks.append(check(
                "NZBDav ARR validation errors",
                not error_issues,
                "arr_validation_clean" if not error_issues else "arr_validation_failed",
                "NZBDav ARR validation reports no errors." if not error_issues
                else "NZBDav ARR validation reports one or more errors.",
            ))
        except BenchmarkFailure:
            checks.append(check(
                "NZBDav ARR validation",
                False,
                "arr_validation_unreachable",
                "NZBDav ARR validation probe failed.",
            ))

        try:
            arr_status = self._request_json(
                "GET",
                self.config.arr_base_url,
                "/api/v3/system/status",
                headers=self._arr_headers(),
                expected=(200,),
                operation="ARR system status probe",
            )
            checks.append(check("ARR connectivity", True, "arr_reachable", "ARR system status probe succeeded."))
            app_name = str(arr_status.get("appName") or "")
            kind_matches = self.config.arr_kind.casefold() in app_name.casefold()
            checks.append(check(
                "ARR application kind",
                kind_matches,
                "arr_application_matches" if kind_matches else "arr_application_mismatch",
                (
                    f"ARR endpoint reports {self.config.arr_kind}."
                    if kind_matches
                    else f"ARR endpoint does not report the configured {self.config.arr_kind} application."
                ),
            ))
            arr_queue = self._request_json(
                "GET",
                self.config.arr_base_url,
                "/api/v3/queue",
                query={"page": "1", "pageSize": "1"},
                headers=self._arr_headers(),
                expected=(200,),
                operation="ARR queue isolation probe",
            )
            total_records = parse_int(arr_queue.get("totalRecords"))
            records = arr_queue.get("records")
            record_count = len(records) if isinstance(records, list) else 0
            queue_count = max(0, total_records if total_records is not None else record_count)
            checks.append(check(
                "ARR queue isolation",
                queue_count == 0,
                "arr_queue_empty" if queue_count == 0 else "arr_queue_not_empty",
                "ARR reports an empty queue." if queue_count == 0
                else "ARR already has queued work that would invalidate an isolated run.",
            ))
        except BenchmarkFailure:
            checks.append(check("ARR connectivity", False, "arr_unreachable", "ARR system status probe failed."))

        try:
            self._request_json(
                "GET",
                self.config.plex_base_url,
                "/identity",
                headers=self._plex_headers(),
                expected=(200,),
                operation="Plex identity probe",
            )
            checks.append(check("Plex connectivity", True, "plex_reachable", "Plex identity probe succeeded."))

            sections = self._request_json(
                "GET",
                self.config.plex_base_url,
                "/library/sections",
                headers=self._plex_headers(),
                expected=(200,),
                operation="Plex library sections probe",
            )
            sections_container = sections.get("MediaContainer")
            directories = (
                sections_container.get("Directory", [])
                if isinstance(sections_container, dict)
                else []
            )
            section = next(
                (
                    item
                    for item in directories
                    if isinstance(item, dict) and str(item.get("key")) == self.config.plex_section_id
                ),
                None,
            ) if isinstance(directories, list) else None
            if section is None:
                checks.append(check(
                    "Plex library section",
                    False,
                    "plex_section_not_found",
                    "Configured Plex library section was not found.",
                ))
            else:
                expected_type = "show" if self.config.arr_kind == "sonarr" else "movie"
                actual_type = str(section.get("type") or "").casefold()
                type_matches = actual_type == expected_type
                checks.append(check(
                    "Plex library section type",
                    type_matches,
                    "plex_section_type_matches" if type_matches else "plex_section_type_mismatch",
                    (
                        f"Plex section is a {expected_type} library."
                        if type_matches
                        else f"Plex section is not a {expected_type} library."
                    ),
                ))
        except BenchmarkFailure:
            checks.append(check(
                "Plex connectivity and authentication",
                False,
                "plex_unreachable",
                "Plex authenticated identity/library probe failed.",
            ))

        if any(not item["passed"] and item["severity"] == "error" for item in checks):
            return checks

        existing = self._find_plex_parts()
        isolated = not existing
        if isolated:
            checks.append(check(
                "Plex pre-existing exact Part.file",
                True,
                "plex_part_absent",
                "No expected exact Plex Part.file exists before submission.",
            ))
        elif self.config.allow_existing_plex_item:
            checks.append(check(
                "Plex pre-existing exact Part.file",
                True,
                "plex_part_already_present_allowed",
                "An expected Plex Part.file already exists; the run is explicitly non-isolated.",
                severity="warning",
            ))
            self.observations["plex"]["visibility"]["pre_existing"] = sorted(existing)
            # The run may still be operationally useful, but an already-visible
            # item cannot prove a grab-to-visible latency.
            self.measurement.update({"valid": False, "isolated": False})
        else:
            checks.append(check(
                "Plex pre-existing exact Part.file",
                False,
                "plex_part_already_present",
                "An expected exact Plex Part.file already exists; refusing a false-positive benchmark.",
            ))
        return checks

    def _full_status_preflight_checks(self, status: dict[str, Any]) -> list[dict[str, Any]]:
        checks: list[dict[str, Any]] = []
        paused = status.get("paused") is True or status.get("paused_all") is True
        checks.append(check(
            "NZBDav queue is not paused",
            not paused,
            "nzbdav_queue_unpaused" if not paused else "nzbdav_queue_paused",
            "NZBDav queue is not paused." if not paused else "NZBDav queue is paused.",
        ))

        worker_queues = status.get("worker_queues")
        worker_queues = worker_queues if isinstance(worker_queues, dict) else {}
        workload_fields = (
            "download_active", "download_waiting", "download_ready", "download_retry", "download_quarantined",
            "verify_active", "verify_ready", "verify_retry", "verify_quarantined",
            "repair_active", "repair_action_needed", "repair_ready", "repair_retry", "repair_quarantined",
        )
        workload = sum(max(0, parse_int(worker_queues.get(field)) or 0) for field in workload_fields)
        workload += max(0, parse_int(status.get("jobs")) or 0)
        workload += max(0, parse_int(status.get("jobs_active")) or 0)
        checks.append(check(
            "NZBDav workload isolation",
            workload == 0,
            "nzbdav_workload_isolated" if workload == 0 else "nzbdav_workload_not_isolated",
            "NZBDav reports no conflicting queue or worker work." if workload == 0
            else "NZBDav reports existing queue or worker work that would invalidate an isolated run.",
        ))

        rclone = status.get("rclone_invalidations")
        rclone = rclone if isinstance(rclone, dict) else {}
        fence_required = rclone.get("visibility_fence_required") is True
        rc_possible = (
            not fence_required
            or (rclone.get("remote_control_enabled") is True and rclone.get("host_configured") is True)
        )
        checks.append(check(
            "Rclone visibility-fence configuration",
            rc_possible,
            "rclone_visibility_fence_available" if rc_possible else "rclone_visibility_fence_unavailable",
            "Rclone visibility-fence configuration is satisfiable." if rc_possible
            else "Rclone visibility is required but remote control is disabled or has no configured host.",
        ))
        whole_cache_fence_pending = rclone.get("whole_cache_visibility_fence_pending") is True
        checks.append(check(
            "Rclone whole-cache visibility proof",
            not whole_cache_fence_pending,
            "rclone_whole_cache_fence_clear" if not whole_cache_fence_pending
            else "rclone_whole_cache_fence_pending",
            "No whole-cache rclone visibility proof is pending." if not whole_cache_fence_pending
            else "A whole-cache rclone visibility proof is pending and would contaminate the run.",
        ))
        failed = max(0, parse_int(rclone.get("failed")) or 0)
        pending = max(0, parse_int(rclone.get("pending")) or 0)
        checks.append(check(
            "Rclone invalidation failures",
            failed == 0,
            "rclone_invalidations_clean" if failed == 0 else "rclone_invalidations_failed",
            "No failed rclone invalidations are present." if failed == 0
            else "Failed rclone invalidations are present.",
        ))
        checks.append(check(
            "Rclone invalidation backlog",
            pending == 0,
            "rclone_invalidations_empty" if pending == 0 else "rclone_invalidations_pending",
            "No rclone invalidations are pending." if pending == 0
            else "Pending rclone invalidations would contaminate the run.",
        ))
        runtime_failed = bool(rclone.get("runtime_last_error"))
        checks.append(check(
            "Rclone configured-call runtime",
            not runtime_failed,
            "rclone_runtime_clean" if not runtime_failed else "rclone_runtime_failed",
            "The latest configured rclone call has no recorded error." if not runtime_failed
            else "The latest configured rclone call failed.",
        ))
        if fence_required:
            has_success = bool(rclone.get("last_successful_configured_call_at"))
            checks.append(check(
                "Rclone configured-call evidence",
                has_success,
                "rclone_configured_call_observed" if has_success else "rclone_configured_call_unverified",
                "A successful configured rclone call has been observed." if has_success
                else "No successful configured rclone call has been observed yet.",
                severity="warning",
            ))

        imports = status.get("arr_import_commands")
        imports = imports if isinstance(imports, dict) else {}
        active_import_fields = (
            "pending", "waiting_for_invalidation", "executing", "retry", "no_route", "quarantined",
        )
        import_backlog = sum(max(0, parse_int(imports.get(field)) or 0) for field in active_import_fields)
        checks.append(check(
            "NZBDav ARR import-command isolation",
            import_backlog == 0,
            "arr_import_backlog_empty" if import_backlog == 0 else "arr_import_backlog_present",
            "No ARR import commands can contaminate the run." if import_backlog == 0
            else "Existing ARR import commands would contaminate the run.",
        ))

        mount = status.get("mount")
        mount = mount if isinstance(mount, dict) else {}
        checks.append(check(
            "Mount and link traversal",
            False,
            "mount_link_support_unverified",
            "Preflight cannot verify external mount readiness or link traversal; later exact Plex Part.file evidence is still required.",
            severity="warning",
        ))
        return checks

    def _submit_nzb(self) -> str:
        body, content_type = build_multipart_nzb(self.config.nzb_path)
        document = self._request_json(
            "POST",
            self.config.nzbdav_base_url,
            "/api",
            query={"mode": "addfile", "output": "json", "cat": self.config.category},
            headers={**self._nzbdav_headers(), "Content-Type": content_type},
            body=body,
            expected=(200,),
            operation="NZBDav addfile",
        )
        if document.get("status") is not True:
            raise BenchmarkFailure("nzbdav_addfile_rejected", "NZBDav rejected the addfile request.")
        nzo_ids = document.get("nzo_ids")
        if not isinstance(nzo_ids, list) or len(nzo_ids) != 1 or not isinstance(nzo_ids[0], str):
            raise BenchmarkFailure(
                "nzbdav_addfile_invalid_response",
                "NZBDav addfile did not return exactly one nzo_id.",
            )
        return nzo_ids[0]

    def _wait_for_nzbdav_completion(self, nzo_id: str, deadline: float) -> None:
        while self.clock.monotonic() <= deadline:
            queue = self._nzbdav_state("queue", nzo_id).get("queue", {}).get("slots", [])
            queue_slot = find_download_record(queue, nzo_id, "nzo_id")
            if queue_slot:
                status = str(queue_slot.get("status") or "unknown")
                self._record_transition(self.observations["nzbdav"]["queue_transitions"], status)
                self.recorder.mark("nzbdav_queue_seen")

            history = self._nzbdav_state("history", nzo_id).get("history", {}).get("slots", [])
            history_slot = find_download_record(history, nzo_id, "nzo_id")
            if history_slot:
                status = str(history_slot.get("status") or "unknown").lower()
                self.observations["nzbdav"]["history_status"] = status
                if status == "completed":
                    self.recorder.mark("nzbdav_history_completed")
                    return
                if status == "failed":
                    raise BenchmarkFailure("nzbdav_history_failed", "NZBDav reported the submitted item as failed.")
            self.clock.sleep(self.config.poll_interval_seconds)
        raise BenchmarkFailure(
            "nzbdav_completion_timeout",
            "Timed out waiting for NZBDav completed history.",
            timed_out=True,
        )

    def _nzbdav_state(self, mode: str, nzo_id: str) -> dict[str, Any]:
        return self._request_json(
            "GET",
            self.config.nzbdav_base_url,
            "/api",
            query={"mode": mode, "output": "json", "nzo_ids": nzo_id},
            headers=self._nzbdav_headers(),
            expected=(200,),
            operation=f"NZBDav {mode}",
        )

    def _start_arr_refresh(self) -> int:
        document = self._request_json(
            "POST",
            self.config.arr_base_url,
            "/api/v3/command",
            headers={**self._arr_headers(), "Content-Type": "application/json"},
            body=json.dumps({"name": "RefreshMonitoredDownloads"}).encode("utf-8"),
            expected=(200, 201, 202),
            operation="ARR RefreshMonitoredDownloads command",
        )
        command_id = document.get("id")
        if not isinstance(command_id, int):
            raise BenchmarkFailure(
                "arr_command_invalid_response",
                "ARR command response did not contain an integer id.",
            )
        self.observations["arr"]["command_id"] = command_id
        self._record_transition(
            self.observations["arr"]["command_transitions"],
            str(document.get("status") or "accepted"),
        )
        self.recorder.mark("arr_command_accepted")
        return command_id

    def _wait_for_arr_import(self, nzo_id: str, command_id: int | None, deadline: float) -> None:
        while self.clock.monotonic() <= deadline:
            queue_doc = self._request_json(
                "GET",
                self.config.arr_base_url,
                "/api/v3/queue",
                query={"downloadId": nzo_id, "page": "1", "pageSize": "100"},
                headers=self._arr_headers(),
                expected=(200,),
                operation="ARR queue poll",
            )
            queue_record = find_download_record(queue_doc.get("records", []), nzo_id, "downloadId")
            if queue_record:
                self.recorder.mark("arr_queue_seen")
                self._record_transition(
                    self.observations["arr"]["queue_transitions"],
                    str(queue_record.get("status") or "unknown"),
                )

            if command_id is not None:
                command = self._request_json(
                    "GET",
                    self.config.arr_base_url,
                    f"/api/v3/command/{command_id}",
                    headers=self._arr_headers(),
                    expected=(200,),
                    operation="ARR command poll",
                )
                command_status = str(command.get("status") or "unknown").lower()
                self._record_transition(self.observations["arr"]["command_transitions"], command_status)
                if command_status == "completed":
                    self.recorder.mark("arr_command_completed")
                elif command_status in {"failed", "aborted", "cancelled"}:
                    raise BenchmarkFailure("arr_command_failed", "ARR RefreshMonitoredDownloads command failed.")

            history = self._request_json(
                "GET",
                self.config.arr_base_url,
                "/api/v3/history",
                query={
                    "downloadId": nzo_id,
                    "page": "1",
                    "pageSize": "100",
                    "sortKey": "date",
                    "sortDirection": "descending",
                    "includeSeries": "true",
                    "includeEpisode": "true",
                    "includeMovie": "true",
                },
                headers=self._arr_headers(),
                expected=(200,),
                operation="ARR history poll",
            )
            records = [
                record for record in history.get("records", [])
                if isinstance(record, dict) and ids_equal(record.get("downloadId"), nzo_id)
            ]
            event_types = [str(record.get("eventType") or "") for record in records]
            self.observations["arr"]["history_event_types"] = sorted(set(event_types))
            for record in records:
                event_type = str(record.get("eventType") or "").lower()
                if event_type in FAILED_EVENT_TYPES:
                    raise BenchmarkFailure("arr_import_failed", "ARR history reported a failed import.")
                if event_type in IMPORTED_EVENT_TYPES:
                    imported_paths = extract_imported_paths(records)
                    self.observations["arr"]["imported_paths"] = imported_paths
                    self.observations["arr"]["history_import_observed"] = True
                    self.recorder.mark("arr_history_imported")
                    return
            self.clock.sleep(self.config.poll_interval_seconds)
        raise BenchmarkFailure("arr_import_timeout", "Timed out waiting for ARR import history.", timed_out=True)

    def _start_plex_scan(self) -> None:
        scan_paths = sorted({plex_parent_path(path) for path in self.config.plex_final_files})
        for path in scan_paths:
            self._request(
                "GET",
                self.config.plex_base_url,
                f"/library/sections/{urllib.parse.quote(self.config.plex_section_id, safe='')}/refresh",
                query={"path": path},
                headers=self._plex_headers(),
                expected=(200, 201, 202),
                operation="Plex targeted scan",
            )
        self.observations["plex"]["scan"] = {"accepted": True, "paths": scan_paths}
        self.recorder.mark("plex_scan_accepted")

    def _wait_for_plex_basic_metadata(self, deadline: float) -> dict[str, dict[str, Any]]:
        visibility_marked = False
        while self.clock.monotonic() <= deadline:
            matches = self._find_plex_parts(observation_window=True)
            if len(matches) == len(self.config.plex_final_files):
                if not visibility_marked:
                    self.recorder.mark("plex_item_visible")
                    self.observations["plex"]["visibility"]["visible"] = True
                    visibility_marked = True
                missing_by_path: dict[str, list[str]] = {}
                for path in self.config.plex_final_files:
                    ready, missing = metadata_fields_ready(matches[path], self.config.basic_fields)
                    if not ready:
                        missing_by_path[path] = missing
                self.observations["plex"]["basic_metadata"]["missing_fields_by_part"] = missing_by_path
                if not missing_by_path:
                    self.observations["plex"]["basic_metadata"]["ready"] = True
                    self.recorder.mark("plex_basic_metadata_ready")
                    return matches
            self.clock.sleep(self.config.poll_interval_seconds)
        raise BenchmarkFailure(
            "plex_basic_metadata_timeout",
            "Timed out waiting for every exact Plex Part.file to become visible with basic metadata.",
            timed_out=True,
        )

    def _observe_rich_metadata(self, matches: dict[str, dict[str, Any]]) -> None:
        if not self.config.rich_fields:
            return
        deadline = self.clock.monotonic() + max(0.0, self.config.rich_timeout_seconds)
        current = matches
        while True:
            missing_by_path: dict[str, list[str]] = {}
            for path in self.config.plex_final_files:
                item = current.get(path, {})
                ready, missing = metadata_fields_ready(item, self.config.rich_fields)
                if not ready:
                    missing_by_path[path] = missing
            self.observations["plex"]["rich_metadata"]["missing_fields_by_part"] = missing_by_path
            if not missing_by_path:
                self.observations["plex"]["rich_metadata"]["ready"] = True
                self.recorder.mark("plex_rich_metadata_ready")
                return
            if self.clock.monotonic() >= deadline:
                if self.config.require_rich_metadata:
                    raise BenchmarkFailure(
                        "plex_rich_metadata_timeout",
                        "Timed out waiting for required rich Plex metadata.",
                        timed_out=True,
                    )
                return
            self.clock.sleep(self.config.poll_interval_seconds)
            current = self._find_plex_parts(observation_window=True)

    def _find_plex_parts(self, *, observation_window: bool = False) -> dict[str, dict[str, Any]]:
        matches: dict[str, dict[str, Any]] = {}
        start = 0
        pages_this_poll = 0
        while True:
            document = self._request_json(
                "GET",
                self.config.plex_base_url,
                f"/library/sections/{urllib.parse.quote(self.config.plex_section_id, safe='')}/all",
                query={
                    "type": "4" if self.config.arr_kind == "sonarr" else "1",
                    "sort": "addedAt:desc",
                    "includeGuids": "1",
                },
                headers={
                    **self._plex_headers(),
                    "X-Plex-Container-Start": str(start),
                    "X-Plex-Container-Size": str(self.config.plex_page_size),
                },
                expected=(200,),
                operation="Plex exact Part.file poll",
            )
            pages_this_poll += 1
            if observation_window:
                observer = self.observations["plex"]["observer"]
                observer["requests"] += 1
                observer["pages"] += 1
            matches.update(extract_exact_part_matches(document, self.config.plex_final_files))
            if len(matches) == len(self.config.plex_final_files):
                return matches
            container = document.get("MediaContainer", {})
            metadata = container.get("Metadata") or [] if isinstance(container, dict) else []
            size = len(metadata) if isinstance(metadata, list) else 0
            total = parse_int(container.get("totalSize")) if isinstance(container, dict) else None
            if size == 0:
                return matches
            if total is not None:
                if start + size >= total:
                    return matches
            elif size < self.config.plex_page_size:
                return matches
            if observation_window and pages_this_poll >= self.PLEX_OBSERVATION_MAX_PAGES_PER_POLL:
                self.observations["plex"]["observer"]["window_exhaustions"] += 1
                return matches
            start += size

    def _record_transition(self, transitions: list[dict[str, Any]], status: str) -> None:
        normalized = status.strip().lower() or "unknown"
        if transitions and transitions[-1]["status"] == normalized:
            return
        transitions.append({
            "status": normalized,
            "utc": self.clock.utcnow().astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
            "monotonic_seconds": round(self.clock.monotonic(), 6),
        })

    def _request_json(self, *args: Any, operation: str, **kwargs: Any) -> dict[str, Any]:
        response = self._request(*args, operation=operation, **kwargs)
        if not response.body:
            return {}
        try:
            document = json.loads(response.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise BenchmarkFailure("invalid_json_response", f"{operation} returned invalid JSON.") from error
        if not isinstance(document, dict):
            raise BenchmarkFailure("invalid_json_response", f"{operation} returned a non-object JSON response.")
        return document

    def _request(
        self,
        method: str,
        base_url: str,
        path: str,
        *,
        query: Mapping[str, str] | None = None,
        headers: dict[str, str] | None = None,
        body: bytes | None = None,
        expected: tuple[int, ...],
        operation: str,
    ) -> HttpResponse:
        url = build_url(base_url, path, query)
        try:
            response = self.transport.send(HttpRequest(
                method=method,
                url=url,
                headers=headers or {},
                body=body,
                timeout_seconds=self.config.http_timeout_seconds,
            ))
        except BenchmarkFailure:
            raise
        except Exception as error:
            raise BenchmarkFailure("http_request_failed", f"{operation} request failed.") from error
        if response.status not in expected:
            raise BenchmarkFailure("http_status_unexpected", f"{operation} returned HTTP {response.status}.")
        return response

    def _nzbdav_headers(self) -> dict[str, str]:
        return {"X-Api-Key": self.config.nzbdav_api_key, "Accept": "application/json"}

    def _arr_headers(self) -> dict[str, str]:
        return {"X-Api-Key": self.config.arr_api_key, "Accept": "application/json"}

    def _plex_headers(self) -> dict[str, str]:
        return {"X-Plex-Token": self.config.plex_token, "Accept": "application/json"}


def validate_config(config: RunConfig) -> None:
    if config.arr_kind not in {"sonarr", "radarr"}:
        raise ValueError("arr_kind must be sonarr or radarr")
    for label, value in (
        ("NZBDav base URL", config.nzbdav_base_url),
        ("ARR base URL", config.arr_base_url),
        ("Plex base URL", config.plex_base_url),
    ):
        parsed = urllib.parse.urlsplit(value)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname:
            raise ValueError(f"{label} must be an absolute HTTP(S) URL")
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError(f"{label} must not contain credentials, query parameters, or a fragment")
    for label, value in (
        ("NZBDav API key", config.nzbdav_api_key),
        ("ARR API key", config.arr_api_key),
        ("Plex token", config.plex_token),
        ("ARR category", config.category),
        ("Plex section id", config.plex_section_id),
    ):
        if not value or not value.strip():
            raise ValueError(f"{label} is required")
    if not config.plex_final_files:
        raise ValueError("at least one exact Plex Part.file is required")
    if len(set(config.plex_final_files)) != len(config.plex_final_files):
        raise ValueError("exact Plex Part.file values must be unique")
    if any(not path or not path.strip() for path in config.plex_final_files):
        raise ValueError("exact Plex Part.file values must be non-empty")
    if any(not is_absolute_media_path(path) for path in config.plex_final_files):
        raise ValueError("exact Plex Part.file values must be absolute Plex-visible paths")
    for path in config.plex_final_files:
        plex_parent_path(path)
    if not config.basic_fields:
        raise ValueError("at least one basic Plex metadata field is required")
    if any(not field.strip() for field in (*config.basic_fields, *config.rich_fields)):
        raise ValueError("Plex metadata fields must be non-empty dot paths")
    if config.require_rich_metadata and not config.rich_fields:
        raise ValueError("rich metadata fields are required when rich metadata is mandatory")
    if config.poll_interval_seconds <= 0:
        raise ValueError("poll interval must be positive")
    if config.timeout_seconds <= 0 or config.http_timeout_seconds <= 0:
        raise ValueError("timeouts must be positive")
    if config.rich_timeout_seconds < 0:
        raise ValueError("rich metadata timeout must be non-negative")
    if config.plex_page_size <= 0:
        raise ValueError("Plex page size must be positive")


def inspect_nzb(path: Path) -> dict[str, Any]:
    resolved = path.expanduser().resolve()
    if not resolved.is_file():
        raise ValueError("NZB path must be a regular file")
    digest = hashlib.sha256()
    with resolved.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)

    file_count = 0
    segment_count = 0
    root_seen = False
    for event, element in ET.iterparse(resolved, events=("start", "end")):
        local_name = element.tag.rsplit("}", 1)[-1]
        if event == "start" and not root_seen:
            if local_name.lower() != "nzb":
                raise ValueError("XML root is not nzb")
            root_seen = True
        if event == "end" and local_name.lower() == "file":
            file_count += 1
            element.clear()
        elif event == "end" and local_name.lower() == "segment":
            segment_count += 1
    if not root_seen or file_count == 0 or segment_count == 0:
        raise ValueError("NZB must contain at least one file and segment")
    stat = resolved.stat()
    return {
        "name": resolved.name,
        "size_bytes": stat.st_size,
        "sha256": digest.hexdigest(),
        "file_count": file_count,
        "segment_count": segment_count,
    }


def check(
    name: str,
    passed: bool,
    code: str,
    message: str,
    *,
    severity: str = "error",
) -> dict[str, Any]:
    return {"name": name, "passed": passed, "code": code, "message": message, "severity": severity}


def build_url(base_url: str, path: str, query: Mapping[str, str] | None = None) -> str:
    url = urllib.parse.urljoin(base_url.rstrip("/") + "/", path.lstrip("/"))
    if query:
        url = f"{url}?{urllib.parse.urlencode(query)}"
    return url


def build_multipart_nzb(path: Path) -> tuple[bytes, str]:
    boundary = f"nzbdav-e2e-{uuid.uuid4().hex}"
    file_name = multipart_filename(path.name)
    prefix = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="nzbFile"; filename="{file_name}"\r\n'
        "Content-Type: application/x-nzb\r\n\r\n"
    ).encode("utf-8")
    suffix = f"\r\n--{boundary}--\r\n".encode("ascii")
    return prefix + path.read_bytes() + suffix, f"multipart/form-data; boundary={boundary}"


def multipart_filename(value: str) -> str:
    sanitized = "".join(character for character in value if character not in {'"', "\r", "\n"})
    return sanitized or "upload.nzb"


def find_download_record(records: Any, expected_id: str, field: str) -> dict[str, Any] | None:
    if not isinstance(records, list):
        return None
    return next(
        (
            record
            for record in records
            if isinstance(record, dict) and ids_equal(record.get(field), expected_id)
        ),
        None,
    )


def ids_equal(left: Any, right: Any) -> bool:
    return isinstance(left, str) and isinstance(right, str) and left.casefold() == right.casefold()


def extract_imported_paths(records: Iterable[dict[str, Any]]) -> list[str]:
    paths: set[str] = set()
    for record in records:
        data = record.get("data")
        if not isinstance(data, dict):
            continue
        for key in ("importedPath", "destinationPath", "path"):
            value = data.get(key)
            if isinstance(value, str) and value.strip():
                paths.add(value)
    return sorted(paths)


def plex_parent_path(path: str) -> str:
    if "/" in path:
        return str(PurePosixPath(path).parent)
    if "\\" in path:
        return path.rsplit("\\", 1)[0]
    raise ValueError("Plex Part.file must include a parent directory")


def is_absolute_media_path(path: str) -> bool:
    return (
        path.startswith("/")
        or path.startswith("\\\\")
        or (
            len(path) >= 3
            and path[0].isalpha()
            and path[1] == ":"
            and path[2] in {"/", "\\"}
        )
    )


def parse_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def stage_durations(stages: Mapping[str, Mapping[str, Any]]) -> dict[str, float]:
    start = stages.get("nzbdav_addfile_request_started", {}).get("monotonic_seconds")
    if not isinstance(start, (int, float)):
        return {}
    names = (
        "nzbdav_addfile_accepted",
        "nzbdav_queue_seen",
        "nzbdav_history_completed",
        "arr_command_accepted",
        "arr_queue_seen",
        "arr_command_completed",
        "arr_history_imported",
        "plex_scan_accepted",
        "plex_item_visible",
        "plex_basic_metadata_ready",
        "plex_rich_metadata_ready",
    )
    durations: dict[str, float] = {}
    accepted = stages.get("nzbdav_addfile_accepted", {}).get("monotonic_seconds")
    for name in names:
        timestamp = stages.get(name, {}).get("monotonic_seconds")
        if isinstance(timestamp, (int, float)):
            request_start_duration = round((timestamp - start) * 1000, 3)
            durations[f"submission_to_{name}_ms"] = request_start_duration
            durations[f"sab_request_start_to_{name}_ms"] = request_start_duration
            if isinstance(accepted, (int, float)) and timestamp >= accepted:
                durations[f"sab_accepted_to_{name}_ms"] = round((timestamp - accepted) * 1000, 3)
    return durations


def percentile(values: list[float], percentile_value: float) -> float | None:
    if not values:
        return None
    if not 0 <= percentile_value <= 100:
        raise ValueError("percentile must be between 0 and 100")

    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    rank = percentile_value / 100 * (len(ordered) - 1)
    lower = math.floor(rank)
    upper = math.ceil(rank)
    if lower == upper:
        return ordered[lower]
    weight = rank - lower
    return ordered[lower] * (1 - weight) + ordered[upper] * weight


def aggregate_documents(documents: Iterable[dict[str, Any]]) -> dict[str, Any]:
    discovered = list(documents)
    included: list[dict[str, Any]] = []
    exclusion_reasons: dict[str, int] = {}
    for document in discovered:
        reason = aggregate_exclusion_reason(document)
        if reason is None:
            included.append(document)
            continue
        exclusion_reasons[reason] = exclusion_reasons.get(reason, 0) + 1
    metric_names = sorted({
        name
        for document in included
        for name, value in document.get("durations_ms", {}).items()
        if isinstance(value, (int, float)) and not isinstance(value, bool)
    })
    metrics: dict[str, Any] = {}
    for name in metric_names:
        values = [
            float(document["durations_ms"][name])
            for document in included
            if isinstance(document.get("durations_ms", {}).get(name), (int, float))
            and not isinstance(document["durations_ms"][name], bool)
        ]
        metrics[name] = {
            "count": len(values),
            "p50": round_value(percentile(values, 50)),
            "p95": round_value(percentile(values, 95)),
            "p99": round_value(percentile(values, 99)),
            "min": round_value(min(values) if values else None),
            "max": round_value(max(values) if values else None),
        }
    return {
        "schema_version": AGGREGATE_SCHEMA_VERSION,
        "kind": "nzbdav-grab-to-plex-aggregate",
        "runs_discovered": len(discovered),
        "runs_included": len(included),
        "runs_excluded": len(discovered) - len(included),
        "exclusion_reasons": dict(sorted(exclusion_reasons.items())),
        "metrics": metrics,
    }


def aggregate_exclusion_reason(document: dict[str, Any]) -> str | None:
    if not isinstance(document, dict):
        return "document_invalid"
    schema_version = document.get("schema_version")
    if type(schema_version) is not int or schema_version != RUN_SCHEMA_VERSION:
        return "schema_version_mismatch"
    if document.get("kind") != RUN_KIND:
        return "kind_mismatch"
    if document.get("status") != "completed":
        return "not_completed"
    measurement = document.get("measurement")
    if not isinstance(measurement, dict) or measurement.get("valid") is not True:
        return "measurement_invalid"
    if measurement.get("isolated") is not True:
        return "measurement_not_isolated"
    if measurement.get("production_representative") is not True:
        return "not_production_representative"
    if document.get("dry_run") is not False:
        return "dry_run_not_false"

    run = document.get("run")
    if not isinstance(run, dict):
        return "run_provenance_invalid"
    nzo_id = run.get("nzo_id")
    if not isinstance(nzo_id, str) or not nzo_id.strip():
        return "run_provenance_invalid"

    inputs = document.get("inputs")
    if not isinstance(inputs, dict):
        return "pipeline_evidence_invalid"
    if inputs.get("arr_refresh_mode") != "observe" or inputs.get("plex_scan_mode") != "observe":
        return "provenance_mode_mismatch"
    exact_part_files = inputs.get("plex_final_files")
    if (
        not isinstance(exact_part_files, list)
        or not exact_part_files
        or any(not isinstance(path, str) or not path.strip() for path in exact_part_files)
        or len(set(exact_part_files)) != len(exact_part_files)
    ):
        return "pipeline_evidence_invalid"

    observations = document.get("observations")
    if not isinstance(observations, dict):
        return "pipeline_evidence_invalid"
    arr_observation = observations.get("arr")
    plex_observation = observations.get("plex")
    if not isinstance(arr_observation, dict) or not isinstance(plex_observation, dict):
        return "pipeline_evidence_invalid"
    if (
        arr_observation.get("refresh_mode") != "observe"
        or plex_observation.get("scan_mode") != "observe"
    ):
        return "provenance_mode_mismatch"

    durations = document.get("durations_ms")
    if not isinstance(durations, dict) or not durations:
        return "durations_invalid"
    for metric_name in (PRIMARY_REQUEST_START_METRIC, PRIMARY_SAB_ACCEPTED_METRIC):
        if metric_name not in durations:
            return "primary_metric_missing"
        if not is_finite_nonnegative_real(durations[metric_name]):
            return "primary_metric_invalid"
    if any(not is_finite_nonnegative_real(value) for value in durations.values()):
        return "duration_value_invalid"

    stages = document.get("stages")
    if not isinstance(stages, dict) or not stages:
        return "stage_timing_missing"
    required_stage_names = (
        "benchmark_started",
        "nzbdav_addfile_request_started",
        "nzbdav_addfile_accepted",
        "nzbdav_history_completed",
        "arr_history_imported",
        "plex_item_visible",
        "plex_basic_metadata_ready",
        "benchmark_completed",
    )
    for stage_name in required_stage_names:
        stage = stages.get(stage_name)
        if not isinstance(stage, dict) or "monotonic_seconds" not in stage:
            return "stage_timing_missing"
    for stage in stages.values():
        if not isinstance(stage, dict):
            return "stage_timing_invalid"
        timestamp = stage.get("monotonic_seconds")
        if not is_finite_nonnegative_real(timestamp):
            return "stage_timing_invalid"

    request_start = stages["nzbdav_addfile_request_started"]["monotonic_seconds"]
    sab_accepted = stages["nzbdav_addfile_accepted"]["monotonic_seconds"]
    metadata_ready = stages["plex_basic_metadata_ready"]["monotonic_seconds"]
    if not request_start <= sab_accepted <= metadata_ready:
        return "stage_timing_misordered"
    ordered_stage_names = (
        "benchmark_started",
        "nzbdav_addfile_request_started",
        "nzbdav_addfile_accepted",
        "nzbdav_queue_seen",
        "nzbdav_history_completed",
        "arr_command_accepted",
        "arr_queue_seen",
        "arr_command_completed",
        "arr_history_imported",
        "plex_scan_accepted",
        "plex_item_visible",
        "plex_basic_metadata_ready",
        "plex_rich_metadata_ready",
        "benchmark_completed",
    )
    ordered_timestamps = [
        stages[name]["monotonic_seconds"]
        for name in ordered_stage_names
        if name in stages
    ]
    if any(left > right for left, right in zip(ordered_timestamps, ordered_timestamps[1:])):
        return "stage_timing_misordered"

    nzbdav_observation = observations.get("nzbdav")
    arr_event_types = arr_observation.get("history_event_types")
    imported_paths = arr_observation.get("imported_paths")
    plex_visibility = plex_observation.get("visibility")
    plex_basic_metadata = plex_observation.get("basic_metadata")
    if (
        not isinstance(nzbdav_observation, dict)
        or str(nzbdav_observation.get("history_status") or "").lower() != "completed"
        or arr_observation.get("history_import_observed") is not True
        or not isinstance(arr_event_types, list)
        or not any(
            isinstance(event_type, str)
            and event_type.lower() in IMPORTED_EVENT_TYPES
            for event_type in arr_event_types
        )
        or not isinstance(imported_paths, list)
        or not imported_paths
        or any(not isinstance(path, str) or not path.strip() for path in imported_paths)
        or not isinstance(plex_visibility, dict)
        or plex_visibility.get("visible") is not True
        or plex_visibility.get("exact_part_files") != exact_part_files
        or not isinstance(plex_basic_metadata, dict)
        or plex_basic_metadata.get("ready") is not True
    ):
        return "pipeline_evidence_invalid"

    expected_request_start_ms = round((metadata_ready - request_start) * 1000, 3)
    expected_sab_accepted_ms = round((metadata_ready - sab_accepted) * 1000, 3)
    if not math.isclose(
        float(durations[PRIMARY_REQUEST_START_METRIC]),
        expected_request_start_ms,
        rel_tol=0,
        abs_tol=0.0015,
    ) or not math.isclose(
        float(durations[PRIMARY_SAB_ACCEPTED_METRIC]),
        expected_sab_accepted_ms,
        rel_tol=0,
        abs_tol=0.0015,
    ):
        return "primary_metric_mismatch"
    return None


def is_finite_nonnegative_real(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value >= 0
    )


def round_value(value: float | None) -> float | None:
    return None if value is None else round(value, 3)


def resolve_secret(
    *,
    direct: str | None,
    file_arg: Path | None,
    env_name: str,
    environ: Mapping[str, str] | None = None,
) -> str | None:
    environment = os.environ if environ is None else environ
    candidates = [
        ("command line", direct),
        ("command line file", str(file_arg) if file_arg is not None else None),
        (env_name, environment.get(env_name)),
        (f"{env_name}_FILE", environment.get(f"{env_name}_FILE")),
    ]
    configured = [(source, value) for source, value in candidates if value not in (None, "")]
    if len(configured) > 1:
        names = ", ".join(source for source, _ in configured)
        raise ValueError(f"multiple secret sources configured for {env_name}: {names}")
    if not configured:
        return None

    source, value = configured[0]
    assert value is not None
    if source in ("command line file", f"{env_name}_FILE"):
        secret = Path(value).read_text(encoding="utf-8").strip()
    else:
        secret = value.strip()
    if not secret:
        raise ValueError(f"secret source for {env_name} is empty")
    return secret


def redact_url(value: str) -> str:
    parsed = urllib.parse.urlsplit(value)
    host = parsed.hostname or ""
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    if parsed.port is not None:
        host = f"{host}:{parsed.port}"
    return urllib.parse.urlunsplit((parsed.scheme, host, parsed.path, "", ""))


def extract_exact_part_matches(
    document: dict[str, Any], expected_files: Iterable[str]
) -> dict[str, dict[str, Any]]:
    expected = set(expected_files)
    matches: dict[str, dict[str, Any]] = {}
    container = document.get("MediaContainer")
    if not isinstance(container, dict):
        return matches
    metadata = container.get("Metadata") or []
    if not isinstance(metadata, list):
        return matches
    for item in metadata:
        if not isinstance(item, dict):
            continue
        for part_file in iter_part_files(item):
            if part_file in expected:
                matches[part_file] = item
    return matches


def iter_part_files(item: dict[str, Any]) -> Iterable[str]:
    media = item.get("Media") or []
    if not isinstance(media, list):
        return
    for media_item in media:
        if not isinstance(media_item, dict):
            continue
        parts = media_item.get("Part") or []
        if not isinstance(parts, list):
            continue
        for part in parts:
            if isinstance(part, dict) and isinstance(part.get("file"), str):
                yield part["file"]


def metadata_fields_ready(item: dict[str, Any], fields: Iterable[str]) -> tuple[bool, list[str]]:
    missing = [field for field in fields if not any(value_ready(value) for value in field_values(item, field))]
    return not missing, missing


def field_values(value: Any, field: str) -> Iterable[Any]:
    parts = [part for part in field.split(".") if part]

    def visit(current: Any, index: int) -> Iterable[Any]:
        if index == len(parts):
            yield current
            return
        if isinstance(current, list):
            for member in current:
                yield from visit(member, index)
            return
        if isinstance(current, dict) and parts[index] in current:
            yield from visit(current[parts[index]], index + 1)

    yield from visit(value, 0)


def value_ready(value: Any) -> bool:
    if value is None:
        return False
    if isinstance(value, str):
        return bool(value.strip())
    if isinstance(value, (list, dict)):
        return bool(value)
    return True


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=__doc__,
        epilog=(
            "Secrets may be supplied directly, through *_FILE options, or through the documented "
            "NZBDAV_E2E_* environment variables. Configure exactly one source per secret."
        ),
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    run_parser = subparsers.add_parser("run", help="submit one NZB and record the end-to-end pipeline")
    add_pipeline_arguments(run_parser)
    run_parser.add_argument(
        "--dry-run",
        action="store_true",
        help="perform only local and read-only remote validation; do not submit, command ARR, or scan Plex",
    )

    validate_parser = subparsers.add_parser("validate", help="read-only validation for a future run")
    add_pipeline_arguments(validate_parser)

    aggregate_parser = subparsers.add_parser(
        "aggregate",
        help="aggregate completed, valid run artifacts with p50/p95/p99 metrics",
    )
    aggregate_parser.add_argument("inputs", nargs="+", type=Path, help="artifact JSON files or directories")
    add_output_arguments(aggregate_parser)
    return parser


def add_pipeline_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--nzbdav-url", help="NZBDav base URL, including URL base if configured")
    parser.add_argument(
        "--nzbdav-api-key",
        help="NZBDav API key (prefer --nzbdav-api-key-file or NZBDAV_E2E_NZBDAV_API_KEY_FILE)",
    )
    parser.add_argument("--nzbdav-api-key-file", type=Path)
    parser.add_argument("--nzb", type=Path, help="one local NZB file to submit")
    parser.add_argument("--category", help="ARR/NZBDav category")
    parser.add_argument("--arr-kind", choices=("sonarr", "radarr"))
    parser.add_argument("--arr-url", help="Sonarr or Radarr base URL, including URL base if configured")
    parser.add_argument(
        "--arr-api-key",
        help="ARR API key (prefer --arr-api-key-file or NZBDAV_E2E_ARR_API_KEY_FILE)",
    )
    parser.add_argument("--arr-api-key-file", type=Path)
    parser.add_argument("--plex-url", help="Plex Media Server base URL")
    parser.add_argument(
        "--plex-token",
        help="Plex token (prefer --plex-token-file or NZBDAV_E2E_PLEX_TOKEN_FILE)",
    )
    parser.add_argument("--plex-token-file", type=Path)
    parser.add_argument("--plex-section-id", help="Plex library section id")
    parser.add_argument(
        "--plex-final-file",
        action="append",
        default=[],
        help="exact expected Plex Part.file; repeat for a multi-file import",
    )
    parser.add_argument(
        "--plex-basic-field",
        action="append",
        default=[],
        help="required metadata dot-path; defaults depend on Sonarr versus Radarr",
    )
    parser.add_argument(
        "--plex-rich-field",
        action="append",
        default=[],
        help="optional rich metadata dot-path to observe; repeat as needed",
    )
    parser.add_argument("--require-rich-metadata", action="store_true")
    parser.add_argument(
        "--force-arr-refresh",
        action="store_true",
        help=(
            "diagnostic only: POST RefreshMonitoredDownloads and mark the artifact "
            "non-production/non-representative"
        ),
    )
    parser.add_argument(
        "--force-plex-scan",
        action="store_true",
        help=(
            "diagnostic only: request a targeted Plex scan and mark the artifact "
            "non-production/non-representative"
        ),
    )
    parser.add_argument(
        "--allow-existing-plex-item",
        action="store_true",
        help="allow a pre-existing exact Part.file but mark the measurement invalid and non-isolated",
    )
    parser.add_argument("--poll-interval-seconds", type=float)
    parser.add_argument("--timeout-seconds", type=float)
    parser.add_argument("--rich-timeout-seconds", type=float)
    parser.add_argument("--http-timeout-seconds", type=float)
    parser.add_argument("--plex-page-size", type=int)
    add_output_arguments(parser)


def add_output_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--output", type=Path, help="exact output path; must not already exist")
    parser.add_argument("--output-dir", type=Path, help="directory for a generated output filename")


def config_from_args(args: argparse.Namespace, *, environ: Mapping[str, str] | None = None) -> RunConfig:
    environment = os.environ if environ is None else environ
    arr_kind = args.arr_kind or environment.get("NZBDAV_E2E_ARR_KIND")
    if arr_kind not in {"sonarr", "radarr"}:
        raise ValueError("--arr-kind or NZBDAV_E2E_ARR_KIND must be sonarr or radarr")

    nzb_value = args.nzb or environment.get("NZBDAV_E2E_NZB_PATH")
    if not nzb_value:
        raise ValueError("--nzb or NZBDAV_E2E_NZB_PATH is required")
    plex_files = tuple(args.plex_final_file or parse_env_list(environment.get("NZBDAV_E2E_PLEX_FINAL_FILES")))
    basic_fields = tuple(
        args.plex_basic_field
        or parse_env_fields(environment.get("NZBDAV_E2E_PLEX_BASIC_FIELDS"))
        or default_basic_fields(arr_kind)
    )
    rich_fields = tuple(
        args.plex_rich_field or parse_env_fields(environment.get("NZBDAV_E2E_PLEX_RICH_FIELDS"))
    )

    return RunConfig(
        nzbdav_base_url=args.nzbdav_url or environment.get("NZBDAV_E2E_NZBDAV_URL", ""),
        nzbdav_api_key=resolve_secret(
            direct=args.nzbdav_api_key,
            file_arg=args.nzbdav_api_key_file,
            env_name="NZBDAV_E2E_NZBDAV_API_KEY",
            environ=environment,
        ) or "",
        nzb_path=Path(nzb_value),
        category=args.category or environment.get("NZBDAV_E2E_CATEGORY", ""),
        arr_kind=arr_kind,
        arr_base_url=args.arr_url or environment.get("NZBDAV_E2E_ARR_URL", ""),
        arr_api_key=resolve_secret(
            direct=args.arr_api_key,
            file_arg=args.arr_api_key_file,
            env_name="NZBDAV_E2E_ARR_API_KEY",
            environ=environment,
        ) or "",
        plex_base_url=args.plex_url or environment.get("NZBDAV_E2E_PLEX_URL", ""),
        plex_token=resolve_secret(
            direct=args.plex_token,
            file_arg=args.plex_token_file,
            env_name="NZBDAV_E2E_PLEX_TOKEN",
            environ=environment,
        ) or "",
        plex_section_id=args.plex_section_id or environment.get("NZBDAV_E2E_PLEX_SECTION_ID", ""),
        plex_final_files=plex_files,
        basic_fields=basic_fields,
        rich_fields=rich_fields,
        require_rich_metadata=args.require_rich_metadata
        or env_bool(environment.get("NZBDAV_E2E_REQUIRE_RICH_METADATA")),
        force_arr_refresh=args.force_arr_refresh,
        force_plex_scan=args.force_plex_scan,
        poll_interval_seconds=arg_or_env_number(
            args.poll_interval_seconds, environment, "NZBDAV_E2E_POLL_INTERVAL_SECONDS", 0.25, float
        ),
        timeout_seconds=arg_or_env_number(
            args.timeout_seconds, environment, "NZBDAV_E2E_TIMEOUT_SECONDS", 120.0, float
        ),
        rich_timeout_seconds=arg_or_env_number(
            args.rich_timeout_seconds, environment, "NZBDAV_E2E_RICH_TIMEOUT_SECONDS", 0.0, float
        ),
        http_timeout_seconds=arg_or_env_number(
            args.http_timeout_seconds, environment, "NZBDAV_E2E_HTTP_TIMEOUT_SECONDS", 10.0, float
        ),
        plex_page_size=arg_or_env_number(
            args.plex_page_size, environment, "NZBDAV_E2E_PLEX_PAGE_SIZE", 200, int
        ),
        allow_existing_plex_item=args.allow_existing_plex_item
        or env_bool(environment.get("NZBDAV_E2E_ALLOW_EXISTING_PLEX_ITEM")),
    )


def parse_env_list(value: str | None) -> list[str]:
    if not value or not value.strip():
        return []
    stripped = value.strip()
    if stripped.startswith("["):
        parsed = json.loads(stripped)
        if not isinstance(parsed, list) or not all(isinstance(item, str) for item in parsed):
            raise ValueError("NZBDAV_E2E_PLEX_FINAL_FILES must be a JSON string array")
        return [item for item in parsed if item]
    return [item.strip() for item in stripped.splitlines() if item.strip()]


def parse_env_fields(value: str | None) -> list[str]:
    if not value:
        return []
    return [item.strip() for item in value.split(",") if item.strip()]


def env_bool(value: str | None) -> bool:
    return bool(value and value.strip().lower() in {"1", "true", "yes", "on"})


def arg_or_env_number(
    argument: Any,
    environ: Mapping[str, str],
    name: str,
    default: Any,
    converter: Any,
) -> Any:
    if argument is not None:
        return argument
    value = environ.get(name)
    if value is None:
        return default
    try:
        return converter(value)
    except (TypeError, ValueError) as error:
        raise ValueError(f"{name} has an invalid numeric value") from error


def collect_artifact_paths(inputs: Iterable[Path]) -> list[Path]:
    paths: set[Path] = set()
    for input_path in inputs:
        if input_path.is_dir():
            paths.update(path for path in input_path.rglob("*.json") if path.is_file())
        elif input_path.is_file():
            paths.add(input_path)
        else:
            raise ValueError(f"artifact input does not exist: {input_path}")
    return sorted(paths)


def load_artifact_documents(paths: Iterable[Path]) -> tuple[list[dict[str, Any]], int]:
    documents: list[dict[str, Any]] = []
    invalid = 0
    for path in paths:
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            invalid += 1
            continue
        if isinstance(document, dict):
            documents.append(document)
        else:
            invalid += 1
    return documents, invalid


def write_private_json(
    document: Mapping[str, Any],
    *,
    output: Path | None,
    output_dir: Path | None,
    prefix: str,
) -> Path:
    if output is None:
        directory = output_dir or DEFAULT_OUTPUT_DIR
        timestamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
        output = directory / f"{prefix}-{timestamp}.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    descriptor = os.open(output, flags, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as target:
            json.dump(document, target, indent=2, sort_keys=True)
            target.write("\n")
    except Exception:
        output.unlink(missing_ok=True)
        raise
    return output


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "aggregate":
            paths = collect_artifact_paths(args.inputs)
            documents, invalid = load_artifact_documents(paths)
            artifact = aggregate_documents(documents)
            artifact["artifact_files_discovered"] = len(paths)
            artifact["artifact_files_invalid"] = invalid
            output = write_private_json(
                artifact,
                output=args.output,
                output_dir=args.output_dir,
                prefix="grab-to-plex-aggregate",
            )
            print(output)
            return 0

        config = config_from_args(args)
        runner = BenchmarkRunner(config, transport=UrllibTransport())
        dry_run = args.command == "validate" or bool(getattr(args, "dry_run", False))
        artifact = runner.run(dry_run=dry_run)
        output = write_private_json(
            artifact,
            output=args.output,
            output_dir=args.output_dir,
            prefix="grab-to-plex-validation" if dry_run else "grab-to-plex-run",
        )
        print(output)
        if artifact["status"] in {"completed", "validated"}:
            return 0
        if artifact["status"] == "timed_out":
            return 3
        return 2
    except (ValueError, OSError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
