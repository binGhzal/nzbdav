#!/bin/sh

wait_either() {
    wait_either_pid_one=$1
    wait_either_pid_two=$2
    wait_either_child_status=

    while true; do
        if ! kill -0 "$wait_either_pid_one" 2>/dev/null; then
            wait "$wait_either_pid_one"
            wait_either_child_status=$?
            EXITED_PID=$wait_either_pid_one
            REMAINING_PID=$wait_either_pid_two
            return "$wait_either_child_status"
        fi

        if ! kill -0 "$wait_either_pid_two" 2>/dev/null; then
            wait "$wait_either_pid_two"
            wait_either_child_status=$?
            EXITED_PID=$wait_either_pid_two
            REMAINING_PID=$wait_either_pid_one
            return "$wait_either_child_status"
        fi

        sleep 0.5
    done
}

maintenance_usage() {
    echo "Usage: /entrypoint.sh [--db-migration [target] | --db-export-json PATH | --db-import-json PATH [--replace]]" >&2
}

entrypoint_failure() {
    printf '%s\n' "entrypoint_failure code=$1" >&2
}

normalize_identity_value() {
    normalized_identity_value=${1:-}
    case "$normalized_identity_value" in
        ""|*[!0-9]*)
            return 1
            ;;
    esac

    while [ "${normalized_identity_value#0}" != "$normalized_identity_value" ]; do
        normalized_identity_value=${normalized_identity_value#0}
    done
    [ -n "$normalized_identity_value" ] || return 1
    printf '%s\n' "$normalized_identity_value"
}

validate_identity() {
    configured_puid=${PUID:-1000}
    configured_pgid=${PGID:-1000}
    normalized_puid=$(normalize_identity_value "$configured_puid") || {
        entrypoint_failure invalid_identity
        return 64
    }
    normalized_pgid=$(normalize_identity_value "$configured_pgid") || {
        entrypoint_failure invalid_identity
        return 64
    }
    PUID=$normalized_puid
    PGID=$normalized_pgid
}

validate_frontend_session_key() {
    case "${AUTH_MODE-local}" in
        authentik-proxy)
            return 0
            ;;
        local)
            ;;
        *)
            printf '%s\n' 'AUTH_MODE must be either local or authentik-proxy.' >&2
            return 78
            ;;
    esac

    if [ -z "${SESSION_KEY:-}" ] || [ "${#SESSION_KEY}" -ne 64 ]; then
        printf '%s\n' 'SESSION_KEY must be exactly 64 hexadecimal characters.' >&2
        return 78
    fi
    case "$SESSION_KEY" in
        *[!0-9A-Fa-f]*)
            printf '%s\n' 'SESSION_KEY must be exactly 64 hexadecimal characters.' >&2
            return 78
            ;;
    esac
    export SESSION_KEY
}

configure_internal_api_key() {
    if [ -z "${FRONTEND_BACKEND_API_KEY:-}" ]; then
        internal_api_key_candidate=
        if ! internal_api_key_candidate=$(hexdump -n 32 -ve '1/1 "%.2x"' /dev/urandom 2>/dev/null); then
            unset internal_api_key_candidate FRONTEND_BACKEND_API_KEY
            entrypoint_failure internal_key_generation_failed
            return 70
        fi
        if [ "${#internal_api_key_candidate}" -ne 64 ]; then
            unset internal_api_key_candidate FRONTEND_BACKEND_API_KEY
            entrypoint_failure internal_key_generation_failed
            return 70
        fi
        case "$internal_api_key_candidate" in
            *[!0-9a-f]*)
                unset internal_api_key_candidate FRONTEND_BACKEND_API_KEY
                entrypoint_failure internal_key_generation_failed
                return 70
                ;;
        esac
        FRONTEND_BACKEND_API_KEY=$internal_api_key_candidate
        unset internal_api_key_candidate
    else
        if [ "${#FRONTEND_BACKEND_API_KEY}" -ne 64 ]; then
            printf '%s\n' 'FRONTEND_BACKEND_API_KEY must be exactly 64 hexadecimal characters.' >&2
            return 78
        fi
        case "$FRONTEND_BACKEND_API_KEY" in
            *[!0-9A-Fa-f]*)
                printf '%s\n' 'FRONTEND_BACKEND_API_KEY must be exactly 64 hexadecimal characters.' >&2
                return 78
                ;;
        esac
    fi

    export FRONTEND_BACKEND_API_KEY
}

prepare_data_directory() {
    if mkdir -p /data >/dev/null 2>&1 \
        && chown "$PUID:$PGID" /data >/dev/null 2>&1 \
        && chmod 775 /data >/dev/null 2>&1; then
        return 0
    fi

    entrypoint_failure data_setup_failed
    return 70
}

prepare_config_ownership() {
    chown "$PUID:$PGID" "$CONFIG_PATH" >/dev/null 2>&1 || {
        entrypoint_failure config_ownership_failed
        return 70
    }
    if [ -f "$CONFIG_PATH/db.sqlite" ]; then
        DB_UID=$(stat -c '%u' "$CONFIG_PATH/db.sqlite" 2>/dev/null) || {
            entrypoint_failure config_stat_failed
            return 70
        }
        DB_GID=$(stat -c '%g' "$CONFIG_PATH/db.sqlite" 2>/dev/null) || {
            entrypoint_failure config_stat_failed
            return 70
        }

        if [ "$DB_UID" -ne "$PUID" ] || [ "$DB_GID" -ne "$PGID" ]; then
            echo "entrypoint_event code=config_ownership_repair"
            chown -R "$PUID:$PGID" "$CONFIG_PATH" >/dev/null 2>&1 || {
                entrypoint_failure config_ownership_failed
                return 70
            }
        fi
    fi
}

