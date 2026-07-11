# NZBDav Gateway Data Plane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract one latency-sensitive gateway process that exclusively owns NNTP providers, connection scheduling, sparse caching, immutable WebDAV manifests, and range serving.

**Architecture:** Introduce an in-process article gateway contract first, then add an authenticated HTTP/2 gRPC transport and a remote `INntpClient` adapter. Refactor WebDAV onto an immutable manifest index so the gateway never opens the control database and can continue read-only service during control outages.

**Tech Stack:** .NET 10, ASP.NET Core gRPC 2.80.0, Grpc.Net.Client 2.80.0, Protobuf, NWebDav, EF Core, MemoryPack, System.IO.Pipelines, xUnit, Docker

## Global Constraints

- Complete `2026-07-11-nzbdav-role-host-durable-coordination.md` first.
- Follow `docs/superpowers/specs/2026-07-11-nzbdav-single-host-role-separation-design.md`.
- Gateway is the only active NNTP provider-pool owner and sparse-cache writer.
- Gateway must not receive a database path or connection string.
- Rclone remains the production mount.
- Preserve `http://nzbdav:3000/` as the rclone WebDAV URL.
- Internal gRPC is private and service-token authenticated.
- Stream data in bounded frames with cancellation and deadlines.
- Do not activate external workers in this plan.
- Do not add WebUI scheduler controls.

---

### Task 1: Vendor Bounded UsenetSharp Backpressure

**Files:**
- Create: `backend/Vendor/UsenetSharp/` from upstream commit `ccc5de1b114c0b0dc7dae0c933a10a2b99562cac`
- Modify: `backend/Vendor/UsenetSharp/UsenetSharp/Clients/UsenetClient.BodyAsync.cs`
- Modify: `backend/Vendor/UsenetSharp/UsenetSharp/Clients/UsenetClient.ArticleAsync.cs`
- Create: `backend/Vendor/UsenetSharp/UsenetSharpTest/Clients/BodyBackpressureTests.cs`
- Modify: `backend/NzbWebDAV.csproj`
- Modify: `backend.Tests/backend.Tests.csproj`
- Create: `NOTICE.md`

**Interfaces:**
- Produces: the existing `UsenetSharp` public API with bounded pipe buffering.

- [ ] **Step 1: Import the exact licensed upstream source**

```bash
git subtree add --prefix backend/Vendor/UsenetSharp https://github.com/nzbdav-dev/UsenetSharp.git ccc5de1b114c0b0dc7dae0c933a10a2b99562cac --squash
```

Verify `LICENSE` and repository metadata remain present.

- [ ] **Step 2: Write a failing slow-reader backpressure test**

Use a fake NNTP stream that emits an article larger than the pause threshold,
consume the returned stream slowly, and assert the producer cannot advance more
than the configured buffered window. Expose the maximum observed buffered bytes
through an internal test hook rather than timing alone.

```csharp
Assert.InRange(
    client.MaximumObservedPipeBytes,
    1,
    UsenetPipeOptions.PauseWriterThreshold + UsenetPipeOptions.MinimumSegmentSize);
```

- [ ] **Step 3: Run the vendored test and confirm failure**

```bash
dotnet test backend/Vendor/UsenetSharp/UsenetSharpTest/UsenetSharpTest.csproj --filter FullyQualifiedName~BodyBackpressureTests
```

Expected: FAIL because upstream uses `long.MaxValue` thresholds.

- [ ] **Step 4: Add one shared bounded pipe policy**

```csharp
internal static class UsenetPipeOptions
{
    internal const long PauseWriterThreshold = 1024 * 1024;
    internal const long ResumeWriterThreshold = 512 * 1024;
    internal const int MinimumSegmentSize = 64 * 1024;

    internal static PipeOptions Create() => new(
        pool: MemoryPool<byte>.Shared,
        readerScheduler: PipeScheduler.ThreadPool,
        writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: PauseWriterThreshold,
        resumeWriterThreshold: ResumeWriterThreshold,
        minimumSegmentSize: MinimumSegmentSize,
        useSynchronizationContext: false);
}
```

Replace both existing pipes whose pause and resume thresholds are set to
`long.MaxValue` with `new Pipe(UsenetPipeOptions.Create())`. Preserve
cancellation and completion behavior.

- [ ] **Step 5: Replace the package reference with project references**

Remove `PackageReference Include="UsenetSharp"`. Add:

```xml
<ItemGroup>
  <Compile Remove="Vendor\UsenetSharp\**\*.cs" />
  <ProjectReference Include="Vendor\UsenetSharp\UsenetSharp\UsenetSharp.csproj" />
</ItemGroup>
```

