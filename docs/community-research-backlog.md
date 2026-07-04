# Community Research Backlog

Last updated: 2026-07-04.

This backlog comes from a live scrape of the public NZBDav and Decypharr
issue/PR trackers plus forum and Reddit threads about ARR/Plex Usenet
streaming. It is intentionally a product backlog, not a list of everything
from Decypharr to clone. NZBDav remains focused on direct Usenet, SAB-compatible
ARR integration, and Plex/Jellyfin access through WebDAV/rclone or benchmarked
DFS.

## Source Snapshot

* NZBDav GitHub: 253 issues and 138 PRs reviewed from
  <https://github.com/nzbdav-dev/nzbdav>.
* Decypharr GitHub: 211 issues and 119 PRs reviewed from
  <https://github.com/sirrobot01/decypharr>.
* Forum/community threads reviewed:
  * <https://github.com/nzbdav-dev/nzbdav/discussions/106>
  * <https://www.reddit.com/r/selfhosted/comments/1mejsdh/nzbdav_infinite_plex_library_w_usenet_streaming/>
  * <https://www.reddit.com/r/selfhosted/comments/1ogaagy/nzbdav_infinite_plex_library_with_usenet_streaming/>
  * <https://www.reddit.com/r/selfhosted/comments/1mmfqx5/decypharr_a_bridge_between_sonarrradarr_and/>
  * <https://forums.unraid.net/topic/198498-rclone-scripts-to-mount-nzbdav-to-create-a-large-plex-library-with-fast-launch-times-and-efficient-arr-usage/>
  * <https://www.reddit.com/r/rclone/comments/1qj4pjm/help_needed_nzbdav_and_rclone_setup_for_symlinks/>
* CineSync, Riven, and FileBot feature research is tracked separately in
  [media-organization-integration-research.md](media-organization-integration-research.md).
* The active ARR-compatible direction is ARR-driven download prioritization in
  [arr-driven-download-prioritization.md](arr-driven-download-prioritization.md).
* ARR-safe implementation boundaries for those features are tracked in
  [arr-safe-media-organization-design.md](arr-safe-media-organization-design.md).

## Already Covered In This Fork

These requests came up repeatedly, but the fork already has working coverage
or a deliberate implementation path:

* Concurrent queue downloads, queue sorting, queue filters, page-size controls,
  pause/resume, and live status refresh.
* SAB-compatible status/fullstatus, queue/history pagination clamps,
  malformed ID handling, active streams, server stats, and warnings endpoints.
* Provider priority/fallback ordering, provider circuit breakers, connection
  pool diagnostics, and optional STAT pipelining.
* PostgreSQL support for new installs plus SQLite WAL/busy-timeout tuning.
* Durable download, verify, and repair jobs with lease/retry/quarantine state.
* Repair history/status APIs and fullstatus repair/cache/mount/worker fields.
* Durable rclone VFS invalidation outbox, including `/content`,
  `/completed-symlinks`, and `/.ids` paths.
* Sparse segment cache and the DFS prototype behind a benchmark gate, with
  rclone remaining the default supported mount path.
* Combined symlink plus STRM import mode for mixed Plex/Jellyfin or HTTP client
  deployments.
* Lidarr instance support.

## Implemented From This Review

* Implicit NNTP TLS normalization: ports `563` and `443` now use TLS even when
  an imported provider config forgot to set `UseSsl=true`. Status diagnostics
  expose whether TLS was configured directly or inferred.
* Opt-in relative `.rclonelink`/DFS symlink targets through
  `api.symlink-target-mode=relative` or `NZBDAV_SYMLINK_TARGET_MODE=relative`.
  This addresses Docker host/container path mismatches without adding another
  WebUI control.
* Linux regression coverage for yEnc segments larger than 1 MiB so direct
  streaming cannot silently truncate oversized NNTP article bodies.

## Ranked Backlog

### P0 - Safety And Correctness

* Extend yEnc/NNTP regression coverage to pooled connection reuse after yEnc
  body/article reads. Decypharr issue #257 shows NNTP stream-desync risk after
  article reads.
* Add focused RAR edge-case tests for split volumes with duplicate-looking
  volume numbers and non-`m0` compression detection. NZBDav issues #463, #150,
  and #132 show real archive compatibility gaps.
* Keep repair conservative: only destructive repair/search actions should run
  after definitive missing-on-all-provider evidence, never provider-error or
  unknown states. This is the lesson from NZBDav issue #424 and Decypharr
  repair issues #193/#278/#297.

### P1 - ARR/Plex Operability

* Add a managed mount supervisor path for rclone deployments: readiness checks,
  stale-mount detection, rc health, restart-order guidance, and clear fail-closed
  status for Plex/Radarr/Sonarr. The Unraid scripts thread shows users building
  this externally today.
* Add optional Plex/Jellyfin scan hooks after successful import/delete/repair.
  Keep this opt-in and per-library because scans can exhaust provider limits on
  virtual media.
* Improve ARR feedback for retryable errors: explicit retry action, retryable
  vs terminal failure text, and path-mapping diagnostics when imports stall.

### P2 - Provider And Network Resilience

* Add provider bandwidth accounting and per-provider bytes/articles counters to
  diagnostics so users can see which provider is serving playback and repair.
* Evaluate SOCKS5 and TLS verification controls for NNTP providers. Do not add
  these to the main UI unless they are needed in production; prefer env/config
  support plus diagnostics first.
* Keep TorBox/debrid routing out of the primary path unless benchmarked evidence
  shows it simplifies the stack. It adds another backend decision and is not
  required for direct-NNTP NZBDav.

### P3 - Setup Friction

* Add platform-specific preflight checks for `/dev/fuse`, rclone `--links`,
  mount propagation, permissions, and ARR remote-path mappings.
* Add a validated compose fragment for rclone RC plus NZBDav health/readiness
  gating.
* Keep reducing settings surface. Prefer good automatic behavior, redacted env
  defaults, and diagnostics over exposing every internal limit in the WebUI.

### P4 - ARR-Driven Download Priority

* Add ARR metadata correlation and adaptive download prioritization so NZBDav
  bumps already-queued ARR downloads for recently aired episodes, shows/seasons
  close to completion, collection-completing movies, recently available movies,
  and similar ARR-owned library goals. See
  [arr-driven-download-prioritization.md](arr-driven-download-prioritization.md).
* Add ARR search nudging so NZBDav can ask ARR to search its own monitored
  missing/wanted items in completion-focused order, while keeping ARR in charge
  of search/grab decisions.
* Harden SAB-compatible queue/history/progress reporting so ARR sees stable
  `nzo_id` lifecycle state and does not repeatedly request the same media
  because NZBDav hid queued, repairing, or completed-but-not-imported work.
* Keep metadata-backed organization, template naming, virtual library profiles,
  media-server refresh hooks, and FileBot integration deferred. They are not the
  active default direction because they compete with ARR ownership.
