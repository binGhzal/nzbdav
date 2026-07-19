# NZBDav V1 Backend Release Implementation Plan

**Status:** ACTIVE

**Last reconciled:** 2026-07-19

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
- V1 mount delivery uses the rclone sidecar contract. Any privileged
  in-container DFS/FUSE prototype remains post-gate and is not covered by a
  dynamic-user exception for the combined application image.
- Full rebrand remains blocked through Task 10. It may begin only after the
  release-candidate GO evidence is complete and the user reviews the backend
  pass/risk report.
- Only disposable, uniquely owned fixtures may be used for tests.
- Production access is read-only diagnosis/canary only after disposable gates;
  never use production for migration, repair, stress, or fault injection.
- Preserve the shared dirty worktree. Never reset, clean, restore, checkout,
  stash, stage, commit, push, or delete unexplained files without explicit user
  authorization.

## Consolidation state

The formerly dirty implementation is preserved in signed checkpoint
`fb03b0e6a247dfeaff9e9965f045a1fb1e6a11cc` on
`pinrail/v1-backend-wip`. This is a continuation branch only. Two independent
reviews found no P0 and multiple reachable P1 blockers. Do not merge it to
`main`, deploy it, publish an image, or describe it as V1-ready.

All GHCR publication paths are disabled while V1 is NO-GO. Branch, Dependabot,
main, and tag workflows run the reusable read-only verifier only. Reintroduce
publication in Task 9 only after one immutable multi-arch artifact is built,
verified by digest, attested, and explicitly authorized.

## Current evidence baseline

Evidence below was executed against the checkpoint source and is not final
release provenance.

| Gate | Current result |
| --- | --- |
| Backend/test Release builds with warnings as errors | PASS |
| Disposable SQLite migration smoke | PASS |
| Complete backend with required PostgreSQL integration | 2,966 passed, 0 skipped, 0 failed |
| Pinned Alpine musl PostgreSQL integration | 942 passed, 0 skipped, 0 failed |
| Usenet controller class | 2/2 PASS; recursive test logger ownership fixed |
| Transfer-v3 Task 8 focused gate | 26/26 passed, but independent review found a P1 pre-budget catalog materialization path; deferred post-V1 |
| Frontend typecheck, unit, client build, server build | PASS |
| Frontend Playwright | 5/5 PASS |
| npm audit, moderate threshold | 0 vulnerabilities in the fresh Task 2 checkpoint scan |
| NuGet vulnerable-package review | No vulnerable direct or transitive package reported for either `backend/NzbWebDAV.csproj` or `backend.Tests/backend.Tests.csproj` |
| Entrypoint shell contract | PASS |
| Python tooling | 110 tests passed |
| Docker image build and exact runtime assertions | PASS for local evidence image only |
| Entrypoint container smoke | PASS |
| `git diff --check` | PASS at last execution |
| Task 2 hermetic backend Release suite | 3,071 total: 2,986 passed, 85 deliberate PostgreSQL-only skips, 0 failed |
| Task 2 frontend unit and Playwright | 50 files/928 unit and 5/5 Playwright PASS |
| Task 2 Python tooling | 157/157 PASS; Task 2E focused checkpoint was 155/155 before two additive container-release tests |
| Task 2 local image/container gate | BLOCKED: local Docker lacks buildx; exact remote CI required |
| Task 2 Trivy | PASS: npm vulnerabilities 0 and Dockerfile misconfigurations 0 after reviewed identity hardening and one exact justified root-Dockerfile exception |

The 2026-07-19 Task 2 checkpoint wires the exact production HTTP/WebSocket
proxy, removes reachable STRM surfaces, enforces the symlink-only clean-install
contract, binds the backend to loopback, closes public failure/log/persistence
canaries, preflights internal/session credentials, freezes the canonical
external protocol/operator contract, and closes the reviewed container-user
boundary. Focused code, operator, and container reviews are green. Graphify
0.9.19 rebuilt the final AST-only graph at 15,770 nodes, 41,044 edges, and 728
communities; external graph update and query/path/explain canaries passed. An
independent combined-diff security review is `0/0/0/0`. The formal Codex
Security workspace setup timed out without Start Scan being submitted, so the
formal diff review, signed push, and exact remote container/CI result remain
pending. Detached repair work, false
readiness, delete-before-commit blob cleanup, and unsafe rclone state recording
remain release blockers. See `HANDOFF.md` for exact evidence and order.