is_maintenance_value() {
    case "${1:-}" in
        ""|-*) return 1 ;;
        *[![:space:]]*) return 0 ;;
        *) return 1 ;;
    esac
}

validate_maintenance_args() {
    case "${1:-}" in
        --db-migration)
            [ "$#" -eq 1 ] || {
                [ "$#" -eq 2 ] && is_maintenance_value "$2"
            } || return 64
            ;;
        --db-export-json)
            [ "$#" -eq 2 ] && is_maintenance_value "$2" || return 64
            ;;
        --db-import-json)
            [ "$#" -eq 2 ] || {
                [ "$#" -eq 3 ] && [ "$3" = "--replace" ]
            } || return 64
            is_maintenance_value "$2" || return 64
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
    cd /app/backend 2>/dev/null || {
        entrypoint_failure backend_directory_unavailable
        return 70
    }
    command -v su-exec >/dev/null 2>&1 && [ -x ./NzbWebDAV ] || {
        entrypoint_failure backend_executable_unavailable
        return 70
    }
    exec su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV "$@"
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
if [ "$#" -gt 0 ] && ! validate_maintenance_args "$@"; then
    maintenance_usage
    return 64
fi

validate_identity || return $?

if [ "$#" -eq 0 ]; then
    validate_frontend_session_key || return $?
fi

configure_internal_api_key || return $?

trap terminate TERM INT

# Create or reuse group based on PGID
if getent group "$PGID" >/dev/null 2>&1; then
    EXISTING_GROUP=$(getent group "$PGID" 2>/dev/null | cut -d: -f1)
    [ -n "$EXISTING_GROUP" ] || {
        entrypoint_failure group_lookup_failed
        return 70
    }
    echo "entrypoint_event code=group_reused"
    GROUP_NAME="$EXISTING_GROUP"
else
    addgroup -g "$PGID" appgroup >/dev/null 2>&1 || {
        entrypoint_failure group_create_failed
        return 70
    }
    GROUP_NAME=appgroup
fi

# Create or reuse user based on PUID
if getent passwd "$PUID" >/dev/null 2>&1; then
    EXISTING_USER=$(getent passwd "$PUID" 2>/dev/null | cut -d: -f1)
    [ -n "$EXISTING_USER" ] || {
        entrypoint_failure user_lookup_failed
        return 70
    }
    echo "entrypoint_event code=user_reused"
    USER_NAME="$EXISTING_USER"
else
    if ! id appuser >/dev/null 2>&1; then
        adduser -D -H -u "$PUID" -G "$GROUP_NAME" appuser >/dev/null 2>&1 || {
            entrypoint_failure user_create_failed
            return 70
        }
    fi
    USER_NAME=appuser
fi

# The public frontend listener remains operator-configurable. The co-located
# backend is an implementation detail and must never bind outside loopback.
LISTEN_ADDRESS=${LISTEN_ADDRESS:-0.0.0.0}
export ASPNETCORE_URLS="http://127.0.0.1:8080"
export DOTNET_URLS="http://127.0.0.1:8080"
export BACKEND_URL="http://127.0.0.1:8080"

if [ -z "${CONFIG_PATH}" ]; then
    export CONFIG_PATH="/config"
fi

prepare_data_directory || return $?
prepare_config_ownership || return $?

if [ "$#" -gt 0 ]; then
    run_maintenance "$@"
    return $?
fi

# Run backend database migration
cd /app/backend 2>/dev/null || {
    entrypoint_failure backend_directory_unavailable
    return 70
}
echo "Running database maintenance."
su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV --db-migration
MIGRATION_EXIT_CODE=$?
if [ "$MIGRATION_EXIT_CODE" -ne 0 ]; then
    echo "entrypoint_failure code=database_migration_failed" >&2
    exit "$MIGRATION_EXIT_CODE"
fi
echo "Done with database maintenance."

# Run backend as "$USER_NAME" in background
su-exec "$USER_NAME:$GROUP_NAME" ./NzbWebDAV &
BACKEND_PID=$!

# Wait for backend health check
echo "Waiting for backend to start."
MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-180}
MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
case "$MAX_BACKEND_HEALTH_RETRIES:$MAX_BACKEND_HEALTH_RETRY_DELAY" in
    *[!0-9:]*)
        entrypoint_failure invalid_health_retry_policy
        return 64
        ;;
esac
i=0
while true; do
    echo "entrypoint_event code=backend_health_check"
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
        echo "entrypoint_failure code=backend_health_timeout" >&2
        kill "$BACKEND_PID"
        wait "$BACKEND_PID"
        exit 1
    fi

    sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
done

# Run frontend as "$USER_NAME" in background
cd /app/frontend 2>/dev/null || {
    entrypoint_failure frontend_directory_unavailable
    return 70
}
su-exec "$USER_NAME:$GROUP_NAME" node dist-node/bootstrap.js &
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
