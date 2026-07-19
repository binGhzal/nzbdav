#!/bin/sh
set -eu

image=${NZBDAV_TEST_IMAGE:-nzbdav:entrypoint-smoke}
root=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-container.XXXXXX")
prefix="nzbdav-entrypoint-smoke-$$"
migration_name="${prefix}-migration"
export_name="${prefix}-export"
rejected_name="${prefix}-rejected"
missing_key_name="${prefix}-missing-session-key"
invalid_identity_name="${prefix}-invalid-identity"
normal_name="${prefix}-normal"
session_key=$(printf '%064d' 0)
internal_key=$(printf '%064d' 1)
identity_uid=$(id -u)
identity_gid=$(id -g)
case "$identity_uid:$identity_gid" in
  0:*|*:0)
    echo "Container identity smoke requires a nonzero host UID and GID fixture." >&2
    exit 1
    ;;
esac
expected_identity_diagnostic='entrypoint_failure code=invalid_identity'
expected_session_key_diagnostic='SESSION_KEY must be exactly 64 hexadecimal characters.'
invalid_session_key=$(printf '%063d' 0)g

remove_containers() {
  for container in "$migration_name" "$export_name" "$rejected_name" "$missing_key_name" "$invalid_identity_name" "$normal_name"; do
    docker rm -f "$container" >/dev/null 2>&1 || true
  done
}

cleanup() {
  remove_containers
  rm -rf "$root"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

run_bounded_container() {
  container_name=$1
  timeout_seconds=$2
  shift 2

  docker run -d --name "$container_name" "$@" >/dev/null
  started=$(date +%s)
  while [ "$(docker inspect --format '{{.State.Running}}' "$container_name")" = "true" ]; do
    now=$(date +%s)
    if [ "$((now - started))" -ge "$timeout_seconds" ]; then
      docker logs "$container_name" >&2 || true
      docker rm -f "$container_name" >/dev/null 2>&1 || true
      echo "Container $container_name timed out after ${timeout_seconds}s." >&2
      return 124
    fi
    sleep 1
  done

  CONTAINER_LOGS=$(docker logs "$container_name" 2>&1 || true)
  printf '%s\n' "$CONTAINER_LOGS"
  CONTAINER_EXIT_CODE=$(docker inspect --format '{{.State.ExitCode}}' "$container_name")
  docker rm "$container_name" >/dev/null
}

remove_containers
mkdir -m 700 "$root/config" "$root/transfer" "$root/missing-key-config" "$root/normal-config"

metadata=$(docker image inspect --format '{{json .Config.Entrypoint}} {{json .Config.Cmd}}' "$image")
case "$metadata" in
  '["/entrypoint.sh"] null'|'["/entrypoint.sh"] []') ;;
  *)
    echo "Unexpected entrypoint metadata: $metadata" >&2
    exit 1
    ;;
esac

run_bounded_container "$migration_name" 120 \
  -e PUID="$identity_uid" -e PGID="$identity_gid" \
  -v "$root/config:/config" -v "$root/transfer:/transfer" \
  "$image" --db-migration
[ "$CONTAINER_EXIT_CODE" -eq 0 ]

run_bounded_container "$export_name" 120 \
  -e PUID="$identity_uid" -e PGID="$identity_gid" \
  -v "$root/config:/config" -v "$root/transfer:/transfer" \
  "$image" --db-export-json /transfer/snapshot.json
[ "$CONTAINER_EXIT_CODE" -eq 0 ]

python3 - "$root/transfer/snapshot.json" <<'PY'
import pathlib, stat, sys
p = pathlib.Path(sys.argv[1])
assert p.is_file()
assert stat.S_IMODE(p.stat().st_mode) == 0o600
PY

run_bounded_container "$rejected_name" 30 "$image" /bin/sh
[ "$CONTAINER_EXIT_CODE" -eq 64 ]
run_bounded_container "$rejected_name" 30 "$image" --db-export-json --db-migration
[ "$CONTAINER_EXIT_CODE" -eq 64 ]
run_bounded_container "$rejected_name" 30 "$image" --db-import-json --db-migration
[ "$CONTAINER_EXIT_CODE" -eq 64 ]
run_bounded_container "$rejected_name" 30 "$image" --db-import-json --replace
[ "$CONTAINER_EXIT_CODE" -eq 64 ]

for configured_identity in \
  "0:$identity_gid" \
  "000000:$identity_gid" \
  "$identity_uid:0" \
  "$identity_uid:000000"
