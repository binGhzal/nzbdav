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

* `--rc` with authenticated RC access configured in NZBDav Settings > Rclone.
* `--vfs-cache-mode=full`.
* `--buffer-size=0M`.
* `--vfs-read-ahead=512M`.
* `--vfs-read-chunk-size=4M`.
* `--vfs-read-chunk-streams=16`.
* short `--dir-cache-time`, plus NZBDav `vfs/forget` invalidations.

Record:

* first-byte latency for sequential read and seek-heavy playback.
* CPU percent for NZBDav, rclone, and the media client.
* managed memory, working set, GC count, and active stream count from `status/fullstatus`.
* rclone invalidation backlog and latest invalidation error.
* provider errors, fallback count, and active connection count.
* time until Plex/Radarr/Sonarr sees new, changed, repaired, and deleted files.

## DFS/native prototype scenario

Prototype DFS/native mount in a branch or isolated deployment. It must use the same NZBDav provider config, same ARR/Plex workloads, same test media, and same host cache/disk constraints as the rclone baseline.

Record the same metrics as the baseline and add:

* mount process health and restart behavior.
* path-level error reporting.
* cache eviction/invalidation behavior.
* filesystem semantics for links, renames, deletes, stat calls, and partial reads.

## Decision rule

Accept DFS/native mounting only if it is faster and safer across the acceptance criteria. If it only improves one dimension while adding another long-lived mount path, keep rclone and optimize the existing backend.
