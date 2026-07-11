# NZBDav Lane Worker Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run download, verify, and repair as independent lease-renewing worker containers that use the gateway for provider work and control for every durable state transition.

**Architecture:** Expose the Task 1 job coordinator over private authenticated gRPC, build one common worker runtime, and extract existing executors lane by lane. Workers never open the application database; large download results cross a bounded temporary artifact exchange and are committed by control.

**Tech Stack:** .NET 10, ASP.NET Core gRPC 2.80.0, EF Core, MemoryPack, xUnit, Docker

## Global Constraints

- Complete `2026-07-11-nzbdav-role-host-durable-coordination.md` and `2026-07-11-nzbdav-gateway-data-plane.md` first.
- Follow `docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md`.
- Workers never receive database or provider credentials.
- One worker process owns one lane only.
- Existing maximum download, verify, and repair settings remain independent.
- Unknown/provider-error verification results never trigger repair.
- Worker results are accepted only for the current lease token and generation.
- Keep `NZBDAV_ROLE=all` working until production rollout is complete.
- Do not add worker/lease tuning to the WebUI.

---

### Task 1: Expose The Job Coordinator Over Private gRPC

**Files:**
- Create: `backend/Coordination/Grpc/job_coordinator.proto`
- Create: `backend/Coordination/Grpc/JobCoordinatorGrpcService.cs`
- Create: `backend/Coordination/Grpc/GrpcWorkerJobCoordinator.cs`
- Create: `backend/Coordination/Grpc/WorkerJobGrpcMapper.cs`
- Create: `backend/Coordination/IWorkerJobResultHandler.cs`
- Create: `backend/Coordination/WorkerJobResultRouter.cs`
- Modify: `backend/NzbWebDAV.csproj`
- Create: `backend.Tests/Coordination/GrpcWorkerJobCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IWorkerJobCoordinator`, `WorkerLease`, and internal-token interceptor.
- Produces: transport parity for lease, renew, progress, fail, release, and cancel plus lane-routed transactional completion.

- [ ] **Step 1: Write a coordinator transport contract suite**

Run the same tests against `DatabaseWorkerJobCoordinator` and
`GrpcWorkerJobCoordinator`. Include stale token, stale generation, cancellation,
deadline, and unavailable-control cases.

```csharp
public abstract class WorkerJobCoordinatorContractTests
{
    protected abstract Task<IWorkerJobCoordinator> CreateCoordinatorAsync();

    [Fact]
    public async Task RejectedRenewalDoesNotExtendLease()
    {
        var coordinator = await CreateCoordinatorAsync();
        var now = DateTimeOffset.UtcNow;
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker", 1, now, CancellationToken.None));
        Assert.False(await coordinator.RenewAsync(
            lease.Identity with { Token = Guid.NewGuid() }, now, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Define protobuf operations**

```protobuf
service WorkerJobCoordinatorRpc {
  rpc LeaseJob(LeaseJobRequest) returns (LeaseJobReply);
  rpc RenewLease(LeaseMutationRequest) returns (LeaseMutationReply);
  rpc ReportProgress(ProgressRequest) returns (LeaseMutationReply);
  rpc CompleteJob(CompleteJobRequest) returns (LeaseMutationReply);
  rpc FailJob(FailJobRequest) returns (LeaseMutationReply);
  rpc ReleaseJob(LeaseMutationRequest) returns (LeaseMutationReply);
  rpc RequestCancellation(CancelJobRequest) returns (LeaseMutationReply);
}

service WorkerStatusRpc {
  rpc GetStatus(WorkerStatusRequest) returns (WorkerStatusReply);
}

service WorkerJobInputRpc {
  rpc ReadVerifyInput(LeaseMutationRequest) returns (stream VerifyInputFrame);
}

message LeaseJobRequest {
  int32 kind = 1;
  string worker_id = 2;
  int32 capacity = 3;
  int64 now_unix_ms = 4;
}

message LeaseJobReply {
  repeated WorkerLeaseMessage leases = 1;
}

message LeaseIdentityMessage {
  string job_id = 1;
  string owner = 2;
  string token = 3;
  int64 generation = 4;
}

message WorkerLeaseMessage {
  LeaseIdentityMessage identity = 1;
  int32 kind = 2;
  string target_id = 3;
  int32 priority = 4;
  int32 attempt = 5;
  string payload_json = 6;
  int64 expires_unix_ms = 7;
  bool cancellation_requested = 8;
}

message LeaseMutationRequest {
  LeaseIdentityMessage lease = 1;
  int64 now_unix_ms = 2;
}

message ProgressRequest {
  LeaseIdentityMessage lease = 1;
  string progress_json = 2;
  int64 now_unix_ms = 3;
}

message CompleteJobRequest {
  LeaseIdentityMessage lease = 1;
  string result_json = 2;
  int64 now_unix_ms = 3;
}

message FailJobRequest {
  LeaseIdentityMessage lease = 1;
  int32 failure_kind = 2;
  string error = 3;
  int64 retry_after_unix_ms = 4;
  int32 max_attempts = 5;
  int64 now_unix_ms = 6;
}

message CancelJobRequest {
  string job_id = 1;
  int64 now_unix_ms = 2;
}

message LeaseMutationReply {
  bool accepted = 1;
}

message WorkerStatusRequest {}