In `backend.Tests/backend.Tests.csproj`, add:

```xml
<ProjectReference Include="..\backend\Vendor\UsenetSharp\UsenetSharp\UsenetSharp.csproj" />
```

Record the upstream commit and license in `NOTICE.md`.

- [ ] **Step 6: Run vendored and backend stream tests**

```bash
dotnet test backend/Vendor/UsenetSharp/UsenetSharpTest/UsenetSharpTest.csproj \
  --filter "FullyQualifiedName~BodyBackpressureTests|FullyQualifiedName~Streams|FullyQualifiedName~Concurrency"
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~YencStream|FullyQualifiedName~MultiSegmentStream|FullyQualifiedName~DownloadingNntpClient"
```

Expected: all deterministic selected tests pass. Do not run the vendored
credential-backed integration fixtures in CI.

- [ ] **Step 7: Commit**

```bash
git add backend/Vendor/UsenetSharp backend/NzbWebDAV.csproj backend.Tests/backend.Tests.csproj NOTICE.md
git commit -m "perf: bound usenet article pipeline buffering"
```

### Task 2: Define The Article Gateway Contract

**Files:**
- Create: `backend/Gateway/ArticleLane.cs`
- Create: `backend/Gateway/ArticleRequestContext.cs`
- Create: `backend/Gateway/IArticleGateway.cs`
- Create: `backend/Gateway/InProcessArticleGateway.cs`
- Create: `backend/Gateway/ArticleGatewayModels.cs`
- Create: `backend.Tests/Gateway/ArticleGatewayContractTests.cs`

**Interfaces:**
- Consumes: existing `INntpClient`, `SegmentCheckResult`, and `IFileRangeReader`.
- Produces: transport-neutral gateway operations used by local and gRPC implementations.

- [ ] **Step 1: Write a reusable contract suite**

The suite is an abstract test base whose derived fixture supplies an
`IArticleGateway`. Cover ordered STAT results, missing/provider-error mapping,
bounded article chunks, range reads, cancellation, and lane propagation.

```csharp
public abstract class ArticleGatewayContractTests
{
    protected abstract Task<IArticleGateway> CreateGatewayAsync(FakeNntpClient inner);

    [Fact]
    public async Task StatSegmentsPreservesInputOrder()
    {
        var gateway = await CreateGatewayAsync(new FakeNntpClient());
        var result = await gateway.StatSegmentsAsync(
            ["a", "b", "c"], ArticleRequestContext.Verify(), CancellationToken.None);
        Assert.Equal(new[] { "a", "b", "c" }, result.Select(x => x.SegmentId));
    }
}
```

- [ ] **Step 2: Run the contract tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~ArticleGatewayContractTests
```

Expected: build fails because the gateway interfaces do not exist.

- [ ] **Step 3: Define lanes and request context**

```csharp
public enum ArticleLane
{
    Stream = 1,
    Download = 2,
    Verify = 3,
    Repair = 4,
}

public sealed record ArticleRequestContext(
    ArticleLane Lane,
    string RequestId,
    DateTimeOffset? Deadline,
    string? AffinityKey)
{
    public static ArticleRequestContext Stream() =>
        new(ArticleLane.Stream, Guid.NewGuid().ToString("N"), null, null);
    public static ArticleRequestContext Download() =>
        new(ArticleLane.Download, Guid.NewGuid().ToString("N"), null, null);
    public static ArticleRequestContext Verify() =>
        new(ArticleLane.Verify, Guid.NewGuid().ToString("N"), null, null);
    public static ArticleRequestContext Repair() =>
        new(ArticleLane.Repair, Guid.NewGuid().ToString("N"), null, null);
}
```

- [ ] **Step 4: Define bounded streaming operations**

```csharp
public interface IArticleGateway
{
    Task<IReadOnlyList<GatewayStatResult>> StatSegmentsAsync(
        IReadOnlyList<string> segmentIds, ArticleRequestContext context, CancellationToken ct);
    Task<GatewayYencHeader> ReadYencHeaderAsync(
        string segmentId, ArticleRequestContext context, CancellationToken ct);
    Task<GatewayArticleResponse> OpenDecodedArticleAsync(
        string segmentId, bool includeHeaders, ArticleRequestContext context, CancellationToken ct);
    ValueTask<int> ReadFileRangeAsync(
        GatewayFileManifest file, long offset, Memory<byte> buffer,
        ArticleRequestContext context, CancellationToken ct);
    IReadOnlyList<ProviderPoolSnapshot> GetProviderSnapshots();
}

