# NZBDav transfer-v3 Phase 2: import-state and fail-closed boundary

Date: 2026-07-12

Status: implemented and independently verified; production transfer-v3
export/import remains unavailable.

## Scope and non-goals

This phase builds only the state, authorization, containment, and startup-safety
foundation needed by a later synthetic transfer-v3 implementation.

In scope:

- the exact canonical `database.import-state` model and codec;
- a provider-neutral, single-statement, exact-byte compare-and-swap store;
- containment of the reserved key from generic configuration and legacy v2
  transfer paths;
- an exact backend maintenance-argument parser and an opaque future-v3
  authorization value;
- a SQLite pre-context state-presence guard;
- unconditional PostgreSQL runtime/maintenance refusal before the application
  retrieves/validates the database connection string, constructs a provider
  connection, or performs transfer-path I/O. When the provider is declared only
  inside dotenv, generic dotenv ingestion necessarily happens first and may also
  ingest a connection-string line;
- an unwired helper that preserves a primary import failure if a later failed-state
  CAS also fails.

Explicitly out of scope:

- no transfer-v3 exporter, snapshot reader, importer, manifest, row codec, blob
  publication, or production/operator destination mutation. Disposable test
  fixtures may be mutated only to prove the foundation;
- no successful public `--db-export-v3` or `--db-import-v3` command;
- no entrypoint allowlist or usage text for v3;
- no PostgreSQL runtime, migration CLI, v2 transfer, or v3 transfer activation;
- no SQLite insertion of the `fresh` marker. A later empty-target importer owns
  that bootstrap transaction;
- no durable state transition from the JSONL parser or observer callbacks.

## Frozen canonical state contract

The only valid serialized UTF-8 values are compact ASCII JSON in this exact
property order:

```text
{"formatVersion":3,"state":"fresh"}
{"formatVersion":3,"state":"importing","manifestSha256":"<64 lowercase hex>"}
{"formatVersion":3,"state":"database-verified","manifestSha256":"<64 lowercase hex>"}
{"formatVersion":3,"state":"failed","manifestSha256":"<64 lowercase hex>"}
```

The only legal transitions are:

```text
fresh -> importing(A)
importing(A) -> database-verified(A)
importing(A) -> failed(A)
```

Every self-transition, digest switch, retry, rewind, terminal-state transition,
or missing/malformed/noncanonical current value changes zero rows. The CAS store
never inserts or upserts.

## Frozen command boundary

The pure backend parser recognizes only:

```text
[]
--db-migration [one non-option target]
--db-export-json one-non-option-path
--db-import-json one-non-option-path [--replace]
--db-export-v3 one-non-option-directory        # classified for future use only
--db-import-v3 one-non-option-directory        # classified for future use only
```

Any other nonzero argv is invalid. A path/target must be nonempty, not
whitespace, and must not begin with `-`. Duplicate, mixed, reordered, or extra
arguments are invalid.

Only the exact future `--db-import-v3` form may mint an internal opaque import
authorization object in the pure parser; future export classification never
mints import authority. `Program` always converts either v3 classification to a
stable unavailable error before dotenv, provider, path, context, or network I/O.
The container entrypoint continues to reject all v3 forms with exit code 64.
No environment variable can mint the authorization and no production I/O API
accepts it during Phase 2.

## Task 1: RED tests for the canonical state codec (P2-01, P2-02)

Create:

- `backend.Tests/Database/Transfer/TransferV3ImportStateCodecTests.cs`

The RED tests must prove:

1. all four valid states serialize and parse to the exact bytes above;
2. `fresh` has no digest and all other states have exactly one digest;
3. whitespace, BOM, leading/trailing newline, comments, trailing comma, trailing
   JSON, property reorder, missing/duplicate/unknown/case-changed properties,
   escaped property/state spellings, version 2/4/string/3.0/exponent, `fresh`
   with a digest, non-fresh without a digest, and oversized input are rejected;
4. 63/65-character, uppercase, mixed-case, non-hex, prefixed, whitespace, and
   escaped digests are rejected;
5. failures use stable redacted diagnostics and never contain the raw input or
   digest;
6. the PostgreSQL baseline fresh literal and the native fresh-bootstrap validator
   remain tied to the codec constant by a source/SQL contract test.

Then implement:

- `backend/Database/Transfer/TransferV3ImportState.cs`
- `backend/Database/Transfer/TransferV3ImportStateCodec.cs`

