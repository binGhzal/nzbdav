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

The benchmark harness is intentionally independent of DFS/native mounting. Use it
now for the rclone baseline, then run the same command against a future DFS
prototype URL when one exists.

Required input:

* `NZBDAV_BENCH_BASE_URL`: NZBDav WebDAV base URL, for example `http://localhost:3000/`.
* `NZBDAV_BENCH_PATHS`: comma-separated WebDAV paths or absolute URLs to large media-like files.

Optional input:

* `NZBDAV_BENCH_WEBDAV_USER` / `NZBDAV_BENCH_WEBDAV_PASS`: Basic auth for WebDAV.
* `NZBDAV_BENCH_API_KEY`: SAB-compatible API key for `api?mode=fullstatus` resource snapshots.
* `RCLONE_RC_URL` or `NZBDAV_BENCH_RCLONE_RC_URL`: rclone RC base URL.
* `RCLONE_RC_USER` / `RCLONE_RC_PASS`: Basic auth for rclone RC.
* `NZBDAV_BENCH_NZBDAV_PID` / `NZBDAV_BENCH_RCLONE_PID`: local process IDs for `ps` CPU/RSS snapshots.
* `NZBDAV_BENCH_RUNS`: repeated passes per path. Default: `5`.
* `NZBDAV_BENCH_SEEK_COUNT`: seek probes per path/run. Default: `5`.
* `NZBDAV_BENCH_SEEK_OFFSETS`: comma-separated byte offsets when HEAD does not expose file length.
* `NZBDAV_BENCH_SEQUENTIAL_BYTES`: sequential read window. Default: `67108864`.
* `NZBDAV_BENCH_FAIL_CLOSED_PATHS`: comma-separated paths that should fail instead of exposing stale/empty fallback content.
* `NZBDAV_BENCH_OUTPUT_DIR`: output directory. Default: `artifacts/benchmarks/`.

Example rclone baseline:

```bash
NZBDAV_BENCH_BASE_URL=http://localhost:3000/ \
NZBDAV_BENCH_PATHS=/content/example-large-file.mkv \
NZBDAV_BENCH_API_KEY=replace-with-api-key \
RCLONE_RC_URL=http://127.0.0.1:5572 \
RCLONE_RC_USER=nzbdav \
RCLONE_RC_PASS=replace-with-rc-password \
python3 scripts/nzbdav_benchmark.py run --scenario rclone
```

The command writes JSON to `artifacts/benchmarks/` by default. That directory is
ignored by git because benchmark output is machine- and workload-specific
evidence.

When DFS/native mounting exists, run the same harness against that URL:

```bash
python3 scripts/nzbdav_benchmark.py run --scenario dfs --base-url http://localhost:3001/ --path /content/example-large-file.mkv
python3 scripts/nzbdav_benchmark.py evaluate --baseline artifacts/benchmarks/rclone-YYYYMMDDTHHMMSSZ.json --candidate artifacts/benchmarks/dfs-YYYYMMDDTHHMMSSZ.json --gate
```

The evaluator is the evidence gate:

* DFS p95 seek latency must improve by at least 20% versus the tuned rclone baseline.
* DFS CPU and RSS must not be worse by more than 10% when those snapshots are available.
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
