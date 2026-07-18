#!/bin/sh
set -eu

image=${NZBDAV_TEST_IMAGE:-nzbdav:entrypoint-smoke}
root=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-container.XXXXXX")
prefix="nzbdav-entrypoint-smoke-$$"
migration_name="${prefix}-migration"
export_name="${prefix}-export"
rejected_name="${prefix}-rejected"
missing_key_name="${prefix}-missing-session-key"
normal_name="${prefix}-normal"
session_key="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

remove_containers() {
  for container in "$migration_name" "$export_name" "$rejected_name" "$missing_key_name" "$normal_name"; do
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
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -v "$root/config:/config" -v "$root/transfer:/transfer" \
  "$image" --db-migration
[ "$CONTAINER_EXIT_CODE" -eq 0 ]

run_bounded_container "$export_name" 120 \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
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

run_bounded_container "$missing_key_name" 60 \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -e AUTH_MODE=local -e FRONTEND_BACKEND_API_KEY=entrypoint-smoke \
  -v "$root/missing-key-config:/config" \
  "$image"
[ "$CONTAINER_EXIT_CODE" -ne 0 ]
case "$CONTAINER_LOGS" in
  *"SESSION_KEY must be exactly 64 hexadecimal characters."*) ;;
  *)
    echo "Missing SESSION_KEY did not produce the required startup diagnostic." >&2
    printf '%s\n' "$CONTAINER_LOGS" >&2
    exit 1
    ;;
esac

docker run -d --name "$normal_name" -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -e AUTH_MODE=local -e SESSION_KEY="$session_key" \
  -e FRONTEND_BACKEND_API_KEY=entrypoint-smoke \
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
docker stop -t 20 "$normal_name" >/dev/null
[ "$(docker inspect --format '{{.State.ExitCode}}' "$normal_name")" -eq 0 ]
docker rm "$normal_name" >/dev/null
echo "entrypoint container smoke: PASS"