Use a small typed state model whose factories enforce the digest invariant.
The codec must compare against the canonical ASCII shape, impose the maximum
canonical byte length before parsing, and never rely on permissive default JSON
deserialization followed by normalization.

## Task 2: RED tests for exact-byte CAS and transition ownership (P2-03, P2-04)

Create:

- `backend.Tests/Database/Transfer/TransferV3ImportStateStoreTests.cs`
- `backend.Tests/Database/Transfer/TransferV3ImportStateStorePostgreSqlTests.cs`

The tests must cover:

1. the complete state-transition matrix and same/different-digest matrix;
2. missing, deleted, malformed, noncanonical, wrong-storage-type, and oversized
   current rows return zero and remain unchanged;
3. the store does not expose a database read/materialization API and never
   selects an unbounded `ConfigValue`;
4. two independent SQLite contexts racing `fresh -> importing`, with the same
   and different digests, produce exactly one winner and one zero result;
5. the same race passes against an owned disposable PostgreSQL 16 database;
6. the SQL predicate byte-matches both `ConfigName` and `ConfigValue` so a
   nondeterministic/collation-equal but byte-different text value cannot win;
7. an active EF transaction, raw ADO transaction/open caller connection, ambient
   `TransactionScope`, or tracked reserved `ConfigItem` is rejected before
   issuing the UPDATE;
8. a successful transition is visible immediately from a newly opened context;
9. the full SQLite CAS invocation, including opening the owned local connection,
   terminates within the declared connection/default/command-timeout and retry
   bounds under lock contention; cancellation is observed between retries without
   changing the old bytes;
10. a PostgreSQL row lock held by a second connection terminates the CAS within
    its positive command-timeout bound and leaves the old bytes unchanged; an
    unreachable endpoint terminates within the separately capped positive
    connection-open timeout;
11. a raw transaction begun directly through the caller context's ADO connection,
    plus an already-open caller connection without a transaction, are both
    rejected; the store neither commits, rolls back, closes, nor otherwise
    mutates that caller connection/transaction;
12. PostgreSQL and SQLite commands are parameterized and no digest appears in
    command text or diagnostics.

Then implement:

- `backend/Database/Transfer/TransferV3ImportStateStore.cs`

Implementation constraints:

- accept only typed states produced by the codec/model;
- reject illegal transitions before database access;
- use one atomic auto-commit UPDATE and affected-row equality; never perform a
  read-then-write, insert, upsert, or multi-statement state transaction;
- construct the store from a closed provider context only to capture provider and
  connection configuration, then execute every CAS through a dedicated, fresh,
  nonpooled store-owned connection. Never execute on the caller context's
  connection;
- at construction and before every CAS, reject an ambient transaction,
  `Database.CurrentTransaction`, any non-Closed caller connection (which also
  catches a raw ADO transaction unknown to EF), and a tracked exact reserved-key
  entity. Leave all caller state untouched on refusal;
- disable ambient enlistment on the owned connection as defense in depth and
  issue exactly one auto-commit UPDATE; close/dispose the owned connection before
  returning;
- SQLite must match exact key/value UTF-8 bytes with BLOB predicates, including
  storage/length checks, and use a short explicit `SqliteCommand.CommandTimeout`
  plus a short connection `DefaultTimeout` and bounded busy/locked retries. The
  test measures the full invocation including owned-connection open. Microsoft.Data.Sqlite
  async calls execute synchronously and command cancellation is not an interrupt,
  so cancellation alone is not the wait bound;
- PostgreSQL must compare `convert_to(text, 'UTF8')` with `bytea` parameters for
  both key and expected value, not collated text equality. Set a positive
  `NpgsqlCommand.CommandTimeout` and cap `NpgsqlConnectionStringBuilder.Timeout`
  on the cloned owned connection; do not inherit unlimited/excessive caller
  defaults for command execution or connection establishment;
- expose CAS only. Do not add a database state-read API in Phase 2. If a later
  read is added, SQL or a sequential reader must cap it at
  `maxCanonicalBytes + 1` before client allocation.

Update the native fresh-bootstrap validation query in:

- `backend/Database/PostgreSqlNativeMigrator.cs`

so the reserved key/value check is exact-byte based. Do not activate the native
migrator through `Program`.

## Task 3: RED tests for primary-error preservation (P2-13)

Create:

- `backend.Tests/Database/Transfer/TransferV3ImportFailurePolicyTests.cs`

The tests use a real state-store row and prove the true, false, and distinct
throwing CAS callback paths:

- true runs exactly once, changes the row to exact `failed(A)`, attaches no
  secondary diagnostic, and still rethrows the identical primary exception;
