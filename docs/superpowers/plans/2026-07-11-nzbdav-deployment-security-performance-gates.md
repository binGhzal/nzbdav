# NZBDav Deployment Security And Performance Gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the role-separated NZBDav topology the safe single-host production default, with a public UI only, private SAB/WebDAV/RPC surfaces, unchanged rclone addressing, crash isolation, and evidence-based performance gates.

**Architecture:** Finish the `ui` role and role-aware container entrypoint, prove the topology in a repo-local Compose fixture, then apply the same aliases, volumes, secrets, and lifecycle rules to `/Users/binghzal/Developer/media-stack`. Keep one image for every NZBDav role and keep rclone as the only production mount while measuring aggregate CPU, PSS, GC, and request latency across processes.

**Tech Stack:** .NET 10, Node.js Alpine runtime, Express 5, Docker Compose, rclone WebDAV/VFS, Python 3 unittest, Bash, GitHub Actions, Playwright

## Global Constraints

- Complete `2026-07-11-nzbdav-role-host-durable-coordination.md`, `2026-07-11-nzbdav-gateway-data-plane.md`, and `2026-07-11-nzbdav-lane-worker-extraction.md` first.
- Follow `docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md`.
- Build and publish one image; select `all`, `control`, `gateway`, `worker-download`, `worker-verify`, `worker-repair`, or `ui` with `NZBDAV_ROLE`.
- Traefik routes only to `ui`; SAB, WebDAV, internal RPC, workers, and rclone publish no host ports.
- Preserve `http://nzbdav:3000/` as the rclone WebDAV URL and use `nzbdav-sab:3000` as the ARR download-client endpoint.
- The private bridge must permit outbound NNTP traffic; do not set Docker network `internal: true`.
- Control is the only role with the application database; gateway is the only role with provider credentials and sparse-cache write access.
- Workers receive neither database nor provider credentials.
- Keep rclone as the production mount and do not promote DFS in this plan.
- Missing correctness or performance evidence fails the release gate.
- Do not add scheduler, GC, lease, or circuit-breaker controls to the WebUI.

---

### Task 1: Restrict The Public UI Proxy

**Files:**
- Create: `frontend/server/backend-proxy-policy.ts`
- Create: `frontend/server/backend-proxy-policy.test.ts`
- Modify: `frontend/server/app.ts`
- Modify: `frontend/server/websocket.server.ts`
- Modify: `frontend/server/websocket.server.test.ts`
- Create: `frontend/server/role-status.ts`
- Create: `frontend/server/role-status.test.ts`
- Modify: `frontend/e2e/server-health.spec.ts`

**Interfaces:**
- Consumes: `BACKEND_URL`, `FRONTEND_BACKEND_API_KEY` shared only by `ui` and `control`, and the internal token for the control-only UI status probe.
- Produces: `evaluateBackendProxy(method, rawUrl): ProxyDecision`, an authenticated endpoint/method allowlist, and a websocket path restricted to `/ws`.

- [ ] **Step 1: Write policy tests before changing the proxy**

Cover an allowed status read, an allowed queue mutation, an allowed repair
action, and forbidden SAB/WebDAV/internal paths:

```ts
import { describe, expect, it } from "vitest";
import { evaluateBackendProxy } from "./backend-proxy-policy";

describe("backend proxy policy", () => {
  it.each([
    ["GET", "/api?mode=fullstatus"],
    ["GET", "/api?mode=pause"],
    ["GET", "/api?mode=queue&name=priority&value=job-id&value2=1"],
    ["POST", "/api?mode=queue&name=delete"],
    ["POST", "/api/repair/run"],
    ["DELETE", "/api/arr/correlations/7f7bb6d8-4278-45e8-923d-054d1764fbe2"],
  ])("allows %s %s", (method, url) => {
    expect(evaluateBackendProxy(method, url)).toEqual({ allowed: true, requiresSession: true });
  });

  it.each([
    ["GET", "/content/movie.mkv"],
    ["PROPFIND", "/"],
    ["GET", "/api/internal/leases"],
    ["POST", "/api?mode=config"],
    ["GET", "/api?mode=queue&apikey=stolen"],
  ])("blocks %s %s", (method, url) => {
    expect(evaluateBackendProxy(method, url).allowed).toBe(false);
  });
});
```

- [ ] **Step 2: Run the policy test and confirm failure**

```bash
npm --prefix frontend test -- backend-proxy-policy.test.ts
```

Expected: FAIL because `backend-proxy-policy.ts` does not exist.

- [ ] **Step 3: Implement an explicit method and path policy**

Define public bootstrap routes separately, allow only UI-used SAB modes, and
enumerate REST route templates:

```ts
export type ProxyDecision = { allowed: boolean; requiresSession: boolean };

const PUBLIC_BOOTSTRAP = new Map<string, ReadonlySet<string>>([
  ["/api/is-onboarding", new Set(["GET"])],
  ["/api/create-account", new Set(["POST"])],
  ["/api/authenticate", new Set(["POST"])],
]);

const SESSION_ROUTES: ReadonlyArray<[RegExp, ReadonlySet<string>]> = [
  [/^\/api\/(get-config|list-webdav-directory|get-health-check-queue|get-health-check-history|repair\/status|repair\/runs|arr\/validation|arr\/search-nudges|arr\/correlations|download-nzb)$/, new Set(["GET"])],
  [/^\/api\/(update-config|test-rclone-connection|test-usenet-connection|test-usenet-pipelining|test-arr-connection|repair\/run|repair\/clear|arr\/search-nudges\/clear|arr\/correlations)$/, new Set(["POST"])],
  [/^\/api\/repair\/run\/[0-9a-f-]+\/cancel$/, new Set(["POST"])],
  [/^\/api\/arr\/search-nudges\/[0-9a-f-]+\/retry$/, new Set(["POST"])],
  [/^\/api\/arr\/correlations\/[0-9a-f-]+$/, new Set(["DELETE"])],
  [/^\/api\/(convert-strm-to-symlinks|recreate-strm-files|remove-unlinked-files|remove-unlinked-files\/dry-run)$/, new Set(["POST"])],
  [/^\/api\/remove-unlinked-files\/audit$/, new Set(["GET"])],
];

const SAB_READ_MODES = new Set(["status", "fullstatus", "version", "get_files"]);
const SAB_QUEUE_ACTIONS = new Set(["delete", "priority", "pause", "resume"]);

export function evaluateBackendProxy(method: string, rawUrl: string): ProxyDecision {
  const normalizedMethod = method.toUpperCase();
  const url = new URL(rawUrl, "http://ui.invalid");
  if (url.searchParams.has("apikey") || url.searchParams.has("apiKey")) {
    return { allowed: false, requiresSession: true };
  }

  const bootstrapMethods = PUBLIC_BOOTSTRAP.get(url.pathname);
  if (bootstrapMethods?.has(normalizedMethod)) {
    return { allowed: true, requiresSession: false };
  }

  if (url.pathname === "/api") {
    const mode = url.searchParams.get("mode") ?? "";
    const name = url.searchParams.get("name");
    let allowed = normalizedMethod === "GET" && SAB_READ_MODES.has(mode);
    if (mode === "pause" || mode === "resume") {
      allowed = name === null && (normalizedMethod === "GET" || normalizedMethod === "POST");
    } else if (mode === "addfile") {
      allowed = name === null && normalizedMethod === "POST";
    } else if (mode === "queue") {
      allowed = name === null
        ? normalizedMethod === "GET"
        : (SAB_QUEUE_ACTIONS.has(name) && (normalizedMethod === "GET" || normalizedMethod === "POST"))
          || (name === "get_files" && normalizedMethod === "GET");
    } else if (mode === "history") {
      allowed = name === null
        ? normalizedMethod === "GET"
        : name === "delete" && (normalizedMethod === "GET" || normalizedMethod === "POST");
    }
    return { allowed, requiresSession: true };
  }

  const allowed = SESSION_ROUTES.some(([path, methods]) =>
    path.test(url.pathname) && methods.has(normalizedMethod));
  return { allowed, requiresSession: true };
}
```

