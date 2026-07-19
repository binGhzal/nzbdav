# Contributing

## Set up your system

The project consists of frontend and backend projects. Both share some
necessary environment variables.

**Ensure that frontend and backend share the same environment configuration!**

Environment variables:

Run this block from the repository root so `CONFIG_PATH` resolves inside the
ignored backend build-output tree:

```bash
export CONFIG_PATH="$PWD/backend/bin/dev-config/"
export BACKEND_URL=http://localhost:5000

configure_dev_internal_api_key() {
  internal_api_key=
  if ! internal_api_key="$(hexdump -n 32 -ve '1/1 "%.2x"' /dev/urandom)"; then
    printf '%s\n' 'Failed to generate FRONTEND_BACKEND_API_KEY.' >&2
    unset internal_api_key
    return 1
  fi
  if [ "${#internal_api_key}" -ne 64 ]; then
    printf '%s\n' 'Failed to generate FRONTEND_BACKEND_API_KEY.' >&2
    unset internal_api_key
    return 1
  fi
  case "$internal_api_key" in
    *[!0-9a-f]*)
      printf '%s\n' 'Failed to generate FRONTEND_BACKEND_API_KEY.' >&2
      unset internal_api_key
      return 1
      ;;
  esac
  export FRONTEND_BACKEND_API_KEY="$internal_api_key"
  unset internal_api_key
}
configure_dev_internal_api_key || exit 1

configure_nzbdav_session_key() {
  nzbdav_session_key_candidate=
  if ! nzbdav_session_key_candidate="$(hexdump -n 32 -ve '1/1 "%.2x"' /dev/urandom)"; then
    printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
    unset nzbdav_session_key_candidate
    return 1
  fi
  if [ "${#nzbdav_session_key_candidate}" -ne 64 ]; then
    printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
    unset nzbdav_session_key_candidate
    return 1
  fi
  case "$nzbdav_session_key_candidate" in
    *[!0-9a-f]*)
      printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
      unset nzbdav_session_key_candidate
      return 1
      ;;
  esac
  export SESSION_KEY="$nzbdav_session_key_candidate"
  unset nzbdav_session_key_candidate
}
configure_nzbdav_session_key || exit 1
export AUTH_MODE=local
export SECURE_COOKIES=false
export ALLOW_INSECURE_COOKIES=true
```

Split frontend/backend development requires both processes to share that exact
independently generated 64-character hexadecimal value. Never reuse
`SESSION_KEY` or a public API credential. The combined container instead
generates a fresh internal key on every start when the variable is omitted.
The frontend development server also needs the separately generated
`SESSION_KEY` and the explicit insecure-cookie opt-in because its documented
development endpoint is plain HTTP. Reuse the same session key across restarts
when session continuity matters.

Use the repository-declared tool families:

- .NET SDK 10 and .NET/ASP.NET Runtime 10
- Node.js 24 and npm 11, as declared by `.nvmrc` and `frontend/package.json`
- Python 3 for repository test tooling
- Docker only for the complete container and disposable PostgreSQL gates

Example installation for Arch based systems:

```bash
sudo pacman -S dotnet-sdk aspnet-runtime nodejs npm python docker
```

## Build / run backend

```bash
cd backend

# Restore the shared backend/test dependency graph first
dotnet restore ../backend.Tests/backend.Tests.csproj

# Build (release)
dotnet publish -c Release -o ./publish

# Create database
mkdir -p $CONFIG_PATH
./publish/NzbWebDAV --db-migration

# Run backend
./publish/NzbWebDAV
```

## Build / serve frontend

```bash
cd frontend

# Install dependencies
npm ci

# Run / serve frontend with hot module replacement
npm run dev
```

## Build Docker image

The combined container defaults to local authentication. The setup block above
exports the checked, nonprinted session key and direct-HTTP cookie policy used
by both container examples. Keep the same `SESSION_KEY` for restarts of the
same development instance.

### Using Docker CLI

In the root directory, run:

```bash
docker build .
```

You can also tag the release, which can be used with `docker compose`:

```bash
docker build -t example/nzbdav:test_build .
```

Run the container:

```bash
docker run --rm -it \
  -v nzbdav-config:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -e AUTH_MODE=local \
  -e SESSION_KEY \
  -e SECURE_COOKIES=false \
  -e ALLOW_INSECURE_COOKIES=true \
  -p 127.0.0.1:3333:3000 \
  example/nzbdav:test_build
```

### Using Docker Compose

```yaml
services:
  nzbdav:
    build: .
    ports:
      - 3333:3000
    volumes:
      - nzbdav-config:/config
      - nzbdav-data:/data
    environment:
      - PUID=1000
      - PGID=1000
      - AUTH_MODE=local
      - SESSION_KEY=${SESSION_KEY:?generate and export SESSION_KEY before starting Compose}
      - SECURE_COOKIES=false
      - ALLOW_INSECURE_COOKIES=true

volumes:
  nzbdav-config:
  nzbdav-data:
```

Build and run container:

```bash
docker compose up
```

## Verify before review

Begin with the narrow tests for the code changed. The repository's reproducible
local non-service baseline is:

```bash
dotnet build backend/NzbWebDAV.csproj --configuration Release --no-restore -warnaserror
dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore
python3 -m unittest discover -s tests -p 'test_*.py'
npm --prefix frontend run typecheck
npm --prefix frontend run test
npm --prefix frontend run build
npm --prefix frontend run build:server
git diff --check
```

PostgreSQL integration requires a uniquely owned disposable PostgreSQL 16.14
target and the `NZBDAV_TEST_POSTGRES_CONNECTION_STRING`,
`NZBDAV_REQUIRE_POSTGRES_TESTS`, `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE`, and `TZ`
variable names. Never point tests at an existing database. Follow the active
plan and CI workflow for exact values and ownership rules.
