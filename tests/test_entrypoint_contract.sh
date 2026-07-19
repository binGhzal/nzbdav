#!/bin/sh
set -eu

repo_root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
tmp=$(mktemp "${TMPDIR:-/tmp}/nzbdav-entrypoint.XXXXXX")
fixture=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-fixture.XXXXXX")
fake_bin=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-bin.XXXXXX")
generation_bin=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-generation-bin.XXXXXX")
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
  rm -rf "$fixture" "$fake_bin" "$generation_bin"
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

fail_entrypoint_contract() {
  printf '%s\n' "$1" >&2
  exit 1
}

stop_child() {
  child=$1
  kill "$child" 2>/dev/null || true
  wait "$child" 2>/dev/null || true
}

awk '/^wait_either\(\) \{/{copy=1} copy{print} copy && /^}/{exit}' \
  "$repo_root/entrypoint.sh" > "$tmp"
# shellcheck source=/dev/null
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
grep -Fqx 'EXPOSE 3000' "$repo_root/Dockerfile"
if grep -Eq '^EXPOSE .*8080' "$repo_root/Dockerfile"; then
  echo "Dockerfile exposes private backend port 8080" >&2
  exit 1
fi
[ ! -e "$repo_root/backend/Dockerfile" ] \
  || fail_entrypoint_contract "retired backend Dockerfile remains reachable"
[ ! -e "$repo_root/backend/entrypoint.sh" ] \
  || fail_entrypoint_contract "retired backend entrypoint remains reachable"
[ ! -e "$repo_root/frontend/Dockerfile" ] \
  || fail_entrypoint_contract "retired frontend Dockerfile remains reachable"
# shellcheck disable=SC2016 # Exact source-code contract, not shell expansion.
grep -Fqx '    exec su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV "$@"' "$repo_root/entrypoint.sh" \
  || fail_entrypoint_contract "maintenance does not drop both user and group"
# shellcheck disable=SC2016 # Exact source-code contract, not shell expansion.
grep -Fqx 'su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV --db-migration' "$repo_root/entrypoint.sh" \
  || fail_entrypoint_contract "startup migration does not drop both user and group"
# shellcheck disable=SC2016 # Exact source-code contract, not shell expansion.
grep -Fqx 'su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV &' "$repo_root/entrypoint.sh" \
  || fail_entrypoint_contract "backend does not drop both user and group"
# shellcheck disable=SC2016 # Exact source-code contract, not shell expansion.
grep -Fqx 'su-exec "$USER_NAME:$GROUP_NAME" node dist-node/bootstrap.js &' "$repo_root/entrypoint.sh" \
  || fail_entrypoint_contract "frontend does not drop both user and group"
# shellcheck disable=SC2016 # Exact source-code contract, not shell expansion.
if grep -Eq 'su-exec "\$USER_NAME"([[:space:]]|$)' "$repo_root/entrypoint.sh"; then
  fail_entrypoint_contract "entrypoint contains a bare user-only privilege drop"
fi
grep -Fqx 'export ASPNETCORE_URLS="http://127.0.0.1:8080"' "$repo_root/entrypoint.sh"
grep -Fqx 'export DOTNET_URLS="http://127.0.0.1:8080"' "$repo_root/entrypoint.sh"
grep -Fqx 'export BACKEND_URL="http://127.0.0.1:8080"' "$repo_root/entrypoint.sh"
# shellcheck disable=SC2016 # This is an intentionally literal source-code canary.
if grep -Fq 'http://${LISTEN_ADDRESS}:8080' "$repo_root/entrypoint.sh"; then
  echo "entrypoint couples private backend binding to the public listener" >&2
  exit 1
fi

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
  cat > "$command_path" <<'EOF'
#!/bin/sh
printf '%s\n' "$0" >> "$NZBDAV_ENTRYPOINT_SIDE_EFFECTS"
exit 99
EOF
  chmod +x "$command_path"
done

config_fixture="$fixture/config"
mkdir -p "$config_fixture"
fixture_file="$config_fixture/db.sqlite"
: > "$fixture_file"
chmod 640 "$fixture_file"
valid_internal_key=$(printf '%064d' 1)
valid_session_key=$(printf '%064d' 2)
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

