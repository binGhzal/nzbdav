# NZBDav Decypharr-Inspired Cache, Repair, And DFS Plan

## Goal

Make NZBDav faster and safer for ARR/Plex Usenet workloads by improving the existing WebDAV/rclone stream path first, then benchmark-gating a Linux-only native DFS prototype as a possible replacement mount backend.

## Execution State

- [x] Preflight checkpoint and baseline gates
- [x] Benchmark harness and DFS evidence gate
- [x] Repair-safe segment checking
- [x] Sparse segment cache and random-access reader
- [x] Archive segment slicing
- [x] Persisted repair history and status APIs
- [x] Operator diagnostics UI/API
- [ ] Native DFS prototype
- [ ] Release and CI gates

## Current Evidence

- Backend repair/status tests passed: `dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~RepairRunTests`
- Backend suite passed: `dotnet test backend.Tests/backend.Tests.csproj`
- Frontend checks passed: `npm --prefix frontend run typecheck`, `npm --prefix frontend test`, `npm --prefix frontend run build:server`, `npm --prefix frontend run build`, `npm --prefix frontend run test:e2e`
- Migration smoke passed with a temporary config path and `--db-migration`
- Whitespace check passed: `git diff --check`

## DFS Acceptance Gate

Keep `Mount:Type=rclone` as the default until repo-local benchmark artifacts show:

- p95 seek latency improves by at least 20% versus tuned rclone VFS/RC.
- NZBDav plus DFS CPU and RSS are not worse by more than 10%.
- ARR import/delete behavior remains correct.
- Restart behavior is fail-closed and never exposes an empty mounted library to Plex/ARR.
- Cache invalidation, `/ids` paths, symlink cleanup, and observability behave at least as well as the rclone path.

## Remaining Work

- Implement `Mount:Type=dfs` behind a Linux-only feature path using `Mono.Fuse.NETStandard`.
- Add `GET /api/mount/status` and include mount readiness in `fullstatus`.
- Document Docker `/dev/fuse` and capability requirements.
- Add DFS mount status tests and benchmark artifact generation for manual production comparisons.
- Finish release docs, changelog notes, and CI/release gates after DFS is benchmarked.
