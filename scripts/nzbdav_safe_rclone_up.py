#!/usr/bin/env python3
"""Apply the rclone sidecar only when its effective config changed."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import secrets
import shlex
import stat
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_SERVICE = "nzbdav_rclone"
DEFAULT_STATE_FILE = ".nzbdav-rclone-state.json"
COMPOSE_CONFIG_HASH_LABEL = "com.docker.compose.config-hash"
STATE_FORMAT_VERSION = 1
MAX_STATE_BYTES = 64 * 1024
STATE_FILE_MODE = 0o600


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
    try:
        path_stat = path.lstat()
    except FileNotFoundError:
        return None

    if not stat.S_ISREG(path_stat.st_mode):
        raise ValueError(f"State path must be a regular file: {path}")
    if hasattr(os, "geteuid") and path_stat.st_uid != os.geteuid():
        raise ValueError(f"State file must be owned by the current user: {path}")
    if path_stat.st_size > MAX_STATE_BYTES:
        raise ValueError(f"State file exceeds {MAX_STATE_BYTES} bytes: {path}")

    flags = os.O_RDONLY
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = os.open(path, flags)
    try:
        opened_stat = os.fstat(descriptor)
        if not stat.S_ISREG(opened_stat.st_mode):
            raise ValueError(f"State path must be a regular file: {path}")
        if (opened_stat.st_dev, opened_stat.st_ino) != (path_stat.st_dev, path_stat.st_ino):
            raise ValueError(f"State file changed while it was being opened: {path}")
        encoded = read_bounded(descriptor, MAX_STATE_BYTES)
    finally:
        os.close(descriptor)

    state = json.loads(encoded.decode("utf-8"), object_pairs_hook=reject_duplicate_json_keys)
    if not isinstance(state, dict):
        raise ValueError(f"State file must contain a JSON object: {path}")
    fingerprint = state.get("fingerprint")
    if not is_fingerprint(fingerprint):
        raise ValueError(f"State file contains an invalid fingerprint: {path}")
    return state


def save_state(path: Path, fingerprint: str) -> None:
    if not is_fingerprint(fingerprint):
        raise ValueError("State fingerprint must be 64 lowercase hexadecimal characters")

    path.parent.mkdir(parents=True, exist_ok=True)
    state = {
        "format_version": STATE_FORMAT_VERSION,
        "fingerprint": fingerprint,
        "updated_at": datetime.now(timezone.utc).isoformat(),
    }
    encoded = (json.dumps(state, indent=2, sort_keys=True) + "\n").encode("utf-8")

    directory_flags = os.O_RDONLY
    if hasattr(os, "O_DIRECTORY"):
        directory_flags |= os.O_DIRECTORY
    if hasattr(os, "O_NOFOLLOW"):
        directory_flags |= os.O_NOFOLLOW
    directory_descriptor = os.open(path.parent, directory_flags)
    temporary_name = f".{path.name}.{secrets.token_hex(16)}.tmp"
    temporary_descriptor: int | None = None
    try:
        existing = try_stat_entry(path.name, directory_descriptor)
        if existing is not None and not stat.S_ISREG(existing.st_mode):
            raise ValueError(f"State path must be a regular file: {path}")

        temporary_flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        if hasattr(os, "O_NOFOLLOW"):
            temporary_flags |= os.O_NOFOLLOW
        temporary_descriptor = os.open(
            temporary_name,
            temporary_flags,
            STATE_FILE_MODE,
            dir_fd=directory_descriptor)
        write_all(temporary_descriptor, encoded)
        os.fsync(temporary_descriptor)
        os.close(temporary_descriptor)
        temporary_descriptor = None
        os.replace(
            temporary_name,
            path.name,
            src_dir_fd=directory_descriptor,
            dst_dir_fd=directory_descriptor)
        os.fsync(directory_descriptor)
    finally:
        if temporary_descriptor is not None:
            os.close(temporary_descriptor)
        try:
            os.unlink(temporary_name, dir_fd=directory_descriptor)
        except FileNotFoundError:
            pass
        os.close(directory_descriptor)


def try_stat_entry(name: str, directory_descriptor: int) -> os.stat_result | None:
    try:
        return os.stat(name, dir_fd=directory_descriptor, follow_symlinks=False)
    except FileNotFoundError:
        return None


def write_all(descriptor: int, data: bytes) -> None:
    view = memoryview(data)
    while view:
        written = os.write(descriptor, view)
        if written <= 0:
            raise OSError("State-file write made no progress")
        view = view[written:]


def read_bounded(descriptor: int, limit: int) -> bytes:
    chunks: list[bytes] = []
    total = 0
    while True:
        chunk = os.read(descriptor, min(8192, limit + 1 - total))
        if not chunk:
            return b"".join(chunks)
        chunks.append(chunk)
        total += len(chunk)
        if total > limit:
            raise ValueError(f"State file exceeds {limit} bytes")


def reject_duplicate_json_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"State file contains duplicate JSON key: {key}")
        value[key] = item
    return value


def is_fingerprint(value: object) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 64
        and all(character in "0123456789abcdef" for character in value)
    )


def state_needs_rewrite(path: Path, state: dict[str, Any]) -> bool:
    return (
        state.get("format_version") != STATE_FORMAT_VERSION
        or set(state) != {"format_version", "fingerprint", "updated_at"}
        or stat.S_IMODE(path.lstat().st_mode) != STATE_FILE_MODE
    )


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

    fingerprint, _ = compute_fingerprint(config_result.stdout, watch_files)
    try:
        previous_state = load_state(state_file)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as error:
        sys.stderr.write(f"Refusing unsafe rclone state file: {error}\n")
        return 78
    if not should_apply(previous_state, fingerprint, args.force):
        if previous_state is not None and state_needs_rewrite(state_file, previous_state):
            try:
                save_state(state_file, fingerprint)
            except (OSError, ValueError) as error:
                sys.stderr.write(f"Could not rewrite private rclone state: {error}\n")
                return 74
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
                try:
                    save_state(state_file, fingerprint)
                except (OSError, ValueError) as error:
                    sys.stderr.write(f"Could not write private rclone state: {error}\n")
                    return 74
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

    try:
        save_state(state_file, fingerprint)
    except (OSError, ValueError) as error:
        sys.stderr.write(f"Could not write private rclone state: {error}\n")
        return 74
    print(f"{args.service}: updated and recorded fingerprint {fingerprint}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
