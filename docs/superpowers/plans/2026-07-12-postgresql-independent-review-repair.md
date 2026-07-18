# PostgreSQL Independent Review Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the disabled native PostgreSQL migration path prove exact PostgreSQL 16.14 environment, security, empty/baseline/head catalog, and lease behavior before any mutation, with required non-skippable CI evidence.

**Architecture:** Keep the public PostgreSQL runtime refusal unchanged. Split the internal migration gate into an environment/security contract, a structured catalog capturer with checked-in baseline and head inventories, and a same-session migrator that validates before DDL and after migration. The generated baseline retains an independent inline guard so direct EF invocation cannot bypass the safety contract.

**Tech Stack:** .NET 10, EF Core 10, Npgsql 10, PostgreSQL 16.14 Alpine, xUnit, GitHub Actions/TRX.

## Global Constraints

- PostgreSQL public/runtime selection remains disabled before connection-secret access.
- PostgreSQL server must be exactly `16.14` / `server_version_num=160014`.
- Tests use a uniquely named owned `postgres:16.14-alpine` container, random loopback port, and tmpfs; the existing gate container/port 32783 is untouched.
- Every production behavior change follows red-green TDD.
- SQLite migrations, source manifest, and provider behavior remain frozen.
- Existing shared-worktree edits are preserved; no staging, committing, or destructive Git commands.

---

### Task 1: Required PostgreSQL test harness

**Files:**
- Modify: `backend.Tests/Database/PostgreSqlFactAttribute.cs`
- Modify: `backend.Tests/Database/PostgreSqlTestSchema.cs`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces `NZBDAV_REQUIRE_POSTGRES_TESTS=1` behavior: missing connection data runs/fails instead of skipping.
- Produces PG-first and full-suite TRX files whose counters are parsed to require `notExecuted=0` and `failed=0`.

- [ ] Add a failing unit test for required-mode behavior with the connection variable absent.
- [ ] Run that test and verify the attribute currently sets `Skip`.
- [ ] Leave `Skip` unset in required mode and expose exact target schema/history options in `PostgreSqlTestSchema`.
- [ ] Run the attribute/harness tests green.
- [ ] Add required env/TRX output and XML counter verification to both CI test steps; parse workflow YAML.

### Task 2: Exact pre-DDL environment and security gate

**Files:**
- Create: `backend/Database/PostgreSqlEnvironmentContract.cs`
- Modify: `backend/Database/PostgreSqlNativeMigrator.cs`
- Test: `backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs`

**Interfaces:**
- Produces `PostgreSqlEnvironmentContract.ValidateAsync(NpgsqlConnection, string, CancellationToken)`.
- Validates exact server patch/number, one explicit/effective schema, no `pg_temp`, current role/database ownership, exclusive schema/database CREATE, safe default ACL, and absence of event/publication/subscription state before lock or DDL.

- [ ] Add zero-mutation failing tests for wrong expected patch, multi-schema search path, temporary shadow relation, event trigger, FOR ALL TABLES publication, schema publication, subscription, unsafe schema/database/default ACL, and wrong owner.
- [ ] Run each focused test and verify refusal is currently missing or occurs after mutation.
- [ ] Implement the environment/security query with schema/database OID parameters and structured privilege checks.
- [ ] Run all environment tests green and compare the exact before/after mutation fingerprint.

### Task 3: Structured exact empty/baseline/head catalogs

**Files:**
- Rewrite: `backend/Database/PostgreSqlPhysicalCatalogContract.cs`
- Create: `backend.Tests/TestData/postgresql-native-empty-history-catalog.txt`
- Create: `backend.Tests/TestData/postgresql-native-baseline-catalog.txt`
- Create: `backend.Tests/TestData/postgresql-native-head-catalog.txt`
- Replace: `backend.Tests/TestData/postgresql-native-schema-contract.json`
- Modify: `backend.Tests/Database/PostgreSqlNativeContractManifestTests.cs`
- Modify: `backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs`

**Interfaces:**
- Produces structured `CaptureCanonicalAsync(connection, targetSchema)` without raw schema-name replacement.
- Produces independently hashed inspectable inventories for exact empty history, baseline prefix, and head.

