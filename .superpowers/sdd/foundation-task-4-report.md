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