do
  configured_puid=${configured_identity%%:*}
  configured_pgid=${configured_identity#*:}
  run_bounded_container "$invalid_identity_name" 30 \
    -e PUID="$configured_puid" -e PGID="$configured_pgid" \
    -e AUTH_MODE=local \
    -v "$root/missing-key-config:/config" \
    "$image"
  [ "$CONTAINER_EXIT_CODE" -eq 64 ]
  [ "$CONTAINER_LOGS" = "$expected_identity_diagnostic" ] || {
    echo "Numeric-zero identity did not produce the exact bounded startup diagnostic." >&2
    exit 1
  }
done

run_bounded_container "$missing_key_name" 60 \
  -e PUID="$identity_uid" -e PGID="$identity_gid" \
  -e AUTH_MODE=local \
  -v "$root/missing-key-config:/config" \
  "$image"
[ "$CONTAINER_EXIT_CODE" -eq 78 ]
[ "$CONTAINER_LOGS" = "$expected_session_key_diagnostic" ] || {
  echo "Missing SESSION_KEY did not produce the exact bounded startup diagnostic." >&2
  exit 1
}

run_bounded_container "$missing_key_name" 60 \
  -e PUID="$identity_uid" -e PGID="$identity_gid" \
  -e AUTH_MODE=local -e SESSION_KEY="$invalid_session_key" \
  -v "$root/missing-key-config:/config" \
  "$image"
[ "$CONTAINER_EXIT_CODE" -eq 78 ]
[ "$CONTAINER_LOGS" = "$expected_session_key_diagnostic" ] || {
  echo "Invalid SESSION_KEY did not produce the exact bounded startup diagnostic." >&2
  exit 1
}
case "$CONTAINER_LOGS" in
  *"$invalid_session_key"*)
    echo "Invalid SESSION_KEY diagnostic exposed candidate material." >&2
    exit 1
    ;;
esac

docker run -d --name "$normal_name" -e PUID="$identity_uid" -e PGID="$identity_gid" \
  -e AUTH_MODE=local -e SESSION_KEY="$session_key" \
  -e FRONTEND_BACKEND_API_KEY="$internal_key" \
  -v "$root/normal-config:/config" -p 127.0.0.1::3000 "$image" >/dev/null
port=$(docker port "$normal_name" 3000/tcp | awk -F: 'NR == 1 { print $NF }')
health_deadline=$(($(date +%s) + 90))
until curl --max-time 5 -fsS "http://127.0.0.1:$port/health" >/dev/null; do
  if [ "$(docker inspect --format '{{.State.Running}}' "$normal_name")" != "true" ]; then
    docker logs "$normal_name" >&2 || true
    echo "Container $normal_name exited before its frontend became healthy." >&2
    exit 1
  fi
  if [ "$(date +%s)" -ge "$health_deadline" ]; then
    docker logs "$normal_name" >&2 || true
    echo "Container $normal_name did not become healthy within 90s." >&2
    exit 1
  fi
  sleep 1
done

docker exec -i \
  -e EXPECTED_UID="$identity_uid" \
  -e EXPECTED_GID="$identity_gid" \
  "$normal_name" sh <<'SH'
set -eu

assert_process_identity() {
  process_status=$1
  expected_uid=$2
  expected_gid=$3
  process_label=$4
  uid_values=$(awk '/^Uid:/ { print $2 " " $3 " " $4 " " $5 }' "$process_status")
  gid_values=$(awk '/^Gid:/ { print $2 " " $3 " " $4 " " $5 }' "$process_status")
  expected_uid_values="$expected_uid $expected_uid $expected_uid $expected_uid"
  expected_gid_values="$expected_gid $expected_gid $expected_gid $expected_gid"

  [ "$uid_values" = "$expected_uid_values" ] || {
    echo "$process_label did not drop every UID identity to the expected value." >&2
    exit 1
  }
  [ "$gid_values" = "$expected_gid_values" ] || {
    echo "$process_label did not drop every GID identity to the expected value." >&2
    exit 1
  }
}

assert_process_identity "/proc/1/status" 0 0 "Container PID 1"

backend_pid=
frontend_pid=
identity_deadline=$(($(date +%s) + 15))
while [ -z "$backend_pid" ] || [ -z "$frontend_pid" ]; do
  # shellcheck disable=SC2013 # Linux exposes direct child PIDs as space-separated words.
  for child_pid in $(cat "/proc/1/task/1/children"); do
    [ -r "/proc/$child_pid/cmdline" ] || continue
    child_command=$(tr '\000' ' ' < "/proc/$child_pid/cmdline")
    case "$child_command" in
      *NzbWebDAV*) backend_pid=$child_pid ;;
      *"node dist-node/bootstrap.js"*) frontend_pid=$child_pid ;;
    esac
  done
  if [ "$(date +%s)" -ge "$identity_deadline" ]; then
    echo "Could not resolve both network workload processes from container PID 1." >&2
    exit 1
  fi
  [ -n "$backend_pid" ] && [ -n "$frontend_pid" ] || sleep 1
done

assert_process_identity "/proc/$backend_pid/status" "$EXPECTED_UID" "$EXPECTED_GID" "Backend workload"
assert_process_identity "/proc/$frontend_pid/status" "$EXPECTED_UID" "$EXPECTED_GID" "Frontend workload"
SH
docker stop -t 20 "$normal_name" >/dev/null
[ "$(docker inspect --format '{{.State.ExitCode}}' "$normal_name")" -eq 0 ]
docker rm "$normal_name" >/dev/null
echo "entrypoint container smoke: PASS"
