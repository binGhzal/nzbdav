# NZBDav Transfer-v3 Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Last reconciled:** 2026-07-16T23:33:21+04:00

**Canonical handoff:** [`HANDOFF.md`](../../../HANDOFF.md)

**Goal:** Import one typed, sealed Transfer-v3 Phase 3 snapshot into a newly migrated, bootstrap-only PostgreSQL 16.14 target, independently verify all database rows and a private target blob stage, and atomically finish at `database-verified(A)` without enabling PostgreSQL or publishing runtime blobs.

**Architecture:** Keep SQLite and the one-control-owner topology as the production default. Phase 4 is a private, test-helper-only PostgreSQL transfer pipeline: source-only representability preflight; exact target admission; bounded in-memory or private-spool direct COPY; private descriptor-relative blob staging; independent locked read-committed verification; and an MVCC-safe final state transition. All PostgreSQL connections come from one private, diagnostics-disabled, nonpooling Npgsql data source pinned to one target identity. PostgreSQL stays disabled and no runtime/public path can reach this code.

**Tech Stack:** .NET 10, C# 14, Npgsql 10.0.3, EF Core 10 only for the existing model/migrations (never transferred-row insertion), PostgreSQL 16.14, xUnit 2.9, POSIX descriptor-relative I/O, Python 3 completion harness, Docker with an exclusively owned pinned PostgreSQL container.

**Scope:** Private Phase 4 transfer foundations, source preflight, target admission,
bounded table/blob staging, independent verification, terminal lifecycle, and an
exclusively owned completion harness. Tasks are implemented in numeric order.

**Non-goals:** Runtime PostgreSQL enablement, deployment, cutover, backup/restore,
runtime blob publication, ARR/Plex work, n8n, UI/rebrand work, or production
resource access.

**Source specifications:**

- `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md`
- `backend/Database/Transfer/Contracts/transfer-v3-source-contract.json`
- `backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json`
- `backend/Database/PostgreSqlCatalogs/postgresql-native-head-catalog.txt`

**Current status summary:** Tasks 0-7 are `COMPLETE` with current pure regression
coverage. Task 8 is `IN PROGRESS`. Its supporting contracts pass, but the exact
Task 8 gate has two failures and the clean test build has two `xUnit2000` errors.
Tasks 9-21 are `NOT STARTED`. No task is blocked. Task 20 deliberately owns all
live PostgreSQL completion proof.

**Explicit first unfinished task:** Task 8. Repair the two exact source/test
defects recorded under Task 8, rerun every listed pure gate, obtain two fresh
read-only reviews, then record the evidence in this plan and `HANDOFF.md`. Do
not start Task 9 before Task 8 is sealed.

## Reconciled task status

| Task | Status | Current evidence or next gate |
| --- | --- | --- |
| 0-7 | COMPLETE | 2026-07-16 pure Transfer-v3 run exercised their current code; only the two Task 8 admission assertions failed. Canonical evidence is summarized here and in `HANDOFF.md`; local SDD records are optional. |
| 8 | IN PROGRESS | 24/26 exact Task 8 tests passed; affected supporting contracts passed 390 with 12 deliberate no-connection skips; two structural failures and two `xUnit2000` errors remain. |
| 9-19 | NOT STARTED | Planned production symbols are absent. Execute in numeric order after Task 8. |
| 20 | NOT STARTED | Owns the isolated PostgreSQL 16.14 completion/crash/contention proof and runner. |
| 21 | NOT STARTED | Owns final isolation, documentation, full regression, and independent review. |

Explicit status labels govern continuation. Checkboxes under completed tasks are
retained as the original acceptance specification and historical execution
record; they are not a second status source. Every unfinished implementation
task follows red, minimal implementation, focused green, regression, docs,
diff review, and only then an explicitly authorized commit. Recovery never uses
reset, clean, checkout, restore, stash, or another destructive worktree action.

## Global Constraints

- The approved design is `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md`; if code and this plan conflict with that design, stop and resolve the discrepancy instead of improvising.
- SQLite remains the production default. PostgreSQL remains disabled. Do not change `backend/Program.cs`, `entrypoint.sh`, provider selection, Compose, release behavior, controllers, or runtime service registration to expose Phase 4.
- Do not implement backup/restore, n8n, runtime blob publication, a migration-completion marker, cutover, deployment, ARR/Plex interaction, or the grab-to-Plex benchmark in this phase.
- Use synthetic fixtures and uniquely owned disposable resources only. Never connect to, inspect, mutate, stop, restart, enumerate, or clean an existing PostgreSQL server/container, real SQLite database, real blob tree, or production service.
- Require one explicit quiescent operator window for the owned target database, endpoint/postmaster, DNS result, role/schema, and staging filesystem for the whole call. Advisory/relation locks protect cooperating writers only; they do not replace this requirement or claim safety against an uncooperative owner/superuser.
- Preserve the dirty shared worktree. Do not reset, checkout, clean, stage, commit, push, or reformat unrelated files. This plan deliberately uses review checkpoints instead of commit steps; Git mutations require fresh user authorization.
- Canonical task status and completion evidence live in this plan and `HANDOFF.md`. Locally excluded `.superpowers/sdd/**` reports, briefs, progress entries, and snapshots are optional corroboration only and never gate continuation or completion.
- This documentation is Git-durable, but all current Phase 4 implementation remains preserved-worktree-only until a separately reviewed implementation commit is explicitly authorized. In a docs-only checkout, stop rather than recreating absent completed-task files from the plan.
- Every production behavior change follows red-green TDD. Write one focused failing test, run it and record the expected failure, implement the minimum complete contract, rerun focused tests, then run the task's regression filter.
- Place all new provider-specific implementation files under `backend/Database/Transfer/Phase4/` and tests under `backend.Tests/Database/Transfer/Phase4/`. Keep the namespace `NzbWebDAV.Database.Transfer`; the directory boundary preserves the existing Phase 1-3 source-side isolation check.
- No Phase 4 API accepts a path to a source snapshot, a runtime blob root, a caller-opened connection, a caller-supplied identifier, a replace flag, or arbitrary SQL. Names come only from reviewed embedded contracts.
- No raw provider, parser, codec, transaction, filesystem, or cleanup exception may cross a Phase 4 boundary. Returned/loggable failures contain only fixed allowlisted codes, a fixed message, optional five-character SQLSTATE, and bounded allowlisted secondary codes.
- Never place a connection string, credential, manifest/row/field/blob value, API key, UUID, absolute path, digest, server detail/hint/context, or raw exception in command text or any Phase 4-owned log, trace, metric, error, or capture. Fixed reviewed SQL and identifiers must likewise remain absent from approved runtime captures. The one narrow negative-control exception is Npgsql's provider-owned metrics surface: an intentionally installed private test `MeterListener` may observe only the fixed data-source name, fixed `postgresql` system tag, generated loopback host/port, fixed connection-state literals, numeric measurements, and instrument metadata; it must observe no payload/credential/path/digest/canary and its capture is never public. Npgsql 10.0.3's separate provider-owned `Npgsql.Sql` EventSource can expose command text only when deliberately enabled; the dedicated helper therefore runs with .NET diagnostics disabled and no injected/in-process EventListener, rather than claiming that the data-source tracing filters control that independent path.
- The exact owned-managed-memory limit is 32 MiB, including the fixed 8 MiB parser/runtime reserve. The separate required logical staging ceiling has no default. Npgsql/PostgreSQL memory is not mislabeled as part of the 32 MiB counter.
- Construct that one managed budget before copying or hashing the manifest. Manifest storage, preflight parsing, retained table/blob receipts, digest storage, and every later phase use leases from the same counter; no phase creates a replacement or side budget.
- Every `NpgsqlCommand`, binary importer, and text importer/writer receives an explicit positive finite operation timeout at its creation site. Reconciliation reduces that timeout to the remaining ten-second fence and never inherits the ordinary 300-second value.
- Exact server/client gates are PostgreSQL `16.14` / `160014` and Npgsql `10.0.3`. Upgrades fail closed until contracts and diagnostics behavior are re-reviewed.
- A final commit outcome that cannot be fenced and proven is `unknown`: preserve the sealed target blob stage, suppress failed-state CAS, suppress destructive cleanup, close/quarantine handles, and require helper-process exit.
- The completion proof owns PostgreSQL from process/container creation through clean stop and stderr EOF. A connection-only test is not completion evidence.
- `scripts/run_transfer_v3_phase4_postgres_tests.py` is the sole authority for every live-PostgreSQL test. No test accepts or inherits an external server, connection, container, volume, schema, fixture, SQLite path, source snapshot, blob tree, or staging path.

## File and Responsibility Map

### Shared/frozen contracts

- `backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json`: exact 27-table/235-column PostgreSQL transfer mapping, target types, collations, key order, and COPY types.
- `backend/Database/PostgreSqlNativeMigrationContract.cs`: the two exact PostgreSQL migration IDs and product versions.
- `backend/Database/PostgreSqlFreshBootstrapContract.cs`: one exact definition of a fresh five-root/two-key/reserved-state target, reused by the native migrator and Phase 4.

### Phase 4 foundations

- `TransferV3Phase4Failure.cs`: redacted primary/secondary failures and one mapper.
- `TransferV3Phase4Budgets.cs`: managed-memory leases, charged fixed digests, and logical staging ledger.
- `TransferV3Phase4StagingParent.cs`: pinned, same-UID trusted staging-parent descriptor.
- `TransferV3PostgreSqlTargetContract.cs`: embedded target mapping loader and identifier-safe SQL metadata.
- `TransferV3PostgreSqlTargetDescriptor.cs`: strict pre-open connection normalization and one private data source.
- `TransferV3PostgreSqlOpenAttempt.cs` and `TransferV3PostgreSqlSession.cs`: post-open validation ownership, one-shot transfer into a validated session, quarantine, and bounded connection close.
- `TransferV3PostgreSqlServerContract.cs`: logging, durability, encoding, time-zone, trigger, and read/write preflight.
- `TransferV3PostgreSqlTargetIdentity.cs`: immutable cluster/postmaster/database/schema/role/endpoint identity.
- `TransferV3PostgreSqlAdmissionLockSet.cs`: shared advisory key and exact 29-relation lock statement.
- `TransferV3PostgreSqlAdmissionValidator.cs`: catalog/history/bootstrap admission and `fresh -> importing(A)`.
- `TransferV3PostgreSqlDeadline.cs` and `TransferV3PostgreSqlCommitReconciler.cs`: one ten-second monotonic reconciliation fence.

### Table import pipeline

- `TransferV3CanonicalLogicalDigest.cs`: canonical cursor/field-length/encoded-field source digest framing.
- `TransferV3PostgreSqlRepresentabilityPreflight.cs`: source-only target representability and peak staging bound.
- `TransferV3Phase4WorkDirectory.cs`: nonce-owned work root and exact-empty durable removal.
- `TransferV3Phase4InMemoryBatch.cs`: charged current-batch typed storage.
- `TransferV3Phase4BatchSpool.cs`: append-only identity-proven spill and independent replay receipts.
- `TransferV3PostgreSqlTextCopyEncoder.cs`: exact bounded PostgreSQL text-COPY representation and escaping.
- `TransferV3PostgreSqlCopy.cs`: binary and text COPY sinks with correct cancellation semantics.
- `TransferV3PostgreSqlTableObserver.cs` and `TransferV3PostgreSqlTableImportCoordinator.cs`: per-table async parsing, bootstrap rules, committed batches, and source receipts.

### Blob stage, independent verification, and orchestration

- `TransferV3TargetBlobStageReceipt.cs`: canonical `blob-stage.json` codec.
- `TransferV3TargetBlobStageBuilder.cs` and `TransferV3TargetBlobStageCandidate.cs`: streaming private target-tree construction, bottom-up sealing, and one-shot candidate ownership.
- `TransferV3TargetBlobStageVerifier.cs`: source-order content proof plus exact fixed-grammar enumeration.
- `TransferV3DatabaseVerifiedStage.cs`: opaque retained descriptors, allocation-free success/unknown ownership transitions, and precommit cleanup authority.
- `TransferV3PostgreSqlTargetRowEncoder.cs`: independent provider-to-transfer encoding with sequential text reads.
- `TransferV3PostgreSqlTargetVerifier.cs`: new-session read-committed counts/digests/catalog/blob proof and final CAS.
- `TransferV3Phase4Coordinator.cs`: exact lifecycle, failure policy, data-source disposal, and typed success return.

### Completion proof

- `backend.TransferV3Phase4.TestHelper/`: the only process allowed to run the complete coordinator in Phase 4.
- `scripts/run_transfer_v3_phase4_postgres_tests.py`: exclusively owned PostgreSQL 16.14 launcher/controller/log scanner.
- `backend.Tests/Database/Transfer/Phase4/*CompletionTests.cs`: helper-driven integration, crash, reconciliation, redaction, and durability tests.

## Dependency Order

1. Redacted failures and frozen target mapping.
2. Budgets, staging-parent ownership, descriptor, server contract, and target identity.
3. Shared fresh-target/history contracts, transaction-bound state operations, admission, and reconciliation.
4. Async parser, source preflight, current-batch/spool, COPY, and table import.
5. Blob construction/verification, target row verification, and top-level coordinator.
6. Isolated helper, exclusively owned PostgreSQL completion harness, crash/log proof, then full regression and independent review.

---

### Task 0: Record the approved boundary and execution baseline

**Status:** COMPLETE

**Objective:** Approve the Phase 4 boundary without changing its technical
contract and capture the pre-implementation worktree, build, and Transfer-v3
baseline.

**Dependency and ordering:** First task. Its attribution and safety evidence
govern every later production task.

**Preconditions:** The design exists; dirty paths are inventoried; no database,
container, service, blob path, Git index, or remote is touched.

**Interfaces consumed and produced:** Consumes the draft design, live worktree,
and existing transfer gates. Produces the approved design status and frozen
baseline evidence.

**Current evidence:** Complete. Release build had zero warnings/errors; 670
Transfer tests passed with 6 deliberate no-connection skips; review was clean.
The 2026-07-16 pure run found only Task 8 failures.

**Expected result and acceptance:** Every checked step below has recorded
evidence, design technical wording is unchanged, and no external resource or
Git mutation occurred.

**Documentation impact:** Design status, active plan, Task 0 report, progress,
and handoff only. No shipped behavior changed.

**Recovery and risks:** Stop and reconcile the handoff if branch, HEAD, or dirty
paths drift. Preserve all worktree content; never reset, clean, restore, or
stash.

**Files:**

- Modify: `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md`
- Verify only: current worktree and existing tests; no production file changes.

**Interfaces:**

- Change the design status from draft to `Approved for implementation planning on 2026-07-14` without altering its technical contract.
- Capture a baseline that distinguishes existing failures/skips from Phase 4 work.

- [x] Run `git status --short` and save the output in the task notes; identify every pre-existing modified/untracked path so later review does not attribute it to Phase 4.
- [x] Change only the design status line. Run `git diff -- docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md` and verify no technical wording changed.
- [x] Run `dotnet build backend/NzbWebDAV.csproj --configuration Release --no-restore -warnaserror`. Expected: exit `0`; if the existing workspace fails, record the exact baseline and stop before attributing it to Phase 4.
- [x] Run `env -u NZBDAV_TEST_POSTGRES_CONNECTION_STRING -u NZBDAV_REQUIRE_POSTGRES_TESTS dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~backend.Tests.Database.Transfer' --logger 'console;verbosity=minimal'`. Expected baseline: existing Phase 1-3 pure tests pass and the existing PostgreSQL class is selected only as a deliberate no-connection skip; this is baseline evidence, not Phase 4 completion proof.
- [x] Review checkpoint: confirm no database, container, service, or blob path was opened and no Git mutation occurred.

### Task 1: Central redacted failure and bounded diagnostic model

**Status:** COMPLETE

**Objective:** Provide one fixed, value-free failure model, bounded secondary
storage, exact caller-cancellation rule, and allowlisted PostgreSQL SQLSTATE
handling for every later Phase 4 boundary.

**Dependency and ordering:** After Task 0 and before any parser, provider,
filesystem, or cleanup boundary can expose a raw failure.

**Preconditions:** Task 0 baseline exists; exact primary/secondary literals and
cancellation precedence in plan/design govern the implementation.

**Interfaces consumed and produced:** Consumes `Exception`, caller tokens, and
`Npgsql.PostgresException`. Produces `TransferV3Phase4Exception`, boundary and
secondary enums, cleanup result, and `TransferV3Phase4FailureMapper`.

**Current evidence:** Complete. Exact two-file scope; focused 52/52 and pure
regression 96/96; sensitive-pattern scan clean; independent specification and
quality reviews approved; no 2026-07-16 Task 1 failure.

**Expected result and acceptance:** Every checked test and review step below
passes; all exposed text remains fixed/value-free; secondary storage stays
bounded and insertion ordered.

**Documentation impact:** Developer-only failure contract in plan, design,
Task 1 report, progress, and handoff. No public/runtime exposure.

**Recovery and risks:** Any raw-detail canary leak stops dependent work. Fix
forward in mapper/tests, preserve the original sanitized primary, and rerun the
listed gates without destructive worktree recovery.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4Failure.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4FailureTests.cs`

**Interfaces:**

```csharp
internal enum TransferV3Phase4Boundary
{
    Argument,
    Parser,
    Codec,
    PostgreSqlOpen,
    PostgreSqlCommand,
    PostgreSqlCopy,
    PostgreSqlCommit,
    Posix,
    Cleanup,
    Unexpected,
}

internal enum TransferV3Phase4SecondaryCode
{
    None,
    ObserverAbortFailed,
    CopyCancelFailed,
    TransactionRollbackFailed,
    SpoolResidue,
    BlobStageResidue,
    FailedStateCasZeroRows,
    FailedStateCasUnknown,
    ConnectionCloseFailed,
    DataSourceDisposeFailed,
    SourceReadCloseFailed,
    DeadlineAbandonedProviderTask,
    CleanupDeadlineExceeded,
    CommitOutcomeUnknown,
}

internal sealed class TransferV3Phase4Exception : Exception
{
    internal string Code { get; }
    internal string? SqlState { get; }
    internal IReadOnlyList<string> SecondaryCodes { get; }
    internal bool TryAddSecondary(TransferV3Phase4SecondaryCode code);
}

internal readonly record struct TransferV3Phase4CleanupResult(
    TransferV3Phase4SecondaryCode First,
    TransferV3Phase4SecondaryCode Second,
    TransferV3Phase4SecondaryCode Third,
    TransferV3Phase4SecondaryCode Fourth);

internal static class TransferV3Phase4FailureMapper
{
    internal static Exception Sanitize(
        Exception raw,
        TransferV3Phase4Boundary boundary,
        CancellationToken callerToken);
}
```

The exact primary codes are:

- `Argument -> phase4-argument`
- `Parser -> phase4-parser`
- `Codec -> phase4-codec`
- `PostgreSqlOpen -> phase4-postgresql-open`
- `PostgreSqlCommand -> phase4-postgresql-command`
- `PostgreSqlCopy -> phase4-postgresql-copy`
- `PostgreSqlCommit -> phase4-postgresql-commit`
- `Posix -> phase4-posix`
- `Cleanup -> phase4-cleanup`
- `Unexpected` and every invalid boundary value -> `phase4-unexpected`

Every `TransferV3Phase4Exception.Message` is exactly
`Transfer-v3 Phase 4 failed.`. The fixed caller-cancellation message is exactly
`Transfer-v3 Phase 4 was canceled.` with null inner exception and the exact
caller token. Treat cancellation as caller cancellation only when the token is
cancellable, already requested, and equals the raw
`OperationCanceledException.CancellationToken`.

The exact non-`None` secondary strings, in enum order, are
`observer-abort-failed`, `copy-cancel-failed`,
`transaction-rollback-failed`, `spool-residue`, `blob-stage-residue`,
`failed-state-cas-zero-rows`, `failed-state-cas-unknown`,
`connection-close-failed`, `data-source-dispose-failed`,
`source-read-close-failed`, `deadline-abandoned-provider-task`,
`cleanup-deadline-exceeded`, and `commit-outcome-unknown`. `None`, invalid
values, duplicates, and a fifth distinct value return false without changing
or allocating secondary storage. Never derive a string with `Enum.ToString()`.

`Sanitize` returns an existing `TransferV3Phase4Exception` by reference before
other handling, then applies the exact caller-cancellation rule, and only then
maps an ordinary raw failure through the boundary switch. Retain SQLSTATE only
from an `Npgsql.PostgresException` at a
`PostgreSqlOpen`, `PostgreSqlCommand`, `PostgreSqlCopy`, or
`PostgreSqlCommit` boundary when it is exactly five ASCII `[0-9A-Z]`
characters; otherwise expose null. Do not normalize or inspect another field,
inner exception, or `Data` for SQLSTATE.

- [x] Write failing tests covering every boundary and asserting fixed code/message output, only a five-character SQLSTATE, at most four preallocated secondary slots, deduplication, and stable insertion order.
- [x] Include invalid primary/secondary enum tests and exact secondary-string tests. Assert `None`, an invalid cast, a duplicate, and a fifth distinct secondary each return false without throwing, consuming a slot, or changing prior entries; assert every successful insertion returns true.
- [x] Preserve-by-reference tests pass an already-sanitized failure with an invalid boundary and assert object identity plus unchanged code, SQLSTATE, and secondary state.
- [x] SQLSTATE tests round-trip one valid value such as `23505` at all four PostgreSQL boundaries; the same `PostgresException` at every non-PostgreSQL and invalid boundary exposes null. Reject lowercase, whitespace/punctuation, non-ASCII, NUL, four/six characters, a generic `NpgsqlException`, and values present only in an inner exception or `Data`.
- [x] Add adversarial exceptions whose message, `InnerException`, `Data`, Npgsql detail/hint/context/internal-query/schema/table/column/constraint fields, path, UUID, API key, and digest contain unique canaries. Assert none appear in `Message`, `ToString()`, properties, or secondary codes.
- [x] Add cancellation tests: only a raw `OperationCanceledException` with the exact cancellable, requested caller token becomes a fresh exception whose message is exactly `Transfer-v3 Phase 4 was canceled.`, whose inner exception is null, and whose token is exactly the caller token. Default/default tokens, the same but unrequested token, a different requested raw token, and a raw default token with a requested caller token each map to the fixed failure/code for the supplied boundary.
- [x] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4FailureTests'`. Expected red: missing types.
- [x] Implement one allowlist mapper. Do not copy `Exception.Data`, attach `InnerException`, retain the raw exception, or reuse `TransferV3ImportFailurePolicy` (it stores diagnostics in raw `Data`).
- [x] Use a fixed array allocated with the sanitized exception for secondary slots; `TryAddSecondary` must never resize or throw.
- [x] Rerun the focused command. Expected green.
- [x] Review checkpoint: search `rg -n 'InnerException|\.Data\[|throw new AggregateException|ToString\(\)' backend/Database/Transfer/Phase4` and inspect every match.

### Task 2: Pin Npgsql and freeze the exact PostgreSQL transfer mapping

**Status:** COMPLETE

**Objective:** Pin direct Npgsql 10.0.3 and freeze the strict embedded
27-table/235-column PostgreSQL mapping, five-column derived health contract,
and safe SQL fragments.

**Dependency and ordering:** After Tasks 0-1; before budgets, descriptors,
admission, COPY, or verification consume target metadata.

**Preconditions:** Reviewed source contract, EF design model, checked-in head
catalog, and Task 1 failure boundary exist; restore resolves exact Npgsql 10.0.3.

**Interfaces consumed and produced:** Consumes source contract, EF model, head
catalog, and Npgsql type identities. Produces the embedded target contract,
immutable table/column records, and reviewed fragment methods.

**Current evidence:** Complete. Direct Npgsql pin; exact 27/235 plus derived
five-column contract; Release build clean; focused 9/9 and source/model/catalog
regression 20/20; two independent P0/P1 reviews clean.

**Expected result and acceptance:** Every checked mapping, immutability,
adversarial JSON, package, fragment, build, and review step below passes with no
guessed or fallback mapping.

**Documentation impact:** Developer-only embedded contract and rationale. No
runtime provider promotion or user-facing availability change.

**Recovery and risks:** Fail closed on package/model/catalog/mapping drift and
re-review the complete mapping. Preserve Task 2 evidence and never guess a
fallback.

**Files:**

- Modify: `backend/NzbWebDAV.csproj`
- Create: `backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContract.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContractTests.cs`

**Interfaces:**

```csharp
internal sealed record TransferV3PostgreSqlColumnContract(
    int Ordinal,
    string Name,
    TransferV3ColumnKind SourceKind,
    string PostgreSqlType,
    string? Collation,
    bool Nullable,
    NpgsqlDbType BinaryCopyType);

internal sealed record TransferV3PostgreSqlTableContract(
    int Ordinal,
    string Name,
    IReadOnlyList<TransferV3PostgreSqlColumnContract> Columns,
    IReadOnlyList<string> KeyColumns,
    bool PreserveBootstrapRoots,
    bool FiltersReservedImportState);

internal sealed class TransferV3PostgreSqlTargetContract
{
    internal static TransferV3PostgreSqlTargetContract LoadEmbedded();
    internal IReadOnlyList<TransferV3PostgreSqlTableContract> Tables { get; }
    internal TransferV3PostgreSqlTableContract DerivedHealthCheckStats { get; }
    internal string GetQuotedTableName(TransferV3PostgreSqlTableContract table);
    internal string GetCopyColumnList(TransferV3PostgreSqlTableContract table);
    internal string GetOrderByList(TransferV3PostgreSqlTableContract table);
    internal string GetSelectProjection(TransferV3PostgreSqlTableContract table);
}
```

