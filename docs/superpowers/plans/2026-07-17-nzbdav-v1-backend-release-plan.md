# NZBDav V1 Backend Release Implementation Plan

**Status:** ACTIVE

**Last reconciled:** 2026-07-17

**Canonical handoff:** [`HANDOFF.md`](../../../HANDOFF.md)

**Governing design:**
[`2026-07-17-nzbdav-v1-backend-release-design.md`](../specs/2026-07-17-nzbdav-v1-backend-release-design.md)

## Goal

Produce one publishable, clean-install-only NZBDav V1 container using SQLite,
one control owner, and `role=all`; prove backend behavior before beginning the
separately planned full rebrand.

## Frozen release boundary

- Supported: fresh Docker install, SQLite, one owner, `role=all`, amd64/arm64.
- Unsupported: every pre-V1 in-place upgrade and in-place downgrade.
- Private/post-V1: PostgreSQL, Transfer-v3 Phase 4, split roles, multi-owner,
  multi-host, and alternate packaging.
- Full rebrand is blocked until Task 7 records the backend freeze.
- Only disposable, uniquely owned fixtures may be used for tests.
- Production access is read-only diagnosis/canary only after disposable gates;
  never use production for migration, repair, stress, or fault injection.
- Preserve the shared dirty worktree. Never reset, clean, restore, checkout,
  stash, stage, commit, push, or delete unexplained files without explicit user
  authorization.

## Current evidence baseline

Evidence below was executed in the current dirty worktree and is not final
release provenance.

| Gate | Current result |
| --- | --- |
| Backend/test Release builds with warnings as errors | PASS |
| Disposable SQLite migration smoke | PASS |
| Backend broad non-live suite | 2,820 passed, 84 deliberate PostgreSQL-only skips, 0 failed |
| Usenet controller class | 2/2 PASS; recursive test logger ownership fixed |
| Transfer-v3 Task 8 focused gate | 26/26 passed, but independent review found a P1 pre-budget catalog materialization path; deferred post-V1 |
| Frontend typecheck, unit, client build, server build | PASS |
| Frontend Playwright | 5/5 PASS |
| npm audit, moderate threshold | 0 vulnerabilities |
| NuGet vulnerable-package review | No known vulnerable package reported in current scan |
| Entrypoint shell contract | PASS |
| Docker image build | PASS for local `nzbdav:entrypoint-smoke` |
| Entrypoint container smoke | PASS |
| `git diff --check` | PASS at last execution |

## Task status

| Task | Status | Exit criterion |
| --- | --- | --- |
| 0. Freeze V1 contract and pivot documentation | COMPLETE | New design/plan govern continuation; Phase 4 is explicitly deferred; handoff and `AGENTS.md` point here. |
| 1. Restore deterministic backend test truth | COMPLETE | No testhost crash; cleanup/visibility contract is correctly classified and green; complete suite runs normally. |
| 2. Secure sessions, proxying, errors, and logs | IN PROGRESS | Stable session policy, exact route authentication, bounded sanitized responses, and canary-negative tests pass. |
| 3. Add real liveness/readiness | NOT STARTED | Core readiness has fault/recovery coverage and entrypoint waits on it. |
| 4. Own repair and shutdown lifecycle | NOT STARTED | No detached persistence mutation; bounded host-owned execution and shutdown tests pass. |
| 5. Make rclone safe-up fail closed | NOT STARTED | Full unchanged/changed/unhealthy/mount/canary/state fault matrix passes. |
| 6. Prove SQLite crash consistency and runtime resilience | NOT STARTED | Fault injection, restart, integrity, streaming, queue, cleanup, and invalidation stress gates pass. |
| 7. Freeze backend | NOT STARTED | Three complete suites, repeated affected gates, clean builds, security scans, independent review, and zero P0/P1. |
| 8. Add behavioral end-user E2E | NOT STARTED | Six required journeys pass without visual redesign. |
| 9. Harden release artifact and provenance | NOT STARTED | Exact multi-arch digest, labels, SBOM, attestation, clean-install/recovery docs, and CI parity pass. |
| 10. Verify V1 release candidate | NOT STARTED | Two fresh acceptance runs, bounded soak, final council, and evidence bundle approve publication. |
| 11. Begin full rebrand | BLOCKED | May be planned only after Task 10 is green and user reviews the backend pass report. |