- false and a callback that throws before issuing the UPDATE leave the row
  byte-exactly `importing(A)` and attach only a stable sanitized secondary
  diagnostic;
- a successful CAS followed by a synthetic acknowledgement/connection-loss throw
  leaves exact `failed(A)` and attaches a stable sanitized secondary diagnostic;
- a genuinely ambiguous store exception may leave either exact `importing(A)` or
  exact `failed(A)`. Both are intentionally non-usable; the helper never reads,
  repairs, retries, or rewinds the marker;
- every path rethrows the identical primary exception object with its original
  dispatch information;
- never expose the state bytes or digest.

Then add an internal, unwired helper:

- `backend/Database/Transfer/TransferV3ImportFailurePolicy.cs`

It accepts an already captured primary exception and one `Func<Task<bool>>`
failed-state CAS callback. It preserves the primary regardless of the callback
outcome and makes no claim that a thrown database acknowledgement identifies
whether the atomic UPDATE committed. It is not referenced by `Program`, the JSONL
parser, an observer, or a callable importer in Phase 2.

## Task 4: central reserved-key policy and generic read/write containment (P2-05, P2-06, P2-07)

Create:

- `backend/Database/Transfer/TransferV3ReservedConfigPolicy.cs`

Use exact ordinal comparison for the one reserved key.

Extend tests in:

- `backend.Tests/Api/GetConfigControllerTests.cs`
- `backend.Tests/Api/UpdateConfigControllerTests.cs`
- `backend.Tests/Config/ConfigManagerConcurrencyTests.cs`

Required RED cases:

1. GET reserved-only and mixed requests, with `include-secrets` both false and
   true, never return the row;
2. generic update reserved-only and mixed requests return 4xx before the first
   database query, change neither ordinary nor reserved values, and emit no
   config event;
3. `ConfigManager.LoadConfig` neither materializes nor caches the reserved row;
4. direct `ConfigManager.UpdateValues` with reserved-only or mixed input rejects
   the whole batch before cache mutation or event emission.

Then modify:

- `backend/Api/Controllers/GetConfig/GetConfigController.cs`
- `backend/Api/Controllers/UpdateConfig/UpdateConfigController.cs`
- `backend/Config/ConfigManager.cs`

Apply query-time filtering plus an in-memory exact-ordinal defense on generic
read/load paths. Validate a complete update batch before deduplication, query,
tracking, mutation, save, or event work.

## Task 5: close tracked and raw database-write bypasses (P2-06 defense in depth)

Extend/new tests under:

- `backend.Tests/Database/Transfer/TransferV3ReservedConfigContainmentTests.cs`

Required RED cases:

1. sync and async `SaveChanges` reject Added, Modified, and Deleted reserved
   entities, including a key changed from/to the reserved key;
2. a rejected batch containing an ordinary config row changes zero database rows;
3. a rejected batch with a pending NZB/RAR/multipart blob writes zero blob bytes
   and stages no rclone invalidation;
4. a source/callsite contract permits the dedicated CAS and historical migrations,
   but rejects any other production raw/`ExecuteUpdate`/`ExecuteDelete` mutation
   of `ConfigItems`. The v2 filtered delete is the one explicit operational
   exception.

Then modify:

- `backend/Database/DavDatabaseContext.cs`

The reserved-key preflight must be the first operation inside both sync and async
`SaveChangesWithBlobsAndInvalidations` paths, before worker validation, BlobStore
writes, DAV inspection, or rclone staging. Check both current and original key
values for changed/deleted entities. The dedicated raw CAS intentionally bypasses
EF tracking and this guard.

## Task 6: contain the reserved key in legacy v2 transfer (P2-08)

Extend:

- `backend.Tests/Database/DatabaseTransferServiceTests.cs`
- `backend.Tests/Database/DatabaseMigratorPreflightTests.cs`

Required RED cases:

1. SQLite v2 export omits the marker and adjusts `TotalRows` accordingly;
2. a v2 input containing the key is rejected after input decoding but before
   migration, target query, transaction, or mutation;
3. v2 replace preserves the target-local marker and deletes other application
   config;
4. target emptiness ignores only the exact reserved key;
5. direct PostgreSQL v2 export and import refuse before query, path normalization,
   directory creation, input open, or output creation, with zero connection opens.

Then modify:

- `backend/Database/DatabaseTransferService.cs`

The provider/type refusal must be the first executable guard in both public v2
methods. Export filters the row at query time and again by exact ordinal comparison.
Import rejects any occurrence before migration/target access. The replace delete
must use an explicit non-reserved predicate rather than clearing all config rows.