expect_invalid_identity() {
  configured_puid=$1
  configured_pgid=$2
  rm -f "$side_effects"
  AUTH_MODE=local
  SESSION_KEY=$valid_session_key
  PUID=$configured_puid
  PGID=$configured_pgid
  export AUTH_MODE SESSION_KEY PUID PGID
  unset FRONTEND_BACKEND_API_KEY || true
  PATH="$fake_bin:$original_path"
  export PATH
  set +e
  identity_output=$(main 2>&1)
  identity_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$identity_status" -eq 64 ] \
    || fail_entrypoint_contract "numeric-zero identity did not return status 64"
  [ "$identity_output" = "entrypoint_failure code=invalid_identity" ] \
    || fail_entrypoint_contract "numeric-zero identity did not return the fixed bounded diagnostic"
  [ ! -e "$side_effects" ] \
    || fail_entrypoint_contract "numeric-zero identity reached a forbidden startup side effect"
  current_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
  current_file=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
  [ "$before_directory" = "$current_directory" ] \
    || fail_entrypoint_contract "numeric-zero identity changed the configuration directory"
  [ "$before" = "$current_file" ] \
    || fail_entrypoint_contract "numeric-zero identity changed the database fixture"
}

expect_invalid_identity 0 1001
expect_invalid_identity 00 1001
expect_invalid_identity 000000 1001
expect_invalid_identity 1000 0
expect_invalid_identity 1000 00
expect_invalid_identity 1000 000000

for identity_shell in /bin/sh /usr/bin/busybox; do
  if [ "$identity_shell" = /usr/bin/busybox ]; then
    [ -x "$identity_shell" ] || continue
    # shellcheck disable=SC2016 # The child shell must expand its own fixture variables.
    "$identity_shell" sh -c '
      NZBDAV_ENTRYPOINT_SOURCE_ONLY=1 . "$1"
      PUID=0001000
      PGID=0001001
      validate_identity
      [ "$PUID" = 1000 ] && [ "$PGID" = 1001 ]
    ' sh "$repo_root/entrypoint.sh"
  else
    # shellcheck disable=SC2016 # The child shell must expand its own fixture variables.
    "$identity_shell" -c '
      NZBDAV_ENTRYPOINT_SOURCE_ONLY=1 . "$1"
      PUID=0001000
      PGID=0001001
      validate_identity
      [ "$PUID" = 1000 ] && [ "$PGID" = 1001 ]
    ' sh "$repo_root/entrypoint.sh"
  fi
done
unset AUTH_MODE SESSION_KEY PUID PGID FRONTEND_BACKEND_API_KEY || true

session_key_diagnostic='SESSION_KEY must be exactly 64 hexadecimal characters.'
auth_mode_diagnostic='AUTH_MODE must be either local or authentik-proxy.'
invalid_session_key=$(printf '%063d' 0)g

expect_local_session_key_failure() {
  session_state=$1
  internal_key_state=$2
  rm -f "$side_effects"
  case "$internal_key_state" in
    configured)
      FRONTEND_BACKEND_API_KEY=$valid_internal_key
      export FRONTEND_BACKEND_API_KEY
      ;;
    unset)
      unset FRONTEND_BACKEND_API_KEY || true
      ;;
    empty)
      FRONTEND_BACKEND_API_KEY=
      export FRONTEND_BACKEND_API_KEY
      ;;
    *)
      fail_entrypoint_contract "invalid internal-key ordering test state"
      ;;
  esac
  case "$session_state" in
    missing)
      unset AUTH_MODE SESSION_KEY || true
      ;;
    invalid)
      AUTH_MODE=local
      SESSION_KEY=$invalid_session_key
      export AUTH_MODE SESSION_KEY
      ;;
    *)
      fail_entrypoint_contract "invalid local session-key test state"
      ;;
  esac
  PATH="$fake_bin:$original_path"
  export PATH
  set +e
  session_output=$(main 2>&1)
  session_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$session_status" -eq 78 ] \
    || fail_entrypoint_contract "invalid local session key did not return the fixed configuration status"
  [ "$session_output" = "$session_key_diagnostic" ] \
    || fail_entrypoint_contract "invalid local session key did not return the fixed bounded diagnostic"
  case "$session_output" in
    *"$invalid_session_key"*)
      fail_entrypoint_contract "invalid local session-key diagnostic exposed candidate material"
      ;;
  esac
  [ ! -e "$side_effects" ] \
    || fail_entrypoint_contract "invalid local session key reached a forbidden startup side effect"
  current_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
  current_file=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
  [ "$before_directory" = "$current_directory" ] \
    || fail_entrypoint_contract "invalid local session key changed the configuration directory"
  [ "$before" = "$current_file" ] \
    || fail_entrypoint_contract "invalid local session key changed the database fixture"
}

