# Foundation Task 5 Report: Persist Completed-Symlink Import Receipts

## Status

Implementation, Task 5-focused provider checks, independent review, and the
required complete backend invocation are green. The narrow Task 5 repair is
complete. This result does not authorize deployment against a
migration-created PostgreSQL schema; the separate physical-schema blocker below
remains open.

Authoritative current evidence:

| Gate | Result | Status |
| --- | --- | --- |
| Task 5-focused SQLite | 60 passed, 0 failed | Green |
| Task 5-focused live PostgreSQL | 4 passed, 0 failed | Green, with the migration limitation below |
| Cross-process isolation stress | Two concurrent testhosts, 678 passed and 8 expected PostgreSQL skips each | Green |
| Required one-process backend suite with live PostgreSQL | 694 passed, 0 failed, 0 skipped | Green |

## Second-Review Repair

- Removed the unintentional `HistoryItem` and `HistoryCleanupItem` string/text
  conversions and restored the provider-neutral migration snapshot. No schema
  migration was added.
- Replaced history and cleanup `ToString()` predicates with native `Guid`
  collection predicates in bounded chunks of 500.
- Added one receipt terminalization CAS command per chunk. Removing 1,201
  history rows now issues exactly three `ImportReceipts` update commands.
- Added a caller-owned transaction savepoint before receipt, history, or cleanup
  mutation. Transactions without savepoint support fail before mutation.
- On every nested exception, rollback now uses `CancellationToken.None`, restores
  the pre-call tracker values, states, per-property modified flags, and temporary
  flags, and detaches nested-only entries. Caller-owned failures and unverified
  controller-owned failures rethrow the operation failure. A rollback, savepoint
  release, or tracker-restoration failure can replace that operation exception;
  cleanup exceptions are not aggregated by this narrow repair.
- A controller-owned `DbUpdateConcurrencyException` returns idempotent success
  only when its non-empty entries are requested `HistoryItem`/`DavItem`
  deletions and fresh no-tracking reads prove histories absent, every matching
  receipt `Removed`, and cleanup either adequately queued or already completed.
  Verified duplicate success does not send a second websocket event.
- Receipt batching no longer detaches caller-tracked receipts on success.
  Persisted receipts reconcile removal-owned terminal fields from durable state
  while preserving unrelated modified properties; Added target receipts are
  inserted as terminal `Removed`; Deleted or identity-mutated target receipts
  fail before mutation; and a missing tracked durable row raises concurrency so
  the savepoint can roll back the whole batch.
- Replaced the existing-row `SaveChangesInterceptor` contention gates with
  bounded `DbCommandInterceptor` barriers around the actual receipt CAS
  `ExecuteUpdate` commands on SQLite and PostgreSQL. The insert-race tests retain
  their appropriate `SaveChangesInterceptor` barrier.

## TDD Evidence

### Historical Existing-Schema RED

Command:

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~GetHistoryControllerTests|FullyQualifiedName~ImportReceiptConcurrencyTests|FullyQualifiedName~ImportReceiptServiceTests|FullyQualifiedName~CompletedSymlink"
```

Result: **67 failed, 12 passed, 0 skipped, 79 total**. The primary failure was
`PendingModelChangesWarning`; the runtime model and snapshot contained broad,
unmigrated history string conversions.

### Historical New Failure-Semantics RED

The new transaction and schema tests initially produced **4 failed, 0 passed**:

- caller-owned `DbUpdateConcurrencyException` was swallowed;
- caller-owned non-concurrency `DbUpdateException` left receipt/history/cleanup
  state committable after the request token was cancelled;
- history properties exposed `string` provider types; and
- model comparison reported four pending `AlterColumnOperation` instances.

A separate exact-tracker test then failed because restoring only
`EntityState.Modified` marked every non-key property modified instead of
preserving the caller's single modified property.

### Follow-up Controller Idempotency RED

Command:

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~RemoveFromHistoryTransactionTests.ControllerOwnedVerifiedDuplicateReturnsSuccessWithoutSecondBroadcast"
```

Result: **2 failed, 0 passed, 0 skipped**. Both the still-queued-cleanup and
already-completed-cleanup cases rethrew the natural requested-row
`DbUpdateConcurrencyException`, reproducing the regression of SAB's intentional
controller-owned idempotency contract.

## Focused Verification

### SQLite