public sealed record GatewayArticleResponse(
    string SegmentId,
    int ResponseCode,
    string ResponseMessage,
    IReadOnlyDictionary<string, string> Headers,
    Stream Content);

public sealed record GatewayFileManifest(
    Guid ItemId,
    long Length,
    IReadOnlyList<GatewayFilePart> Parts);

public sealed record GatewayFilePart(
    long FileOffset,
    long Length,
    IReadOnlyList<GatewaySegmentSlice> Slices);

public sealed record GatewaySegmentSlice(
    string SegmentId,
    long SegmentOffset,
    long FileOffset,
    long Length);

public sealed record GatewayStatResult(
    string SegmentId,
    SegmentCheckState State,
    string? Provider,
    string? Error);

public sealed record GatewayYencHeader(
    string SegmentId,
    long DecodedSize,
    string? FileName,
    long? PartBegin,
    long? PartEnd);
```

- [ ] **Step 5: Implement the in-process adapter**

Map lanes to the existing contextual priority. Wrap returned streams so disposal
and cancellation propagate to the underlying NNTP operation. Do not copy the
whole stream into memory.

- [ ] **Step 6: Run the contract suite**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~ArticleGatewayContractTests
```

Expected: the in-process implementation passes every contract test.

- [ ] **Step 7: Commit**

```bash
git add backend/Gateway backend.Tests/Gateway
git commit -m "refactor: define article gateway contract"
```

### Task 3: Add Deterministic Cross-Lane Scheduling

**Files:**
- Create: `backend/Gateway/Scheduling/GatewayRequestScheduler.cs`
- Create: `backend/Gateway/Scheduling/GatewayRequestLease.cs`
- Create: `backend/Gateway/Scheduling/GatewaySchedulerSnapshot.cs`
- Modify: `backend/Gateway/InProcessArticleGateway.cs`
- Create: `backend.Tests/Gateway/GatewayRequestSchedulerTests.cs`

**Interfaces:**
- Produces: `GatewayRequestScheduler.AcquireAsync(GatewayRequestClass requestClass, CancellationToken ct)` and per-lane diagnostics.

- [ ] **Step 1: Write fairness, cap, and cancellation tests**

```csharp
[Fact]
public async Task NeverExceedsConfiguredGlobalCapacity()
{
    await using var scheduler = new GatewayRequestScheduler(capacity: 4);
    var maximum = 0;
    var active = 0;
    await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
    {
        await using var lease = await scheduler.AcquireAsync(
            i % 2 == 0 ? ArticleLane.Stream : ArticleLane.Verify,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        maximum = Math.Max(maximum, Interlocked.Increment(ref active));
        await Task.Delay(5);
        Interlocked.Decrement(ref active);
    }));
    Assert.InRange(maximum, 1, 4);
}
```

Add a sustained-stream test proving verify/download/repair each receive a grant
within a bounded number of releases, and a cancellation test proving cancelled
waiters disappear. Add a provider-selection test where the primary is in soft
cooldown and a stream request immediately acquires a reachable backup without
waiting for the primary cooldown.

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~GatewayRequestSchedulerTests
```

Expected: build fails because the scheduler is missing.

- [ ] **Step 3: Implement weighted fairness with aging**

Use one FIFO queue per lane under one lock. Stream has the highest base weight;
download, verify, and repair have nonzero weights. Track consecutive grants and
the oldest wait timestamp. Select the next lane deterministically; do not use
random odds.

```csharp
private static readonly IReadOnlyDictionary<ArticleLane, int> Weights =
    new Dictionary<ArticleLane, int>
    {
        [ArticleLane.Stream] = 8,
        [ArticleLane.Download] = 4,
        [ArticleLane.Verify] = 1,
        [ArticleLane.Repair] = 1,
    };
