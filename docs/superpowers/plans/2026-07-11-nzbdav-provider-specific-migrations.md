# NZBDav Provider-Specific PostgreSQL Migration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use
> `superpowers:subagent-driven-development` or `superpowers:executing-plans`.
> Apply this plan task-by-task, use TDD, and request independent review at every
> commit boundary.

**Goal:** Replace the unsafe shared SQLite-scaffolded migration history with
provider-specific histories, create a guarded native PostgreSQL 16 baseline,
and prove a bounded transfer from the real SQLite deployment without changing
the verified meaning of its legacy timestamps.

**Timestamp decision:** Preserve the existing contract. `DavItems.CreatedAt`,
`QueueItems.CreatedAt`, `QueueItems.PauseUntil`, and
`HistoryItems.CreatedAt` are deployment-local wall-clock values. Keep their
.NET type as `DateTime`, keep SQLite's physical text unchanged, and map them to
PostgreSQL `timestamp without time zone`. A conversion to UTC instants is a
separate product/data-migration decision and is intentionally out of scope.

**Architecture:** Keep one shared EF model/behavior base, but use concrete
SQLite and PostgreSQL contexts with independent migration identities and
snapshots. The inherited 49 migrations remain SQLite-only and byte-for-byte
unchanged in their `Up`/`Down` operations. PostgreSQL receives a native
greenfield baseline plus one operational-object migration. Every production
migration call goes through a preflight service before EF can create a history
table. Transfer v3 is a private, framed directory snapshot with bounded
keyset-paged export/import, raw-source validation, per-table rolling digests,
and fail-and-recreate semantics for a partial PostgreSQL target.

**Tech stack:** .NET 10, EF Core 10.0.9, Npgsql EF Core 10.0.2,
Microsoft.Data.Sqlite, PostgreSQL 16, xUnit 2.9.2, Docker, Python 3.9+.

**Phase 3 checkpoint (2026-07-14):** The repository now implements the
transfer-v3 safety foundations plus the complete private SQLite source
snapshot, independent offline verifier, and typed sealed reconstruction stage.
Transfer-v3 commands and normal PostgreSQL runtime remain intentionally
unavailable. The importer, target mutation, ownership transfer, and activation
work described below remains deferred to later, separately reviewed phases.

---

## Approval And Execution Gates

The user has confirmed that PostgreSQL has no real application data. A real
SQLite deployment does exist: the read-only 2026-07-11 check found provider
`sqlite(default)`, container `TZ=Asia/Dubai`, a 5,156,151,296-byte
`/config/db.sqlite`, and active WAL/SHM files.

Do not execute Task 2 or later until the user confirms that the PostgreSQL
target is a dedicated NZBDav database/schema that may be dropped and recreated
during rehearsal. Do not infer this merely from the absence of PostgreSQL data.

Additional gates:

- Preserve the four local-wall fields exactly. Historical timezone continuity
  is not required because this plan does not reinterpret them as instants.
- The candidate container must retain the source deployment's measured current
  timezone at cutover. Rehearsal uses `TZ=Asia/Dubai`; remeasure at cutover.
- Set an explicit `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE` and require it to equal the
  application local zone. PostgreSQL connections must set the Npgsql `Timezone`
  session parameter to the same IANA zone; translated `DateTime.Now` otherwise
  follows the database session zone rather than the application host zone.
- PostgreSQL timestamps have one-microsecond resolution while .NET/SQLite can
  retain 100-nanosecond ticks. The private-source preflight must prove the four
  legacy columns have no sub-microsecond remainder. If any do, stop for an
  explicit rounding-or-schema decision; do not silently lose precision.
- Before source downtime, produce a filesystem-capacity ledger for the private
  work tree, PostgreSQL data volume/tablespace, and target blob filesystem using
  available-block (`statvfs`), quota, and tested headroom—not total/free disk
  guesses. Abort before copying or target mutation if every phase and its
  rollback copy cannot fit.
- Never open the live SQLite source. Use the stopped-source byte copy and
  WAL-correct private backup process in
  `docs/superpowers/plans/2026-07-11-nzbdav-runtime-migration-safety.md`.
- Do not repair or preserve a PostgreSQL schema made by the old shared
  migration chain. Reject it and drop/recreate the disposable target.
- Do not run a real transfer, deploy, push, or open a PR as part of this plan's
  design/review phase.
- If the user later chooses UTC instants, stop and author a separate plan with
  explicit historical-zone evidence, ambiguity policy, API compatibility, and
  rollback. Do not amend this plan in place during execution.

---

## Verified Failure Inventory

The inherited shared chain now applies 48 of 49 migrations before failing on
`20260712123000_Add-Arr-Import-Commands`: it exposes 224 application columns,
while the current native model exposes 240 columns across 28 entities. The 16
missing columns are the complete `ArrImportCommands` table, and 51 physical
definitions among the existing columns differ. For the
four legacy timestamp fields, neither inherited `text` nor Npgsql's default
`timestamptz` is the desired PostgreSQL contract; the baseline must explicitly
use `timestamp without time zone`.