When a new UI feature needs a backend route, its change must add one focused
policy test and one explicit rule in the same commit.

- [ ] **Step 4: Apply the policy before proxying**

In `frontend/server/app.ts`, remove the WebDAV/content prefix proxy. For every
allowed session route, require `isAuthenticated(req)`, delete client-supplied
API credentials, and inject the UI-to-control credential:

```ts
const decision = evaluateBackendProxy(req.method, req.originalUrl);
if (!decision.allowed) {
  res.status(404).type("text/plain").send("Not found");
  return;
}
if (decision.requiresSession && !await isAuthenticated(req)) {
  res.status(401).type("text/plain").send("Authentication required");
  return;
}
delete req.headers["x-api-key"];
delete req.headers["authorization"];
req.headers["x-api-key"] = process.env.FRONTEND_BACKEND_API_KEY ?? "";
return forwardToBackend(req, res, next);
```

Reject startup when either `BACKEND_URL` or `FRONTEND_BACKEND_API_KEY` is empty.

- [ ] **Step 5: Restrict websocket upgrade forwarding**

Accept upgrades only for the normalized `${URL_BASE}/ws` path and require the
existing session cookie before opening the backend websocket. Return HTTP 401
for unauthenticated upgrades and never accept a caller-provided backend API
key.

```ts
expect(await attemptWebsocket("/content/file.mkv")).toBe(404);
expect(await attemptWebsocket("/ws", { authenticated: false })).toBe(401);
expect(await attemptWebsocket("/ws", { authenticated: true })).toBe(101);
```

- [ ] **Step 6: Expose authenticated UI process status to control**

Add `GET /internal/role-status` before the public router. Require
`x-nzbdav-internal-token`, compare equal-length buffers with
`crypto.timingSafeEqual`, and return 404 for missing/wrong tokens. Return only
role, instance ID, PID, readiness, process CPU delta, RSS, heap, allocation
delta, Linux PSS when available, event-loop delay, active proxy requests, and
timestamp. Do not include environment values, cookies, headers, URLs, or paths.

```ts
expect(await getRoleStatus()).toHaveProperty("status", 404);
expect(await getRoleStatus("wrong")).toHaveProperty("status", 404);
const accepted = await getRoleStatus(process.env.NZBDAV_INTERNAL_TOKEN);
expect(accepted.body.role).toBe("ui");
expect(accepted.body).not.toHaveProperty("environment");
```

Control's `RemoteRoleStatusClient` polls this internal HTTP endpoint alongside
gateway/worker gRPC status.

- [ ] **Step 7: Run frontend and Playwright gates**

```bash
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run build:server
npm --prefix frontend run test:e2e
```

Expected: all commands exit 0; direct `/content`, WebDAV methods, unknown API
routes, and query-string keys are denied by UI tests.

- [ ] **Step 8: Commit**

```bash
git add frontend/server frontend/e2e
git commit -m "security: restrict the public ui backend proxy"
```

### Task 2: Finalize The One-Image Role Entrypoint

**Files:**
- Modify: `entrypoint.sh`
- Modify: `Dockerfile`
- Create: `tests/test_entrypoint_roles.py`
- Modify: `.github/workflows/ci.yml`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: role hosts from the three prerequisite plans.
- Produces: one image with deterministic process ownership for every `NZBDAV_ROLE` value.

- [ ] **Step 1: Write an entrypoint command-selection test**

Run the image with `NZBDAV_ENTRYPOINT_DRY_RUN=1` and parse its single output
line. Assert the selected operations:

```python
EXPECTED = {
    "all": ["migrate", "backend", "frontend"],
    "control": ["migrate", "backend"],
    "gateway": ["backend"],
    "worker-download": ["backend"],
    "worker-verify": ["backend"],
    "worker-repair": ["backend"],
    "ui": ["frontend"],
}

def test_role_operation_matrix():
    for role, expected in EXPECTED.items():
        assert entrypoint_operations(role) == expected
```

`entrypoint_operations` invokes `docker run --rm -e NZBDAV_ROLE=<role>
-e NZBDAV_ENTRYPOINT_DRY_RUN=1 nzbdav:roles`, parses
`operations=migrate,backend`, and checks exit status. Also assert an unknown role
exits 64 before migration or process startup.

- [ ] **Step 2: Run the entrypoint test and confirm failure**

```bash
python3 -m unittest tests.test_entrypoint_roles -v
```

Expected: FAIL because the current entrypoint always migrates and starts both
backend and frontend.

- [ ] **Step 3: Dispatch by role without duplicating process supervision**

Use one validated role switch:

```sh
ROLE=${NZBDAV_ROLE:-all}
case "$ROLE" in
  all) RUN_MIGRATION=1; RUN_BACKEND=1; RUN_FRONTEND=1 ;;
  control) RUN_MIGRATION=1; RUN_BACKEND=1; RUN_FRONTEND=0 ;;
  gateway|worker-download|worker-verify|worker-repair)
    RUN_MIGRATION=0; RUN_BACKEND=1; RUN_FRONTEND=0 ;;
  ui) RUN_MIGRATION=0; RUN_BACKEND=0; RUN_FRONTEND=1 ;;
  *) echo "Unsupported NZBDAV_ROLE: $ROLE" >&2; exit 64 ;;
esac

if [ "${NZBDAV_ENTRYPOINT_DRY_RUN:-0}" = "1" ]; then
  operations=""
  [ "$RUN_MIGRATION" = "1" ] && operations="migrate"
  [ "$RUN_BACKEND" = "1" ] && operations="${operations:+$operations,}backend"
  [ "$RUN_FRONTEND" = "1" ] && operations="${operations:+$operations,}frontend"
  printf 'role=%s operations=%s\n' "$ROLE" "$operations"
  exit 0
fi
```

Only create/chown `/config` and run `--db-migration` for `all` and `control`.
Create `/cache/gateway` for gateway and `/cache/exchange` for control/download.
Workers must not infer a default database path.

- [ ] **Step 4: Give each role exact health ports**

Preserve the prerequisite host contracts:

```text
all              3000 frontend, 8080 monolith backend
ui               3000 HTTP/1 frontend health and UI
control          3000 HTTP/1 SAB/admin/websocket, 8081 HTTP/2 coordinator/status RPC
gateway          3000 HTTP/1 WebDAV/health, 8081 HTTP/2 article/status RPC
worker-*         8080 HTTP/1 health, 8081 HTTP/2 status RPC
```

Expose ports as image metadata only; Compose decides network exposure.

- [ ] **Step 5: Preserve exit codes and graceful shutdown**

For backend single-process roles, use
`exec su-exec "$USER_NAME" /app/backend/NzbWebDAV`; for `ui`, use
`exec su-exec "$USER_NAME" npm --prefix /app/frontend run start`. This sends
signals directly to the role process. Keep `wait_either` only for `all`. A
migration failure must return its exact nonzero status and start no server.

- [ ] **Step 6: Run role image smoke tests**

```bash
docker build -t nzbdav:roles .
python3 -m unittest tests.test_entrypoint_roles -v
for role in all control gateway worker-download worker-verify worker-repair ui; do
  docker run --rm -e NZBDAV_ROLE="$role" -e NZBDAV_ENTRYPOINT_DRY_RUN=1 nzbdav:roles
done
```

Expected: each printed operation list matches `EXPECTED`; no non-control role
prints `migrate`.

- [ ] **Step 7: Commit**

```bash
git add entrypoint.sh Dockerfile tests/test_entrypoint_roles.py .github/workflows/ci.yml CHANGELOG.md
git commit -m "feat: finalize one-image role startup"
```

### Task 3: Add A Repo-Local Role-Split Compose Gate

**Files:**
- Create: `tests/integration/compose.role-split.yml`
- Create: `tests/integration/role-split.env`
- Create: `scripts/test_role_split_compose.py`
- Create: `tests/test_role_split_compose.py`
- Modify: `.gitignore`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: role image, ports, internal token, coordinator, gateway, worker, and status contracts.
- Produces: a disposable multi-container smoke test and machine-readable result under `artifacts/role-split/`.

- [ ] **Step 1: Write Compose-structure assertions**

Render the fixture and assert roles, aliases, mounts, networks, and absence of
published backend ports:

```python
def test_role_split_compose_contract(rendered):
    services = rendered["services"]
    assert services["gateway"]["environment"]["NZBDAV_ROLE"] == "gateway"
    assert "nzbdav" in services["gateway"]["networks"]["nzbdav-internal"]["aliases"]
    assert "nzbdav-sab" in services["control"]["networks"]["nzbdav-internal"]["aliases"]
    for name in ["control", "gateway", "download", "verify", "repair"]:
        assert "ports" not in services[name]
    assert services["gateway"]["volumes"] != services["control"]["volumes"]
```

- [ ] **Step 2: Create the fixture with one shared image**

The fixture defines `ui`, `control`, `gateway`, `download`, `verify`, and
`repair` using `image: nzbdav:roles`. It gives:

```yaml
networks:
  nzbdav-internal:
    driver: bridge

volumes:
  control-config:
  gateway-runtime:
  gateway-cache:
  artifact-exchange:
```

Do not use `internal: true`: the gateway needs outbound provider access. Do not
publish control, gateway, or worker ports. Bind only UI `127.0.0.1:33000:3000`
in the CI fixture.

- [ ] **Step 3: Implement the smoke runner**

`scripts/test_role_split_compose.py` must:

1. render `docker compose config --format json`;
2. reject published control/gateway/worker ports;
3. start control, gateway, and workers;
4. wait for every liveness endpoint;
5. verify missing internal tokens return `Unauthenticated`;
6. verify gateway `/api?mode=queue` and control WebDAV `PROPFIND /` return 404;
7. verify UI `/content/test` returns 404;
8. record container IDs, role names, image IDs, health, and checks;
9. run `docker compose down -v --remove-orphans` in `finally`.

Write the redacted result as:

```json
{
  "schema": 1,
  "passed": true,
  "services": {},
  "checks": {
    "no_published_backends": true,
    "rpc_requires_token": true,
    "gateway_has_no_sab": true,
    "control_has_no_webdav": true,
    "ui_has_no_webdav": true
  }
}
```

- [ ] **Step 4: Add crash and stale-lease scenarios**

The runner leases a blocking verify job, kills `verify`, waits for the two-minute
lease to expire with a 150-second bounded timeout, starts a replacement, and
asserts generation increments. It then
submits the old token and asserts the completion is rejected without changing
durable state.

- [ ] **Step 5: Run the fixture**

```bash
docker build -t nzbdav:roles .
python3 -m unittest tests.test_role_split_compose -v
python3 scripts/test_role_split_compose.py \
  --compose-file tests/integration/compose.role-split.yml \
  --output artifacts/role-split/ci.json
```

Expected: all tests pass and the artifact has `"passed": true`.

- [ ] **Step 6: Commit**

```bash
git add tests/integration tests/test_role_split_compose.py scripts/test_role_split_compose.py .gitignore .github/workflows/ci.yml
git commit -m "test: gate the role-split container topology"
```

### Task 4: Convert Media-Stack To The Private Role Topology