expect_invalid_auth_mode_failure() {
  configured_auth_mode=$1
  rm -f "$side_effects"
  AUTH_MODE=$configured_auth_mode
  export AUTH_MODE
  unset SESSION_KEY FRONTEND_BACKEND_API_KEY || true
  PATH="$fake_bin:$original_path"
  export PATH
  set +e
  auth_mode_output=$(main 2>&1)
  auth_mode_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$auth_mode_status" -eq 78 ] \
    || fail_entrypoint_contract "invalid auth mode did not return the fixed configuration status"
  [ "$auth_mode_output" = "$auth_mode_diagnostic" ] \
    || fail_entrypoint_contract "invalid auth mode did not return the fixed bounded diagnostic"
  [ ! -e "$side_effects" ] \
    || fail_entrypoint_contract "invalid auth mode reached a forbidden startup side effect"
  current_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
  current_file=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
  [ "$before_directory" = "$current_directory" ] \
    || fail_entrypoint_contract "invalid auth mode changed the configuration directory"
  [ "$before" = "$current_file" ] \
    || fail_entrypoint_contract "invalid auth mode changed the database fixture"
}

expect_session_key_independent_path() {
  path_name=$1
  auth_mode=$2
  shift 2
  rm -f "$side_effects"
  AUTH_MODE=$auth_mode
  FRONTEND_BACKEND_API_KEY=$valid_internal_key
  export AUTH_MODE FRONTEND_BACKEND_API_KEY
  unset SESSION_KEY || true
  PATH="$fake_bin:$original_path"
  export PATH
  set +e
  independent_output=$(main "$@" 2>&1)
  independent_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$independent_status" -eq 70 ] \
    || fail_entrypoint_contract "$path_name unexpectedly required a session key"
  [ "$independent_output" = "entrypoint_failure code=group_create_failed" ] \
    || fail_entrypoint_contract "$path_name did not reach the first identity side effect"
  [ -s "$side_effects" ] \
    || fail_entrypoint_contract "$path_name did not reach the first identity side effect"
}

expect_local_session_key_failure missing configured
expect_local_session_key_failure invalid configured
expect_local_session_key_failure missing unset
expect_local_session_key_failure invalid empty
expect_invalid_auth_mode_failure LOCAL
expect_invalid_auth_mode_failure typo
expect_invalid_auth_mode_failure ""
expect_session_key_independent_path "maintenance startup" local --db-migration
expect_session_key_independent_path "authentik-proxy startup" authentik-proxy

mixed_case_session_key=$(printf '%032d%s%s' 0 'aBcDeF0123456789' 'aBcDeF0123456789')
AUTH_MODE=local
export AUTH_MODE
unset SESSION_KEY || true
SESSION_KEY=$mixed_case_session_key
session_validation_output="$fixture/session-validation-output"
: > "$session_validation_output"
set +e
validate_frontend_session_key >"$session_validation_output" 2>&1
session_validation_status=$?
set -e
[ "$session_validation_status" -eq 0 ] \
  || fail_entrypoint_contract "valid mixed-case session key was rejected"
[ ! -s "$session_validation_output" ] \
  || fail_entrypoint_contract "valid mixed-case session key produced output"
[ "$SESSION_KEY" = "$mixed_case_session_key" ] \
  || fail_entrypoint_contract "valid mixed-case session key was changed"
/bin/sh -c '[ "$SESSION_KEY" = "$1" ]' sh "$mixed_case_session_key" \
  || fail_entrypoint_contract "valid mixed-case session key was not exported unchanged"

# Subsequent tests isolate the internal-key contract and therefore provide a
# valid local-auth session key explicitly.
AUTH_MODE=local
SESSION_KEY=$valid_session_key
export AUTH_MODE SESSION_KEY

