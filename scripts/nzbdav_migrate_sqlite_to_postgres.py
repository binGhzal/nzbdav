#!/usr/bin/env python3
"""Offline SQLite-to-PostgreSQL migration helper for NZBDav."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any


DEFAULT_IMAGE = "ghcr.io/binghzal/nzbdav:latest"
DEFAULT_OUTPUT_DIR = Path("artifacts/postgres-migration")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sqlite-config", type=Path, required=True, help="Stopped SQLite NZBDav /config directory")
    parser.add_argument("--postgres-config", type=Path, required=True, help="Target PostgreSQL NZBDav /config directory")
    parser.add_argument(
        "--postgres-connection-string",
        default=os.environ.get("NZBDAV_DATABASE_CONNECTION_STRING"),
        help="Target PostgreSQL connection string",
    )
    parser.add_argument("--image", default=DEFAULT_IMAGE, help="NZBDav image to use for export/import")
    parser.add_argument("--work-dir", type=Path, default=None, help="Directory for transfer JSON and manifest")
    parser.add_argument("--replace", action="store_true", help="Allow replacing a non-empty target DB/config")
    parser.add_argument("--dry-run", action="store_true", help="Validate inputs and write a manifest without Docker/import")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if not args.postgres_connection_string and not args.dry_run:
        raise SystemExit("NZBDAV_DATABASE_CONNECTION_STRING or --postgres-connection-string is required.")

    run_id = timestamp()
    work_dir = args.work_dir or DEFAULT_OUTPUT_DIR / run_id
    work_dir.mkdir(parents=True, exist_ok=True)
    snapshot_path = work_dir / "nzbdav-transfer.json"
    verify_path = work_dir / "nzbdav-postgres-verify.json"
    manifest_path = work_dir / "manifest.json"

    validate_paths(args.sqlite_config, args.postgres_config, replace=args.replace)
    docker_commands: list[list[str]] = []
    if not args.dry_run:
        export_cmd = docker_command(args.image, args.sqlite_config, work_dir, ["--db-export-json", "/transfer/nzbdav-transfer.json"])
        run(export_cmd)
        docker_commands.append(redact_command(export_cmd))

        import_args = ["--db-import-json", "/transfer/nzbdav-transfer.json"]
        if args.replace:
            import_args.append("--replace")
        import_cmd = docker_command(
            args.image,
            args.postgres_config,
            work_dir,
            import_args,
            postgres_connection_string=args.postgres_connection_string,
        )
        run(import_cmd)
        docker_commands.append(redact_command(import_cmd))

        verify_cmd = docker_command(
            args.image,
            args.postgres_config,
            work_dir,
            ["--db-export-json", "/transfer/nzbdav-postgres-verify.json"],
            postgres_connection_string=args.postgres_connection_string,
        )
        run(verify_cmd)
        docker_commands.append(redact_command(verify_cmd))
        validate_row_counts(snapshot_path, verify_path)

    blob_summary = copy_blobs(args.sqlite_config, args.postgres_config, replace=args.replace, dry_run=args.dry_run)
    manifest = build_manifest(
        run_id=run_id,
        image=args.image,
        sqlite_config=args.sqlite_config,
        postgres_config=args.postgres_config,
        snapshot_path=snapshot_path,
        verify_path=verify_path,
        blob_summary=blob_summary,
        docker_commands=docker_commands,
        dry_run=args.dry_run,
    )
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Wrote migration manifest: {manifest_path}")
    if args.dry_run:
        print("Dry run complete; no Docker commands were run and no blobs were copied.")
    else:
        print("SQLite-to-PostgreSQL migration completed.")
    return 0


def validate_paths(sqlite_config: Path, postgres_config: Path, *, replace: bool) -> None:
    if not sqlite_config.exists() or not sqlite_config.is_dir():
        raise SystemExit(f"SQLite config directory does not exist: {sqlite_config}")
    if not (sqlite_config / "db.sqlite").exists():
        raise SystemExit(f"SQLite database was not found: {sqlite_config / 'db.sqlite'}")
    postgres_config.mkdir(parents=True, exist_ok=True)
    target_blobs = postgres_config / "blobs"
    if target_blobs.exists() and any(target_blobs.iterdir()) and not replace:
        raise SystemExit("Target blobs directory is not empty. Re-run with --replace to overwrite copied blobs.")


def docker_command(
    image: str,
    config_dir: Path,
    work_dir: Path,
    app_args: list[str],
    *,
    postgres_connection_string: str | None = None,
    readonly_config: bool = False,
) -> list[str]:
    config_mount = f"{config_dir.resolve()}:/config" + (":ro" if readonly_config else "")
    command = [
        "docker",
        "run",
        "--rm",
        "-v",
        config_mount,
        "-v",
        f"{work_dir.resolve()}:/transfer",
    ]
    if postgres_connection_string:
        command += [
            "-e",
            "NZBDAV_DATABASE_PROVIDER=postgres",
            "-e",
            f"NZBDAV_DATABASE_CONNECTION_STRING={postgres_connection_string}",
        ]
    command.append(image)
    command.extend(app_args)
    return command


def run(command: list[str]) -> None:
    subprocess.run(command, check=True)


def validate_row_counts(source_snapshot: Path, postgres_snapshot: Path) -> None:
    source_rows = snapshot_total_rows(source_snapshot)
    postgres_rows = snapshot_total_rows(postgres_snapshot)
    if source_rows != postgres_rows:
        raise SystemExit(f"PostgreSQL verification row count mismatch: source={source_rows}, postgres={postgres_rows}")


def snapshot_total_rows(path: Path) -> int:
    document = json.loads(path.read_text(encoding="utf-8"))
    if "TotalRows" in document:
        return int(document["TotalRows"])
    row_keys = [
        key for key, value in document.items()
        if isinstance(value, list) and key not in {"AccessIssues"}
    ]
    return sum(len(document[key]) for key in row_keys)


def copy_blobs(sqlite_config: Path, postgres_config: Path, *, replace: bool, dry_run: bool) -> dict[str, Any]:
    source = sqlite_config / "blobs"
    target = postgres_config / "blobs"
    summary = scan_tree(source)
    summary["source"] = str(source)
    summary["target"] = str(target)
    summary["copied"] = False
    summary["cache_excluded"] = True
    if dry_run or not source.exists():
        return summary
    if target.exists():
        if replace:
            shutil.rmtree(target)
        elif any(target.iterdir()):
            raise SystemExit("Target blobs directory is not empty. Re-run with --replace to overwrite copied blobs.")
        else:
            shutil.rmtree(target)
    if source.exists():
        shutil.copytree(source, target)
        summary["copied"] = True
    return summary


def scan_tree(path: Path) -> dict[str, int]:
    if not path.exists():
        return {"files": 0, "bytes": 0}
    files = [item for item in path.rglob("*") if item.is_file()]
    return {"files": len(files), "bytes": sum(item.stat().st_size for item in files)}


def build_manifest(
    *,
    run_id: str,
    image: str,
    sqlite_config: Path,
    postgres_config: Path,
    snapshot_path: Path,
    verify_path: Path,
    blob_summary: dict[str, Any],
    docker_commands: list[list[str]],
    dry_run: bool,
) -> dict[str, Any]:
    return {
        "run_id": run_id,
        "generated_at": dt.datetime.now(dt.UTC).isoformat(),
        "image": image,
        "sqlite_config": str(sqlite_config),
        "postgres_config": str(postgres_config),
        "snapshot_path": str(snapshot_path),
        "postgres_verify_snapshot_path": str(verify_path),
        "blob_summary": blob_summary,
        "docker_commands": docker_commands,
        "dry_run": dry_run,
        "cache_copied": False,
    }


def redact_command(command: list[str]) -> list[str]:
    redacted: list[str] = []
    for item in command:
        if item.startswith("NZBDAV_DATABASE_CONNECTION_STRING="):
            redacted.append("NZBDAV_DATABASE_CONNECTION_STRING=***REDACTED***")
        else:
            redacted.append(item)
    return redacted


def timestamp() -> str:
    return dt.datetime.now(dt.UTC).strftime("%Y%m%dT%H%M%SZ")


if __name__ == "__main__":
    raise SystemExit(main())
