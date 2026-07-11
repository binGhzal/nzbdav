# Foundation Task 4 Report: Enforce Compare-And-Swap Lease Mutations

## Status

Implemented and verified. Worker acquisition and mutation now authenticate durable leases by job ID, owner, token, and generation. Queue, verification, and repair workers retain lease identities, renew leases, and stop local work when renewal is rejected or fails.

## RED Evidence

Command:

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~DatabaseWorkerJobCoordinatorTests --no-restore
```

Initial expected failure:

```text
error CS0246: The type or namespace name 'DatabaseWorkerJobCoordinator' could not be found
error CS0246: The type or namespace name 'IWorkerLaneCapacityPolicy' could not be found
```

The first invocation also found a missing test-fixture namespace import. After correcting that test-only setup issue, RED was rerun and failed only on the absent Task 4 coordinator API.

## GREEN Evidence

Coordinator-only tests after implementation:

```text
Passed: 9, Failed: 0, Skipped: 0, Total: 9
```

The coordinator suite covers:

- stale owner/token/generation rejection after re-lease
- exact completion idempotency
- renewal CAS and lease extension
- configured lane capacity across owners
- concurrent acquisition through two database contexts
- progress, release, and failure CAS behavior
- cancellation renewal rejection and acknowledgement
- cancellation request and acknowledgement idempotency
- expired cancellation terminalization without re-lease

Focused command from the brief:

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~DatabaseWorkerJobCoordinatorTests|FullyQualifiedName~WorkerJobLeaseTests|FullyQualifiedName~QueueProcessingLoopTests|FullyQualifiedName~HealthCheckRepairPolicyTests" --no-restore
```

Result:

```text
Passed: 87, Failed: 0, Skipped: 0, Total: 87
```

Complete backend suite:

```bash
dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

Result:

```text
Passed: 599, Failed: 0, Skipped: 1, Total: 600
```

The skipped test is the existing PostgreSQL migration integration test, which requires its external PostgreSQL test environment.

## Implementation

- Added `IWorkerJobCoordinator` with lease, renew, progress, complete, release, fail, and cancellation operations.
- Added `DatabaseWorkerJobCoordinator` using conditional `ExecuteUpdateAsync` mutations authenticated by job ID, owner, token, generation, and active cancellation state.
- Added serializable lease acquisition that terminalizes expired cancellation requests, counts all unexpired lane leases, enforces configured capacity, selects only uncancelled candidates, issues a new token, and increments generation.
- Preserved exact completion identity and result for idempotent completion; stale generations return false.
- Made cancellation terminal: renewal rejects requested cancellation, workers acknowledge it as `Cancelled`, and expired requests are terminalized before candidate selection.
- Added lane-specific capacity policy mapping Download, Verify, and Repair to the existing adaptive configuration methods.
- Added bounded UTF-8 progress/result storage and bounded failure text.
- Adapted QueueManager and HealthCheckService to hold `WorkerLeaseIdentity`/`WorkerLease`, renew in-progress work, and use coordinator CAS mutations.
- Preserved SAB queue creation, ordering, processing outcomes, queue/history behavior, and in-process lane snapshots.
- Refactored transitional `DavDatabaseClient` worker mutations to authenticate through the coordinator and refresh compatibility entities without detached writes.
- Registered options, capacity policy, and the singleton-safe database coordinator in all-role DI.

## Files

- `backend/Coordination/IWorkerJobCoordinator.cs`
- `backend/Coordination/DatabaseWorkerJobCoordinator.cs`
- `backend/Coordination/IWorkerLaneCapacityPolicy.cs`
- `backend/Coordination/ConfigWorkerLaneCapacityPolicy.cs`
- `backend/Coordination/WorkerLeaseOptions.cs`
- `backend.Tests/Coordination/DatabaseWorkerJobCoordinatorTests.cs`
- `backend/Database/DavDatabaseClient.cs`
- `backend/Queue/QueueManager.cs`
- `backend/Services/HealthCheckService.cs`
- `backend/Program.cs`
- `backend.Tests/Database/WorkerJobLeaseTests.cs`
- `backend.Tests/Services/HealthCheckRepairPolicyTests.cs`

## Self-Review

- Race conditions: capacity count and candidate acquisition occur in one serializable transaction; candidate updates repeat eligibility predicates; a two-context SQLite race test confirms no lane oversubscription.
- Stale generations: every mutation authenticates owner, token, and generation; terminal completion and cancellation acknowledgement have exact-identity idempotency checks.
- Cancellation: active mutations reject `CancelRequestedAt`; cancellation acknowledgement alone uses the authenticated lease; acquisition terminalizes expired requested cancellations before counting or selection.
- Compatibility: worker callers no longer write tracked/detached `WorkerJob` entities; legacy database-client mutation methods delegate through CAS and only refresh returned compatibility state.
- DI lifetime: the singleton coordinator creates and disposes one `DavDatabaseContext` per operation; test-only constructors can use a supplied context.

## Concerns

- PostgreSQL runtime concurrency was not exercised because the repository's PostgreSQL integration environment was unavailable and its existing migration test was skipped. The implementation uses provider-supported EF relational updates and serializable transactions, but a live PostgreSQL acquisition race remains the primary residual verification item.

## Review Fix Wave

### Status

Addressed the Critical, Important, and Minor Task 4 review findings in one follow-up wave. The prior PostgreSQL verification concern is resolved using the supplied live test database.

### RED Evidence

Cancellation, re-enqueue, timing, acknowledgement, and truncation filter:

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~CancelWorkerJobsAsync_RequestsCancellation|FullyQualifiedName~ResetsAllStaleState|FullyQualifiedName~CancellationAcknowledgementRequires|FullyQualifiedName~InvalidLeaseTimingOptions|FullyQualifiedName~FailureTruncation" --no-restore
```