The embedded JSON shape is exact:

```json
{
  "formatVersion": 3,
  "tables": [],
  "derivedHealthCheckStats": {}
}
```

Each table object has exactly `ordinal`, `name`, `columns`, `keyColumns`,
`preserveBootstrapRoots`, and `filtersReservedImportState`. Each column has
exactly `ordinal`, `name`, `sourceKind`, `postgreSqlType`, `collation`,
`nullable`, and `binaryCopyType`. Properties and enum strings are case-sensitive
camelCase; enum numbers are forbidden. Exact source-kind strings are `uuid`,
`boolean`, `enumInt32`, `int32`, `int64`, `text`, `localWallTimestamp`, and
`instant`; exact binary-COPY-type strings are `uuid`, `boolean`, `integer`,
`bigint`, `text`, and `timestamp`. Arrays are order-significant; JSON
object property order and insignificant whitespace are not. Reject duplicate,
missing, unknown, or null non-nullable properties, comments, trailing commas,
unknown/numeric enums, and non-integral ordinals.

Table/column ordinals are zero-based source-contract array positions, never
physical PostgreSQL `attnum`. The 27 `Tables` entries and all columns remain in
exact source order. `KeyColumns` is the source keyset's ordered column sequence.
`DerivedHealthCheckStats` is excluded from `Tables`, has ordinal `27`, uses
zero-based derived-source column order, has both behavior flags false, and is
rejected by `GetCopyColumnList`; it remains valid for the other three fragment
methods.

Every exposed `Tables`, `Columns`, and `KeyColumns` object is backed by a deep
immutable defensive snapshot; no mutable array/list or deserialization DTO
escapes behind an `IReadOnlyList`. Mutation attempts and caller-owned/cloned
record changes cannot alter a contract-owned record, iteration order, or any
precomputed fragment.

Only `DavItems.PreserveBootstrapRoots` and
`ConfigItems.FiltersReservedImportState` are true. `Collation` is exactly `C`
only for explicitly C-collated target columns and null otherwise. Cross-check
`C` against physical `pg_catalog.C`, null Text collation against
`pg_catalog.default`, and noncollatable null against the catalog's empty value.

Every contract-sourced table and column identifier must match ASCII
`^[A-Za-z_][A-Za-z0-9_]{0,62}$` and is always double-quoted; fixed reviewed
PostgreSQL identifiers are emitted exactly as specified below. Each fragment
method accepts only one of the exact table
objects owned by that loaded contract instance and rejects null, cloned,
foreign, or merely value-equal records by reference identity. It never accepts
a name or arbitrary SQL. Exact fragment spelling uses comma-space separators:

- quoted table name: `"Table"`;
- COPY list: all `"Column"` values in source order;
- ORDER list: key columns in key order, with exact
  ` COLLATE pg_catalog."C"` on each contract-`C` key; and
- SELECT projection: non-Text as `"Column"`; Text as
  `pg_catalog.octet_length("Column"), "Column"`, in source order.

- [x] Add a direct `<PackageReference Include="Npgsql" Version="10.0.3" />`; do not rely on the EF provider's transitive resolution.
- [x] Run `dotnet restore backend.Tests/backend.Tests.csproj`, then verify `rg -n '"Npgsql/10\.0\.3"' backend/obj/project.assets.json` returns the exact resolved package.
- [x] Write a failing contract test that requires exactly 27 transferred tables, 235 transferred columns, one five-column derived `HealthCheckStats` mapping, unique ordinals/names, and exact source-contract order.
- [x] Add table-driven tests for the only allowed mappings: UUID→`uuid`/`Uuid`; Boolean→`boolean`/`Boolean`; EnumInt32 and Int32→`integer`/`Integer`; Int64 and Instant→`bigint`/`Bigint`; Text→the exact catalog `text` or `character varying(n)`/`Text`; LocalWallTimestamp→`timestamp without time zone`/`Timestamp`.
- [x] Cross-check every target type/nullability/collation against the PostgreSQL EF design model and checked-in head physical catalog. Assert no identity or computed/generated physical target column and no guessed/fallback mapping fact. Assert `WorkerJobs.LeaseGeneration` is the only transferred server-defaulted column, its default is exact `0`, and it remains in the explicit COPY mapping so the default is never invoked.
- [x] Assert zero-based source ordinals/order, exact key-column order, only the two true behavior flags, and the separate ordinal-27/non-copyable derived table contract.
- [x] Add adversarial JSON tests for every rejected shape rule and mismatched source/model/catalog fact, including the source-order/physical-`attnum` divergence.
- [x] Assert deep immutability of `Tables`, every `Columns`/`KeyColumns` collection, and the derived table graph: no mutable backing collection escapes, mutation/cast attempts fail, and caller-owned or cloned changes cannot alter owned iteration order or SQL fragments.
- [x] Assert every identifier is reviewed-safe, every exact fragment spelling above is produced only from contract-owned table references, and null/cloned/foreign/value-equal inputs are rejected. Do not accept a caller string.
- [x] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTargetContractTests'`. Expected red: resource/loader missing.
- [x] Implement the strict embedded JSON loader with unmapped-property rejection, canonical shape checks, and exact source-contract cross-validation.
- [x] Rerun focused tests. Expected green.
- [x] Review checkpoint: inspect the JSON diff table-by-table; a reviewer must account for all 27/235 entries rather than approving only totals.

### Task 3: Exact managed-memory/staging budgets and trusted staging parent

**Status:** COMPLETE

**Objective:** Establish the exact 32-MiB managed budget with 8-MiB runtime
reserve, charged digest/staging ledger, trusted descriptor-relative parent,
consumed options, and supported POSIX ABI gates.

**Dependency and ordering:** After Tasks 0-2; before parser, row, COPY, spool,
blob, or coordinator allocation and staging work.

**Preconditions:** Failure mapper and target contract are frozen; execution uses
a supported host ABI and trusted same-UID staging parent; no live data path.

**Interfaces consumed and produced:** Consumes the failure boundary, target
contract, native ABI, and operator staging descriptor. Produces managed budget,
digest ownership, staging budget/ledger, consumed options, POSIX directory, and
monotonic clock foundations.

**Current evidence:** Complete. Exact 11-file foundation; focused Debug/Release
114/114 with zero skips; Release build clean; pure regression 787 passed with 6
deliberate skips; Darwin arm64 ABI proof and independent reviews clean. The
parallel full-suite descriptor-count issue remains documented non-gating debt.

**Expected result and acceptance:** Every checked budget, ABI, filesystem,
allocation, probe, build, regression, and review step below passes on the
supported host matrix.

**Documentation impact:** Developer-only memory/POSIX foundation in plan,
design, Task 3 report, progress, and handoff. No later high-water claim is made.

**Recovery and risks:** Fail closed on ABI, identity, capacity, or accounting
uncertainty. Remove only identity-proven owned entries; uncertain residue
remains charged and is never broad-cleaned.

**Files:**

- Modify: `backend/Database/Transfer/TransferV3Posix.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4StagingParent.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4Options.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4ManagedBudgetTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4StagingLedgerTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4StagingParentTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4OptionsTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3PosixOwnedDirectoryTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3SnapshotDirectoryTests.cs`

**Interfaces:**

```csharp
internal readonly record struct TransferV3FileStat(
    TransferV3FileFingerprint Fingerprint,
    uint Mode,
    ulong LinkCount,
    uint OwnerUid);

// Additions to the existing TransferV3Posix boundary.
internal static uint GetEffectiveUserId();
internal static long GetAvailableBytes(SafeFileHandle handle);
internal static bool DescriptorHasCloseOnExec(SafeFileHandle handle);
internal static long DecodeAvailableBytesSnapshot(
    ReadOnlySpan<byte> snapshot,
    bool linux,
    bool macOs,
    Architecture architecture);

internal enum TransferV3Phase4MemoryKind
{
    RuntimeReserve,
    Manifest,
    Parser,
    Row,
    Field,
    Copy,
    Digest,
    Receipt,
    DirectoryEnumeration,
    Cleanup,
}

internal sealed class TransferV3Phase4MemoryLease : IDisposable
{
    internal long CapacityBytes { get; }
    internal TransferV3Phase4MemoryKind Kind { get; }
    internal void MarkManagedElementStorageAllocated(long elementStorageBytes);
    public void Dispose();
}

internal sealed class TransferV3Phase4ManagedBudget
{
    internal const long LimitBytes = 32L * 1024 * 1024;
    internal const long RuntimeReserveBytes = 8L * 1024 * 1024;
    internal const int RowReservationBytes = 256;
    internal const int FieldReservationBytes = 64;
    internal TransferV3Phase4MemoryLease Reserve(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind);
    internal bool TryReserve(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind,
        out TransferV3Phase4MemoryLease? lease);
    internal TransferV3Phase4MemoryLease ReserveCharacters(
        int capacityChars,
        TransferV3Phase4MemoryKind kind);
    internal bool TryReserveCharacters(
        int capacityChars,
        TransferV3Phase4MemoryKind kind,
        out TransferV3Phase4MemoryLease? lease);
    internal long CurrentBytes { get; }
    internal long PeakBytes { get; }
    internal long AvailableBytes { get; }
    internal long CurrentAllocatedManagedElementStorageBytes { get; }
    internal long CumulativeAllocatedManagedElementStorageBytes { get; }
}

internal sealed class TransferV3Phase4Digest : IDisposable
{
    internal const int SizeBytes = 32;
    internal static TransferV3Phase4Digest Create(
        TransferV3Phase4ManagedBudget managedBudget,
        ReadOnlySpan<byte> sha256);
    internal ReadOnlySpan<byte> Bytes { get; }
    internal void CopyLowerHexTo(Span<byte> destination);
    public void Dispose();
}

internal sealed class TransferV3Phase4StagingLedger
{
    internal const int EntryReservationBytes = 512;
    internal TransferV3Phase4StagingLedger(long maximumBytes);
    internal TransferV3Phase4StagingScope BeginScope();
    internal long CurrentBytes { get; }
    internal long PeakBytes { get; }
    internal long CurrentLogicalBytes { get; }
    internal long CurrentEntries { get; }
}

internal sealed class TransferV3Phase4StagingScope
{
    internal void Debit(long logicalBytes, int entries);
    internal long CurrentBytes { get; }
    internal long CurrentLogicalBytes { get; }
    internal long CurrentEntries { get; }
    internal void ReleaseAllAfterProvenRemoval();
}

internal sealed class TransferV3Phase4StagingParent : IDisposable
{
    internal static TransferV3Phase4StagingParent OpenOwned(string absolutePath);
    internal static void ValidateOwnedStat(
        TransferV3FileStat stat,
        uint effectiveUserId);
    internal static void ValidateRetainedStat(
        TransferV3FileStat opened,
        TransferV3FileStat current,
        uint effectiveUserId);
    internal TransferV3FileIdentity Identity { get; }
    internal SafeFileHandle DuplicateHandle();
    internal long GetAvailableBytes();
    public void Dispose();
}

internal sealed class TransferV3Phase4Options : IDisposable
{
    internal TransferV3Phase4Options(
        TransferV3Phase4StagingParent stagingParent,
        long maxPostgreSqlTextPayloadBytes,
        long maxPhase4StagingBytes);
    internal TransferV3Phase4ConsumedOptions Consume();
    public void Dispose();
}

internal sealed class TransferV3Phase4ConsumedOptions : IDisposable
{
    internal TransferV3Phase4StagingParent StagingParent { get; }
    internal long MaxPostgreSqlTextPayloadBytes { get; }
    internal long MaxPhase4StagingBytes { get; }
    public void Dispose();
}
```

- [x] Write failing lease tests proving construction creates the only synthetic `RuntimeReserve` charge and reports exact current/peak `8 MiB` and available `24 MiB`. Caller reservations reject nonpositive sizes, invalid kinds, and `RuntimeReserve` with `phase4-argument`; char capacity is checked `2 * capacityChars`. From the initial state, reserving 24 MiB reaches the ceiling, the next byte is refused with no lease/current/peak change, and a direct 32-MiB reservation is refused because it would total 40 MiB.
- [x] Prove `TryReserve` returns false only for ceiling pressure and allocates no lease/payload on refusal. `Reserve` maps ceiling pressure and counter overflow to `phase4-unexpected`. Reserve/release and monotonic peak updates are linearizable; lease disposal is idempotent and releases exactly once under races. `MarkManagedElementStorageAllocated(elementStorageBytes)` is a single, nonallocating post-allocation acknowledgement requiring `0 < elementStorageBytes <= CapacityBytes`; duplicate/after-dispose/oversized marks are invariant failures. Only exact GC-heap element storage may be marked: byte-array length, checked two-times char-array length, checked `Unsafe.SizeOf<T>() * array length`, or checked `string.Length * sizeof(char)` for a newly allocated measured-interval string. Headers, alignment, native/unmanaged memory, stack storage, synthetic reservations, unused conservative slack, interned/pre-existing strings, and any allocation completed before the measurement baseline are never marked. Successful marks update exact current and cumulative managed-element counters, and disposal decrements current exactly once without decrementing cumulative.
- [x] Freeze `RowReservationBytes=256` and `FieldReservationBytes=64` as the complete per-slot charges. Later row/field slot arrays charge `capacity * reservation`, contain no managed references, and do not add `Unsafe.SizeOf` again; cursor, decoded byte/char, and other backing buffers are charged separately at full capacity. Task 12 owns the concrete `RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false` and `Unsafe.SizeOf<T>() <= reservation` gates because those types do not exist in Task 3.
- [x] Write digest tests requiring a nonnull budget and exactly 32 input bytes, a `Digest` lease before one exact-sized allocation, post-allocation marking of exactly 32 element-storage bytes, exact 64-byte lowercase ASCII hex output into a caller buffer, no array/string copy, zeroing before lease release, idempotent disposal, and fixed `phase4-argument` failures for null budget, bad length/destination, or disposed use. The digest is explicitly single-owner and non-concurrent. Structural review must confirm a catch/finally releases the lease and clears any allocated array if an exceptional runtime allocation/copy path fails; Task 3 does not invent a production fault-injection allocator solely to simulate `OutOfMemoryException`.
- [x] Write failing ledger tests for a positive explicit ceiling and one active bounded stage scope at a time. Each scope charges checked `logicalBytes + 512L * entries`, tracks independent logical/entry counters, accepts `(positive,0)` and `(0,positive)`, rejects negative/`(0,0)`, and debits before filesystem mutation. The ledger's peak is monotonic and its scope operations are thread-safe. `ReleaseAllAfterProvenRemoval()` may run exactly once only after the caller has proven the entire work/blob scope absent; it atomically releases that scope's exact aggregate and permits the next scope. A stale/released scope can never debit or release a later scope (including ABA/concurrent races), and cleanup residue leaves the scope charged. Bad inputs/ceiling refusal map to `phase4-argument`; arithmetic overflow, a second active scope, stale/double release, or use after release maps to `phase4-unexpected`.
- [x] Extend `TransferV3FileStat` with unsigned owner UID at exact offsets Linux x64 `28`, Linux arm64 `24`, and macOS arm64 `16`. Add descriptor-only `fstatvfs`: Linux uses the 112-byte layout with unsigned-64 `f_frsize@8` and `f_bavail@32`; macOS arm64 uses the 64-byte layout with unsigned-64 `f_frsize@8` and unsigned-32 `f_bavail@24`. Return checked `f_bavail * f_frsize`; `f_bavail==0` validly returns zero. No path, `statvfs`, `f_bfree`, or saturating fallback is allowed. Preserve the shared low-level boundary's established raw types: zero `f_frsize` or truncated snapshot -> `InvalidDataException`, arithmetic overflow -> `OverflowException`, syscall failure -> `IOException`, unsupported ABI -> `PlatformNotSupportedException`. Only `TransferV3Phase4StagingParent` catches/maps them to fixed `phase4-posix`, so Phase 1-3 never depends on Phase 4.
- [x] Write staging-parent tests requiring a nonnull absolute supported-POSIX path, current `geteuid()` ownership, `(mode & 0700) == 0700`, and `(mode & 0022) == 0`; accept `0700`, `0750`, and `0755`. Null/empty/relative/non-POSIX-root paths, NUL, or `.`/`..` components fail with `phase4-argument`; unsupported ABI and an absolute missing/file/symlink/untrusted path fail with `phase4-posix`. Cover wrong UID through `ValidateOwnedStat`, each missing owner permission, group/other write, stable retained identity, duplicate/capacity/dispose stress with only valid-success or fixed-disposed outcomes, and independent duplicate lifetime. `ValidateRetainedStat` must compare exact opening/current identity, owner UID, and mode plus current trust predicates, so synthetic records cover identity/UID/mode drift and live `chmod` covers mode drift. Rename the opened directory and create a replacement at its former path; prove operations stay pinned to the retained original rather than claiming path-replacement detection. Retained drift fails closed with `phase4-posix`; duplicate/capacity use after disposal fails with `phase4-argument`, while the captured `Identity` value remains stable. Because the static native boundary has no deterministic pause seam, structural review must additionally prove duplicate, retained-stat/capacity query, and disposal all hold one shared lock through each native call and handle handoff; do not add a production fault seam only for this test.
- [x] Write options tests proving a null parent or either nonpositive ceiling fails with `phase4-argument` and leaves a nonnull parent caller-owned. A successful constructor preallocates one sealed consumed owner, and `Consume` returns that exact object with no post-transfer allocation. Structural review—not a production OOM fault seam—must prove validation completes first, the consumed owner is the constructor's final potentially throwing allocation, and ownership changes only when the options constructor returns successfully. `Consume` and `Dispose` race through one atomic exchange: one winner transfers or closes the exact parent, a second `Consume` or `Consume` after `Dispose` fails with `phase4-argument`, and disposal after transfer cannot close the consumed owner's parent. The consumed owner is single-owner/non-concurrent; its disposal is idempotent and every property use after disposal fails with `phase4-argument`.
- [x] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4ManagedBudgetTests|FullyQualifiedName~TransferV3Phase4DigestTests|FullyQualifiedName~TransferV3Phase4StagingLedgerTests|FullyQualifiedName~TransferV3Phase4StagingParentTests|FullyQualifiedName~TransferV3Phase4OptionsTests|FullyQualifiedName~TransferV3PosixOwnedDirectoryTests|FullyQualifiedName~TransferV3SnapshotDirectoryTests'`. Expected red.
- [x] Implement checked accounting and exact-sized sensitive arrays only after lease success. Direct `ArrayPool<T>.Shared` use is prohibited in Phase 4 because its returned capacity is unknowable before `Rent`; any future deterministic pool must reserve its known maximum first and fail if the actual capacity differs. Clear sensitive storage before lease release. `TransferV3Phase4Digest` is the only Task 3 backing allocation and records only the 32 bytes actually allocated.
- [x] Implement `OpenOwned` and `GetAvailableBytes` descriptor-relatively. Never retain or expose the input path. Serialize operations with disposal so a closed/recycled raw descriptor cannot be duplicated or queried. Replace the shared `dup(2)` helper with atomic `fcntl(F_DUPFD_CLOEXEC, 0)` using verified command `1030` on Linux x64/arm64 and `67` on macOS arm64; never use racy `dup` then `F_SETFD`. Linux calls `fcntl` directly. Because Apple arm64 puts variadic arguments on the stack, Darwin must call the pinned .NET 10.0.9 nonvariadic `libSystem.Native!SystemNative_Dup` bridge whose frozen runtime source performs the same atomic operation and retries `EINTR`; prohibit a fixed-three-argument `libc!fcntl` P/Invoke on Darwin. Assert `fcntl(F_GETFD)` includes `FD_CLOEXEC=1` on every original and duplicate descriptor, and source/native-host tests prove the Darwin bridge plus Linux direct path remain distinct.
- [x] Implement options as the preallocated non-copyable ownership envelope above. Both ceilings are positive required typed values with no defaults; Task 11 still owns the theoretical PostgreSQL text maximum and available-space comparisons.
- [x] Rerun focused tests. Expected green on the current supported platform; unsupported ABIs must fail closed, not skip silently.
- [x] Review checkpoint: Task 3 proves only the generic accounting/digest/ledger/POSIX/options foundation. It must not claim real parser, row/field, COPY, blob, or whole-lifecycle high-water acceptance before those types exist. Record Task 3 as foundation-complete after its focused tests and current-host ABI proof. Tasks 10, 12, 13, 15-16, and 18 define component checkpoints; Task 19 owns isolated measurement/formula validation; Task 20 owns PostgreSQL composite/RSS execution; and Task 21 owns the pure cross-ABI matrix and final composite verification.

### Task 4: Strict unopened target descriptor and diagnostics-disabled data source

**Status:** COMPLETE

**Objective:** Build one strict unopened target descriptor,
diagnostics-disabled nonpooling data source, exact provider/environment
normalization, and monotonic command-deadline foundation without opening.

**Dependency and ordering:** After Tasks 0-3; before Task 5 owns connection open
and validation and before every later PostgreSQL operation.

**Preconditions:** Exact `[10.0.3]` restore and Task 1 mapper exist; caller
settings satisfy the fixed allowlist, authentication, and time-zone contract.

**Interfaces consumed and produced:** Consumes raw target options, Task 1
failure mapping, Task 3 clock/budget foundations, and exact Npgsql identity.
Produces descriptor, diagnostics-disabled data source, command fence/deadline,
retry sink, and open-operation seams.

**Current evidence:** Complete. Exact seven-file implementation; explicit
restore; focused Debug/Release 196/196; Release build clean; pure regression 974
passed with 6 deliberate skips; security, deadline, concurrency, and fidelity
reviews clean.

**Expected result and acceptance:** Every checked normalization, descriptor,
deadline, diagnostics, allocation, restore, build, regression, and review step
below passes without opening a connection.

**Documentation impact:** Private developer/security contract in plan, design,
Task 4 report, progress, and handoff. PostgreSQL remains unavailable publicly.

**Recovery and risks:** Reject unexpected keys, versions, diagnostics, or time
behavior before construction/open. Dispose only descriptor-owned state after
lifecycle leases release; never retain or log the caller connection string.

**Files:**

- Modify: `backend/NzbWebDAV.csproj`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetContractTests.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptorTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDiagnosticsTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDeadlineTests.cs`

**Interfaces:**

```csharp
internal sealed partial class TransferV3PostgreSqlTargetDescriptor : IAsyncDisposable
{
    internal const string ApplicationName = "nzbdav-transfer-v3-phase4";
    internal const int ConnectionTimeoutSeconds = 5;
    internal const int CommandTimeoutSeconds = 300;
    internal const int CancellationTimeoutMilliseconds = 2000;

    internal static TransferV3PostgreSqlTargetDescriptor Create(
        string privateConnectionString);
    internal string TargetSchema { get; }
    internal string TimeZoneId { get; }
    public ValueTask DisposeAsync();
}

internal sealed class TransferV3PostgreSqlDeadline
{
    internal static readonly TimeSpan MaximumDuration =
        TimeSpan.FromMilliseconds(0xfffffffeL);
    internal static TransferV3PostgreSqlDeadline Start(
        TimeProvider timeProvider,
        TimeSpan duration);
    internal TimeSpan Remaining { get; }
    internal bool IsExpired { get; }
    internal TransferV3PostgreSqlCommandFence CreateCommandFence(
        int ordinaryMaximumSeconds);
}