**Files:**
- Modify: `/Users/binghzal/Developer/media-stack/services/00-networks.yaml`
- Modify: `/Users/binghzal/Developer/media-stack/services/20-gateways.yaml`
- Modify: `/Users/binghzal/Developer/media-stack/services/30-arrs.yaml`
- Modify: `/Users/binghzal/Developer/media-stack/services/90-maintenance.yaml`
- Create: `/Users/binghzal/Developer/media-stack/compose.nzbdav-rollback.yaml`
- Modify: `/Users/binghzal/Developer/media-stack/configs/app-provisioning/provision_media_apps.py`
- Modify: `/Users/binghzal/Developer/media-stack/configs/homarr/provision_media_stack.py`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/bootstrap.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/validate.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/lib.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/permissions.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/sonarr-fast-symlink-import.py`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/nzbdav-clear-queue.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/nzbdav-missing-article-repair.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/cleanup-arr-stale-files.py`
- Create: `/Users/binghzal/Developer/media-stack/scripts/split-nzbdav-secrets.sh`
- Modify: `/Users/binghzal/Developer/media-stack/.env.example`
- Modify: `/Users/binghzal/Developer/media-stack/env/local-test.env.example`
- Modify: `/Users/binghzal/Developer/media-stack/env/production.env.example`
- Modify: `/Users/binghzal/Developer/media-stack/secrets/secrets.manifest.example`

**Interfaces:**
- Consumes: `NZBDAV_IMAGE`, role URLs, role ports, and stable aliases from Tasks 2-3.
- Produces: production Compose services `nzbdav-ui`, `nzbdav-control`, `nzbdav-gateway`, `nzbdav-download`, `nzbdav-verify`, `nzbdav-repair`, and existing `nzbdav-rclone`.

- [ ] **Step 1: Add the dedicated routable private bridge**

Add this network without `internal: true`:

```yaml
networks:
  nzbdav-internal:
    name: media_nzbdav_internal
    driver: bridge
```

No service on this bridge publishes a backend port. Outbound routing remains
available for the gateway's NNTP connections.

- [ ] **Step 2: Split legacy secrets without evaluating shell content**

`split-nzbdav-secrets.sh --apply` reads `secrets/nzbdav.env` as lines, rejects
duplicate keys, and writes mode-0600 files through temp-file plus rename:

```text
nzbdav-ui.env:         FRONTEND_BACKEND_API_KEY, SESSION_KEY
nzbdav-control.env:    FRONTEND_BACKEND_API_KEY,
                       NZBDAV_ADMIN_USERNAME, NZBDAV_ADMIN_PASSWORD
nzbdav-gateway.env:    WEBDAV_USER, WEBDAV_PASSWORD,
                       NZBDAV_USENET_PROVIDERS_JSON
nzbdav-internal.env:   NZBDAV_INTERNAL_TOKEN
nzbdav-rclone.env:     NZBDAV_RCLONE_RC_USER, NZBDAV_RCLONE_RC_PASS,
                       WEBDAV_USER, WEBDAV_PASSWORD
nzbdav_api_key:        raw FRONTEND_BACKEND_API_KEY value for Compose secrets
```

Generate `NZBDAV_INTERNAL_TOKEN` with `openssl rand -hex 32` only when absent.
Never `source` or `eval` the legacy file. Leave `nzbdav.env` unchanged until
the separated deployment passes and print SHA-256 fingerprints, never values.

- [ ] **Step 3: Define role ownership in Compose**

Use the same `${NZBDAV_IMAGE}` for all roles. The essential service wiring is:

```yaml
nzbdav-ui:
  environment:
    NZBDAV_ROLE: ui
    BACKEND_URL: http://nzbdav-sab:3000
  networks: [proxy, nzbdav-internal]

nzbdav-control:
  environment:
    NZBDAV_ROLE: control
    NZBDAV_GATEWAY_URL: http://nzbdav:8081
  networks:
    nzbdav-internal:
      aliases: [nzbdav-sab]

nzbdav-gateway:
  environment:
    NZBDAV_ROLE: gateway
    NZBDAV_CONTROL_URL: http://nzbdav-control:8081
  networks:
    nzbdav-internal:
      aliases: [nzbdav]

nzbdav-download:
  environment:
    NZBDAV_ROLE: worker-download
    NZBDAV_CONTROL_URL: http://nzbdav-control:8081
    NZBDAV_INTERNAL_GATEWAY_URL: http://nzbdav:8081
```

Define verify and repair with their matching roles and the same internal RPC
URLs. Set `restart: unless-stopped`, `no-new-privileges:true`, and role-specific
health checks. UI checks `/healthz`; control and gateway check
`/health/ready`; workers check `/health/live` so a dependency outage does not
cause restart loops. `nzbdav-rclone` depends on gateway readiness. Only UI gets
Traefik labels in the default profile. Remove the old `nzbdav` service.

Retain one transitional `nzbdav-all` service behind profile
`nzbdav-rollback`. In canary phases it has only the `nzbdav-sab` alias and may
run local lanes not listed in `NZBDAV_EXTERNAL_WORKER_LANES`; gateway owns the
`nzbdav` alias. `compose.nzbdav-rollback.yaml` adds the `nzbdav` alias only
after separated gateway/control are stopped. It uses the retained legacy
`nzbdav.env` for one release cycle and is never part of the default profile.

- [ ] **Step 4: Assign minimal secrets and volumes**

Use this ownership matrix:

```text
ui       ui + internal env; no config/cache/library mount
control  control + internal env; /config RW; /cache/exchange RW; library RO; backup RW
gateway  gateway + internal env; /config/runtime RO; /cache/gateway RW
download internal env; /cache/exchange RW
verify   internal env only
repair   internal env plus read-only library paths only when reconciliation needs them
rclone   rclone env; FUSE mount/config/cache; no provider or database secret
```

The shared artifact exchange is `${CACHE_ROOT}/nzbdav-exchange`. Gateway sparse
cache is `${CACHE_ROOT}/nzbdav-gateway`. Neither is included in backup jobs.
`bootstrap.sh` creates those directories plus
`${CONFIG_ROOT}/nzbdav/runtime/gateway`; `permissions.sh` assigns `PUID:PGID`
without recursively changing the application database on routine runs.

Set no `NZBDAV_THREADPOOL_MIN_*`, `DOTNET_GC*`, or `COMPlus_GC*` overrides in
Compose. .NET owns asynchronous I/O thread-pool adaptation; the bounded worker
CPU scheduler supplies multicore parallelism. Record the actual GC mode per role
before considering a role-specific A/B change.

- [ ] **Step 5: Point ARR and maintenance at control**

Join Sonarr, Radarr, and Lidarr to `nzbdav-internal`. Change provisioning and
maintenance URLs from `http://nzbdav:3000` to `http://nzbdav-sab:3000` while
keeping rclone at `http://nzbdav:3000/`.

