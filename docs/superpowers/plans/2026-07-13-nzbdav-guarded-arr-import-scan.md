# NZBDav guarded ARR completed-download scan

Date: 2026-07-13

Status: approved design; implementation pending.

## Objective

Reduce the interval between durable NZBDav completion and Sonarr/Radarr import
without weakening the existing visibility, invalidation, lease, quarantine, or
retry guarantees. A targeted completed-download scan is an optimization. The
existing `RefreshMonitoredDownloads` command remains the safe fallback and the
only behavior for uncorrelated, ambiguous, unsupported, or unhealthy cases.

This change can remove an internal polling delay. It does not, by itself, prove
the end-to-end grab-to-Plex `<5 seconds` SLO; that remains a production-like
benchmark result involving ARR, the selected mount, library storage, Plex, and
metadata agents.

The repository is already heavily dirty. Before changing any listed file,
capture its current diff and treat that filesystem version as the baseline.
Use only narrow patches, preserve unrelated edits, and do not reset, checkout,
run broad formatters, stage, or commit files as part of this plan.

## Researched upstream contract

Current Sonarr and Radarr source establish the following contract:

- `DownloadedEpisodesScan` and `DownloadedMoviesScan` accept a `Path`, a
  **string** `DownloadClientId`, and an `ImportMode`;
- when the download ID is known, the command associates the scan with the
  tracked download and verifies the import through normal completed-download
  handling;
- the scan handler checks and processes `Path` directly; it does not apply a
  remote-path mapping to a caller-supplied path;
- the queue `OutputPath` is the tracked download item's path, and the ARR SAB
  client applies its own remote-path mapping before exposing a completed
  history item's output path;
- `ImportMode.Auto` preserves normal ARR behavior: a movable SAB item is moved,
  which is NZBDav's intended lightweight symlink copy-plus-unlink flow;
- ARR itself coalesces an identical completed-scan command while that command is
  queued or running. A lost HTTP acknowledgement after later completion is
  still an at-least-once ambiguity and must remain harmless.

Primary references:

- `https://github.com/Sonarr/Sonarr/blob/develop/src/NzbDrone.Core/MediaFiles/Commands/DownloadedEpisodesScanCommand.cs`
- `https://github.com/Sonarr/Sonarr/blob/develop/src/NzbDrone.Core/MediaFiles/DownloadedEpisodesCommandService.cs`
- `https://github.com/Radarr/Radarr/blob/develop/src/NzbDrone.Core/MediaFiles/Commands/DownloadedMoviesScanCommand.cs`
- `https://github.com/Radarr/Radarr/blob/develop/src/NzbDrone.Core/MediaFiles/DownloadedMovieCommandService.cs`

## Non-negotiable safety boundary

The network command remains in `ArrImportCommandService`. It must not be issued
from queue processing, history publication, health verification, priority, or
search-nudge code.

Before any ARR import command is attempted:

1. Queue completion, history, lifecycle state, receipts, invalidation rows, and
   the import-command outbox are already durable.
2. Required rclone invalidation and any whole-cache visibility fence are clear.
3. The outbox row has a durable non-null `VisibleAt`.
4. The current worker still owns the exact `Executing` lease token and the row
   has not been quarantined.
5. SAB history visibility has been published best-effort from the already
   durable row.

Reauthorize items 3 and 4 immediately before **every** network attempt: the
typed queue probe, the targeted POST, a refresh-only POST, and a refresh fallback
after a targeted failure. Do this without holding a database transaction or lock
across HTTP. The post-HTTP lease predicate remains the authority for accepting
the result. A concurrent quarantine may make a network call unavoidable after
the last read, but its cleared token must prevent that stale call from changing
durable state. If reauthorization fails, make no further ARR call.

Direct-scan failure must affect only the import-command outbox and diagnostics.
It must not fail history, change receipts, block or complete verification, or
undo queue completion.

## Exact direct-scan eligibility

