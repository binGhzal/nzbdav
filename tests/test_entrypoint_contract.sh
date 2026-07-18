#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
tmp=$(mktemp "${TMPDIR:-/tmp}/nzbdav-entrypoint.XXXXXX")
fixture=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-fixture.XXXXXX")
fake_bin=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-bin.XXXXXX")
side_effects="$fixture/side-effects"
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
  rm -rf "$fixture" "$fake_bin"
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
expect_invalid --db-migration " "
expect_invalid --db-migration -bad-target
expect_invalid --db-migration --bad-target
expect_invalid --db-migration target extra
expect_invalid --db-migration --db-export-json /transfer/snapshot.json
expect_invalid --db-export-json
expect_invalid --db-export-json ""
expect_invalid --db-export-json -snapshot.json
expect_invalid --db-export-json --db-migration
expect_invalid --db-export-json --replace
expect_invalid --db-export-json /transfer/snapshot.json extra
expect_invalid --db-import-json
expect_invalid --db-import-json ""
expect_invalid --db-import-json -snapshot.json
expect_invalid --db-import-json --db-migration
expect_invalid --db-import-json --replace
expect_invalid --db-import-json /transfer/snapshot.json --unknown
expect_invalid --db-import-json /transfer/snapshot.json extra --replace
expect_invalid --db-import-json /transfer/snapshot.json --replace --replace
expect_invalid --db-import-json /transfer/snapshot.json --replace extra
expect_invalid --
expect_invalid --DB-MIGRATION
expect_invalid --db-export-v3 /transfer/snapshot
expect_invalid --db-import-v3 /transfer/snapshot
expect_invalid --db-export-v3
expect_invalid --db-import-v3
expect_invalid --db-import-v3 /transfer/snapshot extra

usage=$(maintenance_usage 2>&1)
case "$usage" in
  *--db-export-v3*|*--db-import-v3*) exit 1 ;;
esac

# Invalid argv must return before the first identity, random-key, filesystem,
# ownership, database-inspection, or child-execution operation in main.
for command in getent cut addgroup adduser id head hexdump mkdir chown chmod stat su-exec; do
  command_path="$fake_bin/$command"
  {
    echo '#!/bin/sh'
    echo 'printf "%s\n" "$0" >> "$NZBDAV_ENTRYPOINT_SIDE_EFFECTS"'
    echo 'exit 99'
  } > "$command_path"
  chmod +x "$command_path"
done

config_fixture="$fixture/config"
mkdir -p "$config_fixture"
fixture_file="$config_fixture/db.sqlite"
: > "$fixture_file"
chmod 640 "$fixture_file"
before_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
before=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
original_path=$PATH
PATH="$fake_bin:$PATH"
export PATH
export NZBDAV_ENTRYPOINT_SIDE_EFFECTS="$side_effects"
export CONFIG_PATH="$config_fixture"
unset FRONTEND_BACKEND_API_KEY || true
set +e
main --db-import-v3 /transfer/snapshot >/dev/null 2>&1
invalid_import_main_status=$?
main --db-export-v3 /transfer/snapshot >/dev/null 2>&1
invalid_export_main_status=$?
set -e
PATH=$original_path
export PATH
after_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
after=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
[ "$invalid_import_main_status" -eq 64 ]
[ "$invalid_export_main_status" -eq 64 ]
[ "$before_directory" = "$after_directory" ]
[ "$before" = "$after" ]
[ ! -e "$side_effects" ]
echo "entrypoint contract: PASS"