```python
NZBDAV_CONTROL_CONTAINER = "nzbdav-control"
NZBDAV_SAB_HOST = "nzbdav-sab"
url = container_url(NZBDAV_CONTROL_CONTAINER, NZBDAV_PORT)
```

Configure ARR's SAB host with the stable `NZBDAV_SAB_HOST` constant. Do not add
an operator setting for these internal aliases.

Replace the provisioning script's single `nzbdav_env()` reader with explicit
`nzbdav_control_env()`, `nzbdav_gateway_env()`, and raw
`nzbdav_api_key()` readers. Provider normalization writes only
`nzbdav-gateway.env`; API onboarding/configuration targets
`nzbdav-control`; rclone RC values remain in `nzbdav-rclone.env`. A provider
update is applied through control's live gateway snapshot and does not invoke
Compose or restart rclone.

Remove the direct NZBDav database mount from `sonarr-fast-importer`. Add
`--nzbdav-key-file /run/secrets/nzbdav_api_key` to
`sonarr-fast-symlink-import.py`, read it with the existing `read_secret`, and
delete `--nzbdav-db`/`load_nzbdav_key`. Join the importer to
`nzbdav-internal` and point it at `http://nzbdav-sab:3000`.

Update Homarr's internal health URL to `http://nzbdav-ui:3000`. Update queue
maintenance to stop `nzbdav-download`, `nzbdav-verify`, `nzbdav-repair`, then
`nzbdav-control` before offline SQLite work; keep gateway/rclone running so
playback remains available, and restart control before workers. Missing-article
log scans use `nzbdav-gateway`, `nzbdav-control`, and `nzbdav-rclone`.
`cleanup-arr-stale-files.py` defaults its log containers to all six NZBDav
application roles plus rclone instead of the removed `nzbdav` container.

- [ ] **Step 6: Make validation enforce isolation**

Extend `scripts/validate.sh` to fail when:

```bash
docker compose config --format json | python3 -c '
import json, sys
cfg=json.load(sys.stdin)
for name in ("nzbdav-control","nzbdav-gateway","nzbdav-download","nzbdav-verify","nzbdav-repair","nzbdav-rclone"):
    svc=cfg["services"][name]
    assert not svc.get("ports"), f"{name} publishes ports"
    labels="\n".join(svc.get("labels", [])) if isinstance(svc.get("labels"), list) else str(svc.get("labels", {}))
    assert "traefik.enable=true" not in labels, f"{name} is Traefik-routed"
'
```

Also assert control is the only service mounting the application config root
that contains `db.sqlite`; gateway may mount only the
`nzbdav/runtime/gateway` subdirectory. Assert gateway is the only service
mounting `/cache/gateway`, and provider JSON appears only in
`nzbdav-gateway.env`.

- [ ] **Step 7: Render every supported Compose path**

```bash
cd /Users/binghzal/Developer/media-stack
docker compose config --quiet
docker compose --profile '*' config --quiet
docker compose --env-file env/local-test.env.example --profile '*' config --quiet
python3 -m py_compile configs/app-provisioning/provision_media_apps.py
./scripts/validate.sh
git diff --check
```

Expected: all commands exit 0 and validation reports no published SAB/WebDAV
surface.

- [ ] **Step 8: Commit in media-stack**

```bash
cd /Users/binghzal/Developer/media-stack
git add services/00-networks.yaml services/20-gateways.yaml services/30-arrs.yaml services/90-maintenance.yaml compose.nzbdav-rollback.yaml configs/app-provisioning/provision_media_apps.py configs/homarr/provision_media_stack.py scripts/bootstrap.sh scripts/validate.sh scripts/lib.sh scripts/permissions.sh scripts/sonarr-fast-symlink-import.py scripts/nzbdav-clear-queue.sh scripts/nzbdav-missing-article-repair.sh scripts/cleanup-arr-stale-files.py scripts/split-nzbdav-secrets.sh .env.example env/local-test.env.example env/production.env.example secrets/secrets.manifest.example
git commit -m "feat: split nzbdav into private single-host roles"
```

### Task 5: Make Rclone Lifecycle Independent From NZBDav Role Restarts

**Files:**
- Create: `/Users/binghzal/Developer/media-stack/scripts/nzbdav-safe-rclone-up.py`
- Create: `/Users/binghzal/Developer/media-stack/scripts/test_nzbdav_safe_rclone_up.py`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/nzbdav-mount-heartbeat.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/restart-gateways.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/nzbdav-clean-shutdown.sh`
- Modify: `/Users/binghzal/Developer/media-stack/scripts/install-nzbdav-maintenance-timers.sh`
- Modify: `/Users/binghzal/Developer/media-stack/services/20-gateways.yaml`

**Interfaces:**
- Consumes: gateway alias `nzbdav`, `nzbdav-rclone`, rclone config file, Compose config hash, and mount health.
- Produces: idempotent rclone deployment and recovery that recreates rclone only for config change or actual mount failure.

- [ ] **Step 1: Port the proven fingerprint helper with tests**

Port `scripts/nzbdav_safe_rclone_up.py` from the NZBDav repository and retain
these decisions:

```python
def should_recreate(rendered_hash, live_hash, watched_hash, recorded_hash, healthy):
    if not healthy:
        return True, "mount-unhealthy"
    if live_hash != rendered_hash:
        return True, "compose-config-changed"
    if watched_hash != recorded_hash:
        return True, "watched-config-changed"
    return False, "unchanged-and-healthy"