For each resolved target instance, direct scan is permitted only when all of the
following are true:

1. The target is exactly `SonarrClient` or `RadarrClient`; Lidarr and unknown ARR
   clients always use refresh.
2. Routing came from persisted correlations for this history row, not merely a
   category-ownership guess.
3. All correlations for the target yield exactly one distinct, nonempty
   `DownloadId` using ordinal comparison.
4. A just-in-time read through the existing typed `SonarrQueue` or `RadarrQueue`
   endpoint returns a complete result: `TotalRecords == Records.Count`. If ARR
   reports more records than the bounded response contains, or reports otherwise
   inconsistent pagination, direct scan is refused rather than assuming a hidden
   duplicate does not exist.
5. Exactly one returned record has protocol `usenet` and a `DownloadId` that
   matches ordinally. A non-Usenet record is rejected even though the request
   asks ARR to filter by protocol.
6. The record has a nonblank `OutputPath`.
7. If a correlation contains typed media identity, every available value agrees:
   Radarr `MovieId`; Sonarr `SeriesId`, and `EpisodeId` when both sides have one.
8. The path is passed back unchanged to the same ARR instance. NZBDav never
   derives it from mount configuration, history, or invalidation paths and never
   applies another mapping.

The command bodies are exact:

```json
{"name":"DownloadedEpisodesScan","path":"<ARR OutputPath>","downloadClientId":"<DownloadId>","importMode":0}
{"name":"DownloadedMoviesScan","path":"<ARR OutputPath>","downloadClientId":"<DownloadId>","importMode":0}
```

`importMode: 0` is ARR's `Auto` enum value. The numeric queue-record
`DownloadClientId`, when present in a client response, is never used here.

Any missing correlation, missing or duplicate queue match, blank output path,
identity conflict, unsupported app, malformed response, or queue-probe failure
uses `RefreshMonitoredDownloads`. A failed targeted POST also attempts the safe
refresh fallback when time remains. If neither command is accepted within the
bounded request budget, the existing durable retry path owns the failure.

## Result and diagnostic contract

Keep the existing per-instance accepted-result deduplication. Extend the
serialized result with backward-compatible optional properties:

- stable dispatch mode: `direct-scan`, `refresh-fallback`, or `refresh-only`;
- stable fallback reason code, never a raw path or download ID;
- accepted command name;
- publication-to-accept latency in bounded milliseconds.

Old four-property result JSON must still parse. Durable errors, metric labels,
and logs must not
contain ARR paths, download IDs, API keys, titles, or host credentials. Store
only stable reason codes and the already-hashed instance key. Never persist or
log a raw ARR exception message; classify it to a bounded code such as
`queue-timeout`, `direct-http`, `refresh-http`, `invalid-command`, or
`cancelled`.

## Task 1: freeze client request shape with RED tests

Modify:

- `backend.Tests/Clients/RadarrSonarr/ArrClientTests.cs`

Modify production only after observing the focused tests fail:

- `backend/Clients/RadarrSonarr/ArrClient.cs`
- `backend/Clients/RadarrSonarr/SonarrClient.cs`
- `backend/Clients/RadarrSonarr/RadarrClient.cs`

Tests must capture real loopback HTTP requests and prove:

1. Sonarr uses `/api/v3/command` and the exact
   `DownloadedEpisodesScan` body above.
2. Radarr uses the exact `DownloadedMoviesScan` body.
3. `OutputPath` and string download ID survive byte-for-byte as JSON string
   values; no local path normalization occurs.
4. `importMode` is numeric zero.
5. cancellation reaches queue reads and both command methods.
6. no API key is placed in the URI or body.

Add virtual typed scan methods for testability and keep `CommandAsync` as the
single HTTP implementation. Make the existing `GetSonarrQueueAsync` and
`GetRadarrQueueAsync` methods virtual and use those typed responses; the generic
`GetQueueAsync` loses `SeriesId`, `EpisodeId`, and `MovieId` and is not eligible
for this policy. Add wire tests proving those typed IDs deserialize into the
policy input.