expect_invalid_internal_key() {
  configured_internal_key=$1
  rm -f "$side_effects"
  FRONTEND_BACKEND_API_KEY=$configured_internal_key
  export FRONTEND_BACKEND_API_KEY
  PATH="$fake_bin:$original_path"
  export PATH
  set +e
  invalid_key_output=$(main 2>&1)
  invalid_key_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$invalid_key_status" -eq 78 ] \
    || fail_entrypoint_contract "invalid internal key did not return the fixed configuration status"
  [ "$invalid_key_output" = "FRONTEND_BACKEND_API_KEY must be exactly 64 hexadecimal characters." ] \
    || fail_entrypoint_contract "invalid internal key did not return the fixed diagnostic"
  [ ! -e "$side_effects" ] \
    || fail_entrypoint_contract "invalid internal key reached a forbidden startup side effect"
  current_directory=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$config_fixture")
  current_file=$(python3 -c 'import os,sys; s=os.stat(sys.argv[1]); print(f"{s.st_uid}:{s.st_gid}:{s.st_mode}:{s.st_mtime_ns}")' "$fixture_file")
  [ "$before_directory" = "$current_directory" ] \
    || fail_entrypoint_contract "invalid internal key changed the configuration directory"
  [ "$before" = "$current_file" ] \
    || fail_entrypoint_contract "invalid internal key changed the database fixture"
}

expect_invalid_internal_key "replace-with-random-hex"
expect_invalid_internal_key "$(printf '%063d' 0)"
expect_invalid_internal_key "$(printf '%065d' 0)"
expect_invalid_internal_key " "
expect_invalid_internal_key "$(printf '%063d' 0)!"
expect_invalid_internal_key "$(printf '%063d' 0)g"

# Maintenance argv validation has higher precedence than internal-key
# validation, including when both inputs are invalid.
rm -f "$side_effects"
FRONTEND_BACKEND_API_KEY=replace-with-random-hex
export FRONTEND_BACKEND_API_KEY
PATH="$fake_bin:$original_path"
export PATH
set +e
precedence_output=$(main --db-import-v3 /transfer/snapshot 2>&1)
precedence_status=$?
set -e
PATH=$original_path
export PATH
[ "$precedence_status" -eq 64 ] \
  || fail_entrypoint_contract "invalid maintenance argv lost precedence over internal-key validation"
[ "$precedence_output" = "$usage" ] \
  || fail_entrypoint_contract "invalid maintenance argv did not return the fixed usage diagnostic"
[ ! -e "$side_effects" ] \
  || fail_entrypoint_contract "invalid maintenance argv reached a forbidden startup side effect"

expect_generated_internal_key() {
  key_output="$fixture/internal-key-output"
  : > "$key_output"
  set +e
  configure_internal_api_key >"$key_output" 2>&1
  key_status=$?
  set -e
  [ "$key_status" -eq 0 ] \
    || fail_entrypoint_contract "internal key generation failed"
  [ ! -s "$key_output" ] \
    || fail_entrypoint_contract "internal key generation produced output"
  [ "${#FRONTEND_BACKEND_API_KEY}" -eq 64 ] \
    || fail_entrypoint_contract "generated internal key has an invalid shape"
  case "$FRONTEND_BACKEND_API_KEY" in
    *[!0-9a-f]*) fail_entrypoint_contract "generated internal key has an invalid shape" ;;
  esac
  /bin/sh -c '
    [ "${#FRONTEND_BACKEND_API_KEY}" -eq 64 ] || exit 1
    case "$FRONTEND_BACKEND_API_KEY" in *[!0-9a-f]*) exit 1 ;; esac
  ' || fail_entrypoint_contract "generated internal key was not exported"
}

unset FRONTEND_BACKEND_API_KEY || true
expect_generated_internal_key
FRONTEND_BACKEND_API_KEY=
export FRONTEND_BACKEND_API_KEY
expect_generated_internal_key

configured_internal_key=$(printf '%032d%s%s' 0 'aBcDeF0123456789' 'aBcDeF0123456789')
FRONTEND_BACKEND_API_KEY=$configured_internal_key
export FRONTEND_BACKEND_API_KEY
key_output="$fixture/internal-key-output"
: > "$key_output"
set +e
configure_internal_api_key >"$key_output" 2>&1
key_status=$?
set -e
[ "$key_status" -eq 0 ] \
  || fail_entrypoint_contract "valid configured internal key was rejected"