## Global implementation discipline

Every behavior repair follows this order:

1. Re-read target source and usages; verify the dirty-file diff and concurrent
   worktree state.
2. Add one focused failing test proving the intended contract.
3. Run the narrow test and record the expected failure.
4. Implement the minimum complete behavior.
5. Run the narrow gate, affected regression, and complete project gate.
6. Run Release builds with warnings as errors, format verification, and
   `git diff --check`.
7. Obtain independent read-only review for security/concurrency/lifecycle work.
8. Update this plan and `HANDOFF.md` with real results before moving on.

No timing inflation, retry-to-green, assertion weakening, class exclusion,
analyzer suppression, or fake service response can satisfy a gate.

---

## Task 0: Freeze V1 contract and pivot documentation

**Files:**

- Create: `docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md`
- Create: `docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md`
- Modify: `AGENTS.md`
- Modify: `HANDOFF.md`
- Modify: `docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md`

**Steps:**

- [x] Freeze SQLite, one owner, `role=all`, and Docker-first V1.
- [x] Record user choice: V1 is clean-install-only; upgrades are post-V1.
- [x] Record user choice: full rebrand waits until the backend fully passes.
- [x] Run six-seat architecture/release/security/product/scope/adversarial
  council.
- [x] Record unanimous council result: topology accepted, current candidate
  NO-GO.
- [x] Create the governing design and this implementation plan.
- [x] Point `AGENTS.md` at this plan/design.
- [x] Replace the first continuation action in `HANDOFF.md` with Task 1 below.
- [x] Mark Transfer-v3 Phase 4 as deferred post-V1 and record the P1 review
  blocker without changing its private code.
- [x] Run Markdown link/path validation and `git diff --check`.

**Acceptance:** Documentation contains one active plan and one governing design;
no text represents Task 8 as sealed or PostgreSQL as V1-supported.

---

## Task 1: Restore deterministic backend test truth

### 1A. Fix the recursive Usenet test logger

**Files:**

- Modify: `backend.Tests/Api/TestUsenetConnectionControllerTests.cs`
- Production source only if a newly passing redaction test proves a runtime
  defect: `backend/Api/Controllers/TestUsenetConnection/TestUsenetConnectionController.cs`

**RED:**

- Reproduce the stack overflow with:

  `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~TestUsenetConnectionControllerTests' --logger 'console;verbosity=minimal'`

- Add/retain tests for success disposal, hostile login rejection, password and
  `AUTHINFO PASS` redaction, timeout, cancellation, and disposal.

**GREEN:**

- Separate the non-disposable collecting sink from the logger-owning disposable
  scope. Restore `Log.Logger` before disposing only the created logger.
- Do not use `Log.CloseAndFlush()` against shared global test infrastructure.
- Verify isolated class and whole-suite execution exit normally.

### 1B. Reconcile cleanup and whole-cache visibility semantics

**Files:**

- Test: `backend.Tests/Tasks/RemoveUnlinkedFilesTaskTests.cs`
- Test: `backend.Tests/Database/RcloneInvalidationTests.cs`
- Inspect/modify only as proven:
  - `backend/Tasks/RemoveUnlinkedFilesTask.cs`
  - `backend/Database/Interceptors/ContentIndexSnapshotInterceptor.cs`
  - `backend/Database/DavDatabaseContext.cs`
  - `backend/Services/RcloneInvalidationService.cs`
  - `backend/Clients/Rclone/RcloneClient.cs`

**RED and diagnosis:**

- Reproduce the three exact failures individually.
- Trace the transaction that deletes DAV rows, writes per-path invalidations,
  and/or publishes the whole-cache sentinel.
- Prove whether the sentinel intentionally subsumes narrower paths or whether
  path loss violates a consumer contract.
