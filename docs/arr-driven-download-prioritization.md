# ARR-Driven Download Prioritization

Last updated: 2026-07-04.

This supersedes the earlier media-organization direction. NZBDav should not
organize media, replace ARR naming, create competing virtual libraries, or
decide what content should exist. Radarr, Sonarr, and Lidarr own the library.
NZBDav should only download the items ARR already sent, using ARR metadata to
decide which queued downloads should run first.

## Boundary

ARR owns:

* requests, lists, RSS, search, grabs, upgrades, quality profiles, custom
  formats, monitoring, final import, final rename, collections, and delete
  decisions;
* movie, series, episode, artist, album, collection, calendar, and missing-item
  state;
* media-server integration when the operator already configured ARR Connect
  hooks.

NZBDav owns:

* SAB-compatible add/queue/history/status behavior;
* NNTP provider use, connection scheduling, repair, verify, cache, and mount
  readiness;
* adaptive queue ordering among downloads already present in NZBDav;
* optional ARR search nudges that ask ARR to search its own wanted/missing items
  in a smarter order;
* transparent diagnostics explaining why a queued item was bumped or left alone.

NZBDav must never search, add, monitor, unmonitor, rename, import, or delete
ARR library items directly. Search nudges must go through ARR command APIs and
only for media ARR already marks monitored/wanted/missing.

## Product Goal

Use ARR metadata to make the NZBDav download queue finish the most useful
library states first:

* finish shows or seasons that have fewer remaining monitored episodes;
* prioritize recently aired or newly available episodes;
* prioritize movies that complete Radarr collections;
* prioritize recently released movies that ARR already grabbed;
* report Lidarr correlation and completion signals without executing Lidarr
  search commands until command behavior is verified;
* nudge ARR to search the highest-value missing/wanted items first instead of
  relying only on broad wanted-page order, date order, or name order;
* report a stable queue/history lifecycle so ARR knows a requested item is
  queued, downloading, verifying, repairing, completed, or failed and does not
  repeatedly request the same media;
* keep manual priorities and pauses stronger than automation;
* avoid starving old items.

This makes ARR better because ARR still controls the media plan, while NZBDav
uses its download concurrency and provider budget more intelligently.

## Data Sources

Preferred correlation path:

1. ARR sends an NZB to NZBDav through the SAB-compatible API.
2. NZBDav returns `nzo_ids`.
3. ARR queue/history/custom-script data maps that download id back to ARR media
   IDs.
4. NZBDav stores only the metadata needed for queue ordering and diagnostics.

Supported inputs:

* Existing ARR polling through configured Radarr/Sonarr/Lidarr instances.
* Optional ARR custom-script event ingestion for Grab, Import/Upgrade, Rename,
  File Delete, Manual Interaction Required, Health Issue, and Health Restored.
* Existing NZBDav queue item fields: category, job name, file name, size,
  operator priority, pause state, created time, and completion state.
* ARR wanted/missing, queue, history, calendar, movie/series/episode, and
  collection metadata fetched only for configured ARR instances.

Polling must be bounded and cached. NZBDav should poll the ARR queue frequently
enough to keep active downloads correlated, but deeper library metadata should
use TTL caching and only be refreshed for queued items.

## ARR Search Nudging

ARR should still decide what searches mean and what releases are acceptable.
NZBDav can improve the order in which ARR performs searches by asking ARR to
search a small, ranked batch of ARR-owned missing/wanted items.

Search nudge workflow:

1. Fetch missing/wanted candidates from ARR.
2. Remove anything already present in ARR queue, NZBDav queue, NZBDav history
   pending import, or recent failed-search cooldown.
3. Score the remaining candidates with the same completion-focused rules used
   for download priority.
4. Submit a small batch to ARR through `POST /api/v3/command`.
5. Record command id, target ids, reason labels, result, cooldown, and next
   eligible search time.
6. Trigger or wait for `RefreshMonitoredDownloads` so ARR sees resulting
   download-client state.

App-specific command policy:

* Sonarr:
  use missing/episode search commands only for monitored missing episodes. The
  first implementation should prefer episode-level batches so NZBDav can search
  "what completes the season/show" first without asking Sonarr to search a
  whole library backlog.
* Radarr:
  use movie search commands only for monitored missing movies already in Radarr.
  Prioritize collection-completing and recently available movies first.
* Lidarr:
  defer apply mode until album/artist command behavior is verified.

Search nudge controls:

* `SearchNudge.Enabled=false`: no search nudges.
* `SearchNudge.Enabled=true` and `SearchNudge.Mode=report`: compute and store
  the search plan without calling ARR.
* `SearchNudge.Enabled=true` and `SearchNudge.Mode=apply`: call ARR commands
  for ranked Sonarr/Radarr batches.

Search nudges must be rate limited per ARR instance and per media item. They
must never loop on the same missing item when ARR already has a tracked download
or NZBDav still has queue/history state for the previous request.

Implemented config under `arr.instances`:

* `Prioritization.Enabled`
* `Prioritization.Mode=report|apply`
* `Prioritization.RecomputeIntervalSeconds`
* `Prioritization.MaxAutomaticPriority`
* `SearchNudge.Enabled`
* `SearchNudge.Mode=report|apply`
* `SearchNudge.IntervalSeconds`
* `SearchNudge.CooldownSeconds`
* `SearchNudge.MaxCommandsPerHour`
* `SearchNudge.SonarrBatchSize`
* `SearchNudge.RadarrBatchSize`
* `SearchNudge.ConcurrentCommandsPerInstance`

Defaults should be `Enabled=false` and `Mode=report` until production behavior
is verified.

## ARR Progress And Loop Prevention

ARR tracks a SAB-compatible client by `nzo_id`, queue output, history output,
status output, and completed-download path. NZBDav must make that lifecycle
stable and explicit.

Required behavior:

* `addfile` and `addurl` always return stable `nzo_ids`.
* A queue item keeps the same `nzo_id` from queued through downloading, verify,
  repair, and completion.
* Queue output must include every active or pending item ARR might ask about,
  with accurate category, status, percentage, size, size-left, and priority.
* History output must retain completed and failed items long enough for ARR to
  import or handle failure.
* Completed history storage path must not be exposed as completed until the
  completed symlink/STRM path, mount invalidation, repair state, and content
  snapshot are ready.
* Failed history must distinguish definitive failed release from retryable
  provider/repair/cache/mount failures.
* `RefreshMonitoredDownloads` should be debounced after state transitions:
  queued, active progress, verify/repair transition, completed, failed, and
  mount-ready-after-completion.

Loop prevention keys:

* ARR app and instance.
* ARR media id: movie id, series/episode ids, artist/album ids.
* NZBDav `nzo_id`.
* Release title and indexer when available.
* Category.
* Terminal state and timestamp.

NZBDav should expose diagnostics when ARR appears to request the same media
again while an equivalent item is already queued, downloading, verifying,
repairing, completed-but-not-imported, or recently failed with a retryable
reason.

Do not block ARR from sending another NZB. The first implementation should
report duplicates and optionally lower automatic priority for likely duplicates.
Only add a hard reject mode after real production evidence shows ARR duplicate
requests are caused by missing NZBDav reporting rather than legitimate upgrades.

## Priority Model

Do not overwrite operator intent. Store adaptive priority separately from the
manual/SAB priority.

Suggested persisted fields or companion table:

* `QueuePriorityHint`
  * `QueueItemId`
  * `Score`
  * `Band`
  * `Source`
  * `ReasonsJson`
  * `ComputedAt`
  * `ExpiresAt`
* `ArrDownloadCorrelation`
  * `QueueItemId`
  * `ArrApp`
  * `ArrInstance`
  * `DownloadId`
  * `MovieId`
  * `SeriesId`
  * `EpisodeIds`
  * `ArtistId`
  * `AlbumId`
  * `ReleaseTitle`
  * `Quality`
  * `CustomFormats`
  * `IsUpgrade`

Scheduler ordering:

1. paused items never start;
2. manual `Force` always wins;
3. manual `High` beats adaptive normal items;
4. adaptive score orders items inside the same manual priority band;
5. age adds a small anti-starvation score;
6. created time remains the final deterministic tie breaker.

This avoids the current risk of automation constantly rewriting the coarse SAB
priority field and fighting the user.

## Scoring Rules

Initial default rules should be conservative and explainable.

### Sonarr

Signals:

* Recently aired episode:
  bump if the episode aired within the configured recent-air window.
* Season completion:
  bump when this download completes or nearly completes a season.
* Series completion:
  stronger bump when this download completes or nearly completes all monitored
  missing episodes for a series.
* Low remaining count:
  bump more when fewer monitored episodes remain after queued downloads are
  accounted for.
* Manual grab or forced ARR action:
  bump, but still below NZBDav manual `Force`.
* Upgrade:
  lower than missing episodes by default unless the ARR item is already high
  priority manually.

Example reason labels:

* `recently-aired`
* `season-nearly-complete`
* `series-nearly-complete`
* `few-monitored-missing-left`
* `manual-arr-grab`
* `upgrade-lower-than-missing`

### Radarr

Signals:

* Collection completion:
  bump if this movie completes or nearly completes a Radarr collection already
  monitored by Radarr.
* Recently released or recently available:
  bump if Radarr already grabbed the movie and the release/availability date is
  within the recent window.
* Low remaining collection count:
  bump more when fewer missing monitored movies remain in that collection.
* Manual grab:
  bump, but still below NZBDav manual `Force`.
* Upgrade:
  lower than missing collection/recent-release items by default.

Example reason labels:

* `collection-nearly-complete`
* `collection-complete-after-download`
* `recently-available`
* `manual-arr-grab`
* `upgrade-lower-than-missing`

### Lidarr

Signals:

* Album completion:
  bump if an album becomes complete.
* Artist completion:
  bump if few monitored albums remain missing for the artist.
* Recent release:
  bump if ARR metadata marks it newly released.

Keep Lidarr scoring behind Sonarr/Radarr until the metadata behavior is verified
against real usage.

### Global

Signals:

* Queue age:
  add a small bounded score so older items do not starve.