| Mismatch | Count | Exact columns |
| --- | ---: | --- |
| `text` instead of `uuid` | 28 | `ArrDownloadCorrelations.{Id,QueueItemId,HistoryItemId}`; `ArrDownloadLifecycleEvents.{Id,QueueItemId,HistoryItemId}`; `ArrSearchNudgeCommands.Id`; `BlobCleanupItems.Id`; `DavCleanupItems.Id`; `DavItems.{Id,ParentId,FileBlobId,HistoryItemId,NzbBlobId}`; `DavMultipartFiles.Id`; `DavNzbFiles.Id`; `DavRarFiles.Id`; `HealthCheckResults.{Id,DavItemId}`; `HistoryCleanupItems.Id`; `HistoryItems.{Id,DownloadDirId,NzbBlobId}`; `NzbBlobCleanupItems.Id`; `NzbNames.Id`; `QueueItems.Id`; `QueueNzbContents.Id`; `QueuePriorityHints.QueueItemId` |
| `integer/int4` instead of `bigint/int8` | 10 | `DavItems.{FileSize,LastHealthCheck,NextHealthCheck,ReleaseDate}`; `HealthCheckResults.CreatedAt`; `HealthCheckStats.{DateStartInclusive,DateEndExclusive}`; `HistoryItems.TotalSegmentBytes`; `QueueItems.{NzbFileSize,TotalSegmentBytes}` |
| `text` instead of local-wall timestamp | 4 | `DavItems.CreatedAt`; `HistoryItems.CreatedAt`; `QueueItems.{CreatedAt,PauseUntil}` |
| `integer` instead of `boolean` | 2 | `ArrDownloadCorrelations.ManualLock`; `HistoryCleanupItems.DeleteMountedFiles` |
| Lost length contract | 3 | `Accounts.Username` (`varchar(255)`); `ArrDownloadCorrelations.Source` (`varchar(32)`); `DavItems.Name` (`varchar(255)`) |
| Unwanted legacy default | 4 | `DavItems.IdPrefix`; `DavItems.Path`; `DavItems.SubType`; `RcloneInvalidationItems.Revision` (`DEFAULT 1` retained by the shared chain but absent from the runtime model) |

Additional verified drift and invariants:

- The desired native schema has 36 application PK/FK constraints and 84
  PostgreSQL indexes. The partial shared schema has 34 constraints and 80
  indexes.
- Configure these PostgreSQL-only index names before scaffolding so the server
  never truncates them implicitly:
  - `IX_ArrLifecycle_Instance_State_CreatedAt`
  - `IX_WorkerJobs_ClaimOrder`
- The final PostgreSQL schema has exactly nine triggers and nine paired
  functions: four `DavItems` cleanup triggers, three `HealthCheckResults`
  statistics triggers, one `HistoryItems` NZB cleanup trigger, and one
  `QueueItems` NZB cleanup trigger.
- Never recreate `TR_HistoryItems_Delete_AddHistoryCleanup`; migration
  `20260203205014_Add-DeleteMountedFiles-To-HistoryCleanupItems-Table`
  intentionally removed it.
- A fresh baseline seeds five fixed WebDAV roots and runtime-generates distinct
  `api.key` and `api.strm-key` values.
- The broken inherited PostgreSQL schema reproduces SQLSTATE `42883`
  (`text = uuid`) and export fails when EF reads a text identifier as `Guid`.
- There are 50 current `[DbContext(typeof(DavDatabaseContext))]` metadata
  attributes under `backend/Database/Migrations`: 49 migration classes and one
  snapshot. Fifteen migration classes carry inline metadata rather than a
  separate designer. Reattribute all 50.
- The transfer model contains more UUID fields than the 28 physical mismatches.
  Freeze and validate the complete raw UUID/FK inventory; do not limit source
  validation to those 28 columns.

---

## Task 1: Preserve And Test The Local-Wall Timestamp Contract

**Files:**

- Modify: `backend/Services/ArrCorrelationService.cs`
- Modify: `backend/Services/ArrOperationsService.cs`
- Modify: `backend/Services/ArrSearchNudgeService.cs`
- Modify: `backend/Mount/DfsFileSystem.cs`
- Modify: `backend/Queue/QueueItemProcessor.cs`
- Create: `backend.Tests/Database/LegacyTimestampContractTests.cs`
- Modify/Create focused ARR, DFS, and queue processor tests as required

- [ ] **Step 1: Write RED contract tests**

Assert that all four entity properties remain `DateTime`; writes use local wall
time; recent-history queries compare them to local cutoffs; an `Unspecified`
value returned by SQLite/PostgreSQL is interpreted as deployment-local when
projected to Unix time; and elapsed download duration is monotonic-clock based.

Add regression cases that place local and UTC on opposite sides of a 24-hour
cutoff. The old ARR predicates must select the wrong row before the fix.

- [ ] **Step 2: Run RED**

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~LegacyTimestampContractTests|FullyQualifiedName~ArrCorrelationServiceTests|FullyQualifiedName~ArrOperationsServiceTests|FullyQualifiedName~ArrSearchNudgeServiceTests|FullyQualifiedName~DfsFileSystemTests|FullyQualifiedName~QueueItemProcessorVerificationTests'
```

- [ ] **Step 3: Remove mixed-clock comparisons**

Use `DateTime.Now` for cutoffs compared to these four fields. Do not change
modern `DateTimeOffset`/Unix-time models that already represent UTC instants.
Keep server-stat day/week/month boundaries local.

Capture local cutoffs before constructing LINQ predicates so they are bound as
wall-clock parameters rather than translated to server `now()`. Add generated
SQL/command-interceptor coverage for the affected queries. PostgreSQL tests
also set and assert the explicit Npgsql session `Timezone`; application and
database local-zone disagreement is a startup error.

In DFS conversion, treat `DateTimeKind.Unspecified` as local wall time, not UTC;
keep the root sentinel clamped to Unix zero. In `QueueItemProcessor`, measure
duration with `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime()` while
persisting `CreatedAt = DateTime.Now`.

- [ ] **Step 4: Run focused and complete gates**

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~LegacyTimestampContractTests|FullyQualifiedName~ArrCorrelationServiceTests|FullyQualifiedName~ArrOperationsServiceTests|FullyQualifiedName~ArrSearchNudgeServiceTests|FullyQualifiedName~DfsFileSystemTests|FullyQualifiedName~QueueItemProcessorVerificationTests'
dotnet build backend/NzbWebDAV.csproj --no-restore
dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add backend backend.Tests
git commit -m "fix: preserve local timestamp semantics consistently"
```

---

## Task 2: Split Provider Migration Ownership And Fail PostgreSQL Closed

**Files:**

- Modify: `backend/Database/DavDatabaseContext.cs`
- Create: `backend/Database/SqliteDavDatabaseContext.cs`
- Create: `backend/Database/PostgreSqlDavDatabaseContext.cs`
- Replace: `backend/Database/DavDatabaseContextFactory.cs`
- Create: provider-specific design factories
- Create: `backend/Database/DatabaseMigrator.cs`
- Modify: all production `new DavDatabaseContext()` call sites
- Modify: all production/test `Migrate()` and `MigrateAsync()` call sites
- Modify: all 50 context metadata attributes under
  `backend/Database/Migrations`