message WorkerStatusReply {
  string role = 1;
  string instance_id = 2;
  int32 active_slots = 3;
  int32 max_slots = 4;
  repeated string current_job_ids = 5;
  int64 accepted_renewals = 6;
  int64 rejected_renewals = 7;
  int64 retries = 8;
  int64 quarantines = 9;
  string last_error = 10;
  string runtime_metrics_json = 11;
}

message VerifyInputFrame {
  oneof payload {
    VerifyInputHeader header = 1;
    VerifySegmentInput segment = 2;
  }
}

message VerifyInputHeader {
  string dav_item_id = 1;
  string path = 2;
  int32 batch_size = 3;
  int64 recent_good_ttl_ms = 4;
  bool post_download = 5;
  string repair_run_id = 6;
}

message VerifySegmentInput {
  string segment_id = 1;
  int32 recent_state = 2;
  int64 recent_checked_unix_ms = 3;
}
```

Represent GUIDs as canonical lowercase strings and timestamps as Unix
milliseconds. Clamp capacity to `1..128`; control still enforces the configured
lane maximum. Reject malformed values and result/progress JSON over 16 KiB with
`InvalidArgument`.

`ReadVerifyInput` validates the current lease, writes exactly one header, then
streams segment/hint frames without loading all segment IDs into one gRPC
message. The client batches at most 256 frames before provider STAT work and
cancels the stream when the lease is rejected.

- [ ] **Step 3: Implement service-token authentication and mapping**

Reuse `InternalTokenInterceptor`. Never serialize database entities directly.
Map only immutable `WorkerLease` fields. Return `accepted=false` for stale valid
leases and gRPC errors only for malformed/authentication/transport failures.

Route completion by durable job kind rather than marking a job complete before
its result is committed:

```csharp
public interface IWorkerJobResultHandler
{
    WorkerJob.JobKind Kind { get; }
    Task<bool> CommitAsync(
        WorkerLeaseIdentity lease, string resultJson,
        DateTimeOffset now, CancellationToken ct);
}
```

`WorkerJobResultRouter` loads the leased job kind without accepting a worker
supplied kind and invokes exactly one registered handler. Until a lane-specific
handler is installed, `CompleteJob` rejects that remote lane as unavailable.
Verify, download, and repair tasks in this plan add their handlers before their
roles are activated. The handler owns one transaction containing domain state,
follow-up jobs, lifecycle/outboxes, and final worker-job completion.

- [ ] **Step 4: Make unavailable control cancel-safe**

The client distinguishes `Unavailable`/deadline from an explicit rejected lease.
Expose:

```csharp
public sealed class WorkerCoordinatorUnavailableException(string message, Exception inner)
    : IOException(message, inner);
```

Worker runtime may continue only until its known lease expiry.

- [ ] **Step 5: Run transport tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~WorkerJobCoordinator
```

Expected: database and gRPC implementations pass the same suite.

- [ ] **Step 6: Commit**

```bash
git add backend/Coordination/Grpc backend/NzbWebDAV.csproj backend.Tests/Coordination
git commit -m "feat: expose worker coordination rpc"
```

### Task 2: Build A Common Lease-Renewing Worker Runtime

**Files:**
- Create: `backend/Workers/IWorkerJobExecutor.cs`
- Create: `backend/Workers/WorkerRuntime.cs`
- Create: `backend/Workers/WorkerRuntimeOptions.cs`
- Create: `backend/Workers/WorkerInstanceStatus.cs`
- Create: `backend/Workers/WorkerFailureClassifier.cs`
- Create: `backend/Workers/WorkerCpuScheduler.cs`
- Create: `backend.Tests/Workers/WorkerRuntimeTests.cs`
- Create: `backend.Tests/Workers/WorkerCpuSchedulerTests.cs`

**Interfaces:**
- Consumes: remote `IWorkerJobCoordinator` and one lane-specific executor.
- Produces: bounded lease loop with heartbeat, cancellation, progress, and status.

- [ ] **Step 1: Write crash, renewal, and capacity tests**

```csharp
[Fact]
public async Task StopsExecutorWhenRenewalIsRejected()
{
    var executor = new BlockingExecutor();
    coordinator.RenewResult = false;
    await runtime.RunOneCycleAsync(CancellationToken.None);
    Assert.True(executor.ObservedCancellation);
    Assert.False(coordinator.Completed);
}

[Fact]
public async Task NeverRunsMoreThanAdvertisedCapacity()
{
    var executor = new CountingExecutor();
    await runtime.RunUntilIdleAsync(CancellationToken.None);
    Assert.InRange(executor.MaximumActive, 1, options.MaxLocalSlots);
}

[Fact]
public async Task CpuSchedulerUsesSeveralCoresWithoutExceedingItsBound()
{
    using var scheduler = new WorkerCpuScheduler(maximumConcurrency: 4);
    var tracker = new ConcurrentWorkTracker();
    await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
        scheduler.RunAsync(() => tracker.RunCpuWork(), CancellationToken.None)));
    Assert.InRange(tracker.MaximumActive, 1, 4);
}

private sealed class ConcurrentWorkTracker
{
    private int _active;
    private int _maximum;
    public int MaximumActive => Volatile.Read(ref _maximum);

    public int RunCpuWork()
    {
        var active = Interlocked.Increment(ref _active);
        var observed = Volatile.Read(ref _maximum);
        while (active > observed)
        {
            var original = Interlocked.CompareExchange(ref _maximum, active, observed);
            if (original == observed) break;
            observed = original;
        }
        Thread.SpinWait(200_000);
        Interlocked.Decrement(ref _active);
        return active;
    }
}
```

