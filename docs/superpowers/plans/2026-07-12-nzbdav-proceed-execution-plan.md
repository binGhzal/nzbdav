# NZBDav Proceed Execution Plan

**Date:** 2026-07-12
**Status:** Approved implementation scope, corrected after repository and runtime audit
**Related documents:**

- `docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md`
- `docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md`
- `HANDOFF.md`

## Objective

Keep SQLite and the single-control-owner topology, remove full article verification from the normal ARR import critical path, preserve explicit failure visibility, finish the local-wall timestamp contract, and build provider-native PostgreSQL migrations plus a bounded synthetic transfer path while PostgreSQL application startup remains disabled.

The grab-to-Plex target is measured from accepted grab to Plex metadata visibility. Code changes may remove known internal blockers, but the `<5 seconds` SLO is not considered proven until a controlled release is measured through the actual ARR, rclone, Plex, storage, and network path.

## Explicit scope and safety boundaries

- Backup and restore work is excluded by the user's latest instruction.
- No production database, real source database, deployed service, or existing PostgreSQL container is touched.
- PostgreSQL tests use a uniquely named `postgres:16-alpine` 16.14 container with tmpfs storage, a random loopback port, and guaranteed cleanup.
- Normal PostgreSQL application startup remains fail-closed.
- No commit, push, pull request, or deployment is performed without a separate instruction.
- Existing dirty-worktree changes are preserved. Tests are added around them; no revert or synthetic RED state is manufactured by discarding reviewed work.

## Corrected audited baseline

The current dirty worktree, not HEAD and not the older handoff counts, is the implementation baseline:

- 49 SQLite migration classes.
- 50 migration metadata references to `DavDatabaseContext` including the snapshot.
- 28 modeled tables, 240 columns, 28 primary keys, 8 foreign keys, and 56 explicit secondary indexes.
- PostgreSQL would have 84 physical indexes when primary-key indexes are included; the prior phrase “84 explicit index contracts” is incorrect.
- 55 backend parameterless `DavDatabaseContext` construction sites across 26 files.
- 11 direct `Migrate`/`MigrateAsync` call sites across production and tests.
- The current JSON transfer is version 2, materializes whole tables, permits replacement, and is not the planned bounded transfer v3.
- Repository-local `dotnet-ef` is absent. EF Core design/SQLite packages are 10.0.9; Npgsql EF is 10.0.2.

Before provider ownership is changed, tests and manifests must freeze this exact 49-migration SQLite baseline and preserve the SQLite runtime/version gate plus telemetry interceptors.

## Decision 1: post-download verification state machine

### Normal healthy path

1. Queue completion, durable rclone invalidation, history, import receipts, ARR import command, and the post-download verify job are committed as they are today.
2. The ARR import command waits only for its required durable rclone invalidation paths.
3. Active post-download verification no longer hides SAB history, is not an ARR prerequisite, and is not projected as an active SAB download-queue row. Verify-lane visibility remains available through worker/health diagnostics.
4. ARR visibility publication and refresh can proceed while verification runs on the verify lane.
5. Healthy verification completes its worker job independently.

### Inconclusive/provider-error path

1. Persist an inconclusive health result with no repair job.
2. For post-download verification, return an explicit `Indeterminate` outcome to the worker runner.
3. Fail the leased verify job as retryable/provider and use its bounded retry schedule.
4. Do not fail history, do not mark import receipts for review, and do not cancel or quarantine the ARR command.
5. At the retry ceiling only the verify worker job is quarantined, so diagnostics remain visible without retroactively claiming confirmed data loss.

### Confirmed missing path when automatic repair is enabled

1. Persist the unhealthy result and enqueue the repair job.
2. Complete the post-download verify job.
3. Existing repair visibility policy may continue to withhold a damaged release while repair is active. This exception is outside the healthy `<5 seconds` path.

### Confirmed missing path when automatic repair is disabled

The following transition is idempotent and crash-retryable:

1. Persist an unhealthy/action-needed result whose message states that repair is disabled; do not enqueue a repair job.
2. Resolve the release identity from the target item's `HistoryItemId`, `HistoryItems.DownloadDirId`, or durable import receipts.
3. In one database transaction where the rows still exist:
   - change history from `Completed` to `Failed` and set a bounded failure message;
   - change all non-removed matching import receipts, including already `Imported` receipts, to the distinct terminal `VerificationQuarantined` state with the same reason. This state must not be accepted by later automatic ARR `Imported` events; reconciliation-only `NeedsReview` remains recoverable;
   - terminalize the matching ARR import command as `Quarantined`, set `VisibleAt`, clear its lease, and retain a bounded error.
4. Authenticated-fail the leased post-download verify job immediately as `InvalidData`/`Quarantined` rather than completing it.
5. Publish the canonical history/health diagnostics best-effort after durable state is written.

The domain transition is written before the worker is terminalized. If the process dies between them, the lease expires and the same idempotent transition is retried. A stale ARR worker result is fenced by the cleared lease/token and cannot overwrite `Quarantined`. An ARR refresh or imported lifecycle event already accepted before the verifier wins cannot be recalled or overwrite `VerificationQuarantined`; the terminal receipt, health result, and quarantined verify job preserve the late-failure signal even if ARR has already removed the SAB history row.

If verification wins before a later SAB history-removal request, that stale removal is fenced as well: it may return a protocol-safe success, but it must not change `VerificationQuarantined`, delete the failed history row, cascade-delete the quarantined ARR command, queue cleanup, or publish a removal event. A receipt already `Removed` before verification remains terminal and is not resurrected.

## Task 1: implement post-import verification and quarantine

### RED tests

Add or change focused tests before production code:

- `GetHistoryControllerTests`: an active post-download verify job does not hide a completed row after ARR visibility is published.
- `GetQueueControllerTests`: an active post-download verify job is absent from the SAB download queue and its totals/status; active repair remains represented because confirmed damage is an exceptional blocking path.
- `SabRequestTests` and `GetQueueControllerTests`: SAB queue `limit=0` means “all” up to `SabPagination.MaxLimit`, matching the Sonarr/Radarr request contract instead of returning an empty page. Keep this queue-specific so frontend/history pagination semantics do not change accidentally.
- `ArrImportCommandServiceTests`: pending/leased/retry post-download verify states do not block visibility or dispatch; required rclone invalidation still blocks.
- `HealthCheckRepairPolicyTests`:
  - missing segments and missing metadata do not enqueue repair when `repair.enable=false`;
  - the same conditions do enqueue repair when enabled;
  - post-download provider/unknown outcomes retry the worker and never fail history/import state;
  - provider/unknown health websocket events publish only after their database result is durable and are suppressed on save failure;
  - healthy verification websocket events follow the same durable-first and caller-owned-transaction suppression rule, including directory aggregation;
  - confirmed missing with repair disabled marks existing history failed, receipts `VerificationQuarantined`, ARR command quarantined, and the verify job quarantined;
  - a pre-import `Available`, in-flight `UnlinkClaimed`, reconciliation `NeedsReview`, and late `Imported` receipt each become `VerificationQuarantined`, while `Removed` remains terminal;
  - a later ARR imported lifecycle event cannot change `VerificationQuarantined` back to `Imported`;
  - a later SAB history-removal request cannot erase a quarantine that was committed first, while a removal committed first still wins;
  - an executing ARR command cannot overwrite verification quarantine with a stale lease result;
  - the transition remains safe when history was already removed.
  - a multi-file damaged release performs one release-wide quarantine transition and one bulk receipt update rather than rescanning/updating all receipts once per missing child.
- `ImportReceiptServiceTests`: verification quarantine has a distinct terminal state, uses one bulk database update per release, rejects later automatic import transitions, and does not weaken the existing reconciliation-only `MarkNeedsReviewAsync` rule.
- status/API/frontend tests: ARR quarantine count and reason are exposed and rendered as danger state.

### GREEN implementation

Expected production files:

- `backend/Services/HistoryVisibilityService.cs`
- `backend/Services/ArrImportCommandService.cs`
- `backend/Services/HealthCheckService.cs`
- `backend/Services/ImportReceiptService.cs`
- `backend/Api/SabControllers/GetQueue/GetQueueController.cs`
- `backend/Api/SabControllers/GetQueue/GetQueueResponse.cs`
- `backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryController.cs`
- `backend/Api/SabControllers/GetQueue/GetQueueRequest.cs`
- `backend/Api/SabControllers/SabPagination.cs`
- `backend/Database/Models/ArrImportCommand.cs`
- `backend/Database/DavDatabaseClient.cs`
- `backend/Api/SabControllers/StatusDiagnostics.cs`
- `frontend/app/clients/backend-client.server.ts`
- `frontend/app/routes/health/components/operations-status/operations-status.tsx`

No schema migration is needed for the new enum value because the status is stored as an integer. The SQLite model snapshot and PostgreSQL baseline manifest must nevertheless include the enum's operational meaning in tests.

### Focused verification

Run the affected backend filters, frontend component tests/typecheck, backend build, and `git diff --check`. Then run the full backend and frontend suites before proceeding to provider work.

## Task 2: finish the provider-independent local-wall timestamp contract

The four legacy properties remain deployment-local wall time:

- `DavItem.CreatedAt`
- `HistoryItem.CreatedAt`
- `QueueItem.CreatedAt`
- `QueueItem.PauseUntil`

Duration measurement remains monotonic. Add the missing central contract tests plus ARR correlation/search cutoff tests and SQLite command-interceptor assertions for bound local cutoffs. `TimeProvider.GetLocalNow().DateTime` is the testable form of a captured `DateTime.Now`.

Audit every production write to those four fields, including transient DFS nodes and watch-folder retry rows. No write may stamp UTC into a local-wall field. Add a source-level regression test that rejects direct `DateTime.UtcNow` assignments to the four fields, while allowing explicit UTC writes to the repository's `DateTimeOffset` fields.

PostgreSQL session-timezone, generated SQL, provider round-trip, infinity-conversion, and startup-mismatch tests move to Task 4 because the provider-specific context does not exist before then.

## Task 3: freeze SQLite migration ownership before splitting contexts

1. Pin repository-local `dotnet-ef` 10.0.9 in `.config/dotnet-tools.json` and restore it.
2. Add an exact ordered migration-ID manifest for the current 49-class chain, with `DavDatabaseContext` retained as the exact SQLite owner.
3. Add a SQLite logical-schema manifest for all 28 tables, 240 columns, keys, and explicit indexes.
4. Assert the SQLite gate/interceptors are retained by the SQLite context.
5. Assert no pending model changes and upgrade from selected historical checkpoints through all 49 migrations.
6. Record the 49 migration-file hashes in a reviewed manifest so later provider work cannot silently rewrite inherited history.

## Task 4: split provider ownership and add native PostgreSQL migrations

EF Core 10 discovers migrations by exact context type. Therefore the compatibility context name cannot be replaced or retargeted while the current parameterless production construction sites keep using it: that mismatch could make migration discovery return an empty set without an obvious compile-time failure. The implementation audit found 53 explicit constructions across 25 production files plus one scoped DI activation; acceptance is the invariant of zero direct constructions outside approved factories, not a frozen caller count.