- Rename: the SQLite model snapshot class/file
- Create: `backend.Tests/Database/ProviderMigrationOwnershipTests.cs`
- Create: `backend.Tests/Database/DatabaseMigratorPreflightTests.cs`
- Create: `backend.Tests/TestData/sqlite-source-schema-manifest.json`

- [ ] **Step 1: Pin the EF CLI**

Create/update `.config/dotnet-tools.json` with `dotnet-ef` version `10.0.9`.
Run `dotnet tool restore` and `dotnet ef --version`; do not rely on a global
tool.

- [ ] **Step 2: Write migration-ownership RED tests**

Freeze the exact ordered set of all 49 SQLite migration IDs, not only the
count. Assert:

```csharp
Assert.Equal(expectedSqliteIds, sqliteMigrations.Keys);
Assert.Empty(postgresMigrations);
Assert.Empty(sharedBaseMigrations);
```

Assert SQLite uses `__EFMigrationsHistory`, PostgreSQL uses
`__EFMigrationsHistory_PostgreSql`, every production constructor is routed
through the runtime factory, and every production migration operation is
routed through `DatabaseMigrator`.

- [ ] **Step 3: Define a workable context inheritance contract**

Change the sealed base to a non-sealed shared behavior context. Remove its
public parameterless constructor. Retain the public
`DbContextOptions<DavDatabaseContext>` constructor for isolated tests and add a
protected non-generic `DbContextOptions` constructor so derived provider
contexts can pass `DbContextOptions<SqliteDavDatabaseContext>` or
`DbContextOptions<PostgreSqlDavDatabaseContext>`.

Concrete contexts own provider options, interceptors, history-table name, and
migration identity. The runtime factory returns a concrete instance typed as
`DavDatabaseContext`. Replace all production parameterless construction; use
an `rg` assertion so an omitted call site fails review.

- [ ] **Step 4: Reattribute all inherited metadata mechanically**

Change all 50 attributes, including the 15 inline-metadata migration files, to
`SqliteDavDatabaseContext`. Rename the SQLite snapshot class/file. Do not alter
any migration ID or `Up`/`Down` body. Compare normalized method bodies before
and after and assert identical hashes in the ownership test.

The entity types do not change in this plan, so the SQLite snapshot's four
legacy properties remain `DateTime`. Assert
`Database.HasPendingModelChanges()` is false for SQLite after reattribution.
Apply the exact 49-ID chain to a fresh SQLite database and check in a reviewed
raw manifest of tables, columns, affinities, nullability/defaults, indexes,
foreign keys, and triggers. This becomes the source-drift contract for v3.

- [ ] **Step 5: Centralize migration calls and fail PostgreSQL closed**

Replace direct calls in `Program.cs`, `DatabaseTransferService.cs`, and all five
current PostgreSQL migration-test paths. During this checkpoint,
`DatabaseMigrator` must throw a clear `PostgreSQL native baseline is not
installed` error before any PostgreSQL schema mutation. Normal SQLite migration
and no-argument startup remain functional.

- [ ] **Step 6: Run GREEN**

```bash
dotnet build backend/NzbWebDAV.csproj --no-restore
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ProviderMigrationOwnershipTests|FullyQualifiedName~DatabaseMigratorPreflightTests|FullyQualifiedName~DatabaseProviderSelectionTests|FullyQualifiedName~ContentIndexRecoveryServiceTests'
dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

- [ ] **Step 7: Commit the explicit refusal checkpoint**

```bash
git add .config backend backend.Tests
git commit -m "refactor: split database migration ownership by provider"
```

This commit is acceptable only because PostgreSQL startup/migration fails
explicitly before mutation; it must never report success with zero migrations.

---

## Task 3: Add Raw Preflight And A Native PostgreSQL Baseline

**Files:**

- Modify: `backend/Database/PostgreSqlDavDatabaseContext.cs`
- Modify: `backend/Database/DatabaseMigrator.cs`
- Modify: `backend/Database/DavDatabaseContext.cs`
- Modify: `backend/Program.cs`
- Create: `backend/Database/PostgreSqlMigrations/20260711210000_PostgreSqlBaseline.cs`
- Create: matching designer and PostgreSQL snapshot
- Create: `backend.Tests/TestData/postgresql-schema-manifest.json`
- Create/Modify: `backend.Tests/Database/PostgreSqlPhysicalSchemaTests.cs`
- Modify: every migration caller identified in Task 2

- [ ] **Step 1: Provision PostgreSQL 16 before running tests**

Start a disposable, dedicated PostgreSQL 16 database/schema and export a
sanitized `NZBDAV_TEST_POSTGRES_CONNECTION_STRING`. Every test command in Tasks
3-6 must assert zero skips. Missing PostgreSQL configuration is a test failure,
not a skip.

- [ ] **Step 2: Write preflight RED tests**

Through `DatabaseMigrator`, cover an unrelated table, old
`__EFMigrationsHistory`, an old NZBDav table, an orphan `FN_TR_%` routine, and a
custom type as rejection cases. Hash the complete schema before and after each
rejection and require exact equality. Separately prove that a schema containing
only an empty `__EFMigrationsHistory_PostgreSql` table plus its exact
PostgreSQL-owned PK index/constraint and composite/array types is an accepted
baseline starting state and that no other object is accepted with it.

The raw preflight runs on an opened Npgsql connection before EF history-table
creation. If the provider-specific history table is absent/empty, require a
dedicated empty schema. If it contains rows, require the exact native baseline
prefix before applying later migrations. Hold the migration connection and a
database/schema-scoped advisory lock across preflight and EF migration.
Rehearsal also uses an exclusive migration role/window with no other principal
allowed to create objects; an advisory lock only coordinates cooperating
NZBDav migrators and is not a general PostgreSQL DDL lock.

`DatabaseTransferService` and `Program` must not bypass this service.

- [ ] **Step 3: Configure the desired PostgreSQL model before scaffolding**

For PostgreSQL only:

- map the four legacy `DateTime` properties to
  `timestamp without time zone`;
- map the 28 identifiers to `uuid`, ten counters/times to `bigint`, and two
  flags to `boolean`;
- apply the three maximum lengths and remove the three unwanted defaults;
- configure the two explicit short index names listed above with
  `HasDatabaseName(...)`;
- apply deterministic PostgreSQL `COLLATE "C"` to every string property used
  in a primary/unique key or ordered/equality index so SQLite BINARY-distinct
  values cannot collide under the target database locale. Record collations in
  the physical manifest and test non-ASCII/case/normalization-distinct keys.

Build the PostgreSQL connection string with Npgsql's `Timezone` parameter from
required `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE`, and verify `SHOW TimeZone` plus
`TimeZoneInfo.Local.Id` match it before migration or normal startup. Never take
the session default from the PostgreSQL host.

Before any Npgsql type mapper or connection is initialized, set
`AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true)`.
Without that switch, Npgsql writes the existing `DateTime.MinValue` root
sentinel as PostgreSQL `-infinity`, which is not the same stored value. Add a
startup-order test and a physical query that rejects `infinity`/`-infinity` in
all four legacy columns.

Do not scaffold from the current uncorrected Npgsql model, which maps the four
legacy fields to `timestamptz` and rejects `DateTime.Now` writes.

- [ ] **Step 4: Scaffold and hand-review the baseline**

```bash
dotnet tool restore
NZBDAV_DATABASE_PROVIDER=postgres \
NZBDAV_DATABASE_CONNECTION_STRING="$NZBDAV_TEST_POSTGRES_CONNECTION_STRING" \
dotnet tool run dotnet-ef migrations add PostgreSqlBaseline \
  --project backend/NzbWebDAV.csproj \
  --context PostgreSqlDavDatabaseContext \
  --output-dir Database/PostgreSqlMigrations
