# NZBDav V1 Backend Release Design

**Status:** APPROVED FOR IMPLEMENTATION

**Decision date:** 2026-07-17

**Canonical handoff:** [`HANDOFF.md`](../../../HANDOFF.md)

**Implementation plan:**
[`2026-07-17-nzbdav-v1-backend-release-plan.md`](../plans/2026-07-17-nzbdav-v1-backend-release-plan.md)

## 1. Executive decision

NZBDav V1 is a Docker-first, clean-install-only release with:

- SQLite as the only supported production database;
- one control owner and `role=all` as the only supported topology;
- one published container as the supported installation path;
- no in-place upgrade promise from any pre-V1 tag;
- no PostgreSQL, Transfer-v3 Phase 4, split-role, multi-host, or multi-owner
  runtime reachability;
- no full visual rebrand until the backend release gate is completely green;
- no user-facing frontend or rebrand implementation before its design is
  created and approved in the existing
  [Pinrail Figma file](https://www.figma.com/design/WxDUx3FJ9iINrtXn2GmkZC/Pinrail-%E2%80%94-Product-Design---Wireframes?m=auto&t=YBuWaX8ZhLg6aYZd-6).

This boundary was selected by the user and independently reviewed by a
six-seat council. The council was unanimous that the topology is viable and
unanimous that the current worktree is a V1 **NO-GO** until the blockers in
this design close.

## 2. Why this boundary

The current product has substantial passing coverage and a working SQLite
runtime, but release evidence is not yet trustworthy enough for end users:

- three cleanup/visibility tests fail in the broad backend suite;
- a Usenet redaction test recursively disposes its own Serilog sink and crashes
  the test host;
- `scripts/nzbdav_safe_rclone_up.py` can report success for a missing, stopped,
  unhealthy, or mountless runtime and can persist success before postconditions
  are proven;
- authentication can silently use an ephemeral session key and non-secure
  cookies;
- `/health` proves process liveness only;
- some public API paths return raw exception messages;
- persistence-affecting repair work can be detached from host lifecycle;
- the heavily dirty worktree prevents binding existing green results to one
  immutable release artifact.

Expanding database or role topology while these foundations are unresolved
would increase risk without improving the smallest useful end-user product.

## 3. Council record

The council used six fresh read-only seats with distinct review methods.

| Seat | Method | Conclusion |
| --- | --- | --- |
| 1 | Architecture and correctness | Keep SQLite/one-owner topology; NO-GO until cleanup, session, readiness, error, and provenance gates pass. |
| 2 | Release operator/SRE | Freeze one candidate and digest; repair deterministic reds; prove crash consistency and canary lifecycle before publication. |
| 3 | Security and privacy | Raw exception disclosure, unsafe session defaults, incomplete Usenet redaction proof, and mutable provenance block release. |
| 4 | Product/operator journey | Six behavioral journeys must pass before rebrand; visual work may wait, behavioral E2E may not. |
| 5 | Scope/economics | Clean-install-only is the smallest honest V1; upgrades and topology expansion are post-V1. |
| 6 | Adversarial failure modes | Current candidate can appear green while mount, readiness, session, SQLite, or shutdown behavior is broken; NO-GO. |

### Chair synthesis

The chair adopts the unanimous topology decision and NO-GO finding. The
council's stricter proposals are reconciled as follows:

- external Usenet and ARR availability are diagnostics, not core startup
  readiness, so first-run onboarding remains possible;
- backend readiness covers the migrated SQLite store, required local paths,
  configuration, and critical hosted-service initialization;
- rclone health and mount traversal are verified by the external safe-up
  workflow and product diagnostics, not by backend startup;
- three complete backend-suite passes plus ten repeated affected-concurrency
  passes are required; the final exact artifact receives two clean-install
  acceptance runs and one bounded soak;
- pre-V1 upgrades and in-place downgrade are unsupported. Recovery uses a cold
  snapshot compatible with the exact image digest or a fresh install.

## 4. Supported V1 contract

### 4.1 Supported

- Fresh Docker installation on supported amd64 and arm64 hosts.
- Empty, uniquely owned `/config` and `/data` roots at first start.
- SQLite database created and migrated by the container.
- Exactly one backend control owner.
- `role=all` only.
- Web UI, WebDAV, SAB-compatible API, queue, history, health diagnostics,
  cleanup, repair, and rclone integration in that topology.
- Reverse-proxy HTTPS as the recommended authenticated deployment.
- Explicitly acknowledged insecure-cookie mode for loopback/private development
  only.

### 4.2 Unsupported and unreachable

- Upgrading an existing v0.5.x or v0.6.x database in place.
- In-place downgrade.
- PostgreSQL provider selection at runtime.
- Transfer-v3 Phase 4 execution from startup, CLI, controller, service
  registration, Compose, or UI.
- Split roles, multiple control owners, multi-host coordination, and
  Kubernetes/native-package deployment.
- Full visual rebrand before backend freeze.
- Code-first user-facing frontend or rebrand work that has not first been
  designed and approved in the existing Pinrail Figma file. Behavioral tests
  and non-visual server/security work may proceed before that visual gate.

Unsupported paths must fail closed with a stable, actionable message. Merely
omitting documentation is insufficient.

## 5. Architectural invariants

1. One process owns migration, SQLite writes, queues, cleanup, invalidation,
   workers, and shutdown.
2. SQLite foreign keys and the reviewed journal/durability settings remain
   enabled.
3. A durable database mutation and its required follow-up intent are committed
   atomically whenever the operation contract requires both.
4. Deleted or changed content is not declared externally visible until its
   durable visibility fence is acknowledged.
5. The whole-cache sentinel is conservative: it subsumes narrower path
   invalidations for the same publication window and is removed only by exact
   acknowledgement plus revision comparison-and-swap.
6. Fingerprints and state files are caches, never runtime authority.
7. Every persistence-affecting background operation is bounded, observed,
   cancellation-aware, and owned by the host or a durable queue.
8. Public errors contain stable codes and correlation IDs, not raw exceptions,
   paths, provider responses, credentials, connection strings, or SQL details.
9. Liveness and readiness are separate contracts.
10. Every release claim is tied to one source revision, lockfiles, image digest,
    architecture manifest, test record, and SBOM.

## 6. Required end-user journeys

The backend is not frozen until all six journeys pass against the exact
candidate image.

### Journey A: clean install and first login

- Start from empty disposable roots.
- Migration succeeds once and remains idempotent on restart.
- Backend becomes ready before frontend is considered ready.
- Onboarding creates usable credentials.
- A session remains valid across a container restart using the same configured
  key.
- Missing or invalid required secrets fail startup with an actionable message.

### Journey B: configure and verify dependencies

- Save and retrieve redacted settings without overwriting stored secrets with
  display placeholders.
- Test Usenet and ARR connections.
- Expected and hostile failures expose no secret, provider body, stack trace,
  absolute path, or control character in client responses or logs.

### Journey C: submit through completion

- Accept manual upload and SAB/ARR submission.
- Persist queued, downloading, verifying, failed, cancelled, and completed
  states across reconnect and restart.
- Do not publish completion before verification, cleanup durability, and the
  required visibility contract succeed.

### Journey D: consume content

- Browse WebDAV/rclone output, read and seek a completed file, and survive
  backend/rclone restart.
- Never expose a partially committed or stale replacement as complete.
- Isolate provider/segment failure to the affected item.

### Journey E: diagnose and recover

- Distinguish live, ready, degraded, and unavailable states.
- Report timestamped SQLite, local storage, worker, queue, cleanup,
  invalidation, Usenet, ARR, and mount diagnostics without secrets.
- Repair, retry, and cancel operations expose accepted, running, terminal, and
  failed lifecycle rather than fire-and-forget success.

### Journey F: safe restart and configuration update

- Preserve SQLite, queue state, credentials, and sessions across restart.
- Safe rclone update skips only when rendered inputs match a currently healthy
  target and the mount/read canary succeeds.
- A failed start never overwrites last known-good state.

## 7. Security boundary

### 7.1 Authentication-mode and session contract

V1 supports two explicit, mutually exclusive frontend authentication modes:

- `AUTH_MODE=local` is the clean-install default;
- `AUTH_MODE=authentik-proxy` enables a Sonarr-style Authentik Proxy Provider
  boundary.

The user selected Authentik's full Proxy Provider topology for this mode: the
Authentik outpost is nzbdav's browser-facing reverse proxy and directly proxies
requests to the internal nzbdav frontend. A separate Nginx/Traefik forward-auth
deployment is not part of the V1 contract.

Mode selection never probes Authentik or infers trust from request headers. An
Authentik failure or malformed configuration fails closed and never falls back
to local login. Changing modes requires a restart and emits a bounded startup
audit event without secret values.

In local mode:

- `SESSION_KEY` is required and is exactly 64 hexadecimal characters (32 bytes
  of key material); there is no random fallback;
- optional `SESSION_KEY_PREVIOUS` accepts exactly one prior 64-character
  hexadecimal key for bounded one-step cookie rotation, while new cookies are
  always signed by `SESSION_KEY`;
- cookie security policy is explicit. `SECURE_COOKIES=true` is the documented
  default. Plain HTTP requires `ALLOW_INSECURE_COOKIES=true`;
- cookies remain `HttpOnly` and `SameSite=Strict` with a bounded lifetime;
- restart, malformed-cookie, old-cookie, and key-rotation tests are required.

In Authentik proxy mode:

- local login/session creation endpoints are disabled rather than retained as
  a fallback;
- UI, WebSocket, and internal-backend-key injection require Authentik identity
  headers from an explicitly configured trusted outpost IP/CIDR and the
  expected Authentik application metadata;
- missing, conflicting, oversized, malformed, wrong-application, or
  untrusted-source identity headers fail closed;
- the Authentik outpost is the only supported browser ingress and the nzbdav
  application port is not published as a public bypass;
- SAB-compatible API and WebDAV routes remain usable through their independent
  API-key/WebDAV authentication and never inherit browser-admin authority from
  an unauthenticated proxy path.

### 7.2 Proxy contract

The server classifies routes before implementation:

- UI-admin routes require a valid frontend principal from the selected local or
  Authentik mode, strip any client-supplied internal API key, and inject the
  configured backend key server-side;
- WebDAV and SAB-compatible routes remain directly usable with their own
  reviewed authentication contract;
- every forwarded prefix and method is explicitly allowlisted;
- encoded-separator, double-encoding, prefix-confusion, conflicting-header,
  oversized-header, and client-supplied-key cases fail closed.

The public/protocol API-key parser has one frozen compatibility exception.
Individually it accepts exactly one `x-api-key` header, exactly one lowercase
query `apikey`, or exactly one lowercase form `apikey` for a form content type.
It also accepts exactly one equal header-plus-query pair, with no form carrier,
using the existing constant-time comparison. This is the pinned AIOStreams
`v2.31.1` shape: its
[`GET` builder](https://github.com/Viren070/AIOStreams/blob/ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0/packages/core/src/debrid/usenet-stream-base.ts#L132-L173)
sends the same value in header `x-api-key` and lowercase query `apikey`.
Sonarr `v4.0.19.2979`, Radarr `v6.3.0.10514`, and Lidarr `v3.1.0.4875` use one
lowercase query `apikey`; SABnzbd 5.0.4 documents that query spelling; rclone
v1.74.4 uses WebDAV Basic `Authorization` instead.

Repeated values within one location, every form-plus-other-location shape, all
three locations, a conflicting header-plus-query pair, noncanonical query/form
name casing, empty values, and values over 512 characters fail closed with the
existing stable malformed-carrier exception. Missing carriers return null. The
internal parser remains separate and header-only. This parser contract does not
authorize a proxy route or widen browser authority; the production proxy still
requires the complete reviewed route/method/credential matrix before wiring.

### 7.3 Error and log contract

- Public failures use a bounded envelope: stable code, safe message, correlation
  ID, and optional allowlisted detail.
- Server logs use structured stable fields and redaction.
- Tests inject canary credentials, paths, URLs, provider responses, CR/LF,
  terminal escapes, oversized text, and nested exceptions.
- The canary must be absent from response body, logs, persisted diagnostics,
  WebSocket payloads, and test output.

## 8. Liveness and readiness

### Liveness

Liveness proves only that the owning process is running and able to answer. It
must not fail because an external provider is unavailable.

### Core readiness

Core readiness returns success only when:

- configuration loaded successfully;
- SQLite is open, schema is current, and a bounded read succeeds;
- required configuration/blob/cache roots satisfy the selected feature's
  read/write contract;
- critical hosted services completed initialization and have not faulted;
- shutdown/drain has not begun.

Usenet, ARR, and external rclone availability appear as degraded diagnostics,
not clean-install startup blockers. The entrypoint waits on core readiness
before declaring backend startup successful.

## 9. Safe rclone update contract

The safe-up workflow:

1. validates arguments and computes the watched-input fingerprint;
2. inspects the exact Compose service container even when the fingerprint is
   unchanged;
3. requires one unambiguous matching container with expected config hash,
   current watched-input age, running/non-restarting state, and health when a
   healthcheck exists;
4. performs bounded RC/WebDAV and mount/read canaries appropriate to the
   configured deployment;
5. invokes `compose up -d` when any prerequisite is absent or stale;
6. repeats all postcondition checks after Compose returns;
7. writes state atomically only after every check succeeds;
8. leaves last known-good state untouched and exits nonzero on any failure.

Tests cover unchanged healthy, unchanged stopped, unhealthy, missing mount,
ambiguous inspection, hash mismatch, stale start, changed input, failed Compose,
failed canary, and state-write failure.

## 10. Persistence and lifecycle proof

Disposable SQLite fixtures cover:

- cleanup plus visibility-fence atomicity;
- whole-cache sentinel supersession and exact acknowledgement;
- republish-during-acknowledgement revision races;
- `SQLITE_BUSY`, read-only root, disk-full simulation, cancelled transaction,
  and process termination boundaries;
- WAL/SHM restart and `PRAGMA integrity_check`;
- SIGTERM during active stream, cleanup, repair, and invalidation;
- bounded shutdown with no unobserved persistence task.

Tests may use only uniquely owned temporary roots. Production data and services
remain forbidden.

## 11. Release and recovery contract

### Candidate identity

- Freeze one exact source revision before final gates.
- Build amd64 and arm64 images once.
- Record immutable manifest and platform digests.
- Embed OCI version, revision, and source labels.
- Generate SBOM and build provenance/attestation.
- Run acceptance against the exact digest that would be published.

### Clean-install recovery

V1 has no in-place downgrade promise.

- Before a risky operator change, stop the container and capture a cold,
  complete `/config` snapshot, including SQLite WAL/SHM when present.
- A pre-traffic candidate failure may restore a snapshot compatible with the
  previous digest.
- After a newer schema has written data, an older image is never started against
  that database.
- Without a compatible cold snapshot, recovery is same-digest repair or a fresh
  clean installation.

## 12. Definition of done

V1 backend freeze requires all of the following on the exact candidate:

- backend suite: zero failures, zero crashed test hosts, no ad hoc exclusions;
- no increase over the reviewed 84 deliberate post-V1 skips without written
  classification;
- three consecutive complete backend passes;
- ten consecutive affected cleanup/invalidation/concurrency passes;
- Usenet success, rejection, timeout, cancellation, hostile-response, and
  disposal tests complete normally with zero canary leakage;
- Release builds and clean builds pass with warnings as errors;
- formatting and `git diff --check` pass;
- disposable SQLite migration, restart, integrity, lock-contention, read-only,
  and failure-recovery gates pass;
- shell and container entrypoint contracts pass, including child death,
  migration failure, and SIGTERM;
- safe-rclone fault matrix passes;
- session, cookie, proxy, error-envelope, and redaction abuse matrices pass;
- liveness/readiness fault and recovery matrix passes;
- frontend build gates and behavioral E2E for the six journeys pass after
  backend freeze; visual rebrand work is still not part of this gate;
- dependency audits report no known vulnerable production dependency;
- two fresh clean-install acceptance runs and one 30-minute bounded soak pass;
- final independent review reports no P0/P1 and every accepted P2 is documented;
- source revision, image digest, SBOM, provenance, test evidence, clean-install
  limitation, and residual risks are published together.

Any failure, crash, timeout, flaky rerun, unexplained skip, digest mismatch,
missing artifact, secret leak, or unsupported topology reachability is a
release stop condition.