## Task 7: exact backend parser and unavailable future capability (P2-11, P2-12)

Create:

- `backend/Hosting/MaintenanceCommandLine.cs`
- `backend.Tests/Hosting/MaintenanceCommandLineTests.cs`

Required RED cases:

1. accept exactly the frozen forms and preserve path/target text;
2. reject unknowns, case/prefix variants, duplicates, two commands, extras,
   misplaced/duplicate `--replace`, blank/whitespace arguments, any leading-`-`
   target/path, and standalone `--`;
3. classify exact future-v3 forms and mint an opaque internal import authorization
   only for the exact `--db-import-v3` form;
4. malformed v3 cannot mint authorization and no environment value can do so;
5. a source/reflection contract proves no Phase 2 production I/O API consumes
   the authorization.

Modify:

- `backend/Program.cs`

Parse argv before `EnvironmentUtil.LoadDotEnvFile` and replace every
`args.Contains`, `IndexOf`, and first-match maintenance branch with discriminated
dispatch. Exact future v3 classifications throw the unavailable error immediately.
Malformed/nonmatching argv throws the stable usage error immediately. Zero argv
alone reaches normal hosting.

Immediately after pure parsing/v3 refusal, inspect only the already-present
process environment for a PostgreSQL provider and fail before dotenv access.
After dotenv loading, repeat the PostgreSQL refusal check so a provider declared
only by dotenv is also blocked. A provider declared only inside dotenv cannot be
known without reading that file; document this limit instead of claiming otherwise.

Do not add an authorized PostgreSQL context factory or change
`DavDatabaseContextRuntimeFactory` in Phase 2.

## Task 8: entrypoint prevalidation before filesystem/user mutations (P2-12)

Extend:

- `tests/test_entrypoint_contract.sh`

Required RED cases:

1. shell/backend grammar parity for all legacy valid and adversarial invalid forms;
2. exact and malformed v3 forms return 64 and are absent from usage text;
3. invalid argv is rejected at the top of `main` before UID/GID creation, random
   key generation, `/data` creation, config chown, database ownership inspection,
   or child execution;
4. a bind-mounted/config fixture retains ownership, mode, and mtime after an
   invalid invocation.

Modify:

- `entrypoint.sh`

Prevalidate nonempty argv at the top of `main`; retain the defense inside
`run_maintenance`. Reject target/path arguments beginning with any `-`, not only
`--`. Keep v3 out of `maintenance_usage` and the allowlist.

## Task 9: SQLite state-presence guard before context/maintenance side effects (P2-09, P2-10)

Create:

- `backend/Database/Transfer/TransferV3StartupGuard.cs`
- `backend.Tests/Database/Transfer/TransferV3StartupGuardTests.cs`

Required RED cases:

1. no database file, an empty/historical database without `ConfigItems`, and a
   current SQLite database without the marker are allowed;
2. canonical fresh/importing/database-verified/failed and malformed/oversized
   marker values all refuse based only on exact-key presence;
3. the oversized test proves the guard executes `SELECT 1` and never requests or
   materializes `ConfigValue`;
4. allowed/refused checks leave application rows and the main database file bytes
   unchanged. Read-only WAL access may create/use `-shm`/`-wal`; do not claim
   sidecar immutability;
5. a corrupt or incompatible `ConfigItems` schema fails closed with a redacted
   diagnostic.

Implementation constraints:

- first check file existence so a new database is not created;
- use a nonpooled raw `Microsoft.Data.Sqlite` read-only connection, private cache,
  `query_only`, a short connection/default timeout, and a bounded per-command
  timeout; do not use the normal EF
  interceptor that sets WAL/FULL;
- apply the bound to open/WAL initialization as well as `query_only`, schema
  inspection, and presence commands; a held-lock test must prove the complete
  guard returns or fails within the declared bound;
- inspect `sqlite_schema`, then use an exact key-byte predicate and `SELECT 1`;
  never select `ConfigValue`;
- do not use `immutable=1`, because it can ignore live WAL content.

Invoke the guard from `backend/Program.cs` after the pinned in-memory SQLite
runtime/version gate but before ThreadPool configuration, Serilog construction or
emission, `BlockUpgradesToV06X`, runtime-context construction, migration, vacuum,
legacy input/output, config load, or web-host construction. Hold the validated
runtime record and log it only after the guard passes. The native in-memory runtime
gate is the one explicitly documented prerequisite and must use
`CancellationToken.None` so it does not initialize the process SIGTERM hook before
the target guard; the invariant is otherwise
"before any target, application, or maintenance side effect." Add a source/process
ordering canary. Since all v3 commands are refused earlier in Phase 2, every
SQLite runtime/maintenance path is guarded.

