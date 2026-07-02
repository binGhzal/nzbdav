# DFS/native mount benchmark gate

NZBDav should keep one supported media-mount backend at a time. The current supported path is WebDAV plus rclone VFS/RC because it already supports Plex-visible symlinks, cache invalidation, ARR imports, and operational diagnostics.

DFS/native mounting is a replacement candidate only. Do not add it as a second permanent backend unless the benchmark below proves it is better enough to remove the rclone path.

## Acceptance criteria

DFS/native mounting must beat tuned rclone VFS/RC in measured tests across all of these areas:

* seek latency during Plex playback of large media files.
* restart persistence when NZBDav or the mount process restarts mid-playback.
* cache invalidation correctness after imports, deletes, repairs, and cleanup jobs.
* symlink behavior for Plex, Radarr, Sonarr, and Lidarr library scans.
* ARR completed-download import semantics.
* observability for active streams, failed paths, stale caches, provider failures, and mount health.
* failure safety when NZBDav is down or overloaded, so media clients do not scan an empty or stale library.

## Baseline rclone scenario

Use the tuned rclone command from `docs/setup-guide.md`:

* `--links`.
* `--use-cookies`.
* `--allow-other`.
* `--rc` with authenticated RC access configured in NZBDav Settings > Rclone.
* `--vfs-cache-mode=full`.
* `--buffer-size=0M`.
* `--vfs-read-ahead=512M`.
* `--vfs-read-chunk-size=4M`.
* `--vfs-read-chunk-streams=16`.
* short `--dir-cache-time`, plus NZBDav `vfs/forget` invalidations.

Record:

* first-byte latency for sequential read and seek-heavy playback.
* p50/p95 first-byte latency, p95 seek latency, and sequential throughput from `scripts/nzbdav_benchmark.py`.
* CPU percent for NZBDav, rclone, and the media client.
* managed memory, working set, GC count, and active stream count from `status/fullstatus`.
* rclone invalidation backlog and latest invalidation error.
* provider errors, fallback count, and active connection count.
* time until Plex/Radarr/Sonarr sees new, changed, repaired, and deleted files.

## Repo-local benchmark harness

The benchmark harness is intentionally transport-aware. Use filesystem transport
for the rclone baseline and the DFS candidate so the gate measures the same
mounted paths Plex and ARR actually consume.

Required input:

* `NZBDAV_BENCH_TRANSPORT`: `filesystem` for mounted-path benchmarks or `http` for WebDAV range checks. Use `filesystem` for the DFS-vs-rclone acceptance gate because Plex/ARR consume mounted paths.
* `NZBDAV_BENCH_MOUNT_ROOT`: mount root for filesystem benchmarks, for example `/mnt/media/gateways/nzbdav`.
* `NZBDAV_BENCH_BASE_URL`: NZBDav WebDAV base URL, for example `http://localhost:3000/`. Required for HTTP benchmarks and optional for filesystem benchmarks; when present it is used to collect `status/fullstatus` resource, cache, provider, and mount snapshots.
* `NZBDAV_BENCH_PATHS`: comma-separated mounted paths, WebDAV paths, or absolute URLs to large media-like files. For filesystem benchmarks with `NZBDAV_BENCH_MOUNT_ROOT`, paths are logical mount-root paths and are always resolved below that root. Parent-directory escapes are rejected.

Optional input:

* `NZBDAV_BENCH_WEBDAV_USER` / `NZBDAV_BENCH_WEBDAV_PASS`: Basic auth for WebDAV.
* `NZBDAV_BENCH_API_KEY`: SAB-compatible API key for `api?mode=fullstatus` resource snapshots.
* `RCLONE_RC_URL` or `NZBDAV_BENCH_RCLONE_RC_URL`: rclone RC base URL.
* `RCLONE_RC_USER` / `RCLONE_RC_PASS`: Basic auth for rclone RC.
* `NZBDAV_BENCH_NZBDAV_PID` / `NZBDAV_BENCH_RCLONE_PID`: local process IDs for `ps` CPU/RSS snapshots. The rclone baseline must include both NZBDav and rclone process evidence. The DFS candidate must include NZBDav/DFS process evidence.
* `NZBDAV_BENCH_RUNS`: repeated passes per path. Default: `5`.
* `NZBDAV_BENCH_SEEK_COUNT`: seek probes per path/run. Default: `5`.
* `NZBDAV_BENCH_SEEK_OFFSETS`: comma-separated byte offsets when HEAD does not expose file length.
* `NZBDAV_BENCH_SEQUENTIAL_BYTES`: sequential read window. Default: `67108864`.
* `NZBDAV_BENCH_FAIL_CLOSED_PATHS`: comma-separated paths that should fail instead of exposing stale/empty fallback content.
* `NZBDAV_BENCH_OUTPUT_DIR`: output directory. Default: `artifacts/benchmarks/`.

Example rclone baseline:

