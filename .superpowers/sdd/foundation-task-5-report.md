# Foundation Task 5 Report: Persist Completed-Symlink Import Receipts

## Status

Implemented and verified.

Completed-symlink unlink claims are now durable database receipts. Unlinking a
generated symlink does not delete its underlying `/content` media, and claimed
items stay hidden after database context and store recreation.

## Implementation

- Added the additive `ImportReceipts` schema, EF model configuration, indexes,
  migration, and model snapshot update. DAV and history IDs are scalar values
  with no foreign keys, so receipts survive source-row cleanup.
- Added idempotent receipt staging and database-conditional state transitions.
  Competing claims use compare-and-set updates, provider-specific unique losers
  reload the durable receipt, and `Removed` is terminal.
- Staged `Available` file receipts immediately before the existing queue
  completion `SaveChangesAsync`, keeping history, DAV output, worker jobs,
  invalidations, and receipts in one EF transaction. Failed history does not
  stage receipts.
- Replaced the 30-second process cache with persisted WebDAV claims. A failed
  claim commit returns `ServiceUnavailable`; successful unlink returns
  `NoContent` without deleting the underlying DAV item.
- Advanced correlated ARR Import/Download receipts to `Imported` in the same
  transaction as the lifecycle event, including download-ID-only events that
  resolve history and media IDs from an existing correlation. SAB history
  removal commits receipt and cleanup changes atomically.
- Added All-role hosted reconciliation every five minutes. Control is not yet
  executable in this plan; later host activation will move the registration.
  Each run handles at most 100 claims, enumerates organized links once, and
  moves unresolved 30-minute claims to `NeedsReview`.
- Added receipts to database export, import, replacement clear order, empty
  target detection, and total row counts. Version-1 snapshots default receipts
  to an empty list.

## Verification

- Focused receipt/WebDAV/ARR/history/queue/transfer suite: **90 passed, 0 failed,
  0 skipped**.
- Live PostgreSQL receipt concurrency suite: **3 passed, 0 failed, 0 skipped**.
- Full backend suite with
  `NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=55435;Database=nzbdav;Username=nzbdav;Password=REDACTED'`:
  **673 passed, 0 failed, 0 skipped**.
- `git diff --check`: clean.

## Self-Review

- **Restart safety:** receipt-backed visibility was tested after recreating both
  the database context and completed-symlink store.
- **Idempotency and concurrency:** repeated claims return the same receipt;
  competing callers report one change, unique insert losers reload, and stale
  transitions cannot move terminal `Removed` receipts backward.
- **Transaction behavior:** queue completion stages without saving; ARR import
  commits lifecycle and receipt together; history removal commits receipt and
  cleanup together. Forced save failures leave no partial durable state.
- **Reconciliation isolation:** a linked file only advances its exact receipt,
  even when another claimed file shares the same history ID.
- **Provider compatibility:** the full migration chain completed in the supplied
  PostgreSQL test run, while focused tests exercised the default SQLite path.

## Concerns

None outstanding.

## Review Fix RED/GREEN Evidence

### RED

- SQLite concurrency reproduction: **0 passed, 5 failed**. Both existing-row
  claimers reported `Changed=true`; the legacy insert loser leaked a unique
  violation; stale Imported, NeedsReview, and claim writes overwrote `Removed`.
- Official download-ID-only ARR reproduction: **0 passed, 2 failed**. The
  existing correlation's effective `HistoryItemId` was overwritten with null,
  so lifecycle and receipt advancement lost correlation.
- Reconciliation counting seam initially failed to compile because no injected
  traversal existed, matching the per-receipt traversal design.
- Forced transaction failures: queue remained atomic, while ARR and SAB failed
  **2 of 3** rollback assertions because receipt CAS changes committed before
  the later save failed.

### GREEN

- SQLite receipt concurrency, stale-state, race, and unrelated-failure tests
  pass through the provider-enabled focused suite.
- Live PostgreSQL claim/update concurrency: **3 passed, 0 failed, 0 skipped**.
- Official Import/Download correlation tests preserve history, download, and
  media IDs and advance the matching receipt.
- Reconciliation processes 100 receipts with exactly one organized-link
  traversal.
- Queue, ARR, and SAB forced-failure boundaries: **3 passed, 0 failed**.
- Final focused suite: **90 passed, 0 failed, 0 skipped**.
- Final full backend suite: **673 passed, 0 failed, 0 skipped**.