Initial result: 8 failed, 1 passed. Failures showed leased jobs becoming `Cancelled`, stale cancellation/heartbeat state surviving re-enqueue, invalid timing options being accepted, and unrequested cancellation acknowledgement succeeding. The truncation assertion was then strengthened to require 1,023 valid non-surrogate characters at the boundary.

Rejected terminal mutation filter:

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~QueueManagerRejected|FullyQualifiedName~RejectedHealthWorker" --no-restore
```

Initial result: 4 failed. QueueManager returned a non-result task, HealthCheckService had no checked completion path, and rejected failure incorrectly inferred `Retry`. One subsequent retry-test failure was a missing `QueueItem` in the reflection fixture and was corrected before GREEN.

Live PostgreSQL contention:

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=55434;Database=nzbdav;Username=nzbdav;Password=nzbdav' \
  dotnet test backend.Tests/backend.Tests.csproj \
  --filter FullyQualifiedName~DatabaseWorkerJobCoordinatorPostgreSqlTests --no-restore
```

Initial result: 1 failed with PostgreSQL SQLSTATE `40001` from overlapping serializable acquisition.

Repair-run cancellation:

```bash
dotnet test backend.Tests/backend.Tests.csproj \
  --filter FullyQualifiedName~CancelRepairRunAsync_RequestsCancellationForLeasedJob --no-restore
```

Initial result: 1 failed because a leased repair-run job became `Cancelled` and lost its active status.

Atomic cancellation:

```bash
dotnet test backend.Tests/backend.Tests.csproj \
  --filter FullyQualifiedName~CancelWorkerJobsAsync_UsesOneAtomicWorkerJobUpdate --no-restore
```

After correcting non-query interceptor coverage, RED reported 2 worker-job UPDATE statements. The final helper uses one conditional UPDATE and refreshes matching tracked entities.

### Fixes

- `CancelWorkerJobsAsync` and repair-run cancellation now use one atomic conditional update: Pending, Retry, and Quarantined rows become terminal `Cancelled`; Leased rows retain owner, token, generation, status, and expiry while receiving `CancelRequestedAt`.
- Matching tracked worker entities are reloaded after direct cancellation so same-context re-enqueue observes persisted state without clearing unrelated tracked repair-run changes.
- Cancelled worker acknowledgement now requires both exact lease authentication and a non-null cancellation request.
- Single and batch terminal re-enqueue share one reset helper that clears cancellation, ownership, token, expiry, heartbeat, start, failure, progress, result, completion, and error state while preserving monotonic generation.
- PostgreSQL lease acquisition retries the entire serializable transaction up to five attempts only for SQLSTATE `40001` and `40P01`, with cancellation-aware bounded backoff. No partial transaction is retried.
- The live PostgreSQL test uses a unique schema and separate contexts/owners, forces overlap with a schema-local trigger, covers one- and two-candidate contention, asserts capacity non-oversubscription, and renews/completes every returned lease.
- QueueManager and HealthCheckService now branch on every terminal coordinator boolean. Rejected completion/failure/release is treated as lost ownership, rejected failure does not infer retry/quarantine, and rejected cancellation acknowledgement does not fall through to release.
- `WorkerLeaseOptions` validates positive duration and renewal interval with renewal shorter than duration. Validation runs in coordinator/worker construction and through DI `ValidateOnStart`.
- Failure truncation backs off before a high surrogate at the 1,024-character boundary.

### GREEN Evidence

Focused cancellation and repair-run checks:

```text
Passed: 4, Failed: 0, Skipped: 0, Total: 4
```

Rejected terminal mutation checks:

```text
Passed: 4, Failed: 0, Skipped: 0, Total: 4
```

Expanded SQLite coordination, queue, health, lease, and repair focused set:

```text
Passed: 113, Failed: 0, Skipped: 0, Total: 113
```

Live PostgreSQL migration and coordinator contention set:

```text
Passed: 2, Failed: 0, Skipped: 0, Total: 2
```

Complete backend suite with the PostgreSQL environment:

```text
Passed: 618, Failed: 0, Skipped: 0, Total: 618
```

### Concerns

- No blocking concerns remain. The forced live contention test exercises SQLSTATE `40001`; the narrowly included `40P01` deadlock retry is justified because the whole serializable transaction is discarded, but that rarer state is not separately forced by the test.

### Final Required Verification

```bash
dotnet test backend.Tests/backend.Tests.csproj \
  --filter "FullyQualifiedName~DatabaseWorkerJobCoordinatorTests|FullyQualifiedName~WorkerJobLeaseTests|FullyQualifiedName~QueueProcessingLoopTests|FullyQualifiedName~HealthCheckRepairPolicyTests" \
  --no-restore
```

```text
Passed: 103, Failed: 0, Skipped: 0, Total: 103
```

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=55434;Database=nzbdav;Username=nzbdav;Password=nzbdav' \
  dotnet test backend.Tests/backend.Tests.csproj \
  --filter FullyQualifiedName~DatabaseWorkerJobCoordinatorPostgreSqlTests --no-restore
```

```text
Passed: 1, Failed: 0, Skipped: 0, Total: 1
```

```bash
NZBDAV_TEST_POSTGRES_CONNECTION_STRING='Host=localhost;Port=55434;Database=nzbdav;Username=nzbdav;Password=nzbdav' \
  dotnet test backend.Tests/backend.Tests.csproj --no-restore
```

```text
Passed: 618, Failed: 0, Skipped: 0, Total: 618
```

```bash
git diff --check
```

Result: clean.
