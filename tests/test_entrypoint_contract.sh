#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
tmp=$(mktemp "${TMPDIR:-/tmp}/nzbdav-entrypoint.XXXXXX")
first=
second=
watchdog=
cleanup() {
  [ -z "$watchdog" ] || kill "$watchdog" 2>/dev/null || true
  [ -z "$first" ] || kill "$first" 2>/dev/null || true
  [ -z "$second" ] || kill "$second" 2>/dev/null || true
  [ -z "$watchdog" ] || wait "$watchdog" 2>/dev/null || true
  [ -z "$first" ] || wait "$first" 2>/dev/null || true
  [ -z "$second" ] || wait "$second" 2>/dev/null || true
  rm -f "$tmp"
}
trap cleanup EXIT
trap 'exit 124' HUP INT TERM

arm_watchdog() {
  python3 -c 'import os, signal, sys, time; time.sleep(5); os.kill(int(sys.argv[1]), signal.SIGTERM)' "$$" &
  watchdog=$!
}

disarm_watchdog() {
  kill "$watchdog" 2>/dev/null || true
  wait "$watchdog" 2>/dev/null || true
  watchdog=
}

stop_child() {
  child=$1
  kill "$child" 2>/dev/null || true
  wait "$child" 2>/dev/null || true
}

awk '/^wait_either\(\) \{/{copy=1} copy{print} copy && /^}/{exit}' \
  "$repo_root/entrypoint.sh" > "$tmp"
. "$tmp"

/bin/sh -c 'exit 23' & first=$!
sleep 30 & second=$!
expected_first=$first
expected_second=$second
arm_watchdog
set +e
wait_either "$first" "$second"
status=$?
set -e
exited_pid=$EXITED_PID
remaining_pid=$REMAINING_PID
stop_child "$second"
first=
second=
disarm_watchdog
[ "$status" -eq 23 ]
[ "$exited_pid" -eq "$expected_first" ]
[ "$remaining_pid" -eq "$expected_second" ]

sleep 30 & first=$!
/bin/sh -c 'exit 29' & second=$!
expected_first=$first
expected_second=$second
arm_watchdog
set +e
wait_either "$first" "$second"
status=$?
set -e
exited_pid=$EXITED_PID
remaining_pid=$REMAINING_PID
stop_child "$first"
first=
second=
disarm_watchdog
[ "$status" -eq 29 ]
[ "$exited_pid" -eq "$expected_second" ]
[ "$remaining_pid" -eq "$expected_first" ]

grep -Fqx 'ENTRYPOINT ["/entrypoint.sh"]' "$repo_root/Dockerfile"
grep -Fqx 'CMD []' "$repo_root/Dockerfile"

NZBDAV_ENTRYPOINT_SOURCE_ONLY=1 . "$repo_root/entrypoint.sh"

expect_valid() {
  validate_maintenance_args "$@"
}

expect_invalid() {
  set +e
  validate_maintenance_args "$@"
  actual=$?
  set -e
  [ "$actual" -eq 64 ]
}

expect_valid --db-migration
expect_valid --db-migration 20260711000000_TargetMigration
expect_valid --db-export-json /transfer/snapshot.json
expect_valid --db-import-json /transfer/snapshot.json
expect_valid --db-import-json /transfer/snapshot.json --replace

expect_invalid
expect_invalid /bin/sh
expect_invalid --db-migration ""
expect_invalid --db-migration --bad-target
expect_invalid --db-migration target extra
expect_invalid --db-export-json
expect_invalid --db-export-json ""
expect_invalid --db-export-json --db-migration
expect_invalid --db-export-json --replace
expect_invalid --db-export-json /transfer/snapshot.json extra
expect_invalid --db-import-json
expect_invalid --db-import-json ""
expect_invalid --db-import-json --db-migration
expect_invalid --db-import-json --replace
expect_invalid --db-import-json /transfer/snapshot.json --unknown
expect_invalid --db-import-json /transfer/snapshot.json --replace extra
echo "entrypoint contract: PASS"