- Add a semantic test for the selected invariant rather than asserting an
  implementation detail.
- Add concurrent republish/acknowledgement and restart tests.

**GREEN:**

- If the sentinel is authoritative, update stale tests to require the sentinel,
  absence of redundant paths, retained revision, and exact acknowledgement.
- If per-path rows remain required, fix the transaction and retain atomic
  deletion plus invalidation.
- Do not change production code unless the executable invariant proves it
  wrong.

### Task 1 gates

- [x] Usenet class passes with no crash and no secret canary.
- [x] All cleanup/invalidation focused tests pass.
- [x] Every regression has a pre-fix failing result and post-fix passing result.
- [x] Complete backend suite passes with zero failure/crash and no ad hoc
  exclusion.
- [x] Clean test build passes with warnings as errors.
- [x] Repeat affected classes ten times with zero flake.
- [x] Record exact pass/skip counts and classify every skip.

**Current completion evidence (2026-07-17):** the focused Usenet controller
gate passed 4/4; the affected Usenet/cleanup/invalidation regression passed
85 with one deliberate PostgreSQL-only skip; the timing-sensitive controller,
cleanup, and invalidation subset passed ten consecutive audited iterations at
59 passed and one deliberate PostgreSQL-only skip per iteration; the complete
hermetic Release backend assembly passed 2,824 with 84 deliberate
PostgreSQL-only skips and zero failures; production and test Release builds
passed with warnings as errors; scoped format verification and
`git diff --check` passed. The stalled-greeting tests use a disposable loopback
NNTP peer and prove both bounded timeout and caller-cancellation socket disposal.

---

## Task 2: Secure sessions, proxying, errors, and logs

### 2A. Authentication-mode and session startup contract — complete

- [x] User selected full Authentik Proxy Provider mode: the Authentik outpost
  directly reverse-proxies browser traffic to internal nzbdav. Separate
  Nginx/Traefik forward-auth mode is out of scope for V1.

**Files:**

- Modify: `frontend/app/auth/authentication.server.ts`
- Add: `frontend/app/auth/authentication.server.test.ts`
- Modify: `frontend/app/auth/auth-middleware.server.ts`
- Modify/remove local-login routes only when the explicit Authentik mode is
  active; retain local login for the default local mode.
- Modify: `.env.example`
- Modify setup/deployment documentation identified during implementation.

**RED:** Tests prove `AUTH_MODE=local` is the only default, rejects absent or
non-64-hex `SESSION_KEY`, requires explicit secure-cookie policy, preserves
sessions across restart with the same key, rejects malformed/old cookies, and
accepts exactly one valid `SESSION_KEY_PREVIOUS` during controlled rotation.
Tests also prove explicit `AUTH_MODE=authentik-proxy` disables local login,
rejects missing/untrusted/conflicting/wrong-application Authentik identity, and
never falls back to local auth when the outpost is unavailable.

**Current RED evidence (2026-07-17):**
`npm test -- app/auth/authentication.server.test.ts --reporter=verbose` reported
14 expected failures and one pass against the pre-hardening implementation.
Failures cover the random-key fallback, malformed and missing key acceptance,
insecure-cookie default, missing rotation support, absent Authentik startup and
identity validation, retained local sessions in Authentik mode, and the legacy
authentication-disable bypass.

**GREEN:** Remove the random local-session fallback. Sign with the current
32-byte hex key and verify with current plus optional previous key. Make secure
cookies the documented default and require `ALLOW_INSECURE_COOKIES=true` for
direct HTTP development. In Authentik mode, accept identity only from required
trusted outpost CIDRs with expected application metadata, emit no secret
values, and require internal-only application ingress.