- [ ] **Step 2: Define the executor contract**

```csharp
public interface IWorkerJobExecutor
{
    WorkerJob.JobKind Kind { get; }
    Task<WorkerExecutionResult> ExecuteAsync(
        WorkerLease lease,
        IProgress<WorkerProgress> progress,
        CancellationToken ct);
}

public sealed record WorkerProgress(
    string Stage,
    double Percent,
    long? CompletedUnits,
    long? TotalUnits);

public abstract record WorkerExecutionResult
{
    public sealed record Completed(string? ResultJson) : WorkerExecutionResult;
    public sealed record Failed(
        WorkerJob.FailureClass FailureKind,
        string Error,
        DateTimeOffset RetryAt,
        int MaxAttempts) : WorkerExecutionResult;
    public sealed record Released(string Reason) : WorkerExecutionResult;
}

public sealed record WorkerRuntimeOptions
{
    public int MaxLocalSlots { get; init; } = 128;
    public TimeSpan EmptyPollDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
}
```

- [ ] **Step 3: Implement bounded lease loops**

Use one dispatcher loop per process. It computes
`free = MaxLocalSlots - active.Count`, calls `LeaseJob(kind, workerId, free)`,
and starts only the leases returned by control's per-lane capacity policy. An
empty reply waits `EmptyPollDelay`; transport failure uses capped exponential
backoff from one to 30 seconds. Each active job has one renewal loop at
30-second intervals, links host/job/lease cancellation tokens, and cancels when
renewal is rejected or the known expiry passes. Do not use `Task.Run` for async
network or disk I/O.

- [ ] **Step 4: Add one bounded CPU scheduler per worker process**

Use a `ConcurrentExclusiveSchedulerPair` over the .NET thread pool for CPU-only
parsing, archive metadata, parity, and hashing work:

```csharp
public sealed class WorkerCpuScheduler : IDisposable
{
    private readonly ConcurrentExclusiveSchedulerPair _pair;
    private readonly TaskFactory _factory;

    public WorkerCpuScheduler(int maximumConcurrency)
    {
        _pair = new ConcurrentExclusiveSchedulerPair(
            TaskScheduler.Default, Math.Clamp(maximumConcurrency, 1, 8));
        _factory = new TaskFactory(
            CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            TaskContinuationOptions.None, _pair.ConcurrentScheduler);
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) =>
        _factory.StartNew(work, ct);

    public void Dispose() => _pair.Complete();
}
```

Default download CPU concurrency is
`Math.Clamp(Environment.ProcessorCount - 1, 1, 8)`; repair uses at most four;
verify remains asynchronous I/O and does not wrap STAT work in CPU tasks. One
process-wide scheduler prevents job-level loops from multiplying CPU
parallelism. These are code defaults, not WebUI settings.

- [ ] **Step 5: Implement failure classification**

Map existing exception types explicitly:

```csharp
return error switch
{
    OperationCanceledException => WorkerJob.FailureClass.Cancelled,
    RetryableDownloadException => WorkerJob.FailureClass.Retryable,
    CouldNotConnectToUsenetException => WorkerJob.FailureClass.Provider,
    InvalidDataException => WorkerJob.FailureClass.InvalidData,
    NonRetryableDownloadException => WorkerJob.FailureClass.Permanent,
    _ => WorkerJob.FailureClass.Retryable,
};
```

Unknown exceptions receive bounded retry and structured diagnostics; they are
not silently marked completed.

- [ ] **Step 6: Expose worker status**

Record instance ID, role, active slots, max slots, current job IDs, lease expiry,
renewal failures, CPU, managed heap, RSS, GC counters, and last error. Do not
include payloads or secrets.

- [ ] **Step 7: Run worker runtime and CPU-bound tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~WorkerRuntimeTests|FullyQualifiedName~WorkerCpuSchedulerTests"
```

- [ ] **Step 8: Commit**

```bash
git add backend/Workers backend.Tests/Workers
git commit -m "feat: add renewable lane worker runtime"
```

### Task 3: Extract Verification Into A Stateless Executor

**Files:**
- Create: `backend/Workers/Verify/VerifyJobInput.cs`
- Create: `backend/Workers/Verify/VerifyJobResult.cs`
- Create: `backend/Workers/Verify/VerifyJobExecutor.cs`
- Create: `backend/Workers/Verify/VerifyResultCommitter.cs`
- Modify: `backend/Services/HealthCheckService.cs`
- Create: `backend.Tests/Workers/VerifyJobExecutorTests.cs`
- Create: `backend.Tests/Workers/VerifyResultCommitterTests.cs`

**Interfaces:**
- Consumes: immutable file/segment manifest and remote article gateway.
- Produces: verification result DTO without database writes and
  `VerifyResultCommitter : IWorkerJobResultHandler` in control.

- [ ] **Step 1: Write executor purity and result-state tests**

```csharp
[Theory]
[InlineData(SegmentCheckState.Exists, false)]
[InlineData(SegmentCheckState.Missing, true)]
[InlineData(SegmentCheckState.ProviderError, false)]
[InlineData(SegmentCheckState.Unknown, false)]
public async Task OnlyDefinitiveMissingRequestsRepair(
    SegmentCheckState state, bool repairExpected)
{
    var result = await executor.ExecuteAsync(BuildInput(state), progress, CancellationToken.None);
    Assert.Equal(repairExpected, result.ShouldEnqueueRepair);
}
```

Verify the executor can run with no `DavDatabaseContext` service registered.

- [ ] **Step 2: Define immutable input and output DTOs**

```csharp
public sealed record VerifyJobInput(
    Guid DavItemId,
    string Path,
    int BatchSize,
    TimeSpan RecentGoodTtl,
    bool PostDownload,
    Guid? RepairRunId);

