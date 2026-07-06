#!/usr/bin/env python3
"""Apply the rclone sidecar only when its effective config changed."""

from __future__ import annotations

import argparse
import hashlib
import json
import shlex
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_SERVICE = "nzbdav_rclone"
DEFAULT_STATE_FILE = ".nzbdav-rclone-state.json"
COMPOSE_CONFIG_HASH_LABEL = "com.docker.compose.config-hash"


@dataclass(frozen=True)
class LiveContainerState:
    container_id: str
    running: bool
    config_hash: str | None
    started_at: datetime | None


def file_digest(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {"path": str(path), "exists": False}
    if not path.is_file():
        return {"path": str(path), "exists": True, "type": "not-file"}

    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(chunk)
    return {
        "path": str(path),
        "exists": True,
        "type": "file",
        "sha256": hasher.hexdigest(),
        "size": path.stat().st_size,
    }


def compute_fingerprint(compose_config: str, watch_files: list[Path]) -> tuple[str, dict[str, Any]]:
    watch_file_digests = [file_digest(path) for path in watch_files]
    payload = {
        "compose_config": compose_config,
        "watch_files": watch_file_digests,
    }
    fingerprint_payload = {
        "compose_config": compose_config,
        "watch_files": sorted(
            (fingerprint_file_digest(digest) for digest in watch_file_digests),
            key=lambda item: json.dumps(item, sort_keys=True, separators=(",", ":")),
        ),
    }
    encoded = json.dumps(fingerprint_payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest(), payload


def fingerprint_file_digest(digest: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in digest.items() if key != "path"}


def load_state(path: Path) -> dict[str, Any] | None:
    if not path.exists():
        return None
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def save_state(path: Path, fingerprint: str, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    state = {
        "fingerprint": fingerprint,
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "payload": payload,
    }
    path.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def should_apply(previous_state: dict[str, Any] | None, current_fingerprint: str, force: bool) -> bool:
    if force:
        return True
    return previous_state is None or previous_state.get("fingerprint") != current_fingerprint


def compose_base_command(compose_command: str, compose_files: list[Path]) -> list[str]:
    command = shlex.split(compose_command)
    for compose_file in compose_files:
        command.extend(["-f", str(compose_file)])
    return command


def build_compose_config_command(compose_command: str, compose_files: list[Path], service: str) -> list[str]:
    return [*compose_base_command(compose_command, compose_files), "config", service]


def build_compose_hash_command(compose_command: str, compose_files: list[Path], service: str) -> list[str]:
    return [*compose_base_command(compose_command, compose_files), "config", "--hash", service]


def build_compose_ps_command(compose_command: str, compose_files: list[Path], service: str) -> list[str]:
    return [*compose_base_command(compose_command, compose_files), "ps", "-q", service]


def build_compose_up_command(compose_command: str, compose_files: list[Path], service: str) -> list[str]:
    return [*compose_base_command(compose_command, compose_files), "up", "-d", service]


def build_container_inspect_command(compose_command: str, container_id: str) -> list[str]:
    command = shlex.split(compose_command)
    try:
        compose_index = command.index("compose")
        docker_command = command[:compose_index]
    except ValueError:
        docker_command = command[:1]

    if not docker_command:
        docker_command = ["docker"]
    return [*docker_command, "inspect", container_id]


def run_command(command: list[str], cwd: Path, capture: bool) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=cwd,
        check=True,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.PIPE if capture else None,
    )


def resolve_project_path(project_dir: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else project_dir / path


def load_live_container_state(
    compose_command: str,
    compose_files: list[Path],
    service: str,
    project_dir: Path,
) -> tuple[str, LiveContainerState] | None:
    try:
        hash_result = run_command(
            build_compose_hash_command(compose_command, compose_files, service),
            project_dir,
            capture=True)
        compose_hash = first_output_line(hash_result.stdout)
        if not compose_hash:
            return None

        ps_result = run_command(
            build_compose_ps_command(compose_command, compose_files, service),
            project_dir,
            capture=True)
        container_id = first_output_line(ps_result.stdout)
        if not container_id:
            return None

        inspect_result = run_command(
            build_container_inspect_command(compose_command, container_id),
            project_dir,
            capture=True)
        inspect_payload = json.loads(inspect_result.stdout)
        if not isinstance(inspect_payload, list) or not inspect_payload:
            return None

        container = inspect_payload[0]
        labels = container.get("Config", {}).get("Labels", {}) or {}
        state = container.get("State", {}) or {}
        return compose_hash, LiveContainerState(
            container_id=container_id,
            running=bool(state.get("Running")),
            config_hash=labels.get(COMPOSE_CONFIG_HASH_LABEL),
            started_at=parse_docker_timestamp(state.get("StartedAt")))
    except (subprocess.CalledProcessError, json.JSONDecodeError, TypeError, ValueError):
        return None


def first_output_line(value: str | None) -> str | None:
    if value is None:
        return None
    for line in value.splitlines():
        line = line.strip()
        if line:
            return line
    return None


def parse_docker_timestamp(value: str | None) -> datetime | None:
    if not value or value.startswith("0001-01-01"):
        return None

    normalized = value.strip()
    if normalized.endswith("Z"):
        normalized = normalized[:-1] + "+00:00"

    if "." in normalized:
        prefix, suffix = normalized.split(".", 1)
        timezone_index = len(suffix)
        for marker in ("+", "-"):
            marker_index = suffix.find(marker)
            if marker_index >= 0:
                timezone_index = min(timezone_index, marker_index)
        fraction = suffix[:timezone_index][:6].ljust(6, "0")
        normalized = f"{prefix}.{fraction}{suffix[timezone_index:]}"

    started_at = datetime.fromisoformat(normalized)
    if started_at.tzinfo is None:
        started_at = started_at.replace(tzinfo=timezone.utc)
    return started_at.astimezone(timezone.utc)


def can_adopt_live_container(
    compose_hash: str,
    live_container: LiveContainerState,
    watch_files: list[Path],
) -> bool:
    if not live_container.running:
        return False
    if not live_container.config_hash or live_container.config_hash != compose_hash:
        return False
    if live_container.started_at is None:
        return False

    return watched_files_are_not_newer_than(watch_files, live_container.started_at)


def watched_files_are_not_newer_than(watch_files: list[Path], started_at: datetime) -> bool:
    for path in watch_files:
        if not path.exists() or not path.is_file():
            return False
        modified_at = datetime.fromtimestamp(path.stat().st_mtime, timezone.utc)
        if modified_at > started_at:
            return False

    return True


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Safely update the NZBDav rclone sidecar. The service is only applied "
            "when docker compose config or watched files changed."
        )
    )
    parser.add_argument("--project-dir", type=Path, default=Path.cwd())
    parser.add_argument("--service", default=DEFAULT_SERVICE)
    parser.add_argument("--state-file", default=DEFAULT_STATE_FILE)
    parser.add_argument("--compose-command", default="docker compose")
    parser.add_argument("--compose-file", action="append", default=[])
    parser.add_argument("--watch-file", action="append", default=[])
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--force", action="store_true", help="Run docker compose up even if the fingerprint is unchanged.")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    project_dir = args.project_dir.resolve()
    state_file = resolve_project_path(project_dir, args.state_file)
    compose_files = [resolve_project_path(project_dir, value) for value in args.compose_file]
    watch_files = [resolve_project_path(project_dir, value) for value in args.watch_file]

    config_command = build_compose_config_command(args.compose_command, compose_files, args.service)
    try:
        config_result = run_command(config_command, project_dir, capture=True)
    except subprocess.CalledProcessError as error:
        sys.stderr.write(error.stderr or str(error))
        return error.returncode

    fingerprint, payload = compute_fingerprint(config_result.stdout, watch_files)
    previous_state = load_state(state_file)
    if not should_apply(previous_state, fingerprint, args.force):
        print(f"{args.service}: unchanged; skipping docker compose up")
        return 0

    if not args.force:
        live_state = load_live_container_state(
            args.compose_command,
            compose_files,
            args.service,
            project_dir)
        if live_state is not None:
            compose_hash, live_container = live_state
            if can_adopt_live_container(compose_hash, live_container, watch_files):
                save_state(state_file, fingerprint, payload)
                print(
                    f"{args.service}: running container already matches current config; "
                    "recorded fingerprint without docker compose up")
                return 0

    up_command = build_compose_up_command(args.compose_command, compose_files, args.service)
    printable = shlex.join(up_command)
    if args.dry_run:
        print(f"{args.service}: would run {printable}")
        return 0

    try:
        run_command(up_command, project_dir, capture=False)
    except subprocess.CalledProcessError as error:
        return error.returncode

    save_state(state_file, fingerprint, payload)
    print(f"{args.service}: updated and recorded fingerprint {fingerprint}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