[ ! -s "$key_output" ] \
  || fail_entrypoint_contract "valid configured internal key produced output"
[ "$FRONTEND_BACKEND_API_KEY" = "$configured_internal_key" ] \
  || fail_entrypoint_contract "valid configured internal key was not preserved"
/bin/sh -c '[ "$FRONTEND_BACKEND_API_KEY" = "$1" ]' sh "$configured_internal_key" \
  || fail_entrypoint_contract "valid configured internal key was not exported"

generation_arguments="$fixture/internal-key-generation-arguments"
cat > "$generation_bin/hexdump" <<'EOF'
#!/bin/sh
if [ "$#" -eq 5 ] \
  && [ "$1" = "-n" ] \
  && [ "$2" = "32" ] \
  && [ "$3" = "-ve" ] \
  && [ "$4" = '1/1 "%.2x"' ] \
  && [ "$5" = "/dev/urandom" ]; then
  printf '%s\n' single-command > "$NZBDAV_INTERNAL_KEY_GENERATION_ARGUMENTS"
elif [ "$#" -eq 2 ] \
  && [ "$1" = "-ve" ] \
  && [ "$2" = '1/1 "%.2x"' ]; then
  printf '%s\n' legacy-pipeline > "$NZBDAV_INTERNAL_KEY_GENERATION_ARGUMENTS"
else
  printf '%s\n' unexpected > "$NZBDAV_INTERNAL_KEY_GENERATION_ARGUMENTS"
fi

case "$NZBDAV_INTERNAL_KEY_ENCODER_MODE" in
  partial)
    printf '%063d' 0
    exit 91
    ;;
  shaped)
    printf '%064d' 0
    exit 92
    ;;
  *)
    exit 93
    ;;
esac
EOF
chmod +x "$generation_bin/hexdump"

expect_failed_internal_key_generation() {
  initial_state=$1
  encoder_mode=$2
  case "$initial_state" in
    unset)
      unset FRONTEND_BACKEND_API_KEY || true
      ;;
    exported-empty)
      FRONTEND_BACKEND_API_KEY=
      export FRONTEND_BACKEND_API_KEY
      ;;
    *)
      fail_entrypoint_contract "invalid internal key generation test state"
      ;;
  esac

  rm -f "$generation_arguments"
  key_output="$fixture/internal-key-output"
  : > "$key_output"
  PATH="$generation_bin:$original_path"
  export PATH
  export NZBDAV_INTERNAL_KEY_GENERATION_ARGUMENTS="$generation_arguments"
  export NZBDAV_INTERNAL_KEY_ENCODER_MODE="$encoder_mode"
  set +e
  configure_internal_api_key >"$key_output" 2>&1
  key_status=$?
  set -e
  PATH=$original_path
  export PATH

  [ "$key_status" -eq 70 ] \
    || fail_entrypoint_contract "failing internal key encoder was accepted"
  [ "$(cat "$key_output")" = "entrypoint_failure code=internal_key_generation_failed" ] \
    || fail_entrypoint_contract "internal key generation failure did not return the fixed diagnostic"
  [ -z "${FRONTEND_BACKEND_API_KEY+x}" ] \
    || fail_entrypoint_contract "failed internal key generation retained candidate material"
  /bin/sh -c '[ -z "${FRONTEND_BACKEND_API_KEY+x}" ]' \
    || fail_entrypoint_contract "failed internal key generation exported candidate material"
  [ "$(cat "$generation_arguments")" = "single-command" ] \
    || fail_entrypoint_contract "internal key generation did not use the checked single-command encoder"
}

expect_failed_internal_key_generation unset partial
expect_failed_internal_key_generation exported-empty partial
expect_failed_internal_key_generation unset shaped
expect_failed_internal_key_generation exported-empty shaped

if grep -Eq '^[[:space:]]*FRONTEND_BACKEND_API_KEY=' "$repo_root/.env.example"; then
  fail_entrypoint_contract ".env.example assigns the internal key"
fi
grep -Fq 'FRONTEND_BACKEND_API_KEY is intentionally omitted' "$repo_root/.env.example" \
  || fail_entrypoint_contract ".env.example does not explain the omitted internal key"
grep -Fq 'fresh random 64-character hexadecimal key on every container start' "$repo_root/.env.example" \
  || fail_entrypoint_contract ".env.example does not explain per-start internal-key generation"