```

Weights are internal policy, not configuration. Aging must elevate a waiting
lane after one complete weighted cycle.

- [ ] **Step 4: Gate every provider operation once**

Acquire one scheduler lease immediately before entering the existing
`DownloadingNntpClient`/provider pool. Release when the provider connection is
ready for reuse, not when the caller finishes consuming already-buffered bytes.
STAT batches acquire one lease per provider connection, not one per segment.
For stream requests, skip primaries already in retryable cooldown and probe the
highest-priority reachable backup immediately. Missing-article responses remain
normal fallback results and do not alter provider circuit state.

- [ ] **Step 5: Expose scheduler snapshots**

Include active, queued, total grants, cancellations, and average/max wait by
lane. Add these fields to gateway diagnostics without secrets.

- [ ] **Step 6: Run scheduler and provider tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~GatewayRequestSchedulerTests|FullyQualifiedName~PrioritizedSemaphoreTests|FullyQualifiedName~ConnectionPoolTests|FullyQualifiedName~MultiProviderCircuitBreakerTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```bash
git add backend/Gateway backend.Tests/Gateway
git commit -m "feat: schedule provider work across gateway lanes"
```

### Task 4: Add Authenticated gRPC Transport

**Files:**
- Modify: `backend/NzbWebDAV.csproj`
- Create: `backend/Gateway/Grpc/article_gateway.proto`
- Create: `backend/Gateway/Grpc/InternalTokenInterceptor.cs`
- Create: `backend/Gateway/Grpc/InternalRpcMetricsInterceptor.cs`
- Create: `backend/Gateway/Grpc/ArticleGatewayGrpcService.cs`
- Create: `backend/Gateway/Grpc/GrpcArticleGatewayClient.cs`
- Create: `backend/Gateway/Grpc/GrpcArticleStream.cs`
- Create: `backend.Tests/Gateway/GrpcArticleGatewayContractTests.cs`
- Create: `backend.Tests/Gateway/InternalTokenInterceptorTests.cs`

**Interfaces:**
- Consumes: `IArticleGateway` from Task 2.
- Produces: gRPC server/client parity and bounded server-streaming article frames.

- [ ] **Step 1: Add pinned gRPC packages and protobuf generation**

```xml
<PackageReference Include="Grpc.AspNetCore" Version="2.80.0" />
<PackageReference Include="Grpc.Net.Client" Version="2.80.0" />
<PackageReference Include="Grpc.Tools" Version="2.80.0" PrivateAssets="All" />
<Protobuf Include="Gateway/Grpc/article_gateway.proto" GrpcServices="Both" />
```

- [ ] **Step 2: Write transport contract and authentication tests**

Derive `GrpcArticleGatewayContractTests` from the Task 2 suite using an in-memory
ASP.NET Core test server. Add tests for missing/wrong token, client cancellation,
deadline expiry, and a slow consumer that never buffers more than four frames.

- [ ] **Step 3: Define a bounded protobuf contract**

The proto must define:

```protobuf
service ArticleGatewayRpc {
  rpc StatSegments(StatSegmentsRequest) returns (StatSegmentsReply);
  rpc ReadYencHeader(YencHeaderRequest) returns (YencHeaderReply);
  rpc ReadDecodedArticle(DecodedArticleRequest) returns (stream ArticleFrame);
  rpc ReadFileRange(FileRangeRequest) returns (stream ArticleFrame);
  rpc GetStatus(GatewayStatusRequest) returns (GatewayStatusReply);
}

message ArticleFrame {
  oneof payload {
    ArticleMetadata metadata = 1;
    bytes data = 2;
  }
  bool end_of_stream = 3;
}

message ArticleMetadata {
  string segment_id = 1;
  int32 response_code = 2;
  string response_message = 3;
  map<string, string> headers = 4;
  int64 content_length = 5;
}
```

Every request includes lane, request ID, optional deadline Unix milliseconds,
and optional affinity key. `ReadDecodedArticle` writes exactly one metadata
frame before data; `ReadFileRange` writes metadata with the accepted offset and
length before data. Set maximum frame data to 256 KiB in server code. Map
missing articles to `NotFound` and retryable provider failures to `Unavailable`
with a redacted `x-nzbdav-failure-kind` trailer.

- [ ] **Step 4: Implement constant-time internal-token authentication**

Read `NZBDAV_INTERNAL_TOKEN` once at startup. Require metadata header
`x-nzbdav-internal-token`. Compare decoded byte arrays with
`CryptographicOperations.FixedTimeEquals`. Return `Unauthenticated` before
resolving the service implementation.

`InternalRpcMetricsInterceptor` increments active and queued counters by RPC
method, records elapsed time and status, and decrements in `finally`. It stores
no request messages, metadata values, paths, or tokens.

- [ ] **Step 5: Implement streaming without whole-body buffering**

Rent one frame buffer from `MemoryPool<byte>`, read at most 256 KiB, await
`WriteAsync`, and return the owner before renting the next frame. Link
`ServerCallContext.CancellationToken` to provider cancellation.

- [ ] **Step 6: Implement the client stream adapter**

`GrpcArticleStream.ReadAsync` consumes frames incrementally and retains only the
unread bytes from the current frame. `DisposeAsync` cancels the RPC. It must not
start an unobserved background reader.

- [ ] **Step 7: Run gRPC contract tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~GrpcArticleGateway|FullyQualifiedName~InternalTokenInterceptor"
```

Expected: all transport and authentication tests pass.

- [ ] **Step 8: Commit**