internal sealed class TransferV3PostgreSqlCommandFence : IDisposable
{
    internal int CommandTimeoutSeconds { get; }
    internal CancellationToken CancellationToken { get; }
    internal bool IsExpired { get; }
    public void Dispose();
}
```

- [x] Change the direct Npgsql reference to the exact closed NuGet range `[10.0.3]`, run `dotnet restore backend.Tests/backend.Tests.csproj`, and update the existing target-contract test to require that literal, the restored direct dependency `Npgsql = 10.0.3`, the resolved `Npgsql/10.0.3` library, and exact restored content hash `7nb5YzXuvWWJxB0J8DiyL3we+X4FOctZrt0fIBnucOIaIevFEEwGQVZKtiu9olXdlNAK1eNgqSral6r/jlhI4w==`. A stale pre-edit assets file must fail. Keep the runtime gates below even after the restore pin is exact.
- [x] Write pre-open tests that prove no connection attempt occurs while rejecting: any true `Persist Security Info`, `Log Parameters`, `Include Error Detail`, or `Include Failed Batched Command`; missing/nonfixed application name; nonempty connection `Options`; multiple/comma-delimited hosts; missing explicit port; an embedded host-port form (including a suffix after bracketed IPv6); non-`Any` target-session attributes; load balancing; multiplexing; explicitly enabled pooling or enlistment; and descriptor/environment/process time-zone mismatch or alias. Parse through Npgsql first and compare its canonical keys: a provider-supported raw keyword alias is accepted only when it canonicalizes to an allowlisted key with the exact safe semantic value; disallowed canonical keys are rejected. Omitted pooling/enlistment values normalize to false; explicit false is accepted.
- [x] Freeze the Task 20 owned-loopback authentication/transport contract before build: require explicit nonblank `Database`, `Username`, and `Password`; exact `Client Encoding=UTF8`, `SSL Mode=Disable`, `SSL Negotiation=Postgres`, `GSS Encryption Mode=Disable`, `Require Auth=ScramSHA256`, and `Channel Binding=Disable`; reject integrated-security alternatives. The complete canonical connection-key allowlist is exactly `Host`, `Port`, `Database`, `Username`, `Password`, `Application Name`, `Search Path`, `Client Encoding`, `Timezone`, `SSL Mode`, `SSL Negotiation`, `GSS Encryption Mode`, `Require Auth`, `Channel Binding`, `Persist Security Info`, `Log Parameters`, `Include Error Detail`, `Include Failed Batched Command`, `Pooling`, `Enlist`, `Load Balance Hosts`, `Multiplexing`, `Target Session Attributes`, `Timeout`, `Command Timeout`, `Cancellation Timeout`, and `Options`; reject every other key, including passfile/certificate/key/root-certificate settings. The descriptor accepts one nonblank DNS name, IPv4 literal, bare/bracketed IPv6 literal, rooted Unix-socket path, or abstract Unix-socket name plus one separately explicit port in `1..65535`; it performs no DNS or loopback attestation. Task 20 alone generates and proves the actual loopback route. Phase 5 must perform a new security review before broadening this private helper-only transport contract.
- [x] Reject every nonempty value of the exact Npgsql 10.0.3 `PG*` variables below before data-source construction: `PGUSER`, `PGPASSWORD`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT`, `PGCLIENTENCODING`, `PGTZ`, `PGOPTIONS`, `PGTARGETSESSIONATTRS`, `PGSSLNEGOTIATION`, `PGGSSENCMODE`, `PGREQUIREAUTH`, and `PGAPPNAME`. `HOME`/`APPDATA` are not rejected as Npgsql variables: ordinary Phase-4-executing children must bind them to runner-owned empty mode-0700 directories, while one Task 20 canary case uses a runner-owned nonempty private home. Task 4 pure tests prove the normalized explicit password/username/startup/auth/disabled-SSL values and exact-source fallback conditions; the Task 20 canary case opens successfully with invalid `.pgpass`/`.postgresql` defaults and proves them inert.
- [x] Define target schema without a tautology: any one exact safe operator-selected schema accepted by `PostgreSqlEnvironmentContract.GetRequiredTargetSchema` is valid, but the original `Search Path` value must ordinally equal that returned value, so whitespace, aliases, quoting, or multiple schemas are rejected.
- [x] Test timeout rules independently: omitted values normalize to 5 seconds / 300 seconds / 2000 milliseconds; exact explicit values are accepted; any caller alternative, zero, negative, skipped-wait, or infinite value is rejected.
- [x] Test the normalized private builder has one nonblank host plus one separately explicit port, nonblank database/username/password, exact fixed auth/transport/encoding values, `Pooling=false`, `Enlist=false`, `Load Balance Hosts=false`, `Multiplexing=false`, exact lowercase `Target Session Attributes=any`, fixed application name, exact schema/time zone, and an explicit empty `Options` assignment even when the input omitted it. Npgsql 10.0.3's `Build()` clone canonicalizes that empty value back to `null`, so the built private data source must expose no startup options (`null` or empty) and every nonempty caller value remains rejected. Assert individual safe properties only; never print or compare the full secret-bearing string.
- [x] Write deadline tests with no sleep/system clock for null provider; zero, negative, infinite, `TimeSpan.MaxValue`, and exact `MaximumDuration + 1 tick`; invalid ordinary maximum; exact initial/decreasing/regressed/expired/post-expiry-regressed time; a concurrent stale-sample race; subsecond and one-tick-over-second integer-ceiling round-up; and ordinary-maximum clamping. Exact `MaximumDuration` (`0xfffffffe` milliseconds) is accepted; invalid duration/maximum maps to `phase4-argument` before provider access, while timestamp/frequency/elapsed/timer failures map to `phase4-unexpected`.
- [x] `CreateCommandFence` samples remaining time once and atomically pairs the positive timeout with one owned cancellation source. Positive remaining uses the original `TimeProvider` and that remaining interval; exact zero returns timeout `1` plus a synchronously cancelled source without creating a timer. The caller must refuse provider work when `IsExpired`, dispose every fence, and treat the token as cooperative cancellation: after every provider race it rechecks the authoritative deadline before accepting the result. Test progressively smaller fences, exact due times, timer disposal, and no raw provider exception.
- [x] After the project edit, run the explicit restore above. Then run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTargetDescriptorTests|FullyQualifiedName~TransferV3PostgreSqlDiagnosticsTests|FullyQualifiedName~TransferV3PostgreSqlDeadlineTests|FullyQualifiedName~TransferV3PostgreSqlTargetContractTests'`. Expected red.
- [x] Build exactly one private `NpgsqlDataSource` with `Name=ApplicationName`, `NullLoggerFactory.Instance`, `EnableParameterLogging(false)`, and one `ConfigureTracing` callback that sets command, batch, and COPY filters to false and `EnablePhysicalOpenTracing(false)`. Do not call those tracing methods directly on `NpgsqlDataSourceBuilder`, use a global data source, create an `NpgsqlConnection`, or open a connection in Task 4.
- [x] Store neither the caller string nor a public normalized string. The descriptor owns only the data source plus safe schema/time-zone facts, is sequentially idempotent on disposal, and is single-owner/non-concurrent: it may be disposed only after every later open attempt/session has closed. Task 5 adds the only open path without exposing the private normalized value.
- [x] Gate the resolved assembly version at exact `10.0.3.0` and `AssemblyInformationalVersionAttribute` at exact `10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc`. Add a pure metadata-validator seam with table-driven wrong/null/absent/malformed/prerelease/different-build tests and prove failure occurs before data-source construction/open. Unsafe caller/environment settings fail with fixed `phase4-argument`; version/provider failures use fixed `phase4-unexpected`; neither echoes input.
- [x] Implement the deadline from exactly one start-time `TimeProvider.GetTimestamp()` plus elapsed monotonic time. `Remaining` is linearizable, atomically retains the minimum observed value, and never increases even if a faulty provider regresses. Validate the exact timer bound in ticks rather than relying on the runtime's truncating millisecond cast. Preserve an already-sanitized Phase 4 exception and map every other provider/timer failure at its boundary.
- [x] Add a Roslyn-scoped source/API audit for the exact Npgsql calls and record the independent `Npgsql.Sql` EventSource and provider-owned metrics limitations. Scope the no-open/no-connection rule to Task 4's `Create` path so Task 5's partial open implementation does not invalidate it. Do not add an `EventListener` negative-control that the provider cannot pass; Tasks 19-20 instead enforce the no-injected-listener/EventPipe helper boundary while retaining Activity/logger and the narrow allowlisted metrics negative control.
- [x] Rerun focused tests. Expected green.
- [x] Review checkpoint: use the local Npgsql 10.0.3 XML and exact `d3768398c17877b3a916c3c4d87e8e11698991fc` source contract to verify every controllable tracing method, the restore/runtime version gates, connection defaults/environment fallbacks, host-port postprocessing, and the documented provider-owned EventSource boundary.

### Task 5: Exact server settings, five-way time-zone equality, and target identity

**Status:** COMPLETE

**Objective:** Validate exact PostgreSQL 16.14 settings and five-way time-zone
equality, capture immutable target identity, and provide one-owner bounded
open/session/close/quarantine lifecycle.

**Dependency and ordering:** After Tasks 0-4; before history/bootstrap readers,
transaction state operations, admission, and reconciliation.

**Preconditions:** Task 4 descriptor/deadline is sealed; exact non-superuser
settings and provider seams govern validation; pure work uses injected seams.

**Interfaces consumed and produced:** Consumes target descriptor, data source,
deadline/fence, native time-zone descriptor, and provider operations. Produces
server settings, target identity, open attempt, session, environment contract,
close result, and lifecycle leases.

**Current evidence:** Complete. Exact 13-file implementation; focused
Debug/Release 395 passed with 12 deliberate live skips; Release build clean;
Task 4 regression 204/204; pure regression 1,329 passed with 6 deliberate skips;
contract, lifecycle, and fidelity reviews clean.

**Expected result and acceptance:** Every checked settings, identity,
time-zone, ownership, deadline, cleanup, allocation, build, regression, and
review step below passes; live privilege negatives remain Task 20.

**Documentation impact:** Private architecture/security contract in plan,
design, Task 5 report, progress, and handoff. No public provider claim.

**Recovery and risks:** If ownership, close, deadline, or late completion is
unproven, retain the lifecycle lease, quarantine, require helper exit, and
suppress unsafe descriptor disposal.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetIdentity.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlOpenAttempt.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlProviderOperations.cs`
- Modify: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs`
- Modify: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs`
- Modify: `backend/Database/PostgreSqlEnvironmentContract.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlServerContractTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetIdentityTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlSessionTests.cs`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDeadlineTests.cs`
- Modify test: `backend.Tests/Database/PostgreSqlEnvironmentContractTests.cs`

**Interfaces:**

```csharp
internal static class TransferV3PostgreSqlServerContract
{
    internal static Task<TransferV3PostgreSqlTargetIdentity> ValidateAndCaptureAsync(
        NpgsqlConnection connection,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);

    internal static TransferV3PostgreSqlTargetIdentity ValidateProjection(
        in TransferV3PostgreSqlServerSettingsProjection settings,
        in TransferV3PostgreSqlIdentityProjection identity,
        string expectedTimeZoneId);
}

internal sealed record TransferV3PostgreSqlTargetIdentity(
    string SystemIdentifier,
    DateTimeOffset PostmasterStartTimeUtc,
    string DatabaseName,
    uint DatabaseOid,
    string SchemaName,
    uint SchemaOid,
    string RoleName,
    uint RoleOid,
    string ServerVersion,
    int ServerVersionNumber,
    bool IsInRecovery,
    bool DefaultTransactionReadOnly,
    bool TransactionReadOnly,
    string? ServerAddress,
    int? ServerPort);

internal interface ITransferV3PostgreSqlProviderOperations
{
    NpgsqlConnection CreateConnection(NpgsqlDataSource dataSource);
    Task OpenAsync(NpgsqlConnection connection, CancellationToken cancellationToken);
    Task<TransferV3PostgreSqlTargetIdentity> ValidateServerAsync(
        NpgsqlConnection connection,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    Task<string> ValidateEnvironmentAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    ValueTask DisposeConnectionAsync(NpgsqlConnection connection);
    ValueTask DisposeDataSourceAsync(NpgsqlDataSource dataSource);
}

internal sealed partial class TransferV3PostgreSqlTargetDescriptor
{
    internal TransferV3PostgreSqlOpenAttempt CreateOpenAttempt();
    internal int ActiveLifecycleLeaseCount { get; }
    internal static TransferV3PostgreSqlTargetDescriptor CreateForTesting(
        string privateConnectionString,
        ITransferV3PostgreSqlProviderOperations operations);
}

internal sealed class TransferV3PostgreSqlOpenAttempt
{
    internal ValueTask OpenAsync(CancellationToken cancellationToken);
    internal ValueTask<TransferV3PostgreSqlSession> ValidateFirstAsync(
        string sourceTimeZoneId,
        CancellationToken cancellationToken);
    internal ValueTask<TransferV3PostgreSqlSession> ValidateMatchingAsync(
        string sourceTimeZoneId,
        TransferV3PostgreSqlTargetIdentity expected,
        CancellationToken cancellationToken);
    internal ValueTask<TransferV3PostgreSqlSession> ValidateMatchingWithinAsync(
        string sourceTimeZoneId,
        TransferV3PostgreSqlTargetIdentity expected,
        TransferV3PostgreSqlDeadline deadline);
    internal ValueTask<TransferV3Phase4CleanupResult> CloseWithinAsync(
        TransferV3PostgreSqlDeadline deadline);
    internal void AbandonForHelperExit();
}

internal sealed class TransferV3PostgreSqlSession
{
    internal NpgsqlConnection BorrowConnection();
    internal TransferV3PostgreSqlTargetIdentity Identity { get; }
    internal int OrdinaryCommandTimeoutSeconds { get; }
    internal bool IsQuarantined { get; }
    internal void Quarantine();
    internal ValueTask<TransferV3Phase4CleanupResult> CloseWithinAsync(
        TransferV3PostgreSqlDeadline deadline);
}
```

- [x] Use ordinary readonly-struct `TransferV3PostgreSqlServerSettingsProjection` and `TransferV3PostgreSqlIdentityProjection` seams rather than records/dictionaries with value-revealing generated formatting. Write typed parser tests for every approved setting and every null/unsafe/case/whitespace alternative. Require exact: `log_min_messages=panic`; `log_min_error_statement=panic`; `log_error_verbosity=terse`; statement/duration/sample logging off; parameter lengths zero; debug prints off; preload-library lists empty; `log_destination=stderr`; `logging_collector=off`; `fsync=on`; `full_page_writes=on`; and `synchronous_commit=on` (not aliases such as `local`).
- [x] Also require `client_encoding=UTF8`; exactly `ISO, MDY`, `ISO, DMY`, or `ISO, YMD` `DateStyle`; `session_replication_role=origin`; no temporary schema; `default_transaction_read_only=off`; `transaction_read_only=off`; and exact session time zone. The current role must be non-superuser and have immediately usable inherited `pg_read_all_settings`; missing visibility refuses rather than dropping the privileged preload checks. PostgreSQL 16.14 defines `pg_control_system()` without a custom ACL and does not revoke it in `system_functions.sql`, so the stock cluster retains the documented default `PUBLIC EXECUTE` on functions; do not incorrectly require `pg_monitor`. The owned harness must nevertheless assert `has_function_privilege(current_user, 'pg_catalog.pg_control_system()', 'EXECUTE')` before the completion run and include an isolated negative target that revokes that exact function from `PUBLIC` and proves refusal before payload.
- [x] Make the fixed, noninterpolated, zero-parameter settings/identity query the first SQL command after open and the in-process four-way time-zone check. It must contain no manifest digest, state value, row, key, UUID, path, or COPY payload and no `SET`, DML, COPY, or caller-controlled SQL. Only after that safe query succeeds may the parameterized environment query run. Reject settings; never issue `SET` to repair them.
- [x] Write identity tests covering every field, including `long.MaxValue + 1` and `ulong.MaxValue` system identifiers, signs/leading zero/non-digits/overflow/zero refusal, exact finite UTC-zero-offset postmaster incarnation, nonzero OIDs/nonblank names, and TCP IPv4/IPv6 versus both-null Unix-domain server address/port values. Reject XOR-null endpoints, scoped IPv6, and ports outside `1..65535`; store a TCP address only through canonical `IPAddress.ToString()`.
- [x] Write ownership tests proving the descriptor allocates the unpublished attempt owner shell before acquiring a provider connection; under the descriptor lifecycle gate it then attaches the returned connection through one allocation-free, nonthrowing direct assignment and increments the prevalidated lifecycle lease count as the publication linearization point. Connection-state validation occurs inside the published attempt before the open provider call, so invalid returned connections remain caller-deadline closeable. The descriptor thereby creates/registers one attempt and transfers one still-unopened connection plus one descriptor-owned lifecycle lease to it before `OpenAsync` starts; allocation/open success/fault/cancellation never creates an ownerless connection or registered ownerless lease. Validation atomically transfers the same connection and lease exactly once from attempt to one session, and validation failure leaves both attempt-owned for caller-deadline close. Attempt/session bounded close owns connection close-and-disposal only; a transaction-owning component must rollback/dispose its own transaction first under the same deadline.
- [x] Remove the unprovable bare cleanup token: bounded close derives its own non-caller operation fence from the supplied deadline. Freeze bounded-close results: proven connection `DisposeAsync` returns four `None` codes and releases the lifecycle lease exactly once; a catchable provider invocation, `ValueTask` status/conversion/await, or operation-fence construction/sampling fault before proven provider success returns `ConnectionCloseFailed` first and three `None`; an already-expired deadline starts no provider call and returns `CleanupDeadlineExceeded` first and three `None`; a dispose provider call abandoned at expiry returns `DeadlineAbandonedProviderTask` first, `CleanupDeadlineExceeded` second, and two `None`. Proven provider success is published to the owning attempt/session before operation-fence disposal or any other re-entrant cleanup. A later operation-fence-disposal fault is contained and cannot downgrade proven success, retain/reacquire the lease, or permit another provider call. Every non-success retains the lease, causes the coordinator to mark `RequiresHelperExit`, and suppresses descriptor disposal. A retry is allowed only after a catchable fault, only while the exact same deadline object remains unexpired, and never creates a new lease or deadline. Task 18 must record the first failure before retry and may never erase helper exit even if retry later proves disposal.
- [x] Add an opaque disposable `TransferV3PostgreSqlOperationFence` created only by `TransferV3PostgreSqlDeadline.CreateOperationFence()`. It carries the exact remaining-time token from the deadline's original `TimeProvider`, uses no caller token, is synchronously expired without a timer at zero, and is idempotently disposable. It exposes no constructor or deadline-reset API to call sites.
- [x] Deterministically cover a late open completion after the coordinator has abandoned its await. The first `AbandonForHelperExit` that owns a nontransferred attempt atomically wins and permanently quarantines it; a repeated call on that abandoned attempt is a no-op. Abandonment prevents validation/consumption/close provider reuse, keeps the descriptor lifecycle lease outstanding, forbids descriptor disposal, and sets the coordinator's immediate-helper-exit requirement. A call after successful close or attempt-to-session transfer throws fixed `phase4-unexpected`. No late continuation may publish a usable session or start another cleanup deadline.
- [x] Freeze the remaining linearizable transition table. Exactly the first `OpenAsync` from created state and the first validation from opened state may start; any repeated/concurrent open or validation, or either call after close/transfer, throws fixed `phase4-unexpected` without a provider call. Initial attempt close is legal only from quiescent `Created`, `Opened`, `OpenFailed`, or `ValidationFailed`; initial session close only from `Owned` or `Quarantined`; close during `Opening`, `Validating`, or close preparation/running is fixed unexpected with no provider call. Retry deadline sampling reserves a distinct checking state, runs outside the re-entrant lifecycle lock, and must reacquire/revalidate the same owner state and deadline before close preparation. Fence creation occurs in a distinct preparation state; only an owner-controlled transition may atomically move preparation to running and invoke the provider while holding that owner gate, so abandonment during preparation starts no provider call. A close that wins is the sole `NpgsqlConnection.DisposeAsync` provider call, and session borrowing is quarantined from that instant. Repeating close after proven success returns the same four-`None` success without a provider call; after a catchable close fault, only a sequential retry under the same still-live deadline is legal; expired-before-start and close abandonment are terminal fixed replays. If provider completion is already complete when the race is sampled, it wins an exact tie and is inspected without another blocking wait. Once deadline/abandonment wins, attach only a nonlogging late-fault observer, never await again, never mutate terminal state, and never release the lease. If validation transfer or proven close wins, later attempt abandonment throws as above. Session quarantine is idempotent. No transition duplicates or releases the lifecycle lease twice.
- [x] Prove `BorrowConnection` refuses after quarantine and every Phase 4 call site discards its borrow before quarantine. State the honest limit: quarantine cannot revoke a previously retained raw reference or stop an already-running provider call; abandonment marks helper exit.
- [x] Add one instance-scoped internal provider-operations adapter for synchronous unopened-connection creation, open, the two validation commands, connection `DisposeAsync`, and data-source disposal. Production delegates only to exact Npgsql/environment/server calls; tests inject `TaskCompletionSource` operations while retaining a real unopened `NpgsqlConnection`. No process-global hook is permitted. Source-audit that Phase 4 never calls `OpenConnection`/`OpenConnectionAsync` and that only the descriptor invokes `CreateConnection`.
- [x] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlServerContractTests|FullyQualifiedName~TransferV3PostgreSqlTargetIdentityTests|FullyQualifiedName~TransferV3PostgreSqlSessionTests|FullyQualifiedName~TransferV3PostgreSqlDeadlineTests|FullyQualifiedName~PostgreSqlEnvironmentContractTests'`. Expected red for the new pure/structural cases; completion/live PostgreSQL facts must be excluded or skipped before opening a connection.
- [x] Implement one bounded settings/identity query and return the captured typed identity. Add and use `PostgreSqlEnvironmentContract.ValidateAsync(connection, commandTimeoutSeconds, cancellationToken)` plus the corresponding expected-version seam overload. Preserve both existing signatures by delegating through the connection's effective positive `CommandTimeout`; zero/infinite refuses. Validate a supplied timeout before connection state/provider access and assign it before execution.
- [x] Capture `pg_control_system().system_identifier` as canonical unsigned decimal text without signed narrowing. Because PostgreSQL 16.14 exposes the underlying `uint64` through `bigint`, the SQL must add exact numeric `18446744073709551616` before text conversion when the exposed value is negative; pure validation accepts only canonical `[1-9][0-9]{0,19}` no greater than `18446744073709551615`. Also capture `pg_postmaster_start_time()`, names/OIDs, version, recovery/read-only state, role privilege facts, and `inet_server_addr/port`. Failure to read any required field refuses; there is no endpoint-only fallback and no `CommandBehavior.SingleRow` cardinality suppression.
- [x] Make `CreateOpenAttempt` the descriptor's only connection factory. Under the descriptor lifecycle gate it allocates the unpublished owner shell first, then creates exactly one connection synchronously from the private data source, directly attaches it, and registers the lease/attempt before returning. `OpenAsync` refuses a non-closed connection before calling the provider, and only that attempt may call `OpenAsync`; never use `OpenConnection`/`OpenConnectionAsync`. The attempt first validates manifest source time zone = `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE` = `TimeZoneInfo.Local.Id` = descriptor `Timezone` without SQL, then runs the parameter-free settings/identity query whose `SHOW TimeZone` supplies the fifth equality member, then runs the environment query, compares its schema with the captured identity/descriptor, and atomically transfers the connection into a first validated session with pinned identity. Every later attempt repeats that exact order and complete contract before exact identity comparison and before receiving sensitive data.
- [x] Ordinary validation assigns the explicit 300-second command timeout before the parameter-free settings/identity command and then the environment command. Both query methods explicitly dispose the reader and then command; once a provider/read/cancellation primary has been captured, later disposal faults are best-effort suppressed and cannot replace it, while the first disposal fault after otherwise successful work is sanitized as a PostgreSQL command failure even when it is an `OperationCanceledException` carrying a now-canceled caller token. Cleanup-only cancellation must not be misclassified as caller cancellation. `await using` unwinding that can replace the primary is insufficient. `ValidateMatchingWithinAsync` is reconciliation-only: before each command in that order it creates a fresh command fence capped by 300 seconds, refuses an expired fence, executes with that fence's token, disposes it, and rechecks the authoritative deadline only after success before accepting the result. After a provider primary it sanitizes/captures that primary immediately, best-effort disposes the fence, performs no secondary authoritative-deadline sample, and rethrows the same primary. Passing one token or one timeout for the whole multi-command preflight is insufficient.
- [x] If open itself fails, preserve the still-owned attempt and return only the mapped PostgreSQL-open failure; caller-deadline close owns any catchable cleanup. If any post-open validation fails, do not hide-close or start a private timeout inside validation: sanitize immediately as `phase4-postgresql-command`, preserve that primary, and leave the attempt owned by the caller so Task 18's first-catch deadline covers its close. If open/validation is abandoned, observe/drop any late raw result or fault, publish no session, complete the abandoned operation only with fixed `phase4-unexpected`, and do not close or dispose the attempt/descriptor; require immediate helper exit.
- [x] Give the descriptor an atomic `Ready -> Disposing -> Disposed|DisposeFailed` gate shared by attempt creation and disposal. A retained-lease disposal attempt returns fixed `phase4-cleanup`, leaves the descriptor ready, and calls the data source zero times; create or concurrent dispose during disposal is fixed unexpected. Successful repeated disposal is a no-op. Cache/replay the same sanitized `phase4-cleanup` data-source-dispose fault without a second provider call so Npgsql idempotence cannot mask it.
- [x] Rerun focused tests. Expected green.
- [x] Review checkpoint: confirm error mapping occurs immediately around every existing environment/catalog call so its detailed raw message cannot escape, and confirm no validation failure loses the only owner of an opened connection.

### Task 6: One exact migration-history and fresh-target definition

**Status:** COMPLETE

**Objective:** Define one exact PostgreSQL migration-history prefix/head and
one bounded canonical disposable fresh-bootstrap snapshot shared by migration,
admission, and final verification.

**Dependency and ordering:** After Tasks 0-5; before Task 7 state access and
Task 8 admission rely on exact history/catalog/bootstrap state.

**Preconditions:** Target mapping/head catalog, transaction readiness, exact
migration IDs/product versions, and embedded source bootstrap contract exist;
live integration remains Task 20-only.

**Interfaces consumed and produced:** Consumes target contract, session,
migration catalog, source bootstrap contract, and canonical codec. Produces
native migration contract, fresh-bootstrap contract/snapshot, transaction-bound
readers, and bootstrap-secret ownership.

