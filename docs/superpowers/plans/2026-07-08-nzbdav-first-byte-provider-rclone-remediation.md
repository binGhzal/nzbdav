# NZBDav First-Byte, Provider Stability, Health Repair, And Rclone Lifecycle Plan

## Summary
Fix the measured production bottleneck in this order: NZBDav provider reliability, health-check crash handling, faster verification, local read caching, rclone stale-mount recovery, then Plex scan isolation. Keep rclone as the production mount and do not promote DFS. Add diagnostics and benchmarks, not more WebUI tuning knobs.

## Key Changes
- Save this plan during execution to `docs/superpowers/plans/2026-07-08-nzbdav-first-byte-provider-rclone-remediation.md`, create a `codex/first-byte-provider-rclone-remediation` branch, and preserve the current clean `main`.

- Provider stability:
  - Harden `ProviderCircuitBreaker` so missing articles do not trip providers, retryable socket/timeouts use soft cooldowns, and hard trips are limited to clear auth/config failures.
  - Add provider status fields to `status/fullstatus`: active connections, configured max, failure count, circuit state, cooldown-until, last success, last failure kind, and backup/priority role.
  - Prefer reachable backup providers immediately for foreground reads instead of waiting for all primaries to recover.
  - Keep provider circuit tuning out of the WebUI; use safe internal defaults and existing config/env paths only.

- Health check and repair correctness:
  - Replace direct segment-result dictionary indexing in `HealthCheckService` with a safe helper that emits `SegmentCheckState.Unknown` for absent results and continues the scan.
  - Only schedule repair for definitive missing-on-all-provider results; unknown/provider-error results should recheck later and surface as degraded, not crash or trigger unsafe repair.
  - Audit other segment-result dictionary access, especially verification dedupe paths, for the same failure mode.

- Verification speed:
  - Batch post-download verification by queue item and provider, reuse recent segment-check results, and avoid rechecking known-good segments inside the same run.
  - Keep download, verify, and repair queues separate with independent max concurrency so verify saturation cannot starve downloads or repairs.
  - Add timing counters for verification queue wait, segment-check latency, provider latency, and repair-enqueue decisions.

- First-byte and range-read caching:
  - Extend the existing sparse cache path so small range reads can return after the requested bytes are available instead of waiting on oversized read-ahead.
  - Add adaptive initial fetch sizing for first-byte reads, then schedule larger read-ahead in the background.
  - Cache hot assembled file ranges locally under the existing temporary cache budget; cache files remain disposable and excluded from migration/backup.
  - Add cache diagnostics for first-byte latency, range hit/miss, pending fetches, evictions, and provider fetch errors.

- Rclone lifecycle safety:
  - Update the media-stack rclone entrypoint so it checks for `Socket not connected` / `Transport endpoint is not connected` before mounting and performs lazy unmount before starting rclone.
  - Keep the current tuned rclone profile as the baseline: `vfs-cache-mode=writes`, zero buffer/read-ahead, small read chunks, and no DFS promotion.
  - Ensure helper scripts do not recreate rclone unless config/settings changed or the mount is actually unhealthy.
  - Make the heartbeat recovery path pause Plex/ARR consumers, unmount stale FUSE, recreate rclone, verify the mount, then resume consumers.

- Plex scan isolation:
  - Add operational guardrails so broad Plex scans/metadata jobs are avoided while NZBDav/rclone is degraded or active playback is using the gateway.
  - Prefer targeted ARR import refresh hooks over root library scans.
  - Document the production setting changes needed to reduce Plex analysis churn against remote-backed files.

## Public Interfaces
- `status/fullstatus` gains additive `provider_pools`, `cache`, `verification`, and `mount` diagnostics.
- Existing SAB-compatible queue/history/status shapes remain ARR-compatible.
- No new required default settings and no broad WebUI control expansion.
- Benchmark artifacts continue under `artifacts/benchmarks/`.

## Test Plan
- Add failing backend tests for missing segment results in health checks, proving no `KeyNotFoundException` and correct unknown-vs-repair behavior.
- Add provider circuit tests for missing article, retryable timeout, auth/config hard failure, backup fallback, and foreground probe recovery.
- Add sparse-cache tests for 1 MiB range reads, concurrent read dedupe, hot range hits, eviction under budget, and no permanent cache storage.
- Extend `scripts/nzbdav_benchmark.py` scenarios to capture direct WebDAV, `rclone cat`, FUSE path, Plex part endpoint, and 4-file parallel wall time.
- Acceptance gate: rerun the benchmark set and require direct NZBDav first-byte average/max to improve materially from the captured baseline, rclone FUSE overhead not to exceed the previous ratio, no health-check crashes, and no stale FUSE mount after forced rclone restart.
- Full gates: backend tests/build, frontend typecheck/test/build/server build, Docker build, migration smoke, `git diff --check`, then production benchmark artifact capture.

## Assumptions
- Rclone remains the production filesystem path.
- DFS remains rejected until benchmark evidence beats tuned rclone on the same host and workload.
- Unknown segment-check results are not treated as safe repair triggers.
- Provider credential/SSL/port changes must be verified against the real provider configuration before applying production config edits.