## Task status

| Task | Status | Exit criterion |
| --- | --- | --- |
| 0. Freeze V1 contract and pivot documentation | COMPLETE | New design/plan govern continuation; Phase 4 is explicitly deferred; handoff and `AGENTS.md` point here. |
| 1. Restore deterministic backend test truth | COMPLETE | No testhost crash; cleanup/visibility contract is correctly classified and green; complete suite runs normally. |
| 2. Secure sessions, proxying, errors, and logs | 2A-2E + CONTAINER HARDENING LOCAL GREEN; FINAL SECURITY/CI AND 2F REFERENCE AUDIT PENDING | Stable session policy, exact route authentication, bounded sanitized responses, canary-negative tests, reference-gap audit, and exact remote CI pass. |
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
- [x] Record user choice: full rebrand waits for Task 10 GO and user review of
      the complete backend pass/risk report.
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
and the no-direct-ingress requirement. Exact-HEAD GitHub Actions run
[`29658476416`](https://github.com/binGhzal/pinrail/actions/runs/29658476416)
passed the container lifecycle smoke and every stated native Transfer job on
glibc/musl and x64/arm64 at the carrier-slice base. This is CI evidence for the
slice base, not final release-candidate provenance.

### 2B. Exact proxy and symlink-only boundary — complete locally

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
zero. The Task 2B key-parser slice has landed: its affected gate passed `91/91`
before review characterization and now passes `96/96`. Signed review-fix
checkpoint `c550bc61a7d16df17278ec755fc2516015d95b1e` passed independent re-review
and exact-HEAD GitHub Actions run
[`29661598011`](https://github.com/binGhzal/pinrail/actions/runs/29661598011):
the full verifier and native Transfer glibc/musl x64/arm64 jobs completed with
zero failed jobs or steps. This seals the carrier sub-slice, not the production
proxy or final release regression.

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

**Frozen public API-key carrier contract (2026-07-18):**

- Accept exactly one `x-api-key` header, with normal HTTP header-name
  case-insensitivity; exactly one lowercase query `apikey`; or exactly one
  lowercase form `apikey` for a form content type.
- Preserve exactly one multi-location compatibility shape: one header plus one
  query carrier with equal values under the existing constant-time comparison
  and no form carrier. This is the pinned AIOStreams request shape.
- Reject repeated values within a location, header-plus-form,
  query-plus-form, all three locations, conflicting header-plus-query values,
  noncanonical `apiKey`/`APIKEY` query or form names, empty values, and values
  over 512 characters. Missing carriers return null. The internal API parser
  remains separate and header-only.

Pinned client evidence:

- AIOStreams stable `v2.31.1` at
  `ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0` and observed main
  `4f0c4aace62d5981f495f42659b1ae4e83764b11` have byte-identical integration
  files. Its [`GET` request builder](https://github.com/Viren070/AIOStreams/blob/ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0/packages/core/src/debrid/usenet-stream-base.ts#L132-L173)
  and [`addurl`/`history` callers](https://github.com/Viren070/AIOStreams/blob/ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0/packages/core/src/debrid/usenet-stream-base.ts#L273-L347)
  send the same key in lowercase query `apikey` and header `x-api-key`.
- Sonarr `v4.0.19.2979` uses one lowercase query key in
  [`BuildRequest`](https://github.com/Sonarr/Sonarr/blob/v4.0.19.2979/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L162-L184).
  Its multipart
  [`DownloadNzb`](https://github.com/Sonarr/Sonarr/blob/v4.0.19.2979/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L46-L53)
  keeps the key in that request query and puts the NZB in form field `name`.
  Radarr `v6.3.0.10514` and Lidarr `v3.1.0.4875` have byte-identical
  `BuildRequest`/lowercase-query logic
  ([Radarr](https://github.com/Radarr/Radarr/blob/v6.3.0.10514/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L162-L184),
  [Lidarr](https://github.com/Lidarr/Lidarr/blob/v3.1.0.4875/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L162-L184)).
  Their multipart category properties differ
  ([Radarr](https://github.com/Radarr/Radarr/blob/v6.3.0.10514/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L46-L53),
  [Lidarr](https://github.com/Lidarr/Lidarr/blob/v3.1.0.4875/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L46-L53)),
  but carrier placement is structurally equivalent: the key stays in the
  `BuildRequest` query and the upload form contains the NZB as `name`.
- [SABnzbd 5.0.4 documents lowercase query `apikey`](https://sabnzbd.org/wiki/configuration/5.0/api).
  Form support remains a Pinrail compatibility carrier; no researched current
  client requires a form duplicate. rclone `v1.74.4` uses WebDAV Basic
  `Authorization` and does not consume this parser.

**Carrier-slice evidence (2026-07-18):** tests-only RED failed exactly the
same-value header-plus-form, query-plus-form, and all-three-location cases
against unchanged production code (`3` failed, `26` passed). Minimal GREEN
passed the focused class `29/29` and the parser/SAB/ARR/add-file affected gate
`91/91`, both with zero skips. Production and test Release builds passed with
warnings as errors, and scoped formatter verification passed. Review
characterization for missing, 512-character, and mixed-case-header boundaries
now passes focused `34/34` and affected `96/96`, with no production-code change.
The first independent specification, quality, and bounded-security reviews
found no P0/P1/P2 and no functional parser defect. The backend parser is sealed
after accepted review-fix re-review and exact-HEAD run `29661598011`;
at that carrier-only checkpoint, production proxy work remained blocked on the
RED route/method/credential matrix. The later Task 2B evidence below supersedes
that historical status.

**Security-tool gate (2026-07-18, sealed):**
gitleaks 8.30.1 identified exactly eight `generic-api-key` findings, all in
deterministic test fixtures introduced by one consolidation commit. Current
fixtures now preserve their exact length/shape/distinctness/redaction behavior
without key-like literals. Immutable history uses exactly eight fingerprint-
scoped exceptions for that introduction commit; no rule, path, regex, or global
suppression exists. Current-tree and full-history scans report zero. Focused
C# fixture tests passed `170/170`; frontend auth tests passed `24/24`, the full
frontend unit suite passed `261/261`, and type/client/server builds passed.
Production and test Release warning-as-error builds, scoped formatter, shell
syntax/key-shape, and independent bounded review are green. Signed checkpoint
`c925ebc84dc8e8bdd3e10fb7f35ee3ee249bc622` passed exact-HEAD GitHub Actions
run `29663552874`: main verifier and native Transfer glibc/musl x64/arm64, with
zero failed jobs or steps.

**Urgent pre-proxy incident gate (2026-07-18, sealed):** current report mode
must never drain persisted apply work. The
tests-only RED proved due pre-existing Sonarr and Radarr `pending_apply` rows
were both posted and executed under `Enabled=true`, current `Mode=report`.
Minimal GREEN gates only pending apply-command processing on current normalized
`Mode=apply`, preserving report planning and deployment defaults
`Enabled=false`, `Mode=report`. Focused `1/1`, affected `28/28`, complete local
Release `2,868` passed with 85 deliberate PostgreSQL-only skips, warning-as-
error builds, scoped format, whitespace, gitleaks, and independent review are
green. Signed checkpoint `88e05f87e37147bf60b7fad8b0914df43e219eab`
was pushed without force; exact-HEAD run `29664771054` completed with the main
verifier and native Transfer glibc/musl x64/arm64 all successful, zero failed
jobs, and zero failed steps. The then-required stop before resuming the proxy
matrix was honored and later superseded by explicit continuation. External
signed commit
`0e9e3583e6b26fedb26c222f06519a02853bc902` already added clone-local Graphify
configuration and passed exact run `29663671084`; the prohibition applied to
that incident slice only. The current checkpoint must perform its final
AST-only update after the diff stabilizes.

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

**Local completion evidence (2026-07-19):** The production listener now runs
the raw-target classifier for every HTTP lane and authenticates exact
`<URL_BASE>/ws` before 101. UI/admin relays require the selected principal and
receive one server-owned internal key; `/protocol` lanes preserve only their
reviewed public/WebDAV credentials. Browser authority, hop-by-hop fields,
private redirects, and private cookies are stripped. A `Connection` option may
not nominate an API-key, authorization, form/framing, conditional, range,
authority, destination, or internal PathBase header; capture and real ASP.NET
regressions prove conflicts cannot be erased before the sealed backend parser.
The backend binds explicit loopback only and rejects configured Kestrel
endpoints; the image exposes only port 3000.

Mounted WebDAV uses one bounded base64url internal PathBase header that is
removed before NWebDav and logical store lookup. Empty, single, and nested
mounts pass disposable ASP.NET `34/34` plus the subsequent Connection-carrier
regressions. Pinned rclone `1.74.4` passes `7/7` across list, range, upload,
queue removal, operator content deletion, and completed-symlink acknowledgement.
Runtime `URL_BASE` is restricted to literal unreserved ASCII segments, exact
case, and an 8,192-byte mounted WebDAV PathBase ceiling. Wildcard runtime
`DEBUG` is disabled before routing so Express/HPM cannot emit credential-bearing
targets.

The symlink-only sub-slice removes STRM/both configuration, migration seed,
post-processing, recreation/conversion controllers and tasks, UI actions, and
dead utilities. Fresh-install/source manifests and executable contract tests
prove retired values/routes are absent while canonical `/.ids` symlink targets,
ARR completed paths, cleanup, DFS, and rclone behavior remain green.

**Confirmed adjacent defect:** `scripts/nzbdav_arr_report_validation.py` sends
the documented public `NZBDAV_API_KEY` to `/api/get-config`, but
`BaseApiController` accepts only `FRONTEND_BACKEND_API_KEY`. Existing Python
tests validate parsing/redaction rather than live authorization. Do not expose
the generic config controller to the public key. Add RED service/script tests,
return only the required non-secret app-type/search-mode/duplicate-policy fields
from `/api/arr/validation`, remove the helper's general-config request, and run
the focused .NET/Python gates before adding that route to `/protocol`.

**Completion status (2026-07-19):** GREEN locally and accepted within the
independent Task 2 whole-diff review. `ArrValidationResponse` returns only configured app kinds,
normalized search-nudge mode, and normalized duplicate policy. The helper no
longer references `/api/get-config`. Verified focused service `1/1`, focused
Python `7/7`, full `ArrOperationsServiceTests` `27/27`, all Python `110/110`,
and complete backend Release `2,825` passed with `84` expected PostgreSQL skips.
Release warning-as-error build, edited-file formatter checks, Python compile,
documentation validation, and `git diff --check` all exited zero. The wider
Task 2 checkpoint later passed backend Release `2,986` with `85` deliberate
PostgreSQL-only skips; exact remote checkpoint CI is still pending.

### 2C. Public error, log, and persistence contract — complete

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

**Local completion evidence (2026-07-19):** The affected admin/SAB/history/
maintenance/config/ARR/WebDAV projections now emit bounded stable summaries or
allowlisted values rather than raw exception, URL, provider, path, credential,
or persisted failure text. Frontend proxy policy failures and private-hop
failures use fixed bounded JSON; wildcard Express/HPM diagnostics are disabled.
Synthetic canaries are absent from response bodies, headers, process streams,
logs, configuration projection, history, maintenance, and WebSocket status
topics in the affected suites. Gitleaks current-tree and full-history scans are
`0/0`; the sealed eight-entry fingerprint-only historical exception file is
unchanged.

Final focused review found P0/P1/P2/P3 `0/0/0/0`. The accepted terminology
residual is that `Repaired` means an ARR repair command was accepted, not that
the external ARR completed it. A future extension of maintenance
`ExecuteUpdate` strings to external text would require renewed sanitization
review; current values are fixed and code-owned.

### 2D. Internal-key and normal-startup authentication preflight — complete

The combined entrypoint generates a fresh lowercase 64-hex
`FRONTEND_BACKEND_API_KEY` only when omitted or empty, preserves an explicitly
configured valid 64-hex value, and rejects malformed explicit values before
identity-system discovery/mutation, filesystem, database, or child-process
work. Generation is checked before export and no failure path prints candidate
material.

Normal startup validates exact `AUTH_MODE` before internal-key generation.
`local` requires and exports an exact 64-hex `SESSION_KEY` unchanged;
`authentik-proxy` remains session-key independent. Valid maintenance commands
remain independent of frontend session configuration, while invalid
maintenance argv retains first precedence and status `64`.

The first startup-preflight review found P0/P1/P2/P3 `0/0/3/0`: non-local mode
bypass, a test that masked validation ordering by injecting the internal key,
and a digits-only fixture that did not prove preservation/export. RED tests for
invalid/empty mode, omitted/empty internal-key ordering, and mixed-case child
export preceded the repair. Final shell portability cleanup and independent
review found `0/0/0/0`; shell contract, `sh -n`, severity-error ShellCheck,
actionlint, frontend production/packaged gates, and diff checks are green.

Local checkpoint evidence is in
`.superpowers/sdd/task-2d-internal-key-startup-report.md`. Exact current hashes
are recorded there after the adjacent container-user repair.

The adjacent Trivy gate is also complete. V1 rejects zero/padded-zero
`PUID`/`PGID`, normalizes valid nonzero IDs, runs backend/frontend children with
the resolved user and group, and keeps only PID 1 root for dynamic identity,
owned-path preparation, and supervision. The exact root `Dockerfile` has one
DS-0002 exception limited to that contract; it does not cover privileged DFS/
FUSE, and V1 uses the rclone sidecar. Legacy backend/frontend standalone
Dockerfiles and entrypoint/dependency surfaces are retired.

The first implementation review found P0/P1/P2/P3 `0/0/1/0` because the smoke
used client-host `/proc` and checked only real IDs. A tests-only RED required
inspection inside the container PID namespace and all real/effective/saved/
filesystem UID/GID columns. Repair and re-review are `0/0/0/0`. Final focused
gates: Python `157/157`, packaged runtime `6/6`, shell contract, typecheck,
`sh`/Dash/BusyBox syntax, full ShellCheck, actionlint, diff check, Trivy npm
vulnerabilities `0`, and Trivy Dockerfile misconfigurations `0`. Local Docker
was not run; exact remote CI owns executable container proof.

### 2E. Canonical protocol client and operator contract — complete

External clients use exactly `<origin><normalized URL_BASE>/protocol`. Root and
nested deployments retain suffixes beneath that base; rclone mounts the full
protocol root so `/.ids`, `/completed-symlinks`, `/content`, and `/nzbs` remain
available. `<origin><URL_BASE>/view` remains a frontend-principal route outside
the public protocol namespace and every reverse-proxy bypass.

The shared Python normalizer runs before secret resolution, network access, or
artifact creation. Benchmark HTTP paths are suffixes, not replacement URLs.
C0/DEL controls, separator resets, dot/empty segments, and unresolved
ambiguous/excessive encodings fail closed; terminal literal `%25`, `%2541`, and
`%2525literal` forms and frontend-compatible C1 values remain valid. Sonarr,
Radarr, and Lidarr setup uses the exact URL Base, first-run local HTTP examples
include required session/cookie policy, and public docs expose no private
PostgreSQL implementation path.

Final focused counts are helper `4/4`, ARR `12/12`, benchmark `41/41`,
grab-to-Plex `42/42`, operator contract `17/17`, request policy `158/158`, and
complete Python `155/155`. Initial code review `0/0/3/1`, second code review
`0/0/2/0`, and operator review `0/3/3/0` were repaired RED-first. Final code
and operator reviews each found P0/P1/P2/P3 `0/0/0/0`.

### 2F. Clean-room reference archaeology and V1 gap reconciliation — next

Analyze these user-selected reference implementations only after the signed
Task 2 checkpoint is clean and exact remote CI passes:

- `https://github.com/qooode/nzbdavex`
- `https://github.com/Gaisberg/streamnzb`
- `https://github.com/javi11/altmount`

Clone them read-only beneath an ephemeral directory outside `/opt/pinrail`.
Never create `/opt/pinrail/repo`, a nested repository, or a published fork.
For each reference, inspect the complete current tree, relevant commit/tag/
release history, exact license, dependency licenses, docs, configuration,
containers, API/protocol contracts, tests, CI, failure/recovery behavior,
security boundaries, caching and random-access/mount behavior, budgets,
observability, operator UI, and deployment model. Build an AST-only Graphify
graph per clone and use `query`, `path`, and `explain` on the important flows.

Repository-page claims are hypotheses until verified in source and history.
The reported MIT licenses for nzbdavex/altmount and GPL-3.0 license for
streamnzb must be read from exact current and relevant tagged `LICENSE` files.
GPL provenance is an explicit compatibility blocker: copy no code, test, asset,
schema, or expression unless a documented Pinrail license analysis proves it
compatible. Prefer clean-room behavior reimplementation from public contracts
and independently authored RED tests; add no dependency merely because a
reference uses it.

Produce a source-cited matrix against current Pinrail HEAD, this plan, the
governing design, `HANDOFF.md`, and the progress ledger. Classify every candidate
as implemented, partial, genuinely missing in approved V1, deliberately
post-V1, or unsuitable/unsafe. Rank actionable gaps P0/P1/P2 and specifically
cover NZB validation/ingestion, article availability, range/archive streaming,
sparse/cache semantics, WebDAV/rclone/FUSE behavior, durable queue recovery,
retry/idempotency, multi-provider operation, resource budgets, filesystem
correctness, API compatibility, security, diagnostics, and UI workflows.

Only verified, license-safe gaps inside the frozen Docker/SQLite/one-owner/
`role=all` V1 may change Pinrail. Reuse existing abstractions, preserve the
SearchNudge and category/CDH containment contracts, and apply focused RED,
minimum GREEN, affected/full gates, independent review, documentation,
signed-off push, and exact remote CI to each accepted slice. PostgreSQL,
Transfer-v3 Phase 4, split roles, visual rebrand, production mutation, and
unapproved deployment remain unreachable.

### Task 2 gates

- [x] Session matrix passes.
- [x] Proxy authorization/header matrix passes locally.
- [x] Error and log canary matrix passes locally.
- [x] Internal-key and normal-startup authentication preflight passes locally.
- [x] Canonical protocol client/operator contract passes locally.
- [x] Frontend and backend full test/build gates remain green locally.
- [x] Independent security review reports no P0/P1 on the reviewed code diff.
- [x] Close the actionable Trivy container-user P2 and document the exact
      dynamic-identity exception, retirement scope, and non-coverage of the
      privileged DFS prototype.
- [x] Refresh the final AST-only Graphify graph and run query/path/explain
      canaries against the stable combined diff.
- [ ] Complete the formal Codex Security diff scan. Its native workspace is
      open, but the setup wait expired before Start Scan was submitted; the
      independent `0/0/0/0` review is additive and does not replace this gate.
- [ ] Signed Task 2 checkpoint passes exact remote CI, including container
      lifecycle smoke and native Transfer glibc/musl x64/arm64.
- [ ] Reference archaeology matrix is complete and every accepted V1 gap is
      either closed or routed explicitly to its existing later task.

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
commands and results. Record the backend freeze, but do not begin visual
rebrand. The strict design/rebrand gate remains Task 10 GO plus user review of
the complete backend pass and risk report.

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

After Task 10 reaches GO, present the complete backend verification evidence
and release risk report to the user. Only after the user reviews that report
may the official Figma MCP be used to inspect the existing
[Pinrail — Product Design / Wireframes file](https://www.figma.com/design/WxDUx3FJ9iINrtXn2GmkZC/Pinrail-%E2%80%94-Product-Design---Wireframes?m=auto&t=YBuWaX8ZhLg6aYZd-6).

The Figma file is the mandatory source of truth for the redesign. Ask before
creating a new page/section if the file has no existing NZBDav destination.
Create and review every route, component, state, accessibility behavior, and
responsive contract in Figma first; obtain user approval there before changing
user-facing frontend or rebrand implementation. Do not substitute Pencil,
OpenPencil, code-first mockups, or another design tool without explicit user
approval.