**Current evidence:** Complete. Exact 11-file implementation; focused
Debug/Release 174/174; provider/SQLite/refusal Release 215/215; codec 51/51;
pure regression 1,329 passed with 6 deliberate skips; builds and final reviews
clean.

**Expected result and acceptance:** Every checked history, catalog, bootstrap,
codec, disposal, security, build, regression, and review step below passes; live
cases remain deferred.

**Documentation impact:** Developer migration/bootstrap contract in plan,
design, Task 6 report, progress, and handoff. SQLite migrations remain intact.

**Recovery and risks:** Fail closed on history, catalog, cardinality, or secret
buffer uncertainty; dispose/zero snapshots and keep public PostgreSQL disabled.

**Files:**

- Create: `backend/Database/PostgreSqlNativeMigrationContract.cs`
- Create: `backend/Database/PostgreSqlFreshBootstrapContract.cs`
- Modify: `backend/Database/PostgreSqlNativeMigrator.cs`
- Modify: `backend/Database/PostgreSqlPhysicalCatalogContract.cs`
- Modify: `backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.cs`
- Test: `backend.Tests/Database/PostgreSqlNativeMigrationContractTests.cs`
- Test: `backend.Tests/Database/PostgreSqlFreshBootstrapContractTests.cs`
- Modify test: `backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs`
- Modify test: `backend.Tests/Database/PostgreSqlCatalogInventoryTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs`
- Modify test data: `backend.Tests/TestData/postgresql-native-schema-contract.json`

**Interfaces:**

```csharp
internal sealed record PostgreSqlMigrationHistoryEntry(
    string MigrationId,
    string ProductVersion);

internal static class PostgreSqlNativeMigrationContract
{
    internal static IReadOnlyList<PostgreSqlMigrationHistoryEntry> Head { get; }
    internal static void ValidatePrefix(
        IReadOnlyList<PostgreSqlMigrationHistoryEntry> capturedRows);
    internal static void ValidateHead(
        IReadOnlyList<PostgreSqlMigrationHistoryEntry> capturedRows);
    internal static Task<IReadOnlyList<PostgreSqlMigrationHistoryEntry>> CaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    internal static Task ValidatePrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    internal static Task ValidateHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal readonly record struct PostgreSqlApplicationRelationCount(
    string RelationName,
    long Count);

internal sealed class PostgreSqlFreshBootstrapSnapshot : IDisposable
{
    internal PostgreSqlFreshBootstrapSnapshot(
        ReadOnlyMemory<byte> canonicalDavItemsUtf8,
        ReadOnlyMemory<byte> canonicalConfigItemsUtf8,
        IReadOnlyList<PostgreSqlApplicationRelationCount> otherRelationCounts);
    internal ReadOnlyMemory<byte> CanonicalDavItemsUtf8 { get; }
    internal ReadOnlyMemory<byte> CanonicalConfigItemsUtf8 { get; }
    internal IReadOnlyList<PostgreSqlApplicationRelationCount> OtherRelationCounts { get; }
    public void Dispose();
}

internal static class PostgreSqlFreshBootstrapContract
{
    internal static void Validate(PostgreSqlFreshBootstrapSnapshot snapshot);
    internal static Task<PostgreSqlFreshBootstrapSnapshot> CaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    internal static Task ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}
```

- [x] Write pure failing snapshot tests that require the exact locked history rows `20260712000000_PostgreSqlNativeBaseline|10.0.9` and `20260712000100_PostgreSqlOperationalTriggers|10.0.9`; reject missing, extra, reordered, duplicate, or wrong product version.
- [x] Write pure failing `PostgreSqlFreshBootstrapSnapshot` tests for all canonical fields of all five bootstrap `DavItems`, including required nulls; exactly two distinct generated 32-lower-hex keys named `api.key`/`api.strm-key`; exactly one byte-canonical `fresh` reserved row; no extra ConfigItems/DavItems; and zero rows in every other application table including `HealthCheckStats`.
- [x] Add a transaction overload to `PostgreSqlPhysicalCatalogContract.ValidateAsync` so catalog validation participates in the already-held admission/final transaction and exact locks.
- [x] Run only the new pure fixture filter: `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~PostgreSqlNativeMigrationContractTests|FullyQualifiedName~PostgreSqlFreshBootstrapContractTests'`. Expected red on exact-value/product-version cases. Do not select `PostgreSqlNativeMigrationIntegrationTests`, `PostgreSqlCatalogInventoryTests`, or inherit an external PostgreSQL connection at this task.
- [x] Implement `CaptureAsync` as the only live reader: explicit-timeout commands produce bounded canonical snapshots, then the pure `Validate`/`ValidateHead` comparers own every exact-value/order/cardinality decision. Factor the current count-only `ValidateFreshBootstrapRowsAsync` into that shared contract; make the native migrator call it. Do not keep two definitions.
- [x] Freeze both snapshot byte grammars as compact UTF-8 JSON arrays with exact row order, exact PostgreSQL-target column/property order, exact scalar spellings, no BOM/whitespace/unnecessary escapes/trailing bytes, and exact valid byte lengths. The embedded reserved-state JSON string uses only the mandatory JSON quote escapes (`\"`); every other reviewed string is unescaped. Validate lengths before parsing or decoding. Capture at most the expected cardinality plus one sentinel row; check `octet_length`, lazily convert only values within their byte cap, and return an empty `bytea` sentinel for oversized text so conversion itself cannot materialize an unbounded value. Use a finite six-fractional-digit `CreatedAt` representation and separately require the AD era so PostgreSQL `-infinity` or a BC timestamp cannot alias the reviewed value. All failures are fixed and value-free.
- [x] Serialize both expected and captured config documents through one fixed-capacity, non-growing `IBufferWriter<byte>` whose capacity covers worst-case JSON escaping of all bounded fields and whose complete backing array is zeroed on disposal. A stream-mode writer that can abandon a resized secret-bearing array is insufficient.
- [x] Make captured snapshots own private byte-array copies and zero the API-key buffer on `Dispose`; `ValidateAsync` must dispose in `finally`, while callers of `CaptureAsync` explicitly own disposal. Validate the two secrets byte-by-byte as exact 32-byte lowercase ASCII hexadecimal values rather than with a regex anchor.
- [x] Derive the exact ordered 26-relation zero-count list from `TransferV3PostgreSqlTargetContract.LoadEmbedded()` table order after excluding only `ConfigItems`/`DavItems` and appending its derived `HealthCheckStats`; do not add another production literal list.
- [x] Make the native migrator run preflight and final validation in separate explicit `REPEATABLE READ`, `READ ONLY` transactions, with an explicit positive command timeout on every command, then commit before EF migration work continues. The shared transaction-taking contracts accept an already-held read/write Phase 4 transaction and therefore must not require read-only themselves. Every shared contract must prove that the supplied Npgsql transaction is still active: pinned Npgsql 10.0.3 retains `Connection` after commit/rollback, so probe its public `IsolationLevel` getter, which invokes the provider's readiness check, after exact connection ownership.
- [x] Probe whether the provider history relation exists under the already-owned advisory lock but before opening the native preflight's `REPEATABLE READ` transaction. When it exists, make the `SHARE` table lock the first non-control statement in that transaction, then revalidate the relation's exact shape before reading history; final validation after EF migration likewise locks before its first history/catalog read. This ordering prevents a lock wait from leaving the transaction on a stale snapshot. An advisory-lock acquisition failure is acknowledgement-ambiguous: preallocate cleanup ownership, close/evict/dispose the possibly locked session on failure, and preserve the acquire primary plus distinct first-wins rollback/dispose/acquire/unlock cleanup evidence. Reader/command cleanup may not replace its execute/read primary.
- [x] Use the shared exact-prefix migration contract for 0/1/2-row native preflight and the exact-head contract after migration; remove the private ID-only migration list/read. The direct operational-trigger EF migration must also refuse a baseline row whose `ProductVersion` is not exactly `10.0.9`.
- [x] Read root expectations from the embedded reviewed source bootstrap contract and application-relation expectations from frozen contracts; use parameterized values and exact UTF-8 comparisons.
- [x] Rerun the new pure fixture filter. Expected green. Compile the modified integration/catalog tests but defer every live-PostgreSQL execution to Task 20's exclusively owned runner.
- [x] Review checkpoint: prove the refactor does not change SQLite migrations or the public PostgreSQL refusal.

### Task 7: Transaction-bound import-state CAS and locking read

**Status:** COMPLETE

**Objective:** Add exact transaction-bound PostgreSQL import-state CAS and
bounded `FOR SHARE` read while preserving the cross-provider context-owned API.

**Dependency and ordering:** After Tasks 0-6; before Task 8 composes locks with
admission and Task 9 reconciles state.

**Preconditions:** Canonical codec/transition graph and bootstrap contract are
frozen; caller supplies an open connection and exact active owning transaction;
live cases remain Task 20-only.

**Interfaces consumed and produced:** Consumes canonical state codec, target
session/transaction readiness, and Task 1 failure mapping. Produces the
transaction-bound CAS and bounded locking read while retaining the existing
cross-provider API unchanged.

**Current evidence:** Complete. Exact three-file implementation; focused pure
11/11; Release Task 7 plus SQLite/context regression 53/53; non-live PostgreSQL
2/2; pure Debug/Release 1,338/1,338; builds and final reviews clean.

**Expected result and acceptance:** Every checked validation, SQL, cardinality,
zeroing, lifecycle, regression, build, and review step below passes; live cases
compile but do not execute until Task 20.

**Documentation impact:** Private database contract in plan, design, Task 7
report, progress, and handoff. No user-facing provider change.

**Recovery and risks:** Methods never own caller resource lifecycle. Preserve
the execution/read primary, dispose reader then command best-effort, zero state
bytes, and leave transaction recovery to its owner.

**Files:**

- Modify: `backend/Database/Transfer/TransferV3ImportStateStore.cs`
- Create test: `backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3ImportStateStorePostgreSqlTests.cs`

**Interfaces:**

```csharp
internal static Task<int> TryTransitionInPostgreSqlTransactionAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    TransferV3ImportState expected,
    TransferV3ImportState next,
    int commandTimeoutSeconds,
    CancellationToken cancellationToken);

internal static Task<TransferV3ImportState> ReadForShareInPostgreSqlTransactionAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    int commandTimeoutSeconds,
    CancellationToken cancellationToken);
```

- [x] Add pure failing reflection/Roslyn command and argument contract tests for the exact two static signatures, every legal/illegal transition, validation order, wrong connection/transaction ownership, non-open/completed transactions, exact positive command timeout, exact parameter types, exact byte predicates, and no connection opening. These tests must not read a PostgreSQL environment variable or create/open a provider connection; provider-only ownership/completion behavior is proved structurally here and by compiled live cases below. Validate the positive timeout and all nonnull arguments first. An illegal transition then returns `0` before cancellation, connection/transaction property access, serialization, or command creation. For a legal transition require an already-open connection, exact `ReferenceEquals(transaction.Connection, connection)` ownership, and only then the pinned Npgsql 10.0.3 `transaction.IsolationLevel` readiness probe.
- [x] Add live cases to the existing PostgreSQL test class for same-transaction visibility/rollback, exact 0/1/2 affected-row results, closed/foreign/committed-undisposed transaction refusal with caller ownership retained, missing/duplicate/non-text/byte-different reserved key/value, noncanonical JSON, mismatched digest, and a held-row timeout. Do not run them in this task; Task 20's composite runner is their only live executor.
- [x] Freeze the read as one parameterized, transaction-bound `SELECT ... LIMIT 2 FOR SHARE OF` statement. Its searched/nested `CASE` expressions must prove native `text` storage and bound database-encoding length before `convert_to`, then independently bound the converted UTF-8 length by `TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes`; plain reorderable `AND` terms are insufficient. Require the exact byte key, read without `SingleRow`/`SingleResult`, perform a second `ReadAsync` cardinality check, accept exactly one bounded byte array, canonical-decode through `TransferV3ImportStateCodec`, and expose only one fixed value-free malformed-row failure.
- [x] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3ImportStateStorePostgreSqlContractTests'`. Expected red for new overloads; no PostgreSQL environment variable is accepted or read.
- [x] Implement the static PostgreSQL-only operations inside `TransferV3ImportStateStore`; reuse its legal transition graph and exact byte predicate. Harden both existing and transaction-bound PostgreSQL CAS use with native-`text` checks and searched `CASE` length guards before `convert_to`, because PostgreSQL may reorder ordinary Boolean terms. Bind only exact `Text`, `Bytea`, and `Integer` parameters, plus the supplied transaction and timeout. Never open, begin, commit, roll back, close, or dispose the supplied connection/transaction. Explicitly dispose the reader then command through the primary-preserving helper, zero serialized/captured state bytes after cleanup/decode, and let cleanup never replace the provider/read primary.
- [x] Keep the existing cross-provider context-owned API and its closed/outside-transaction contract unchanged.
- [x] Rerun the pure contract test. Expected green. Compile the modified live class and defer its execution to Task 20.
- [x] Review checkpoint: `rg -n 'database\.import-state|PostgreSqlCasSql' backend/Database/Transfer backend/Database/Transfer/Phase4` must show no ad hoc UPDATE outside the import-state store.

### Task 8: Exact advisory/table locks and atomic fresh-target admission

**Status:** IN PROGRESS

**Objective:** Acquire the shared advisory fence and exact 29 schema-qualified
`EXCLUSIVE MODE NOWAIT` locks, revalidate the target in one `READ COMMITTED`
transaction, and fully charged transition `fresh -> importing(A)` without
commit.

**Dependency and ordering:** Tasks 0-7 are complete. Task 9 may not start until
this task has a green exact gate, clean Release builds and formatting, two fresh
read-only reviews, and the resulting evidence recorded in this plan and
`HANDOFF.md`. Local reports and snapshots are optional corroboration.

**Preconditions:** The same live managed budget owns the digest; caller supplies
the validated session and exact active read-committed transaction; Task 20 owns
all live PostgreSQL execution. The two recorded source/test defects must be
repaired before completion.

**Interfaces consumed and produced:** Consumes session, transaction, digest,
managed budget, target/environment/catalog/history/bootstrap validators, codec,
and state store. Produces the lock set and target-only admission validator plus
additive digest-owner, span-codec, and borrowed-buffer CAS seams listed below.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs`
- Modify: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs`
- Modify: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs`
- Modify: `backend/Database/PostgreSqlEnvironmentContract.cs`
- Modify: `backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs`
- Modify: `backend/Database/Transfer/TransferV3ImportStateCodec.cs`
- Modify: `backend/Database/Transfer/TransferV3ImportStateStore.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSetTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlServerContractTests.cs`
- Modify test: `backend.Tests/Database/PostgreSqlEnvironmentContractTests.cs`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs`

**Interfaces:**

```csharp
internal static class TransferV3PostgreSqlAdmissionLockSet
{
    internal const long AdvisoryNamespaceSeed = 0x4E5A425456335034;
    internal static IReadOnlyList<string> RelationNames { get; }
    internal static string BuildRelationLockSql(string targetSchema);
    internal static Task<bool> TryAcquireAdvisoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
    internal static Task AcquireRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal static class TransferV3PostgreSqlAdmissionValidator
{
    internal static Task ValidateFreshAndMarkImportingAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3Phase4Digest manifestDigest,
        TransferV3Phase4ManagedBudget managedBudget,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}
```

Additional interfaces produced by the accounting repair:

```csharp
internal sealed class TransferV3Phase4Digest
{
    internal void ValidateOwner(TransferV3Phase4ManagedBudget managedBudget);
}

internal static class TransferV3ImportStateCodec
{
    internal const int FreshCanonicalUtf8Length = 35;
    internal const int ImportingCanonicalUtf8Length = 123;
    internal static void WriteFreshCanonical(Span<byte> destination);
    internal static Span<byte> InitializeImportingCanonical(Span<byte> destination);
    internal static bool IsCanonicalFreshToImportingTransition(
        ReadOnlySpan<byte> expectedCanonicalUtf8,
        ReadOnlySpan<byte> nextCanonicalUtf8);
}

internal static Task<int>
    TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        byte[] expectedCanonicalUtf8,
        byte[] nextCanonicalUtf8,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
```

- [x] Require exactly the 27 source tables in contract order, then `HealthCheckStats`, then `__EFMigrationsHistory_PostgreSql`, with no duplicates and immutable iteration.
- [x] Build one identifier-safe, configured-target-schema-qualified `LOCK TABLE ... IN EXCLUSIVE MODE NOWAIT` statement in that exact order, with no `ONLY`.
- [x] Freeze the nested, explicitly `pg_catalog`-qualified advisory hash and exact `Text`/`Bigint` parameter types.
- [x] Require an open, ready, exact-owner `READ COMMITTED` transaction before either lock command.
- [x] Record the exact Task 20 owned-PostgreSQL lock/contention/drift/two-importer cases without creating live Task 8 tests.
- [x] Expose descriptor-frozen `TimeZoneId` and add transaction-bound server/environment validation overloads while preserving existing overloads.
- [x] Implement the locked identity, environment, catalog, history, and fresh-bootstrap validation order with explicit finite timeouts.
- [x] Keep actual sealed-source bootstrap proof in Tasks 11 and 18 rather than adding a Boolean or source stage to target admission.
- [x] Validate that the digest belongs to the same live managed budget before borrowing the connection or doing provider work.
- [x] Reserve exact 35-byte and 123-byte `Copy` leases before either array allocation, acknowledge exact element storage, build canonical bytes through span seams, erase both arrays, null both references, and release leases in reverse order.
- [x] Add the specialized borrowed-buffer PostgreSQL CAS seam; preserve Task 7's state-object API and require exactly one changed row without committing in the validator.
- [ ] Repair `TransferV3PostgreSqlAdmissionValidator.ValidateFreshAndMarkImportingAsync` so the raw CAS invocation has the exact reviewed expression shape required by both structural tests. Current symptom: `CanonicalFreshToImportingCasIsFullyChargedAndOccursOnlyAfterEveryPreflight` and `EveryOperationalFailureIsSanitizedAtThePostgreSqlCommandBoundary` fail because the receiver/member access is split by trivia.
- [ ] Swap expected/actual arguments in the two canonical-length assertions at `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs:131` and `:132`; current clean test build fails with `xUnit2000` twice.
- [ ] Rerun the exact two-fixture pure filter. Expected: 26 passed, 0 failed, 0 skipped. Leave real integration tests for Task 20.
- [ ] Run the affected-contract filter for digest, codec, raw store, server, session, and environment. Current result: 390 passed and 12 deliberate no-connection skips; retain that result after repair.
- [ ] Run both Release builds with `-warnaserror`, the pure Transfer-v3 regression, scoped `dotnet format --verify-no-changes`, and `git diff --check`. Expected: all green; pure regression 1,372 passed, 0 failed, 0 skipped.
- [ ] Obtain two fresh independent read-only reviews, record their findings and
  final gate results in this task and `HANDOFF.md`, then update this status.
  Verify no sensitive value is sent before descriptor/session preflight
  completes. Local `.superpowers/sdd/**` artifacts may be updated when present,
  but are supplemental and never a completion dependency.

**Exact remaining verification commands, in order:**

```bash
dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter '(FullyQualifiedName~TransferV3PostgreSqlAdmissionLockSetTests|FullyQualifiedName~TransferV3PostgreSqlAdmissionTests)&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'
```

Expected after the two minimal repairs: 26 passed, 0 failed, 0 skipped.

```bash
dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter '(FullyQualifiedName~TransferV3Phase4DigestTests|FullyQualifiedName~TransferV3ImportStateCodecTests|FullyQualifiedName~TransferV3ImportStateStorePostgreSqlContractTests|FullyQualifiedName~TransferV3PostgreSqlServerContractTests|FullyQualifiedName~TransferV3PostgreSqlSessionTests|FullyQualifiedName~PostgreSqlEnvironmentContractTests)&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'
```

Expected: 390 passed, 12 deliberate no-connection skips, 0 failed.

```bash
dotnet build backend/NzbWebDAV.csproj --configuration Release --no-restore -warnaserror
dotnet build backend.Tests/backend.Tests.csproj --configuration Release --no-restore --no-incremental -warnaserror
dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~TransferV3&FullyQualifiedName!~TransferV3ImportStateStorePostgreSqlTests&Category!=TransferV3Phase4PostgreSqlCompletion' --logger 'console;verbosity=minimal'
dotnet format backend.Tests/backend.Tests.csproj --no-restore --verify-no-changes --include backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs backend/Database/Transfer/TransferV3ImportStateCodec.cs backend/Database/Transfer/TransferV3ImportStateStore.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs backend/Database/PostgreSqlEnvironmentContract.cs backend.Tests/Database/Transfer/Phase4/TransferV3Phase4DigestTests.cs backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3ImportStateStorePostgreSqlContractTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSetTests.cs backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionTests.cs
git diff --check
```

Expected: both builds, the formatter, and whitespace gate exit 0; pure
Transfer-v3 reports 1,372 passed, 0 failed, 0 skipped. None of these commands
may use a live PostgreSQL resource.

**Current evidence, 2026-07-16:** Backend Release build passed with zero
warnings/errors. Affected contracts passed 390 with 12 deliberate no-connection
skips. Exact Task 8 failed 2 of 26. Pure Transfer-v3 failed the same 2 of
1,372. Full no-PostgreSQL backend failed only those same 2 of 2,831, with
2,745 passed and 84 deliberate skips. Clean test build failed the two
`xUnit2000` diagnostics. Scoped formatter verification exited 2 on the same
diagnostics. No live database or container was used.

**Expected result and acceptance:** The exact remaining commands above all exit
0; 26/26 focused and 1,372/1,372 pure Transfer-v3 tests pass; both reviews have
no P0/P1; their findings and final results are recorded in this task and
`HANDOFF.md` before status becomes `COMPLETE`.

**Documentation impact:** This private API is not shipped or user-facing.
Update the design, this plan, and canonical handoff. Local task brief/progress
files may be updated when present but are supplemental. Do not advertise
PostgreSQL availability in README or setup documentation.

**Recovery and risk:** Preserve every pre-existing untracked and modified file.
The repair is source/test-only and must not alter SQL, ordering, allocation,
ownership, lifecycle, or public runtime registration. If a verification command
surprises, stop and update the handoff before changing code.

### Task 9: One-deadline MVCC-safe commit reconciliation fence

**Status:** NOT STARTED

**Objective:** Reconcile acknowledgement-ambiguous admission/final commits
under one nonresetting ten-second monotonic deadline and publish only
`Committed`, `NotCommitted`, or `Unknown` after fenced state and release proof.

**Dependency and ordering:** Begins only after Task 8 is sealed. Consumes Task 5
session/identity/deadline ownership, Task 7 locking read, and Task 8 advisory
fence.

**Preconditions:** Task 8 is `COMPLETE` in this plan and `HANDOFF.md`, all Task 8
gates are green, reconciler/operations symbols remain absent, and provider work
uses bounded command fences and preallocated publication state.

**Interfaces consumed and produced:** Consumes descriptor/session identity,
advisory lock, transaction-bound state read, failure model, and monotonic clock.
Produces reconciliation operations, preallocated sink/result, and the exact
three-way disposition.

**Current evidence:** Not started. Planned production/test symbols are absent;
no implementation or test result is claimed.

**Expected result and acceptance:** Every exact RED/GREEN, deadline,
lost-acknowledgement, cleanup, build, regression, and review step below passes
before Task 10 begins.

**Documentation impact:** Plan, design, future Task 9 report, progress, and
handoff only. Behavior remains private until verified.

**Recovery and risks:** `Unknown` authorizes no failed CAS or destructive
cleanup, preserves the stage, and requires helper exit when transaction or
release completion cannot be proven.

**Files:**

- Modify: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlCommitReconciler.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlReconciliationOperations.cs`
- Modify test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlDeadlineTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlCommitReconcilerTests.cs`

**Interfaces:**