- [ ] Add failing baseline drift tests for column storage/options, rules, inheritance, disabled internal FK triggers, ACL/owner, invalid index, constraint state, namespace operator/opclass/opfamily/conversion/text-search/type/extended-statistics objects, event/publication/subscription state, and TOAST metadata.
- [ ] Expand canonical lines to cover table/TOAST/access/options/tablespace/ACL, all physical column fields, constraint parent flags, full index state, rewrite/inheritance state, stable internal constraint triggers, namespace closure, schema/database/default ACL, and global replication/event objects.
- [ ] Generate and check in complete line inventories; make tests assert inventory contents/counts and SHA-256, not only opaque totals.
- [ ] Make preflight select exact empty/baseline/head inventory by history prefix before `MigrateAsync`.
- [ ] Run adversarial tests green and prove byte-for-byte zero mutation.

### Task 4: Direct-EF inline baseline guard

**Files:**
- Modify: `backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.cs`
- Regenerate/update: baseline designer and PostgreSQL snapshot only if model metadata changes.
- Test: `backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs`

**Interfaces:**
- The first baseline `DO` statement accepts only EF's exact empty, owner-only history table in the one safe target schema and refuses before application-table creation otherwise.

- [ ] Add direct `context.Database.MigrateAsync` failing tests for extra/mistyped/reordered/dropped history columns, malformed PK/index, changed owner/ACL, extra object, multi-path/temp shadow, and nonempty history.
- [ ] Verify each currently mutates or fails without the required guard error.
- [ ] Implement exact inline catalog/security predicates using schema-qualified catalog OIDs.
- [ ] Run direct-EF tests green and update pinned migration file hashes.

### Task 5: Advisory-lock failure closure

**Files:**
- Modify: `backend/Database/PostgreSqlNativeMigrator.cs`
- Test: `backend.Tests/Database/PostgreSqlNativeMigrationIntegrationTests.cs`

**Interfaces:**
- If unlock fails, the migrator closes/disposes even a caller-owned connection while preserving the primary exception.

- [ ] Add a real two-session failing test that acquires scope A, invokes unlock
  with unowned scope B after provider-owned operations have unwound, and proves
  that closing the idle failed-unlock session releases A for the second session.
  Do not model cleanup with an active COPY/import operation: Npgsql keeps that
  physical connector busy until the provider-owned operation is unwound.
- [ ] Implement bounded cleanup that closes/disposes on any unlock failure and
  records the cleanup failure without masking a primary failure.
- [ ] Run the two-session test green and prove the second session can acquire the lock.

### Task 6: Lease and local-wall edge contracts

**Files:**
- Modify: `backend/Coordination/DatabaseWorkerJobCoordinator.cs`
- Modify: `backend/Database/LocalWallQueryBounds.cs`
- Test: `backend.Tests/Coordination/DatabaseWorkerJobCoordinatorPostgreSqlTests.cs`
- Test: `backend.Tests/Database/DavDatabaseClientProviderSqlTests.cs`

**Interfaces:**
- Expired cancellation terminalization is lane-scoped by `job.Kind == kind`.
- Exclusive lower `>` bounds floor to a PostgreSQL microsecond.
- Orphan Download jobs are neither leased nor reported ready; generated claims contain no `random()`.

- [ ] Add failing concurrent-lane cross-cancellation test.
- [ ] Add exclusive-lower boundary, orphan stats, and generated SQL ordering tests.
- [ ] Implement the kind predicate and explicit exclusive-lower helper.
- [ ] Run focused lease/query tests green, then repeated synchronized concurrency tests.

### Task 7: Final verification and independent review

**Files:**
- Verify all changed files; no new production surface.

- [ ] Run Release build with zero warnings.
- [ ] Run exact PG-first filter with required mode and zero skipped/notExecuted.
- [ ] Run all PostgreSQL tests with zero skips.
- [ ] Run SQLite migration/provider/timestamp freeze filters.
- [ ] Run full backend suite with required PostgreSQL mode.
- [ ] Verify public PostgreSQL refusal occurs before secret read.
- [ ] Parse workflow YAML, verify inventories/hashes, and run `git diff --check`.
- [ ] Remove the owned container and confirm the unknown gate container remains untouched.
- [ ] Request fresh independent review and report exact commands/results.
