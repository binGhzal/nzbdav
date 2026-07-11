# NZBDav Role Host And Durable Coordination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish role ownership, renewable compare-and-swap worker leases, durable ARR import receipts/outboxes, and bounded memory behavior while preserving the current all-in-one production runtime.

**Architecture:** Add the durable state and interfaces required by later container extraction before enabling any separated production role. `NZBDAV_ROLE=all` remains the only production path at the end of this plan; the role graph and contracts are testable, but gateway and worker activation belongs to the dependent plans.

**Tech Stack:** .NET 10, EF Core 10.0.9, SQLite/PostgreSQL, xUnit, Python unittest, Docker

## Global Constraints

- Follow `docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md`.
- Preserve SAB queue/history/status response compatibility.
- Keep rclone as the production mount.
- Keep SQLite as the default database and support PostgreSQL migrations.
- Do not add WebUI controls for leases, GC, provider scheduling, or circuit breakers.
- Do not activate separated production roles in this plan.
- All schema changes must be additive.
- All state transitions must be idempotent and cancellation-aware.
- Existing provider credentials and API keys must remain redacted.

---

### Task 1: Repair The Benchmark Baseline

**Files:**
- Modify: `scripts/nzbdav_benchmark.py:875-1060`
- Modify: `tests/test_nzbdav_benchmark.py`

**Interfaces:**
- Produces: `rclone_cat_enabled(args): bool`, which keeps direct `run_benchmark` callers that construct `argparse.Namespace` objects compatible when the optional parser field is absent.

- [ ] **Step 1: Preserve the current failing direct-call regression**

Keep `test_run_benchmark_reads_filesystem_mount_paths`, which currently creates
an `argparse.Namespace` without `rclone_cat`. Add focused helper tests:

```python
def test_rclone_cat_defaults_false_for_programmatic_namespace():
    assert nzbdav_benchmark.rclone_cat_enabled(Namespace()) is False

def test_rclone_cat_reads_explicit_namespace_value():
    assert nzbdav_benchmark.rclone_cat_enabled(Namespace(rclone_cat=True)) is True
```

- [ ] **Step 2: Run the focused test and confirm the current failure**

Run:

```bash
python3 -m unittest tests.test_nzbdav_benchmark -v
```

Expected: ERROR in `test_run_benchmark_reads_filesystem_mount_paths` with
`AttributeError: 'Namespace' object has no attribute 'rclone_cat'`, plus a
failure because `rclone_cat_enabled` does not exist. The CLI parser already
defines `--rclone-cat`.

- [ ] **Step 3: Normalize the optional field at the benchmark boundary**

Add one helper:

```python
def rclone_cat_enabled(args: argparse.Namespace) -> bool:
    return bool(getattr(args, "rclone_cat", False))
```

At the start of `run_benchmark`, set:

```python
include_rclone_cat = rclone_cat_enabled(args)
```

Use `include_rclone_cat` in the existing rclone-cat measurement branch and in
`inputs.rclone_cat_enabled`. Do not change the existing parser option.

- [ ] **Step 4: Run the Python suite**

Run:

```bash
python3 -m unittest discover -s tests -v
```

Expected: all Python tests pass.

- [ ] **Step 5: Commit**

```bash
git add scripts/nzbdav_benchmark.py tests/test_nzbdav_benchmark.py
git commit -m "fix: default optional rclone benchmark probes"
```

### Task 2: Add A Safe Runtime Role Model

**Files:**
- Create: `backend/Hosting/NzbdavRole.cs`
- Create: `backend/Hosting/NzbdavRoleResolver.cs`
- Create: `backend/Hosting/NzbdavRoleCapabilities.cs`
- Create: `backend.Tests/Hosting/NzbdavRoleResolverTests.cs`
- Modify: `backend/Program.cs:24-40`

**Interfaces:**
- Produces: `NzbdavRole`, `NzbdavCapability`, `NzbdavRoleResolver.Resolve(string? value)`, and `NzbdavRoleCapabilities.For(NzbdavRole role)`.
- Constraint: only `NzbdavRole.All` is executable until dependent plans register complete role hosts.

- [ ] **Step 1: Write role parsing and ownership tests**

```csharp
using NzbWebDAV.Hosting;

namespace backend.Tests.Hosting;

public sealed class NzbdavRoleResolverTests
{
    [Theory]
    [InlineData(null, NzbdavRole.All)]
    [InlineData("all", NzbdavRole.All)]
    [InlineData("control", NzbdavRole.Control)]
    [InlineData("gateway", NzbdavRole.Gateway)]
    [InlineData("worker-download", NzbdavRole.WorkerDownload)]
    [InlineData("worker-verify", NzbdavRole.WorkerVerify)]
    [InlineData("worker-repair", NzbdavRole.WorkerRepair)]
    [InlineData("ui", NzbdavRole.Ui)]
    public void ResolveMapsSupportedValues(string? value, NzbdavRole expected)
    {
        Assert.Equal(expected, NzbdavRoleResolver.Resolve(value));
    }

    [Fact]
    public void ResolveRejectsUnknownValues()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            NzbdavRoleResolver.Resolve("worker-everything"));
        Assert.Contains("Unsupported NZBDAV_ROLE", error.Message);
    }

    [Fact]
    public void GatewayOwnsProviderPoolAndWebDavButNotDatabase()
    {
        var capabilities = NzbdavRoleCapabilities.For(NzbdavRole.Gateway);
        Assert.Contains(NzbdavCapability.ProviderPool, capabilities);
        Assert.Contains(NzbdavCapability.WebDav, capabilities);
        Assert.DoesNotContain(NzbdavCapability.Database, capabilities);
    }
}
```

