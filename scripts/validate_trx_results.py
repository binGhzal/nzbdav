#!/usr/bin/env python3
"""Fail closed unless every selected TRX test executed and passed."""

from __future__ import annotations

import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET
from typing import NamedTuple


class TrxCounters(NamedTuple):
    total: int
    executed: int
    passed: int
    not_executed: int
    failed_like: int


def validate_trx(path: pathlib.Path) -> TrxCounters:
    if not path.is_file():
        raise ValueError(f"missing TRX result: {path}")

    counters = ET.parse(path).getroot().find(".//{*}Counters")
    if counters is None:
        raise ValueError(f"missing TRX counters: {path}")

    try:
        values = {key: int(value) for key, value in counters.attrib.items()}
    except ValueError as error:
        raise ValueError(f"non-integer TRX counter: {path}") from error

    total = values.get("total", 0)
    executed = values.get("executed", 0)
    passed = values.get("passed", 0)
    not_executed = values.get("notExecuted", 0)
    failed_like = sum(
        values.get(key, 0)
        for key in (
            "failed",
            "error",
            "timeout",
            "aborted",
            "inconclusive",
            "passedButRunAborted",
            "notRunnable",
            "disconnected",
            "warning",
        )
    )
    summary = (
        f"total={total} executed={executed} passed={passed} "
        f"notExecuted={not_executed} failedLike={failed_like}"
    )

    if total <= 0:
        raise ValueError(f"TRX selected no tests: {path}: {summary}")
    if executed != total or not_executed != 0 or passed != total or failed_like != 0:
        raise ValueError(f"TRX did not execute and pass every selected test: {path}: {summary}")

    return TrxCounters(total, executed, passed, not_executed, failed_like)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Require nonempty TRX files with every selected test executed and passed."
    )
    parser.add_argument("paths", nargs="+", type=pathlib.Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        for path in args.paths:
            counters = validate_trx(path)
            print(
                f"{path}: total={counters.total} executed={counters.executed} "
                f"passed={counters.passed} notExecuted=0 failedLike=0"
            )
    except (OSError, ET.ParseError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