```bash
git add backend/NzbWebDAV.csproj backend/Gateway/Grpc backend.Tests/Gateway
git commit -m "feat: expose authenticated article gateway rpc"
```

### Task 5: Implement RemoteNntpClient Compatibility

**Files:**
- Create: `backend/Clients/Usenet/RemoteNntpClient.cs`
- Create: `backend/Clients/Usenet/RemoteExclusiveConnection.cs`
- Create: `backend.Tests/Clients/Usenet/RemoteNntpClientTests.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`

**Interfaces:**
- Consumes: `GrpcArticleGatewayClient`.
- Produces: `RemoteNntpClient : NntpClient` for existing queue, verify, and stream code.

- [ ] **Step 1: Write parity and callback tests**

Cover `StatAsync`, ordered `StatPipelinedAsync`, `HeadAsync`, decoded body,
decoded article, yEnc header, cancellation, missing article, provider error, and
exactly-once `onConnectionReadyAgain` callback.

```csharp
var callbackCount = 0;
var response = await client.DecodedBodyAsync(
    "segment", _ => Interlocked.Increment(ref callbackCount), CancellationToken.None);
await response.Stream.CopyToAsync(Stream.Null);
Assert.Equal(1, callbackCount);
```

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~RemoteNntpClientTests
```

Expected: build fails because the remote client is missing.

- [ ] **Step 3: Implement response translation**

Construct the existing UsenetSharp response objects from RPC metadata and use
`GrpcArticleStream` as their stream. Map gRPC cancellation to
`OperationCanceledException`, missing responses to
`UsenetArticleNotFoundException`, and structured provider failures to
`RetryableDownloadException`.

- [ ] **Step 4: Remove raw remote connection ownership**

Implement `AcquireExclusiveConnectionAsync` as an affinity token. Subsequent
decoded calls send that token to gateway; gateway may reuse a connection but is
free to reschedule it after timeout. Disposing the token sends no provider
command and holds no worker-side socket.

- [ ] **Step 5: Add in-process/remote selection**

`UsenetStreamingClient` receives an `IArticleGatewayClientFactory`. In
`NZBDAV_ROLE=all`, no internal gateway URL means in-process behavior. A valid
`NZBDAV_INTERNAL_GATEWAY_URL` constructs `RemoteNntpClient`. Invalid or missing
tokens fail startup instead of silently falling back to a second local pool.

- [ ] **Step 6: Run all NNTP and stream tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~Usenet|FullyQualifiedName~Stream"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```bash
git add backend/Clients/Usenet backend.Tests/Clients/Usenet
git commit -m "feat: adapt nntp consumers to remote gateway"
```

### Task 6: Add Versioned Provider Configuration And Graceful Drain

**Files:**
- Create: `backend/Gateway/Configuration/GatewayRuntimeConfig.cs`
- Create: `backend/Gateway/Configuration/GatewayConfigHasher.cs`
- Create: `backend/Gateway/Configuration/GatewayConfigSnapshotStore.cs`
- Create: `backend/Gateway/Configuration/GatewayConfigPublisher.cs`
- Create: `backend/Gateway/Configuration/GatewayConfigManager.cs`
- Create: `backend/Gateway/Configuration/DrainingProviderPool.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`
- Modify: `backend/Clients/Usenet/WrappingNntpClient.cs`
- Create: `backend.Tests/Gateway/GatewayConfigurationTests.cs`

**Interfaces:**
- Produces: `GatewayRuntimeConfigSnapshot`, `GatewayConfigPublisher`, `GatewayConfigManager.ApplyAsync(GatewayRuntimeConfigSnapshot snapshot, CancellationToken ct)`, hash-based no-op reload, and reference-counted pool drain.

- [ ] **Step 1: Write unchanged, failed, and active-stream reload tests**

```csharp
Assert.Equal(GatewayConfigApplyResult.Unchanged,
    await manager.ApplyAsync(firstSnapshot, CancellationToken.None));

await using var active = await manager.OpenAsync(CancellationToken.None);
Assert.Equal(GatewayConfigApplyResult.Applied,
    await manager.ApplyAsync(secondSnapshot, CancellationToken.None));
Assert.False(firstPool.Disposed);
await active.DisposeAsync();
Assert.True(firstPool.Disposed);
```

- [ ] **Step 2: Run focused tests and confirm failure**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~GatewayConfigurationTests
```

- [ ] **Step 3: Canonicalize and hash relevant configuration**

Sort providers by stable ID and serialize only provider, adaptive-enable,
connection-cap, cache, and WebDAV semantic fields. Hash UTF-8 JSON with SHA-256.
Do not include generated timestamps.