* Small fast-complete item:
  optional low-weight score if completing a small item clears a pending ARR
  import quickly.
* Provider pressure:
  do not let priority scoring exceed provider connection budgets; it only
  chooses ordering, not unlimited concurrency.

## API And UI

Implemented additive SAB/status surface:

* `status` and `fullstatus` expose `arr_prioritization`,
  `arr_search_nudge`, and `arr_download_report`.
* SAB `queue` rows expose additive `arr_priority` with score, effective
  priority, report/apply scheduling state, reasons, source, and stale reason.
* SAB `mode=get_files` returns per-item file status when ARR asks for file
  details.
* Queue/history shapes remain SAB-compatible for ARR clients.

Future operator APIs may add manual recompute, custom-script event ingestion,
and a dedicated download-report endpoint. Those are not required for the first
safe ARR-driven download pass because status/fullstatus and queue rows already
expose the diagnostics ARR integrations need.

WebUI:

* Queue table shows ARR priority metadata as compact row details with effective
  priority and reason labels.
* Health page shows correlation coverage, stale metadata, search nudge
  dry-run/apply state, command failures, duplicate-request diagnostics, and
  lifecycle state counts.
* Settings should stay minimal:
  * enable priority scoring;
  * priority mode;
  * recompute interval;
  * enable search nudging;
  * search nudge mode;
  * cooldown, hourly rate limit, and Sonarr/Radarr batch sizes.

Default mode should be `report` until production behavior is verified.

## Safety Rules

* Do not reprioritize paused items.
* Do not demote manual `Force`.
* Do not promote above configured `MaxAutomaticBand`.
* Do not change ARR quality, monitoring, search, or custom format decisions.
* Do not call ARR grab/add/delete endpoints or direct indexer/manual-grab APIs
  from this system.
* Search nudge apply mode may call ARR command endpoints only for ARR-owned,
  monitored, missing/wanted items after duplicate and cooldown checks.
* Do not blocklist from scoring; blocklist/search remains only the explicit
  stuck-item cleanup path.
* Do not use stale metadata for apply mode. Fall back to existing queue order.
* If ARR is unavailable, keep the previous computed hints until expiry, then
  fall back to manual priority plus age.
* Every automatic priority change must have visible reasons and timestamps.
* Every ARR search nudge command must have visible target ids, reason labels,
  command id, cooldown, result, and error text.
* Do not suppress duplicate ARR requests by default; report them first.

## Implementation Order

1. Add `ArrDownloadCorrelation` and `QueuePriorityHint` models/migrations.
2. Add `ArrSearchNudgePlan`, `ArrSearchNudgeCommand`, and download lifecycle
   report storage.
3. Extend ARR queue models with download id and enough media IDs to correlate
   queued items.
4. Add `ArrPriorityService` that computes scores and writes hints without
   changing queue order.
5. Add `ArrSearchNudgeService` in report-only mode.
6. Add download lifecycle reporting and duplicate-request diagnostics.
7. Add status API/UI to inspect scores, search plans, and lifecycle reports.
8. Change queue selection to sort by manual priority, adaptive score, age, and
   created time.
9. Enable queue-priority apply mode behind `Prioritization.Enabled=true` and
   `Prioritization.Mode=apply`.
10. Enable search-nudge apply mode behind `SearchNudge.Enabled=true` and
   `SearchNudge.Mode=apply`.
11. Add Sonarr scoring first, then Radarr collection/recent-availability
   scoring, then Lidarr.
12. Add custom-script event ingestion later if queue polling does not provide
   enough correlation coverage in production.

## Tests

Backend:

* Manual `Force` and paused items override adaptive scoring.
* Stale ARR metadata is ignored in apply mode.
* Recent Sonarr episodes get higher score than older equal-priority episodes.
* A season-completing episode gets higher score than an unrelated episode.
* A series-completing download gets higher score than a season-only completion.
* A Radarr collection-completing movie gets higher score than an unrelated
  movie.
* Upgrade downloads score below missing-media downloads by default.
* ARR API outage leaves queue stable and visible as degraded.
* Priority decisions are deterministic for equal scores.
* Search nudge dry run excludes media already in ARR queue, NZBDav queue, or
  NZBDav completed history awaiting import.
* Search nudge apply mode calls only ARR command endpoints and respects
  cooldown/batch limits.
* Duplicate ARR requests are reported without rejecting the new request.
* `nzo_id` stays stable across queue, verify, repair, completion, and history.
* Completed history is withheld until import path and mount readiness gates pass.

Integration:

* Simulated Radarr/Sonarr queue polling correlates NZBDav `nzo_id` to ARR media.
* Custom-script Grab and Import events update correlation idempotently.
* Queue selection order changes only after fresh hints exist.
* Sonarr missing-search nudge prioritizes recently aired and season-completing
  monitored episodes before older unrelated missing episodes.
* Radarr missing-search nudge prioritizes collection-completing monitored
  movies before unrelated missing movies.
* `RefreshMonitoredDownloads` is debounced after important NZBDav state
  transitions.
* Existing SAB queue/history response shapes remain compatible.
