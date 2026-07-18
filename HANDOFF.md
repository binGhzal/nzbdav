# Agent Handoff

## Handoff metadata

| Field | Value |
| --- | --- |
| Content verification cutoff | 2026-07-18T23:47:26+04:00 |
| Handoff status | AUDITED WIP CHECKPOINT; V1 RELEASE NO-GO |
| Repository | `pinrail` (NZBDav compatibility names remain until the post-freeze rebrand) |
| Current branch | `pinrail/v1-backend-wip` |
| Initial documentation baseline HEAD | `8c06d9caacd8c0d2ab5d69f47e3b230b75b16704`, HEAD immediately before authorized publication |
| Initial handoff publication commit | The commit that first adds `AGENTS.md`. Resolve stably with `git log --diff-filter=A -1 --format=%H -- AGENTS.md` |
| Upstream | `https://github.com/nzbdav-dev/nzbdav.git` |
| Default remote branch | `origin/main` |
| Base and merge base | `origin/main` at merge base `86af7b816c496aea2654c438be7fa553b98bb91c` |
| Current relation | Checkpoint `fb03b0e6a247dfeaff9e9965f045a1fb1e6a11cc` is an ancestor; this handoff, the CI repair, and its verification record make the branch 34 ahead, 0 behind `origin/main` |
| Worktree | Clean after the signed checkpoint, CI repair, and verification record. Ignored env, Finder, bytecode, TRX, and local artifact files were excluded, not deleted. |
| Durability boundary | All reviewed worktree source is tracked in the signed WIP checkpoint. It is safe for remote continuation only, not merge, deployment, image publication, or release. Private Phase 4 remains unreachable and post-V1. |
| Canonical active plan | [V1 backend release implementation plan](docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md) |
| Governing design | [V1 backend release design](docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md) |
| Related pull request or issue | No pull request found for this branch by an authenticated read-only `gh pr view` query on 2026-07-16; no linked issue was identified from repository evidence |
| Reconstructed-objective confidence | High |

**VERIFIED FACT:** This file is the sole canonical live handoff. The former
2026-07-11 role-separation handoff was materially stale and has been replaced,
not supplemented with another competing addendum.