```csharp
public sealed record GatewayRuntimeConfigSnapshot(
    long Version,
    string Sha256,
    UsenetProviderConfig Providers,
    GatewayConnectionOptions Connections,
    GatewayCacheOptions Cache,
    GatewayWebDavOptions WebDav);

public sealed record GatewayConnectionOptions(
    bool AdaptiveEnabled,
    int MaxDownloadConnections,
    int MaxStreamingConnections,
    int MaxTotalStreamingConnections,
    int ArticleBufferSize,
    int StreamingPriorityPercent);

public sealed record GatewayWebDavOptions(
    string User,
    string Password,
    IReadOnlyList<string> Categories);

public sealed record GatewayCacheOptions(
    bool Enabled,
    long MaxBytes,
    int ChunkBytes,
    int ReadAheadBytes,
    TimeSpan IdleTtl);
```

`GatewayConfigPublisher` is control-owned: it reads persisted configuration,
constructs this immutable snapshot, atomically stores the last accepted file,
and publishes only when version or hash changes. `GatewayConfigManager` is
gateway-owned: it validates the hash and applies the pool/cache/WebDAV change.
The snapshot carries cache behavior but not a host path. Gateway always stores
temporary cache data under `/cache/gateway/segments`; the old configurable
directory remains honored only by transitional `all` mode.

- [ ] **Step 4: Build, probe, swap, and drain**

Build a new pool without touching the active pool. Probe authentication and one
safe command per provider. Swap only after all required providers validate.
Reference-count active operations and dispose the old pool after the count
reaches zero. A bounded shutdown timeout may force cancellation only during
process termination.

- [ ] **Step 5: Persist the last accepted snapshot atomically**

The control publisher writes `/config/runtime/gateway-config.json` via a temp
file in the same directory, `Flush(true)`, mode `0600`, and atomic rename. The
gateway mounts that runtime directory, validates the embedded hash before use,
and never writes the control-owned snapshot. A temporary control outage may use
the last valid file; a missing or invalid file fails gateway readiness.

- [ ] **Step 6: Run configuration and provider tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~GatewayConfigurationTests|FullyQualifiedName~WrappingNntpClientTests|FullyQualifiedName~MultiProviderCircuitBreakerTests"
```

- [ ] **Step 7: Commit**

```bash
git add backend/Gateway/Configuration backend/Clients/Usenet backend.Tests/Gateway
git commit -m "feat: reload gateway provider pools without disruption"
```

### Task 7: Replace WebDAV Database Reads With A Manifest Mirror

**Files:**
- Create: `backend/Gateway/Manifest/IWebDavIndex.cs`
- Create: `backend/Gateway/Manifest/DatabaseWebDavIndex.cs`
- Create: `backend/Gateway/Manifest/ManifestWebDavIndex.cs`
- Create: `backend/Gateway/Manifest/ManifestMirrorService.cs`
- Create: `backend/Gateway/Manifest/ManifestSnapshotStore.cs`
- Create: `backend/Gateway/Control/IGatewayControlPlane.cs`
- Create: `backend/Gateway/Control/InProcessGatewayControlPlane.cs`
- Create: `backend/Gateway/Control/GrpcGatewayControlPlaneClient.cs`
- Create: `backend/Gateway/Grpc/gateway_control.proto`
- Create: `backend/Gateway/Grpc/GatewayControlGrpcService.cs`
- Modify: `backend/WebDav/DatabaseStore.cs`
- Modify: `backend/WebDav/DatabaseStoreCollection.cs`
- Modify: `backend/WebDav/DatabaseStoreIdsCollection.cs`
- Modify: `backend/WebDav/DatabaseStoreNzbFile.cs`
- Modify: `backend/WebDav/DatabaseStoreRarFile.cs`
- Modify: `backend/WebDav/DatabaseStoreMultipartFile.cs`
- Modify: `backend/WebDav/DatabaseStoreSymlinkCollection.cs`
- Modify: `backend/WebDav/DatabaseStoreWatchFolder.cs`
- Modify: `backend/WebDav/DatabaseStoreCategoryWatchFolder.cs`
- Create: `backend.Tests/Gateway/ManifestWebDavIndexTests.cs`
- Create: `backend.Tests/Gateway/ManifestMirrorServiceTests.cs`
- Create: `backend.Tests/Gateway/GatewayControlPlaneContractTests.cs`

**Interfaces:**
- Consumes: ordered `ManifestChange` records and durable import receipts.
- Produces: `IGatewayControlPlane`, authenticated in-process/gRPC implementations, and database-backed/mirror-backed `IWebDavIndex` implementations.

- [ ] **Step 1: Write one shared WebDAV index contract suite**

Cover root lookup, child lookup, ID prefix lookup, file metadata, completed
symlink filtering, queue watch folders, ordered change replay, duplicate replay,
sequence gap, snapshot replacement, and restart from disk.

Run `GatewayControlPlaneContractTests` against in-process and gRPC adapters:

```csharp
[Fact]
public async Task ManifestWatchResumesAfterAcknowledgedSequence()
{
    var control = await CreateControlPlaneAsync();
    await control.AckManifestAsync(41, CancellationToken.None);
    await using var changes = control
        .WatchManifestChangesAsync(41, CancellationToken.None)
        .GetAsyncEnumerator();
    Assert.True(await changes.MoveNextAsync());
    Assert.Equal(42, changes.Current.Sequence);
}
```

Also cover configuration resume, snapshot fallback after a sequence gap,
idempotent acknowledgement, authenticated claim, and unavailable-control
failure.

- [ ] **Step 2: Define the read/mutation boundary**

```csharp
public interface IWebDavIndex
{
    ValueTask<DavIndexItem?> GetItemAsync(Guid id, CancellationToken ct);
    ValueTask<DavIndexItem?> GetChildAsync(Guid parentId, string name, CancellationToken ct);
    ValueTask<IReadOnlyList<DavIndexItem>> GetChildrenAsync(Guid parentId, CancellationToken ct);
    ValueTask<IReadOnlyList<DavIndexItem>> GetByIdPrefixAsync(string prefix, CancellationToken ct);
    ValueTask<GatewayFileManifest?> GetFileManifestAsync(Guid id, CancellationToken ct);
    ValueTask<ImportClaimResult> ClaimCompletedImportAsync(Guid id, CancellationToken ct);
}
```

Read methods are served locally. `ClaimCompletedImportAsync` calls control and
fails closed when control is unavailable.

Use one immutable item shape in both adapters:

```csharp
public enum DavIndexItemKind { Directory = 1, File = 2, Symlink = 3 }