- [ ] **Step 2: Run the focused tests and confirm types are missing**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~NzbdavRoleResolverTests
```

Expected: build fails because `NzbWebDAV.Hosting` does not exist.

- [ ] **Step 3: Add the role and capability types**

```csharp
namespace NzbWebDAV.Hosting;

public enum NzbdavRole
{
    All,
    Control,
    Gateway,
    WorkerDownload,
    WorkerVerify,
    WorkerRepair,
    Ui,
}

public enum NzbdavCapability
{
    Database,
    AdminApi,
    SabApi,
    ArrBackground,
    Maintenance,
    WebDav,
    ProviderPool,
    SparseCache,
    DownloadWorker,
    VerifyWorker,
    RepairWorker,
    InternalRpc,
    UiFrontend,
}
```

Implement strict parsing:

```csharp
namespace NzbWebDAV.Hosting;

public static class NzbdavRoleResolver
{
    public static NzbdavRole Resolve(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => NzbdavRole.All,
            "control" => NzbdavRole.Control,
            "gateway" => NzbdavRole.Gateway,
            "worker-download" => NzbdavRole.WorkerDownload,
            "worker-verify" => NzbdavRole.WorkerVerify,
            "worker-repair" => NzbdavRole.WorkerRepair,
            "ui" => NzbdavRole.Ui,
            _ => throw new InvalidOperationException($"Unsupported NZBDAV_ROLE '{value}'.")
        };
    }
}
```

Implement the ownership graph explicitly; `All` is the union and no ownership
is inferred from string prefixes:

```csharp
public static class NzbdavRoleCapabilities
{
    private static readonly IReadOnlyDictionary<NzbdavRole, IReadOnlySet<NzbdavCapability>> Map =
        new Dictionary<NzbdavRole, IReadOnlySet<NzbdavCapability>>
        {
            [NzbdavRole.Control] = Set(
                NzbdavCapability.Database, NzbdavCapability.AdminApi,
                NzbdavCapability.SabApi, NzbdavCapability.ArrBackground,
                NzbdavCapability.Maintenance, NzbdavCapability.InternalRpc),
            [NzbdavRole.Gateway] = Set(
                NzbdavCapability.WebDav, NzbdavCapability.ProviderPool,
                NzbdavCapability.SparseCache, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerDownload] = Set(
                NzbdavCapability.DownloadWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerVerify] = Set(
                NzbdavCapability.VerifyWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerRepair] = Set(
                NzbdavCapability.RepairWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.Ui] = Set(NzbdavCapability.UiFrontend),
        };

    private static readonly IReadOnlySet<NzbdavCapability> All =
        Map.Values.SelectMany(x => x).ToHashSet();

    public static IReadOnlySet<NzbdavCapability> For(NzbdavRole role) =>
        role == NzbdavRole.All ? All : Map[role];

    private static IReadOnlySet<NzbdavCapability> Set(params NzbdavCapability[] values) =>
        values.ToHashSet();
}
```

- [ ] **Step 4: Parse the role at startup but fail safely for incomplete roles**

At the start of `Main`, resolve `NZBDAV_ROLE`. Until later plans activate the roles, reject non-all values before opening the database:

```csharp
var role = NzbdavRoleResolver.Resolve(EnvironmentUtil.GetVariable("NZBDAV_ROLE"));
if (role != NzbdavRole.All)
{
    throw new InvalidOperationException(
        $"NZBDAV_ROLE '{role}' is defined but not executable until its service implementation is installed.");
}
```

This prevents an operator from accidentally starting an incomplete split.

- [ ] **Step 5: Run focused and startup tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~NzbdavRoleResolverTests|FullyQualifiedName~ProgramStartup"
```

Expected: all selected tests pass and default startup remains `all`.

- [ ] **Step 6: Commit**

```bash
git add backend/Hosting backend/Program.cs backend.Tests/Hosting
git commit -m "refactor: define nzbdav runtime role ownership"
```

### Task 3: Add Renewable Lease State

**Files:**
- Modify: `backend/Database/Models/WorkerJob.cs`
- Create: `backend/Coordination/WorkerLease.cs`
- Modify: `backend/Database/DavDatabaseContext.cs:600-670`
- Modify: `backend/Database/DavDatabaseClient.cs:1882-1945`
- Create: `backend/Database/Migrations/20260711120000_Add-Worker-Lease-Coordination.cs`
- Modify: `backend/Database/Migrations/DavDatabaseContextModelSnapshot.cs`
- Modify: `backend/Database/DatabaseTransferService.cs`
- Modify: `backend.Tests/Database/WorkerJobLeaseTests.cs`

**Interfaces:**
- Produces: `WorkerLeaseIdentity`, `WorkerLease`, `WorkerJob.FailureClass`, and additive lease columns.

- [ ] **Step 1: Add failing model and migration tests**

Add assertions after leasing a job:

```csharp
Assert.NotEqual(Guid.Empty, downloadLease.LeaseToken);
Assert.Equal(1, downloadLease.LeaseGeneration);
Assert.Equal(now, downloadLease.StartedAt);
Assert.Equal(now, downloadLease.LastHeartbeatAt);
Assert.Null(downloadLease.CancelRequestedAt);
```

Add a migrated-schema assertion for these columns:

```csharp
var columns = await ReadSqliteColumnsAsync(dbContext, "WorkerJobs");
Assert.Contains("LeaseToken", columns);
Assert.Contains("LeaseGeneration", columns);
Assert.Contains("LastHeartbeatAt", columns);
Assert.Contains("StartedAt", columns);
Assert.Contains("CancelRequestedAt", columns);
Assert.Contains("FailureKind", columns);
Assert.Contains("ProgressJson", columns);
Assert.Contains("ProgressUpdatedAt", columns);
Assert.Contains("ResultJson", columns);
```

- [ ] **Step 2: Run the lease tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~WorkerJobLeaseTests
```

Expected: build fails because the lease properties do not exist.

- [ ] **Step 3: Extend the model with additive fields**

```csharp
public Guid? LeaseToken { get; set; }
public long LeaseGeneration { get; set; }
public DateTimeOffset? LastHeartbeatAt { get; set; }
public DateTimeOffset? StartedAt { get; set; }
public DateTimeOffset? CancelRequestedAt { get; set; }
public FailureClass? FailureKind { get; set; }
public string? ProgressJson { get; set; }
public DateTimeOffset? ProgressUpdatedAt { get; set; }
public string? ResultJson { get; set; }

public enum FailureClass
{
    Retryable = 1,
    Provider = 2,
    InvalidData = 3,
    Cancelled = 4,
    Permanent = 5,
}
```

Add immutable lease values:

```csharp
namespace NzbWebDAV.Coordination;

public readonly record struct WorkerLeaseIdentity(
    Guid JobId,
    string Owner,
    Guid Token,
    long Generation);

public sealed record WorkerLease(
    WorkerLeaseIdentity Identity,
    WorkerJob.JobKind Kind,
    Guid TargetId,
    int Priority,
    int Attempt,
    string? PayloadJson,
    DateTimeOffset ExpiresAt,
    bool CancellationRequested);
```

- [ ] **Step 4: Configure EF conversions and indexes**

Map nullable timestamps using the existing UTC tick conversion pattern. Map
`FailureKind` with `HasConversion<int?>()`. Limit `ProgressJson` and
`ResultJson` to 16 KiB before persistence; large results use the artifact
descriptor from the worker plan. Add an index on
`{ Status, LeaseExpiresAt, LeaseGeneration }` for expiry scans.

Create the migration with nullable columns and `LeaseGeneration` default `0`. Do not rewrite existing rows.

- [ ] **Step 5: Populate lease identity through the transitional in-process path**

Until Task 4 routes callers through `IWorkerJobCoordinator`, update
`LeaseNextWorkerJobCoreAsync` immediately before saving the selected job:

```csharp
job.LeaseToken = Guid.NewGuid();
job.LeaseGeneration += 1;
job.LastHeartbeatAt = referenceTime;
job.StartedAt = referenceTime;
job.CancelRequestedAt = null;
job.FailureKind = null;
job.ProgressJson = null;
job.ProgressUpdatedAt = null;
job.ResultJson = null;
```

Keep the existing owner, expiry, attempt, and transaction behavior. This makes
the additive fields valid for current in-process workers and gives Task 4 a
working compatibility path to replace with compare-and-swap mutations.

- [ ] **Step 6: Update database transfer compatibility**

The `WorkerJobs` list already transfers rows. Increment `DatabaseTransferSnapshot.CurrentVersion` to `2`, accept versions `1` and `2` during import, and let missing JSON properties deserialize to their default values:

```csharp
if (snapshot.Version is not (1 or DatabaseTransferSnapshot.CurrentVersion))
    throw new InvalidDataException($"Unsupported database transfer snapshot version {snapshot.Version}.");
```

- [ ] **Step 7: Run database tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~WorkerJobLeaseTests|FullyQualifiedName~DatabaseTransferServiceTests|FullyQualifiedName~DatabaseProviderSelectionTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit**

```bash
git add backend/Coordination backend/Database backend.Tests/Database
git commit -m "feat: persist renewable worker lease state"
```

### Task 4: Enforce Compare-And-Swap Lease Mutations

**Files:**
- Create: `backend/Coordination/IWorkerJobCoordinator.cs`
- Create: `backend/Coordination/DatabaseWorkerJobCoordinator.cs`
- Create: `backend/Coordination/IWorkerLaneCapacityPolicy.cs`
- Create: `backend/Coordination/ConfigWorkerLaneCapacityPolicy.cs`
- Create: `backend/Coordination/WorkerLeaseOptions.cs`
- Create: `backend.Tests/Coordination/DatabaseWorkerJobCoordinatorTests.cs`
- Modify: `backend/Database/DavDatabaseClient.cs:1882-2011`
- Modify: `backend/Queue/QueueManager.cs`
- Modify: `backend/Services/HealthCheckService.cs`

**Interfaces:**
- Consumes: `WorkerLeaseIdentity` and `WorkerLease` from Task 3.
- Produces: owner-checked lease, renew, complete, release, fail, and cancellation methods.

- [ ] **Step 1: Write stale-owner and renewal tests**

```csharp
[Fact]
public async Task StaleLeaseCannotCompleteAReLeasedJob()
{
    var first = Assert.Single(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

    var second = Assert.Single(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Verify, "worker-b", 1, now.AddMinutes(3), CancellationToken.None));
    Assert.True(second.Identity.Generation > first.Identity.Generation);

    Assert.False(await coordinator.CompleteAsync(
        first.Identity, null, now.AddMinutes(3), CancellationToken.None));
    Assert.True(await coordinator.CompleteAsync(
        second.Identity, "{}", now.AddMinutes(3), CancellationToken.None));
    Assert.True(await coordinator.CompleteAsync(
        second.Identity, "{}", now.AddMinutes(3), CancellationToken.None));
}

