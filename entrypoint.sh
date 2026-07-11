#!/bin/sh

wait_either() {
    local pid1=$1
    local pid2=$2
    local child_status

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            child_status=$?
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return "$child_status"
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            child_status=$?
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return "$child_status"
        fi

        sleep 0.5
    done
}

maintenance_usage() {
    echo "Usage: /entrypoint.sh [--db-migration [target] | --db-export-json PATH | --db-import-json PATH [--replace]]" >&2
}

validate_maintenance_args() {
    case "${1:-}" in
        --db-migration)
            [ "$#" -eq 1 ] || {
                [ "$#" -eq 2 ] && [ -n "$2" ] && [ "${2#--}" = "$2" ]
            } || return 64
            ;;
        --db-export-json)
            [ "$#" -eq 2 ] && [ -n "$2" ] && [ "${2#--}" = "$2" ] || return 64
            ;;
        --db-import-json)
            [ "$#" -eq 2 ] || {
                [ "$#" -eq 3 ] && [ "$3" = "--replace" ]
            } || return 64
            [ -n "$2" ] && [ "${2#--}" = "$2" ] || return 64
            ;;
        *)
            return 64
            ;;
    esac
}

run_maintenance() {
    if ! validate_maintenance_args "$@"; then
        maintenance_usage
        return 64
    fi

    umask 077
    cd /app/backend || return 70
    exec su-exec "$USER_NAME" ./NzbWebDAV "$@"
}

# Signal handling for graceful shutdown
terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    # Wait for children to exit
    wait
    exit 0
}

main() {
trap terminate TERM INT

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create or reuse group based on PGID
if getent group "$PGID" >/dev/null; then
    EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1)
    echo "GID $PGID already exists, using group $EXISTING_GROUP"
    GROUP_NAME="$EXISTING_GROUP"
else
    addgroup -g "$PGID" appgroup
    GROUP_NAME=appgroup
fi

# Create or reuse user based on PUID
if getent passwd "$PUID" >/dev/null; then
    EXISTING_USER=$(getent passwd "$PUID" | cut -d: -f1)
    echo "UID $PUID already exists, using user $EXISTING_USER"
    USER_NAME="$EXISTING_USER"
else
    if ! id appuser >/dev/null 2>&1; then
        adduser -D -H -u "$PUID" -G "$GROUP_NAME" appuser
    fi
    USER_NAME=appuser
fi

# Configure the listen address.
# Defaults to 0.0.0.0 (all interfaces), preserving the existing behaviour.
# Set LISTEN_ADDRESS to a specific IP (e.g. 192.168.1.10) to restrict which
# network interface nzbdav binds to.
LISTEN_ADDRESS=${LISTEN_ADDRESS:-0.0.0.0}
export ASPNETCORE_URLS="http://${LISTEN_ADDRESS}:8080"

# BACKEND_URL is used internally by the frontend to proxy requests to the backend.
# When LISTEN_ADDRESS is a wildcard/loopback address localhost is always reachable.
# When LISTEN_ADDRESS is a specific non-loopback IP, derive BACKEND_URL from it.
if [ -z "${BACKEND_URL}" ]; then
    if [ "$LISTEN_ADDRESS" = "0.0.0.0" ] || [ "$LISTEN_ADDRESS" = "127.0.0.1" ] || [ "$LISTEN_ADDRESS" = "localhost" ] || [ "$LISTEN_ADDRESS" = "::" ] || [ "$LISTEN_ADDRESS" = "::1" ]; then
        export BACKEND_URL="http://localhost:8080"
    else
        export BACKEND_URL="http://${LISTEN_ADDRESS}:8080"
    fi
fi

if [ -z "${FRONTEND_BACKEND_API_KEY}" ]; then
    export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
fi

if [ -z "${CONFIG_PATH}" ]; then
    export CONFIG_PATH="/config"
fi

mkdir -p /data
chown "$PUID:$PGID" /data
chmod 775 /data

# Recursively update permissions to all $CONFIG_PATH files if needed
chown "$PUID:$PGID" "$CONFIG_PATH"
if [ -f "$CONFIG_PATH/db.sqlite" ]; then
    DB_UID=$(stat -c '%u' "$CONFIG_PATH/db.sqlite")
    DB_GID=$(stat -c '%g' "$CONFIG_PATH/db.sqlite")

    if [ "$DB_UID" -ne "$PUID" ] || [ "$DB_GID" -ne "$PGID" ]; then
        echo "$CONFIG_PATH/db.sqlite ownership mismatch: (uid:$DB_UID gid:$DB_GID) vs expected (uid:$PUID gid:$PGID)"
        echo "Updating ownership of $CONFIG_PATH/* to (uid:$PUID gid:$PGID)"
        chown -R "$PUID:$PGID" "$CONFIG_PATH"
    fi
fi

if [ "$#" -gt 0 ]; then
    run_maintenance "$@"
    return $?
fi

# Run backend database migration
cd /app/backend
echo "Running database maintenance."
su-exec "$USER_NAME" ./NzbWebDAV --db-migration
MIGRATION_EXIT_CODE=$?
if [ "$MIGRATION_EXIT_CODE" -ne 0 ]; then
    echo "Database migration failed. Exiting with error code $MIGRATION_EXIT_CODE."
    exit "$MIGRATION_EXIT_CODE"
fi
echo "Done with database maintenance."

# Run backend as "$USER_NAME" in background
su-exec "$USER_NAME" ./NzbWebDAV &
BACKEND_PID=$!

# Wait for backend health check
echo "Waiting for backend to start."
MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-180}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
i=0
while true; do
    echo "Checking backend health: $BACKEND_URL/health ..."
    if curl --max-time 5 -s -o /dev/null -w "%{http_code}" "$BACKEND_URL/health" | grep -q "^200$"; then
        echo "Backend is healthy."
        break
    fi

    if ! kill -0 "$BACKEND_PID" 2>/dev/null; then
        wait "$BACKEND_PID"
        BACKEND_EXIT_CODE=$?
        echo "Backend exited before becoming healthy with code $BACKEND_EXIT_CODE."
        exit "$BACKEND_EXIT_CODE"
    fi

    i=$((i+1))
    if [ "$i" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
        echo "Backend failed health check after $MAX_BACKEND_HEALTH_RETRIES retries. Exiting."
        kill $BACKEND_PID
        wait $BACKEND_PID
        exit 1
    fi

    sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
done

# Run frontend as "$USER_NAME" in background
cd /app/frontend
su-exec "$USER_NAME" npm run start &
FRONTEND_PID=$!

    wait_either "$BACKEND_PID" "$FRONTEND_PID"
    EXIT_CODE=$?
    if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
        echo "The web-frontend has exited. Shutting down the web-backend..."
    else
        echo "The web-backend has exited. Shutting down the web-frontend..."
    fi
    kill "$REMAINING_PID" 2>/dev/null || true
    wait "$REMAINING_PID" 2>/dev/null || true
    return "$EXIT_CODE"
}

if [ "${NZBDAV_ENTRYPOINT_SOURCE_ONLY:-0}" != "1" ]; then
    main "$@"
    exit $?
fi