public sealed record DavIndexItem(
    Guid Id,
    Guid? ParentId,
    string Name,
    DavIndexItemKind Kind,
    long Length,
    string ContentType,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    string? SymlinkTarget,
    bool IsCompletedImport,
    GatewayFileManifest? FileManifest);

public sealed record ManifestSnapshot(
    long Sequence,
    IReadOnlyList<DavIndexItem> Items,
    string Sha256);

public sealed record ImportClaimResult(
    bool Accepted,
    ImportReceiptState State,
    string? Error);
```

- [ ] **Step 3: Define the control-plane transport**

```csharp
public interface IGatewayControlPlane
{
    IAsyncEnumerable<GatewayRuntimeConfigSnapshot> WatchConfigurationAsync(
        long afterVersion, CancellationToken ct);
    IAsyncEnumerable<ManifestChangeEnvelope> WatchManifestChangesAsync(
        long afterSequence, CancellationToken ct);
    Task<ManifestSnapshot> GetManifestSnapshotAsync(CancellationToken ct);
    Task AckManifestAsync(long sequence, CancellationToken ct);
    Task<ImportClaimResult> ClaimCompletedImportAsync(Guid itemId, CancellationToken ct);
}

public sealed record ManifestChangeEnvelope(
    long Sequence,
    Guid ChangeId,
    string Kind,
    DavIndexItem? Item,
    Guid? DeletedItemId);
```

`gateway_control.proto` exposes server-streaming configuration and manifest
watches plus unary snapshot, acknowledgement, and import-claim operations. It
uses the same internal-token interceptor and canonical GUID/timestamp rules as
the article RPC. The `all` and `control` hosts serve it; gateway consumes it.

- [ ] **Step 4: Implement the database adapter without behavior changes**

Move existing `DavDatabaseClient` queries behind `DatabaseWebDavIndex`. Run the
existing WebDAV tests against it before changing the store classes.

- [ ] **Step 5: Refactor store classes to depend on `IWebDavIndex`**

Remove direct `DavDatabaseContext` and `DavDatabaseClient` access from WebDAV
classes. Preserve names, unique keys, MIME types, symlink targets, and range
stream behavior.

- [ ] **Step 6: Implement ordered mirror replay**

Apply a change only when `change.Sequence == LastSequence + 1`. Ignore exact
duplicate sequences whose `ChangeId` already matches. On a gap, stop applying,
mark readiness degraded, and request a complete snapshot.

- [ ] **Step 7: Persist mirror snapshots atomically**

Store the last valid snapshot and sequence under `/cache/gateway/manifests`. Validate
tree parent references and file metadata before replacing the active mirror.
Write temp files mode `0600`, flush, and atomically rename. Never replace a
valid active mirror with an empty or invalid snapshot.

- [ ] **Step 8: Run WebDAV, manifest, and control-plane tests**

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~WebDav|FullyQualifiedName~GetWebdav|FullyQualifiedName~Manifest|FullyQualifiedName~GatewayControlPlane"
```