**Completion evidence (2026-07-17):** the authentication and middleware matrix
passed 24/24; the complete frontend unit suite passed 158/158; type generation,
TypeScript compilation, client production build, and server production build
passed; Playwright passed 5/5 through the real local login route with no auth
bypass. A rebuilt disposable `nzbdav:entrypoint-smoke` image proved that local
mode without `SESSION_KEY` exits nonzero with the exact startup diagnostic and
that the same image becomes healthy and stops cleanly with an explicit
synthetic 64-hex key. Shell syntax validation passed. `.env.example` documents
both supported modes, rotation, secure-cookie policy, trusted outpost CIDRs,
and the no-direct-ingress requirement. The current CI workflow does not invoke
`tests/test_entrypoint_container.sh`; close that parity gap in the release
workflow task rather than treating local smoke evidence as CI evidence.

### 2B. Exact proxy boundary

**Verified topology decision (2026-07-17):** Use the existing frontend listener
and hostname with a dedicated mount-relative `/protocol` client-ingress prefix.
The browser UI, UI-admin API calls, and `/ws` remain outside that prefix and
require the selected local or Authentik principal. `/protocol` is the only
candidate Authentik unauthenticated-path exception; it retains independent SAB
API-key, WebDAV Basic-auth, and signed-media credentials and never receives
`FRONTEND_BACKEND_API_KEY`. Backend port 8080 remains private. Sonarr/Radarr URL
Base, rclone WebDAV base, and current AIOStreams base-URL construction all
support this shape.

**Verified public API-key decision (2026-07-17):** The `/protocol` operator key
authorizes the complete existing SAB-compatible dispatcher, read-only ARR
validation/search-nudge/correlation reports, and `POST` event ingestion for
`sonarr`, `radarr`, and `lidarr`. ARR retry, clear, correlation upsert/delete,
general config, maintenance, database, and every other admin route remain
UI-principal-only.

**Verified WebDAV decision (2026-07-17):** Use path-scoped semantic writes.
Allow `OPTIONS`, `PROPFIND`, `GET`, and `HEAD` on exact WebDAV namespaces;
allow `PUT` only below `/protocol/nzbs/<category>/...`; allow `DELETE` only
below `/protocol/nzbs`, `/protocol/content`, and
`/protocol/completed-symlinks`. This preserves NZB watch-folder ingest, queue
removal, operator-enabled content deletion, and completed-import
acknowledgement. Reject `COPY`, `MOVE`, `MKCOL`, `PROPPATCH`, `LOCK`, `UNLOCK`,
and every other unapproved method before proxying. If disposable real-client
evidence proves a blocked protocol method is required, return for explicit
approval before widening the allowlist.

V1 does not support unauthenticated WebDAV. `DISABLE_WEBDAV_AUTH=true` now fails
startup with an actionable diagnostic, NWebDav authentication is unconditionally
required, and the bypass branches have been removed. Focused auth tests pass
`8/8`; Release warning-as-error build and edited-file formatter checks exit
zero. Keep the combined backend regression pending until the Task 2B key-parser
slice lands, then rerun once across both changes.

**Verified import and signed-media decision (2026-07-17):** V1 is hard
symlink-only. Remove the `strm` and `both` import settings and every STRM
generation, recreation, and conversion surface from the clean-install V1
runtime/UI. Plex/ARR use `/completed-symlinks` targeting mounted `/.ids`;
AIOStreams continues through independently authenticated WebDAV `/content`;
Dav Explore preview/download remains frontend-principal-protected at `/view`.
Do not expose any `/protocol/view` route.

Treat symlink-only removal as its own RED-GREEN slice. Inventory and remove the
STRM config/default/secret seed, queue post-processor, maintenance kinds and
controllers, UI options/actions, utility branches, tests, and documentation.
Update clean-install migration/baseline evidence rather than retaining dead V1
compatibility stubs; pre-V1 upgrade compatibility is outside the frozen V1
contract. Preserve the shared symlink and `/.ids` implementation and prove ARR
completed-path behavior, rclone `--links`, DFS symlink resolution, cleanup, and
organized-link discovery still pass.

**Remaining unresolved product contract; do not implement production proxy
changes until resolved:**

- the exact supported API-key carriers. Current backend evidence supports
  `x-api-key` and lowercase `apikey` query/form values, while the frontend also
  recognizes unsupported camel-case `apiKey`; conflicting or duplicate carriers
  must fail closed.

**Files:**