```

Normalize the migration ID to `20260711210000`. The first `Up` operation must
be a defense-in-depth empty-schema guard. It may allow only the EF-created
`__EFMigrationsHistory_PostgreSql` structural closure: its table, exact PK
constraint/index, row composite type, and implicit array type. Resolve these by
catalog ownership/dependency and exact definitions rather than a broad name
prefix; reject every unrelated relation, index, constraint, routine, sequence,
or type. Put no extension, enum, sequence, table, or other mutation before the
guard.

The operational preflight is what guarantees zero mutation for rejected
targets. A direct EF invocation that races after preflight or bypasses the
service taints the target; drop/recreate it. Document that `dotnet ef database
update` is not an approved operational path.

- [ ] **Step 5: Seed only bootstrap state**

Insert the five fixed roots with SQL timestamp literals for their existing
year-1 local-wall sentinel, and prove the stored text is not `-infinity`. On
PostgreSQL 16 generate each authentication key with the core CSPRNG-backed
`replace(gen_random_uuid()::text, '-', '')` expression (or a reviewed equivalent
with at least the same 122 random bits), assert distinct lowercase 32-hex
format, and forbid `random()`. Seed the reserved target-local ConfigItem with
the exact value `{"formatVersion":3,"state":"fresh"}`; reject
`database.import-state` if that key appears in a SQLite source.
Never embed generated values in source, output, or logs.

- [ ] **Step 6: Check in an exact schema manifest**

The reviewed JSON fixture enumerates every one of the 240 columns by table,
column, data type/UDT, nullability, length, default, and applicable collation;
all 36 constraints; all
84 explicit index contracts; five bootstrap roots; and both history-table
contracts. It also records the two generated-key names and reserved import-state
row without their secret/runtime values. Generate it from the reviewed desired
model, inspect the diff, and make physical-schema tests compare the live
database to this file.

Also assert local and unspecified `DateTime` writes return identical wall-clock
components with `DateTimeKind.Unspecified` and physical types are
`timestamp without time zone`; PostgreSQL does not preserve the input Kind.

- [ ] **Step 7: Run GREEN with zero skips**

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING="$NZBDAV_TEST_POSTGRES_CONNECTION_STRING" \
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~DatabaseMigratorPreflightTests|FullyQualifiedName~PostgreSqlPhysicalSchemaTests'
dotnet build backend/NzbWebDAV.csproj --no-restore
```

Require fresh `0 -> latest`, idempotent second migration, rejected-target digest
stability through `DatabaseMigrator`, and `HasPendingModelChanges() == false`.

- [ ] **Step 8: Keep production PostgreSQL disabled and request review**

Do not commit or activate this checkpoint. Production `DatabaseMigrator` must
continue throwing the explicit refusal from Task 2 until both the baseline and
`20260711211000_PostgreSqlOperationalObjects` exist and are applied. Baseline
tests may use an internal test-only migration path that still performs raw
preflight. Task 3 and Task 4 form one atomic schema-review checkpoint, not a
runtime-release checkpoint.

---

## Task 4: Install And Verify PostgreSQL Operational Objects

**Files:**

- Create: `backend/Database/PostgreSqlMigrations/20260711211000_PostgreSqlOperationalObjects.cs`
- Create: matching designer/snapshot update
- Modify: `backend/Database/Migrations/MigrationProvider.cs`
- Modify: `backend.Tests/TestData/postgresql-schema-manifest.json`
- Modify: `backend.Tests/Database/PostgreSqlPhysicalSchemaTests.cs`

- [ ] **Step 1: Write trigger RED tests**

Assert the exact nine-trigger/nine-function inventory and exercise insert,
update, and delete behavior for blob cleanup, NZB cleanup, directory cleanup,
and health statistics. Assert history deletion does not create a
`HistoryCleanupItems` row. Include a `HealthCheckResults.CreatedAt` change in
the update test; if the existing trigger does not maintain the correct bucket,
fix its predicate rather than copying the bug.

- [ ] **Step 2: Implement operational SQL**

