# SQLite WAL durability-cost benchmark

This benchmark measures the latency and throughput cost of SQLite WAL
`synchronous=FULL` relative to `synchronous=NORMAL` without opening or changing
an NZBDav production database. It uses `Microsoft.Data.Sqlite` through the
backend project and refuses to run unless the loaded native runtime passes
NZBDav's startup gate (currently SourceGear SQLite 3.53.3 with the approved
source ID and compile options).

## Run it

Use a temporary root on the same storage volume and filesystem as the planned
NZBDav data directory. The runner creates a unique child directory and removes
all benchmark database, WAL, and SHM files when the run ends.

```bash
dotnet run \
  --project benchmarks/SqliteWalDurabilityBenchmark/SqliteWalDurabilityBenchmark.csproj \
  -c Release -- \
  --transactions 2000 \
  --warmup-transactions 200 \
  --batch-size 8 \
  --rounds 4 \
  --temp-root /path/on/the/nzbdav-data-volume
```

Omit `--temp-root` only for a developer-machine smoke run. That uses the
operating system's default temporary volume, which may not behave like the
deployment volume.

Add `--json` for a machine-readable report. The command writes the report to
standard output; it does not create an artifact unless the operator explicitly
redirects it.

## What it measures

For each mode and round, the runner:

1. creates a new temporary database through the backend's pinned native SQLite
   runtime;
2. requests WAL and the selected synchronous mode, then reads both pragmas back
   and aborts if SQLite did not apply them;
3. warms the connection and page cache;
4. truncates the warmup WAL before timing;
5. executes a single-writer workload with eight small queue/outbox-like inserts
   plus one coordination-row update per transaction by default; and
6. reports aggregate transactions/second, logical operations/second, and
   transaction p50/p95/p99 across alternating NORMAL-first and FULL-first
   rounds.

`wal_autocheckpoint` remains at 1000 pages so the test includes SQLite's normal
WAL checkpoint behavior. Direct ADO.NET commands intentionally isolate SQLite
transaction and storage cost from HTTP, EF materialization, NNTP, ARR, Plex,
and scheduler latency.

## Interpreting the result

Compare FULL with NORMAL on the production-equivalent host under representative
storage contention. Keep the workload values identical between comparisons and
run enough rounds for p95/p99 to stabilize. Treat a developer-laptop result as
a tool validation only.

In WAL mode, FULL adds a durability sync for each transaction commit. NORMAL
can preserve database consistency while still losing recently acknowledged
transactions after an operating-system crash or power loss. Therefore, an RPO
that requires acknowledged database commits to survive power loss requires
FULL, subject to the storage stack actually honoring sync requests. The
benchmark answers the performance-cost question; it cannot weaken the stated
RPO.

This is not a destructive power-cut test. It does not prove the behavior of a
drive's volatile cache, RAID controller, hypervisor, network filesystem, or
cloud volume. Validate those separately on the actual deployment class before
making a durability claim.

## RPO and RTO decision boundary

The checked-in runtime policy uses WAL `synchronous=FULL`. Its scoped durability
intent is an RPO of zero acknowledged **database transactions** after a process,
container, operating-system, or power failure when the same storage returns and
the complete storage stack honors SQLite's sync requests. It does not cover an
unrecoverable device/filesystem loss, blob loss, or restore from backup.

No RTO is encoded or claimed. The operator must supply an intact-storage restart
RTO and measure it on the deployment host. Disk-loss RPO/RTO also remain
undefined while database-plus-blob backup/restore is explicitly out of scope.
Do not convert either missing objective into an implicit default in release
notes, health status, or benchmark results.

## Verify the benchmark implementation

```bash
dotnet test \
  benchmarks/SqliteWalDurabilityBenchmark.Tests/SqliteWalDurabilityBenchmark.Tests.csproj \
  -c Release
```

The integration test checks the pinned runtime, WAL activation, NORMAL=1,
FULL=2, percentile output, and temporary-file cleanup.