```bash
NZBDAV_BENCH_TRANSPORT=filesystem \
NZBDAV_BENCH_MOUNT_ROOT=/mnt/media/gateways/nzbdav \
NZBDAV_BENCH_BASE_URL=http://localhost:3000/ \
NZBDAV_BENCH_PATHS=/content/example-large-file.mkv \
NZBDAV_BENCH_API_KEY=replace-with-api-key \
NZBDAV_BENCH_NZBDAV_PID=$(pgrep -f NzbWebDAV | head -1) \
NZBDAV_BENCH_RCLONE_PID=$(pgrep -f 'rclone mount' | head -1) \
RCLONE_RC_URL=http://127.0.0.1:5572 \
RCLONE_RC_USER=nzbdav \
RCLONE_RC_PASS=replace-with-rc-password \
python3 scripts/nzbdav_benchmark.py run --scenario rclone
```

The command writes JSON to `artifacts/benchmarks/` by default. That directory is
ignored by git because benchmark output is machine- and workload-specific
evidence.

When DFS/native mounting exists, run the same harness against the DFS mount root. Keep the same `NZBDAV_BENCH_PATHS`, run count, seek count, sequential window, fail-closed paths, and host:

```bash
python3 scripts/nzbdav_benchmark.py run \
  --scenario dfs \
  --transport filesystem \
  --mount-root /mnt/media/gateways/nzbdav-dfs \
  --base-url http://localhost:3000/ \
  --nzbdav-pid "$(pgrep -f /tmp/nzbdav-dfs-selfcontained/NzbWebDAV | head -1)" \
  --path /content/example-large-file.mkv
python3 scripts/nzbdav_benchmark.py evaluate --baseline artifacts/benchmarks/rclone-YYYYMMDDTHHMMSSZ.json --candidate artifacts/benchmarks/dfs-YYYYMMDDTHHMMSSZ.json --gate
```

For acceptance-gate runs, provide enough status/PID inputs for both artifacts to include CPU and RSS metrics. The evaluator rejects missing CPU/RSS evidence because the plan requires DFS resource use to be no worse than tuned rclone by more than 10%.

The evaluator is the evidence gate:

* Baseline and candidate artifacts must both use the same transport. The production acceptance gate should compare `filesystem` to `filesystem`, not WebDAV HTTP to FUSE.
* DFS p95 seek latency must improve by at least 20% versus the tuned rclone baseline.
* DFS CPU and RSS must not be worse by more than 10%, and CPU/RSS evidence is mandatory for both compared stacks.
* DFS correctness and fail-closed checks must pass.

## DFS/native prototype scenario

Prototype DFS/native mount in a branch or isolated deployment. It must use the same NZBDav provider config, same ARR/Plex workloads, same test media, and same host cache/disk constraints as the rclone baseline.

Record the same metrics as the baseline and add:

* mount process health and restart behavior.
* path-level error reporting.
* cache eviction/invalidation behavior.
* filesystem semantics for links, renames, deletes, stat calls, and partial reads.

## Decision rule

Accept DFS/native mounting only if it is faster and safer across the acceptance criteria: p95 seek improves at least 20%, CPU/RSS are not worse by more than 10%, and correctness/fail-closed checks pass. If it only improves one dimension while adding another long-lived mount path, keep rclone and optimize the existing backend.

## Latest production result

The 2026-07-02 production benchmark kept rclone as the accepted backend:

* rclone baseline artifact: `artifacts/benchmarks/rclone-20260702T192559Z.json`.
* DFS candidate artifact: `artifacts/benchmarks/dfs-20260702T202200Z.json`.
* evaluation artifact: `artifacts/benchmarks/evaluation-20260702T202331Z.json`.
* DFS p95 seek latency improved from `7568.446ms` to `3284.053ms`.
* DFS CPU failed the gate at `10.37` cores versus `2.86` baseline cores.
* DFS correctness failed because one sequential read timed out.

Do not switch production to `Mount:Type=dfs` from this result. Fix the DFS CPU regression and timeout first, then rerun the same filesystem-transport benchmark gate.

Follow-up remediation moved DFS reads onto direct `IFileRangeReader` handles, extended the sparse cache to RAR/multipart streams, and fixed legacy blob-backed multipart files whose version-tolerant `SegmentSlices` field deserializes as `null`.

The patched production benchmark on 2026-07-02 still kept rclone as the accepted backend:

* rclone baseline artifact: `/opt/media-stack/artifacts/nzbdav-bench/rclone-20260702T203750Z.json`.
* patched DFS candidate artifact: `/opt/media-stack/artifacts/nzbdav-bench/dfs-patched-20260702T210018Z.json`.
* evaluation artifact: `/opt/media-stack/artifacts/nzbdav-bench/evaluation-20260702T210030Z.json`.
* DFS correctness, fail-closed, CPU, and RSS checks passed.
* DFS p95 seek latency failed the gate at `6025.497ms` versus rclone `0.610ms`.

Do not switch production to `Mount:Type=dfs` from this result. Keep rclone as the default and optimize the DFS random-seek path before running the gate again.