Reuse the reviewed provider helpers for the four `DavItems` triggers, the
history/queue NZB triggers, and the three health-stat triggers. `Down` drops the
exact triggers and paired functions in reverse dependency order. Do not call
`CreateQueueItemsBlobCleanupTrigger` or
`CreateHistoryItemsCleanupTrigger`.

- [ ] **Step 3: Run GREEN with zero skips**

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING="$NZBDAV_TEST_POSTGRES_CONNECTION_STRING" \
dotnet test backend.Tests/backend.Tests.csproj --no-restore \
  --filter FullyQualifiedName~PostgreSqlPhysicalSchemaTests
```

Require exact manifest equality, behavior tests, and a no-op second migrate.

- [ ] **Step 4: Inventory PostgreSQL legacy-field fixtures**

Search the complete test project for `DateTime.UtcNow` or `DateTimeKind.Utc`
assigned to the four local-wall properties. Correct only those fixtures to
local/unspecified wall values; do not change UTC fields. Add a source assertion
so future UTC assignments to these properties fail review.

- [ ] **Step 5: Keep normal runtime refusal after the complete behavioral gate**

The exact baseline and operational migration IDs, physical manifest, and
behavioral suite must pass, but normal PostgreSQL startup remains fail-closed.
Only an internal test path may apply the native migrations at this checkpoint;
the exact offline Transfer-v3 maintenance path is the first non-test path that
may use them. Run:

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING="$NZBDAV_TEST_POSTGRES_CONNECTION_STRING" \
dotnet build backend/NzbWebDAV.csproj --no-restore
NZBDAV_TEST_POSTGRES_CONNECTION_STRING="$NZBDAV_TEST_POSTGRES_CONNECTION_STRING" \
dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

Require the complete backend suite with zero failures and zero skips. Do not
lift production refusal until Transfer v3 also passes and a separate promotion
decision is made. A focused
schema suite is not sufficient to activate PostgreSQL.

- [ ] **Step 6: Commit Tasks 3 and 4 atomically**

```bash
git add backend backend.Tests
git commit -m "feat: add complete native PostgreSQL schema"
```

---

## Task 5: Replace Whole-Snapshot Transfer With Bounded Transfer V3

**Files:**

- Modify: `backend/Database/DatabaseTransferService.cs`
- Modify: `backend/Program.cs`
- Modify: config get/update controllers and `ConfigManager` reserved-key paths
- Create: `backend/Database/Transfer/` helpers for manifests, framing,
  validation, digests, and bootstrap inspection
- Modify: `entrypoint.sh` and maintenance CLI for exact
  `--db-export-v3 DIR` / `--db-import-v3 DIR` commands
- Modify/Create: transfer unit and PostgreSQL round-trip tests
- Modify: `scripts/nzbdav_migrate_sqlite_to_postgres.py`
- Modify: Python and shell runbook tests
- Modify: `docs/superpowers/plans/2026-07-11-nzbdav-runtime-migration-safety.md`

- [ ] **Step 1: Freeze the complete raw source contract**

Generate and review a checked-in manifest of every transferred table, stable
sort/keyset key, `Guid`/`Guid?` column, declared FK, application-level external
reference, bootstrap row, and provider-specific key collation. The current
model contains 46 UUID properties; confirm that count from the model and raw
SQLite schema rather than assuming the 28 mismatch list is complete.
The manifest and dependency order must include the current `MaintenanceRuns`
and `ArrImportCommands` tables; a transfer fixture that omits either table is
not representative of the 49-migration source schema.

String keysets use provider-neutral ordinal UTF-8 byte ordering: SQLite
`COLLATE BINARY` and PostgreSQL `COLLATE "C"`. Configure both query paths
explicitly; never inherit database/default locale collation. Test composite
keys such as `(Accounts.Type, Accounts.Username)` with non-ASCII, case, and
normalization-distinct usernames and prove identical frame order/digests.

Before EF materializes any entity, scan raw SQLite values with a data reader in
bounded batches. Require exact hyphenated `D` shape case-insensitively—EF Core
10 SQLite normally writes uppercase `D`, while historical seed literals may be
lowercase. Normalize parsed values to lowercase `D` for export, collision, and
FK checks. Detect normalized collisions using a mode-`0600` private disk-backed
temporary SQLite index that is removed on success, failure, and signal exit, run
`PRAGMA foreign_key_check`, and validate application-level references. Errors
contain table/column/row ordinal and a digest prefix, never the raw identifier
or row.

Use that normalized disk index for UUID keyset pagination itself. Order UUIDs
by canonical RFC 4122 network bytes (equivalently lowercase `D` ASCII with
constant hyphens), never by the mixed-case raw SQLite text or
`Guid.ToByteArray()`'s platform/.NET field order. Join the normalized cursor to
the source row by its verified row identity. Prove this ordering matches native
PostgreSQL `uuid` ordering, including composite keys, and test upper/lower UUIDs
straddling every one-row and multi-row batch boundary with no skip/duplicate.

In the same raw pass, parse each legacy timestamp exactly and require its ticks
to be divisible by ten (PostgreSQL's one-microsecond resolution). A non-zero
100-nanosecond remainder is a blocking validation error until the user approves
rounding or an explicit remainder-preservation schema.

Generate a source-value representability contract from the reviewed target
schema manifest and validate every source column/constraint in bounded raw
batches before publishing the v3 manifest or mutating PostgreSQL. At minimum it
checks target nullability, signed numeric ranges, canonical SQLite booleans
(`0`/`1` only), enum/check domains, exact UUID rules, timestamp range/precision,
unique keys after normalization/target collation, and every text value by raw
bytes with strict UTF-8 decoding, no U+0000, and PostgreSQL `varchar(n)`
character-count limits. Do not let EF normalization hide a noncanonical raw
value. Add overlength ASCII and multibyte text, invalid UTF-8/NUL, `2`/`-1`
boolean, range, normalized-unique, and nullable/required regression cases. A
representability failure is source review work; no target history table or
import-state marker may yet exist.

Before any model query/export, require the private source
`__EFMigrationsHistory` IDs to equal the exact ordered 49-ID set from Task 2,
with no missing, duplicate, or future ID. Parse and record each nonblank
`ProductVersion`; a version outside the reviewed supported history is a manual
review failure, not an ignored warning. Compare raw `sqlite_master`/PRAGMA
metadata to `sqlite-source-schema-manifest.json` and reject every unknown,
missing, or definition-drifted object. This prevents a newer/manual schema from
being silently omitted by the current transfer model.

- [ ] **Step 2: Define a private framed snapshot directory**

Transfer v3 is a mode-`0700` directory containing one canonical UTF-8 JSONL
file per table plus a `manifest.json` written atomically last. Every file is
created mode `0600`; table files contain private source values and may contain
secrets even though the manifest and logs are redacted. Each table is exported
with keyset pagination and dual row/UTF-8-byte budgets; memory is never bounded
by row count alone. V3 uses sequential raw readers rather than EF entity
materialization for unbounded text. Any field larger than the frame budget
(especially `QueueNzbContents.NzbContents`) is represented by ordered,
digest-covered chunk frames capped at one MiB and reconstructed through a
session-local PostgreSQL temporary staging table that disappears on connection
loss. Require an operator/tested
`NZBDAV_TRANSFER_MAX_FIELD_BYTES` ceiling that is at least the measured source
maximum and within the migration container/server memory contract; reject a
larger source field before export. Use explicit DTO writers/property order
rather than reflection-dependent serialization. The manifest records:

```json
{
  "version": 3,
  "timestampSemantics": "deployment-local-wall",
  "sourceTimeZoneAtExport": "Asia/Dubai",
  "tables": {
    "DavItems": { "rows": 0, "sha256": "..." }
  }
}
```

The snapshot-exporter residue policy applies to `TransferV3SnapshotDirectory`.
Its unfinalized/error cleanup removes every sensitive file descriptor-relatively
through the pinned private directory and durably records those removals. POSIX
does not provide a portable conditional `rmdir` by an already-open directory
descriptor, so this exporter never performs a separate inode check followed by
a name-based `rmdir`: a replacement can win that interval. It leaves the
resulting empty mode-`0700` directory name in place, reports its path as a
bounded non-secret cleanup diagnostic when it is still identity-proven, and
requires any later removal to be a separate operator-audited action. Never
claim that this exporter removed the empty directory name.

The sealed-stage residue policy applies separately to
`TransferV3SealedSnapshotStage`. Its unpredictable nonce root is created under
a pinned trusted parent and cleanup operates under the documented quiescent
same-UID threat model. For an empty owned root, cleanup performs its identity
check immediately followed by `unlinkat(AT_REMOVEDIR)`. That adjacent sequence
narrows the replacement interval but does not claim an atomic conditional
unlink. Unknown, replaced, and nonempty entries are preserved and reported;
external hard-link residues are reported without following or removing the
outside link. Its restart audit reports nonce-shaped candidates plus
unknown-prefixed and unreadable counts, no-follow opens candidate roots only
for classification, never enumerates or opens their contents, and never deletes
anything. Neither cleanup policy claims recovery after SIGKILL, daemon loss, or
host power loss.

An unpredictable staging name reduces accidental collisions but is not a
security boundary. The native descriptor layer is enabled only for verified
Linux x64/arm64 and macOS arm64 ABIs; macOS x64 fails closed until separate
bindings and native x64 tests prove its inode structure layouts. Linux invokes
the architecture-pinned `renameat2(RENAME_NOREPLACE)` syscall directly so glibc
and musl behave identically; `ENOSYS`, `EINVAL`, an unknown ABI, or any other
inability to prove no-replace semantics fails closed and never falls back to
overwrite-capable `rename`.

Legacy wall values use an exact offset-free format and deserialize as
`DateTimeKind.Unspecified`; no UTC conversion occurs. Logs/manifests contain no
connection strings, keys, ConfigItem values, or raw rows.

The v3 framing and digest contract is exact:

- A canonical line is the exact compact UTF-8 byte sequence produced by the
  ordered frame DTO writer followed by one LF byte. CRLF, a missing final LF,
  alternate escaping/property order, whitespace, duplicate/unknown fields, and
  trailing content are invalid even when a generic JSON parser would accept
  them.
- A batch SHA-256 covers its `batch-start` line plus every `row`, `row-start`,
  `field-chunk`, and `row-end` line, each including LF. It excludes its
  `batch-end` line to avoid self-reference. `batch-end` carries the verified
  row count, decoded payload-byte count, last cursor, and that digest.
- A table SHA-256 covers the table header, every line included by every batch
  digest, and each verified `batch-end` line, each including LF. It excludes
  `table-end` to avoid self-reference. `table-end` and the final manifest carry
  the same verified table digest and counts.
- `row-end` retains a separate logical field/chunk payload digest covering each
  field index, chunk index, decoded length, and payload so reconstruction can
  be checked independently; it does not replace the canonical batch/table
  digests.

Keep v1/v2 readers only for small compatibility tests and SQLite-only CLI
coverage. `Program` and `DatabaseTransferService` must reject legacy JSON
import/export when the runtime/target provider is PostgreSQL before context
migration, file deserialization, or any target mutation; direct CLI invocation
may not bypass the Python helper. Add pre/post PostgreSQL schema-and-row digest
tests for those rejections. The real migration runbook must reject v1/v2 and
require v3. Amend the runtime-safety plan before implementation so its allowlist, file
inventory, cleanup/recovery paths, and container smoke cover the v3 directory
plus the disk-backed validation file; its legacy single-JSON examples are not
executable v3 instructions.

- [ ] **Step 3: Define bootstrap-aware target emptiness**

A newly migrated PostgreSQL target is eligible only when it contains exactly:

- the expected migration history;
- the five baseline roots;
- generated `api.key` and `api.strm-key` rows;
- one reserved `database.import-state` ConfigItem with the exact value
  `{"formatVersion":3,"state":"fresh"}`;
- no other application rows.

A real SQLite source always carries its fixed roots and existing API keys even
when it has no user rows. Require and transfer those keys so existing clients
keep working; missing, blank, or duplicate source keys are a production
preflight failure. Only a brand-new PostgreSQL install with no transfer retains
the generated target keys. Synthetic zero-row snapshots are test-only and are
not accepted by the production migration command. Exclude the reserved
target-local import-state key from source/target content digests. Refuse
`--replace` for PostgreSQL v3 and refuse any target with non-bootstrap content.

- [ ] **Step 4: Stream the import in dependency order**

Read and validate one JSONL batch at a time, verify rolling digests/counts, and
commit in bounded table transactions. The importer observer stages only the
current batch and receives `CommitBatch` only after its `batch-end`
count/cursor/digest verifies. Cancellation, corruption, or an observer failure
rolls back only the current staged batch; earlier verified batches remain
committed and the target is marked tainted. `CompleteTable` occurs only after
the `table-end` count/digest verifies and the physical file reaches EOF;
trailing data prevents table completion without undoing prior verified batch
commits. Do not hold a single 5+ GB transaction or deserialize/sort the whole
snapshot in memory. A failure marks the target tainted; the only retry is
drop/recreate and reapply migrations.

Before the first data batch, commit `database.import-state` with the exact value
`{"formatVersion":3,"state":"importing","manifestSha256":"<64-lowercase-hex>"}`.
Normal application startup and all non-import maintenance commands must refuse
`importing` or `failed`. After all database table/schema/digest checks succeed,
atomically set only
`{"formatVersion":3,"state":"database-verified","manifestSha256":"<same-64-lowercase-hex>"}`;
that state is still non-usable. Preserve this reserved row when importing or
clearing ConfigItems. SIGKILL/power-loss tests must prove the committed marker
survives and blocks startup after an arbitrary batch; recovery is drop/recreate,
not resumption in place.

The outer migration helper may publish a mode-`0600`, target-runtime-owned
`.nzbdav-migration-complete.json` only after blob publication/digest checks,
final source database/sidecar/blob stability checks, and sensitive-work cleanup
are durable. Write/fsync/rename/fsync it atomically under the already verified
target-config descriptor. It contains only the manifest/database/blob digests
and format version. A transferred deployment starts only when the DB state is
the exact canonical `database-verified` JSON value above and this external
completion marker exists with the same digest; exact canonical `fresh` remains
the separate no-transfer greenfield state.
Reject/remove stale marker files before a new run. Test crashes at every
post-database, blob-stage, blob-publish, source-recheck, cleanup, and
pre/post-marker boundary.

Treat `database.import-state` as an internal reserved key: omit it from ordinary
config reads/exports, reject it in generic update/insert APIs before saving, and
reject it in source snapshots. Only a dedicated import-state store may perform
the finite canonical transitions `fresh -> importing(A) -> database-verified(A)`
or `importing(A) -> failed(A)` for a catchable failure. No API or retry
may transition `importing`/`failed` back to usable; drop/recreate is required.
Test authorization, visibility, transition ordering, digest match, and
concurrent generic-update attempts.

Do not import `HealthCheckStats`. Insert `HealthCheckResults` with operational
triggers enabled, then compare rebuilt statistics to a source-derived expected
digest. Before export, independently recompute source statistics from
`HealthCheckResults` and fail if they disagree with `HealthCheckStats`; do not
silently canonize inconsistent derived state. Treat other cleanup/outbox tables
as durable source state and import them explicitly.

- [ ] **Step 5: Prove bounded behavior and complete round trip**

Tests must cover:

- a generated data set larger than the configured batch with an asserted
  memory ceiling or bounded-buffer instrumentation;
- a single unbounded-text value larger than the row-batch byte budget, proving
  one-MiB chunk frames, bounded client memory, correct reconstruction, and
  preflight rejection above the approved maximum-field ceiling;
- all UUID/FK/raw-source failures before EF conversion;
- full, empty, interrupted, and tampered snapshots;
- real bootstrap-only sources, synthetic missing-bootstrap rejection,
  non-bootstrap targets, and failed/retried targets;
- SIGKILL after arbitrary committed batches with durable startup refusal;
- exact local-wall round trip through six fractional digits, plus rejection of
  any unapproved 100-nanosecond remainder;
- source/target current-timezone mismatch rejection;
- health-stat rebuild without duplicate-key collisions;
- per-table row/digest equality and no secret leakage.

Run focused .NET, Python, shell, and disposable PostgreSQL gates with zero
skips. Build the real image and exercise the maintenance entrypoint against a
private Docker network; do not use host shortcuts.

- [ ] **Step 6: Commit**

```bash
git add backend backend.Tests scripts tests entrypoint.sh docs
git commit -m "feat: add bounded database transfer v3"
```

---

## Task 6: Rehearse Offline Cutover And Define Rollback Boundaries

**Files:**

- Modify: `tests/test_nzbdav_migration_container.sh`
- Modify: `tests/test_nzbdav_migration_runbook.sh`
- Modify: `docs/setup-guide.md`
- Modify: `HANDOFF.md`

- [ ] **Step 1: Rehearse only from a stopped-source private copy**

Use the runtime-migration-safety plan to capture the exact DB/WAL/SHM set,
prove the source fingerprint unchanged, normalize only the private copy, and
export v3. Never mount or open the rollback source through SQLite.

Before downtime, compute and persist a redacted capacity ledger. At minimum it
accounts for raw DB/WAL/SHM capture, canonical backup (already more than
10,312,302,592 bytes for the observed main DB before WAL), source and target
verification v3 trees including JSON escaping/chunk overhead, validation DB,
PostgreSQL base/WAL growth, source blobs, target staging, and any existing blob
rollback tree. Use the exact artifact sizes from a prior rehearsal where
available, remeasure current source/blob sizes, enforce quota as well as
`statvfs().f_bavail`, and reserve a reviewed percentage plus fixed emergency
headroom. An unknown quota/headroom result is blocking.

Define phase reclamation explicitly: validation temp state is deleted after a
verified source manifest; raw capture may be reclaimed only after canonical
backup, finalized v3 snapshot, and repeated source fingerprints; verification
frames are removed only after target digests; rollback/source snapshots remain
until cutover acceptance. Recheck capacity before every allocation phase. Test
low-space, quota, growth-after-preflight, cleanup, and simulated `ENOSPC` paths
without starting source downtime or normal PostgreSQL services.

- [ ] **Step 2: Rehearse into a disposable dedicated target**

Preflight, migrate, import, verify schema manifest, verify every table digest,
and run read-only application queries. Repeat from a new empty target. Any
failed/partial target is dropped, not repaired.

- [ ] **Step 3: Test the lossless rollback window**

Before normal PostgreSQL services start, rollback is lossless:

1. stop the offline candidate;
2. discard PostgreSQL;
3. restore the prior SQLite image/config;
4. verify SQLite migration history, health, queue, WebDAV, and mount;
5. resume consumers.

Normal PostgreSQL startup is the explicit point of no return for this rollback.
It can immediately write leases, health results, cleanup rows, and receipts.
After any PostgreSQL write, switching to the stopped SQLite source loses data;
freeze the service and use PostgreSQL backup/restore or a separately designed
reverse-delta procedure. Do not claim connection-switch rollback after that
point.

- [ ] **Step 4: Run the complete release gate**

```bash
dotnet build backend/NzbWebDAV.csproj --no-restore
dotnet test backend.Tests/backend.Tests.csproj --no-restore
npm --prefix frontend test
npm --prefix frontend run typecheck
npm --prefix frontend run build
npm --prefix frontend run build:server
npm --prefix frontend run test:e2e
python3 -m unittest discover -s tests -p 'test_*.py' -v
/bin/sh tests/test_entrypoint_contract.sh
/bin/sh tests/test_nzbdav_migration_container.sh
/bin/sh tests/test_nzbdav_migration_runbook.sh
git diff --check
git status --short
```

Also require zero PostgreSQL skips, SQLite upgrade smoke, Docker image build,
forced migration-failure startup smoke, npm/NuGet vulnerability scans, and a
clean reviewed diff. Obtain explicit user approval before running the existing
Playwright E2E command under the selected Product Design browser workflow. The
in-app Browser audit is additional evidence and does not replace automated E2E;
without approval the release gate remains incomplete rather than silently
substituting a manual audit.

- [ ] **Step 5: Commit documentation and rehearsal evidence**

```bash
git add tests docs HANDOFF.md
git commit -m "docs: prove PostgreSQL migration rehearsal and rollback"
```

---

## Independent Review Checklist

- [ ] The user confirmed a dedicated, disposable PostgreSQL database/schema.
- [ ] The four legacy fields remain `DateTime` local-wall values end to end.
- [ ] PostgreSQL uses `timestamp without time zone` for exactly those fields.
- [ ] Npgsql infinity conversion is disabled before provider initialization;
  the root sentinel is stored as year 1, never `-infinity`.
- [ ] Source timestamp precision is proven microsecond-aligned or execution
  stops for an explicit precision-loss decision.
- [ ] UTC `DateTimeOffset`/Unix models remain unchanged.
- [ ] All mixed local/UTC comparisons and wall-clock duration calculations have
  dedicated regression tests.
- [ ] All 50 inherited metadata attributes map to SQLite; the exact 49 IDs and
  every `Up`/`Down` body are unchanged.
- [ ] The base context owns no migrations and every production construction and
  migration call uses the provider factory/migrator.
- [ ] PostgreSQL refuses to start at the intermediate split checkpoint.
- [ ] PostgreSQL remains disabled until both native migrations and the complete
  PostgreSQL-enabled backend suite pass with zero skips.
- [ ] Raw preflight executes before EF history creation and rejected schema
  digests are byte-for-byte unchanged.
- [ ] The PostgreSQL baseline's guard is its first mutation.
- [ ] The checked-in manifest covers all 240 columns, 36 constraints, and 84
  index contracts, including the two explicit short names.
- [ ] Exactly nine final triggers/functions exist; the obsolete history cleanup
  trigger does not.
- [ ] `dotnet-ef` is repository-pinned to the EF package version.
- [ ] Every PostgreSQL task provisions version 16 and treats a skip as failure.
- [ ] Raw source validation covers the complete UUID/FK inventory before EF
  materialization.
- [ ] Every raw SQLite value is representable under the target type, length,
  encoding, nullability, uniqueness, and domain contracts before target mutation.
- [ ] UUID pagination uses canonical network-byte order from the normalized
  disk index and passes mixed-case batch-boundary tests.
- [ ] Private-source migration history and raw SQLite schema exactly match the
  reviewed 49-migration manifest before model queries.
- [ ] Transfer memory is row-and-byte bounded; giant fields are chunked or
  rejected by the approved ceiling and no whole-database list/sort remains.
- [ ] String keysets use explicit SQLite BINARY/PostgreSQL C ordering and pass
  non-ASCII digest tests.
- [ ] Capacity/quota/headroom is proven for every filesystem before downtime,
  with a tested reclamation sequence and ENOSPC behavior.
- [ ] Bootstrap roots/keys have explicit full-source, empty-source, and failure
  behavior.
- [ ] Health statistics are rebuilt, not imported into active triggers.
- [ ] Failed partial targets are tainted and dropped/recreated.
- [ ] A committed import-state marker blocks normal startup after SIGKILL or
  power loss at every batch boundary.
- [ ] Database verification remains non-usable until the atomically durable
  external completion marker proves blobs, final source checks, and cleanup.
- [ ] Generic configuration APIs cannot read or mutate the import-state key;
  legacy JSON commands cannot run against PostgreSQL.
- [ ] Offline validation and the first-write point of no return are explicit.
- [ ] No production source, deployment, push, PR, or unrelated `artifacts/`
  content was touched.

## Remaining User Decision

Before execution, confirm one statement:

> The PostgreSQL target is a dedicated NZBDav database/schema with no shared
> objects and may be dropped and recreated throughout rehearsal.

The verified local-wall timestamp contract is preserved by default. If UTC
instants are desired instead, request that separately; it requires a different
approved data-migration plan.