[Fact]
public async Task RenewExtendsOnlyTheCurrentLease()
{
    var lease = Assert.Single(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));

    Assert.True(await coordinator.RenewAsync(lease.Identity, now.AddSeconds(30), CancellationToken.None));
    Assert.False(await coordinator.RenewAsync(
        lease.Identity with { Token = Guid.NewGuid() }, now.AddSeconds(31), CancellationToken.None));
}

[Fact]
public async Task LeaseCapacityNeverExceedsConfiguredLaneMaximum()
{
    capacityPolicy.Set(WorkerJob.JobKind.Verify, 3);
    var leases = await coordinator.LeaseAsync(
        WorkerJob.JobKind.Verify, "worker-a", 128, now, CancellationToken.None);
    Assert.Equal(3, leases.Count);
}

[Fact]
public async Task CancellationRejectsRenewalAndCanBeAcknowledged()
{
    var lease = Assert.Single(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

    Assert.True(await coordinator.RequestCancellationAsync(
        lease.Identity.JobId, now.AddSeconds(10), CancellationToken.None));
    Assert.False(await coordinator.RenewAsync(
        lease.Identity, now.AddSeconds(30), CancellationToken.None));
    Assert.True(await coordinator.FailAsync(
        lease.Identity, WorkerJob.FailureClass.Cancelled, "cancelled by request",
        now.AddSeconds(30), 3, now.AddSeconds(30), CancellationToken.None));

    var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
    Assert.Equal(WorkerJob.JobStatus.Cancelled, job.Status);
}

[Fact]
public async Task ExpiredCancellationRequestIsTerminalizedInsteadOfReLeased()
{
    var lease = Assert.Single(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));
    Assert.True(await coordinator.RequestCancellationAsync(
        lease.Identity.JobId, now.AddSeconds(10), CancellationToken.None));

    Assert.Empty(await coordinator.LeaseAsync(
        WorkerJob.JobKind.Download, "worker-b", 1, now.AddMinutes(3), CancellationToken.None));
    var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
    Assert.Equal(WorkerJob.JobStatus.Cancelled, job.Status);
}
```

- [ ] **Step 2: Run the focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~DatabaseWorkerJobCoordinatorTests
```

Expected: build fails because `IWorkerJobCoordinator` is missing.

- [ ] **Step 3: Define the coordinator contract and timing**

```csharp
public interface IWorkerJobCoordinator
{
    Task<IReadOnlyList<WorkerLease>> LeaseAsync(
        WorkerJob.JobKind kind, string owner, int capacity,
        DateTimeOffset now, CancellationToken ct);
    Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct);
    Task<bool> ReportProgressAsync(
        WorkerLeaseIdentity lease, string progressJson, DateTimeOffset now, CancellationToken ct);
    Task<bool> CompleteAsync(
        WorkerLeaseIdentity lease, string? resultJson,
        DateTimeOffset now, CancellationToken ct);
    Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct);
    Task<bool> FailAsync(
        WorkerLeaseIdentity lease,
        WorkerJob.FailureClass failureKind,
        string error,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        DateTimeOffset now,
        CancellationToken ct);
    Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct);
}

public sealed record WorkerLeaseOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IWorkerLaneCapacityPolicy
{
    int GetMaximum(WorkerJob.JobKind kind);
}
```

`ConfigWorkerLaneCapacityPolicy` maps `Download`, `Verify`, and `Repair` to
`ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads()`,
`GetAdaptiveMaxConcurrentVerifyJobs()`, and
`GetAdaptiveMaxConcurrentRepairJobs()` respectively. It throws for an unknown
kind instead of sharing capacity between lanes.

- [ ] **Step 4: Implement compare-and-swap updates**

Use one authentication predicate and derive the active-work predicate from it:

```csharp
private IQueryable<WorkerJob> AuthenticatedLease(WorkerLeaseIdentity lease) =>
    _db.WorkerJobs.Where(job =>
        job.Id == lease.JobId &&
        job.Status == WorkerJob.JobStatus.Leased &&
        job.LeaseOwner == lease.Owner &&
        job.LeaseToken == lease.Token &&
        job.LeaseGeneration == lease.Generation);

private IQueryable<WorkerJob> ActiveLease(WorkerLeaseIdentity lease) =>
    AuthenticatedLease(lease).Where(job => job.CancelRequestedAt == null);
```

Renew with one conditional update and return `changed == 1`:

```csharp
var changed = await ActiveLease(lease).ExecuteUpdateAsync(setters => setters
    .SetProperty(job => job.LastHeartbeatAt, now)
    .SetProperty(job => job.LeaseExpiresAt, now + _options.Duration)
    .SetProperty(job => job.UpdatedAt, now), ct);
return changed == 1;
```

Lease acquisition assigns a new token, increments generation, sets heartbeat,
start, expiry, and clears cancellation/failure. Completion, release,
non-cancelled failure, renewal, and progress use `ActiveLease` and never attach
a detached entity supplied by a worker. A `Cancelled` failure uses
`AuthenticatedLease`, transitions the job to `Cancelled`, records completion
and failure state, and clears the expiry; this is the worker's idempotent
cancellation acknowledgement.

At the start of the lease-acquisition transaction, transition expired leased
rows with `CancelRequestedAt != null` to `Cancelled` before counting active
leases or selecting candidates. Candidate selection must explicitly require
`CancelRequestedAt == null`. This handles a worker that exits after cancellation
without acknowledging it and prevents cancelled work from being leased again.