- Modify: `frontend/server/app.ts`
- Add/modify server route tests beside `frontend/server/*.test.ts`
- Inspect every backend route before defining the allowlist.

**RED:** Build a route/method matrix for unauthenticated and locally or
Authentik-authenticated UI-admin, WebDAV, content, SAB-compatible, WebSocket,
and health paths. Include untrusted proxy sources, wrong Authentik application
metadata, encoded separator, double encoding, prefix confusion,
conflicting/client-supplied API keys, and oversized headers.

The matrix must also cover exact `/protocol` prefix stripping under empty,
single, and nested `URL_BASE`; WebDAV `Destination`/resource-tag rewriting;
signed `GET`/`HEAD` range requests; exact `/ws` upgrade-path authentication;
stable 400/405 responses for malformed paths/methods; zero upstream calls for
unknown admin routes; and proof that Authentik unauthenticated-path handling
preserves WebDAV `Authorization` without adding browser authority.

The scoped public-API positives are exact `/protocol/api` `GET`/`POST`, exact
`GET` routes for `/protocol/api/arr/validation`,
`/protocol/api/arr/search-nudges`, and `/protocol/api/arr/correlations`, plus
exact `POST /protocol/api/arr/events/{sonarr|radarr|lidarr}`. Every other
`/protocol/api/*` path or unsupported method is a zero-upstream-call negative.

WebDAV positives and negatives must be generated from the approved semantic
method/path table, not from NWebDav's complete registered handler set. Exercise
real disposable rclone listing, range/seek, NZB upload, queue removal,
operator-enabled content deletion, and completed-symlink import acknowledgement
before acceptance.

No `/protocol/view` case is positive. Any such request must make zero upstream
calls. Browser `/view` `GET`/`HEAD` range behavior remains covered by the
frontend-principal matrix and must not be reachable through an Authentik
unauthenticated-path exception.

**GREEN:** Require a principal from the explicitly selected authentication mode
before admin proxying, strip client internal keys, inject the configured key
only server-side, and preserve independently authenticated WebDAV/SAB routes
through exact allowlists.

**Confirmed adjacent defect:** `scripts/nzbdav_arr_report_validation.py` sends
the documented public `NZBDAV_API_KEY` to `/api/get-config`, but
`BaseApiController` accepts only `FRONTEND_BACKEND_API_KEY`. Existing Python
tests validate parsing/redaction rather than live authorization. Do not expose
the generic config controller to the public key. Add RED service/script tests,
return only the required non-secret app-type/search-mode/duplicate-policy fields
from `/api/arr/validation`, remove the helper's general-config request, and run
the focused .NET/Python gates before adding that route to `/protocol`.

**Implementation status (2026-07-17):** GREEN locally; independent review is
pending. `ArrValidationResponse` now returns only configured app kinds,
normalized search-nudge mode, and normalized duplicate policy. The helper no
longer references `/api/get-config`. Verified focused service `1/1`, focused
Python `7/7`, full `ArrOperationsServiceTests` `27/27`, all Python `110/110`,
and complete backend Release `2,825` passed with `84` expected PostgreSQL skips.
Release warning-as-error build, edited-file formatter checks, Python compile,
documentation validation, and `git diff --check` all exited zero. Do not mark
this slice sealed until the independent review report is read and any material
finding is resolved.

### 2C. Public error and log contract

**Files:**

- Modify: `backend/Api/Controllers/BaseApiController.cs`
- Inspect/modify: `backend/Api/SabControllers/SabApiController.cs`
- Modify: `backend/Middlewares/ExceptionMiddleware.cs`
- Modify: `backend.Tests/Middlewares/ExceptionMiddlewareTests.cs`
- Add focused controller error-envelope tests in `backend.Tests/Api/`.
- Modify as needed: `frontend/app/clients/backend-client.server.ts` and its test.

**RED:** Inject credential/path/URL/SQL/provider/body/CRLF/control-character and
oversized canaries through 400/401/500 paths. Assert canary absence everywhere.