public sealed record VerifySegmentInput(
    string SegmentId,
    SegmentCheckState? RecentState,
    DateTimeOffset? RecentCheckedAt);

public sealed record VerifyJobResult(
    Guid DavItemId,
    int Checked,
    int Missing,
    int ProviderErrors,
    int Unknown,
    IReadOnlyList<string> MissingSamples,
    IReadOnlyList<string> ProviderErrorSamples,
    bool SamplesTruncated,
    bool ShouldEnqueueRepair,
    TimeSpan ProviderElapsed);
```

`VerifyJobExecutor` consumes the header plus
`IAsyncEnumerable<VerifySegmentInput>`. Keep at most 64 missing segment IDs and
16 provider-error IDs as redacted diagnostics; counts remain exact and
`SamplesTruncated` records omitted IDs. The serialized result therefore stays
below the 16 KiB coordinator limit even for very large releases.

- [ ] **Step 3: Move checking logic out of `HealthCheckService`**

Move segment metadata normalization, batch checking, dedupe, recent-good reuse,
and state aggregation into `VerifyJobExecutor`. Process the input stream in
bounded batches and never retain the complete segment list. Keep DB query and
commit behavior in `VerifyResultCommitter` under control.

- [ ] **Step 4: Make commit transactional and lease-aware**

For a current lease, commit health result, next-check time, repair entry, optional
repair job, lifecycle event, and job completion in one transaction. Reject a
stale lease before applying any result.

- [ ] **Step 5: Adapt all-in-one verification through the executor**

`HealthCheckService` becomes scheduling/compatibility glue and calls the same
executor and committer used remotely. Existing behavior remains testable before
activating the worker role.

- [ ] **Step 6: Run verification suites**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~VerifyJob|FullyQualifiedName~SegmentCheck|FullyQualifiedName~HealthCheckRepairPolicyTests"
```

- [ ] **Step 7: Commit**

```bash
git add backend/Workers/Verify backend/Services/HealthCheckService.cs backend.Tests/Workers
git commit -m "refactor: isolate verification job execution"
```

### Task 4: Activate The Verify Worker Role

**Files:**
- Create: `backend/Hosting/VerifyWorkerHost.cs`
- Modify: `backend/Program.cs`
- Modify: `entrypoint.sh`
- Create: `backend.Tests/Hosting/VerifyWorkerHostTests.cs`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: executable `NZBDAV_ROLE=worker-verify`.

- [ ] **Step 1: Write dependency ownership tests**

Assert verify role resolves worker runtime, `VerifyJobExecutor`, remote job
coordinator, and remote article gateway. Assert it cannot resolve database,
provider pool, sparse cache, WebDAV, SAB, ARR background, download, or repair
executors.

- [ ] **Step 2: Register verify role and health endpoints**

Remove the startup guard for `WorkerVerify`. Require
`NZBDAV_CONTROL_URL`, `NZBDAV_INTERNAL_GATEWAY_URL`, and
`NZBDAV_INTERNAL_TOKEN`. Missing values fail startup. Worker roles listen on
HTTP/1 port 8080 for `/health/live` and `/health/ready` and HTTP/2 port 8081 for
authenticated `WorkerStatusRpc`; they map no SAB, admin, WebDAV, or job
coordinator service.

- [ ] **Step 3: Add container crash/re-lease smoke test**

Start fake control/gateway plus verify worker, lease a blocking job, kill the
worker, advance lease time, start replacement worker, and assert generation
increments and stale completion is rejected.

- [ ] **Step 4: Run role and verification tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~VerifyWorker|FullyQualifiedName~VerifyJob"
```

- [ ] **Step 5: Commit**

```bash
git add backend/Hosting backend/Program.cs entrypoint.sh backend.Tests/Hosting .github/workflows/ci.yml
git commit -m "feat: activate isolated verification worker"
```

### Task 5: Create A Bounded Download Artifact Exchange

**Files:**
- Create: `backend/Workers/Download/ProcessedDownloadArtifactHeader.cs`
- Create: `backend/Workers/Download/ProcessedFileArtifact.cs`
- Create: `backend/Workers/Download/ProcessedDownloadArtifactReader.cs`
- Create: `backend/Workers/Download/ProcessedDownloadArtifactWriter.cs`
- Create: `backend/Workers/Artifacts/ArtifactExchange.cs`
- Create: `backend/Workers/Artifacts/ArtifactDescriptor.cs`
- Create: `backend/Workers/Artifacts/ArtifactExchangeOptions.cs`
- Create: `backend/Workers/Artifacts/ArtifactCleanupService.cs`
- Create: `backend.Tests/Workers/ArtifactExchangeTests.cs`
- Create: `backend.Tests/Workers/ProcessedDownloadArtifactTests.cs`

**Interfaces:**
- Produces: `ArtifactDescriptor`, streamed raw NZB inputs, versioned MemoryPack results, reusable job-scoped input artifacts, lease-scoped result artifacts, and an atomic bounded exchange protocol.

- [ ] **Step 1: Write traversal, hash, partial-write, and cleanup tests**

```csharp
await Assert.ThrowsAsync<InvalidDataException>(() =>
    exchange.OpenVerifiedAsync(
        descriptor with { RelativePath = "../../config/db.sqlite" },
        lease,
        CancellationToken.None));

