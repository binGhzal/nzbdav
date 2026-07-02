# NZBDav Decypharr-Inspired Cache, Repair, And DFS Plan

## Goal

Make NZBDav faster and safer for ARR/Plex Usenet workloads by improving the existing WebDAV/rclone stream path first, then benchmark-gating a Linux x64 native DFS prototype as a possible replacement mount backend.

## Execution State

- [x] Preflight checkpoint and baseline gates
- [x] Benchmark harness and DFS evidence gate
- [x] Repair-safe segment checking
- [x] Sparse segment cache and random-access reader
- [x] Archive segment slicing
- [x] Persisted repair history and status APIs
- [x] Operator diagnostics UI/API
- [x] Native DFS prototype
- [x] Release and CI gates

## Current Evidence

- Backend repair/status tests passed: `dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~RepairRunTests`
- Backend suite passed: `dotnet test backend.Tests/backend.Tests.csproj`
- Frontend checks passed: `npm --prefix frontend run typecheck`, `npm --prefix frontend test`, `npm --prefix frontend run build:server`, `npm --prefix frontend run build`, `npm --prefix frontend run test:e2e`
- Migration smoke passed with a temporary config path and `--db-migration`
- Whitespace check passed: `git diff --check`
- DFS path resolver/runtime-gate tests passed: `dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~DfsDavPathResolverTests|FullyQualifiedName~DfsMountServiceTests|FullyQualifiedName~ConfigManagerConcurrencyTests"`
- Docker image build passed: `docker build -t nzbdav:dfs-check .`
- Docker image runtime check passed for managed DFS assemblies in `/app/backend`; arm64 image correctly lacks `libMonoFuseHelper.so` because `Mono.Fuse.NETStandard` 1.1.0 only packages the Linux x64 native helper.
- Publish checks passed for `linux-musl-x64` and `linux-musl-arm64`; only x64 includes `libMonoFuseHelper.so`, and DFS fails closed elsewhere.
- Vulnerability checks passed: `dotnet list backend/NzbWebDAV.csproj package --vulnerable` and `npm --prefix frontend audit --audit-level=moderate`
- Production filesystem benchmark artifacts were captured on 2026-07-02 under `artifacts/benchmarks/`:
  - rclone baseline: `rclone-20260702T192559Z.json`
  - DFS candidate: `dfs-20260702T202200Z.json`
  - gate evaluation: `evaluation-20260702T202331Z.json`
- DFS was not accepted. It improved p95 seek latency from `7568.446ms` to `3284.053ms`, but CPU was `10.37` cores versus the rclone baseline `2.86` cores, and one sequential read timed out. The gate rejected DFS for CPU regression and failed correctness checks.
- Local DFS read-path remediation added direct `IFileRangeReader` handling for DFS opens, sparse-cache-backed RAR/multipart reads, and serialized range reads for AES fallback streams.
- Post-remediation local checks passed: `dotnet test backend.Tests/backend.Tests.csproj --filter "FullyQualifiedName~Streams"`, `dotnet test backend.Tests/backend.Tests.csproj`, and `git diff --check`.

## DFS Acceptance Gate

Keep `Mount:Type=rclone` as the default until repo-local benchmark artifacts show:

- p95 seek latency improves by at least 20% versus tuned rclone VFS/RC.
- NZBDav plus DFS CPU and RSS are not worse by more than 10%.
- ARR import/delete behavior remains correct.
- Restart behavior is fail-closed and never exposes an empty mounted library to Plex/ARR.
- Cache invalidation, `/ids` paths, symlink cleanup, and observability behave at least as well as the rclone path.

## Remaining Work

- Keep `Mount:Type=rclone` as the default.
- Rerun the same production DFS-vs-rclone benchmark gate after the read-path remediation. Do not consider DFS as a replacement backend unless CPU, RSS, sequential-read correctness, fail-closed, and ARR import/delete gates pass.