**GREEN:** Return stable bounded error code, safe message, and correlation ID.
Keep only redacted structured detail in logs. Bound frontend display and log
capture.

### Task 2 gates

- [x] Session matrix passes.
- [ ] Proxy authorization/header matrix passes.
- [ ] Error and log canary matrix passes.
- [ ] Frontend and backend full test/build gates remain green.
- [ ] Independent security review reports no P0/P1.

---

## Task 3: Add real liveness and readiness

**Files:**

- Modify: `backend/Program.cs`
- Add health-check implementations under `backend/Services/` or a verified
  neighbouring health-check directory.
- Modify/add: `backend.Tests/Api/HealthCheckRequestTests.cs`
- Modify: `entrypoint.sh`
- Modify: `tests/test_entrypoint_contract.sh`
- Modify: `tests/test_entrypoint_container.sh`
- Modify frontend health proxy/tests only if endpoint contracts require it.

**RED:** Prove current `/health` remains green when SQLite/config/blob roots are
unusable. Add independent liveness and core-readiness tests for healthy, failed,
and recovered states.

**GREEN:**

- Liveness is process-only.
- Core readiness verifies config loaded, schema current, bounded SQLite read,
  required local path contract, critical hosted-service initialization, and
  non-draining state.
- External Usenet/ARR/rclone failures remain degraded diagnostics.
- Entrypoint waits on core readiness and exits nonzero if backend dies or the
  deadline expires.

**Gates:** endpoint tests, shell contract, container smoke, migration failure,
read-only config, child death, and recovery all pass.

---

## Task 4: Own repair and shutdown lifecycle

**Files:**

- Modify: `backend/Middlewares/ExceptionMiddleware.cs`
- Inspect/modify the existing durable `WorkerJob` and repair coordination paths
  before adding any new abstraction.
- Add focused lifecycle tests in `backend.Tests/Middlewares/` and existing
  repair/worker test suites.

**RED:** Flood distinct missing items, inject database failure, cancel shutdown,
and assert current detached work is not durably owned.

**GREEN:** Route dynamic repair intent through a bounded host-owned/durable
queue with deduplication, observed errors, retry policy, startup recovery,
shutdown cancellation, and terminal status. No request path reports accepted
unless intent is durable.

**Gates:** bounded flood, dedupe, restart recovery, cancellation, drain timeout,
and secret-redaction tests pass; no unobserved task exception appears.

---

## Task 5: Make rclone safe-up fail closed

**Files:**

- Modify: `scripts/nzbdav_safe_rclone_up.py`
- Modify: `tests/test_nzbdav_safe_rclone_up.py`
- Modify operational documentation that invokes the script.

**RED matrix:**

- unchanged healthy target;
- unchanged missing/stopped/restarting/unhealthy target;
- missing mount or failed traversal/read canary;
- changed input;
- ambiguous/malformed inspect output;
- config-hash mismatch and stale container start;
- Compose failure or delayed crash;
- failed RC/WebDAV canary;
- state-write failure.

**GREEN:** Always inspect and verify. Recreate only when necessary. Repeat all
postconditions after Compose. Atomically save only after success. Preserve last
known-good state and exit nonzero on failure.

**Gates:** Python unit suite passes repeatedly; a disposable Compose fixture
proves real Docker behavior without production resources.

---

## Task 6: Prove SQLite crash consistency and runtime resilience

**Test domains:**

- database migration and integrity;
- queue/worker leases and recovery;
- cleanup plus visibility acknowledgement;
- streaming/range/seek and cache pressure;
- Usenet timeout, disconnect, partial article, and cancellation;
- controlled shutdown and restart.

**Fault matrix:**

- `SQLITE_BUSY` and concurrent writers;
- read-only root and unavailable blob/cache root;
- bounded disk-full simulation in an owned fixture;
- cancellation before/after transaction commit;
- process termination at reviewed boundaries;
- WAL/SHM restart and `PRAGMA integrity_check`;
- SIGTERM during active stream, cleanup, repair, and invalidation;
- rclone timeout/malformed acknowledgement and republish race;
- provider DNS/TLS/auth/header/body timeout and partial data.