## Task 2: freeze pure guard selection with RED tests

Create:

- `backend/Services/ArrCompletedDownloadDispatchPolicy.cs`
- `backend.Tests/Services/ArrCompletedDownloadDispatchPolicyTests.cs`

The pure policy receives the target app, target correlation facts, and queue
records. It returns either a typed direct-scan request or one stable fallback
reason. Tests cover:

- exact Sonarr and Radarr matches;
- no correlation and category-only routing;
- empty, duplicate, case-changed, and conflicting download IDs;
- zero, duplicate, and blank-path queue matches;
- a non-Usenet match, truncated `TotalRecords > Records.Count`, and inconsistent
  pagination;
- matching and conflicting media IDs;
- Lidarr/unknown clients;
- unchanged Windows and POSIX paths;
- no raw identifier/path in diagnostics or `ToString()`.

Use ordinal download-ID equality. Do not guess case-insensitive identity.

## Task 3: integrate the guarded dispatcher with TDD

Modify tests first:

- `backend.Tests/Services/ArrImportCommandServiceTests.cs`

Then modify:

- `backend/Services/ArrImportCommandService.cs`

Required tests:

1. Exact correlated Sonarr/Radarr targets direct-scan only after `VisibleAt` is
   durable and history publication has been attempted.
2. Every policy refusal uses one refresh and never posts a targeted scan.
3. Queue-probe exceptions and malformed data use refresh.
4. A fast targeted-command failure uses refresh; if both fail the row retries.
5. One accepted target is not repeated while a sibling target retries.
6. Old result JSON remains readable and suppresses replay.
7. Cancellation requeues without leaving the long lease.
8. Total probe/dispatch time remains within the existing latency-bound test.
9. A quarantine committed before the final authorization read suppresses all ARR
   calls; a quarantine racing after the call fences the stale finalize.
10. A quarantine committed after a failed direct POST but before fallback
    authorization suppresses the refresh fallback.
11. Missing configured correlated instances still retry without broadcasting.
12. Category-only routing, Lidarr, and no-correlation ownership retain current
    refresh behavior.
13. Direct scan never waits for post-download verification.
14. Queue and HTTP exceptions containing a path, download ID, title, API key, or
    credential-bearing host do not reach `ResultsJson`, `LastError`, or captured
    logs.
15. A slow probe followed by a direct failure still leaves the reserved refresh
    slice and the entire attempt finishes inside one monotonic deadline.

Carry correlation facts into the resolved `DispatchTarget`; do not add a schema
column. One monotonic per-target deadline covers authorization, probe, targeted
POST, and fallback; no operation resets the deadline. For direct-eligible
targets, the probe and targeted POST share the first half and the final half is
reserved for refresh. A refresh-only target may use the whole budget. Derive
linked cancellation from the remaining monotonic time before each call and test
the exact boundary with the existing short-budget harness.

## Task 4: documentation and benchmark truth

Modify:

- `docs/grab-to-plex-benchmark.md`
- `docs/setup-guide.md`

Document that NZBDav may issue a guarded targeted scan after durable
publication, otherwise refreshes. Do not claim the ARR command ID proves import,
rename, metadata, Plex scan, or Plex visibility. The benchmark must continue to
observe the authoritative ARR/Plex milestones and report timeout/failure rather
than inferring success from a 2xx response.

## Verification gate

Run, in order:

1. focused ARR client tests;
2. pure policy tests;
3. import-command, visibility, quarantine, report, and PostgreSQL visibility
   tests;
4. backend build;
5. full backend suite;
6. benchmark Python tests;
7. `git diff --check`.

No live ARR, Plex, SQLite source, deployed service, or existing PostgreSQL
container is used by this implementation phase. No commit, push, or deployment
is authorized by this plan.