`LeaseAsync` clamps requested capacity to `1..128` and enforces the existing
per-kind configured maximum across all unexpired leases, so another worker
process cannot exceed the download, verify, or repair limit.

`ReportProgressAsync` updates `ProgressJson` and `ProgressUpdatedAt` only for the
active lease. `CompleteAsync` stores the bounded result descriptor and returns
`true` when the exact owner/token/generation is already completed; stale
generations return `false`. `RequestCancellationAsync` sets
`CancelRequestedAt` idempotently on a leased job; renewal requires that field to
remain null, so the next renewal is rejected and the worker cancels locally.

After a conditional completion updates one row, preserve owner, token, and
generation on the terminal job. If zero rows update, perform this idempotency
check before returning false:

```csharp
return await _db.WorkerJobs.AsNoTracking().AnyAsync(job =>
    job.Id == lease.JobId &&
    job.Status == WorkerJob.JobStatus.Completed &&
    job.LeaseOwner == lease.Owner &&
    job.LeaseToken == lease.Token &&
    job.LeaseGeneration == lease.Generation &&
    job.ResultJson == resultJson, ct);
```

- [ ] **Step 5: Adapt existing in-process workers through the coordinator**

Inject `IWorkerJobCoordinator` into `QueueManager` and `HealthCheckService`.
Store `WorkerLeaseIdentity` on in-progress work instead of relying on the tracked
`WorkerJob` entity. Preserve existing queue/history behavior.

- [ ] **Step 6: Run coordination and queue tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~DatabaseWorkerJobCoordinatorTests|FullyQualifiedName~WorkerJobLeaseTests|FullyQualifiedName~QueueProcessingLoopTests|FullyQualifiedName~HealthCheckRepairPolicyTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```bash
git add backend/Coordination backend/Database/DavDatabaseClient.cs backend/Queue backend/Services/HealthCheckService.cs backend.Tests
git commit -m "feat: enforce worker lease ownership and renewal"
```

### Task 5: Persist Completed-Symlink Import Receipts

**Files:**
- Create: `backend/Database/Models/ImportReceipt.cs`
- Create: `backend/Services/ImportReceiptService.cs`
- Create: `backend/Services/ImportReceiptReconciliationService.cs`
- Create: `backend/Database/Migrations/20260711133000_Add-Import-Receipts.cs`
- Modify: `backend/Database/DavDatabaseContext.cs`
- Modify: `backend/Database/DatabaseTransferService.cs`
- Modify: `backend/Database/DavDatabaseClient.cs:609-640`
- Modify: `backend/Queue/QueueItemProcessor.cs:194-224,424-440`
- Modify: `backend/WebDav/DatabaseStoreSymlinkCollection.cs:19-145`
- Modify: `backend/Services/ArrOperationsService.cs:253-350`
- Modify: `backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryController.cs`
- Create: `backend.Tests/Services/ImportReceiptServiceTests.cs`
- Create: `backend.Tests/Services/ImportReceiptReconciliationServiceTests.cs`
- Create: `backend.Tests/WebDav/CompletedSymlinkImportTests.cs`
- Modify: `backend.Tests/Services/ArrOperationsServiceTests.cs`
- Modify: `backend.Tests/Api/GetHistoryControllerTests.cs`

**Interfaces:**
- Produces: `ImportReceipt`, `ImportReceiptState`, `ImportClaimRequest`, `ImportReceiptService.StageAvailableReceiptsAsync(Guid historyItemId, DateTimeOffset now, CancellationToken ct)`, and `ImportReceiptService.ClaimAsync(ImportClaimRequest request, CancellationToken ct)`.

- [ ] **Step 1: Write durable and idempotent claim tests**

```csharp
[Fact]
public async Task ClaimPersistsAcrossContextsAndIsIdempotent()
{
    var first = await service.ClaimAsync(
        new ImportClaimRequest(davItem.Id, history.Id, now), CancellationToken.None);
    var second = await service.ClaimAsync(
        new ImportClaimRequest(davItem.Id, history.Id, now.AddSeconds(1)), CancellationToken.None);

    Assert.Equal(ImportReceiptState.UnlinkClaimed, first.State);
    Assert.Equal(first.Id, second.Id);

    await using var reopened = await fixture.CreateMigratedContextAsync();
    var saved = await reopened.ImportReceipts.SingleAsync();
    Assert.Equal(ImportReceiptState.UnlinkClaimed, saved.State);
}
```

Add a WebDAV test proving claimed items remain hidden after a new store and
database context are created.

Add lifecycle tests proving an ARR import event marks the matching history
receipt `Imported`, and SAB history removal marks it `Removed` before deleting
the history row.

Add reconciliation tests proving a matching organized-library symlink marks a
stale claim `Imported`, an unresolved claim becomes `NeedsReview`, and neither
case returns to `Available`.

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~ImportReceiptServiceTests|FullyQualifiedName~CompletedSymlinkImportTests"
```

Expected: build fails because the receipt model and service do not exist.

- [ ] **Step 3: Add the receipt model**

```csharp
public sealed class ImportReceipt
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public Guid HistoryItemId { get; set; }
    public ImportReceiptState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? Detail { get; set; }
}