```

Tests must cover healthy unchanged, compose hash change, `rclone.conf` change,
missing container, and stale mount.

- [ ] **Step 2: Run helper tests**

```bash
cd /Users/binghzal/Developer/media-stack
python3 -m unittest scripts.test_nzbdav_safe_rclone_up -v
```

Expected: all decision cases pass.

- [ ] **Step 3: Separate gateway health from mount health**

The heartbeat checks in this order:

```text
1. gateway liveness via `compose exec -T nzbdav-gateway curl -fsS http://127.0.0.1:3000/health/live`
2. mount listed in /proc/mounts
3. bounded directory stat/read succeeds
4. rclone container health
5. rendered and watched rclone fingerprints
```

If only gateway is unhealthy, restart `nzbdav-gateway` and wait for readiness;
do not stop, unmount, or recreate rclone. Only proceed to FUSE recovery when
the mount or rclone process is unhealthy.

- [ ] **Step 4: Preserve fail-closed FUSE recovery**

For a stale mount, keep this order:

```bash
stop_consumers
compose stop nzbdav-rclone
unmount_stale_mount
prepare_mountpoint
python3 scripts/nzbdav-safe-rclone-up.py --force --service nzbdav-rclone
wait_for_mount
start_consumers
```

If mount verification fails, keep consumers stopped and state `DOWN`. Do not
create an ordinary directory that Plex or ARR can scan as an empty library.

- [ ] **Step 5: Make routine restart scripts idempotent**

`restart-gateways.sh` may run `compose up -d` for the NZBDav application roles,
then must call the safe helper without `--force`. A healthy unchanged rclone
prints `unchanged; skipping docker compose up` and retains the same container
ID and mount ID.

- [ ] **Step 6: Verify lifecycle behavior**

```bash
mount_id() {
  awk -v target="$NZBDAV_MOUNT" '$5 == target { print $1; exit }' /proc/self/mountinfo
}
before_container=$(docker inspect -f '{{.Id}}' nzbdav-rclone)
before_mount=$(mount_id)
./scripts/restart-gateways.sh
test "$before_container" = "$(docker inspect -f '{{.Id}}' nzbdav-rclone)"
test "$before_mount" = "$(mount_id)"
docker restart nzbdav-gateway
test "$before_container" = "$(docker inspect -f '{{.Id}}' nzbdav-rclone)"
```

Then force-kill rclone, run heartbeat `--apply`, and assert it receives a new
container ID only after consumers are paused and the old mount is lazily
unmounted.

- [ ] **Step 7: Commit in media-stack**

```bash
cd /Users/binghzal/Developer/media-stack
git add scripts/nzbdav-safe-rclone-up.py scripts/test_nzbdav_safe_rclone_up.py scripts/nzbdav-mount-heartbeat.sh scripts/restart-gateways.sh scripts/nzbdav-clean-shutdown.sh scripts/install-nzbdav-maintenance-timers.sh services/20-gateways.yaml
git commit -m "fix: decouple rclone lifecycle from nzbdav roles"
```

### Task 6: Prove Process And Failure Isolation

**Files:**
- Create: `scripts/nzbdav_role_isolation_test.py`
- Create: `tests/test_nzbdav_role_isolation.py`
- Modify: `tests/integration/compose.role-split.yml`
- Modify: `.github/workflows/ci.yml`
- Create: `docs/operations.md`

**Interfaces:**
- Consumes: role health/status, worker leases, WebDAV range endpoint, and Compose service control.
- Produces: redacted JSON evidence for each failure semantic in the design.

- [ ] **Step 1: Unit-test the scenario evaluator**

```python
def test_worker_crash_requires_read_continuity_and_re_lease():
    result = evaluate_scenario({
        "read_errors": 0,
        "lease_generation_before": 4,
        "lease_generation_after": 5,
        "stale_completion_accepted": False,
    })
    assert result["passed"] is True

def test_gateway_failure_rejects_empty_mount():
    result = evaluate_scenario({"mount_root_entries_before": 12, "mount_root_entries_during": 0})
    assert result["passed"] is False
```

- [ ] **Step 2: Add exact scenarios to the runner**

Run each scenario from a clean fixture state:

```text
kill-download-worker: active WebDAV range read has zero errors; lease reissued
kill-verify-worker: active WebDAV range read has zero errors; verify generation increases
kill-repair-worker: download/verify continue; repair lease reissued
kill-control: existing gateway read continues; mutations return 503; workers stop at lease expiry
kill-gateway: control remains ready; rclone is not recreated; mount never becomes an empty local root
provider-config-noop: gateway pool generation and active connection IDs do not change
verify-repair-saturation: stream scheduler retains grants and download lane progresses
```

Use bounded timeouts for every probe and include timestamps, container IDs,
lease generations, mount ID, and request results in the artifact.

- [ ] **Step 3: Run isolation tests**

```bash
python3 -m unittest tests.test_nzbdav_role_isolation -v
python3 scripts/nzbdav_role_isolation_test.py \
  --compose-file tests/integration/compose.role-split.yml \
  --output artifacts/role-split/isolation.json
```

Expected: every named scenario has `passed: true`; a missing measurement is a
failed scenario, not a skipped result.

- [ ] **Step 4: Document operator-visible semantics**

`docs/operations.md` records the expected readiness, queue, mount, and playback
behavior for each failed role, plus the exact recovery command. It explicitly
states that application role restart is not a reason to recreate rclone.

- [ ] **Step 5: Commit**

```bash
git add scripts/nzbdav_role_isolation_test.py tests/test_nzbdav_role_isolation.py tests/integration/compose.role-split.yml .github/workflows/ci.yml docs/operations.md
git commit -m "test: prove nzbdav role failure isolation"
```

### Task 7: Measure Aggregate Multicore Memory And Latency

**Files:**
- Modify: `scripts/nzbdav_benchmark.py`
- Modify: `tests/test_nzbdav_benchmark.py`
- Modify: `docs/dfs-rclone-benchmark.md`
- Create: `docs/role-split-performance-gate.md`

**Interfaces:**
- Consumes: one or more `--role-pid role=pid` values, control fullstatus, rclone PID, direct WebDAV, FUSE paths, and existing benchmark scenarios.
- Produces: schema-versioned aggregate CPU/PSS/GC/latency evidence for monolith-versus-role-split comparison.

- [ ] **Step 1: Write multi-process resource tests**

```python
def test_role_processes_are_summed_once():
    sources = {
        "role:control": {"cpu_cores_max": 0.2, "pss_bytes_max": 100},
        "role:gateway": {"cpu_cores_max": 1.5, "pss_bytes_max": 500},
        "role:verify": {"cpu_cores_max": 0.8, "pss_bytes_max": 250},
        "rclone_process": {"cpu_cores_max": 0.4, "pss_bytes_max": 200},
    }
    total = nzbdav_benchmark.aggregate_stack_resources(sources)
    assert total["cpu_cores_max"] == 2.9
    assert total["pss_bytes_max"] == 1050

def test_composite_gate_requires_direct_and_fuse_evidence(self):
    with self.assertRaisesRegex(ValueError, "candidate-fuse"):
        nzbdav_benchmark.evaluate_role_split(
            baseline_http_idle=benchmark_doc(),
            baseline_http_contended=benchmark_doc(),
            baseline_fuse=benchmark_doc(),
            candidate_http_idle=benchmark_doc(),
            candidate_http_contended=benchmark_doc(),
            candidate_fuse=None,
        )