**Acceptance:** No corruption, false completion, stale acknowledgement deletion,
unbounded retry, leaked handle, unobserved task, secret leak, or orphan worker.
Every failure is recoverable or explicitly quarantined with stable evidence.

---

## Task 7: Freeze backend

Run on the same reviewed worktree state:

1. three consecutive complete backend suites;
2. ten consecutive affected cleanup/invalidation/concurrency suites;
3. production and test Release builds with `--no-incremental -warnaserror`;
4. formatter verification and `git diff --check`;
5. dependency vulnerability scans;
6. SQLite migration/integrity/fault matrix;
7. entrypoint and rclone operational matrix;
8. session/proxy/error/readiness/lifecycle security matrix;
9. two independent read-only reviews, including security/concurrency.

**Acceptance:** Zero failure/crash/flaky rerun, no P0/P1, and no unexplained
skip. Record accepted P2/P3 and residual risk. Update `HANDOFF.md` with exact
commands and results. Present the backend pass report to the user. Do not begin
visual rebrand yet.

---

## Task 8: Add behavioral end-user E2E without redesign

**Files:**

- Extend `frontend/test/mock-backend.ts` only with behavior faithful to reviewed
  backend contracts.
- Add focused Playwright specs under `frontend/e2e/`.
- Add route/unit tests beside existing route tests.

Cover the six journeys in the governing design: clean install/login,
configuration/redaction, submit-to-completion, content consumption,
diagnostics/recovery, and restart/config update.

Existing 5/5 Playwright coverage remains required but is not sufficient.
Visual rebrand, spacing, icon sizing, typography, and broad responsive polish
remain deferred.

---

## Task 9: Harden release artifact and provenance

**Files:**

- Modify: `Dockerfile`
- Modify reviewed release workflows under `.github/workflows/`
- Modify: `README.md`, `.env.example`, and setup documentation.
- Add release-evidence tooling only after inspecting existing scripts/workflows.

**Requirements:**

- exact amd64/arm64 platform and manifest digests;
- OCI source, revision, version, license, and created labels;
- SBOM and signed provenance/attestation;
- one build used for validation and publication;
- clean-install-only statement, unsupported upgrade/downgrade statement,
  reverse-proxy/session requirements, backup/recovery drill;
- explicit rejection of PostgreSQL and split roles;
- CI runs the same commands as local acceptance.

---

## Task 10: Verify V1 release candidate

Against the exact immutable candidate digest:

1. Fresh acceptance run A from empty disposable roots.
2. Stop/restart, session persistence, queue recovery, WebDAV read/seek, cleanup,
   visibility, and integrity checks.
3. Fresh acceptance run B from independent empty roots.
4. Thirty-minute bounded soak with five-second health sampling, synthetic
   queue/stream activity, and resource/leak monitoring.
5. Verify amd64 and arm64 behavior.
6. Verify evidence manifest, SBOM, attestation, source revision, and digest.
7. Run final six-seat read-only release council.

**GO requires:** unanimous absence of P0/P1, every objective gate green, exact
artifact identity, documented residual risk, and explicit user publication
authorization. No tag, commit, push, release, or `latest` mutation occurs without
separate user authorization.

---

## Task 11: Begin full rebrand

**Status:** BLOCKED

After Task 10, present the complete backend verification evidence and release
risk report to the user. Only then use the official Figma MCP to inspect the
existing
[Pinrail — Product Design / Wireframes file](https://www.figma.com/design/WxDUx3FJ9iINrtXn2GmkZC/Pinrail-%E2%80%94-Product-Design---Wireframes?m=auto&t=YBuWaX8ZhLg6aYZd-6).

The Figma file is the mandatory source of truth for the redesign. Ask before
creating a new page/section if the file has no existing NZBDav destination.
Create and review every route, component, state, accessibility behavior, and
responsive contract in Figma first; obtain user approval there before changing
user-facing frontend or rebrand implementation. Do not substitute Pencil,
OpenPencil, code-first mockups, or another design tool without explicit user
approval.