Expected: both index implementations pass the same behavior suite.

- [ ] **Step 9: Commit**

```bash
git add backend/Gateway/Manifest backend/Gateway/Control backend/Gateway/Grpc backend/WebDav backend.Tests/Gateway backend.Tests/WebDav
git commit -m "refactor: serve webdav from a replayable manifest index"
```

### Task 8: Activate And Verify The Gateway Role

**Files:**
- Create: `backend/Hosting/GatewayHost.cs`
- Modify: `backend/Program.cs`
- Modify: `entrypoint.sh`
- Modify: `Dockerfile`
- Modify: `backend/Api/SabControllers/StatusDiagnostics.cs`
- Modify: `backend/Api/SabControllers/GetStatus/GetStatusController.cs`
- Modify: `backend/Api/SabControllers/GetFullStatus/GetFullStatusController.cs`
- Create: `backend.Tests/Hosting/GatewayHostTests.cs`
- Modify: `.github/workflows/ci.yml`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Produces: executable `NZBDAV_ROLE=gateway` and optional all-in-one remote-gateway mode.

- [ ] **Step 1: Write service ownership and endpoint tests**

Assert gateway resolves `IArticleGateway`, provider pools, sparse cache,
`IWebDavIndex`, WebDAV, gRPC, liveness, and readiness. Assert it cannot resolve
`DavDatabaseContext`, SAB controller, ARR services, queue manager, verify, or
repair services.

- [ ] **Step 2: Configure role-specific Kestrel endpoints**

Gateway listens on:

```text
0.0.0.0:3000  HTTP/1.1  WebDAV and health
0.0.0.0:8081  HTTP/2    private gRPC
```

Do not start Node in gateway role. `entrypoint.sh` runs database migration only
for `all` and `control`, never gateway.

- [ ] **Step 3: Register the gateway host**

Remove the Task 2 startup guard for `NzbdavRole.Gateway`. Map only health,
WebDAV, and gRPC endpoints. Require `NZBDAV_CONTROL_URL` and
`NZBDAV_INTERNAL_TOKEN`. Start from valid on-disk configuration and manifest
snapshots, then resume both control-plane watches; fail readiness only when no
valid snapshot exists or a manifest gap cannot be repaired.

In `all`, map `GatewayControlRpc` on HTTP/2 port 8081. When
`NZBDAV_INTERNAL_GATEWAY_URL` is set, register `RemoteNntpClient` and the remote
gateway status client and do not construct a local provider pool or sparse
cache writer. This is the reversible gateway-extraction phase; an invalid URL
or token fails startup instead of silently opening a second pool.

- [ ] **Step 4: Add role and gateway diagnostics**

Expose role, instance ID, config hash, provider pools, scheduler lanes, cache,
manifest sequence/lag, active streams, CPU, PSS when available, and GC metrics.
Control aggregation is completed in the worker/deployment plans.

- [ ] **Step 5: Add Docker role smoke tests**

Start a fake control/config endpoint and gateway container. Assert:

```bash
curl -fsS http://127.0.0.1:3000/health/live
curl -fsS http://127.0.0.1:3000/health/ready
```

Assert port 8081 rejects missing tokens and port 3000 has no SAB API.

- [ ] **Step 6: Run all repository gates**

```bash
dotnet test backend.Tests/backend.Tests.csproj
dotnet build backend/NzbWebDAV.csproj --no-restore
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build
npm --prefix frontend run build:server
python3 -m unittest discover -s tests -v
docker build -t nzbdav:gateway-ci .
git diff --check
```

- [ ] **Step 7: Commit**

```bash
git add backend/Hosting backend/Program.cs entrypoint.sh Dockerfile backend/Api .github/workflows/ci.yml CHANGELOG.md
git commit -m "feat: activate isolated nzbdav gateway role"
```

## Completion Gate

This plan is complete only when:

- Gateway is the only role capable of opening provider connections and sparse cache files.
- Gateway opens no application database.
- Remote and in-process article gateways pass one contract suite.
- Slow RPC consumers cannot cause unbounded article buffering.
- Provider reload is hash-based, validated, and gracefully drained.
- Gateway WebDAV survives control outage from its manifest mirror.
- Rclone's `http://nzbdav:3000/` URL remains valid.
- Existing all-in-one behavior remains available for rollback.