## Task 10: process-level PostgreSQL and side-effect canaries (P2-11)

Extend:

- `backend.Tests/Hosting/NzbdavRoleStartupTests.cs`

Required process tests:

1. PostgreSQL normal, migration, v2 export, v2 import, and v2 import+replace all
   refuse before `NZBDAV_DATABASE_CONNECTION_STRING` access, connection creation,
   native migration, input open, or output parent creation;
2. exact future v3 and malformed/extra v3 refuse before dotenv/provider/path I/O;
3. invalid argv and every valid process-environment PostgreSQL normal/v2 command,
   with `NZBDAV_ENV_FILE` naming a FIFO, exit within a bounded time without
   opening the FIFO;
4. a SQLite database containing any marker refuses normal, migration, v2 export,
   and v2 import before upgrade inspection, vacuum, input/output, config load, or
   Kestrel startup;
5. absent-marker SQLite preserves current successful startup/migration behavior.

Keep the existing post-dotenv unconditional PostgreSQL gate and add the
process-environment pre-dotenv gate in `Program`, both before any runtime factory
or application retrieval/validation of `NZBDAV_DATABASE_CONNECTION_STRING`.
Provider discovery from dotenv necessarily reads the generic dotenv file first;
neither gate may construct a connection or inspect a PostgreSQL state row in
Phase 2.

## Task 11: documentation convergence

Modify:

- `docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md`
- `docs/superpowers/plans/2026-07-11-nzbdav-runtime-migration-safety.md`
- `backend/Database/Transfer/Contracts/README.md`

Replace every legacy colon-form import-state example with the frozen canonical
JSON contract. Record that Phase 2 supplies only state/authorization/containment
foundations and keeps every v3 command unavailable.

## Task 12: focused and full verification

Run RED first for every task and record the expected failure. After GREEN:

1. focused codec, state-store, failure-policy, containment, parser, startup-guard,
   v2 transfer, and process tests;
2. `tests/test_entrypoint_contract.sh`;
3. PostgreSQL state-store tests against a newly created, uniquely named disposable
   PostgreSQL 16 container/schema owned by this task, including the held-row-lock
   timeout and raw/open-caller-connection cases. Do not access or stop
   `nzbdav-pg-gate-1783815319` or any other pre-existing container;
4. run the complete PostgreSQL-first TRX suite under the owned PG environment and
   require zero skips and zero failures, then run the complete backend suite with
   zero unexpected skips/failures and warn-as-error builds;
5. `dotnet format --verify-no-changes` for production and tests; do not run a
   bulk formatter that writes across the dirty shared tree;
6. the existing four-cell Linux native Transfer-v3 matrix plus macOS arm64 focused
   tests if Phase 1 files are affected;
7. frontend pinned-Node install/audit/typecheck/tests/client+SSR/server builds only
   if shared frontend files changed;
8. Python release/runtime/TRX tests, `actionlint`, YAML/JSON validation, and
   `git diff --check`;
9. remove only the disposable resources created by this task.

Then request an independent Phase 2 specification/code-quality review. Phase 3
must not begin until that review passes and its separate snapshot/blob/target
safety decisions are explicitly resolved.

## Acceptance map

| ID | Acceptance evidence |
|---|---|
| P2-01 | Exact four canonical serialized values and round trips |
| P2-02 | Full adversarial noncanonical/oversized rejection matrix |
| P2-03 | Only three legal CAS edges; every other edge changes zero |
| P2-04 | SQLite and disposable-PG concurrent CAS produce one winner |
| P2-05 | Reserved row never returned by generic GET |
| P2-06 | Mixed generic/tracked writes reject atomically before side effects |
| P2-07 | ConfigManager never loads, caches, or emits the reserved row |
| P2-08 | v2 omits/rejects/preserves the key and PG v2 fails before I/O |
| P2-09 | Missing SQLite state is allowed without target mutation |
| P2-10 | Any present SQLite state blocks all normal/non-import paths early |
| P2-11 | Every PG public path fails before app connection-string use, connection, input, or output; dotenv-only provider discovery is documented |
| P2-12 | Exact parser/capability is unforgeable and all public v3 remains refused |
| P2-13 | Secondary failed-state CAS cannot replace the original import failure |