```csharp
internal enum TransferV3PostgreSqlCommitPoint { Admission, DatabaseVerified }
internal enum TransferV3PostgreSqlCommitOutcome { Committed, NotCommitted, Unknown }

internal sealed class TransferV3PostgreSqlCommitReconciliationResult
{
    internal TransferV3PostgreSqlCommitOutcome Outcome { get; }
    internal bool RequiresHelperExit { get; }
    internal TransferV3Phase4SecondaryCode SecondaryCode { get; }
    internal void SetProven(
        TransferV3PostgreSqlCommitOutcome outcome,
        bool requiresHelperExit,
        TransferV3Phase4SecondaryCode secondaryCode);
}

internal interface ITransferV3PostgreSqlCommitProofSink
{
    // Implementations are preallocated, nonthrowing, and allocation-free.
    void OnProven(TransferV3PostgreSqlCommitOutcome outcome);
}

internal interface ITransferV3PostgreSqlReconciliationOperations
{
    ValueTask<TransferV3Phase4CleanupResult> QuarantineAndCloseAsync(
        TransferV3PostgreSqlSession failedSession,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask<TransferV3PostgreSqlSession> OpenMatchingAsync(
        TransferV3PostgreSqlTargetDescriptor target,
        TransferV3PostgreSqlTargetIdentity expectedIdentity,
        string sourceTimeZoneId,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask<NpgsqlTransaction> BeginReadCommittedAsync(
        TransferV3PostgreSqlSession session,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask<bool> TryAcquireFenceAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask ValidateIdentityAfterFenceAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlTargetIdentity expectedIdentity,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask<TransferV3ImportState> ReadForShareAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlDeadline deadline);
    ValueTask<TransferV3PostgreSqlReconciliationReleaseResult> RollbackReleaseAndCloseAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlDeadline deadline);
}

internal readonly record struct TransferV3PostgreSqlReconciliationReleaseResult(
    bool TransactionEndedFenceReleasedAndConnectionClosed,
    TransferV3Phase4CleanupResult Cleanup);

internal sealed class TransferV3PostgreSqlCommitReconciler
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    internal TransferV3PostgreSqlCommitReconciler(
        TimeProvider timeProvider,
        ITransferV3PostgreSqlReconciliationOperations operations);
    internal Task ReconcileAsync(
        TransferV3PostgreSqlCommitPoint point,
        TransferV3PostgreSqlTargetDescriptor target,
        TransferV3PostgreSqlSession failedSession,
        TransferV3PostgreSqlTargetIdentity expectedIdentity,
        string sourceTimeZoneId,
        TransferV3Phase4Digest manifestDigest,
        TransferV3PostgreSqlCommitReconciliationResult preallocatedResult,
        ITransferV3PostgreSqlCommitProofSink preallocatedProofSink);
}
```

- [ ] Write deterministic `TimeProvider` and provider-operation-adapter tests proving one monotonic deadline starts as the first reconciliation action and is never reset. Cover failed-session quarantine/close, DNS/open/auth, all preflights, transaction start, polling, post-lock identity, state read, rollback/release, and connection close.
- [ ] Write failing state-machine tests: admission accepts only `importing(A)` as committed and `fresh` as not committed; final accepts only `database-verified(A)` as committed and `importing(A)` as not committed; every other/missing/unreadable/mismatched state is unknown. `SetProven` is preallocated fixed-field, allocation-free, nonthrowing, idempotent for the same outcome, and refuses conflicting second writes only through a pre-recorded fixed code.
- [ ] Prove no state read occurs before the same transaction advisory lock is acquired, the transaction is `READ COMMITTED`, the locking read is a later statement/snapshot, identity is rechecked after the fence, and observed state is only provisional until an explicit successful `TransactionEndedFenceReleasedAndConnectionClosed` proof with empty cleanup codes is returned inside the deadline. The proof sink and public result remain untouched while state is merely observed.
- [ ] Test stalled reconciliation-owned provider calls by racing them against the remaining deadline. An abandoned call records unknown plus `RequiresHelperExit=true`; it is never awaited indefinitely or left in a long-lived runtime. Include never-completing transaction rollback/release and reconciliation-session close fakes through `ITransferV3PostgreSqlReconciliationOperations`. Descriptor/data-source disposal belongs to the coordinator, so its never-completing fake is exercised only through Task 18's fault seam.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlDeadlineTests|FullyQualifiedName~TransferV3PostgreSqlCommitReconcilerTests'`. Expected red.
- [ ] Implement every provider operation with a fresh single-observation command fence and non-caller-cancellable internal token. Set each reconciliation command's timeout to that fence's positive rounded-up remaining duration capped by the ordinary limit; refuse work if the fence is already expired, dispose it, and recheck the authoritative deadline before accepting a raced provider result. `OpenMatchingAsync` owns the attempt before starting open and must call Task 5's reconciliation-aware validation overload so every post-open preflight command receives its own reduced timeout rather than the ordinary 300-second value. Quarantine the failed session; never reuse it.
- [ ] Poll `pg_try_advisory_xact_lock` every 50 ms, then recapture identity and call the import-state store's `FOR SHARE` read in a fresh statement.
- [ ] After an allowed state is observed, first rollback/dispose the reconciliation transaction and close the session under the same deadline. Only an explicit successful release proof with no cleanup code may call the preallocated sink with provisional `Committed` or `NotCommitted`; call the sink first and only then mutate/complete the preallocated result. A rollback/release/close failure, missing proof, cleanup code, timeout, or abandonment must ignore the provisional state, call only `Unknown` (whose stage-backed sink performs `PreserveCommitOutcomeUnknown()` first), and require helper exit when applicable. Once the ten-second fence expires, do not await cleanup, disposal, logging, or any provider task.
- [ ] Rerun focused tests. Expected green.
- [ ] Review checkpoint: an unknown result must expose no API that authorizes failed CAS or destructive blob cleanup.

### Task 10: Async parser observer with single-frame ownership and safe abort

**Status:** NOT STARTED

**Objective:** Add the separately accounted async parser/observer path with one
in-flight callback, borrowed-frame lifetime through await, one bounded sanitized
abort, and byte-for-byte preservation of the synchronous path.

**Dependency and ordering:** After Task 9. Consumes Task 1 failure/cleanup codes
and Task 3 budget; feeds Tasks 11-14.

**Preconditions:** Synchronous parser behavior remains frozen; budget and
cleanup boundary exist; async fixture/symbols remain absent and begin RED.

**Interfaces consumed and produced:** Consumes parser frames, managed budget,
failure mapper, cancellation, and abort surface. Produces async observer,
borrowed frame lifetime contract, and async parser overload without changing
the synchronous overload.

**Current evidence:** Not started. Async observer/overload and focused tests are
absent; no result is claimed.

**Expected result and acceptance:** Every exact parser, abort, cancellation,
allocation, synchronous-regression, build, and review step below passes before
Task 11 begins.

**Documentation impact:** Plan, design, future Task 10 report, progress, and
handoff. No user-facing parser change.

**Recovery and risks:** Preserve the sanitized primary and stack; abort exactly
once; append only bounded abort failure; clear/release charged frames; stop if
the synchronous overload changes.

**Files:**

- Modify: `backend/Database/Transfer/TransferV3Frames.cs`
- Modify: `backend/Database/Transfer/TransferV3JsonlParser.cs`
- Modify: `backend/Database/Transfer/TransferV3Utf8LineReader.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3JsonlAsyncParserTests.cs`

**Interfaces:**

```csharp
internal enum TransferV3ObserverAbortReason
{
    ParseFailure,
    ObserverFailure,
    UnexpectedEof,
    CallerCancellation,
}

internal interface ITransferV3AsyncFrameObserver
{
    ValueTask ObserveAsync(TransferV3Frame frame, CancellationToken cancellationToken);
    ValueTask CommitBatchAsync(
        TransferV3BatchEndFrame batchEnd,
        CancellationToken cancellationToken);
    ValueTask CompleteTableAsync(
        TransferV3TableEndFrame tableEnd,
        CancellationToken cancellationToken);
    ValueTask<TransferV3Phase4CleanupResult> AbortAsync(
        TransferV3ObserverAbortReason reason,
        CancellationToken cleanupToken);
}

internal sealed class TransferV3SyncObserverAsyncAdapter(
    ITransferV3FrameObserver inner) : ITransferV3AsyncFrameObserver;
```

Add this overload without changing the existing one:

```csharp
internal static Task<TransferV3BufferMetrics> ParseAsync(
    Stream source,
    TransferV3Limits limits,
    ITransferV3AsyncFrameObserver observer,
    TransferV3Phase4ManagedBudget managedBudget,
    CancellationToken cancellationToken = default);
```

- [ ] Write failing ordering tests for header/batch/row/chunk/batch-end/table-end dispatch, exactly one in-flight callback, and no callback after failure.
- [ ] Hold an async callback incomplete and assert the frame's borrowed payload remains intact until the await completes, then is zeroed immediately afterward. A reference-type observer can retain the frame object, so do not claim that retention is mechanically impossible: prove a deliberately retaining test observer sees only cleared payload after return, and restrict production use to the reviewed internal observer/adapter implementations.
- [ ] Add parse, observer, EOF, and caller-cancellation failures at every operation. Assert `AbortAsync` runs exactly once with only a reason enum and a non-caller cleanup token bounded to five seconds.
- [ ] For the synchronous-to-async adapter, translate the abort reason to one fixed sanitized exception created by the adapter; never pass the async primary or a raw exception into the legacy `Abort(Exception)` method.
- [ ] Make abort throw a canary exception; assert the already-sanitized primary object and stack are preserved through `ExceptionDispatchInfo`, no `AggregateException` is created, and only `observer-abort-failed` is appended.
- [ ] Add accounting tests for the line-reader backing capacity, 16 KiB read buffer, returned line, decoded payload, canonical parser copy, and dispatch lease. Reserve exact capacity before allocation, mark only exact managed array element-storage bytes after successful allocation, and clear before release. The fixed 8 MiB reserve covers only explicitly documented opaque JSON/runtime objects.
- [ ] Define the fixed parser probe scenario and its maximum-live checkpoint while the async observer is deliberately held incomplete. Unit-test deterministic current/cumulative payload metrics here; Task 19 supplies the isolated helper process and Task 21 runs the final native-matrix opaque-allocation gate. Task 10 must not present an in-process shared-testhost GC sample as final proof.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3JsonlAsyncParserTests'`. Expected red.
- [ ] Implement the overload as a separate code path sharing frame parsing/state helpers. Keep the current synchronous overload's raw abort/AggregateException behavior byte-for-byte unchanged for Phase 1-3.
- [ ] Add an accounting-aware `TransferV3Utf8LineReader` constructor used only by the async overload; reserve capacities before allocating and clear before release.
- [ ] Rerun the focused test plus `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3JsonlParserTests|FullyQualifiedName~TransferV3JsonlAsyncParserTests'`. Expected green.
- [ ] Review checkpoint: compare the synchronous overload diff and prove no behavior change outside the new overload/helpers.

### Task 11: Canonical source receipts and read-only representability/staging preflight

**Status:** NOT STARTED

**Objective:** Compute charged canonical table receipts and validate all 27
sealed streams for PostgreSQL representability, exact source bootstrap, text
ceiling, and peak staging bound without opening PostgreSQL.

**Dependency and ordering:** After Task 10. Consumes its async parser plus the
managed budget, staging parent, sealed source, and target contract.

**Preconditions:** Valid sealed snapshot; one live budget; trusted staging
parent; positive text ceiling no greater than 1,073,741,819; positive required
staging ceiling.

**Interfaces consumed and produced:** Consumes sealed snapshot/parser, target
contract, managed budget/staging parent, and source bootstrap contract. Produces canonical
logical digest, table receipts, typed preflight result, and representability
validator exactly as declared below.

**Current evidence:** Not started. Primary implementation and focused-test paths
are absent; no completion evidence exists.

**Expected result and acceptance:** The exact receipt, representability,
bootstrap, staging-bound, allocation, build, and review gates below pass before
any target open or mutation.

**Documentation impact:** Record exact no-default ceilings,
`SourceBootstrapValidated`, ownership, and real gate results in plan, report,
progress, and handoff; Task 21 owns contract README changes.

**Recovery and risks:** Every refusal leaves canonical `fresh`, creates no
staging entry, and clears/releases charged temporary or returned receipt
storage.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3CanonicalLogicalDigest.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlRepresentabilityPreflight.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3CanonicalLogicalDigestTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlRepresentabilityPreflightTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4StagingBoundTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3CanonicalLogicalDigest : IDisposable
{
    internal TransferV3CanonicalLogicalDigest(TransferV3Phase4ManagedBudget managedBudget);
    internal void BeginRow(ReadOnlySpan<byte> cursorAscii, int fieldCount);
    internal void BeginField(long encodedLength);
    internal void AppendFieldChunk(ReadOnlySpan<byte> encodedBytes);
    internal void EndField();
    internal void EndRow();
    internal TransferV3Phase4Digest CompleteAndTransfer();
    internal long Rows { get; }
    public void Dispose();
}

internal sealed class TransferV3TableLogicalReceipt : IDisposable
{
    internal int TableOrdinal { get; }
    internal long Rows { get; }
    internal TransferV3Phase4Digest Sha256 { get; }
    public void Dispose();
}

internal sealed record TransferV3Phase4PreflightResult(
    long EffectiveMaxTextPayloadBytes,
    long MaximumBatchSpoolBytes,
    long BlobStageLogicalBytes,
    long RequiredPeakStagingBytes,
    bool SourceBootstrapValidated);

internal static class TransferV3PostgreSqlRepresentabilityPreflight
{
    internal const long TheoreticalMaximumTextPayloadBytes = 1_073_741_819;
    internal static Task<TransferV3Phase4PreflightResult> ValidateAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3PostgreSqlTargetContract targetContract,
        TransferV3Phase4StagingParent stagingParent,
        TransferV3Phase4ManagedBudget managedBudget,
        long maxPostgreSqlTextPayloadBytes,
        long maxPhase4StagingBytes,
        CancellationToken cancellationToken);
}
```

- [ ] Add golden vectors for the exact logical preimage: four-byte big-endian cursor ASCII length, cursor bytes, four-byte field count, then eight-byte encoded-field length plus exact marker/payload bytes for each field. Cover streamed text chunks split at every boundary.
- [ ] Write source-only tests that parse all 27 sealed table streams sequentially, retain only fixed counters/current-field state/charged digests, accept both verified inline `TransferV3RowFrame` and chunked row forms, and never open PostgreSQL, create a work/spool/blob entry, or read physical blob content.
- [ ] Cover field count/nullability/kind/fixed-width/integer/enum/timestamp/strict UTF-8/rune/NUL rules and text payload lengths. Test the theoretical boundary arithmetically without allocating a 1 GiB string.
- [ ] Require a positive explicit operator text ceiling no greater than `1_073_741_819`; there is no default and a larger supplied value is rejected rather than silently trusted.
- [ ] Compute exact append-only spool bytes for every batch with checked arithmetic. Compute blob bound as manifest blob bytes + maximum canonical receipt + `512 * (stage root + blobs root + at most two shard directories and one file per blob + receipt)`; shared shard prefixes do not reduce the conservative bound.
- [ ] Compute spool peak as maximum exact batch-spool bytes + `512 * (work root + spool)`, and required peak as `max(spool peak, blob peak)` because the work root must be durably gone before blob construction.
- [ ] Test zero/negative/missing staging ceiling, arithmetic overflow, logical bound above ceiling, available bytes below bound, and `statvfs` diagnostic failure. All deterministic failures leave the target unopened/canonical `fresh` and create no entry.
- [ ] Validate exact source roots and two valid distinct source API keys during this pass, but do not retain/log their values.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3CanonicalLogicalDigestTests|FullyQualifiedName~TransferV3PostgreSqlRepresentabilityPreflightTests|FullyQualifiedName~TransferV3Phase4StagingBoundTests'`. Expected red.
- [ ] Implement bounded observers using the async parser, the already-created single managed budget, and the target contract. Preserve the distinct inline-versus-chunked canonical transfer-frame representation so the exact spool bound covers either form. Repeat the checks later during batch staging as defense in depth.
- [ ] Require all returned table/preflight receipt storage, including every 32-byte digest, to own an explicit lease from the same budget and to clear/release it on failure/disposal; no unleased `byte[]` digest return is allowed.
- [ ] Rerun focused tests. Expected green.
- [ ] Review checkpoint: instrument the target/session factory in tests and assert its open count remains zero for every preflight refusal.

### Task 12: Bounded in-memory batches, whole-batch promotion, and append-only spool

**Status:** NOT STARTED

**Objective:** Implement exact-capacity in-memory batches, whole-batch
promotion, append-only spool/replay, and identity-proven work-root lifecycle
under one managed and staging budget.

**Dependency and ordering:** After Task 11. Consumes canonical digest/preflight
bounds; supplies batch/spool inputs to Task 13 and proven work-root removal to
Task 15.

**Preconditions:** Same budget/ledger and trusted parent; valid preflight bounds;
one active work scope; exact source frame representation.

**Interfaces consumed and produced:** Consumes preflight receipts/bounds,
parser frames, POSIX directory, and staging ledger. Produces decoded row sink,
spool row representation, replay receipt, batch spool, in-memory batch, and
owned work directory.

**Current evidence:** Not started. Work directory, batch, spool, and focused
tests are absent.

**Expected result and acceptance:** Exact capacity, promotion, format, replay,
tamper, cleanup, budget, probe, build, and review gates below pass before Task
13.

**Documentation impact:** Record spool format, slot-layout gates, probe metrics,
and outcomes in plan, report, progress, and handoff.

**Recovery and risks:** Remove only identity-proven mode-0600 spools under the
nonce-owned mode-0700 root, sync changed parents, and release scope only after
proven absence. Uncertain residue stays charged.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4WorkDirectory.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4InMemoryBatch.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4BatchSpool.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4InMemoryBatchTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4BatchSpoolTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4WorkDirectoryTests.cs`

**Spool format:**

- Header: ASCII `NZBDSP01`, big-endian table ordinal, big-endian batch ordinal.
- Batch-start record: tag, null/value marker, optional cursor length and exact ASCII cursor.
- Inline-row record: distinct tag, checked canonical-frame length, and the exact canonical `TransferV3RowFrame` bytes streamed through a bounded writer; it is never normalized into chunked records.
- Chunked-row-start, field-chunk, and chunked-row-end records: distinct tags plus every field, ordinal, boundary, count, length, digest, and payload needed to reproduce the exact original canonical frame sequence.
- No footer and no stored batch-end authority. Inline-versus-chunked representation and every original frame boundary are preserved so replay can independently reconstruct the canonical batch digest byte-for-byte.

**Interfaces:**

```csharp
internal enum TransferV3SpoolRowRepresentation { Inline, Chunked }

internal interface ITransferV3DecodedRowSink
{
    ValueTask BeginRowAsync(
        TransferV3SpoolRowRepresentation representation,
        ReadOnlyMemory<byte> cursorAscii,
        int fields,
        CancellationToken ct);
    ValueTask WriteFieldChunkAsync(int field, ReadOnlyMemory<byte> bytes, CancellationToken ct);
    ValueTask EndRowAsync(CancellationToken ct);
}

internal sealed class TransferV3SpoolReplayReceipt : IDisposable
{
    internal int Rows { get; }
    internal long DecodedBytes { get; }
    internal ReadOnlyMemory<byte> LastCursorAscii { get; }
    internal TransferV3Phase4Digest CanonicalBatchSha256 { get; }
    public void Dispose();
}

internal sealed class TransferV3Phase4BatchSpool : IAsyncDisposable
{
    internal ValueTask AppendFrameAsync(TransferV3Frame frame, CancellationToken ct);
    internal ValueTask<TransferV3SpoolReplayReceipt> ReplayAsync(
        ITransferV3DecodedRowSink sink,
        TransferV3CanonicalLogicalDigest tableLogicalDigest,
        TransferV3BatchEndFrame expected,
        CancellationToken ct);
    internal ValueTask<TransferV3Phase4SecondaryCode> RemoveAsync(
        CancellationToken cleanupToken);
    public ValueTask DisposeAsync();
}
```

- [ ] Write failing in-memory tests for exact-capacity cursor/field storage, row/field reservations, strict field validation, sensitive clearing, and promotion before any allocation would cross 32 MiB.
- [ ] Introduce the concrete row-slot and field-slot value types here. On every supported runtime/ABI assert `RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false`, `Unsafe.SizeOf<RowSlot>() <= 256`, and `Unsafe.SizeOf<FieldSlot>() <= 64`. Charge slot arrays as checked `capacity * 256/64`, mark only checked `capacity * Unsafe.SizeOf<T>()` as allocated managed element storage, and charge every referenced cursor/decoded backing buffer separately. A breach blocks acceptance; it is not papered over by the 8-MiB opaque reserve.
- [ ] Prove promotion is whole-batch: write already retained rows to spool, clear/release every retained sensitive buffer, then append later chunks directly with only fixed counters and one bounded I/O buffer.
- [ ] Write exact-format/golden tests for every spool record, truncated/extra/unknown records, overflow, cursor ASCII, chunk order, and no footer authority. Include an inline-only, chunked-only, and mixed inline/chunked batch; replay must reproduce the exact verified canonical batch digest for all three.
- [ ] Mutate spool content, cursor, field length, row digest, final length, mode, link count, identity, and pathname binding independently. Replay must refuse before completing COPY.
- [ ] During replay recompute row count, decoded bytes, last cursor, and canonical batch digest from the exact original inline/chunked frame representation; compare all with the verified batch-end and staging receipts. Feed decoded exact row preimages into the one table-scoped `TransferV3CanonicalLogicalDigest`; do not try to combine per-batch SHA-256 values.
- [ ] Charge the replay receipt object reservation, last-cursor backing capacity, and canonical 32-byte digest to the same managed budget before allocation; disposal clears/releases all three. No replay path returns an unleased array.
- [ ] Because the canonical row preimage places each encoded field length before its bytes while the append-only spool learns length only at field end, replay each row in two bounded passes: first collect checked field lengths into one charged fixed column-count array, then seek back to that row start and stream lengths/bytes to COPY and the table hasher. Revalidate spool identity/mode/link-count/length around both passes.
- [ ] Test cancellation and injected `ENOSPC`/close/sync/unlink failures. Cleanup removes only identity-proven mode-0600 spools under the nonce-owned mode-0700 work root and syncs changed parents; uncertain entries produce bounded residue codes.
- [ ] Test exact work-root grammar `.nzbdav-transfer-v3-import-work-<32-lower-hex>`, no-follow/no-replace creation, no table/key/UUID/path/value in names, exact-empty audit, durable removal, and parent sync before blob stage.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4InMemoryBatchTests|FullyQualifiedName~TransferV3Phase4BatchSpoolTests|FullyQualifiedName~TransferV3Phase4WorkDirectoryTests'`. Expected red.
- [ ] The work directory begins and owns the ledger's sole active scope. Implement descriptor-relative creation/open/stat/sync/removal with scope debits before every write/entry creation. Only after exact-empty proof, durable work-root removal, and parent sync may it call `ReleaseAllAfterProvenRemoval`; residue leaves the scope charged and prevents blob-stage scope admission.
- [ ] Rerun focused tests. Expected green.
- [ ] Add the fixed current-batch/promotion probe checkpoint and deterministic allocation metrics for Task 19's isolated runner. Review checkpoint: search for `List<`, `Dictionary<`, `HashSet<`, `ArrayPool`, and `string` in current-batch code and every shared helper reached by it; direct `ArrayPool<T>.Shared` is forbidden and every capacity/growth must be accounted or replaced with a bounded representation.

### Task 13: Exact binary/text COPY codecs and cancellation semantics

**Status:** NOT STARTED

**Objective:** Implement explicit-type binary COPY for bounded batches and
strict escaped text COPY for spools with exact timeout, completion,
cancellation, and redaction behavior.

**Dependency and ordering:** After Task 12. Consumes batch/spool and Task 11
canonical digest; supplies COPY sinks to Task 14.

**Preconditions:** Caller-owned open connection/transaction, embedded table
contract, whole batch or spool, positive timeout, and charged codec buffers.

**Interfaces consumed and produced:** Consumes target mapping, batch/spool,
connection/transaction, budget, and failure model. Produces the exact binary
and text COPY sink interfaces and strict text encoder declared below.

**Current evidence:** Not started. COPY encoder/sinks and focused tests are
absent; live completion remains Task 20.

**Expected result and acceptance:** Exact encoding, parameter type, timeout,
cancel/dispose, redaction, allocation, pure gate, build, and review steps below
pass before Task 14.

**Documentation impact:** Record exact Npgsql 10.0.3 assumptions, probe
checkpoint, and real outcomes in plan/report/progress/handoff; Task 20 records
live evidence.

**Recovery and risks:** Text failure/cancellation calls `CancelAsync` before
dispose; binary failure never calls `CompleteAsync`; neither sink commits the
outer transaction.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTextCopyEncoder.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlCopy.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTextCopyEncoderTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlCopyTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlCopyCompletionTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3PostgreSqlBinaryCopySink
{
    internal ValueTask CopyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlTableContract table,
        TransferV3Phase4InMemoryBatch batch,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken);
}