Command:

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~GetHistoryControllerTests|FullyQualifiedName~ImportReceiptConcurrencyTests|FullyQualifiedName~ImportReceiptServiceTests|FullyQualifiedName~CompletedSymlink|FullyQualifiedName~RemoveFromHistoryTransactionTests|FullyQualifiedName~ImportReceiptSchemaTests"
```

Final result: **60 passed, 0 failed, 0 skipped**.

This covers caller-owned concurrency and non-concurrency rollback, cancelled
request cleanup, exact unrelated Modified/Added/Deleted tracker preservation,
restoration of the exact pretracked receipt instance after rollback,
unsupported-savepoint fail-fast behavior, verified controller-owned duplicate
success without a second websocket event, propagation of unrelated or
entryless concurrency, and durably-unproven concurrency (including an active
receipt after rollback),
successful preservation of unrelated caller receipt changes, rejection of
stale terminal-field overwrites, Added-receipt terminalization, Deleted or
identity-mutated receipt fail-fast behavior, and missing-row rollback that
leaves the caller transaction usable,
provider-neutral model/snapshot parity,
command-level SQLite CAS overlap, and the 1,201-row bounded batch.

### PostgreSQL 16

A disposable `postgres:16` container named
`nzbdav-task5-postgres-20260711-repair` ran on localhost port `32770`. It was
stopped after verification and removed by Docker's `--rm` policy.

Command:

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=32770;Database=nzbdav;Username=nzbdav;Password=REDACTED' \
  dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~ImportReceiptPostgreSqlConcurrencyTests"
```

Final result: **4 passed, 0 failed, 0 skipped**.

Three tests use the migration-created receipt schema. The caller-owned
savepoint/rollback test intentionally uses a dedicated `SearchPath` schema built
with `EnsureCreated` from the current native model. This result does not validate
the historical PostgreSQL history-table migrations described under Concerns.

## Complete Backend Gate

### Historical Failures Before Isolation Repair

A historical required single-invocation command was run with the live PostgreSQL
connection before the isolation repair and was not green:

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=32770;Database=nzbdav;Username=nzbdav;Password=REDACTED' \
  dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

The first run produced **2 failed, 678 passed**:

- `DatabaseTransferServiceTests.ExportImportJson_RoundTripsApplicationRows`;
- `WorkerJobLeaseTests.CancelWorkerJobsAsync_RequestsCancellationWithoutDestroyingActiveLeaseIdentity`.

Both passed immediately when run individually. A repeated default-parallel run
failed eight different content-index/blob tests with rows, directories, and
temporary blob files removed during their assertions: **8 failed, 672 passed**.

The eight failures were:

- `BlobCleanupServiceTests.ExecuteAsyncDoesNotDeleteBlobStillReferencedByHistoryItem`;
- `BlobCleanupServiceTests.DavItemFileBlobUpdateTriggerQueuesSharedOldBlobOnlyOnce`;
- `BlobCleanupServiceTests.DavItemDeleteTriggerQueuesSharedFileBlobOnlyOnce`;
- `ArrOperationsServiceTests.BuildValidationAsync_ReportsCorrelationCoverageAndIssues`;
- `ContentIndexRecoveryServiceTests.StartupRecovery_StartAsyncDoesNotWaitForLibraryLinkScan`;
- `ContentIndexRecoveryServiceTests.StartupRecovery_DoesNotBringBackDeletedItems_WhenSnapshotWasUpdatedAfterDeletion`;
- `ContentIndexRecoveryServiceTests.SnapshotWriter_PrunesItemsWithMissingMetadata_WhenMetadataRowsDisappear`;
- `WorkerJobLeaseTests.EnqueueWorkerJobsAsync_CanDeferSaveForAtomicQueueCompletion`.

A serial full run removed those shared database/filesystem races and exposed a
second deterministic isolation defect: **12 failed, 668 passed**. The test
`HealthCheckRepairPolicyTests.PerformHealthCheckEnqueuesRepairJobForDefinitiveMissingSegments`
stores the literal `segment-1` in `HealthCheckService`'s static six-hour missing
segment cache. Twelve later stream/cache tests reuse `segment-1`, so they throw
`UsenetArticleNotFoundException` or time out.

The twelve failures were:

- `DavMultipartFileStreamTests.ReadAsyncUsesSparseCacheForSeekBackIntoMultipartChunk`;
- `DavMultipartFileStreamTests.ReadAsyncUsesSegmentSlicesAcrossSegmentBoundaries`;
- `DavMultipartFileStreamTests.ReadAsyncFallsBackToLegacySegmentRangesWhenSlicesAreMissing`;
- `DavMultipartFileStreamTests.ReadAsyncFallsBackToLegacySegmentRangesWhenSlicesAreNull`;
- `DavMultipartFileStreamTests.ReadAsyncThrowsWhenSegmentEndsBeforeSliceRange`;
- `SparseSegmentCacheTests.SegmentFileRangeReaderDoesNotCacheCancelledYencHeaderLookup`;
- `SparseSegmentCacheTests.SegmentFileRangeReaderDoesNotCancelSharedYencHeaderLookupWhenOneWaiterCancels`;
- `SparseSegmentCacheTests.NzbFileStreamUsesSparseCacheForSeekBackIntoCachedChunk`;
- `SparseSegmentCacheTests.SegmentFileRangeReaderCachesYencHeadersAcrossRepeatedSeeks`;
- `SparseSegmentCacheTests.NzbFileStreamStopsCachedReadsAtRequestedEndByte`;
- `SparseSegmentCacheTests.NzbFileStreamThrowsWhenDirectSegmentEndsBeforeDeclaredLength`;
- `SparseSegmentCacheTests.SegmentFileRangeReaderThrowsWhenSegmentEndsBeforeRequestedRange`.

Complete assertion coverage is green when that proven cache poisoner runs in a
fresh process:

```text
Serial suite excluding the poisoner: 679 passed, 0 failed
Excluded poisoner in a fresh process:   1 passed, 0 failed
Total assertion coverage:             680 passed, 0 failed
```

This split evidence was diagnostic, not the required gate. It led to two
test-only isolation repairs:

- the definitive-missing test now uses a per-test GUID segment rather than the
  process-global literal `segment-1`;
- each content-index fixture now owns a GUID-specific temporary config root,
  clears pooled SQLite handles, deletes that root, and restores the prior
  database environment on disposal.

The cache fix produced a serial RED of **5 failed, 667 passed, 8 skipped** and a
serial GREEN of **672 passed, 0 failed, 8 skipped**. Two concurrent testhosts on
the old shared path failed independently (**6 failed** and **3 failed**); after
the unique-root repair, both concurrent hosts passed **678 tests** with **8
expected PostgreSQL skips**, and no fixture roots remained. A separate manual
review found no blocking isolation issue.

### Final Required Invocation

A disposable `postgres:16-alpine` container named
`nzbdav-test-isolation-pg` exposed PostgreSQL on localhost port `32772`. The
single required command was:

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=32772;Database=REDACTED;Username=REDACTED;Password=REDACTED' \
  dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

Final result: **694 passed, 0 failed, 0 skipped** in one testhost. The disposable
container was then removed, and no `content-index-recovery-*` fixture directory
remained.

## Independent Review

The fresh second review found no critical or major issue in the initial Task 5
repair, and its minor report-ambiguity finding was corrected. A narrower manual
contract review then found that propagating every controller-owned concurrency
failure regressed SAB's intentional idempotent delete behavior. The follow-up
repair now admits only a requested deletion with complete durable proof, keeps
caller-owned propagation intact, and tests both the success and rejection
paths. A final review then found that successful caller-owned batching detached
pretracked receipt changes; RED tests reproduced the loss for a persisted
receipt and an Added receipt. Durable terminal-field reconciliation, fail-fast
ambiguity handling, and missing-row rollback tests resolved that finding. The
final narrow re-review found no remaining Task 5 code blocker.

## Concerns And Blockers

1. **Backend test isolation is resolved.** The repaired cache data and
   per-fixture filesystem/database ownership passed serial, concurrent-testhost,
   and final live-PostgreSQL gates. These are test-only changes.
2. **Historical PostgreSQL migrations remain a high-severity deployment
   blocker.** The full migration chain leaves at least 28 identifier-like
   columns as `text` while newer tables and the current runtime model use native
   UUIDs. It also leaves legacy date/boolean/integer store-type mismatches. A
   migrated-schema history predicate fails with PostgreSQL `42883: operator does
   not exist: text = uuid`, and a seeded database export fails reading `text` as
   `Guid`. The focused PostgreSQL savepoint test deliberately uses an isolated
   `EnsureCreated` schema, so neither it nor the 694-test gate proves physical
   migrated-schema compatibility. Task 5 adds no broad conversion or unreviewed
   bridge. Provider-specific migration sets, the presence of real PostgreSQL
   data, and legacy timestamp semantics must be decided before that separate
   repair.

No frontend, Docker application image, release, PR, or deployment result is
claimed in this report.