Assert.False(File.Exists(partialPath));
Assert.True(File.Exists(readyPath));
Assert.Equal(expectedSha256, descriptor.Sha256);
```

- [ ] **Step 2: Define the artifact descriptor and limits**

```csharp
public enum ArtifactKind { DownloadInput = 1, DownloadResult = 2 }

public sealed record ArtifactDescriptor(
    ArtifactKind Kind,
    Guid JobId,
    Guid? LeaseToken,
    long? LeaseGeneration,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record ArtifactExchangeOptions
{
    public string Root { get; init; } = "/cache/exchange";
    public long MaxArtifactBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxRecordBytes { get; init; } = 64 * 1024 * 1024;
    public long MaxExchangeBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public TimeSpan OrphanTtl { get; init; } = TimeSpan.FromHours(6);
}
```

These are internal safety defaults, not WebUI settings.

- [ ] **Step 3: Define versioned MemoryPack result DTOs**

```csharp
[MemoryPackable]
public partial class ProcessedDownloadArtifactHeader
{
    public int Version { get; set; } = 1;
    public Guid QueueItemId { get; set; }
    public string Category { get; set; } = "";
    public string JobName { get; set; } = "";
    public bool QueuePostDownloadVerification { get; set; }
    public int FileCount { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(PlainFileArtifact))]
[MemoryPackUnion(1, typeof(RarFileArtifact))]
[MemoryPackUnion(2, typeof(SevenZipFileArtifact))]
[MemoryPackUnion(3, typeof(MultipartMkvFileArtifact))]
public abstract partial class ProcessedFileArtifact;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class NzbFileArtifact
{
    [MemoryPackOrder(0)] public string Subject { get; set; } = "";
    [MemoryPackOrder(1)] public List<NzbSegmentArtifact> Segments { get; set; } = [];
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class NzbSegmentArtifact
{
    [MemoryPackOrder(0)] public int Number { get; set; }
    [MemoryPackOrder(1)] public long Bytes { get; set; }
    [MemoryPackOrder(2)] public string MessageId { get; set; } = "";
}

[MemoryPackable]
public partial class PlainFileArtifact : ProcessedFileArtifact
{
    public NzbFileArtifact NzbFile { get; set; } = new();
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTimeOffset ReleaseDate { get; set; }
}

[MemoryPackable]
public partial class RarFileArtifact : ProcessedFileArtifact
{
    public List<RarStoredSegmentArtifact> StoredSegments { get; set; } = [];
}

[MemoryPackable]
public partial class RarStoredSegmentArtifact
{
    public NzbFileArtifact NzbFile { get; set; } = new();
    public long PartSize { get; set; }
    public string ArchiveName { get; set; } = "";
    public int? PartNumberFromHeader { get; set; }
    public int? PartNumberFromFilename { get; set; }
    public string PathWithinArchive { get; set; } = "";
    public LongRange ByteRangeWithinPart { get; set; } = null!;
    public AesParams? AesParams { get; set; }
    public long FileUncompressedSize { get; set; }
    public DateTimeOffset ReleaseDate { get; set; }
}

[MemoryPackable]
public partial class SevenZipFileArtifact : ProcessedFileArtifact
{
    public List<SevenZipEntryArtifact> Entries { get; set; } = [];
}

[MemoryPackable]
public partial class SevenZipEntryArtifact
{
    public string PathWithinArchive { get; set; } = "";
    public DavMultipartFile.Meta Metadata { get; set; } = new();
    public DateTimeOffset ReleaseDate { get; set; }
}

[MemoryPackable]
public partial class MultipartMkvFileArtifact : ProcessedFileArtifact
{
    public string FileName { get; set; } = "";
    public List<DavMultipartFile.FilePart> Parts { get; set; } = [];
    public DateTimeOffset ReleaseDate { get; set; }
}
```

The mapper converts every `NzbFile` segment, `LongRange`, segment slice,
archive part number, AES metadata, release date, and size; no processor result
object crosses the process boundary directly.

The result file is not one serialized object. `ProcessedDownloadArtifactWriter`
writes magic `NZBDAVR1`, a little-endian 32-bit header length, one header, then
one little-endian 32-bit length plus one MemoryPack `ProcessedFileArtifact` per
record. `ProcessedDownloadArtifactReader` validates magic/version/count/record
limits and exposes `IAsyncEnumerable<ProcessedFileArtifact>` so control retains
only the current record plus durable entities being committed.

- [ ] **Step 4: Implement atomic artifact writes**

Use a canonical path:

```text
/cache/exchange/{jobId:N}/{generation}/{token:N}.ready
```

Write `.tmp`, serialize records incrementally, flush with `Flush(true)`, atomically
rename, calculate SHA-256, and return only a relative path.

Download inputs use `/cache/exchange/inputs/{jobId:N}.nzb.ready` and omit token
and generation because the immutable input is reused after lease expiry.
Control copies the blob to this file with pooled 128 KiB buffers while hashing;
it never materializes the NZB as a `byte[]`. Control creates or validates this
input before making a download job leaseable.

- [ ] **Step 5: Implement verified reads and cleanup**

Validate job ID, token, generation, containment, extension, exact size, and hash
before deserialization. Enforce artifact and per-record limits before and during writes.
Cleanup deletes committed outputs, terminal-job inputs, and `.tmp`/`.ready`
orphans only when no current lease owns them. `ArtifactCleanupService` runs in
control, where it can query durable leases; workers delete only temp files they
created before publication. Control startup runs one cleanup pass.

- [ ] **Step 6: Run artifact tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~ArtifactExchange|FullyQualifiedName~ProcessedDownloadArtifact"
```

- [ ] **Step 7: Commit**

```bash
git add backend/Workers/Artifacts backend/Workers/Download backend.Tests/Workers
git commit -m "feat: add bounded worker artifact exchange"
```

### Task 6: Extract Download Processing And Control Commit

**Files:**
- Create: `backend/Workers/Download/DownloadJobInput.cs`
- Create: `backend/Workers/Download/DownloadJobExecutor.cs`
- Create: `backend/Workers/Download/DownloadJobInputMaterializer.cs`
- Create: `backend/Workers/Download/DownloadResultCommitter.cs`
- Create: `backend/Workers/Download/ProcessorResultArtifactMapper.cs`
- Modify: `backend/Queue/QueueItemProcessor.cs`
- Modify: `backend/Queue/QueueManager.cs`
- Modify: `backend/Api/SabControllers/AddFile/AddFileController.cs`
- Create: `backend.Tests/Workers/DownloadJobExecutorTests.cs`
- Create: `backend.Tests/Workers/DownloadResultCommitterTests.cs`

**Interfaces:**
- Consumes: remote `INntpClient`, artifact exchange, and current queue processor pipeline.
- Produces: database-free processing plus
  `DownloadResultCommitter : IWorkerJobResultHandler` in control.

- [ ] **Step 1: Write processing/commit separation tests**

Verify executor can parse/process a fake NZB without a database. Verify committer
produces the same DAV tree/history/verify job as the current all-in-one path.
Verify invalid hash and stale lease commit nothing. Verify a pre-existing
download job with no input artifact is backfilled before lease and the NZB is
copied with a bounded buffer.

- [ ] **Step 2: Define immutable download input**

```csharp
public sealed record DownloadJobInput(
    Guid QueueItemId,
    string FileName,
    string JobName,
    string Category,
    string? ArchivePassword,
    QueueItem.PriorityOption Priority,
    QueueItem.PostProcessingOption PostProcessing,
    ArtifactDescriptor NzbInput,
    DownloadProcessingOptions Processing,
    DownloadCommitOptions Commit);

public sealed record DownloadProcessingOptions(
    int FileProcessingConcurrency);

public sealed record DownloadCommitOptions(
    string DuplicateNzbBehavior,
    bool EnsureImportableVideo,
    string ImportStrategy,
    bool QueuePostDownloadVerification);
```

Clamp `FileProcessingConcurrency` to `1..8`. Control captures these values when
it creates the job payload; a retry uses the same immutable values.

- [ ] **Step 3: Materialize streamed input before lease**

`DownloadJobInputMaterializer` streams `QueueNzbContents`/blob data to
`/cache/exchange/inputs/{jobId:N}.nzb.ready`, calculates size/SHA-256, and stores
the descriptor plus immutable options in `WorkerJob.PayloadJson`. New SAB adds
materialize after the queue/job transaction. The coordinator backfills old
pending jobs before returning them. Materialization failure leaves the job
pending with a retry time and does not expose a partial file.

- [ ] **Step 4: Split processing from durable commit**

`DownloadJobExecutor` opens and verifies the raw input descriptor, performs
streaming NZB parsing, first-segment lookup, PAR2 metadata,
file-info resolution, and file processors. It maps results to artifact DTOs and
writes each result through `ProcessedDownloadArtifactWriter` as soon as its
ordered processor slot is ready. It does not call aggregators, post-processors,
websocket, ARR lifecycle, or EF. Replace processor `ConfigManager` dependencies
with `DownloadProcessingOptions` and use the process-wide `WorkerCpuScheduler`
only for CPU-bound parse/archive/hash sections.

Processor results flow through a bounded `Channel<(int Index,
ProcessedFileArtifact File)>` of capacity two. The writer preserves original
processor order and may retain at most `FileProcessingConcurrency` out-of-order
records; producer writes await when that bound is reached.

- [ ] **Step 5: Implement control-side commit**

Within one transaction, `DownloadResultCommitter`:

1. Validates current lease and artifact descriptor.
2. Resolves duplicate behavior.
3. Creates/reuses category and mount folder.
4. Streams DTO records through `ProcessedDownloadArtifactReader` and maps one
   record at a time back to processor results.
5. Runs RAR/file/7z/multipart aggregators.
6. Runs duplicate rename, blocklist, and importable-video validation.
7. Creates STRM links when configured.
8. Creates one post-download verify job when
   `DownloadCommitOptions.QueuePostDownloadVerification` is true.
9. Creates history/lifecycle/manifest/invalidation records.
10. Removes queue row and completes job.

Any failure rolls back DB state and leaves the artifact available for retry.

- [ ] **Step 6: Fix finalization failure semantics**

If failed-job history finalization itself fails, return a retryable failure and
keep the queue/job retryable. Never return `Completed` merely because error
handling failed.

- [ ] **Step 7: Adapt all-in-one path through executor/committer**

Keep `QueueItemProcessor` as a compatibility facade that calls the new executor
and committer. Existing queue tests must pass before role activation.

- [ ] **Step 8: Run queue and download tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~DownloadJob|FullyQualifiedName~QueueItemProcessor|FullyQualifiedName~QueueProcessingLoop|FullyQualifiedName~DavDatabaseClientQueueTests"
```

- [ ] **Step 9: Commit**

```bash
git add backend/Workers/Download backend/Queue backend/Api/SabControllers/AddFile backend.Tests/Workers
git commit -m "refactor: split download processing from durable commit"
```

### Task 7: Activate Download And Repair Worker Roles

**Files:**
- Create: `backend/Hosting/DownloadWorkerHost.cs`
- Create: `backend/Hosting/RepairWorkerHost.cs`
- Create: `backend/Workers/Repair/RepairJobExecutor.cs`
- Create: `backend/Workers/Repair/RepairResultCommitter.cs`
- Modify: `backend/Services/HealthCheckService.cs`
- Modify: `backend/Services/ArrRepairCommandService.cs`
- Modify: `backend/Program.cs`
- Modify: `entrypoint.sh`
- Create: `backend.Tests/Hosting/DownloadWorkerHostTests.cs`
- Create: `backend.Tests/Hosting/RepairWorkerHostTests.cs`
- Create: `backend.Tests/Workers/RepairJobExecutorTests.cs`

**Interfaces:**
- Produces: executable `worker-download` and `worker-repair` roles plus
  `RepairResultCommitter : IWorkerJobResultHandler` in control.

- [ ] **Step 1: Write role ownership tests**

Download role resolves only download executor, artifact exchange, remote control,
and remote gateway. Repair role resolves only repair executor, remote control,
and the required read-only library resolver. It does not resolve ARR clients or
receive ARR credentials. Neither role resolves a database, local provider pool,
sparse cache, WebDAV, or another lane executor.

- [ ] **Step 2: Extract repair planning from database mutation**

`RepairJobExecutor` returns one of:

```csharp
public abstract record RepairPlan
{
    public sealed record DeleteUnlinked(string Path) : RepairPlan;
    public sealed record IgnoreBlocked(string Path) : RepairPlan;
    public sealed record ArrRemoveAndSearch(
        string ArrInstanceKey, string LinkPath, string DeduplicationKey) : RepairPlan;
    public sealed record NeedsReview(string Reason) : RepairPlan;
}
```

Control commits plans and durable ARR commands. The worker never removes a DAV
item directly.

- [ ] **Step 3: Keep ARR command execution in control**

`RepairResultCommitter` runs in control and persists `RepairPlan` plus the
deduplicated `ArrRepairCommand`. Control-hosted `ArrRepairCommandService` owns
ARR clients. Before retrying an `executing` command without a stored ARR command
ID, it queries ARR queue/media state, records success when the old media record
is gone or a replacement search command is active, and otherwise retries once
under the same deduplication key. The repair worker receives no ARR API key.

- [ ] **Step 4: Activate roles**

Remove startup guards for download and repair. Require internal URLs/token and
exchange path. Role-specific entrypoint skips DB migration and Node. Both roles
listen on HTTP/1 port 8080 for liveness/readiness and HTTP/2 port 8081 for
authenticated status RPC only.

- [ ] **Step 5: Run worker role tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~DownloadWorker|FullyQualifiedName~RepairWorker|FullyQualifiedName~RepairJob|FullyQualifiedName~ArrRepairCommand"
```

- [ ] **Step 6: Commit**

```bash
git add backend/Hosting backend/Workers/Repair backend/Program.cs entrypoint.sh backend/Services backend.Tests
git commit -m "feat: activate download and repair workers"
```

### Task 8: Activate Control And Aggregate Role Status

**Files:**
- Create: `backend/Hosting/ControlHost.cs`
- Create: `backend/Hosting/ExternalWorkerLaneSelection.cs`
- Create: `backend/Services/RoleStatusAggregator.cs`
- Create: `backend/Services/RemoteRoleStatusClient.cs`
- Modify: `backend/Program.cs`
- Modify: `backend/Api/SabControllers/GetStatus/GetStatusController.cs`
- Modify: `backend/Api/SabControllers/GetFullStatus/GetFullStatusController.cs`
- Modify: `backend/Api/SabControllers/StatusDiagnostics.cs`
- Modify: `backend/Api/SabControllers/GetQueue/GetQueueController.cs`
- Modify: `frontend/app/clients/backend-client.server.ts`
- Modify: `frontend/app/routes/health/components/operations-status/operations-status.tsx`
- Modify: `frontend/app/routes/health/components/operations-status/operations-status.module.css`
- Create: `frontend/app/routes/health/components/operations-status/role-status.test.tsx`
- Create: `backend.Tests/Hosting/ControlHostTests.cs`
- Create: `backend.Tests/Hosting/ExternalWorkerLaneSelectionTests.cs`
- Create: `backend.Tests/Api/RoleStatusAggregationTests.cs`
- Modify: `.github/workflows/ci.yml`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Produces: executable `NZBDAV_ROLE=control`, aggregated queue/runtime status, and all-in-one parity.

- [ ] **Step 1: Write control ownership tests**

Control resolves database, SAB/admin APIs, ARR services, job coordinator,
committers, cleanup, outboxes, and status aggregator. It does not resolve local
providers, sparse cache, WebDAV, or worker executors.

For transitional `all` mode, parse one deployment-only environment value:

```csharp
var externalLanes = ExternalWorkerLaneSelection.Parse(
    Environment.GetEnvironmentVariable("NZBDAV_EXTERNAL_WORKER_LANES"));
```

Accepted comma-separated values are `download`, `verify`, and `repair`; default
is empty. A listed lane is not registered in-process. Unknown or duplicate
values fail startup. This enables one-lane-at-a-time canaries and is not exposed
in WebUI. `control` never registers local lane executors.

- [ ] **Step 2: Implement role status heartbeats**

Gateway/workers expose authenticated status RPC. Control polls with timeout and
stores only the latest in-memory snapshot. Missing roles appear degraded; they
do not cause `status/fullstatus` to fail.

Use configured internal endpoints `nzbdav:8081`,
`nzbdav-download:8081`, `nzbdav-verify:8081`, and
`nzbdav-repair:8081`, plus the UI HTTP status endpoint at
`nzbdav-ui:3000/internal/role-status`. Poll every five seconds with a two-second
deadline and retain the last successful snapshot with its timestamp.

- [ ] **Step 3: Aggregate queue state from durable jobs**

SAB queue/history stages come from `WorkerJobs`, lifecycle events, and latest
worker progress rather than process-local dictionaries. Keep existing ARR
response fields and add role details under additive diagnostics.

- [ ] **Step 4: Add role diagnostics to the operations UI**

Add this status shape to `FullStatusResponse`:

```ts
export type RoleStatusDiagnostic = {
  role: "ui" | "control" | "gateway" | "worker-download" | "worker-verify" | "worker-repair";
  instance_id: string;
  live: boolean;
  ready: boolean;
  stale: boolean;
  last_seen_at: string | null;
  cpu_cores: number;
  working_set_bytes: number;
  proportional_set_bytes: number | null;
  managed_heap_bytes: number;
  allocation_bytes_per_second: number;
  gc_pause_p99_ms: number | null;
  threadpool_pending_items: number | null;
  active_rpcs: number;
  queued_rpcs: number;
  active_jobs: number;
  max_jobs: number;
  last_error: string | null;
};
```

Add `roles: RoleStatusDiagnostic[]` to fullstatus and render one compact row per
role in the existing Operations page. Show readiness, age, CPU, PSS/RSS, heap,
GC p99, RPC pressure, and worker slots. Add a degraded banner when gateway,
control, or an expected external lane is missing, stale, or unready. Do not show
tokens, provider credentials, job payloads, or paths.

```tsx
render(<OperationsStatus {...props} fullStatus={statusWith({
  roles: [{ ...gatewayRole, ready: false, stale: true }],
})} />);
expect(screen.getByText(/gateway is stale or not ready/i)).toBeInTheDocument();
```

- [ ] **Step 5: Activate control role**

Control listens on HTTP/1 port 3000 for SAB/admin/websocket/liveness/readiness
and HTTP/2 port 8081 for job coordinator, gateway control-plane, and status RPC.
It performs DB migration and does not map WebDAV. Readiness fails when the
database is unavailable or migrations are pending, and lease issuance pauses
while readiness is false.

- [ ] **Step 6: Add multi-role integration smoke**

Start control, gateway, and one of each worker with fake providers. Add a test
NZB through SAB, observe download -> verify -> completed, then confirm WebDAV
manifest and history. Assert workers have no provider sockets and gateway has no
database handle.

- [ ] **Step 7: Run all repository gates**

```bash
dotnet test backend.Tests/backend.Tests.csproj
dotnet build backend/NzbWebDAV.csproj --no-restore
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run build:server
npm --prefix frontend run test:e2e
python3 -m unittest discover -s tests -v
docker build -t nzbdav:workers-ci .
git diff --check
```

- [ ] **Step 8: Commit**

```bash
git add backend/Hosting backend/Services backend/Api backend.Tests frontend/app/clients frontend/app/routes/health .github/workflows/ci.yml CHANGELOG.md
git commit -m "feat: activate control and aggregate role status"
```

## Completion Gate

This plan is complete only when:

- Control is the only application database owner.
- Gateway is the only NNTP/cache owner.
- Each worker role processes exactly one lane.
- Worker leases renew and stale results are rejected.
- Download artifacts are bounded, verified, temporary, and committed transactionally.
- Verify unknown/provider errors never create repair jobs.
- Repair side effects are durable and reconciled.
- SAB and WebDAV behavior remains compatible.
- Killing any worker does not interrupt gateway playback.
- `NZBDAV_ROLE=all` still passes the complete regression suite.