public enum ImportReceiptState
{
    Available = 0,
    UnlinkClaimed = 1,
    Imported = 2,
    Removed = 3,
    NeedsReview = 4,
}
```

Configure a unique index on `{ DavItemId, HistoryItemId }` and indexes on
`State` and `UpdatedAt`. Keep IDs as scalar references without cascade-delete
foreign keys because the receipt must survive history/DAV cleanup.

- [ ] **Step 4: Implement transactional state transitions**

Create an `Available` receipt in the same download-finalization transaction that
creates a completed `HistoryItem`. `ClaimAsync` must find or create the receipt and transition only `Available` to
`UnlinkClaimed`. `MarkImportedAsync`, `MarkRemovedAsync`, and
`MarkNeedsReviewAsync` must be idempotent and must not move a `Removed` receipt
backward.

Return a result object instead of throwing for repeated valid transitions:

```csharp
public sealed record ImportReceiptResult(
    Guid Id,
    ImportReceiptState State,
    bool Changed);

public sealed record ImportClaimRequest(
    Guid DavItemId,
    Guid HistoryItemId,
    DateTimeOffset Now);
```

`StageAvailableReceiptsAsync` gathers non-deleted `UsenetFile` entries with the
matching `HistoryItemId` from both the current change tracker and the database,
deduplicates by `DavItem.Id`, queries existing receipts once, and adds only the
missing `Available` rows to the current context without calling `SaveChanges`.
In `QueueItemProcessor.MarkQueueItemCompleted`, call it for a successful history
item after aggregation/post-processing and immediately before the existing
`SaveChangesAsync`. The history row, DAV items, worker-job enqueue, rclone
invalidation, and available receipts therefore commit atomically in one EF
transaction. Failed history items do not stage available receipts.

- [ ] **Step 5: Replace the 30-second memory cache**

Remove `DeletedFileManager`. `GetItemAsync` and `GetAllItemsAsync` query claimed
receipt IDs for the relevant completed category. `DeleteItemAsync` resolves the
target `DavItem` and `HistoryItemId`, calls `ClaimAsync`, saves before returning
`NoContent`, and returns `ServiceUnavailable` when the durable claim cannot be
committed.

When `ArrOperationsService` normalizes an `Import` or `Download` custom-script
event with a correlated history item, call `MarkImportedAsync` in the same
transaction as the lifecycle event. Before `RemoveHistoryItemsAsync` deletes a
history row, call `MarkRemovedAsync`; when `deleteFiles=1`, enqueue cleanup only
after that state transition is durable. Repeated ARR and SAB events remain
idempotent.

`ImportReceiptReconciliationService` runs in control every five minutes and
processes at most 100 `UnlinkClaimed` receipts older than five minutes. Use
`OrganizedLinksUtil` against the read-only library mount to confirm a link to
the claimed DAV item. Mark a confirmed link `Imported`; mark an unresolved
claim older than 30 minutes `NeedsReview`. Never restore it to `Available`.

- [ ] **Step 6: Include receipts in database transfer**

Add `ImportReceipts` to export, import, clear ordering, and total row count.
Keep version-1 snapshots compatible by defaulting the list to empty.

- [ ] **Step 7: Run WebDAV, database, and ARR tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~ImportReceipt|FullyQualifiedName~CompletedSymlink|FullyQualifiedName~GetHistoryControllerTests|FullyQualifiedName~DatabaseTransferServiceTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit**

```bash
git add backend/Database backend/Services/ImportReceiptService.cs backend/WebDav/DatabaseStoreSymlinkCollection.cs backend.Tests
git commit -m "feat: persist completed symlink import receipts"
```

### Task 6: Add Manifest And ARR Repair Outboxes

**Files:**
- Create: `backend/Database/Models/ManifestChange.cs`
- Create: `backend/Database/Models/ManifestConsumerCheckpoint.cs`
- Create: `backend/Database/Models/ArrRepairCommand.cs`
- Create: `backend/Services/ManifestChangeService.cs`
- Create: `backend/Services/ArrRepairCommandService.cs`
- Create: `backend/Services/ArrRepairCommandDispatcherService.cs`
- Create: `backend/Database/Migrations/20260711150000_Add-Control-Outboxes.cs`
- Modify: `backend/Database/DavDatabaseContext.cs:1244-1305`
- Modify: `backend/Database/DatabaseTransferService.cs`
- Modify: `backend/Services/HealthCheckService.cs:1767-1940`
- Modify: `backend/Program.cs`
- Create: `backend.Tests/Services/ManifestChangeServiceTests.cs`
- Create: `backend.Tests/Services/ArrRepairCommandServiceTests.cs`

**Interfaces:**
- Produces: ordered `ManifestChange.Sequence`, durable `ManifestConsumerCheckpoint`, durable `ArrRepairCommand`, `ManifestChangeService.AppendSnapshotBoundaryAsync(string sha256, int itemCount, DateTimeOffset now, CancellationToken ct)`, and `ArrRepairCommandDispatcherService`.

- [ ] **Step 1: Write atomicity and idempotency tests**

Test that a committed DAV add/delete creates a manifest change in the same
transaction and a rolled-back transaction creates none. Test that an ARR repair
command with the same deduplication key is stored once and retry does not issue
another command after an ARR command ID is recorded. Acknowledge manifest
sequences `2`, `1`, then `2` for consumer `gateway` and prove the durable
checkpoint remains `2`.

```csharp
Assert.Equal(
    new long[] { 1, 2 },
    await db.ManifestChanges.OrderBy(x => x.Sequence).Select(x => x.Sequence).ToArrayAsync());

Assert.Single(await db.ArrRepairCommands
    .Where(x => x.DeduplicationKey == deduplicationKey)
    .ToListAsync());