internal sealed class TransferV3PostgreSqlTextCopySink
{
    internal ValueTask<TransferV3SpoolReplayReceipt> CopyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlTableContract table,
        TransferV3Phase4BatchSpool spool,
        TransferV3CanonicalLogicalDigest tableLogicalDigest,
        TransferV3BatchEndFrame expected,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken);
}
```

- [ ] Write table-driven binary tests requiring explicit column lists and explicit `NpgsqlDbType` for every mapped kind. Every transferred column is supplied, including `WorkerJobs.LeaseGeneration`; no COPY may omit a column to invoke a server default. No identity/computed-generated column, `COPY FREEZE`, `ON CONFLICT`, trigger/constraint disabling, or unreviewed identifier may appear.
- [ ] Before binary text conversion, reserve worst-case UTF-16/string capacity. If it cannot fit, require whole-batch promotion before opening COPY; never promote halfway through a COPY.
- [ ] Write text-COPY golden tests for null (`\N`), empty field, backslash, tab, newline, carriage return, backspace, form feed, vertical tab, literal `\N`, literal `\.`, multibyte UTF-8 split at every byte, invalid UTF-8, and NUL rejection.
- [ ] Charge strict UTF-8 decoder, character, and escape buffers before allocation and mark only their exact managed element-storage bytes. Define the fixed all-escape/split-multibyte COPY probe checkpoint and deterministic metrics here; Task 19 runs it in isolation and Task 21 owns native-matrix acceptance.
- [ ] Assert exact non-text spellings: Boolean `t`/`f`; signed invariant base-10 integer/enum/int64/Instant with only `0` for zero; lowercase-D UUID; and `yyyy-MM-dd HH:mm:ss.ffffff` local-wall timestamp.
- [ ] Bound `BeginBinaryImportAsync`/`BeginTextImportAsync` themselves with a non-caller token derived from the positive finite operation timeout. Then set `NpgsqlBinaryImporter.Timeout` explicitly as a positive finite `TimeSpan`. `BeginTextImportAsync` is typed as `TextWriter`; require and check the Npgsql 10.0.3 result is `NpgsqlCopyTextWriter`, then set its positive finite integer-millisecond `Timeout` with checked conversion. Text error/cancellation must call `CancelAsync` before disposal because normal disposal completes COPY; binary error must dispose without `CompleteAsync`.
- [ ] On binary success call `CompleteAsync`; on text success flush and normally dispose; only then may the outer transaction commit.
- [ ] Add owned completion tests, all carrying Task 20's VSTest-visible completion category, for active constraints/triggers and adversarial provider detail containing source canaries; returned/logged errors must be sanitized and server stderr must not contain canaries.
- [ ] Run pure tests: `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTextCopyEncoderTests|FullyQualifiedName~TransferV3PostgreSqlCopyTests'`. Expected red.
- [ ] Implement bounded strict decoding/escaping and the two sinks.
- [ ] Rerun pure tests. Expected green. Run real COPY completion tests only through Task 20's owned harness.
- [ ] Review checkpoint: compare behavior with Npgsql 10.0.3 `NpgsqlBinaryImporter` and `NpgsqlCopyTextWriter` contracts; no undocumented streaming assumption is allowed.

### Task 14: Bootstrap-aware table import and committed-batch receipts

**Status:** NOT STARTED

**Objective:** Import all 27 tables in contract order with per-batch
transactions, bootstrap replacement, active constraints/triggers, and fixed
charged source logical receipts.

**Dependency and ordering:** After Task 13. Consumes Task 11 preflight/receipts,
Task 12 work scope, Task 13 COPY sinks, validated session, digest, and budget.

**Preconditions:** Admission committed `importing(A)`; typed preflight is valid;
source stays sealed; timeout is positive; work scope is active.

**Interfaces consumed and produced:** Consumes parser/preflight receipts,
target contract, session, COPY sinks, work scope, digest, and budget. Produces
source logical receipts and table import coordinator exactly as declared below.

**Current evidence:** Not started. Observer, coordinator, pure tests, and
completion tests are absent.

**Expected result and acceptance:** Exact table order, bootstrap rules,
transaction/receipt, rollback, trigger, cleanup, pure, build, and review gates
below pass before Task 15.

**Documentation impact:** Record bootstrap replacement, transaction boundaries,
receipt ownership, and gate totals in plan/report/progress/handoff; Task 20 owns
live completion evidence.

**Recovery and risks:** Earlier committed batches remain; current batch rolls
back. Ambiguous ordinary commit is terminal, removes only transient stage, and
permits no in-place resume.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTableObserver.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTableImportCoordinator.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTableObserverTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTableImporterTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTableImporterCompletionTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3SourceLogicalReceipts : IDisposable
{
    internal IReadOnlyList<TransferV3TableLogicalReceipt> Tables { get; }
    public void Dispose();
}

internal sealed class TransferV3PostgreSqlTableImportCoordinator
{
    internal Task<TransferV3SourceLogicalReceipts> ImportAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3PostgreSqlTargetContract targetContract,
        TransferV3PostgreSqlSession importSession,
        TransferV3Phase4ManagedBudget managedBudget,
        TransferV3Phase4StagingLedger stagingLedger,
        TransferV3Phase4WorkDirectory workDirectory,
        TransferV3Phase4PreflightResult preflight,
        TransferV3Phase4Digest manifestDigest,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken);
}
```

- [ ] Write observer tests that repeat every representability check using `preflight.EffectiveMaxTextPayloadBytes`, accept only the exact table ordinal/name/column shape and both inline/chunked row forms, own no more than the current batch, and compute charged source logical receipts from exact sealed encoded fields.
- [ ] Keep one canonical logical hasher alive per table across all committed batches. In-memory rows and two-pass spool replay feed that same hasher in source order; finalize exactly once at verified table-end.
- [ ] Import exactly the 27 tables in contract order. For each verified batch: begin a new transaction; prepare bootstrap action; choose binary or text COPY for the whole batch; pass the explicit positive finite `operationTimeout` to that COPY and every command; complete COPY; check caller cancellation immediately before commit; commit with a bounded internal token; then clear/remove the batch stage.
- [ ] Cross-check the frozen order against every physical foreign key and assert each principal precedes its dependent; a future FK/order drift is a contract failure, not a runtime retry.
- [ ] Add tests proving an earlier committed batch survives a later parser/COPY/table/EOF failure and the current uncommitted batch rolls back.
- [ ] For the first `ConfigItems` batch transaction, delete exactly the two admitted generated keys, require two affected rows, prove reserved state is exact `importing(A)`, exclude any source reserved key, and COPY all source ConfigItems. Failure rolls back both delete and COPY.
- [ ] Prove the two source API keys may fall in different batches while the target remains non-runnable under `importing(A)`.
- [ ] Include all five exact source bootstrap roots in expected count/digest, byte-compare them with admitted target roots, and omit only those five rows from COPY. Any field difference fails.
- [ ] Never copy `HealthCheckStats`; import `HealthCheckResults` with all three operational triggers active and preserve all physical constraints.
- [ ] Exercise the operational delete/update triggers in the owned completion fixture and prove preserved bootstrap roots did not create accidental blob/history/queue cleanup rows.
- [ ] Treat an ambiguous ordinary batch commit as a terminal import failure: remove only its transient stage, later mark the whole disposable target failed, and never claim batch rollback. No in-place resume exists.
- [ ] After table 27, prove the work root exact-empty, remove/sync it durably, and only then return the fixed 27 source receipts.
- [ ] Tag every `TransferV3PostgreSqlTableImporterCompletionTests` case with Task 20's VSTest-visible completion category; none may execute or skip in a pure task command.
- [ ] Run pure tests: `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTableObserverTests|FullyQualifiedName~TransferV3PostgreSqlTableImporterTests&Category!=TransferV3Phase4PostgreSqlCompletion'`. Expected red.
- [ ] Implement minimal complete observer/coordinator behavior and rerun pure tests green. Defer real PostgreSQL completion cases to Task 20.
- [ ] Review checkpoint: inspect transaction boundaries and prove no parser callback can make rows visible before a verified batch-end.

### Task 15: Canonical receipt and streaming private target blob-stage construction

**Status:** NOT STARTED

**Objective:** Stream verified source blobs into one private sealed target tree
with canonical charged receipt, bounded descriptor-relative enumeration, and
one-shot candidate ownership.

**Dependency and ordering:** After Task 14 and only after Task 12 work root is
durably absent and its sole staging scope released. Task 16 consumes candidate
once.

**Preconditions:** Sealed source, trusted parent, borrowed digest, same budget
and ledger, and next sole staging scope available.

**Interfaces consumed and produced:** Consumes source stage, digest,
budget/ledger, and POSIX streaming directory. Produces target
blob-stage receipt/codec, builder, and one-shot candidate exactly as declared.

**Current evidence:** Not started. Receipt, builder, candidate, streaming POSIX
extension, and focused tests are absent.

**Expected result and acceptance:** Exact tree grammar, streaming hash, receipt,
constant-memory, tamper, cleanup, probe, build, and review gates below pass
before Task 16.

**Documentation impact:** Record tree grammar, constant-memory limits, receipt
ownership, probe metrics, and results in plan/report/progress/handoff.

**Recovery and risks:** Cleanup reparses source and removes only identity-proven
owned entries; scope releases only after root absence and parent sync. Plain
dispose closes descriptors and never guesses membership.

**Files:**

- Modify: `backend/Database/Transfer/TransferV3Posix.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3TargetBlobStageReceipt.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3TargetBlobStageBuilder.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3TargetBlobStageCandidate.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PosixStreamingDirectoryTests.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3PosixOwnedDirectoryTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3TargetBlobStageReceiptTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3TargetBlobStageBuilderTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3TargetBlobStageReceipt : IDisposable
{
    internal int FormatVersion { get; }
    internal TransferV3Phase4Digest ManifestSha256 { get; }
    internal TransferV3Phase4Digest BlobInventorySha256 { get; }
    internal long Count { get; }
    internal long TotalBytes { get; }
    public void Dispose();
}

internal static class TransferV3TargetBlobStageReceiptCodec
{
    internal static int MaximumCanonicalBytes { get; }
    internal static int WriteCanonical(
        TransferV3TargetBlobStageReceipt receipt,
        Span<byte> chargedDestination);
    internal static TransferV3TargetBlobStageReceipt ParseCanonical(
        ReadOnlySpan<byte> canonicalUtf8,
        TransferV3Phase4ManagedBudget managedBudget);
}

internal sealed class TransferV3TargetBlobStageBuilder
{
    internal Task<TransferV3TargetBlobStageCandidate> BuildAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3Phase4StagingParent stagingParent,
        TransferV3Phase4Digest manifestDigest,
        TransferV3Phase4ManagedBudget managedBudget,
        TransferV3Phase4StagingLedger stagingLedger,
        CancellationToken cancellationToken);
}

internal sealed class TransferV3TargetBlobStageCandidate : IAsyncDisposable
{
    internal bool IsConsumed { get; }
    internal ValueTask<TransferV3Phase4CleanupResult> DeleteOwnedBeforeCommitAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3Phase4ManagedBudget managedBudget,
        CancellationToken cleanupToken);
    public ValueTask DisposeAsync();
}
```

- [ ] Write canonical receipt tests for exact compact property order, lowercase 64-hex digests, nonnegative counts/bytes, no unknown/duplicate/reordered properties, and parse-then-byte-compare reserialization into caller-owned charged storage. The codec never returns an unleased `byte[]` or digest string.
- [ ] `BuildAsync` borrows `manifestDigest` only for the awaited call and never disposes or retains it. Before receipt ownership is sealed, create a second `Digest`-charged clone with `TransferV3Phase4Digest.Create(managedBudget, manifestDigest.Bytes)`; the receipt/candidate owns that clone. Inject every fault before/after clone creation and prove the clone is cleared/disposed on failure while the coordinator's original remains usable. Success/unknown retains only the receipt clone; the coordinator disposes the original on every terminal path.
- [ ] Test exact tree grammar `nzbdav-transfer-v3-target-<32-lower-hex>/blobs/aa/bb/<lowercase-D-uuid>` plus `blob-stage.json`; source paths are never copied and `BlobStore` is never referenced.
- [ ] Parse `source.OpenBlobBundleRead()` with the async parser. Derive UUID/shards from the verified cursor; validate descriptor bytes before content; create no-replace mode-0600 files; stream content/hash/length; durable-close; sync the `bb` leaf; reopen no-follow; compare identity/stat; and rehash.
- [ ] Cover empty blobs, multi-field blobs, a practical large blob streamed in bounded chunks, physical orphans, duplicate UUIDs, missing/extra chunks, high-count tiny blobs, cancellation, `ENOSPC`, symlink/hard-link/nonregular/link-count/mode/identity attacks, and actual staging-ledger debits.
- [ ] Charge and mark exact managed array element-storage bytes for parser/hash/receipt/directory-name/path-segment/cleanup buffers. Define the fixed high-count streaming-construction probe and maximum-live checkpoint here; its metrics must not subtract conservative reservation slack or the synthetic runtime reserve. Task 19 supplies isolation and Task 21 owns native-matrix acceptance.
- [ ] Compute the inventory digest while consuming the canonical source bundle order. Never derive it from `readdir` order; POSIX enumeration order is not canonical.
- [ ] Extend `TransferV3Posix` with a budget-aware streaming directory-entry primitive that inspects native name length before copying into one caller-owned charged buffer and refuses an overlong name without constructing a managed string. Do not use `IEnumerable<string>` or any API that allocates one name/object per entry.
- [ ] After the last blob, exact-enumerate the fixed grammar with bounded depth/current-entry state and reject extras. The proof algorithm is fixed: retain only root/`blobs`/current `aa`/current `bb` identities; compare each opened directory with its at-most-four ancestors; require every blob to be a regular file with `nlink=1`; and compare file identity/mode/length before and after reading. `nlink=1` proves file identities cannot alias, while POSIX forbids ordinary directory hard links; no pairwise identity set is retained. Do not retain an `OwnedFile`, directory, UUID, path, digest, descriptor, dictionary, or list per blob.
- [ ] Write/sync a no-replace temporary receipt while root is writable; seal/sync each blob 0400; seal/sync `bb`, `aa`, and `blobs` directories deepest-first to 0500; sync receipt after write and again after chmod 0400; rename no-replace to `blob-stage.json`; sync writable root; seal/sync root 0500; then sync the trusted parent.
- [ ] Inject a fault after every file/directory sync, seal, rename, and parent sync; assert only identity-proven entries are candidates for later cleanup and no broader recursive path deletion occurs.
- [ ] Blob construction begins the next sole staging scope only after Task 12 has released the proven-absent work scope. Return a one-shot opaque candidate that owns the retained parent/root descriptors, the still-charged blob scope, charged canonical receipt, and precommit cleanup authority. It exposes no path/digest/rename/publication API. Explicit unconsumed cleanup reparses the borrowed source with the same budget and removes only identity-proven entries, then releases the scope only after exact root absence and parent sync; cleanup residue leaves it charged. Plain disposal only closes descriptors and reports residue rather than guessing membership. Task 16 consumes ownership exactly once.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PosixStreamingDirectoryTests|FullyQualifiedName~TransferV3PosixOwnedDirectoryTests|FullyQualifiedName~TransferV3TargetBlobStageReceiptTests|FullyQualifiedName~TransferV3TargetBlobStageBuilderTests'`. Expected red.
- [ ] Implement construction with fixed-depth traversal and bounded buffers. Do not copy Phase 3's `_blobFiles`, `_ownedFiles`, `_ownedDirectories`, or `_directoriesByKey` design.
- [ ] Rerun focused tests. Expected green.
- [ ] Review checkpoint: high-count fixture peak must stay inside the same 32 MiB managed counter and report `MaxRetainedBlobEntries <= 1`, `MaxOpenBlobDescriptors <= 8`, and `MaxDirectoryNameBuffers <= 5`, independent of blob count.

### Task 16: Independent current-tree verification and typed stage lifecycle

**Status:** NOT STARTED

**Objective:** Independently reparse/rehash the current target blob tree,
consume candidate ownership into a typed database-verified stage, and freeze
success, unknown, and cleanup lifecycle.

**Dependency and ordering:** After Task 15. Consumes its candidate and
constant-memory tree proof; supplies stage to Task 17.

**Preconditions:** One unconsumed candidate, same live budget, sealed source for
reparse, and owned charged receipt/staging scope.

**Interfaces consumed and produced:** Consumes target blob candidate/receipt,
source, budget, staging ledger, and POSIX verifier. Produces
`TransferV3DatabaseVerifiedStage`, `Consume`, current verification, explicit
precommit cleanup, terminal markers, and one-shot lifecycle.

**Current evidence:** Not started. Verifier, stage, and focused tests are absent.

**Expected result and acceptance:** Exact reparse, tree/hash, one-shot
ownership, cleanup, terminal-state, memory, build, and review gates below pass
before Task 17.

**Documentation impact:** Record one-shot ownership, non-usability,
retained-charge semantics, probe limits, and results in plan/report/progress/
handoff; Task 21 owns final developer docs.

**Recovery and risks:** Before terminal transition call explicit cleanup;
disposal never infers deletion authority. Verified/unknown states preserve the
sealed tree and charged scope and record bounded residue/helper-exit state.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3TargetBlobStageVerifier.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3DatabaseVerifiedStage.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3TargetBlobStageVerifierTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3DatabaseVerifiedStageTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3DatabaseVerifiedStage : IAsyncDisposable
{
    internal static TransferV3DatabaseVerifiedStage Consume(
        TransferV3TargetBlobStageCandidate candidate,
        TransferV3Phase4ManagedBudget managedBudget);
    internal ValueTask VerifyCurrentAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3Phase4ManagedBudget managedBudget,
        CancellationToken cancellationToken);
    internal ValueTask<TransferV3Phase4CleanupResult> DeleteOwnedBeforeCommitAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3Phase4ManagedBudget managedBudget,
        CancellationToken cleanupToken);
    internal void MarkDatabaseVerified();
    internal void PreserveCommitOutcomeUnknown();
    internal bool TryRecordCleanup(TransferV3Phase4SecondaryCode code);
    internal void MarkRequiresHelperExit();
    internal bool IsDatabaseVerified { get; }
    internal bool IsPreservedUnknown { get; }
    internal bool RequiresHelperExit { get; }
    public ValueTask DisposeAsync();
}
```

- [ ] Write completion-candidate tests proving receipt presence alone is insufficient. Parse canonical receipt, verify retained root binding/current identity, and compare it with manifest digest/count/total/inventory.
- [ ] Test `Consume` as an atomic one-shot ownership transfer: the preallocated stage receives the candidate's descriptors/charged receipt/cleanup authority, the candidate becomes inert, and any second consume/dispose race fails with a fixed code without double-close/delete.
- [ ] Reparse the source blob bundle in canonical UUID order and open each derived target file; stream/recompute length/hash/inventory without retaining per-blob metadata. Separately enumerate the exact fixed grammar to detect missing/extra/invalid entries.
- [ ] For every opened entry, use Task 15's fixed constant-memory proof: charged streaming names; at most four ancestor identities; regular-file `nlink=1`; and type/mode/name/length/identity comparisons before and after bounded reads. Detect replacement during a pass; explicitly do not claim inode continuity for byte-identical replacement completed entirely between closed sessions.
- [ ] Test receipt-only spoof, changed content, extra file/directory, invalid shard/name, symlink/hard link, mode change, root replacement, mutation during verify, empty stage, and high-count pressure.
- [ ] Define the fixed high-count reopen/verifier probe and its maximum-live checkpoint. Assert exact current/cumulative managed element-storage metrics plus `MaxRetainedBlobEntries<=1`, `MaxOpenBlobDescriptors<=8`, and `MaxDirectoryNameBuffers<=5`; final isolated/native execution remains in Tasks 19 and 21.
- [ ] Implement precommit cleanup by reparsing the still-open source bundle with the same managed budget for expected UUID membership, restoring writable modes only on retained identity-proven owned directories, removing files/directories deepest-first, and syncing each parent. Stop and return residue codes on any unexpected/unprovable entry.
- [ ] Preallocate the stage and its four bounded diagnostic slots before final CAS. `MarkDatabaseVerified`, `PreserveCommitOutcomeUnknown`, `TryRecordCleanup`, and `MarkRequiresHelperExit` are allocation-free/nonthrowing; the ownership transitions are one-way `Interlocked` operations and cleanup recording failure is ignored.
- [ ] After either transition, disposal closes descriptors only and never deletes the tree. Before either transition, the coordinator must call explicit precommit cleanup; disposal itself never infers recursive authority. The successful/unknown stage retains the charged receipt/digest leases, the still-charged blob staging scope, and their one budget/ledger owners. Because the sealed tree still exists, stage disposal does not release that logical staging charge; only a later phase with proven removal/publication semantics may do so.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3TargetBlobStageVerifierTests|FullyQualifiedName~TransferV3DatabaseVerifiedStageTests'`. Expected red.
- [ ] Implement and rerun focused tests green.
- [ ] Review checkpoint: prove the returned type exposes no arbitrary path, receipt/digest copy, rename/publication method, runtime blob root, data source, or connection.

### Task 17: Independent target row encoding, logical verification, and final CAS

**Status:** NOT STARTED

**Objective:** Independently encode/digest target rows under fresh locks,
reverify the blob tree, perform exact `importing(A) -> database-verified(A)`
CAS, and publish typed commit disposition.

**Dependency and ordering:** After Task 16. Consumes Task 8 locks, Task 9
reconciliation, Task 14 receipts, and Task 16 stage.

**Preconditions:** Valid descriptor/identity, sealed source, 27 receipts,
database-verified stage, same budget, exact source time zone, digest, positive
timeout, and preallocated final disposition.

**Interfaces consumed and produced:** Consumes target mapping/session, source
receipts, blob stage, lock set, reconciler, state store, digest, and budget.
Produces target-row encoder, `TransferV3FinalCommitDisposition`, and target
verifier exactly as declared.

**Current evidence:** Not started. Encoder, verifier, pure tests, and completion
tests are absent; live proof remains Task 20.

**Expected result and acceptance:** Exact row encoding, receipt equality,
current-tree proof, final CAS, commit disposition, cancellation, cleanup, pure,
build, and review gates below pass before Task 18.

**Documentation impact:** Record final-CAS cancellation boundary, commit
outcome, stage ordering, and results in plan/report/progress/handoff.

**Recovery and risks:** `Committed` retains success; `NotCommitted` preserves
the exact primary for Task 18 cleanup; `Unknown` preserves stage, forbids
destructive cleanup/failed CAS, and requires helper exit.

**Files:**

- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetRowEncoder.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetVerifier.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetRowEncoderTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetVerifierTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3PostgreSqlTargetVerifierCompletionTests.cs`

**Interfaces:**

```csharp
internal sealed class TransferV3PostgreSqlTargetRowEncoder
{
    internal Task<TransferV3TableLogicalReceipt> ComputeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransferV3PostgreSqlTableContract table,
        TransferV3Phase4ManagedBudget managedBudget,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal sealed class TransferV3FinalCommitDisposition
{
    internal TransferV3PostgreSqlCommitOutcome Outcome { get; }
    internal bool RequiresHelperExit { get; }
    internal TransferV3Phase4Exception? SanitizedCommitFailure { get; }
    internal void RecordCommitted();
    internal void RecordUnknown();
    internal void RecordNotCommitted(TransferV3Phase4Exception sanitizedCommitFailure);
}

internal sealed class TransferV3PostgreSqlTargetVerifier
{
    internal Task VerifyAndCommitAsync(
        TransferV3PostgreSqlTargetDescriptor target,
        TransferV3PostgreSqlTargetIdentity expectedIdentity,
        TransferV3SealedSnapshotStage source,
        TransferV3SourceLogicalReceipts expectedTables,
        TransferV3DatabaseVerifiedStage blobStage,
        TransferV3Phase4ManagedBudget managedBudget,
        string sourceTimeZoneId,
        TransferV3Phase4Digest manifestDigest,
        int commandTimeoutSeconds,
        TransferV3FinalCommitDisposition preallocatedDisposition,
        CancellationToken callerCancellationToken);
}
```

- [ ] Write independent encoder golden tests for every target type. It may reuse transfer field encodings but must not reuse binary/text COPY serialization or its value formatting as verification authority.
- [ ] Build identifier-safe ordered SELECTs from the embedded target contract. Use `CommandBehavior.SequentialAccess`; for each text field select/read `octet_length` immediately before the value, stream with `GetTextReader` and a strict bounded UTF-8 encoder, and read columns in increasing ordinal order. Set every command's `CommandTimeout` explicitly from the positive finite argument and assert it in command-factory tests.
- [ ] Materialize only budget-bounded key text needed for cursor encoding. Stream unbounded non-key text; charge char/byte buffers and reservations.
- [ ] Open a new matching nonpooling session; begin exact `READ COMMITTED`; acquire the same advisory lock and exact 29-relation `EXCLUSIVE MODE NOWAIT` lock set; revalidate identity, server settings, exact catalog, migration IDs/product versions, constraints, indexes, functions, and trigger enablement. The advisory query necessarily precedes the relation lock, so a snapshot-isolating transaction is prohibited.
- [ ] Verify exact counts and logical digests for all 27 tables. For `ConfigItems`, byte-exactly exclude only `database.import-state` from count/digest, prove unfiltered physical count is source+1, and separately prove exact `importing(A)`.
- [ ] Verify key ordering with reviewed `C` collation, all five preserved roots, both source keys, and derived `HealthCheckStats` count/digest.
- [ ] Immediately before state CAS, call `blobStage.VerifyCurrentAsync`; do not accept cached construction hashes or receipt-only comparison.
- [ ] Check caller cancellation before final CAS. Perform exactly one transaction-bound `importing(A) -> database-verified(A)` CAS under locks; require one row. From that point use a bounded internal token and never observe caller cancellation.
- [ ] Allocate `TransferV3FinalCommitDisposition`, a fixed unknown/helper-exit result, and the stage-backed nonthrowing commit-proof sink before the final CAS. `RecordCommitted`/`RecordUnknown` are fixed-field, allocation-free, nonthrowing, and idempotent; recording failure is ignored after the stage flag. On ordinary commit acknowledgement, call `blobStage.MarkDatabaseVerified()` immediately in the commit continuation, before any allocation, result mutation, cleanup, logging, or further await, then call only `RecordCommitted` and drop any raw commit error without sanitizing/storing it. On reconciliation, merely observing the reserved row never invokes the sink. Only after the reconciliation transaction/fence/session has an explicit successful in-deadline release proof may the reconciler invoke the preallocated sink with the provisional result: `Committed` first calls `MarkDatabaseVerified`, then only `RecordCommitted`; every missing/failed release proof becomes `Unknown`, first calls `PreserveCommitOutcomeUnknown`, then only `RecordUnknown`; `NotCommitted` does not transition the stage and is the only outcome allowed to allocate/sanitize/store the commit primary through `RecordNotCommitted`.
- [ ] The verifier owns the final transaction/session through bounded close. After `Committed` or `Unknown`, close/rollback only within the applicable remaining non-caller deadline and record failures in the stage's preallocated slots; an abandoned provider cleanup cannot delay return/exit or reverse durable success. For `NotCommitted`, preserve the sanitized commit primary in the preallocated disposition so the coordinator can delete/mark-failed without inventing a replacement error.
- [ ] Add owned completion mutations for wrong value/count/collation order, disabled trigger, invalid constraint/index, catalog/history drift, reserved-filter mistakes, receipt spoof, extra/mutated blob, target file/directory mode or identity replacement during an open final-verification pass, and database identity drift. Assert a byte-identical replacement wholly between closed sessions is outside the inode-continuity claim.
- [ ] Tag every target-verifier completion case with Task 20's VSTest-visible completion category.
- [ ] Run pure tests: `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3PostgreSqlTargetRowEncoderTests|FullyQualifiedName~TransferV3PostgreSqlTargetVerifierTests'`. Expected red, then green after implementation.
- [ ] Review checkpoint: inspect every SQL projection and prove target text cannot be materialized as one unbounded string; separately trace normal-commit and reconciled-commit code to prove the stage transition is the first operation after proof.

### Task 18: Top-level Phase 4 coordinator and terminal lifecycle

**Status:** NOT STARTED

**Objective:** Compose the complete Phase 4 sequence under one budget/ledger,
enforce typed source proof before target open, and implement every terminal
path.

**Dependency and ordering:** After Task 17. Integrates Tasks 9-17 and existing
target/session foundations; Task 19 invokes the coordinator.

**Preconditions:** Borrowed sealed source; consumed descriptor and options;
trusted staging-parent ownership; positive required ceilings; every component
complete; no public/runtime exposure.

**Interfaces consumed and produced:** Consumes all Phase 4 component contracts,
target options/descriptor, source stage, budget, ledger, deadlines, and hooks.
Produces terminal disposition and top-level coordinator exactly as declared.

**Current evidence:** Not started. Coordinator, hooks, lifecycle tests, and
sealed-stage Phase 4 accessors are absent.

**Expected result and acceptance:** Exact ordering, ownership, ordinary/
committed/not-committed/unknown/timeout lifecycle, memory, cleanup, build,
regression, and review gates below pass before Task 19.

**Documentation impact:** Record sequence, ownership transfers, terminal
matrix, memory checkpoints, real totals, and unknowns in plan/report/progress/
handoff.

**Recovery and risks:** Deadlines never reset. On expiry stop awaiting,
preserve primary and uncertain residue/lifecycle lease, suppress unsafe
disposal, and require helper exit; `Unknown` forbids destructive cleanup.

**Files:**

- Modify: `backend/Database/Transfer/TransferV3SealedSnapshotStage.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4Coordinator.cs`
- Create: `backend/Database/Transfer/Phase4/TransferV3Phase4Hooks.cs`
- Modify test: `backend.Tests/Database/Transfer/TransferV3SealedSnapshotStageTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4CoordinatorTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4FailureLifecycleTests.cs`

**Interface:**

```csharp
internal sealed class TransferV3Phase4TerminalDisposition
{
    // Dedicated slot; it cannot be displaced by ordinary secondary codes.
    internal bool RequiresHelperExit { get; }
    internal TransferV3Phase4SecondaryCode TerminalCode { get; }
    internal void MarkDatabaseLifecycleRetained();
    internal void MarkCleanupDeadlineExceeded();
    internal void MarkCommitOutcomeUnknown();
}

internal sealed class TransferV3Phase4Coordinator
{
    internal TransferV3Phase4TerminalDisposition TerminalDisposition { get; }
    internal Task<TransferV3DatabaseVerifiedStage> ImportAsync(
        TransferV3SealedSnapshotStage source,
        TransferV3PostgreSqlTargetDescriptor target,
        TransferV3Phase4Options options,
        CancellationToken cancellationToken);
}
```

- [ ] Write pure lifecycle tests using narrow internal fault hooks/fakes; hooks may select fixed fault points/outcomes but may not carry SQL, values, paths, UUIDs, or digests and are not compiled into a public API.
- [ ] Add allocation-free `CanonicalManifestLength` and `CopyCanonicalManifestTo(Span<byte>)` accessors to the sealed source stage, guarded by its existing lock/open checks. Keep `GetCanonicalManifestCopy()` unchanged for Phase 1-3 callers. Phase 4 first reads the checked length, reserves that exact capacity, allocates and marks one exact buffer, then copies into it; it must never call the allocating copy API.
- [ ] Assert exact sequence: validate arguments; construct the single 32 MiB budget (with its 8 MiB reserve active) and required-ceiling staging ledger; reserve manifest capacity before copying canonical manifest bytes from the sealed stage once; hash them once into a charged `TransferV3Phase4Digest`; clear/release the temporary manifest bytes; run source-only preflight with that same budget; require its typed `SourceBootstrapValidated` proof before opening any target session; first session/identity; `READ COMMITTED` admission transaction and commit reconciliation; table import with the preflight result; durable work-root absence; blob build/seal into a candidate; consume it once into the preallocated database-verified stage; independent locked `READ COMMITTED` verification/final reconciliation with the same budget. No manifest, preflight, receipt, or digest allocation precedes or bypasses that counter.
- [ ] Admission commit error: start the ten-second deadline immediately. `importing(A)` continues on a new matching import session; `fresh` is a non-mutating refusal; unknown quarantines/disposes and exits with no failed CAS.
- [ ] Every ordinary catchable failure/caller cancellation starts one fixed ten-second monotonic cleanup deadline as the first catch action. Before admission commits, cleanup is limited to bounded cancel/rollback/close/disposal, the target remains `fresh`, and failed CAS is forbidden. After proven admission, the same fence additionally clears/removes only identity-proven transient work/blob entries and best-effort transitions `importing(A) -> failed(A)` through a new matching descriptor-owned session/transaction. It then closes connections/source reads and disposes the data source only if every connection close released its descriptor lifecycle lease. Any retained lease suppresses data-source disposal, marks `RequiresHelperExit`, and preserves the sanitized primary before rethrow through `ExceptionDispatchInfo`.
- [ ] For final `Committed`, require that the verifier/proof sink has already called allocation-free `MarkDatabaseVerified()` as the first operation after commit proof. Drop the raw commit error without mapping it, tolerate/ignore any impossible disposition-recording failure, then close remaining DB/COPY/source-read handles and dispose the data source only after its last lifecycle lease is released; a failed/abandoned close retains the lease, skips data-source disposal, records only preallocated safe codes, and requires helper exit. Return the already-created stage regardless of postcommit bookkeeping/close failure.
- [ ] For final `NotCommitted`, and only that outcome, use the same ten-second ordinary-failure cleanup fence and the disposition's preserved sanitized commit primary: call the already-consumed `databaseVerifiedStage.DeleteOwnedBeforeCommitAsync(source, managedBudget, cleanupToken)`, best-effort mark failed, close, and rethrow that exact primary. The original candidate is inert and is never reused. For final `Unknown`, require that the verifier/proof sink has already called `PreserveCommitOutcomeUnknown()` as the first operation after the unknown decision, use only the fixed preallocated unknown result, immediately mirror it with `TerminalDisposition.MarkCommitOutcomeUnknown()`, suppress failed CAS and all destructive cleanup, quarantine, and throw the fixed unknown/helper-exit outcome without mapping/storing the raw commit error.
- [ ] Preallocate the coordinator's independent terminal disposition before import begins. It has one dedicated terminal slot that ordinary secondary codes cannot consume; `MarkDatabaseLifecycleRetained`, `MarkCleanupDeadlineExceeded`, and `MarkCommitOutcomeUnknown` are allocation-free, nonthrowing, idempotent, one-way, and accept no caller value. `MarkDatabaseLifecycleRetained` sets `RequiresHelperExit=true` with fixed terminal `ConnectionCloseFailed`; invoke it for any catchable close failure that retains a descriptor lifecycle lease on pre-admission, ordinary-failure, `NotCommitted`, or post-success paths. Deadline abandonment/expiry and commit unknown retain their more specific terminal transitions.
- [ ] If the ordinary/`NotCommitted` cleanup deadline expires, stop awaiting every provider/filesystem/cleanup task immediately, preserve the original sanitized primary unchanged (including a plain fixed-message caller-token `OperationCanceledException`), mark only the independent terminal disposition, retain any unprovable residue, and rethrow through `ExceptionDispatchInfo` for Task 19's pre-serialized flush-and-exit path. The deadline is never reset by rollback, COPY cancel, deletion, failed CAS, close, or disposal.
- [ ] Give post-success close/data-source/source-read cleanup one separate fixed five-second monotonic deadline. Race every operation; once it expires, do not await it again, mark `RequiresHelperExit`, emit only its bounded code, and preserve the success stage. The unknown path inherits the reconciliation fence and performs no await after its ten-second expiry. Test never-completing COPY cancel, rollback, work/blob deletion, failed CAS, connection/source-read close, and data-source disposal on ordinary failure, normal/reconciled success, not-committed, and unknown paths.
- [ ] In every terminal-state test assert descriptor lifecycle-lease count, `TerminalDisposition`, and data-source-dispose calls. The count follows ownership attempt -> session without a gap, reaches zero only after proven close, and remains nonzero after close fault/deadline/abandonment; no terminal path may call data-source disposal while it is nonzero. Exercise retained-close signaling before admission, during ordinary/`NotCommitted` cleanup, and after acknowledged/reconciled success, proving Task 19 always sees `RequiresHelperExit=true`.
- [ ] Prove source stage is borrowed: Phase 4 closes only reads it issues, never disposes/deletes the source, and fails closed if the caller concurrently disposes/mutates it.
- [ ] Prove descriptor and options/staging-parent ownership are consumed once; successful stage retains only duplicate parent/root descriptors and receipt bytes, never the data source.
- [ ] On every terminal path, clear/dispose the coordinator-owned manifest buffer/original digest, preflight temporaries, all 27 source table receipt/digest leases, replay receipts, and work buffers in deterministic reverse ownership order. The blob builder only borrowed the original and placed its separately charged manifest-digest clone in the receipt. On success/unknown, transfer only that blob-stage receipt and its digest leases plus their budget/ledger owners into the preallocated stage; no original manifest digest or source receipt remains reachable.
- [ ] Test parser/COPY/admission-commit/blob/verification/final-CAS cancellation and ambiguity points and every cleanup failure. Cleanup codes never replace primary; no caller cancellation is observed after final CAS starts, including commit reconciliation and post-success cleanup.
- [ ] Define the whole-lifecycle composite memory probe with one budget from pre-manifest through retained success stage. Record each inter-phase maximum-live checkpoint and prove no side/replacement budget appears. The deterministic 32-MiB counter is asserted here; Task 19 supplies the dedicated process and Tasks 20-21 own opaque allocation/RSS/native acceptance.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4CoordinatorTests|FullyQualifiedName~TransferV3Phase4FailureLifecycleTests'`. Expected red, then green.
- [ ] Review checkpoint: enumerate all terminal states and match them to the approved kill/failure table; no path may transition failed/importing/database-verified back to fresh or usable.

### Task 19: Dedicated helper process and synthetic fixture protocol

**Status:** NOT STARTED

**Objective:** Build the private helper protocol, synthetic Phase 1-3 fixture,
isolated pure probe runner, exact memory/RSS measurements, and fixed helper-exit
behavior.

**Dependency and ordering:** After Task 18. Consumes coordinator and component
probe checkpoints; supplies helper/protocol to Task 20.

**Preconditions:** Internal Phase 4 implementation complete; one new
`InternalsVisibleTo`; clean allowlisted child environment; runner-owned roots;
PostgreSQL variables absent in pure mode.

**Interfaces consumed and produced:** Consumes coordinator, synthetic fixture
contracts, measurement checkpoints, and terminal disposition. Produces helper
project, canonical stdin/stdout protocol, scenario allowlist, immutable pure
manifest, and Python runner.

**Current evidence:** Not started. Helper project, protocol, fixture/probe
tests, and runner tests are absent.

**Expected result and acceptance:** Exact protocol, scenario, fixture, memory,
RSS, environment, cleanup, helper build, Python, and review gates below pass
before Task 20.

**Documentation impact:** Record protocol, manifests, formulas, exit codes, and
per-process results in plan/report/progress/handoff.

**Recovery and risks:** Clean only retained-identity owned roots. Unexpected
residue fails closed. Required helper exit performs no cleanup await, flushes
the pre-serialized envelope within one second, then terminates.

**Files:**

- Create: `backend.TransferV3Phase4.TestHelper/backend.TransferV3Phase4.TestHelper.csproj`
- Create: `backend.TransferV3Phase4.TestHelper/Program.cs`
- Create: `backend.TransferV3Phase4.TestHelper/TransferV3Phase4ControlCodec.cs`
- Create: `backend.TransferV3Phase4.TestHelper/TransferV3Phase4MemoryProbe.cs`
- Create: `backend.TransferV3Phase4.TestHelper/TransferV3Phase4SyntheticFixture.cs`
- Create: `scripts/run_transfer_v3_phase4_pure_probes.py`
- Create: `tests/test_run_transfer_v3_phase4_pure_probes.py`
- Modify: `backend/Properties/AssemblyInfo.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4HelperProtocolTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4MemoryProbeTests.cs`
- Test: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4SyntheticFixtureTests.cs`

**Protocol:**

- Canonical JSON control document on stdin; it contains scenario/ordinal controls only and rejects every path, connection component, identifier, token, source/blob/SQLite location, or arbitrary value. Fixed environment variables are created only by an owned runner. Both runner modes provide generated harness/staging roots, ownership attestation, and canary seed; only the Task 20 PostgreSQL-owned mode additionally provides the generated private connection/controller values.
- Allowlisted scenarios only: `roundtrip`, `activity-canary`, `measure-budget`, `measure-parser`, `measure-current-batch`, `measure-copy`, `measure-blob-build`, `measure-blob-verify`, `measure-lifecycle`, `pause-after-admission`, `pause-after-batch`, `pause-during-blob`, `pause-after-blob-seal`, `pause-after-final-commit`, `reconcile-admission`, `reconcile-final`, and `inspect`.
- The immutable pure manifest is exactly `measure-budget`, `measure-parser`, `measure-current-batch`, `measure-copy` (codec/escape only; no Npgsql session), `measure-blob-build`, and `measure-blob-verify`. Pure mode requires every PostgreSQL variable absent. `measure-lifecycle` and every roundtrip/activity/admission/reconciliation/pause/inspect scenario are PostgreSQL-owned Task 20 scenarios and cannot run under the pure runner.
- Canonical stdout JSON contains allowlisted scenario/event/result codes and bounded numeric metrics only. Stderr is empty except a fixed fatal launcher code. No value/path/UUID/digest/connection component is emitted.

- [ ] Add only `[assembly: InternalsVisibleTo("backend.TransferV3Phase4.TestHelper")]`; do not make Phase 4 types public.
- [ ] Write protocol tests for exact property order, unknown/duplicate/missing fields, scenario allowlist, ordinals/ranges, canonical output, exit codes, and canary redaction.
- [ ] Build the cross-platform pure runner with a fixed `--configuration` and output-only results-directory interface; it accepts no scenario, path, fixture, SQLite/blob source, staging root, identity, or secret input. Before creating any resource, fail on inherited `NZBDAV_TRANSFER_V3_PHASE4_*`, `NZBDAV_POSTGRES_*`, `POSTGRES_*`, `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, `PGSERVICE`, `PGSERVICEFILE`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT`, `PGCLIENTENCODING`, `PGTZ`, `PGOPTIONS`, `PGTARGETSESSIONATTRS`, `PGSSLNEGOTIATION`, `PGGSSENCMODE`, `PGREQUIREAUTH`, `PGAPPNAME`, `NZBDAV_REQUIRE_POSTGRES_TESTS`, `NZBDAV_TEST_POSTGRES_CONNECTION_STRING`, `NZBDAV_DATABASE_PROVIDER`, `NZBDAV_DATABASE_CONNECTION_STRING`, `NZBDAV_UPDATE_POSTGRES_CATALOGS`, `DOTNET_STARTUP_HOOKS`, `DOTNET_DiagnosticPorts`, or any enabled `CORECLR_`/`COR_` profiler setting; then construct each child environment from a clean allowlist rather than mutating the inherited environment. Create and retain one mode-0700 harness/staging root with exact owner UID/mode/device/inode plus a secret marker, create a fresh private empty mode-0700 `HOME` and `APPDATA` beneath it for every child, and launch every helper with exact `DOTNET_EnableDiagnostics=0`, no diagnostic-port/startup-hook/profiler variable, no in-process `EventListener` code, and PostgreSQL environment absent. Launch each exact pure-manifest scenario once in a fresh helper process and validate canonical bounded output plus an immutable scenario/platform manifest; unknown, omitted, duplicate, skipped, or wrong-mode scenarios fail.
- [ ] The pure runner performs descriptor-relative no-follow cleanup after re-proving root identity/UID/mode/marker and exact child grammar. A replacement, symlink, unexpected entry, or uncertain identity leaves bounded residue and fails without pathname-recursive deletion. Add fake-subprocess/filesystem tests for every create/build/invoke/parse/cleanup failure and for signal handling; prove only runner-owned roots are touched.
- [ ] For each helper, bind the runner's full-lifetime RSS sampler and the helper's bounded numeric `Process.PeakWorkingSet64` result to the same retained child identity rather than PID alone. Require both measurements to be present and valid, normalize both to bytes, define `rssHighWaterBytes = max(runnerObservedHighWaterBytes, helperPeakWorkingSet64Bytes)`, and require `rssHighWaterBytes <= 384 MiB`. A missing value, identity mismatch/reuse, invalid unit conversion, negative value, or sampler failure fails closed; do not require the two samplers to be equal. Record this separately from managed accounting and heap diagnostics. Pure mode has no PostgreSQL/server-memory metric.
- [ ] At process start, create one new random mode-0700 helper-owned fixture root beneath the runner-owned harness root and retain its descriptor/identity/token. Generate the SQLite database, source blob tree, Phase 1-3 snapshot, and all temporary files only below that root; accept no fixture/source/blob/SQLite/temp path through argv, environment, stdin, or test API.
- [ ] Build synthetic SQLite/Phase 1-3 input through existing validators/exporter/verifier/sealed stage, not handwritten unverified JSONL. Include all 27 tables, five roots, distinct API keys, physical blob orphans, empty/multifield blobs, derived health rows, multiple batches, practical large text, and a practical large streamed blob.
- [ ] Add fixture-root replacement/token/mode/link/identity and interrupted-cleanup tests. Cleanup acts only through the retained descriptor after re-proving the exact root identity/token and never follows a supplied/discovered path; unprovable residue produces a bounded code and helper exit.
- [ ] The helper may invoke the internal native PostgreSQL migrator only against the harness-owned target, then constructs the strict descriptor/options and calls the coordinator. It never initializes application telemetry, runtime DI, `Program`, global Npgsql data sources, or runtime `BlobStore`.
- [ ] Register adversarial `ActivityListener`, logger, and `MeterListener` canaries before constructing the descriptor. The `activity-canary` scenario reports only booleans/counts and must capture no Npgsql activity, payload, credential, exception, path, digest, connection string, or canary. Npgsql metrics cannot be disabled: their private capture may contain only instrument metadata, numeric values, the fixed data-source name/system/connection-state literals, and the runner-generated loopback host/port; any other tag/value fails. Do not register an `EventListener`: exact Npgsql 10.0.3 source proves that doing so would deliberately enable the independent `Npgsql.Sql` command-text source, which is outside the approved helper contract.
- [ ] Implement each `measure-*` scenario as the only scenario in a fresh helper process. Warm JIT, JSON, cryptographic, and async paths and build source fixtures before the baseline; then create one fresh budget and deterministic charged-buffer state. `measure-budget` reserves the logical remaining 24 MiB without allocating a payload and proves exact 32-MiB current/peak accounting rather than a hidden backing allocation.
- [ ] Preallocate the sampler and capture `A0/C0/H0/L0/G0` immediately before each measured interval and `A1/C1/H1/L1/G1` at its checkpoint, without logging/assertion/serialization inside the interval. `A` is precise `GC.GetTotalAllocatedBytes(true)`, `C` cumulative acknowledged managed element-storage bytes, `H` approximate `GC.GetTotalMemory(false)`, `L` current acknowledged managed element-storage bytes, and `G` every generation's collection count. Require `A1>=A0`, `C1>=C0`, and `(A1-A0)>=(C1-C0)`; any violation invalidates the probe. Define the unclamped `opaqueAllocatedUpperBound=(A1-A0)-(C1-C0)` and require it `<=8 MiB`. Only exact GC-heap array/string element storage completed inside the interval may contribute to `C1-C0`; native memory and pre-baseline/interned storage are excluded.
- [ ] Report the signed diagnostic `heapResidual=(H1-H0)-(L1-L0)` without clamping. If any collection count changed or the residual is negative, mark that heap diagnostic indeterminate. `GetTotalMemory(false)` is reporting-only, never an acceptance/high-water gate. Acceptance requires deterministic `PeakBytes<=32 MiB`, `opaqueAllocatedUpperBound<=8 MiB`, and the independent RSS caps. No formula subtracts `RuntimeReserveBytes`, unused row/field slack, headers/alignment, source-fixture bytes, native memory, or an unmarked/failed allocation. Never call `GC.Collect` or use a shared xUnit testhost as final evidence.
- [ ] Pause scenarios emit one fixed sentinel and block. The harness may SIGKILL only this exact helper PID. `pause-after-batch` accepts table/batch ordinals, never row keys/values.
- [ ] Pre-serialize fixed success/unknown/fatal result envelopes before invoking the coordinator. After return or catch, inspect only the returned stage (when present) and the coordinator's independent `TerminalDisposition`; the coordinator mirrors any internal unknown/abandoned-cleanup helper-exit requirement into that disposition before completing. Never inspect or mutate the thrown primary. If either says `RequiresHelperExit=true`, perform no provider/filesystem cleanup await, write and flush the appropriate pre-serialized bounded envelope within one second, and terminate the helper immediately; if the flush itself stalls, terminate with the fixed launcher exit code. No orphan provider task may survive in a reusable process.
- [ ] Run `dotnet test backend.Tests/backend.Tests.csproj --no-restore --filter 'FullyQualifiedName~TransferV3Phase4HelperProtocolTests|FullyQualifiedName~TransferV3Phase4MemoryProbeTests|FullyQualifiedName~TransferV3Phase4SyntheticFixtureTests'` and `python3 -m unittest discover -s tests -p 'test_run_transfer_v3_phase4_pure_probes.py' -v`. Expected red.
- [ ] Implement the helper and rerun pure tests green. Before its first build, run `dotnet restore backend.TransferV3Phase4.TestHelper/backend.TransferV3Phase4.TestHelper.csproj`; then build with `dotnet build backend.TransferV3Phase4.TestHelper/backend.TransferV3Phase4.TestHelper.csproj --configuration Release --no-restore -warnaserror`.
- [ ] Review checkpoint: `rg -n 'NzbWebDAV\.Program|CreateHost|BlobStore|Serilog|EventListener|AddOpenTelemetry' backend.TransferV3Phase4.TestHelper` must contain no runtime/telemetry initialization or EventSource listener registration; separately review the narrowly scoped Activity/logger/metrics negative-control fixture.

### Task 20: Exclusively owned PostgreSQL completion, crash, log, and reconciliation proof

**Status:** NOT STARTED

**Objective:** Own the complete PostgreSQL 16.14 lifecycle and prove live
admission, COPY, verification, crash durability, reconciliation, redaction,
memory, RSS, and cleanup.

**Dependency and ordering:** After Task 19. Consumes helper and completion cases
from Tasks 8, 13, 14, and 17; workflow edits occur only after local harness
green.

**Preconditions:** Pinned image; runner-owned private root, role/database/schema,
container, volume, loopback port, tokens, and staging parent; inherited
PostgreSQL/diagnostic variables rejected; no external resource accepted.

**Interfaces consumed and produced:** Consumes helper protocol, internal
completion fixtures, Docker, TRX validator, and owned resource identities.
Produces exact runner CLI/modes, completion attribute/client, live/crash/
reconciliation suites, captures, and workflow integration.

**Current evidence:** Not started. Runner, completion attribute/client, and
completion/crash/reconciliation tests are absent.

**Expected result and acceptance:** Python runner tests, owned completion and
composite suites, exact TRX validation, redaction scans, memory/RSS ceilings,
crash/reconciliation cases, cleanup, workflow contracts, and review all pass.

**Documentation impact:** After local green only, update specified workflow
blocks/matrix and record owned IDs, TRX totals, scans, caps, and cleanup in
plan/report/progress/handoff. Do not claim production availability.

**Recovery and risks:** One `finally` stops/kills only identity-proven resources,
drains/scans captures, then removes exact owned container/volume/root. Uncertain
identity leaves bounded residue and fails without discovery or broad cleanup.

**Files:**

- Create: `scripts/run_transfer_v3_phase4_postgres_tests.py`
- Create: `tests/test_run_transfer_v3_phase4_postgres_tests.py`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4PostgreSqlCompletionAttribute.cs`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4HelperClient.cs`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4PostgreSqlCompletionTests.cs`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4CrashCompletionTests.cs`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4ReconciliationCompletionTests.cs`
- Modify: `.github/workflows/verify.yml` only after the local harness is green.
- Modify: `tests/test_release_workflow_contract.py` only with the corresponding fail-closed workflow assertions.

**Owned harness contract:**

- Image: the repository's already pinned `postgres:16.14-alpine@sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777`.
- Accept only `--configuration`, an output-only results directory, and the fixed `--suite completion|composite` selector. Before creating any resource, fail on inherited `NZBDAV_TRANSFER_V3_PHASE4_*`, `NZBDAV_POSTGRES_*`, `POSTGRES_*`, `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, `PGSERVICE`, `PGSERVICEFILE`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT`, `PGCLIENTENCODING`, `PGTZ`, `PGOPTIONS`, `PGTARGETSESSIONATTRS`, `PGSSLNEGOTIATION`, `PGGSSENCMODE`, `PGREQUIREAUTH`, `PGAPPNAME`, `NZBDAV_REQUIRE_POSTGRES_TESTS`, `NZBDAV_TEST_POSTGRES_CONNECTION_STRING`, `NZBDAV_DATABASE_PROVIDER`, `NZBDAV_DATABASE_CONNECTION_STRING`, `NZBDAV_UPDATE_POSTGRES_CATALOGS`, `DOTNET_STARTUP_HOOKS`, `DOTNET_DiagnosticPorts`, or enabled `CORECLR_`/`COR_` profiler settings; then construct every child environment from a clean allowlist and set exact `DOTNET_EnableDiagnostics=0` for every helper/test process that can execute Phase 4. Never accept an external connection, server/container/volume ID or name, fixture, SQLite/source/blob/staging/temp/home path, role, database, schema, port, or ownership token.
- Create a mode-0700 private harness root and fresh empty mode-0700 `HOME` and `APPDATA` directories beneath it for every ordinary child. The one owned default-file canary child is the sole exception: its separate private home contains only the exact invalid `.pgpass`/`.postgresql` canaries required below. Also create the exact random container name `nzbdav-v3p4-<32-lower-hex>`, exact random volume name of the same grammar, a random **non-secret** ownership ID, separate secret controller/password tokens and canaries, and exact staging parent. Create the volume with `nzbdav.transfer-v3-phase4.owner-id=<non-secret-id>`, inspect only that exact name with a narrow `--format` that returns that label, and verify it before attaching or removing the volume. Publish PostgreSQL only through a Docker-assigned `127.0.0.1` loopback port. Store the returned container ID and operate only on that ID.
- Put Docker secrets/controller/password tokens in a mode-0600 private `--env-file`, and pass helper/test secrets through subprocess environment dictionaries. The non-secret ownership ID may appear only in exact label argv and narrow ownership-check output. Never place a password, connection string, secret token, staging path, or canary in process argv, command echo, test display names, or exception text.
- Give the container the same non-secret owner-ID label and verify exact name/ID/label before each kill/restart/removal using narrowly formatted non-secret fields or exit status. Explicitly prohibit raw/full `docker inspect` JSON because it can expose container environment secrets. Never call `docker ps`, list volumes, or use label discovery to find cleanup candidates; never inspect/remove another PostgreSQL container or volume. The loopback controller is created by the runner, accepts only its separate generated secret token and fixed action, and accepts no caller-supplied PID/container/volume/fixture identifier.
- Start each boot with `docker start --attach`, capture stdout separately, capture the complete PostgreSQL stderr stream from process creation, and append every restart's stderr to one private file.
- Configure exact approved logging, WAL durability, UTF-8, and time-zone settings at server start. Create one non-superuser owner role/database/schema inside this container only and grant that role inherited `pg_read_all_settings` visibility (and no broader monitoring role) so the privileged preload settings can be read. Assert the role's effective `EXECUTE` on `pg_catalog.pg_control_system()` separately: stock PostgreSQL 16.14 supplies it through the default `PUBLIC` function privilege, not through `pg_monitor` or `pg_read_all_settings`. Prove independently that missing `pg_read_all_settings` and an isolated target with exact `pg_control_system()` `PUBLIC EXECUTE` revoked are each refused before payload; never substitute a superuser or grant `pg_monitor`.
- Restore and build the backend, tests, and helper itself before opening PostgreSQL so the same command works from a clean checkout. Run tests with `TZ=Etc/UTC`, required PostgreSQL mode, generated compliant connection string, ownership attestation, helper path, canary file, and a token-authenticated loopback controller endpoint for exact container kill/restart requests.
- Capture separately and privately: container stdout; startup-to-EOF PostgreSQL stderr across every boot; helper stdout/stderr; dotnet console stdout/stderr; TRX files; runner/controller logs; every Docker subprocess stdout/stderr; and Activity/logger/metrics negative-control captures. Test display names and runner messages must not contain target identifiers or SQL.
- In one `finally` path used for success, assertion failure, setup failure, signal, helper crash, and database crash: clean-stop or identity-proven kill the final server, wait for the attached process and stderr EOF, drain every pipe, and scan every capture before cleanup. Scan every byte of every capture for generated canaries. Also parse structured TRX and scan its dynamic stdout/stderr/error/result-message/attachment payloads, plus every unstructured runtime stream, for frozen SQL text, `COPY`/`LOCK TABLE`/DML fragments, and reviewed table/column/constraint/function identifiers; static checked-in test identity metadata is not treated as runtime output.
- The only redaction exception is a narrowly parsed pre-authentication record in PostgreSQL stderr that may contain the generated role/database/client endpoint and fixed application name. It never permits password/token, source/stage path, payload, UUID, digest, SQL, schema object identifiers, helper/client output, or post-preflight records; no blanket line/file exclusion is allowed.
- Trap cleanup verifies the retained harness-root descriptor identity/mode/UID/secret marker and exact child grammar before descriptor-relative no-follow host cleanup, and verifies exact container ID/name plus the non-secret container/volume ownership ID before Docker removal. This is also the crash cleanup for helper-owned fixture roots. Any unexpected/unprovable host entry or Docker identity leaves bounded residue and fails without pathname-recursive or broad cleanup.
- Dedicated required mode is `NZBDAV_REQUIRE_TRANSFER_V3_PHASE4_POSTGRES_TESTS=1`. Outside that mode, completion-category tests are excluded from ordinary test commands rather than selected-and-skipped; inside it, any missing runner-generated harness input executes and fails setup. `completion` runs only the category once; `composite` runs the entire native noncompletion backend suite once, the completion category once, and the existing PostgreSQL subset once in an exactly owned pinned Alpine/musl SDK child container, all inside the same owned PostgreSQL lifecycle, then validates three TRXs and embedded required-test-class/platform manifests.

- [ ] Write Python unit tests with a fake subprocess/Docker boundary for inherited-environment rejection, fixed-suite parsing, runner-owned restore/build, unique non-secret-owner-ID container/volume labels, exact-name narrowly formatted inspect, rejection of full inspect JSON, private env-file delivery, secret-free argv, ID-only operations, loopback-only port extraction, attach-before-each-start, every separate capture, clean stop/EOF/drain, secret-token-authenticated controller, forbidden-corpus/pre-auth scanning, TRX validation, and identity-proven cleanup. Assert no discovery/list command exists.
- [ ] Add early-failure/signal tests at every create/start/build/test/kill/stop/drain/scan/remove step. In every case prove the common `finally` path stops only proven-owned resources, reaches stdout/stderr EOF where possible, drains and scans container stdout, PostgreSQL stderr, helper/dotnet/Docker stdout+stderr, TRX, controller/runner, and Activity/logger/metrics negative-control captures before returning. A scan/drain failure must fail closed and must not print the sensitive capture.
- [ ] For the composite musl child, use the retained-ID create/start/attach lifecycle and a runner-generated single host: Linux may use host networking with `127.0.0.1`; Docker Desktop uses `host.docker.internal`; fail closed if the platform cannot prove the route. Exclude the completion category, pass secrets only by environment, capture every stream, and apply the same label/identity/finally/redaction rules as the PostgreSQL container.
- [ ] Implement the custom completion fact/theory attribute with an xUnit trait discoverer that emits a VSTest-visible exact `Category=TransferV3Phase4PostgreSqlCompletion`; add a meta-test over discovery output. Missing connection, attestation, helper, controller, log capture, or runner-created fixture input is a setup failure, never a skip. Apply it to every `*CompletionTests.cs` case introduced in Tasks 8, 13, 14, 17, and 20.
- [ ] Add exact fresh admission; exact admission/final 29-lock SQL; exactly 29 granted target-relation `ExclusiveLock` rows filtered by backend PID and expected OIDs while allowing incidental catalog locks; ordinary-writer and `SELECT ... FOR UPDATE` contention; two-importer arbitration; full 27-table/blob roundtrip; binary, spill/text COPY, mixed inline/chunked rows, multi-batch, active FK/health triggers, active delete/update triggers with zero accidental bootstrap cleanup rows, root/key replacement, ConfigItems filter, derived-stat, practical large-text/large-blob, and bounded-accounting cases.
- [ ] Add adversarial parser/COPY/constraint/trigger/POSIX failures whose raw details contain generated canaries and unique SQL/table/column/constraint/COPY fragment canaries; prove every capture omits the full forbidden corpus. Add failed-auth/preflight proof that no transfer payload/digest was sent, applying the narrow server-stderr-only pre-auth metadata exception record by record.
- [ ] Add one owned pre-payload connection proof whose private child home contains deliberately invalid canary `.pgpass`, client certificate/key, and root-certificate defaults while the explicit-password/disabled-SSL descriptor opens and validates successfully. Prove no default file was used, no canary entered any capture, and the ordinary completion helpers instead receive fresh empty private home directories.
- [ ] Add cancellation at parse, COPY, admission commit/ambiguity, ordinary batch commit, blob write/seal, target verification, pre-final-CAS, and final commit. Assert durable `failed(A)` after catchable post-admission failures and prove no caller cancellation is observed after final CAS begins.
- [ ] Add helper SIGKILL after admission, after arbitrary committed batches, during blob build, and after blob seal; inspect `importing(A)` and expected residue, prove reuse is blocked, then drop/recreate only the disposable target.
- [ ] Kill the owned PostgreSQL postmaster/container immediately after acknowledged admission and final commits, reattach/restart the same owned data volume, and prove crash-durable `importing(A)` or `database-verified(A)` plus corresponding rows/stage without corruption.
- [ ] Test lost acknowledgement with real PostgreSQL outcomes, not outcome enums: for admission and final, (a) execute and acknowledge the actual `COMMIT` inside the adapter, then inject the client-side error; (b) execute an actual `ROLLBACK`/noncommit, then inject the error; and (c) leave the real transaction and advisory lock in flight past the fence. From an independent session prove the lock remains held until the original transaction really ends, reconciliation waits for that fence, and the later READ COMMITTED locking snapshot yields exact committed/not-committed/unknown stage and failed-CAS behavior.
- [ ] Cover reconciliation timeout, DNS/open stall, identity/postmaster/system-ID/database/schema/role/recovery/endpoint drift, rollback/close/data-source-dispose stall, and abandoned provider task. Unknown must preserve stage and suppress failed CAS/destructive cleanup; after the ten-second fence the helper performs no further cleanup await. Apply the separate five-second post-success liveness proof to acknowledged and reconciled success.
- [ ] Run one-byte tiny-blob fixtures at exactly `N=4,096` and `4N=16,384`. For both require the Phase 4 managed peak `<=32 MiB`, `MaxRetainedBlobEntries<=1`, `MaxOpenBlobDescriptors<=8`, and `MaxDirectoryNameBuffers<=5`; require the 4N managed peak to be no more than N plus 1 MiB. Separately sample helper RSS and PostgreSQL container memory high-water: each helper run must be `<=384 MiB`, PostgreSQL `<=512 MiB`, and each 4N-minus-N delta `<=64 MiB`. These are checked-in completion-fixture caps, not production defaults or substitutes for the managed counter.
- [ ] Run `measure-lifecycle` plus the 4,096/16,384 blob construction and verification probes as separate fresh helpers against only the owned PostgreSQL lifecycle. Require the exact Task 19 opaque allocation-volume gate `<=8 MiB`, no managed peak above 32 MiB, and the independent RSS/server caps above. Report allocation volume, signed approximate heap residual/indeterminate state, current/peak/cumulative managed element-storage counters, collection-count changes, and RSS separately; never combine them into a misleading single memory figure or treat the heap sample as a high-water gate.
- [ ] Run `python3 -m unittest discover -s tests -p 'test_run_transfer_v3_phase4_postgres_tests.py' -v`. Expected red, then green.
- [ ] Run `python3 scripts/run_transfer_v3_phase4_postgres_tests.py --configuration Release --suite completion --results-directory artifacts/test-results/transfer-v3-phase4-postgres`.
- [ ] From a clean checkout state, the runner itself must restore/build backend, tests, and helper, then execute `dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore --filter 'Category=TransferV3Phase4PostgreSqlCompletion' --results-directory artifacts/test-results/transfer-v3-phase4-postgres --logger 'trx;LogFileName=transfer-v3-phase4-postgres.trx'` and `python3 scripts/validate_trx_results.py artifacts/test-results/transfer-v3-phase4-postgres/transfer-v3-phase4-postgres.trx` while its owned server is live.
- [ ] Expected completion result: exit `0`, selected/executed test count >0, `failed=0`, `notExecuted=0`, no skip/warning, clean final stop, captured stderr EOF, and no canary in any capture.
- [ ] Only after local green, replace—not supplement—the current `verify` job's service-backed `services.postgres`, `Test native PostgreSQL integration first`, `Test complete backend suite`, `Validate native backend TRX results`, and `Test PostgreSQL integration on Alpine musl` blocks with one required `--suite composite` step. Preserve their union through the native full-suite class manifest and an exactly owned/labeled/ID-tracked pinned Alpine SDK child container that runs the existing musl PostgreSQL filter against the runner-owned loopback server and writes `postgres-musl.trx`; never use `docker run --rm` without retained identity/captures. Keep vulnerability scanning, public PostgreSQL refusal, SQLite, Python, frontend, and all unrelated gates unchanged. Do not duplicate either native set, record completion tests as skips, accept an external connection, or weaken coverage.
- [ ] In the same reviewed workflow edit, replace the current four-entry `transfer-native` execution with the Task 19 pure runner and immutable matrix: Linux x64 glibc native, Linux x64 musl native, Linux arm64 glibc QEMU/emulated, Linux arm64 musl QEMU/emulated, and native macOS arm64 on `macos-15` with `uname -m=arm64`. Every entry runs the exact six-scenario pure manifest once, the complete pure Transfer/POSIX/layout class manifest once, and validates zero omitted/duplicate/skipped/not-executed cases. Assert little-endian and 64-bit everywhere. Pin/verify the checked-in exact SDK/runtime manifest (`10.0.301` SDK and `10.0.9` .NET runtime at this checkpoint); no floating `10.0.x` value is accepted. Reverify the macOS label against the current GitHub-hosted-runners reference when editing.
- [ ] The pure matrix also requires the Task 19 same-retained-child-identity RSS proof for every scenario on every entry: both byte-normalized measurements must be valid and `max(runnerObservedHighWaterBytes, helperPeakWorkingSet64Bytes) <= 384 MiB`. Keep those measurements separate from the 32-MiB importer counter and 8-MiB opaque allocation-volume gate; absence of PostgreSQL in pure mode means no server-memory sample is expected.
- [ ] Review checkpoint: independently inspect every Docker command and cleanup branch before allowing the harness to run.

### Task 21: Isolation, documentation, full regression, and independent review

**Status:** NOT STARTED

**Objective:** Prove Phase 4 remains private/disabled, update authoritative
developer docs, run pure and owned composite regressions/native matrix, and
obtain three independent reviews.

**Dependency and ordering:** Final task after Task 20. Task 20 removes its owned
server; Task 21 never runs a live filter directly or reuses a server.

**Preconditions:** Tasks 11-20 complete; helper/runners present; Task 20
workflow/matrix locally green; dirty pre-existing worktree preserved.

**Interfaces consumed and produced:** Consumes all Phase 4 production/tests,
runners, workflow, docs, and reports. Produces no new production interface;
produces isolation proof, final documentation, regression/native-matrix
evidence, and review record.

**Current evidence:** Not started. Isolation test, final docs, runner/workflow/
regression/native-matrix evidence, and reviews do not exist.

**Expected result and acceptance:** Every exact restore/build, pure/composite,
Python, entrypoint, isolation, native matrix, documentation, whitespace, leak
scan, and three-review gate below passes with PostgreSQL still private.

**Documentation impact:** Modify the transfer Contracts README and older
provider plan exactly as listed; document private boundary, ownership,
`database-verified` non-usability, residue recovery, Phase 5, and required field
ceiling without promotion or performance claims.

**Recovery and risks:** Any failed gate keeps Task 21 incomplete. Repair only
the scoped issue, rerun affected/final proof, preserve dirty files, and never
use destructive Git cleanup or an external PostgreSQL resource.

**Files:**

- Modify: `backend.Tests/Database/Transfer/TransferV3IsolationCanaryTests.cs`
- Create: `backend.Tests/Database/Transfer/Phase4/TransferV3Phase4IsolationTests.cs`
- Modify: `backend/Database/Transfer/Contracts/README.md`
- Modify: `docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md`
- Verify: `.github/workflows/verify.yml`
- Verify: `scripts/run_transfer_v3_phase4_pure_probes.py`
- Verify: every Phase 4 and shared file above.

- [ ] Update source-side isolation so existing top-level Phase 1-3 components still accept no target context/Npgsql/runtime BlobStore, while the `Phase4/` subdirectory is the only provider-specific exception.
- [ ] Assert no Phase 4 coordinator/stage/descriptor reference from `backend/Program.cs`, entrypoint, provider selection, runtime factories/services, controllers/public commands, `BlobStore`, Compose, frontend, release startup, or migration-completion-marker code.
- [ ] Assert only the backend transfer assembly, backend tests, and `backend.TransferV3Phase4.TestHelper` can reference the internal coordinator. Assert no successful public v3 maintenance kind was added and PostgreSQL refusal still occurs before secret read.
- [ ] Document the Phase 4 private boundary, exact inputs/ownership, `database-verified` non-usability, residue recovery, and Phase 5 follow-on. Amend the older provider plan only to state that this approved design supersedes temporary-table reconstruction and blob publication/completion wording, and preserve the explicit future obligation that public orchestration bind the same required `NZBDAV_TRANSFER_MAX_FIELD_BYTES` value at both export and import. Do not pull Phase 5 into this work.
- [ ] Run `dotnet restore backend.Tests/backend.Tests.csproj` and `dotnet restore backend.TransferV3Phase4.TestHelper/backend.TransferV3Phase4.TestHelper.csproj`.
- [ ] Run `dotnet build backend/NzbWebDAV.csproj --configuration Release --no-restore -warnaserror` and the helper build with the same flags. Expected zero warnings/errors.
- [ ] Do not run a live-PostgreSQL filter directly from Task 21 or refer to a server left behind by Task 20; Task 20 always removes its owned instance. Encode required Transfer-v3, migration, catalog, environment, provider-selection, and SQLite-runtime test-class names in the runner's immutable composite manifest.
- [ ] Run `python3 -m unittest discover -s tests -p 'test_*.py' -v`, `python3 -m compileall -q scripts tests`, and `bash tests/test_entrypoint_contract.sh`.
- [ ] Run `python3 scripts/run_transfer_v3_phase4_pure_probes.py --configuration Release --results-directory artifacts/test-results/transfer-v3-phase4-pure` on the current supported host and validate its immutable six-scenario/platform manifest before the final PostgreSQL composite proof.
- [ ] Run `python3 scripts/run_transfer_v3_phase4_postgres_tests.py --configuration Release --suite composite --results-directory artifacts/test-results/transfer-v3-phase4-composite` as the final owned proof. While the one runner-owned server is live, it runs native `Category!=TransferV3Phase4PostgreSqlCompletion` exactly once into `artifacts/test-results/transfer-v3-phase4-composite/backend-full.trx`, the completion category exactly once into `artifacts/test-results/transfer-v3-phase4-composite/transfer-v3-phase4-postgres.trx`, and the pinned Alpine child filter `FullyQualifiedName~PostgreSql&Category!=TransferV3Phase4PostgreSqlCompletion` into `artifacts/test-results/transfer-v3-phase4-composite/postgres-musl.trx`. Validate all three exact paths and prove every immutable class/platform manifest entry executed with zero failed/not-executed/skipped tests.
- [ ] Verify Task 20's exact five-entry native matrix and immutable six-scenario pure manifest: Linux x64 glibc native, Linux x64 musl native, Linux arm64 glibc QEMU/emulated, Linux arm64 musl QEMU/emulated, and native `macos-15` arm64. Each job must prove little-endian, 64-bit, exact SDK/runtime `10.0.301`/`10.0.9`, complete pure Phase 1-4 POSIX/stage/layout classes, and zero skipped/not-executed/omitted/duplicate scenarios. QEMU evidence does not substitute for native macOS arm64. Reverify the label against <https://docs.github.com/en/actions/reference/runners/github-hosted-runners>.
- [ ] Scope the source guard against forced collection to Phase 4 implementation/helper code and assert it contains no `GC.Collect`. Require the exact Task 19 formulas, never subtract the synthetic runtime reserve or conservative slot slack, and report `GetTotalMemory(false)` only as an approximate canary alongside the precise process-lifetime allocation-volume delta and independent RSS caps.
- [ ] In every pure native job, retain the explicit exclusion of `TransferV3ImportStateStorePostgreSqlTests`, add `Category!=TransferV3Phase4PostgreSqlCompletion`, and assert the selected TRX has zero skips/not-executed tests. The new pure `TransferV3ImportStateStorePostgreSqlContractTests` remains included; no job may rely on an unset connection merely to turn a live test into a skip.
- [ ] Run scoped whitespace/status review for only Phase 4/shared paths, then `git status --short`; confirm all pre-existing dirty files remain preserved and no unexpected artifact/secret/canary is present.
- [ ] Use `superpowers:requesting-code-review` for three fresh independent reviews: approved-design coverage, security/redaction/ownership, and concurrency/crash/commit-reconciliation correctness. Resolve every P0/P1 and rerun affected gates.
- [ ] Review checkpoint: report commands, exact pass/fail/skip totals, owned resource IDs removed, any identity-proven residue, high-water measurements, and the still-unproven production `<5s` grab-to-Plex objective. Do not claim PostgreSQL promotion or deployment readiness.

## Design-to-Task Coverage Matrix

| Approved contract | Implemented/proved in |
| --- | --- |
| Strict descriptor, Npgsql pin, no diagnostics | Tasks 2, 4, 5, 20 |
| Five-way time-zone and exact target identity | Tasks 4, 5, 9, 20 |
| Read-only representability and staging ceiling | Tasks 3, 10, 11 |
| Exact catalog/history/bootstrap admission | Tasks 6, 7, 8 |
| Advisory fence and uncertain commits | Tasks 7, 8, 9, 17, 18, 20 |
| Async single-frame parser and safe abort | Tasks 1, 10 |
| 32 MiB accounting and current-batch spill | Tasks 3, 10-16, 18-21 |
| Concrete row/field layout gates | Tasks 12, 21 |
| 8 MiB opaque allocation-volume gate | Tasks 10, 12-13, 15-16, 18-21 |
| Direct binary/text COPY and bootstrap rules | Tasks 13, 14, 20 |
| Canonical source and independent target digests | Tasks 11, 14, 17 |
| Private bounded blob stage and fresh reproof | Tasks 15, 16, 17, 20 |
| Failure/cancellation/crash lifecycle | Tasks 1, 9, 12-20 |
| No runtime/public/provider promotion | Tasks 0, 19, 21 |
| Owned PG16.14 stderr/durability completion proof | Task 20 |

## Final Acceptance Checklist

- [ ] All eleven design acceptance criteria are backed by a named test/gate above.
- [ ] No placeholder, TODO, skipped required test, or “similar to” implementation remains.
- [ ] Every type/interface used across tasks has one owner and consistent signature.
- [ ] The completion run owns and removes only its generated resources and has zero skipped PostgreSQL completion tests.
- [ ] The full backend and Phase 1-3 regression suites remain green.
- [ ] PostgreSQL is still disabled; SQLite and one-control-owner remain the production topology.
- [ ] The successful output is only an opaque private `database-verified` stage; nothing is published or runnable.
- [ ] No staging, commit, push, deployment, database, container, or service mutation occurs without the authority specified for the execution step.