```

Also test duplicate PIDs are rejected, malformed `role=pid` values fail parser
validation, missing PSS fails the memory gate, and the existing monolith input
remains readable.

- [ ] **Step 2: Collect PSS and role identity**

Extend `process_snapshot` to read Linux `/proc/<pid>/smaps_rollup`:

```python
def read_pss_bytes(pid: int) -> int | None:
    path = Path(f"/proc/{pid}/smaps_rollup")
    if not path.exists():
        return None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("Pss:"):
            return int(line.split()[1]) * 1024
    return None
```

Record role, PID, process start time, CPU cores, RSS, PSS, and availability.
Never silently substitute RSS for a required PSS gate.
Aggregate CPU and PSS across all role/rclone processes within each timestamped
sample, then calculate stack max/p95. Do not sum each process's independent
maximum from different moments.

- [ ] **Step 3: Add repeated role process arguments**

```python
run_parser.add_argument(
    "--role-pid",
    action="append",
    default=[],
    metavar="ROLE=PID",
    help="Collect one NZBDav role process; repeat for every role container.",
)
run_parser.add_argument(
    "--workload-label",
    choices=["unspecified", "idle", "verify-repair-saturated", "parallel-read"],
    default="unspecified",
)
```

Normalize roles to `ui`, `control`, `gateway`, `download`, `verify`, and
`repair`, reject duplicate roles/PIDs, and persist them in benchmark inputs.

- [ ] **Step 4: Add a composite role-aware evaluator**

Add `evaluate-role-split` with required arguments
`--baseline-http-idle`, `--baseline-http-contended`, `--baseline-fuse`,
`--candidate-http-idle`, `--candidate-http-contended`, and `--candidate-fuse`.
It rejects mismatched paths, offsets, runs, provider/rclone configuration
fingerprints, workload labels, or missing process/status evidence.

The evaluator must require:

```text
idle direct WebDAV p95 first-byte and seek regression <= 5%
gateway p95 under verify+repair saturation <= candidate idle p95 * 1.10
contended first-byte improvement >= 20% versus monolith
aggregate CPU regression <= 10%
aggregate PSS regression <= 10%
gateway GC p99 pause does not regress
forced blocking Gen2 collections during workload = 0
worker direct NNTP connections = 0
four-file direct and FUSE wall time both improve from 11.46s and 18.67s
```

Every rule includes measured baseline, measured candidate, threshold, and a
boolean. A missing value sets the boolean false.

- [ ] **Step 5: Run benchmark unit tests**

```bash
python3 -m unittest tests.test_nzbdav_benchmark -v
```

Expected: all parser, schema, aggregation, and acceptance tests pass.

- [ ] **Step 6: Capture comparable production artifacts**

Use the same files, offsets, run count, provider config, rclone config, and host
for all six runs. Set `BENCH_PATH` to the same large Plex-indexed logical path
and capture the monolith artifacts before cutover. For contended runs, verify
`worker_queues.verify.active > 0` and `worker_queues.repair.active > 0` in the
captured status; otherwise the evaluator rejects the workload label.

```bash
python3 scripts/nzbdav_benchmark.py run \
  --scenario monolith-http-idle --transport http \
  --workload-label idle \
  --base-url "$MONOLITH_WEBDAV_URL" --path "$BENCH_PATH" \
  --nzbdav-pid "$MONOLITH_PID" \
  --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/monolith-http-idle.json

python3 scripts/nzbdav_benchmark.py run \
  --scenario monolith-http-contended --transport http \
  --workload-label verify-repair-saturated \
  --base-url "$MONOLITH_WEBDAV_URL" --path "$BENCH_PATH" \
  --nzbdav-pid "$MONOLITH_PID" --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/monolith-http-contended.json

python3 scripts/nzbdav_benchmark.py run \
  --scenario monolith-fuse --transport filesystem \
  --workload-label parallel-read \
  --mount-root /mnt/media/gateways/nzbdav --path "$BENCH_PATH" \
  --nzbdav-pid "$MONOLITH_PID" --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/monolith-fuse.json

python3 scripts/nzbdav_benchmark.py run \
  --scenario role-http-idle --transport http \
  --workload-label idle \
  --base-url "$GATEWAY_WEBDAV_URL" --path "$BENCH_PATH" \
  --role-pid ui="$UI_PID" \
  --role-pid control="$CONTROL_PID" \
  --role-pid gateway="$GATEWAY_PID" \
  --role-pid download="$DOWNLOAD_PID" \
  --role-pid verify="$VERIFY_PID" \
  --role-pid repair="$REPAIR_PID" \
  --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/role-http-idle.json

python3 scripts/nzbdav_benchmark.py run \
  --scenario role-http-contended --transport http \
  --workload-label verify-repair-saturated \
  --base-url "$GATEWAY_WEBDAV_URL" --path "$BENCH_PATH" \
  --role-pid ui="$UI_PID" --role-pid control="$CONTROL_PID" \
  --role-pid gateway="$GATEWAY_PID" --role-pid download="$DOWNLOAD_PID" \
  --role-pid verify="$VERIFY_PID" --role-pid repair="$REPAIR_PID" \
  --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/role-http-contended.json

python3 scripts/nzbdav_benchmark.py run \
  --scenario role-fuse --transport filesystem \
  --workload-label parallel-read \
  --mount-root /mnt/media/gateways/nzbdav --path "$BENCH_PATH" \
  --role-pid ui="$UI_PID" --role-pid control="$CONTROL_PID" \
  --role-pid gateway="$GATEWAY_PID" --role-pid download="$DOWNLOAD_PID" \
  --role-pid verify="$VERIFY_PID" --role-pid repair="$REPAIR_PID" \
  --rclone-pid "$RCLONE_PID" \
  --output artifacts/benchmarks/role-fuse.json

python3 scripts/nzbdav_benchmark.py evaluate-role-split \
  --baseline-http-idle artifacts/benchmarks/monolith-http-idle.json \
  --baseline-http-contended artifacts/benchmarks/monolith-http-contended.json \
  --baseline-fuse artifacts/benchmarks/monolith-fuse.json \
  --candidate-http-idle artifacts/benchmarks/role-http-idle.json \
  --candidate-http-contended artifacts/benchmarks/role-http-contended.json \
  --candidate-fuse artifacts/benchmarks/role-fuse.json \
  --gate
