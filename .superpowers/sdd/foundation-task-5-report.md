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
- Added idempotent receipt staging and state transitions. `Removed` is terminal;
  reconciliation never restores a receipt to `Available`.
- Staged `Available` file receipts immediately before the existing queue
  completion `SaveChangesAsync`, keeping history, DAV output, worker jobs,
  invalidations, and receipts in one EF transaction. Failed history does not
  stage receipts.
- Replaced the 30-second process cache with persisted WebDAV claims. A failed
  claim commit returns `ServiceUnavailable`; successful unlink returns
  `NoContent` without deleting the underlying DAV item.
- Advanced correlated ARR Import/Download receipts to `Imported` in the same
  save as the lifecycle event. SAB history removal persists `Removed` before
  history/DAV cleanup is staged.
- Added control/all hosted reconciliation every five minutes. Each run handles
  at most 100 claims older than five minutes, confirms organized-library links
  through `OrganizedLinksUtil`, and moves unresolved 30-minute claims to
  `NeedsReview`.
- Added receipts to database export, import, replacement clear order, empty
  target detection, and total row counts. Version-1 snapshots default receipts
  to an empty list.

## Verification

- Focused receipt/WebDAV/ARR/history/queue/transfer suite: **56 passed, 0 failed,
  0 skipped**.
- Full backend suite with
  `NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=55435;Database=nzbdav;Username=nzbdav;Password=REDACTED'`:
  **639 passed, 0 failed, 0 skipped**.
- `git diff --check`: clean.

## Self-Review

- **Restart safety:** receipt-backed visibility was tested after recreating both
  the database context and completed-symlink store.
- **Idempotency:** repeated claims return the same receipt; repeated lifecycle
  transitions do not move terminal `Removed` receipts backward.
- **Transaction behavior:** queue completion stages without saving; ARR import
  commits lifecycle and receipt together; history removal durably marks receipts
  before cleanup staging.
- **Reconciliation isolation:** a linked file only advances its exact receipt,
  even when another claimed file shares the same history ID.
- **Provider compatibility:** the full migration chain completed in the supplied
  PostgreSQL test run, while focused tests exercised the default SQLite path.

## Concerns

None outstanding.