Assert.Equal(2, await db.ManifestConsumerCheckpoints
    .Where(x => x.ConsumerId == "gateway")
    .Select(x => x.Sequence)
    .SingleAsync());
```

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~ManifestChangeServiceTests|FullyQualifiedName~ArrRepairCommandServiceTests"
```

Expected: build fails because the outbox models do not exist.

- [ ] **Step 3: Add ordered manifest changes**

```csharp
public sealed class ManifestChange
{
    public long Sequence { get; set; }
    public Guid ChangeId { get; set; }
    public Guid? DavItemId { get; set; }
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ManifestConsumerCheckpoint
{
    public string ConsumerId { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

Use a database-generated integer sequence and create changes from the same
`SaveChangesWithBlobsAndInvalidations` pass that already captures added and
removed DAV items. Payloads contain complete immutable item metadata needed by
the gateway plan; delete payloads contain ID, path, and prior revision.
Use `ConsumerId` as the checkpoint primary key. Acknowledgements only move the
sequence forward with a conditional update; duplicate or older acknowledgements
are no-ops.

`AppendSnapshotBoundaryAsync` appends a `snapshot-boundary` change containing
the snapshot SHA-256 and item count after a complete snapshot has been written.
Compaction computes the minimum acknowledged sequence across consumers, finds
the newest boundary at or below that minimum, and deletes only older changes.
It always retains that boundary and every newer change. With no consumer
checkpoint or no eligible boundary, it deletes nothing. Add tests for reconnect
from the retained boundary and for a lagging consumer preventing compaction.

- [ ] **Step 4: Add durable ARR repair commands**

```csharp
public sealed class ArrRepairCommand
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public string ArrInstanceKey { get; set; } = "";
    public string CommandName { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string DeduplicationKey { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int? ArrCommandId { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
}
```

Use a unique index on `DeduplicationKey`. Implement `pending -> executing ->
executed|retry|failed` with conditional updates.

`ArrRepairCommandDispatcherService` is the only background executor. In `all`
and later `control`, it polls every five seconds, conditionally leases at most
20 available `pending`/`retry` rows by changing them to `executing`, and invokes
`ArrRepairCommandService`. It recovers `executing` rows whose heartbeat is older
than two minutes to `retry`. Cancellation stops new leases and does not convert
in-flight ambiguous ARR outcomes to success. Register the dispatcher once in
`Program`; separated roles remain rejected in this plan.

- [ ] **Step 5: Move repair side effects behind the outbox**

Change `HealthCheckService.Repair` to persist a repair command and leave the DAV
item in place. `ArrRepairCommandService` executes the current `RemoveAndSearch`
logic. Only after confirmed success does it transactionally record repair
success and remove the DAV item. On ambiguous failure, query ARR state before
issuing another command.

- [ ] **Step 6: Include all three outbox tables in database transfer**

Add ordered export/import/clear handling for manifest changes, manifest consumer
checkpoints, and ARR repair commands, and update row totals. A transfer must
preserve `ManifestChange.Sequence`, checkpoint values, ARR command IDs, and
retry state. Older version-1/version-2 snapshots default all three lists to
empty.

- [ ] **Step 7: Run repair, recovery, and transfer tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~ManifestChange|FullyQualifiedName~ArrRepairCommand|FullyQualifiedName~HealthCheckRepairPolicyTests|FullyQualifiedName~ContentIndexRecoveryServiceTests|FullyQualifiedName~DatabaseTransferServiceTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit**

```bash
git add backend/Database backend/Services backend.Tests/Services
git commit -m "feat: persist manifest and arr repair outboxes"
```

### Task 7: Remove Forced GC And Add Bounded Runtime Metrics

**Files:**
- Modify: `backend/Queue/QueueManager.cs:320-340`
- Modify: `backend/Services/ContentIndexSnapshotWriterService.cs:9-31`
- Create: `backend/Services/RuntimeMetricsSnapshot.cs`
- Create: `backend/Services/RuntimeMetricsSampler.cs`
- Create: `backend/Services/GcPauseWindow.cs`
- Create: `backend/Utils/LinuxProcessMemoryReader.cs`
- Modify: `backend/Api/SabControllers/GetStatus/GetStatusController.cs`
- Modify: `backend/Api/SabControllers/GetFullStatus/GetFullStatusController.cs`
- Create: `backend.Tests/Services/RuntimeMetricsSamplerTests.cs`
- Create: `backend.Tests/Utils/LinuxProcessMemoryReaderTests.cs`
- Modify: `backend.Tests/Services/ContentIndexRecoveryServiceTests.cs`

**Interfaces:**
- Produces: passive `RuntimeMetricsSnapshot`, a 1024-entry GC pause window, Linux PSS collection, and no normal runtime call to `GC.Collect`.

- [ ] **Step 1: Add tests for bounded signals, PSS, pause history, and forced-GC removal**

```csharp
[Fact]
public void ParsesPssFromSmapsRollup()
{
    const string text = "Rss: 2048 kB\nPss: 1536 kB\n";
    Assert.Equal(1_572_864, LinuxProcessMemoryReader.ParsePssBytes(text));
}

[Fact]
public void PauseWindowRetainsOnlyNewestSamples()
{
    var window = new GcPauseWindow(capacity: 3);
    foreach (var value in new[] { 1d, 2d, 3d, 4d }) window.Add(value);
    Assert.Equal(new[] { 2d, 3d, 4d }, window.GetOrderedSamples());
}

[Fact]
public void QueueManagerContainsNoExplicitCollection()
{
    var path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../backend/Queue/QueueManager.cs"));
    var source = File.ReadAllText(path);
    Assert.DoesNotContain("GC.Collect(", source, StringComparison.Ordinal);
}

[Fact]
public void RepeatedSnapshotRequestsCoalesceIntoOneQueuedSignal()
{
    for (var i = 0; i < 10_000; i++) ContentIndexSnapshotWriterService.RequestSnapshot();
    Assert.Equal(1, ContentIndexSnapshotWriterService.QueuedSignalCountForTests);
}
```

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~RuntimeMetrics|FullyQualifiedName~LinuxProcessMemoryReader|FullyQualifiedName~ContentIndexRecoveryServiceTests"
```

Expected: build fails because the new metrics types and test seam are missing;
the source assertion also fails while `GC.Collect` remains.

- [ ] **Step 3: Delete forced compaction from queue completion**

Remove `TryCompactManagedHeapAfterLargeQueueItem`, `_lastMemoryCompactionTicks`,
`GCSettings.LargeObjectHeapCompactionMode`, and the blocking `GC.Collect` call.
Do not replace them with another explicit collection.

- [ ] **Step 4: Use a capacity-one coalescing channel**

```csharp
private static readonly Channel<byte> Requests = Channel.CreateBounded<byte>(
    new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });
```

Keep `_pendingRequestCount` for durability accounting, but queue at most one
wake signal.

- [ ] **Step 5: Define the per-process runtime snapshot**

```csharp
public sealed record RuntimeMetricsSnapshot(
    string Role,
    string InstanceId,
    int ProcessId,
    double CpuCores,
    long WorkingSetBytes,
    long? ProportionalSetBytes,
    bool IsServerGc,
    long ManagedBytes,
    long HeapBytes,
    long FragmentedBytes,
    long TotalAllocatedBytes,
    double AllocationBytesPerSecond,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseP50Ms,
    double GcPauseP95Ms,
    double GcPauseP99Ms,
    int ThreadPoolThreads,
    long ThreadPoolPendingItems,
    DateTimeOffset CapturedAt);
```

`RuntimeMetricsSampler` samples every five seconds. Derive CPU cores from
`Process.TotalProcessorTime` delta divided by wall-clock delta, allocation rate
from `GC.GetTotalAllocatedBytes(false)` delta, and PSS from
`/proc/self/smaps_rollup` when available. On non-Linux systems, PSS is null.

- [ ] **Step 6: Record GC pauses without an event-listener allocation loop**

Track `GC.GetGCMemoryInfo().Index`. When the index changes, append each value in
`PauseDurations` to a lock-protected `GcPauseWindow(1024)`. Snapshot a sorted
copy only when status is requested and calculate p50/p95/p99 from that bounded
copy. Keep .NET dynamic GC behavior unchanged and set no `DOTNET_GC*` overrides.

- [ ] **Step 7: Register and expose the sampler**

Register one singleton sampler and hosted service in every backend role.
`status/fullstatus` expose the snapshot additively. Query-string API keys,
payloads, paths, and environment values are not included.

- [ ] **Step 8: Run focused and full backend tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj
```

Expected: all backend tests pass and the source scan finds no explicit normal
runtime collection.

- [ ] **Step 9: Commit**

```bash
git add backend/Queue/QueueManager.cs backend/Services backend/Utils backend/Api/SabControllers backend.Tests
git commit -m "perf: remove forced gc and bound runtime telemetry"
```

### Task 8: Complete Foundation Gates And Documentation

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `CHANGELOG.md`
- Modify: `.env.example`
- Modify: `docs/setup-guide.md`

**Interfaces:**
- Produces: repeatable migration and all-in-one compatibility gates.

- [ ] **Step 1: Add CI checks for migrations and role safety**

Add commands that run the role parser tests, SQLite migration smoke, PostgreSQL
migration smoke, and confirm a non-all role exits nonzero until its dependent
implementation lands:

```bash
docker run --rm -e NZBDAV_ROLE=gateway -e FRONTEND_BACKEND_API_KEY=ci-role-test nzbdav:ci
```

The step passes only when the container exits nonzero with the explicit
"not executable" message.

- [ ] **Step 2: Run all repository gates**

```bash
dotnet test backend.Tests/backend.Tests.csproj
dotnet build backend/NzbWebDAV.csproj --no-restore
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run build:server
npm --prefix frontend run test:e2e
python3 -m unittest discover -s tests -v
docker build -t nzbdav:coordination-ci .
git diff --check
```

Expected: every command exits `0`.

- [ ] **Step 3: Update operator documentation**

Document that `NZBDAV_ROLE` is reserved for the staged split and only `all` is
enabled by this release. Document the new lease/import diagnostics without
adding operator knobs.

- [ ] **Step 4: Review the diff for accidental runtime activation**

Run:

```bash
git diff --stat HEAD~7
rg -n "NZBDAV_ROLE|LeaseToken|ImportReceipt|ManifestChange|ArrRepairCommand" backend docs .github
```

Expected: no Compose or production default enables a separated role.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml CHANGELOG.md .env.example docs
git commit -m "docs: document durable coordination foundation"
```

## Completion Gate

This plan is complete only when:

- The full all-in-one application remains functionally compatible.
- All worker state mutations are lease-token and generation checked.
- Completed symlink unlink survives process restart.
- ARR repair side effects are represented durably before execution.
- Manifest changes are ordered and replayable.
- Normal queue completion performs no explicit full GC.
- All tests and build gates pass.
- No separated role is enabled in production yet.