grep -Fq 'independently generated 64-character hexadecimal value' "$repo_root/.env.example" \
  || fail_entrypoint_contract ".env.example does not explain pinned internal-key requirements"

grep -Fq "internal_key=\$(printf '%064d' 1)" "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke lacks a deterministic valid internal-key fixture"
grep -Fq -- "-e FRONTEND_BACKEND_API_KEY=\"\$internal_key\"" "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke does not use its valid internal-key fixture"
grep -Fq "expected_session_key_diagnostic='SESSION_KEY must be exactly 64 hexadecimal characters.'" \
  "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke lacks the fixed local session-key diagnostic"
grep -Fq "invalid_session_key=\$(printf '%063d' 0)g" \
  "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke lacks an invalid session-key canary"
# shellcheck disable=SC2016 # These are intentionally literal source-code assertions.
grep -Fq '[ "$CONTAINER_LOGS" = "$expected_session_key_diagnostic" ]' \
  "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke does not require the exact bounded session-key diagnostic"
# shellcheck disable=SC2016 # These are intentionally literal source-code assertions.
grep -Fq '*"$invalid_session_key"*)' "$repo_root/tests/test_entrypoint_container.sh" \
  || fail_entrypoint_contract "container smoke does not reject session-key candidate disclosure"
grep -Fq "internal_key=\$(printf '%064d' 2)" "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "verification workflow lacks a deterministic valid internal-key fixture"
grep -Fq -- "-e FRONTEND_BACKEND_API_KEY=\"\$internal_key\"" "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "verification workflow does not use its valid internal-key fixture"
grep -Fq "session_key=\$(printf '%064d' 3)" "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "migration-failure smoke lacks a distinct valid session-key fixture"
grep -Fq -- "-e AUTH_MODE=local" "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "migration-failure smoke does not declare local authentication intent"
grep -Fq -- "-e SESSION_KEY=\"\$session_key\"" "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "migration-failure smoke does not pass its valid session-key fixture"
grep -Fq "grep -Fqx 'entrypoint_failure code=database_migration_failed' /tmp/nzbdav-entrypoint.log" \
  "$repo_root/.github/workflows/verify.yml" \
  || fail_entrypoint_contract "migration-failure smoke does not prove the exact failure boundary"

if grep -Fq "export FRONTEND_BACKEND_API_KEY=\"\$(" "$repo_root/CONTRIBUTING.md"; then
  fail_entrypoint_contract "contributor setup masks internal-key generator status during export"
fi
grep -Fq "if ! internal_api_key=\"\$(hexdump -n 32" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not check internal-key generation"
grep -Fq "hexdump -n 32 -ve '1/1 \"%.2x\"' /dev/urandom" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not use the bounded entropy encoder"
grep -Fq "[ \"\${#internal_api_key}\" -ne 64 ]" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not validate internal-key length"
grep -Fq "*[!0-9a-f]*" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not validate lowercase hexadecimal shape"
grep -Fq "export FRONTEND_BACKEND_API_KEY=\"\$internal_api_key\"" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not export the validated internal key"
grep -Fq "unset internal_api_key" "$repo_root/CONTRIBUTING.md" \
  || fail_entrypoint_contract "contributor setup does not clear its temporary candidate"

# Setup command failures must not echo a configured path or the external
# command's diagnostic. Exercise the source-only helper without touching host
# ownership or container services.
hostile_config="$fixture/credential-marker
provider-body-marker"
mkdir -p "$hostile_config"
failure_bin="$fixture/failure-bin"
mkdir -p "$failure_bin"
cat > "$failure_bin/chown" <<'EOF'
#!/bin/sh
echo "external-command-secret $*" >&2
exit 1
EOF
chmod +x "$failure_bin/chown"
PATH="$failure_bin:$original_path"
export PATH CONFIG_PATH="$hostile_config" PUID=1000 PGID=1000
set +e
setup_output=$(prepare_config_ownership 2>&1)
setup_status=$?
set -e
PATH=$original_path
export PATH
[ "$setup_status" -eq 70 ]
[ "$setup_output" = "entrypoint_failure code=config_ownership_failed" ]
case "$setup_output" in
  *credential-marker*|*provider-body-marker*|*external-command-secret*) exit 1 ;;
esac
echo "entrypoint contract: PASS"