```

Do not accept results captured during unmatched Plex scan or metadata workloads.

- [ ] **Step 7: Keep native FUSE deferred**

Update `docs/dfs-rclone-benchmark.md` to state that role split is evaluated
with rclone first. A future native FUSE sidecar may use gateway manifest and
`ReadFileRange` RPCs, but it is not deployed or accepted by this plan.

- [ ] **Step 8: Commit**

```bash
git add scripts/nzbdav_benchmark.py tests/test_nzbdav_benchmark.py docs/dfs-rclone-benchmark.md docs/role-split-performance-gate.md
git commit -m "perf: gate aggregate role-split resources and latency"
```

### Task 8: Add Release Gates And A Reversible Production Cutover

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/pre-release.yml`
- Modify: `.github/workflows/ghcr-release.yml`
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `docs/setup-guide.md`
- Create: `docs/role-split-rollout.md`
- Create: `/Users/binghzal/Developer/media-stack/scripts/deploy-nzbdav-role-split.sh`
- Modify: `/Users/binghzal/Developer/media-stack/docs/usenet-vfs-strategy.md`

**Interfaces:**
- Consumes: all test, Compose, isolation, migration, vulnerability, and performance gates.
- Produces: one GHCR image tagged `beta`, `latest`, and `sha-<commit>`, plus phased deploy and rollback commands.

- [ ] **Step 1: Extend CI without removing existing gates**

After existing backend/frontend/Playwright/vulnerability/migration tests, add:

```yaml
- name: Test role entrypoint
  run: python3 -m unittest tests.test_entrypoint_roles -v
- name: Test role Compose contract
  run: python3 -m unittest tests.test_role_split_compose -v
- name: Run role Compose smoke
  run: python3 scripts/test_role_split_compose.py --compose-file tests/integration/compose.role-split.yml --output artifacts/role-split/ci.json
- name: Upload role evidence
  uses: actions/upload-artifact@v4
  with:
    name: nzbdav-role-split
    path: artifacts/role-split/
```

Release workflows must depend on the same gate and continue publishing exactly
`beta`, `latest`, and `sha-${GITHUB_SHA}` from the single image.

- [ ] **Step 2: Implement phased deployment**

`deploy-nzbdav-role-split.sh` supports:

```text
--phase gateway   run rollback-profile all as control/local workers plus external gateway
--phase verify    restart all with external lane verify and start verify worker
--phase download  restart all with external lanes verify,download and start download worker
--phase repair    restart all with all three external lanes and start repair worker
--phase ui        replace all with control+ui; keep gateway/workers and switch Traefik
--phase full      make all separated roles authoritative
--rollback        drain leases and restore updated all role
```

Every phase renders Compose, verifies required secrets, records current image
digest/container IDs/mount ID, performs health checks, and writes a redacted
manifest beneath `${INVENTORY_ROOT}/nzbdav-role-split/`.
Restarting `nzbdav-all` during lane changes must not call `compose up` for
`nzbdav-rclone`; the safe helper verifies and skips the unchanged mount.

- [ ] **Step 3: Encode rollback ordering**

The script's rollback path must execute:

```text
1. ask control to stop issuing leases
2. wait until active leases are zero or their two-minute expiry passes
3. stop role workers
4. ask gateway to drain providers
5. start updated all role through `compose.nzbdav-rollback.yaml` with `nzbdav`
   and `nzbdav-sab` aliases
6. verify SAB, WebDAV, provider, cache, and mount health
7. stop separated control and gateway
```

It does not downgrade the database, rewrite rclone config, recreate healthy
rclone, or restore an older application image that cannot read additive schema.

- [ ] **Step 4: Run the complete repository gate**

```bash
dotnet test backend.Tests/backend.Tests.csproj
dotnet build backend/NzbWebDAV.csproj --no-restore
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run build:server
npm --prefix frontend run test:e2e
python3 -m unittest discover -s tests -v
docker build -t nzbdav:release-gate .
python3 scripts/test_role_split_compose.py --compose-file tests/integration/compose.role-split.yml --output artifacts/role-split/release.json
git diff --check
```

Expected: every command exits 0 and both role-split artifacts pass.

- [ ] **Step 5: Validate media-stack and safe-rclone behavior**

```bash
cd /Users/binghzal/Developer/media-stack
docker compose config --quiet
docker compose --profile '*' config --quiet
docker compose --env-file env/local-test.env.example --profile '*' config --quiet
python3 -m unittest scripts.test_nzbdav_safe_rclone_up -v
./scripts/validate.sh
git diff --check
```

Expected: all commands exit 0, only UI has NZBDav Traefik labels, and no backend
role publishes a host port.

- [ ] **Step 6: Perform production report-only canary phases**

Run `gateway`, `verify`, `download`, `repair`, and `ui` one at a time. At every
phase, capture role health, queue state, ARR report-mode validation, mount ID,
rclone container ID, provider pool generation, and benchmark evidence. Do not
advance when any correctness/isolation rule fails or aggregate CPU/PSS exceeds
the accepted threshold. Before `full`, import one controlled Sonarr item and
one controlled Radarr item through the normal copy-plus-unlink path; verify the
library destinations remain symlinks, the source DELETE is idempotent, receipts
reach `Imported`, and neither ARR repeats the grab.

- [ ] **Step 7: Update docs and changelog**

Document stable aliases, private SAB/WebDAV policy, secret ownership, role
health ports, artifact exchange, rclone lifecycle, canary phases, rollback, and
the reason DFS remains deferred. Avoid adding new operator tuning knobs.

- [ ] **Step 8: Commit each repository**

```bash
cd /Users/binghzal/Developer/nzbdav
git add .github/workflows CHANGELOG.md README.md docs/setup-guide.md docs/role-split-rollout.md
git commit -m "ci: gate and document role-separated releases"

cd /Users/binghzal/Developer/media-stack
git add scripts/deploy-nzbdav-role-split.sh docs/usenet-vfs-strategy.md
git commit -m "ops: add reversible nzbdav role rollout"
```

## Completion Gate

This plan is complete only when:

- only UI is externally routed and its proxy rejects non-allowlisted paths and methods;
- ARR reaches SAB only at `nzbdav-sab:3000` on the private bridge;
- rclone still reaches WebDAV at `http://nzbdav:3000/` with no config rewrite;
- control is the only database owner, gateway is the only provider/cache owner, and workers have neither credential class;
- worker/control/gateway crash scenarios pass without an empty mount or unrelated process failure;
- restarting any NZBDav application role leaves a healthy unchanged rclone container and mount untouched;
- aggregate CPU, PSS, GC, direct-WebDAV, and FUSE performance gates pass on the production host;
- all repository and media-stack gates pass;
- the role split has a tested rollback to the updated `all` role;
- one GHCR image continues to publish `beta`, `latest`, and immutable SHA tags.