**VERIFIED DECISION:** The active mission is the clean-install-only V1 backend
release. V1 supports Docker, SQLite, one control owner, and `role=all` only.
PostgreSQL, Transfer-v3 Phase 4, split roles, pre-V1 upgrades, and in-place
downgrade are post-V1. Full rebrand work remains blocked until the backend
freeze and release-candidate gates pass. After those gates, every user-facing
frontend/rebrand change must be designed and approved first in the existing
[Pinrail Figma file](https://www.figma.com/design/WxDUx3FJ9iINrtXn2GmkZC/Pinrail-%E2%80%94-Product-Design---Wireframes?m=auto&t=YBuWaX8ZhLg6aYZd-6); code-first visual implementation and substitute design
tools are forbidden unless the user explicitly approves them.

**VERIFIED DECISION:** `AUTH_MODE=authentik-proxy` means Authentik's full Proxy
Provider topology. The Authentik outpost is the browser-facing reverse proxy and
directly proxies to the internal nzbdav frontend. Separate Nginx/Traefik
forward-auth mode is not supported by the V1 contract.

**VERIFIED REVIEW CONFLICT:** Transfer-v3 Task 8's executable pure gates are
green, and two reviewers found no finding, but a separate security/concurrency
review identified pre-budget unbounded client-side PostgreSQL catalog
materialization in `PostgreSqlPhysicalCatalogContract`. Because the green
reviews did not address that path, Task 8 is not sealed; the whole Phase 4 plan
is deferred post-V1.

## 2026-07-18 consolidation audit

The sole registered worktree was recovered without discarding content. Nineteen
dangling tips, including a deleted stash with divergent tracked content, were
anchored under local `refs/rescue/pre-pinrail-20260718/*`. A verified all-ref
bundle plus tracked and untracked worktree backups exist outside the repository.
No unreachable object was pruned and no rescue ref was published.

All 28 open upstream pull-request heads were already ancestors of `origin/main`.
Closed PR 466 was rejected because its unbounded in-memory concurrency design is
superseded by the durable bounded worker lanes in this checkpoint. Closed PR 473
was rejected as written because its telemetry lifecycle, WebSocket routing,
payload bounds, and UI work violate V1 contracts. Its raw NNTP byte-accounting
idea may be reconsidered during Task 6 after backend lifecycle ownership exists.

Local verification on the checkpoint source:

- Release backend and test builds: zero warnings and zero errors.
- Complete backend with required PostgreSQL integration: 2,966 passed, zero
  failed, zero skipped.
- Pinned Alpine musl PostgreSQL integration: 942 passed, zero failed or skipped.
- Python: 110 passed.
- Frontend typecheck, 261 Vitest tests, client build, and server build: pass.
- Playwright Chromium: 5/5 pass.
- Candidate image build and exact Node, npm, Alpine, and .NET runtime assertions:
  pass. This local image is evidence only, never a publishable candidate.
- Staged secret scan: eight fixed synthetic test canaries only; ignored runtime
  env and local auth state were not staged.

The audit found and repaired two cross-platform test-gate defects before the
checkpoint: unsupported PostgreSQL `EXTRACT(ERA FROM timestamp)` usage and a
musl-only nanosecond fixture violating the whole-microsecond timestamp contract.

The first remote branch verifier run, GitHub Actions `29656900140`, then exposed
one Linux-only test assumption and two missing source-contract fixtures in the
native matrix sandbox. The repair no longer treats SQLite's platform-dependent
`pragma_database_list.file` display value as proof of the descriptor route, and
the matrix now copies the tracked `docs` and `entrypoint.sh` fixtures it tests.
Before commit, the exact descriptor-plus-isolation filter passed 9/9 on both
pinned glibc/x64 and musl/x64 images, and the release-workflow contract passed
9/9. Replacement GitHub Actions run
[`29657383730`](https://github.com/binGhzal/pinrail/actions/runs/29657383730)
then completed successfully at exact repair commit
`83cef06f1d43b285d77e9537feda9963337128f5`. The full reusable verifier and
all four native Transfer jobs, glibc and musl on x64 and arm64, were green.

All GHCR publication is deliberately disabled while V1 is NO-GO. Branch,
Dependabot, main, and tag workflows now have read-only contents permission and
run the reusable verification gate only. The gate fails closed on NuGet audit
findings and includes the shell/container lifecycle contracts.

Two independent whole-diff reviews found no P0. They found these reachable P1
blockers, which make this branch WIP-only:

1. The exact `/protocol` proxy policy is draft test-only code; production still
   uses broad pre-auth forwarding and unrestricted WebSocket upgrade paths.
2. API-key duplicate-carrier behavior contradicts the canonical fail-closed
   plan and must be frozen from an executable client inventory.
3. Raw exceptions remain exposed or persisted through public responses, logs,
   queue history, and maintenance records.
4. Missing-file repair still performs detached persistence mutation with
   unbounded `Task.Run` work.
5. STRM generation and maintenance/controller surfaces remain reachable despite
   the V1 hard-symlink-only contract.
6. Entrypoint treats process-only `/health` as readiness; dependency readiness
   remains absent.
7. Blob cleanup deletes the file before durable database commit.
8. Safe-rclone records or trusts fingerprints without proving live container,
   RC, mount, and traversal postconditions.

The existing Pinrail Figma file was authenticated and its metadata resolved on
2026-07-18. No design was changed. Visual work stays blocked until backend freeze
and release-candidate gates pass.

## Resume in 60 seconds

1. Work from repository root, represented as `.`.
2. Read [`AGENTS.md`](AGENTS.md), this handoff, the
   [active plan](docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md),
   and the
   [design](docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md).
3. Verify checkpoint identity and compare live state with this snapshot:

   `git branch --show-current`

   `git merge-base --is-ancestor fb03b0e6a247dfeaff9e9965f045a1fb1e6a11cc HEAD`

   `git status --short --branch`

   Expected branch is the metadata value. Do not merge it to `main`, publish an
   image, or discard any new worktree difference.
4. Confirm the active V1 documents exist. If either is absent, stop rather than
   continuing the historical Phase 4 plan:

   `test -f docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md && test -f docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md`
5. Continue Task 2, `Secure sessions, proxying, errors, and logs`. Do not begin
   readiness, lifecycle, rclone, frontend rebrand, or release-candidate work
   first.
6. Task 2A is complete. Treat `frontend/server/request-policy.ts` as an unsealed
   draft because production does not import it. First exact next action: finish
   the frontend/backend route, method, key-carrier, and WebSocket inventory;
   reconcile it with the active plan; then freeze exact duplicate/conflict
   behavior before wiring production proxy code.
7. Build the Task 2B RED matrix beside `frontend/server/*.test.ts`: anonymous,
   local-authenticated, trusted Authentik, untrusted source, wrong application,
   encoded/double-encoded separator, prefix-confusion, conflicting API-key, and
   oversized-header cases.
8. Preserve independently API-key-authenticated WebDAV/SAB clients while
   proving browser UI-admin routes require the selected frontend principal and
   client-supplied internal keys are stripped before server-side injection.
9. Immediate acceptance criterion: the exact proxy authorization/header matrix
   is green; then continue public-error and log sanitization.

## Mission and definition of done

### Verified requirements

**VERIFIED FACT:** V1 must ship one exact Docker artifact that starts from empty
owned roots, migrates SQLite, runs exactly one control owner with `role=all`,
and completes the six end-user journeys in the governing V1 design.

**VERIFIED FACT:** V1 is done only when:

- deterministic backend tests have zero failure, crash, ad hoc exclusion, or
  unexplained skip;
- cleanup, visibility, queue, repair, streaming, shutdown, and restart
  contracts pass disposable fault injection;
- authentication requires a stable session key and explicit cookie-security
  policy;
- proxy, public error, log, and provider-response abuse tests leak no canary;
- liveness and core readiness are distinct and recover correctly;
- safe rclone update verifies live runtime and mount/read postconditions before
  recording state;
- the exact amd64/arm64 candidate digest passes two clean-install acceptance
  runs, one bounded soak, and independent review;
- evidence includes source revision, image digest, SBOM, provenance, test
  counts, clean-install limitation, recovery contract, and residual risk;
- PostgreSQL, Phase 4, split roles, upgrades, and in-place downgrade remain
  unreachable and unsupported;
- the user receives the complete backend pass report before full rebrand work
  begins.

### Scope

V1 backend correctness, security, lifecycle, SQLite resilience, operational
scripts, behavioral end-to-end journeys, container packaging, provenance,
release evidence, and documentation.

### Non-goals

PostgreSQL, Transfer-v3 Phase 4 completion, split roles, multi-owner/multi-host
operation, pre-V1 upgrades, in-place downgrade, alternative packaging, full
visual rebrand before backend freeze, and destructive/stress use of production.

### Inferred requirements

**STRONG INFERENCE:** A fresh agent must restore trustworthy backend test
execution before widening scope. Existing green gates cannot compensate for a
crashed security fixture or unclassified cleanup/visibility failures.

## Source-of-truth order

Trust sources in this order when they conflict:

1. [`AGENTS.md`](AGENTS.md), for repository operating constraints and the
   active-work pointer.
2. [V1 backend release design](docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md),
   for approved architecture and invariants.
3. [V1 backend release plan](docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md),
   for task order, exact interfaces, status, commands, and acceptance criteria.
4. Current preserved-worktree source plus executable tests, for behavior
   actually present. Current V1 and private Phase 4 implementation files are
   not part of the initial handoff publication commit.
5. This handoff, for the verified live snapshot, failures, and next action.
6. The deferred Phase 4 plan/design, older plans, and branch commit history, as
   historical or post-V1 rationale only.

When plan/design and code disagree, stop. Reconcile documentation or obtain a
decision before implementing.

## Repository snapshot

### Pre-documentation snapshot

| Fact | Value |
| --- | --- |
| Timestamp | 2026-07-16T22:01:55+04:00 |
| Working directory | Repository root |
| Branch | `codex/single-host-role-separation-design` |
| HEAD | `8c06d9caacd8c0d2ab5d69f47e3b230b75b16704` |
| Upstream query | Exit 128, no upstream configured |
| Default remote | `origin/main` |
| Merge base | `86af7b816c496aea2654c438be7fa553b98bb91c` |
| Relative history | 29 commits ahead, 0 behind |
| Worktrees | One worktree, this branch |
| Staged | 0 files |
| Tracked modified | 156 files |
| Untracked | 282 files |
| Pull request | None found for the current branch |
| Git mutations at snapshot capture | None |

Recent HEAD history begins with:

- `8c06d9ca docs: record verified provider migration continuation`
- `c86e4d57 fix: seed mock websocket connection state`
- `c17422f6 docs: refresh engineering continuation plan`
- `d7395a56 fix: preserve container maintenance contract`
- `986a29a2 fix: complete import receipt history removal`

### Documentation publication and implementation portability

**VERIFIED FACT:** The user authorized one signed documentation-only commit.
The commit containing this handoff is a direct child of the documented baseline
HEAD. It contains `AGENTS.md`, `CONTRIBUTING.md`, this handoff, the Phase 4 plan,
and the Phase 4 design. It contains no implementation file and was not pushed.

**VERIFIED FACT:** All 30 current files under
`backend/Database/Transfer/Phase4/**` and
`backend.Tests/Database/Transfer/Phase4/**` are absent from the documentation
baseline HEAD. That was true for the initial documentation-only publication.
The 2026-07-18 audit and signed WIP checkpoint supersede this portability limit:
the implementation is now durable in Git, while remaining non-releaseable.

### Attribution rule

The complete pre-edit tracked and untracked inventories appear in the appendix.
Documentation-pass changes are listed separately in the Changed-file ledger.
Every other dirty path in the final worktree predates this pass. No pre-existing
implementation file was edited, removed, staged, or cleaned by this pass.

## Current state summary

The V1 boundary and implementation sequence are frozen. The current release is
NO-GO. Release builds, disposable SQLite migration, frontend build gates,
5/5 Playwright tests, 110/110 Python tests, npm and NuGet vulnerability scans,
Docker image build, and entrypoint shell/container smoke are green in the
checkpoint source.

Task 1 backend truth is green in the current checkpoint source:

- the complete hermetic Release backend assembly passed 2,824 with 84
  deliberate PostgreSQL-only skips and zero failures or exclusions;
- the focused Usenet controller gate passed 4/4 and proves stalled-greeting
  timeout, request cancellation, and partial-connection disposal;
- the affected Usenet/cleanup/invalidation regression passed 85 with one
  deliberate PostgreSQL-only skip;
- the timing-sensitive subset passed ten consecutive audited iterations at
  59 passed and one deliberate PostgreSQL-only skip per iteration;
- production and test Release builds passed with warnings as errors, and scoped
  format plus whitespace verification passed.

The release remains NO-GO. Session defaults, full-proxy authentication and route
abuse boundaries, readiness, public errors, detached repair lifecycle, safe
rclone update, and immutable release provenance remain V1 blockers.

One read-only production documentation search occurred after explicit user
authorization. No production database, blob tree, service, container, ARR,
Plex, mount, configuration, or credential was mutated or stress-tested.

## Completed work

### V1 Tasks 0-1

**VERIFIED FACT:** V1 Task 0 froze the clean-install Docker/SQLite/one-owner
boundary. V1 Task 1 restored deterministic backend test truth, reconciled the
whole-cache sentinel contract without an unsupported production semantic
change, proved transactional cleanup rollback after deletion begins, exercised
the active rclone path, and repaired NNTP greeting cancellation by disposing the
underlying UsenetSharp client at the cancellation boundary.

### Deferred Transfer-v3 Phase 4 Tasks 0-7

**VERIFIED FACT:** Tasks 0-7 are `COMPLETE` in the reconciled plan. Current
2026-07-16 pure regression ran 1,372 Transfer-v3 tests. Its 1,370 passes cover
the completed foundations; the only two failures are new Task 8 structural
tests. Historical focused counts and reviews remain recorded in the local
progress ledger, but the canonical completion evidence required for continuation
is reproduced in this handoff and the active plan.

Per-task implementation and evidence:

- Task 0, boundary/baseline: design heading in
  `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md`.
  Historical gate:
  `env -u NZBDAV_TEST_POSTGRES_CONNECTION_STRING -u NZBDAV_REQUIRE_POSTGRES_TESTS dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~backend.Tests.Database.Transfer' --logger 'console;verbosity=minimal'`.
  Acceptance recorded: 670 passed, 6 deliberate skips, Release build clean.
- Task 1, fixed/redacted failure model:
  `backend/Database/Transfer/Phase4/TransferV3Phase4Failure.cs`, symbol
  `TransferV3Phase4FailureMapper.Sanitize`. Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4FailureTests'`.
  Acceptance recorded: focused 52/52 and pure regression 96/96.
- Task 2, target mapping and Npgsql pin:
  `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContract.cs`,
  symbol `TransferV3PostgreSqlTargetContract.LoadEmbedded`, plus
  `backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json`.
  Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTargetContractTests'`.
  Acceptance recorded: focused 9/9 and mapping regression 20/20.
- Task 3, managed/staging budgets and trusted parent:
  `backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs`, symbols
  `TransferV3Phase4ManagedBudget` and `TransferV3Phase4Digest`, plus
  `backend/Database/Transfer/Phase4/TransferV3Phase4StagingParent.cs`.
  Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4ManagedBudgetTests|FullyQualifiedName~TransferV3Phase4DigestTests|FullyQualifiedName~TransferV3Phase4StagingLedgerTests|FullyQualifiedName~TransferV3Phase4StagingParentTests|FullyQualifiedName~TransferV3Phase4OptionsTests|FullyQualifiedName~TransferV3PosixOwnedDirectoryTests|FullyQualifiedName~TransferV3SnapshotDirectoryTests'`.
  Acceptance recorded: focused Debug/Release 114/114.
- Task 4, unopened descriptor/deadline:
  `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs`,
  symbol `TransferV3PostgreSqlTargetDescriptor`, and
  `backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs`.
  Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTargetDescriptorTests|FullyQualifiedName~TransferV3PostgreSqlDiagnosticsTests|FullyQualifiedName~TransferV3PostgreSqlDeadlineTests|FullyQualifiedName~TransferV3PostgreSqlTargetContractTests'`.
  Acceptance recorded: focused Debug/Release 196/196.
- Task 5, exact settings/identity/session lifecycle:
  `backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs`,
  `TransferV3PostgreSqlTargetIdentity.cs`, and
  `TransferV3PostgreSqlSession.cs`. Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlServerContractTests|FullyQualifiedName~TransferV3PostgreSqlTargetIdentityTests|FullyQualifiedName~TransferV3PostgreSqlSessionTests|FullyQualifiedName~TransferV3PostgreSqlDeadlineTests|FullyQualifiedName~PostgreSqlEnvironmentContractTests'`.
  Acceptance recorded: focused Debug/Release 395 with 12 deliberate live skips.
- Task 6, exact history/bootstrap:
  `backend/Database/PostgreSqlNativeMigrationContract.cs`, symbol
  `PostgreSqlNativeMigrationContract`, and
  `backend/Database/PostgreSqlFreshBootstrapContract.cs`, symbol
  `PostgreSqlFreshBootstrapContract`. Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~PostgreSqlNativeMigrationContractTests|FullyQualifiedName~PostgreSqlFreshBootstrapContractTests'`.
  Acceptance recorded: focused Debug/Release 174/174 plus 215 provider/SQLite
  refusal tests and 51 codec tests.
- Task 7, transaction-bound state operations:
  `backend/Database/Transfer/TransferV3ImportStateStore.cs`, symbols
  `TryTransitionInPostgreSqlTransactionAsync` and
  `ReadForShareInPostgreSqlTransactionAsync`. Historical gate:
  `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3ImportStateStorePostgreSqlContractTests'`.
  Acceptance recorded: focused 11/11, provider regression 53/53, and pure
  Debug/Release 1,338/1,338.

### Task 8 supporting implementation

**VERIFIED FACT:** Current source provides:

- immutable 29-relation target-derived lock order;
- nested component-wise advisory key;
- schema-qualified `EXCLUSIVE MODE NOWAIT` relation lock;
- exact open-owner-ready `READ COMMITTED` gates;
- transaction-bound server/environment revalidation;
- six-argument admission with same-budget digest ownership;
- exact 35-byte and 123-byte `Copy` leases before allocation;
- allocation-free canonical span construction and digest copy;
- borrowed-buffer raw canonical PostgreSQL CAS;
- buffer zeroing/reference clearing before reverse lease release;
- no commit, rollback, connection/session/digest lifecycle ownership;
- no production call site. Task 18 owns coordinator wiring.

Evidence: backend Release build passed; affected contracts passed 390 with 12
deliberate no-connection skips.

## Work in progress

### V1 Task 1, deterministic backend test truth

What exists:

- exact reproduction of the Usenet testhost stack overflow;
- exact reproduction of the three cleanup/visibility failures;
- source tracing through cleanup, snapshot interceptor, database context,
  rclone invalidation service, and rclone client;
- a complete six-seat V1 council and approved backend release design/plan.

What remains, in order:

1. separate the Usenet test's non-disposable collecting sink from logger
   ownership and rerun the isolated class;
2. prove whether the whole-cache sentinel intentionally subsumes narrower
   invalidations or whether production violates a consumer contract;
3. repair the correct test or runtime contract through red-green TDD;
4. rerun the complete backend suite without an excluded class;
5. repeat affected concurrency gates ten times;
6. record exact results in the active V1 plan and this handoff.

### Deferred Transfer-v3 Phase 4

Tasks 8-21 are deferred post-V1. Task 8's focused and pure executable gates are
green, but review remains conflicting because only the P1 review examined the
pre-budget catalog materialization path. Do not edit Phase 4, start Task 9, or
represent Task 8 as complete during V1 work.

Safety: private Phase 4 code remains unregistered with no production call site;
PostgreSQL remains disabled.

## Not started

V1 Tasks 2-10 are `NOT STARTED`; V1 Task 11 rebrand is `BLOCKED` pending the
backend freeze and release-candidate evidence. Historical Phase 4 Tasks 9-21
remain `NOT STARTED` and post-V1:

- Task 9: one-deadline MVCC-safe commit reconciliation;
- Task 10: async parser observer;
- Task 11: source receipts and representability/staging preflight;
- Task 12: bounded in-memory batch and spool;
- Task 13: binary/text COPY codecs;
- Task 14: table import and committed-batch receipts;
- Task 15: private target blob-stage construction;
- Task 16: current-tree verification and typed stage lifecycle;
- Task 17: independent target row verification and final CAS;
- Task 18: coordinator and terminal lifecycle;
- Task 19: isolated helper process;
- Task 20: owned PostgreSQL completion/crash/contention/log proof;
- Task 21: isolation, documentation, full regression, and final reviews.

Do not interpret either plan's future task text as implemented behavior.

## Changed-file ledger

### Documentation changes made by this pass

| Path | Pre-edit Git state | Role in the work | Verified current behavior | Remaining work | Evidence | Risk or caution |
| --- | --- | --- | --- | --- | --- | --- |
| `AGENTS.md` | Tracked, modified by this V1 pivot | Project context | Points to the V1 handoff, plan, design, scope, and stable safety constraints | Remove active pointer only after V1 completion | Path/link audit | Durable in the signed WIP checkpoint |
| `HANDOFF.md` | Tracked, modified by this V1 pivot | Sole canonical handoff | Records the V1 boundary, council, blockers, current evidence, and exact continuation | Maintain each session | Final consistency audit | Do not add another competing handoff |
| `CONTRIBUTING.md` | Clean tracked file | Developer setup/verification | Declares .NET 10, Node 24/npm 11, `npm ci`, current local/CI gates | Keep synchronized with CI | Source review | Frontend cannot run under current Node 22/npm 10 |
| `docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md` | Created by this V1 pivot | Canonical active plan | Orders deterministic backend, security, readiness, lifecycle, rclone, resilience, behavioral E2E, artifact, and RC gates | Continue Task 2 | Six-seat council plus current executable evidence | Durable in the signed WIP checkpoint |
| `docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md` | Created by this V1 pivot | Governing V1 design | Freezes Docker/SQLite/one-owner/clean-install boundary and release definition of done | Implement active plan | Six-seat council plus current executable evidence | Durable in the signed WIP checkpoint |
| `docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md` | Tracked, modified by this V1 pivot | Deferred post-V1 plan | Preserves Tasks 0-7 and unsealed Task 8 evidence without governing continuation | Resolve catalog-memory finding post-V1 | Current source/tests and conflicting reviews | Planned behavior remains private and non-shipped |
| `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md` | Tracked, unchanged by this V1 pivot | Deferred Phase 4 design | Preserves the private PostgreSQL design and memory contract | Post-V1 only | Source/design comparison | Do not broaden runtime reachability |
| `.superpowers/sdd/progress.md` | Pre-existing, locally excluded | Supplemental local execution ledger | Points to Phase 4 and records Task 8 failures/accounting repair | Optional local update after repair | Local file review | Deliberately noncanonical and not committed; no continuation step depends on it |
| `.superpowers/sdd/phase4/task-8/task-8-brief.md` | Pre-existing, locally excluded | Supplemental task-scoped brief | Reconciled with six-argument admission and current gates | Optional local update after repair | Local file review | Deliberately noncanonical and not committed; no continuation step depends on it |

### Historical Task 8 implementation ledger, now deferred post-V1

The table below preserves the initial handoff's file-by-file Task 8 snapshot.
Its former structural failures were repaired and its pure gates are green, but
the later catalog-materialization P1 review prevents sealing. Current V1 work
must not edit these paths.

| Path | Pre-edit Git state | Role in the work | Verified current behavior | Remaining work | Evidence | Risk or caution |
| --- | --- | --- | --- | --- | --- | --- |
| `backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs` | Untracked | Advisory/relation locks | Exact immutable 29-relation `EXCLUSIVE MODE NOWAIT` and advisory key | Final review and Task 20 live proof | 15 pure lock tests within exact gate | Never weaken to `SHARE ROW EXCLUSIVE` |
| `backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs` | Untracked | Target admission orchestration | Full locked preflight, budgeted canonical CAS, sanitization, cleanup | Join raw CAS receiver/member expression into exact reviewed shape | 2 current structural failures identify this expression | Change no ordering, SQL, allocation, or lifecycle |
| `backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs` | Untracked | Owned session | Exposes descriptor-frozen `TimeZoneId` and borrowed connection | Final review | Affected contract gate passed | Caller owns lifecycle |
| `backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs` | Untracked | Transaction-bound identity/settings | Validates connection ownership/readiness and forwards transaction | Final review | Affected contract gate passed | Preserve existing overloads |
| `backend/Database/PostgreSqlEnvironmentContract.cs` | Untracked | Transaction-bound environment | Validates open owner-ready transaction and exact schema environment | Final review and Task 20 live proof | Affected contract gate passed with deliberate live skips | Do not use existing server |
| `backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs` | Untracked | Digest/budget ownership | `TransferV3Phase4Digest.ValidateOwner` enforces live creating budget | Final review | Digest component tests passed | One budget only |
| `backend/Database/Transfer/TransferV3ImportStateCodec.cs` | Untracked | Canonical state bytes | Exact 35/123 span writers and strict transition predicate | Final review | Codec component tests passed | Canonical bytes are compatibility contract |
| `backend/Database/Transfer/TransferV3ImportStateStore.cs` | Untracked | Reserved-state persistence | Additive borrowed-buffer PostgreSQL CAS reuses existing executor | Final review and later Task 20 live proof | Store component tests passed | Preserve Task 7 API and illegal-edge zero semantics |
| `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSetTests.cs` | Untracked | Lock source contract | 15 pure checks | Final review | Exact Task 8 run | No live dependency |
| `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs` | Untracked | Admission source/accounting contract | 9 of 11 facts pass | Repair two expression-dependent facts and two `xUnit2000` assertions | Exact Task 8 run and clean test build | Do not silence analyzers |
| `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs` | Untracked | Digest ownership/accounting | Same/different/null/disposed owner coverage | Final review | Affected gate passed | No budget drift |
| `backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs` | Untracked | Canonical vectors/allocation | Exact bytes, mutation, wrong-length, zero-allocation coverage | Final review | Affected gate passed | Avoid weakening canonical grammar |
| `backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs` | Untracked | Raw CAS/store contract | Signature, order, SQL delegation, non-ownership coverage | Final review | Affected gate passed | Live behavior deferred |
| `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlServerContractTests.cs` and `backend.Tests/Database/PostgreSqlEnvironmentContractTests.cs` | Untracked | Shared validator coverage | Transaction-bound wrappers and environment checks | Task 20 live cases | Affected gate passed, 12 live skips | Pre-Task-8 hashes recovered as `0c71b5ed5aca317cbdb7b09997ce76cc96fd8cc11391051c77e169a86085b614` and `2a37668b805c48ed79caf0aaa958d8c0615d16ee198fd15ae05b7799888894b1` from the Task 5 after-snapshot; Tasks 6-7 recorded no unrelated-file changes |

### Other pre-existing dirty work

Every other path in the appendix predates this documentation pass. It spans
role separation, ARR/SAB/queue behavior, database/provider work, maintenance,
telemetry, UI, CI/release gates, benchmarks, test artifacts, and generated
caches. Ownership is mixed and not reconstructed as part of the active Phase 4
mission. Preserve all of it.

High-risk groups:

| Path | Pre-edit Git state | Role in the work | Verified current behavior | Remaining work | Evidence | Risk or caution |
| --- | --- | --- | --- | --- | --- | --- |
| `.github/workflows/*.yml`, including untracked `verify.yml` | Modified/untracked | Earlier CI/release work, outside Phase 4 Task 8 | Reusable workflow and callers exist | None in this documentation pass | Pre-edit inventory and source review | Do not lose the untracked workflow while callers already reference it |
| `backend/**` and `backend.Tests/**` outside active Task 8 | Modified/untracked | Multiple earlier backend workstreams | Mixed partial and verified behavior, not reconstructed as one workstream here | Preserve for their owning plans | Pre-edit inventory, plans, tests, and source review | No blanket formatting, cleanup, or attribution to Phase 4 |
| `frontend/**` | Modified/untracked | Earlier UI/runtime work, outside Phase 4 | Current local Node/npm do not meet declared versions | Verify only under Node 24/npm 11 in its owning workstream | Pre-edit inventory and declared package engines | Do not run package rewrites under Node 22/npm 10 |
| `artifacts/**` and `*.trx` | Untracked | Historical evidence and screenshots | Present before this pass | None | Pre-edit inventory | Never stage or delete without explicit authorization |
| `**/.DS_Store` and `**/__pycache__/**` | Untracked | Generated local residue unrelated to Phase 4 | Present before this pass | None | Pre-edit inventory | Do not clean automatically |
| `docs/setup-guide.md` and older plans/specs | Modified/untracked | Historical/provider documentation | Setup guide still keeps SQLite supported and PostgreSQL disabled | Reconcile only within owning future tasks | Documentation review | Do not overwrite with unfinished Phase 4 shipped claims |
| `benchmarks/**`, `scripts/**`, `tests/**` | Modified/untracked | Performance, migration, release, and tooling work outside Task 8 | Mixed earlier work preserved | Continue only from their owning plans | Pre-edit inventory and source review | Do not fold into Task 8 or delete generated evidence |

## Relevant architecture and data flow

Current Task 8 call path:

`TransferV3PostgreSqlAdmissionValidator.ValidateFreshAndMarkImportingAsync`

1. validates timeout, session, transaction, digest, and managed budget;
2. calls `manifestDigest.ValidateOwner(managedBudget)`;
3. borrows one connection from `TransferV3PostgreSqlSession`;
4. calls `TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync`;
5. calls `AcquireRelationsAsync` for all 29 target relations;
6. recaptures settings/identity with
   `TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync`;
7. revalidates target environment with
   `PostgreSqlEnvironmentContract.ValidateAsync`;
8. validates physical catalog, migration head, and fresh bootstrap;
9. reserves 35/123-byte `Copy` leases and allocates exact arrays;
10. writes canonical `fresh` and `importing(A)` bytes through
    `TransferV3ImportStateCodec`;
11. calls
    `TransferV3ImportStateStore.TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync`;
12. requires one row, sanitizes operational failures, zeroes arrays, and releases
    leases in reverse order.

The validator does not commit. Task 18 will own transaction lifecycle and call
it only after typed source preflight proof. Task 9 will reconcile ambiguous
commit outcomes. Task 20 will prove real PostgreSQL lock behavior and durability
inside an exclusively owned harness.

State transition for this phase:

`fresh -> importing(A) -> database-verified(A)`

Failure after committed admission eventually becomes `failed(A)` only under the
later coordinator policy. Unknown commit outcomes never authorize failed-state
CAS or destructive cleanup.

## Decisions and rationale

| Decision | Classification | Rationale/evidence |
| --- | --- | --- |
| SQLite remains production default | VERIFIED FACT | Global plan/design constraint; no runtime registration changed |
| PostgreSQL remains disabled/private | VERIFIED FACT | Phase 4 is test-helper-only and has no admission call site |
| Keep one managed budget | VERIFIED FACT | Digest owner gate plus exact Copy leases prevent a side budget |
| `EXCLUSIVE MODE NOWAIT` | VERIFIED FACT | Blocks prior `ROW SHARE` locking readers while allowing plain `ACCESS SHARE` readers; avoids later CAS wait/deadlock |
| Exact `READ COMMITTED` | VERIFIED FACT | Advisory `SELECT` precedes relation lock; stronger snapshot levels could freeze stale state |
| Raw canonical CAS is additive | VERIFIED FACT | Avoids uncharged digest string/state allocations while preserving Task 7 API |
| Live PostgreSQL proof deferred to Task 20 | VERIFIED FACT | Task 8 must remain pure and must not use external/unowned databases |
| Preserve the complete implementation in a WIP checkpoint | VERIFIED FACT | User authorized consolidation after review; checkpoint is durable but explicitly blocked from main, deployment, and release |
| PostgreSQL promotion/cutover rejected now | VERIFIED FACT | Outside Phase 4; exact completion and runtime enablement gates are unfinished |

## Invariants and constraints

- Preserve the signed WIP checkpoint and every unexplained new path.
- No reset, clean, restore, checkout, stash, rebase, merge, pull, branch switch,
  stage, commit, push, PR, remote mutation, or force operation without explicit
  authorization.
- Use `apply_patch` for edits. Avoid unrelated formatting.
- Never access a real SQLite database, PostgreSQL database, blob tree, container,
  service, host, mount, or user configuration.
- No connection string, credential, token, API key, payload, digest, UUID,
  absolute local path, or raw provider exception in documentation or captures.
- PostgreSQL 16.14 and Npgsql 10.0.3 are exact gates.
- Exact managed budget is 32 MiB, including 8 MiB runtime reserve.
- Every provider command has a positive finite timeout.
- Caller owns session, connection, transaction, and digest lifecycle.
- Canonical state bytes and transition graph are compatibility contracts.
- Task 8 tests are pure. Task 20 owns all live PostgreSQL proof.
- Do not modify `backend/Program.cs`, `entrypoint.sh`, provider selection,
  Compose, controllers, or runtime registration to expose Phase 4.
- Do not start Task 9 before Task 8 is fully green, reviewed, and recorded.
- Do not document unfinished Phase 4 behavior as shipped.

## Documentation impact

| Document | Result |
| --- | --- |
| `HANDOFF.md` | Rewritten as sole canonical current handoff and included in the authorized documentation commit |
| `AGENTS.md` | Created as Hermes project context/resume pointer and included in the authorized documentation commit |
| Phase 4 plan | Reconciled statuses, Task 8 scope/interfaces/failures/next action and included in the authorized documentation commit |
| Phase 4 design | Updated exact same-budget canonical-buffer admission lifecycle and included in the authorized documentation commit |
| Local progress and Task 8 brief | Reconciled but deliberately left locally excluded, supplemental, and noncanonical |
| `CONTRIBUTING.md` | Updated declared runtimes and tracked verification surface, then included in the authorized documentation commit |
| `README.md` | Reviewed, no Phase 4 change required |
| `docs/setup-guide.md` | Reviewed, already correctly keeps SQLite supported and PostgreSQL disabled |
| `backend/Database/Transfer/Contracts/README.md` | Reviewed, source-boundary description remains correct |
| `frontend/README.md` | Reviewed as unrelated generic template; not changed |
| `CHANGELOG.md` | Not applicable. Phase 4 is not shipped. |

No Markdown lint, documentation build, or repository link checker is configured.
Relative-link existence and whitespace are verified explicitly below.

## Verification ledger

All commands ran from repository root. No command used a live database or
container.

| Timestamp | Command | Working directory | Result | Exit code | Meaning | Follow-up |
| --- | --- | --- | --- | ---: | --- | --- |
| 2026-07-16T22:05:49+04:00 | `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter '(FullyQualifiedName~TransferV3PostgreSqlAdmissionLockSetTests\|FullyQualifiedName~TransferV3PostgreSqlAdmissionTests)&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'` | `.` | BLOCKED | 1 | Sandbox denied testhost loopback socket before tests ran | Repeated with local socket permission |
| 2026-07-16T22:05:58+04:00 | `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter '(FullyQualifiedName~TransferV3PostgreSqlAdmissionLockSetTests\|FullyQualifiedName~TransferV3PostgreSqlAdmissionTests)&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'` | `.` | FAIL | 1 | 24 passed, 2 failed, 0 skipped; rerun with local testhost socket permitted | Repair exact raw CAS expression shape |
| 2026-07-16T22:06:15+04:00 | `dotnet build backend/NzbWebDAV.csproj --configuration Release --no-restore -warnaserror` | `.` | PASS | 0 | Backend build, 0 warnings/errors | Retain after repair |
| 2026-07-16T22:06:24+04:00 | `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~TransferV3&FullyQualifiedName!~TransferV3ImportStateStorePostgreSqlTests&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'` | `.` | BLOCKED | unavailable | First orchestration attempt returned partial failure output without a conclusive exit | Repeated conclusively |
| 2026-07-16T22:07:22+04:00 | `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~TransferV3&FullyQualifiedName!~TransferV3ImportStateStorePostgreSqlTests&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'` | `.` | FAIL | 1 | 1,370 passed, 2 failed, 0 skipped; same Task 8 failures | Repair and rerun |
| 2026-07-16T22:08:16+04:00 | `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter '(FullyQualifiedName~TransferV3Phase4DigestTests\|FullyQualifiedName~TransferV3ImportStateCodecTests\|FullyQualifiedName~TransferV3ImportStateStorePostgreSqlContractTests\|FullyQualifiedName~TransferV3PostgreSqlServerContractTests\|FullyQualifiedName~TransferV3PostgreSqlSessionTests\|FullyQualifiedName~PostgreSqlEnvironmentContractTests)&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'` | `.` | PASS | 0 | 390 passed, 12 deliberate no-connection skips | Retain after repair |
| 2026-07-16T22:08:35+04:00 | `dotnet build backend.Tests/backend.Tests.csproj --configuration Release --no-restore -warnaserror` | `.` | PASS | 0 | Incremental build reported clean | Superseded by clean build below |
| 2026-07-16T22:08:42+04:00 | `dotnet build backend.Tests/backend.Tests.csproj --configuration Release --no-restore --no-incremental -warnaserror` | `.` | BLOCKED | unavailable | Returned after backend output without conclusive exit | Repeated conclusively |
| 2026-07-16T22:09:21+04:00 | `dotnet build backend.Tests/backend.Tests.csproj --configuration Release --no-restore --no-incremental -warnaserror` | `.` | FAIL | 1 | Two `xUnit2000` errors at admission-test canonical-length assertions | Swap expected/actual |
| 2026-07-16T22:10:41+04:00 | `dotnet format backend.Tests/backend.Tests.csproj --no-restore --verify-no-changes --include backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs backend/Database/Transfer/TransferV3ImportStateCodec.cs backend/Database/Transfer/TransferV3ImportStateStore.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs backend/Database/PostgreSqlEnvironmentContract.cs backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSetTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs` | `.` | FAIL | 2 | Same two `xUnit2000` diagnostics | Repair assertions and rerun |
| 2026-07-16T22:11:21+04:00 | `env -u NZBDAV_TEST_POSTGRES_CONNECTION_STRING -u NZBDAV_REQUIRE_POSTGRES_TESTS dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --logger 'console;verbosity=minimal'` | `.` | FAIL | 1 | 2,745 passed, 2 failed, 84 deliberate PostgreSQL skips; only same Task 8 failures | Repair and rerun full suite at Task 21 or when Task 8 requires |
| 2026-07-16T22:24:48+04:00 | `git diff --check` | `.` | PASS | 0 | No tracked-diff whitespace errors | None |
| 2026-07-16T22:24:48+04:00 | `! rg -n '[[:blank:]]+$' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | No trailing whitespace in documentation-pass files | None |
| 2026-07-16T22:24:48+04:00 | `for path in AGENTS.md HANDOFF.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs; do test -e "$path" \|\| exit 1; done` | `.` | PASS | 0 | Every local link target introduced or relied on by the handoff exists | External links remain outside this filesystem check |
| 2026-07-16T22:27:58+04:00 | `! rg -n '/[U]sers/\|/[o]pt/\|10[.]10[.]\|postgres(ql)?://\|[P]assword=\|[G]enerated with\|[C]o-authored-by\|[T]BD\|[F]IXME:\|[X]XX:\|[T]ODO:\|generic [T]ODO\|implement [l]ater\|add appropriate [h]andling\|write [t]ests\|similar to the previous [t]ask' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | No personal path, obvious secret value, provenance trailer, or forbidden unresolved placeholder | None |
| 2026-07-16T22:25:15+04:00 | `ruby -e 'text = File.read(ARGV.fetch(0)); sections = text.split(/^### Task /)[1..]; actual = sections.map { \|section\| [Integer(section.split(":", 2).first), section.lines.find { \|line\| line.start_with?("**Status:** ") }.split("**Status:** ", 2).last.strip] }; expected = (0..21).map { \|task\| [task, task <= 7 ? "COMPLETE" : task == 8 ? "IN PROGRESS" : "NOT STARTED"] }; abort("task status mismatch: #{actual.inspect}") unless actual == expected' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md` | `.` | PASS | 0 | All 22 task statuses match reconstructed state | Task 8 remains first unfinished task |
| 2026-07-16T22:24:48+04:00 | `ruby -e 'required = ["Handoff metadata", "Resume in 60 seconds", "Mission and definition of done", "Source-of-truth order", "Repository snapshot", "Current state summary", "Completed work", "Work in progress", "Not started", "Changed-file ledger", "Relevant architecture and data flow", "Decisions and rationale", "Invariants and constraints", "Documentation impact", "Verification ledger", "Known failures and blockers", "Environment and prerequisites", "Exact next actions", "Do not redo", "Deferred or out of scope", "Open questions", "Recovery and rollback", "Handoff maintenance protocol"]; text = File.read("HANDOFF.md"); missing = required.reject { \|heading\| text.include?("## #{heading}\\n") }; abort("missing headings: #{missing.join(", ")}") unless missing.empty?'` | `.` | PASS | 0 | Every required handoff section exists | None |
| 2026-07-16T22:24:48+04:00 | `rg -q 'ValidateFreshAndMarkImportingAsync' backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs && rg -q 'CanonicalSpanCodecWritesExactFreshAndImportingBytesAndRejectsMutations' backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs` | `.` | PASS | 0 | First-action symbols exist | None |
| 2026-07-16T22:24:48+04:00 | `test "$(rg --files --hidden -g 'HANDOFF*' \| wc -l \| tr -d ' ')" = 1` | `.` | PASS | 0 | Exactly one handoff artifact exists | Keep `HANDOFF.md` canonical |
| 2026-07-16T22:27:58+04:00 | ``rg -Fq 'Canonical handoff: `HANDOFF.md`' AGENTS.md && rg -Fq 'Active implementation plan: `docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md`' AGENTS.md && rg -Fq 'docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md' HANDOFF.md && rg -Fq '../../../HANDOFF.md' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md`` | `.` | PASS | 0 | Hermes context, handoff, and plan cross-link correctly | None |
| 2026-07-16T22:24:48+04:00 | `git diff --cached --quiet` | `.` | PASS | 0 | Staged set remains empty | None |
| 2026-07-16T22:24:48+04:00 | `git branch --show-current; git rev-parse HEAD; git rev-list --left-right --count HEAD...origin/main` | `.` | PASS | 0 | Branch and HEAD unchanged; 29 ahead and 0 behind `origin/main` | No upstream is configured |
| 2026-07-16T22:24:48+04:00 | `git diff --stat`; `git diff --name-status`; `git status --short --branch` | `.` | PASS | 0 | Final Git-visible baseline reviewed; 158 tracked modifications and 283 untracked paths, with only the three documented Git-visible additions to the pre-edit sets | Preserve all pre-existing work |
| 2026-07-16T22:45:05+04:00 | `git diff --check` | `.` | PASS | 0 | No tracked-diff whitespace errors after the final planning corrections | None |
| 2026-07-16T22:45:05+04:00 | `! rg -n '[[:blank:]]+$' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | No trailing whitespace after the final planning corrections | None |
| 2026-07-16T22:45:05+04:00 | `! rg -n '/[U]sers/\|/[o]pt/\|10[.]10[.]\|/where/to/\|/path/to/\|postgres(ql)?://\|[P]assword=\|[G]enerated with\|[C]o-authored-by\|[T]BD\|[F]IXME:\|[X]XX:\|[T]ODO:\|generic [T]ODO\|implement [l]ater\|add appropriate [h]andling\|write [t]ests\|similar to the previous [t]ask' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | No personal path, obvious secret value, provenance trailer, or forbidden unresolved placeholder after the final corrections | None |
| 2026-07-16T22:45:05+04:00 | `ruby -e 'text = File.read(ARGV.fetch(0)); sections = text.split(/^### Task /)[1..]; abort("expected 22 tasks") unless sections.length == 22; required = ["**Status:**", "**Objective:**", "**Dependency and ordering:**", "**Preconditions:**", "**Interfaces consumed and produced:**", "**Current evidence", "**Expected result and acceptance:**", "**Documentation impact:**", "**Recovery and risk"]; sections.each do \|section\|; task = Integer(section.split(":", 2).first); missing = required.reject { \|field\| section.include?(field) }; abort("Task #{task} missing #{missing.join(", ")}") unless missing.empty?; status = section.lines.find { \|line\| line.start_with?("**Status:** ") }.split("**Status:** ", 2).last.strip; boxes = section.lines.grep(/^- \[[ x]\]/); checked = boxes.count { \|line\| line.start_with?("- [x]") }; expected = task <= 7 ? "COMPLETE" : task == 8 ? "IN PROGRESS" : "NOT STARTED"; abort("Task #{task} status #{status}") unless status == expected; abort("Task #{task} checkbox mismatch") if status == "COMPLETE" && checked != boxes.length; abort("Task 8 checkbox mismatch") if task == 8 && !(checked.positive? && checked < boxes.length); abort("Task #{task} checkbox mismatch") if status == "NOT STARTED" && checked != 0; end' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md` | `.` | PASS | 0 | All 22 tasks retain required fields, reconciled statuses, and status-consistent checkboxes | Task 8 remains first unfinished task |
| 2026-07-16T22:45:05+04:00 | `ruby -e 'required = ["Handoff metadata", "Resume in 60 seconds", "Mission and definition of done", "Source-of-truth order", "Repository snapshot", "Current state summary", "Completed work", "Work in progress", "Not started", "Changed-file ledger", "Relevant architecture and data flow", "Decisions and rationale", "Invariants and constraints", "Documentation impact", "Verification ledger", "Known failures and blockers", "Environment and prerequisites", "Exact next actions", "Do not redo", "Deferred or out of scope", "Open questions", "Recovery and rollback", "Handoff maintenance protocol"]; lines = File.readlines("HANDOFF.md"); text = lines.join; missing = required.reject { \|heading\| text.include?("## #{heading}\\n") }; abort("missing headings: #{missing.join(", ")}") unless missing.empty?; abort("changed ledgers") unless text.scan(%r{^\| Path \| Pre-edit Git state \| Role in the work \| Verified current behavior \| Remaining work \| Evidence \| Risk or caution \|$}).length == 3; start = lines.index { \|line\| line.start_with?("\| Timestamp \| Command \| Working directory \|") }; abort("verification ledger missing") unless start; rows = lines[(start + 2)..].take_while { \|line\| line.start_with?("\|") }; bad = rows.each_with_index.select { \|line, _\| line.gsub(%q{\|}, "").count("\|") != 8 }; abort("bad verification rows #{bad.map { \|_, i\| i + 1}.inspect}") unless bad.empty?'` | `.` | PASS | 0 | Every required handoff section and ledger schema remained valid before the final ledger append | None |
| 2026-07-16T22:45:05+04:00 | `ruby -e 'ARGV.each do \|file\|; File.readlines(file).each_with_index do \|line, i\|; line.scan(%r{\]\\(([^)]+)\\)}).flatten.each do \|target\|; next if target.match?(%r{\\A(?:https?:\|mailto:\|#)}); target = target.split("#", 2).first; path = File.expand_path(target, File.dirname(file)); abort("#{file}:#{i + 1}: missing #{target}") unless File.exist?(path); end; end; end' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | Every relative Markdown link in the documentation-pass files resolves | None |
| 2026-07-16T22:45:05+04:00 | ``rg -Fq 'Canonical handoff: `HANDOFF.md`' AGENTS.md && rg -Fq 'Active implementation plan: `docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md`' AGENTS.md && rg -Fq 'Task 8: IN PROGRESS' .superpowers/sdd/progress.md && test "$(rg -c '^- Task (9\|10\|11\|12\|13\|14\|15\|16\|17\|18\|19\|20\|21): NOT STARTED$' .superpowers/sdd/progress.md)" = 13 && test "$(shasum -a 256 docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md \| cut -d ' ' -f 1)" = "$(sed -n 's/^Plan SHA-256: `\\([0-9a-f]*\\)`.*/\\1/p' .superpowers/sdd/progress.md)"`` | `.` | PASS | 0 | Hermes pointers, Task 8 status, Tasks 9-21 status, and current plan hash agree | None |
| 2026-07-16T22:45:05+04:00 | `test "$(git branch --show-current)" = 'codex/single-host-role-separation-design' && test "$(git rev-parse HEAD)" = '8c06d9caacd8c0d2ab5d69f47e3b230b75b16704' && test "$(git rev-list --left-right --count HEAD...origin/main)" = $'29\\t0' && git diff --cached --quiet` | `.` | PASS | 0 | Branch, HEAD, base-relative counts, and empty staged set still match the snapshot | No upstream is configured |
| 2026-07-16T22:45:05+04:00 | `ruby -e 'text = File.read("HANDOFF.md"); a = text.split("## Appendix A", 2).last.split("```text", 2).last.split("```", 2).first.lines.map(&:chomp).reject(&:empty?); b = text.split("## Appendix B", 2).last.split("```text", 2).last.split("```", 2).first.lines.map(&:chomp).reject(&:empty?); abort("appendix counts #{a.length}/#{b.length}") unless a.length == 156 && b.length == 282'` | `.` | PASS | 0 | Pre-edit inventories remain complete at 156 tracked and 282 untracked paths | Preserve every baseline path |
| 2026-07-16T22:45:05+04:00 | `rg -Fq 'contract, managed budget/staging parent, and source bootstrap contract' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md && rg -Fq 'budget/ledger, and POSIX streaming directory. Produces target' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md && ! sed -n '1905,1925p' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md \| rg -q 'target contract'` | `.` | PASS | 0 | Task 11 and Task 15 interface metadata match their declared implementation contracts | None |
| 2026-07-16T22:46:05+04:00 | `! rg -n '/[U]sers/\|/[o]pt/\|10[.]10[.]\|/where/to/\|/path/to/\|postgres(ql)?://\|[P]assword=\|[G]enerated with\|[C]o-authored-by\|[T]BD\|[F]IXME:\|[X]XX:\|[T]ODO:\|generic [T]ODO\|implement [l]ater\|add appropriate [h]andling\|write [t]ests\|similar to the previous [t]ask' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | FAIL | 1 | The new verification row contained the exact regex text, so this whole-file scan matched its own ledger entry; no repository-content finding was reported | Replace with a ledger-aware scan |
| 2026-07-16T22:46:30+04:00 | `ruby -e 'pattern = %r{/[U]sers/\|/[o]pt/\|10[.]10[.]\|/where/to/\|/path/to/\|postgres(?:ql)?://\|[P]assword=\|[G]enerated with\|[C]o-authored-by\|[T]BD\|[F]IXME:\|[X]XX:\|[T]ODO:\|generic [T]ODO\|implement [l]ater\|add appropriate [h]andling\|write [t]ests\|similar to the previous [t]ask}; findings = ARGV.flat_map do \|file\|; File.readlines(file).each_with_index.filter_map do \|line, index\|; next if file == "HANDOFF.md" && line.start_with?("\| 2026-"); "#{file}:#{index + 1}:#{line}" if line.match?(pattern); end; end; abort(findings.join) unless findings.empty?' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | BLOCKED | 1 | Installed Ruby lacks `Enumerator#filter_map`; scan did not run | Repeat with version-compatible iteration |
| 2026-07-16T22:46:38+04:00 | `ruby -e 'pattern = %r{/[U]sers/\|/[o]pt/\|10[.]10[.]\|/where/to/\|/path/to/\|postgres(?:ql)?://\|[P]assword=\|[G]enerated with\|[C]o-authored-by\|[T]BD\|[F]IXME:\|[X]XX:\|[T]ODO:\|generic [T]ODO\|implement [l]ater\|add appropriate [h]andling\|write [t]ests\|similar to the previous [t]ask}; findings = []; ARGV.each do \|file\|; File.readlines(file).each_with_index do \|line, index\|; next if file == "HANDOFF.md" && line.start_with?("\| 2026-"); findings << "#{file}:#{index + 1}:#{line}" if line.match?(pattern); end; end; abort(findings.join) unless findings.empty?' HANDOFF.md AGENTS.md CONTRIBUTING.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md .superpowers/sdd/progress.md .superpowers/sdd/phase4/task-8/task-8-brief.md` | `.` | PASS | 0 | Ledger-aware scan found no personal path, obvious secret value, provenance trailer, or forbidden unresolved placeholder | None |
| 2026-07-16T23:29:54+04:00 | `test "$(sed -n '9s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '0c71b5ed5aca317cbdb7b09997ce76cc96fd8cc11391051c77e169a86085b614' && test "$(sed -n '13s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '2a37668b805c48ed79caf0aaa958d8c0615d16ee198fd15ae05b7799888894b1' && rg -Fq 'touched no container, service, blob tree, Git index, or unrelated' .superpowers/sdd/phase4/task-6/task-6-report.md && rg -Fq 'connection and touched no container, service, blob tree, Git index, or unrelated' .superpowers/sdd/phase4/task-7/task-7-report.md` | `.` | FAIL | 1 | Exact hashes matched, but line-oriented `rg` could not match wrapped report prose | Repeat with multiline report matching |
| 2026-07-16T23:30:11+04:00 | `test "$(sed -n '9s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '0c71b5ed5aca317cbdb7b09997ce76cc96fd8cc11391051c77e169a86085b614' && test "$(sed -n '13s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '2a37668b805c48ed79caf0aaa958d8c0615d16ee198fd15ae05b7799888894b1' && ruby -e 'ARGV.each { \|path\| abort(path) unless File.read(path).match?(/touched no container, service, blob tree, Git index,\s+or unrelated dirty file/m) }' .superpowers/sdd/phase4/task-6/task-6-report.md .superpowers/sdd/phase4/task-7/task-7-report.md` | `.` | FAIL | 1 | Multiline check still required whitespace tolerance between `unrelated` and `dirty` | Repeat with both line breaks accepted |
| 2026-07-16T23:30:29+04:00 | `test "$(sed -n '9s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '0c71b5ed5aca317cbdb7b09997ce76cc96fd8cc11391051c77e169a86085b614' && test "$(sed -n '13s/ .*//p' .superpowers/sdd/phase4/task-5/task-files.after.txt)" = '2a37668b805c48ed79caf0aaa958d8c0615d16ee198fd15ae05b7799888894b1' && ruby -e 'ARGV.each { \|path\| abort(path) unless File.read(path).match?(/touched no container, service, blob tree, Git index,\s+or unrelated\s+dirty file/m) }' .superpowers/sdd/phase4/task-6/task-6-report.md .superpowers/sdd/phase4/task-7/task-7-report.md` | `.` | PASS | 0 | Both pre-Task-8 hashes recovered and Tasks 6-7 prove no unrelated-file changes | Attribution gap closed |
| 2026-07-16T23:35:50+04:00 | `test "$(git rev-parse HEAD)" = '8c06d9caacd8c0d2ab5d69f47e3b230b75b16704' && git diff --cached --quiet && test "$(git branch --show-current)" = 'codex/single-host-role-separation-design' && test "$(git rev-list --left-right --count HEAD...origin/main)" = $'29\\t0'` | `.` | PASS | 0 | Prepublication baseline, branch, relation, and empty index match the handoff | Abort publication if repeated result differs |
| 2026-07-16T23:35:50+04:00 | `test "$(git diff --name-only \| wc -l \| tr -d ' ')" = 158 && test "$(git ls-files --others --exclude-standard \| wc -l \| tr -d ' ')" = 283` | `.` | PASS | 0 | Before publication, 158 tracked modifications and 283 untracked paths match exact attribution arithmetic | Five-file publication should leave 156 and 280 |
| 2026-07-16T23:35:50+04:00 | `git diff --check` | `.` | PASS | 0 | Final prepublication tracked diff has no whitespace errors | Repeat after commit for residual worktree |
| 2026-07-16T23:35:50+04:00 | `ruby -e 'paths = Dir["backend/Database/Transfer/Phase4/**/*", "backend.Tests/Database/Transfer/Phase4/**/*"].select { \|path\| File.file?(path) }; present = paths.count { \|path\| system("git", "cat-file", "-e", "HEAD:#{path}", out: File::NULL, err: File::NULL) }; abort("phase4 portability #{paths.length}/#{present}") unless paths.length == 30 && present == 0'` | `.` | PASS | 0 | All 30 current Phase 4 production/test files are absent from the baseline commit | Documentation is portable; executable Task 8 remains preserved-worktree-only |
| 2026-07-16T23:35:50+04:00 | `! rg -n 'OPEN UNKNOWN\|future authorized commit\|Original Task 8 pre-edit hashes were not captured\|Task 8 report/after-snapshot' AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md && rg -Fq 'No handoff or documentation question remains open.' HANDOFF.md` | `.` | PASS | 0 | Former handoff unknowns and excluded-SDD completion dependencies are resolved | Task 20 remains planned work, not a handoff question |
| 2026-07-16T23:35:50+04:00 | `ruby -e 'text = File.read(ARGV.fetch(0)); sections = text.split(/^### Task /)[1..]; abort("expected 22 tasks") unless sections.length == 22; required = ["**Status:**", "**Objective:**", "**Dependency and ordering:**", "**Preconditions:**", "**Interfaces consumed and produced:**", "**Current evidence", "**Expected result and acceptance:**", "**Documentation impact:**", "**Recovery and risk"]; sections.each do \|section\|; task = Integer(section.split(":", 2).first); missing = required.reject { \|field\| section.include?(field) }; abort("Task #{task} missing #{missing.join(", ")}") unless missing.empty?; status = section.lines.find { \|line\| line.start_with?("**Status:** ") }.split("**Status:** ", 2).last.strip; boxes = section.lines.grep(/^- \[[ x]\]/); checked = boxes.count { \|line\| line.start_with?("- [x]") }; expected = task <= 7 ? "COMPLETE" : task == 8 ? "IN PROGRESS" : "NOT STARTED"; abort("Task #{task} status #{status}") unless status == expected; abort("Task #{task} checkbox mismatch") if status == "COMPLETE" && checked != boxes.length; abort("Task 8 checkbox mismatch") if task == 8 && !(checked.positive? && checked < boxes.length); abort("Task #{task} checkbox mismatch") if status == "NOT STARTED" && checked != 0; end' docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md` | `.` | PASS | 0 | All 22 tasks retain required fields, reconciled statuses, and status-consistent checkboxes | Task 8 remains first unfinished task |
| 2026-07-16T23:35:50+04:00 | `ruby -e 'required = ["Handoff metadata", "Resume in 60 seconds", "Mission and definition of done", "Source-of-truth order", "Repository snapshot", "Current state summary", "Completed work", "Work in progress", "Not started", "Changed-file ledger", "Relevant architecture and data flow", "Decisions and rationale", "Invariants and constraints", "Documentation impact", "Verification ledger", "Known failures and blockers", "Environment and prerequisites", "Exact next actions", "Do not redo", "Deferred or out of scope", "Open questions", "Recovery and rollback", "Handoff maintenance protocol"]; lines = File.readlines("HANDOFF.md"); text = lines.join; missing = required.reject { \|heading\| text.include?("## #{heading}\\n") }; abort("missing headings: #{missing.join(", ")}") unless missing.empty?; abort("changed ledgers") unless text.scan(%r{^\| Path \| Pre-edit Git state \| Role in the work \| Verified current behavior \| Remaining work \| Evidence \| Risk or caution \|$}).length == 3; start = lines.index { \|line\| line.start_with?("\| Timestamp \| Command \| Working directory \|") }; abort("verification ledger missing") unless start; rows = lines[(start + 2)..].take_while { \|line\| line.start_with?("\|") }; bad = rows.each_with_index.select { \|line, _\| line.gsub(%q{\|}, "").count("\|") != 8 }; abort("bad verification rows #{bad.map { \|_, i\| i + 1}.inspect}") unless bad.empty?'` | `.` | PASS | 0 | Required handoff sections and ledger schema remain complete | None |
| 2026-07-16T23:35:50+04:00 | `ruby -e 'allowed = ARGV.shift.split(","); ARGV.each do \|file\|; File.readlines(file).each_with_index do \|line, i\|; line.scan(%r{\\]\\(([^)]+)\\)}).flatten.each do \|target\|; next if target.match?(%r{\\A(?:https?:\|mailto:\|#)}); target = target.split("#", 2).first; path = File.expand_path(target, File.dirname(file)).delete_prefix(Dir.pwd + "/"); tracked = system("git", "cat-file", "-e", "HEAD:#{path}", out: File::NULL, err: File::NULL); abort("#{file}:#{i + 1}: nonportable #{target}") unless tracked \|\| allowed.include?(path); end; end; end' 'AGENTS.md,CONTRIBUTING.md,HANDOFF.md,docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md,docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md' AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md` | `.` | PASS | 0 | Every relative canonical-document link resolves from the baseline or exact publication allowlist | No committed link depends on excluded SDD evidence or uncommitted implementation |
| 2026-07-16T23:35:50+04:00 | `test "$(shasum -a 256 docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md \| cut -d ' ' -f 1)" = "$(sed -n 's/^Plan SHA-256: `\\([0-9a-f]*\\)`.*/\\1/p' .superpowers/sdd/progress.md)"` | `.` | PASS | 0 | Supplemental local progress digest matches the canonical plan | Progress remains noncanonical |
| 2026-07-16T23:35:50+04:00 | `rg -Fq 'git log --diff-filter=A -1 --format=%H -- AGENTS.md' AGENTS.md HANDOFF.md && rg -Fq 'Initial documentation baseline HEAD' HANDOFF.md && rg -Fq 'Initial handoff publication commit' HANDOFF.md && rg -Fq 'preserve the immutable initial baseline/publication fields' HANDOFF.md` | `.` | PASS | 0 | Initial publication anchor is stable across future handoff revisions | Postpublication commit identity is verified outside this self-contained commit |
| 2026-07-16T23:35:50+04:00 | `test "$(git status --short -- AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md)" = $' M CONTRIBUTING.md\\n M HANDOFF.md\\n?? AGENTS.md\\n?? docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md\\n?? docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md'` | `.` | PASS | 0 | Exact five-file publication candidate identified before staging | Stage no implementation or excluded SDD file |
| 2026-07-16T23:37:59+04:00 | `! rg -n 'OPEN UNKNOWN\|future authorized commit\|Original Task 8 pre-edit hashes were not captured\|Task 8 report/after-snapshot' AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md && rg -Fq 'No handoff or documentation question remains open.' HANDOFF.md` | `.` | FAIL | 1 | Whole-file scan matched its own newly appended verification row | Replace with timestamp-row-aware scan |
| 2026-07-16T23:38:50+04:00 | `ruby -e 'pattern = /OPEN UNKNOWN\|future authorized commit\|Original Task 8 pre-edit hashes were not captured\|Task 8 report\\/after-snapshot/; findings = []; ARGV.each do \|file\|; File.readlines(file).each_with_index do \|line, index\|; next if file == "HANDOFF.md" && line.start_with?("\| 2026-"); findings << "#{file}:#{index + 1}:#{line}" if line.match?(pattern); end; end; abort(findings.join) unless findings.empty?; abort("resolution statement missing") unless File.read("HANDOFF.md").include?("No handoff or documentation question remains open.")' AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md` | `.` | BLOCKED | unavailable | Orchestrator returned no conclusive child exit code | Repeated with literal-marker iteration |
| 2026-07-16T23:43:08+04:00 | `ruby -e 'bad = ["OPEN UNKNOWN", "future authorized commit", "Original Task 8 pre-edit hashes were not captured", "Task 8 report/after-snapshot"]; ARGV.each do \|file\|; File.foreach(file).with_index(1) do \|line, index\|; next if file == "HANDOFF.md" && line.start_with?("\| 2026-"); marker = bad.find { \|value\| line.include?(value) }; abort("#{file}:#{index}:#{marker}") if marker; end; end; abort("resolution statement missing") unless File.read("HANDOFF.md").include?("No handoff or documentation question remains open.")' AGENTS.md CONTRIBUTING.md HANDOFF.md docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md` | `.` | PASS | 0 | No unresolved handoff marker remains outside historical verification rows | Handoff questions closed |
| 2026-07-17 | Focused Task 8 admission/lock-set gate | `.` | PASS | 0 | 26 passed, 0 failed, 0 skipped | Executable gate is green; review conflict still prevents sealing |
| 2026-07-17 | Affected Task 8 contracts and pure Transfer-v3 regression | `.` | PASS | 0 | 390 passed with 12 deliberate no-connection skips; pure Transfer-v3 1,372 passed | Deferred post-V1 after independent P1 catalog-memory review |
| 2026-07-17 | Backend and test Release builds, `-warnaserror`, plus scoped formatter and `git diff --check` | `.` | PASS | 0 | Zero warnings/errors and no scoped formatting/whitespace drift | Rerun after every V1 repair |
| 2026-07-17 | Broad backend suite excluding `TestUsenetConnectionControllerTests` and live Phase 4 completion | `.` | FAIL | 1 | 2,815 passed, 84 skipped, 3 cleanup/visibility assertions failed | V1 Task 1B |
| 2026-07-17 | `dotnet test ... --filter 'FullyQualifiedName~TestUsenetConnectionControllerTests'` | `.` | CRASH | 1 | Testhost stack overflow recursively alternates `LoggerScope.Dispose` and `Logger.Dispose` | V1 Task 1A first edit |
| 2026-07-17 | Frontend typecheck, unit, client build, server build, and Playwright | `frontend` | PASS | 0 | Build gates green; Playwright 5/5 in 9.5 seconds | Expand behavioral E2E only after backend freeze |
| 2026-07-17 | `npm audit --audit-level=moderate` | `frontend` | PASS | 0 | `found 0 vulnerabilities` | Rerun on exact candidate |
| 2026-07-17 | `dotnet list backend.Tests/backend.Tests.csproj package --vulnerable --include-transitive` | `.` | PASS | 0 | No vulnerable packages from current sources | Rerun on exact candidate |
| 2026-07-17 | `python3 -m unittest discover -s tests -p 'test_*.py'` | `.` | PASS | 0 | 108 tests passed | Current tests preserve unsafe rclone unchanged-skip behavior; Task 5 must change contract and tests |
| 2026-07-17 | `docker build --tag nzbdav:entrypoint-smoke .` | `.` | PASS | 0 | Local smoke image built successfully | Final evidence must use immutable multi-arch digest |
| 2026-07-17 | `bash tests/test_entrypoint_contract.sh && bash tests/test_entrypoint_container.sh` | `.` | PASS | 0 | Shell and container smoke passed | Expand readiness, child-death, migration-failure, and SIGTERM cases |
| 2026-07-17 | Six-seat V1 council | `.` | NO-GO | not applicable | Six fresh architecture, SRE, security, product, scope, and adversarial seats accepted the frozen topology and unanimously rejected current release fitness | Implement active V1 plan |

The .NET commands refreshed only ordinary ignored build/test outputs. The final
Git-visible set comparison found no generated path attributable to verification,
so nothing was cleaned. Documentation checks generated no files.

## Known failures and blockers

### Resolved by V1 Task 1: deterministic backend test truth

- The Usenet crash was a test-harness ownership defect: `LoggerScope` owned a
  logger that in turn owned the same scope as its sink. A separate
  non-disposable collecting sink removed recursive disposal while preserving
  the password/provider-response redaction assertion.
- Cleanup tests were stale under their explicitly disabled path-level-rclone
  topology. Source and 38/38 invalidation tests prove the durable whole-cache
  sentinel intentionally supersedes narrower rows. The affected tests now
  require the one deterministic sentinel, no redundant path rows, and a
  positive retained revision; no production cleanup code changed.
- Verified result: 2,820 passed, 84 deliberate PostgreSQL-only skips, 0 failed;
  affected classes passed ten consecutive runs, and the clean test build has
  zero warnings or errors.

### 1. Safe rclone update can return false success

- Unchanged fingerprint exits without checking live container or mount state.
- Changed state is recorded immediately after `compose up` without readiness or
  traversal proof.
- Current 108/108 Python result confirms the old unsafe contract is encoded in
  tests; green count does not close the defect.
- Next action: V1 Task 5 after backend freeze prerequisites.

### 4. Session startup is hardened; proxy boundary remains release-blocking

- Task 2A removed the ephemeral session-key fallback, made secure cookies the
  default, added one-step key rotation, and documented both supported modes in
  `.env.example`.
- Local login/onboarding/logout are unavailable in Authentik mode; absent or
  invalid outpost identity fails closed without a local fallback.
- The rebuilt container contract proves missing-key failure and explicit-key
  success, but the current CI workflow does not yet invoke that container smoke.
- Proxy route/header boundaries still need the Task 2B executable abuse matrix.
- Verified topology decision: use the same frontend hostname/listener with a
  dedicated mount-relative `/protocol` prefix for independently authenticated
  SAB, ARR, and WebDAV clients. UI/admin, `/view`, and `/ws` remain outside
  `/protocol`; backend 8080 remains private.
- Verified public API-key scope: `/protocol` permits the complete SAB dispatcher,
  read-only ARR validation/search-nudge/correlation reports, and `POST` event
  ingestion for the three supported ARR kinds. ARR mutations and every other
  admin route remain UI-principal-only.
- Verified WebDAV scope: read/list methods on exact namespaces, `PUT` only
  under `/protocol/nzbs/<category>/...`, and `DELETE` only under
  `/protocol/nzbs`, `/protocol/content`, and
  `/protocol/completed-symlinks`. Unsupported mutation/protocol methods fail
  before proxying; any widening requires disposable client evidence and renewed
  approval.
- V1 WebDAV authentication is fail-closed. `DISABLE_WEBDAV_AUTH=true` now fails
  startup, NWebDav always requires authentication, focused tests pass `8/8`,
  and Release build/format gates are green. Combined backend regression remains
  pending after the key-parser slice.
- Verified V1 import/media scope: hard symlink-only imports. Remove STRM/both
  settings and generation/recreation/conversion surfaces. Plex/ARR use mounted
  `/completed-symlinks` → `/.ids`; AIOStreams uses authenticated WebDAV; Dav
  Explore `/view` remains frontend-principal-only. No `/protocol/view` route is
  public.
- Exact key-carrier compatibility remains open; production proxy edits are
  blocked until that contract is explicit.
- The adjacent ARR helper defect is GREEN locally, pending independent review.
  `/api/arr/validation` now returns only derived configured-app/search-mode/
  duplicate-policy fields and the helper no longer calls internal-only
  `/api/get-config`. Focused service `1/1`, Python `7/7`, ARR service `27/27`,
  all Python `110/110`, complete backend Release `2,825` passed with `84`
  expected PostgreSQL skips, and build/format/compile/docs/diff gates exited
  zero. Do not call it sealed before reading the reviewer report.
- Product decision verified on 2026-07-17: V1 defaults to `AUTH_MODE=local` and
  supports explicit `AUTH_MODE=authentik-proxy` using the official
  Sonarr-style Authentik Proxy Provider pattern. Authentik mode disables local
  login and never falls back to it.
- Local mode requires an exact 64-hex `SESSION_KEY`; optional
  `SESSION_KEY_PREVIOUS` provides one-step rotation. Authentik mode requires
  trusted outpost source CIDRs and expected application metadata, and the app
  port must not be exposed as a browser-auth bypass.
- Next action: resolve Task 2B's remaining key-carrier contract, finish the
  independent council synthesis, then write the RED route/method/credential
  matrix before any production proxy edit.

### 5. Liveness, public errors, and repair lifecycle

- `/health` has no substantive registered checks.
- base/SAB controllers and middleware can expose raw exception details.
- missing-file repair scheduling uses detached persistence-affecting work.
- Next actions: V1 Tasks 2-4 in order.

### 6. Immutable release provenance is absent

Existing evidence belongs to a signed WIP revision, not one publishable image
digest. Final multi-arch build, SBOM, provenance, and acceptance must bind one
immutable candidate after every P1 closes.

### 7. Transfer-v3 Phase 4 review conflict

Task 8's executable gates are green, but `PostgreSqlPhysicalCatalogContract`
can materialize unbounded catalog rows and function bodies before the explicit
Phase 4 budget leases begin. Reviews that approved sealing did not analyze this
path. Phase 4 is private and post-V1; do not run live PostgreSQL or edit it in
the V1 plan.

## Environment and prerequisites

| Tool | Current | Required/relevant |
| --- | --- | --- |
| macOS | 27.0, arm64 | Current host for pure gates |
| .NET SDK | 10.0.301 | V1 backend build/test gate |
| .NET/ASP.NET runtime | 10.0.9 | V1 runtime gate |
| Npgsql | 10.0.3 exact package range | Private post-V1 code only; no V1 runtime path |
| EF Core | 10.0.9 | Existing model/migrations only |
| xUnit | 2.9.2 | Current tests |
| Python | 3.14.6 | V1 operational scripts/tests |
| Docker | 29.4.0 | V1 image and entrypoint gates; current local image/smoke passed |
| Node | 22.22.3 | Does not satisfy declared Node 24 |
| npm | 10.9.8 | Does not satisfy declared npm 11 |

Relevant environment variable names:

- `SESSION_KEY`
- `SESSION_KEY_PREVIOUS`, optional one-step local-session rotation key
- `AUTH_MODE`, `local` by default or explicit `authentik-proxy`
- `AUTHENTIK_TRUSTED_PROXY_CIDRS`
- `AUTHENTIK_APP_SLUG`
- `SECURE_COOKIES`
- `ALLOW_INSECURE_COOKIES`, explicit development-only opt-in
- `FRONTEND_BACKEND_API_KEY`
- `NZBDAV_TEST_POSTGRES_CONNECTION_STRING`
- `NZBDAV_REQUIRE_POSTGRES_TESTS`
- `NZBDAV_REQUIRE_TRANSFER_V3_PHASE4_POSTGRES_TESTS`, planned Task 20
- `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE`
- `TZ`

Never record their secret values. V1 tests use synthetic canaries and disposable
fixtures only.

## Exact next actions

1. **Resolve API-key carriers.** Confirm `x-api-key` plus lowercase `apikey`
   query/form support and fail-closed duplicate/conflict behavior; do not treat
   unsupported camel-case `apiKey` as an established contract.
2. **Finish independent review.** Read every completed Task 2B council report,
   bind it to the recorded file hashes, and synthesize consensus/conflicts. Do
   not treat pending or truncated reports as approvals.
3. **Freeze the matrix.** Enumerate exact UI-admin, `/protocol` SAB/ARR, WebDAV,
   signed-media, health, and `/ws` path/method/credential classes. Include
   `URL_BASE`, encoded/double-encoded paths, conflicting/oversized headers,
   WebDAV `Destination`, and zero-upstream-call negatives.
4. **Write RED tests before production code.** Use a pure classifier plus a
   disposable capture backend; add a disposable ASP.NET/client integration gate
   for normalization and protocol behavior. Do not use placeholders or weaken
   existing frontend behavior.
5. **Implement the symlink-only removal slice.** RED-first removal of STRM
   settings, generation, maintenance, seed, UI, and docs, while preserving ARR,
   rclone, DFS, cleanup, and organized-link symlink regressions.
6. **Seal the ARR helper review.** Read the pending independent report, resolve
   any material finding, and rerun its focused and affected gates before marking
   the slice complete.
7. **Only after RED is reviewed, implement minimal GREEN.** Then run frontend
   unit/type/build/E2E, backend focused/full Release gates, container smoke,
   documentation validation, independent security review, and `git diff --check`.

## Do not redo

- Do not reopen the V1 topology or upgrade policy. V1 is clean-install-only,
  Docker/SQLite/one-owner/`role=all`; PostgreSQL and upgrades are post-V1.
- Do not start the full rebrand before the backend freeze, RC gates, and user
  review of the pass report.
- Do not treat the 108 passing Python tests as proof that safe-rclone is safe;
  current tests encode the false-success behavior.
- Do not weaken cleanup assertions until whole-cache supersession and consumer
  semantics are executable facts.
- Do not replace `EXCLUSIVE MODE NOWAIT` with `SHARE ROW EXCLUSIVE`.
- Do not change Task 8 to repeatable-read or serializable.
- Do not recreate digest strings or state objects in admission.
- Do not create a second managed budget.
- Do not duplicate direct `ConfigItems` SQL in the validator.
- Do not add a Task 8 live PostgreSQL test or external connection dependency
  during V1.
- Do not rerun Tasks 0-7 implementation. Their current pure code passed.
- Do not create another handoff or active plan.
- Do not stage/delete artifacts, Finder files, caches, or unexplained untracked
  source/tests.
- Do not repeat the PR search unless branch/HEAD/remote state changes.

## Deferred or out of scope

- All Transfer-v3 Phase 4 work, including Task 8's catalog-memory repair and
  Tasks 9-21.
- PostgreSQL runtime, split roles, multi-owner/multi-host operation, and
  migration/cutover.
- Pre-V1 upgrade support and in-place downgrade.
- Full visual rebrand until the backend freeze and RC pass report.
- General ARR/Plex expansion and grab-to-Plex performance beyond the six V1
  behavioral journeys.
- n8n.
- Destructive, repair, migration, or stress use of production.
- Native FUSE promotion.
- Alternative packaging and orchestration.

## Open questions

The V1 topology, `/protocol` same-host ingress, scoped public API-key authority,
path-scoped semantic WebDAV writes, hard symlink-only imports, and authenticated
UI-only `/view` are fixed. One Task 2B policy question remains open and blocks
production proxy edits: exact accepted public key carriers and conflict
behavior.

Full rebrand remains deferred until the backend passes.

- The two pre-Task-8 shared-test hashes are recovered from the Task 5
  after-snapshot: server-contract tests are
  `0c71b5ed5aca317cbdb7b09997ce76cc96fd8cc11391051c77e169a86085b614`;
  environment-contract tests are
  `2a37668b805c48ed79caf0aaa958d8c0615d16ee198fd15ae05b7799888894b1`.
  Task 6 and Task 7 reports explicitly record no unrelated-file changes, and
  their scopes exclude both files.
- Phase 4 PostgreSQL results are post-V1 implementation evidence, not an
  unresolved V1 decision. Do not use an existing server.
- Local `.superpowers/sdd/**` evidence remains deliberately excluded,
  supplemental, and noncanonical. Canonical continuation depends only on
  `AGENTS.md`, `HANDOFF.md`, the active V1 plan, and the V1 design.
- Current V1 documentation and executable implementation are durable on
  `pinrail/v1-backend-wip`. Continue there; never treat it as a release branch.

## Recovery and rollback

The current V1 state is anchored by signed checkpoint
`fb03b0e6a247dfeaff9e9965f045a1fb1e6a11cc`. Recovery means returning to that
checkpoint or the external recovery bundle, never deleting unexplained work.

- Verify the handoff publication commit is an ancestor of `HEAD`, then compare
  branch and status with this handoff.
- Use the active V1 plan, checkpoint diff, and exact Task 2 filters to identify
  scope. Local snapshot files are optional corroboration only.
- Inspect only scoped diffs/source. Untracked files have no Git base, so never
  assume they are disposable.
- If an edit fails, re-read and repair only that exact edit. Do not use reset,
  restore, checkout, clean, stash, or deletion.
- If a gate produces a different failure set, stop, update this handoff, and
  diagnose before further edits.
- Current V1 tests use disposable fixtures only. No production rollback or data
  repair has been attempted.

## Handoff maintenance protocol

Every future working session must:

1. verify initial publication ancestry, then compare branch and status with this snapshot;
2. update this handoff before coding when they differ materially;
3. update task status only after current verification;
4. append real verification results, including failures and blocked commands;
5. change the first exact next action before ending;
6. keep `AGENTS.md` pointer concise;
7. keep one canonical handoff and one canonical active plan;
8. remove or archive the active-work pointer only when V1 is fully complete.
9. preserve the immutable initial baseline/publication fields. Before any later
   handoff revision, refresh the content-verification timestamp, current
   branch/worktree facts, ahead/behind counts, and continuation state; require
   the initial publication anchor to remain an ancestor and never embed a new
   self-referential commit ID.

## Appendix A, complete pre-edit tracked-modification inventory

Captured by `git diff --name-status` at
2026-07-16T22:01:55+04:00. Exactly 156 paths:

```text
M	.github/dependabot.yml
M	.github/workflows/branch.yml
M	.github/workflows/ci.yml
M	.github/workflows/dependabot.yml
M	.github/workflows/ghcr-release.yml
M	.github/workflows/pre-release.yml
M	Dockerfile
M	backend.Tests/Api/GetConfigControllerTests.cs
M	backend.Tests/Api/GetHistoryControllerTests.cs
M	backend.Tests/Api/GetQueueControllerTests.cs
M	backend.Tests/Api/GetWebdavItemControllerTests.cs
M	backend.Tests/Api/RemoveFromHistoryTransactionTests.cs
M	backend.Tests/Api/SabRequestTests.cs
M	backend.Tests/Api/UpdateConfigControllerTests.cs
M	backend.Tests/Api/WorkerQueueStatusTests.cs
M	backend.Tests/Clients/RadarrSonarr/ArrClientTests.cs
M	backend.Tests/Clients/Rclone/RcloneClientTests.cs
M	backend.Tests/Clients/Usenet/DownloadingNntpClientTests.cs
M	backend.Tests/Config/ConfigManagerConcurrencyTests.cs
M	backend.Tests/Coordination/DatabaseWorkerJobCoordinatorPostgreSqlTests.cs
M	backend.Tests/Coordination/DatabaseWorkerJobCoordinatorTests.cs
M	backend.Tests/Database/BlobStoreTests.cs
M	backend.Tests/Database/DatabaseProviderSelectionTests.cs
M	backend.Tests/Database/DatabaseTransferServiceTests.cs
M	backend.Tests/Database/PostgreSqlFactAttribute.cs
M	backend.Tests/Database/RcloneInvalidationTests.cs
M	backend.Tests/Database/WorkerJobLeasePostgreSqlMigrationTests.cs
M	backend.Tests/Database/WorkerJobLeaseTests.cs
M	backend.Tests/Hosting/NzbdavRoleStartupTests.cs
M	backend.Tests/Models/Nzb/NzbDocumentTests.cs
M	backend.Tests/Mount/DfsDavPathResolverTests.cs
M	backend.Tests/Mount/DfsFileSystemTests.cs
M	backend.Tests/Queue/QueueItemProcessorVerificationTests.cs
M	backend.Tests/Services/ArrOperationsServiceTests.cs
M	backend.Tests/Services/ContentIndexRecoveryServiceTests.cs
M	backend.Tests/Services/HealthCheckRepairPolicyTests.cs
M	backend.Tests/Services/ImportReceiptPostgreSqlConcurrencyTests.cs
M	backend.Tests/Services/ImportReceiptServiceTests.cs
M	backend.Tests/Tasks/RemoveUnlinkedFilesTaskTests.cs
M	backend.Tests/WebDav/GetAndHeadHandlerPatchTests.cs
M	backend.Tests/Websocket/WebsocketManagerTests.cs
M	backend.Tests/backend.Tests.csproj
M	backend/Api/Controllers/ConvertStrmToSymlinks/ConvertStrmToSymlinksController.cs
M	backend/Api/Controllers/GetConfig/GetConfigController.cs
M	backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs
M	backend/Api/Controllers/RecreateStrmFiles/RecreateStrmFilesController.cs
M	backend/Api/Controllers/RemoveUnlinkedFiles/RemoveUnlinkedFilesController.cs
M	backend/Api/Controllers/RemoveUnlinkedFiles/RemoveUnlinkedFilesDryRunController.cs
M	backend/Api/Controllers/Repair/CancelRepairRunController.cs
M	backend/Api/Controllers/Repair/RepairStatusController.cs
M	backend/Api/Controllers/TestRcloneConnection/TestRcloneConnectionController.cs
M	backend/Api/Controllers/TestRcloneConnection/TestRcloneConnectionRequest.cs
M	backend/Api/Controllers/TestRcloneConnection/TestRcloneConnectionResponse.cs
M	backend/Api/Controllers/UpdateConfig/UpdateConfigController.cs
M	backend/Api/SabControllers/AddFile/AddFileController.cs
M	backend/Api/SabControllers/GetFullStatus/GetFullStatusController.cs
M	backend/Api/SabControllers/GetFullStatus/GetFullStatusResponse.cs
M	backend/Api/SabControllers/GetHistory/GetHistoryController.cs
M	backend/Api/SabControllers/GetQueue/GetQueueController.cs
M	backend/Api/SabControllers/GetQueue/GetQueueRequest.cs
M	backend/Api/SabControllers/GetQueue/GetQueueResponse.cs
M	backend/Api/SabControllers/GetServerStats/GetServerStatsController.cs
M	backend/Api/SabControllers/GetStatus/GetStatusController.cs
M	backend/Api/SabControllers/GetStatus/GetStatusResponse.cs
M	backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryController.cs
M	backend/Api/SabControllers/SabPagination.cs
M	backend/Api/SabControllers/StatusDiagnostics.cs
M	backend/Clients/RadarrSonarr/ArrClient.cs
M	backend/Clients/RadarrSonarr/RadarrClient.cs
M	backend/Clients/RadarrSonarr/RadarrModels/RadarrQueueRecord.cs
M	backend/Clients/RadarrSonarr/SonarrClient.cs
M	backend/Clients/RadarrSonarr/SonarrModels/SonarrQueueRecord.cs
M	backend/Clients/Rclone/Models/RcloneResponse.cs
M	backend/Clients/Rclone/RcloneClient.cs
M	backend/Config/ConfigManager.cs
M	backend/Coordination/DatabaseWorkerJobCoordinator.cs
M	backend/Database/BlobStore.cs
M	backend/Database/DatabaseTransferService.cs
M	backend/Database/DavDatabaseClient.cs
M	backend/Database/DavDatabaseContext.cs
M	backend/Database/DavDatabaseContextFactory.cs
M	backend/Database/Interceptors/SqliteForeignKeyEnabler.cs
M	backend/Database/Migrations/DavDatabaseContextModelSnapshot.cs
M	backend/Database/Models/DavItem.cs
M	backend/Database/Models/ImportReceipt.cs
M	backend/Database/Models/RcloneInvalidationItem.cs
M	backend/Middlewares/ExceptionMiddleware.cs
M	backend/Models/Nzb/NzbDocument.cs
M	backend/Models/Nzb/NzbFile.cs
M	backend/Mount/DfsDavPathResolver.cs
M	backend/Mount/DfsFileSystem.cs
M	backend/Mount/MountStatusProvider.cs
M	backend/NzbWebDAV.csproj
M	backend/Program.cs
M	backend/Queue/DeobfuscationSteps/1.FetchFirstSegment/FetchFirstSegmentsStep.cs
M	backend/Queue/QueueItemProcessor.cs
M	backend/Queue/QueueManager.cs
M	backend/Services/ArrCorrelationService.cs
M	backend/Services/ArrDownloadReportService.cs
M	backend/Services/ArrIntegration.cs
M	backend/Services/ArrOperationsService.cs
M	backend/Services/ArrPriorityService.cs
M	backend/Services/ArrSearchNudgeService.cs
M	backend/Services/BlobCleanupService.cs
M	backend/Services/ContentIndexRecoveryService.cs
M	backend/Services/ContentIndexSnapshotWriterService.cs
M	backend/Services/DavCleanupService.cs
M	backend/Services/HealthCheckService.cs
M	backend/Services/HistoryCleanupService.cs
M	backend/Services/ImportReceiptReconciliationService.cs
M	backend/Services/ImportReceiptService.cs
M	backend/Services/NzbBlobCleanupService.cs
M	backend/Services/RcloneInvalidationService.cs
M	backend/Services/RemoveOrphanedFilesSchedulerService.cs
M	backend/Services/UsenetFileToBlobstoreMigrationService.cs
M	backend/Tasks/BaseTask.cs
M	backend/Tasks/RecreateStrmFilesTask.cs
M	backend/Tasks/RemoveUnlinkedFilesTask.cs
M	backend/Tasks/StrmToSymlinksTask.cs
M	backend/WebDav/Base/GetAndHeadHandlerPatch.cs
M	backend/WebDav/DatabaseStoreCategoryWatchFolder.cs
M	backend/Websocket/WebsocketManager.cs
M	docs/setup-guide.md
M	docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md
M	docs/superpowers/plans/2026-07-11-nzbdav-runtime-migration-safety.md
M	docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md
M	entrypoint.sh
M	frontend/Dockerfile
M	frontend/app/clients/backend-client.server.ts
M	frontend/app/routes/health/components/operations-status/operations-status.tsx
M	frontend/app/routes/health/route.tsx
M	frontend/app/routes/queue/components/action-button/action-button.tsx
M	frontend/app/routes/queue/components/queue-table/queue-table.test.tsx
M	frontend/app/routes/queue/components/queue-table/queue-table.tsx
M	frontend/app/routes/queue/controllers/events-controller.test.ts
M	frontend/app/routes/queue/controllers/events-controller.ts
M	frontend/app/routes/queue/controllers/websocket-controller.ts
M	frontend/app/routes/queue/route.module.css
M	frontend/app/routes/queue/route.module.css.d.ts
M	frontend/app/routes/queue/route.tsx
M	frontend/app/routes/settings/maintenance/maintenance-actions.test.tsx
M	frontend/app/routes/settings/maintenance/recreate-strm-files/recreate-strm-files.tsx
M	frontend/app/routes/settings/maintenance/remove-unlinked-files/remove-unlinked-files.tsx
M	frontend/app/routes/settings/maintenance/start-maintenance-task.ts
M	frontend/app/routes/settings/maintenance/strm-to-symlinks/strm-to-symlinks.tsx
M	frontend/app/routes/settings/rclone/rclone-ui.test.tsx
M	frontend/app/routes/settings/rclone/rclone.test.ts
M	frontend/app/routes/settings/rclone/rclone.tsx
M	frontend/app/routes/settings/route.tsx
M	frontend/e2e/health.spec.ts
M	frontend/package-lock.json
M	frontend/package.json
M	frontend/test/mock-backend.ts
M	scripts/nzbdav_safe_rclone_up.py
M	tests/test_entrypoint_contract.sh
M	tests/test_nzbdav_safe_rclone_up.py
```

## Appendix B, complete pre-edit untracked-file inventory

Captured by `git ls-files --others --exclude-standard` at
2026-07-16T22:01:55+04:00. Exactly 282 paths:

```text
.DS_Store
.config/dotnet-tools.json
.github/.DS_Store
.github/workflows/verify.yml
.nvmrc
artifacts/.DS_Store
artifacts/pg-review-final/postgres-final.trx
artifacts/pg-review-final2/postgres-final.trx
artifacts/pg-review-final3/postgres-final.trx
artifacts/pg-review-full-final/backend-full-final.trx
artifacts/pg-review-full/backend-full.trx
artifacts/pg-review-results/postgres-first.trx
artifacts/product-audit/.DS_Store
artifacts/product-audit/2026-07-11/01-queue-desktop.png
artifacts/product-audit/2026-07-11/02-settings-desktop.png
artifacts/product-audit/2026-07-11/03-queue-mobile.png
artifacts/product-audit/2026-07-11/04-health-overview-desktop.png
artifacts/product-audit/2026-07-11/05-health-arr-operations-desktop.png
artifacts/product-audit/2026-07-11/06-settings-mobile.png
artifacts/product-audit/2026-07-11/07-health-mobile.png
artifacts/product-audit/2026-07-11/REPO_SANITY_AUDIT.md
backend.Tests/.DS_Store
backend.Tests/Api/AddFileLocalTimestampTests.cs
backend.Tests/Api/MaintenanceRunControllerTests.cs
backend.Tests/Api/StatusControllerDiagnosticsTests.cs
backend.Tests/Api/TestRcloneConnectionControllerTests.cs
backend.Tests/Clients/Rclone/RcloneLogSafetyTests.cs
backend.Tests/Database/DatabaseMigratorPreflightTests.cs
backend.Tests/Database/DatabaseTelemetryTests.cs
backend.Tests/Database/DavDatabaseClientProviderSqlTests.cs
backend.Tests/Database/LegacyTimestampContractTests.cs
backend.Tests/Database/LegacyTimestampSourceGuardTests.cs
backend.Tests/Database/LocalWallQueryBoundsTests.cs
backend.Tests/Database/MaintenanceRunSchemaTests.cs
backend.Tests/Database/PostgreSqlCatalogInventoryTests.cs
backend.Tests/Database/PostgreSqlConnectionPolicyTests.cs
backend.Tests/Database/PostgreSqlEnvironmentContractTests.cs
backend.Tests/Database/PostgreSqlFactAttributeTests.cs
backend.Tests/Database/PostgreSqlFreshBootstrapContractTests.cs
backend.Tests/Database/PostgreSqlNativeContractManifestTests.cs
backend.Tests/Database/PostgreSqlNativeMigrationContractTests.cs
backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs
backend.Tests/Database/PostgreSqlNativeModelTests.cs
backend.Tests/Database/PostgreSqlTestSchema.cs
backend.Tests/Database/PostgreSqlTimestampIntegrationTests.cs
backend.Tests/Database/ProviderContextBoundaryTests.cs
backend.Tests/Database/ProviderMigrationOwnershipTests.cs
backend.Tests/Database/SqliteConnectionPolicyTests.cs
backend.Tests/Database/SqliteContractTestSupport.cs
backend.Tests/Database/SqliteMigrationSmokeTests.cs
backend.Tests/Database/SqliteRuntimeGateTests.cs
backend.Tests/Database/SqliteSourceSchemaManifestTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4FailureTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4ManagedBudgetTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4OptionsTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4StagingLedgerTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3Phase4StagingParentTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSetTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDeadlineTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDiagnosticsTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlServerContractTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlSessionTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContractTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptorTests.cs
backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetIdentityTests.cs
backend.Tests/Database/Transfer/TransferV3BlobBundleWriterTests.cs
backend.Tests/Database/Transfer/TransferV3BlobReferenceIndexTests.cs
backend.Tests/Database/Transfer/TransferV3CursorCodecTests.cs
backend.Tests/Database/Transfer/TransferV3ImportFailurePolicyTests.cs
backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs
backend.Tests/Database/Transfer/TransferV3ImportStateStorePostgreSqlTests.cs
backend.Tests/Database/Transfer/TransferV3ImportStateStoreTests.cs
backend.Tests/Database/Transfer/TransferV3IsolationCanaryTests.cs
backend.Tests/Database/Transfer/TransferV3JsonlParserTransactionTests.cs
backend.Tests/Database/Transfer/TransferV3JsonlTests.cs
backend.Tests/Database/Transfer/TransferV3JsonlWriterHardeningTests.cs
backend.Tests/Database/Transfer/TransferV3LogicalRowHasherTests.cs
backend.Tests/Database/Transfer/TransferV3ManifestCodecTests.cs
backend.Tests/Database/Transfer/TransferV3ParserBufferHardeningTests.cs
backend.Tests/Database/Transfer/TransferV3PosixOwnedDirectoryTests.cs
backend.Tests/Database/Transfer/TransferV3ReservedConfigContainmentTests.cs
backend.Tests/Database/Transfer/TransferV3RowCodecTests.cs
backend.Tests/Database/Transfer/TransferV3SealedSnapshotStageAdversarialTests.cs
backend.Tests/Database/Transfer/TransferV3SealedSnapshotStageTests.cs
backend.Tests/Database/Transfer/TransferV3SensitiveBufferErasureTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotDirectoryPublicationTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotDirectoryTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotExporterTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotReaderTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotVerifierArtifactTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotVerifierSemanticTests.cs
backend.Tests/Database/Transfer/TransferV3SnapshotVerifierTests.cs
backend.Tests/Database/Transfer/TransferV3SourceContractTests.cs
backend.Tests/Database/Transfer/TransferV3SqliteExportSessionTests.cs
backend.Tests/Database/Transfer/TransferV3SqlitePreflightValidatorTests.cs
backend.Tests/Database/Transfer/TransferV3SqliteTableExporterTests.cs
backend.Tests/Database/Transfer/TransferV3StartupGuardTests.cs
backend.Tests/Hosting/MaintenanceCommandLineTests.cs
backend.Tests/Mount/MountStatusProviderTests.cs
backend.Tests/Queue/FetchFirstSegmentsStepTests.cs
backend.Tests/Services/ArrCompletedDownloadDispatchPolicyTests.cs
backend.Tests/Services/ArrCorrelationServiceTests.cs
backend.Tests/Services/ArrDownloadReportServiceTests.cs
backend.Tests/Services/ArrImportCommandServiceTests.cs
backend.Tests/Services/ArrImportVisibilityPostgreSqlTests.cs
backend.Tests/Services/ArrIntegrationTests.cs
backend.Tests/Services/ArrOperationsLocalTimestampTests.cs
backend.Tests/Services/ArrSearchNudgeServiceTests.cs
backend.Tests/Services/MaintenanceRunServiceTests.cs
backend.Tests/Services/MaintenanceRunTransitionTests.cs
backend.Tests/Services/MaintenanceServiceRegistrationTests.cs
backend.Tests/Tasks/BaseTaskLifecycleTests.cs
backend.Tests/Tasks/MaintenanceTaskExecutorTests.cs
backend.Tests/Telemetry/CriticalPathTelemetryTests.cs
backend.Tests/TestData/postgresql-native-schema-contract.json
backend.Tests/TestData/sqlite-migration-contract.json
backend.Tests/TestData/sqlite-source-schema-manifest.json
backend.Tests/TestDoubles/CommandCaptureInterceptor.cs
backend.Tests/WebDav/DatabaseStoreCategoryWatchFolderTimestampTests.cs
backend/.DS_Store
backend/Api/.DS_Store
backend/Api/Controllers/Maintenance/CancelMaintenanceRunController.cs
backend/Api/Controllers/Maintenance/MaintenanceControllerHelpers.cs
backend/Api/Controllers/Maintenance/MaintenanceRunController.cs
backend/Api/Controllers/Maintenance/MaintenanceRunResponses.cs
backend/Api/Controllers/Maintenance/MaintenanceRunsController.cs
backend/Api/Controllers/Maintenance/MaintenanceStatusController.cs
backend/Database/.DS_Store
backend/Database/ArrImportCommandWakeSignal.cs
backend/Database/DatabaseMigrationPolicy.cs
backend/Database/DatabaseMigrator.cs
backend/Database/DatabaseStorageTelemetry.cs
backend/Database/DatabaseTelemetry.cs
backend/Database/DavDatabaseContextRuntimeFactory.cs
backend/Database/HealthWorkerWakeSignal.cs
backend/Database/Interceptors/DatabaseCommandTelemetryInterceptor.cs
backend/Database/Interceptors/DatabaseTransactionTelemetryInterceptor.cs
backend/Database/LocalWallQueryBounds.cs
backend/Database/Migrations/20260712113000_Add-Maintenance-Runs.cs
backend/Database/Migrations/20260712120000_Add-Rclone-Invalidation-Revision.cs
backend/Database/Migrations/20260712123000_Add-Arr-Import-Commands.cs
backend/Database/Models/ArrImportCommand.cs
backend/Database/Models/MaintenanceRun.cs
backend/Database/NpgsqlRuntimeSwitches.cs
backend/Database/PostgreSqlCatalogs/postgresql-native-baseline-catalog.txt
backend/Database/PostgreSqlCatalogs/postgresql-native-empty-history-catalog.txt
backend/Database/PostgreSqlCatalogs/postgresql-native-empty-schema-catalog.txt
backend/Database/PostgreSqlCatalogs/postgresql-native-head-catalog.txt
backend/Database/PostgreSqlConnectionPolicy.cs
backend/Database/PostgreSqlDavDatabaseContext.cs
backend/Database/PostgreSqlEnvironmentContract.cs
backend/Database/PostgreSqlFreshBootstrapContract.cs
backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.Designer.cs
backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.cs
backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.Designer.cs
backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.cs
backend/Database/PostgreSqlMigrations/PostgreSqlDavDatabaseContextModelSnapshot.cs
backend/Database/PostgreSqlModelConfiguration.cs
backend/Database/PostgreSqlNativeMigrationContract.cs
backend/Database/PostgreSqlNativeMigrator.cs
backend/Database/PostgreSqlPhysicalCatalogContract.cs
backend/Database/RcloneInvalidationWakeSignal.cs
backend/Database/SqliteRuntimeGate.cs
backend/Database/Transfer/Contracts/README.md
backend/Database/Transfer/Contracts/sqlite-migration-contract.json
backend/Database/Transfer/Contracts/sqlite-source-schema-manifest.json
backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json
backend/Database/Transfer/Contracts/transfer-v3-source-contract.json
backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs
backend/Database/Transfer/Phase4/TransferV3Phase4Failure.cs
backend/Database/Transfer/Phase4/TransferV3Phase4Options.cs
backend/Database/Transfer/Phase4/TransferV3Phase4StagingParent.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlOpenAttempt.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlProviderOperations.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContract.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs
backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetIdentity.cs
backend/Database/Transfer/TransferV3BlobBundleWriter.cs
backend/Database/Transfer/TransferV3BlobInventoryScanner.cs
backend/Database/Transfer/TransferV3BlobReferenceIndex.cs
backend/Database/Transfer/TransferV3BlobSourceGuard.cs
backend/Database/Transfer/TransferV3CursorCodec.cs
backend/Database/Transfer/TransferV3DurableFileStream.cs
backend/Database/Transfer/TransferV3FrameCodec.cs
backend/Database/Transfer/TransferV3FrameState.cs
backend/Database/Transfer/TransferV3Frames.cs
backend/Database/Transfer/TransferV3ImportFailurePolicy.cs
backend/Database/Transfer/TransferV3ImportState.cs
backend/Database/Transfer/TransferV3ImportStateCodec.cs
backend/Database/Transfer/TransferV3ImportStateStore.cs
backend/Database/Transfer/TransferV3JsonlParser.cs
backend/Database/Transfer/TransferV3JsonlWriter.cs
backend/Database/Transfer/TransferV3LogicalRowHasher.cs
backend/Database/Transfer/TransferV3Manifest.cs
backend/Database/Transfer/TransferV3ManifestCodec.cs
backend/Database/Transfer/TransferV3Posix.cs
backend/Database/Transfer/TransferV3ReferenceValidator.cs
backend/Database/Transfer/TransferV3ReservedConfigPolicy.cs
backend/Database/Transfer/TransferV3RowCodec.cs
backend/Database/Transfer/TransferV3SealedSnapshotStage.cs
backend/Database/Transfer/TransferV3SnapshotDirectory.cs
backend/Database/Transfer/TransferV3SnapshotExporter.cs
backend/Database/Transfer/TransferV3SnapshotReader.cs
backend/Database/Transfer/TransferV3SnapshotVerifier.cs
backend/Database/Transfer/TransferV3SourceContract.cs
backend/Database/Transfer/TransferV3SqliteExportSession.cs
backend/Database/Transfer/TransferV3SqlitePreflightValidator.cs
backend/Database/Transfer/TransferV3SqliteRawScanner.cs
backend/Database/Transfer/TransferV3SqliteSchemaManifest.cs
backend/Database/Transfer/TransferV3SqliteSourceGuard.cs
backend/Database/Transfer/TransferV3SqliteTableExporter.cs
backend/Database/Transfer/TransferV3StartupGuard.cs
backend/Database/Transfer/TransferV3Utf8LineReader.cs
backend/Hosting/MaintenanceCommandLine.cs
backend/Properties/AssemblyInfo.cs
backend/Services/ArrCompletedDownloadDispatchPolicy.cs
backend/Services/ArrImportCommandService.cs
backend/Services/HistoryVisibilityService.cs
backend/Services/MaintenanceRunService.cs
backend/Services/MaintenanceRunTransitions.cs
backend/Services/MaintenanceServiceCollectionExtensions.cs
backend/Tasks/MaintenanceTaskExecutor.cs
backend/Telemetry/CriticalPathTelemetry.cs
benchmarks/.DS_Store
benchmarks/SqliteWalDurabilityBenchmark.Tests/SqliteWalDurabilityBenchmark.Tests.csproj
benchmarks/SqliteWalDurabilityBenchmark.Tests/SqliteWalDurabilityBenchmarkTests.cs
benchmarks/SqliteWalDurabilityBenchmark/BenchmarkCommandLine.cs
benchmarks/SqliteWalDurabilityBenchmark/BenchmarkOutput.cs
benchmarks/SqliteWalDurabilityBenchmark/PercentileCalculator.cs
benchmarks/SqliteWalDurabilityBenchmark/Program.cs
benchmarks/SqliteWalDurabilityBenchmark/SqliteWalBenchmarkModels.cs
benchmarks/SqliteWalDurabilityBenchmark/SqliteWalBenchmarkRunner.cs
benchmarks/SqliteWalDurabilityBenchmark/SqliteWalDurabilityBenchmark.csproj
docs/.DS_Store
docs/grab-to-plex-benchmark.md
docs/sqlite-wal-durability-benchmark.md
docs/superpowers/plans/2026-07-12-nzbdav-proceed-execution-plan.md
docs/superpowers/plans/2026-07-12-nzbdav-transfer-v3-phase-2.md
docs/superpowers/plans/2026-07-12-postgresql-independent-review-repair.md
docs/superpowers/plans/2026-07-13-nzbdav-guarded-arr-import-scan.md
docs/superpowers/plans/2026-07-13-nzbdav-transfer-v3-phase-3.md
docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md
docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md
frontend/.npmrc
frontend/app/routes/health/components/operations-status/operations-status.test.ts
frontend/app/routes/health/health-refresh-controller.test.ts
frontend/app/routes/health/health-refresh-controller.ts
frontend/app/routes/health/route.ui.test.tsx
frontend/app/routes/queue/controllers/websocket-controller.test.ts
frontend/app/routes/settings/maintenance/start-maintenance-task.test.ts
frontend/app/routes/settings/maintenance/use-maintenance-run.test.ts
frontend/app/routes/settings/maintenance/use-maintenance-run.ts
scripts/.DS_Store
scripts/__pycache__/nzbdav_arr_report_validation.cpython-314.pyc
scripts/__pycache__/nzbdav_benchmark.cpython-314.pyc
scripts/__pycache__/nzbdav_grab_to_plex_benchmark.cpython-314.pyc
scripts/__pycache__/nzbdav_migrate_sqlite_to_postgres.cpython-314.pyc
scripts/__pycache__/nzbdav_safe_rclone_up.cpython-314.pyc
scripts/__pycache__/validate_trx_results.cpython-314.pyc
scripts/nzbdav_grab_to_plex_benchmark.py
scripts/validate_trx_results.py
tests/.DS_Store
tests/__pycache__/test_nzbdav_arr_report_validation.cpython-314.pyc
tests/__pycache__/test_nzbdav_benchmark.cpython-314.pyc
tests/__pycache__/test_nzbdav_grab_to_plex_benchmark.cpython-314.pyc
tests/__pycache__/test_nzbdav_migrate_sqlite_to_postgres.cpython-314.pyc
tests/__pycache__/test_nzbdav_safe_rclone_up.cpython-314.pyc
tests/__pycache__/test_release_workflow_contract.cpython-314.pyc
tests/__pycache__/test_runtime_release_contract.cpython-314.pyc
tests/__pycache__/test_validate_trx_results.cpython-314.pyc
tests/test_nzbdav_grab_to_plex_benchmark.py
tests/test_release_workflow_contract.py
tests/test_runtime_release_contract.py
tests/test_validate_trx_results.py
```

`.superpowers/sdd/**` is absent from Appendix B because local
`.git/info/exclude` excludes it. Those files existed before this documentation
pass and are listed explicitly in the Changed-file ledger.