1. Keep `DavDatabaseContext` as the concrete SQLite context and exact owner of the inherited 49 migrations. Leave all existing migration metadata references unchanged.
2. Unseal `DavDatabaseContext`, add a protected options constructor, and make its parameterless construction explicitly SQLite-only/fail-closed when PostgreSQL is selected.
3. Add `PostgreSqlDavDatabaseContext : DavDatabaseContext` as a second exact migration identity, plus separate design-time factory and `__EFMigrationsHistory_PostgreSql` history table. Keep both migration sets in the backend assembly; separate output folders and exact context identity are sufficient.
4. Extract option construction carefully while preserving the SQLite WAL/FULL policy, runtime gate, foreign-key enabler, custom migration SQL generator, telemetry interceptors, content-index interceptor, and retry behavior. Add a runtime factory returning the base type but constructing the exact provider owner; route every parameterless production construction and the scoped DI registration through it. Acceptance is zero direct parameterless production construction outside the approved runtime/design factories. Keep the PostgreSQL startup refusal before the first factory call and before connection-string access.
5. Replace production direct migration calls with a `DatabaseMigrator` that preserves `--db-migration [target]` for SQLite.
6. Keep normal PostgreSQL startup and legacy migration/transfer-v2 commands refused.
7. Add PostgreSQL empty-schema preflight, native baseline, a reserved provider-neutral `database.import-state` record in `ConfigItems`, and operational trigger/function migration targeting only `PostgreSqlDavDatabaseContext`. Store bounded v3 progress as JSON in that reserved record and exclude it from transferred application configuration; do not add a provider-only application table.
8. Configure PostgreSQL `timestamp without time zone` for the four local-wall fields, session timezone verification, and Npgsql infinity behavior in the PostgreSQL subtype only.
9. Refactor `RemoveUnlinkedFilesTask` to keep one explicitly opened context/connection for a run and use a true connection-scoped temporary linked-ID table on both providers. Read `COUNT()` as `long`, retain checked UI conversion, and prove success/cancellation/failure leave no persistent schema object.
10. Keep `Npgsql.EntityFrameworkCore.PostgreSQL` at 10.0.2 for this slice. Its resolved core driver is already Npgsql 10.0.3; use `Gss Encryption Mode=Disable` only for the disposable password/SCRAM path and add an Alpine connection smoke gate because current upstream Alpine/Kerberos failures remain unresolved. Do not silently disable GSS for a future environment that requires it.
11. Apply from zero and inspect the exact schema/functions/triggers in the disposable PG16 container. Verify the SQLite migration hashes and snapshot are unchanged, then drop the disposable database/container.

The earlier plan's PostgreSQL confirmation gate applies here and is satisfied only for the disposable local test container authorized by “proceed.” It does not authorize a real target or deployment.

## Task 5: bounded transfer v3, synthetic only

1. Leave legacy v2 commands available only as explicitly legacy behavior; do not treat them as the promotion path.
2. Add strict raw SQLite validation against the frozen manifest.
3. Export deterministic, keyset-paginated, chunked JSONL plus blobs without materializing full tables.
4. Require a completely empty destination and remove `--replace` from the v3 path.
5. Use bounded per-chunk transactions and the durable reserved `database.import-state` marker; exclude that marker and derived `HealthCheckStats` from the stream, then rebuild the derived stats.
6. Allow PostgreSQL writes only through the narrowly scoped v3 import command while normal runtime remains fail-closed.
7. Prove synthetic SQLite-to-SQLite and SQLite-to-disposable-PG16 round trips, referential integrity, exact UUID/text/timestamp preservation, bounded memory, interruption refusal, and empty-target refusal.

No real source, live blob directory, cutover, backup, rollback drill, or deployment is part of this task.

## Final verification gates

- Focused RED/GREEN evidence for every behavior change.
- All backend tests with PostgreSQL-only tests first against disposable PG16 and then the complete suite.
- Frontend unit tests, typecheck, client build, SSR build, and server build.
- Python benchmark/transfer tests.
- SQLite WAL benchmark and runtime/version gate tests.
- Migration manifests and zero-to-head smoke tests for each provider.
- Disposable PostgreSQL container removed and no unknown pre-existing container modified.
- `git diff --check` clean.
- Independent specification and code-quality review of each implementation slice.

## Remaining proof input for the `<5 seconds` SLO

After code and synthetic verification pass, a real benchmark still needs:

- one controlled Sonarr or Radarr release;
- the exact NZBDav completed/mount path visible to ARR;
- the target Plex library and expected media identity;
- access to the relevant service endpoints/secrets in their existing files;
- permission to trigger the controlled grab and observe ARR/Plex.

Without those inputs the code can be made benchmark-ready and internal blockers can be removed, but the end-to-end SLO must remain unproven rather than inferred.
