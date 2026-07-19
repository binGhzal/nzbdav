# Grab-to-Plex benchmark harness

`scripts/nzbdav_grab_to_plex_benchmark.py` measures one controlled NZB from the
start of an NZBDav SAB `addfile` request through ARR import and exact Plex
metadata visibility. It supports Sonarr and Radarr. It records UTC and monotonic
timestamps and writes a private (`0600`) JSON artifact.

This is an evidence harness, not a promise that any deployment will complete in
five seconds. Cold metadata-provider calls, storage, mounts, Usenet providers,
ARR parsing, Plex scanning, and host load remain external variables.

## Safety boundaries

Always run `validate` first. Validation reads the NZB, probes NZBDav/ARR/Plex,
authenticates to NZBDav, verifies the Sonarr/Radarr application kind and Plex
movie/show section type, and checks that every expected Plex `Part.file` is
absent. It does not submit the NZB, create an ARR command, or request a Plex
scan.

The NZBDav probe uses SAB `fullstatus`, not the shallow `status` response. It
refuses a measurement when the queue is paused; queue/worker work already
exists; rclone's required visibility fence is impossible; rclone invalidations
or a whole-cache visibility proof are pending or failed; ARR import commands are
active, retrying, unroutable, or quarantined; no ARR instance is configured; or
ARR validation reports an error.
It also makes a bounded ARR queue request (`pageSize=1`) and requires ARR's own
queue to be empty.
External mount readiness and link traversal cannot be proved by this API, so
validation records that limitation as an explicit warning. The later exact
Plex `Part.file` match is still required.

A default live `run` performs exactly one mutation:

1. submits the supplied NZB to NZBDav's SAB `addfile` endpoint.

Production-representative mode does **not** command ARR from the benchmark
process. After NZBDav has durably published completion and cleared every
required visibility fence, NZBDav itself may issue a guarded
`DownloadedEpisodesScan`/`DownloadedMoviesScan` for an exact persisted
correlation and just-in-time queue match. It falls back to
`RefreshMonitoredDownloads` when that proof is missing, ambiguous, unsupported,
unhealthy, or the targeted request fails within its bounded deadline. The
benchmark observes ARR queue and history until the matching
`downloadFolderImported` event appears, so it measures the deployed production
path without pretending that acceptance of either ARR command proves import.

Production-representative mode also does **not** command Plex. It observes the
exact Plex `Part.file` and metadata fields, so the deployment must use its real
ARR Plex connection, AutoPulse hook, or equivalent import-complete scan path.
This prevents the benchmark from masking a missing production notification.

`--force-arr-refresh` is an explicit diagnostic mode. It additionally posts
ARR's `RefreshMonitoredDownloads` command after NZBDav reports completed
history, records its optional command id and status stages, and marks the JSON
artifact `production_representative: false`. That 2xx response or command id
proves only ARR command acceptance—not import, rename, metadata retrieval, Plex
scan, or Plex visibility. Aggregation excludes these forced runs from production
latency percentiles.

`--force-plex-scan` is the equivalent Plex diagnostic. It requests a targeted
scan for each expected parent path, records `plex_scan_accepted`, and marks the
artifact non-production/non-representative. It is useful for separating a
missing production scan trigger from Plex scanner or metadata latency.

It never removes queue/history rows, media, or library items and does not clean
up a failed test. Use a dedicated, recognizable test release and confirm the
normal ARR category, completed-download handling, remote-path mappings, mount,
and Plex library paths before running it.

By default the harness refuses to run if an exact expected `Part.file` already
exists in Plex. `--allow-existing-plex-item` is available for diagnostics, but
marks the artifact invalid/non-isolated, and aggregation excludes it.

## Configuration

Secrets accept exactly one source each: a direct option, an option pointing to a
file, an environment value, or an environment `_FILE` pointer. Secret files and
environment variables are preferable because direct options may be visible in
the process list. Secret values are sent only in headers and are never written
to artifacts or normal output.

Example environment:

```sh
export NZBDAV_E2E_NZBDAV_URL=http://nzbdav:3000/protocol
export NZBDAV_E2E_NZBDAV_API_KEY_FILE=/run/secrets/nzbdav_api_key
export NZBDAV_E2E_NZB_PATH=/bench/input/example.nzb
export NZBDAV_E2E_CATEGORY=movies
export NZBDAV_E2E_ARR_KIND=radarr
export NZBDAV_E2E_ARR_URL=http://radarr:7878
export NZBDAV_E2E_ARR_API_KEY_FILE=/run/secrets/radarr_api_key
export NZBDAV_E2E_PLEX_URL=http://plex:32400
export NZBDAV_E2E_PLEX_TOKEN_FILE=/run/secrets/plex_token
export NZBDAV_E2E_PLEX_SECTION_ID=1
export NZBDAV_E2E_PLEX_FINAL_FILES='["/movies/Example (2026)/Example (2026).mkv"]'
```

`NZBDAV_E2E_NZBDAV_URL` is the canonical NzbDAV protocol base, not the UI
origin. It must end in the exact `protocol` segment. A deployment with
`URL_BASE=/nzbdav` therefore uses
`https://example.com/nzbdav/protocol`. The harness normalizes one trailing
slash and rejects ambiguous or escaping bases before reading secret files,
the NZB, output paths, or the network.

The expected file is the exact string Plex will expose as `Media[].Part[].file`,
not the ARR container path unless both applications see the same path. Repeat
`--plex-final-file` (or use a JSON array in
`NZBDAV_E2E_PLEX_FINAL_FILES`) for multi-file imports; visibility and metadata
stages complete only when every expected file passes.

Validate, then run:

```sh
python3 scripts/nzbdav_grab_to_plex_benchmark.py validate
python3 scripts/nzbdav_grab_to_plex_benchmark.py run
```

`run --dry-run` is equivalent to the read-only validation path.

Use forced refresh only to diagnose ARR polling delay, not for production SLO
evidence:

```sh
python3 scripts/nzbdav_grab_to_plex_benchmark.py run --force-arr-refresh
```

Likewise, use a forced Plex scan only to diagnose the production notification
path:

```sh
python3 scripts/nzbdav_grab_to_plex_benchmark.py run --force-plex-scan
```

The default basic Plex predicate is:

- Radarr: `ratingKey`, `title`, `year`
- Sonarr: `ratingKey`, `title`, `grandparentTitle`, `parentIndex`, `index`

Override it by repeating `--plex-basic-field`. Dot paths traverse nested objects
and arrays. Rich metadata is separate and optional; repeat
`--plex-rich-field summary --plex-rich-field thumb` and set
`--rich-timeout-seconds` to observe it. Add `--require-rich-metadata` only when a
missing rich field should fail the run.

## Recorded boundaries

Artifacts keep separate stages for:

- NZBDav addfile request start and acceptance;
- first NZBDav queue observation and completed history;
- ARR queue observation and import history;
- optional ARR command acceptance/completion only in `--force-arr-refresh`
  diagnostic mode;
- optional Plex targeted-scan acceptance only in `--force-plex-scan`
  diagnostic mode;
- exact Plex item visibility;
- basic metadata readiness;
- optional rich metadata readiness.

Times are client-observation timestamps. For example, `arr_history_imported`
means the first poll that observed ARR's `downloadFolderImported` record, not the
ARR database event's own timestamp. The measurement begins immediately before
building and sending the SAB multipart request; indexer search and ARR's original
grab decision are outside this harness.

Schema-v2 run artifacts report two distinct end-to-end origins:
`sab_request_start_to_*` begins immediately before the HTTP request is built and
sent, while `sab_accepted_to_*` begins only after NZBDav has accepted and
answered that SAB request. The latter excludes request construction, upload,
and acceptance time. The legacy `submission_to_*` values remain aliases of the
request-start origin only; they are never aliases of SAB acceptance. The
default observer is deliberately deterministic and polls the
NZBDav, ARR, and Plex phases serially. Intermediate transition timestamps are
therefore upper bounds: a transition may have happened before the next phase
was first polled.

An ARR `downloadFolderImported` history record proves only that ARR reported an
import. This harness does not query NZBDav's durable import receipt and never
claims that receipt reached `Imported`; artifacts state
`nzbdav_receipt_measurement: not_measured` explicitly.

The one-time preflight absence check may page the Plex section to rule out an
existing exact `Part.file`. Timed observation does not repeat that full-library
scan: it queries at most three pages per poll, ordered by `addedAt:desc`, and
records the strategy, page/item limit, request count, pages read, and window
exhaustions under `observations.plex.observer`. With the default page size this
is a 300-item recent window per poll, so observer traffic stays bounded on large
libraries.

## Aggregate runs

Aggregate JSON files or directories. Only completed, valid,
production-representative measurements are included:

```sh
python3 scripts/nzbdav_grab_to_plex_benchmark.py aggregate \
  artifacts/grab-to-plex \
  --output artifacts/grab-to-plex-summary.json
```

Every available duration receives a count, minimum, maximum, p50, p95, and p99.
Percentiles are descriptive; this command intentionally has no five-second
pass/fail gate. Define the workload and required percentile before using the
results as an SLO decision.

Aggregation is fail-closed. A run is included only when its schema version and
kind exactly match the current harness and `measurement.valid`,
`measurement.isolated`, and `measurement.production_representative` are all
explicitly `true`. The artifact must also be a non-dry run with a non-empty
`nzo_id`; record exact observe-mode ARR/Plex inputs and observations; prove
NZBDav completed history, ARR `downloadFolderImported`, exact Plex `Part.file`
visibility, and basic metadata readiness; and contain ordered start, acceptance,
completion, import, visibility, metadata, and benchmark-completed stages. A
forced ARR refresh or Plex scan cannot be relabeled production-representative.
Legacy artifacts or artifacts with missing or contradictory provenance are
excluded rather than treated as valid. Python booleans are not accepted as
schema numbers. `durations_ms` must be a non-empty object, both request-start
and SAB-accepted basic-metadata metrics must be finite nonnegative numbers, and
the required monotonic stages must be finite, ordered, and consistent with
those durations. NaN, infinity, negative, boolean, missing, empty, or misordered
measurements are excluded under a fixed bounded set of reason keys. The
aggregate records a count for each reason under `exclusion_reasons`.

The two origins answer different questions and must remain separate in any
report or SLO definition. Aggregation reports evidence for both; it does not
combine them, choose a five-second target, or claim that an unmeasured live
deployment meets one.

## Known limitations

- A direct SAB addfile is a controlled pipeline test, not a measurement of
  indexer lookup or ARR's grab decision.
- ARR must already recognize the release and have completed-download handling,
  category mapping, and path mappings configured correctly.
- Default production mode intentionally waits for ARR's authoritative import
  history even when NZBDav's guarded targeted scan removes a polling delay.
  Forced refresh changes that path and is diagnostic only.
- Default production mode expects the deployment's actual ARR/AutoPulse Plex
  notification path. Forced Plex scan changes that path and is diagnostic only.
- Exact final Plex paths must be known before submission. This avoids fuzzy title
  matches and false positives but requires deterministic ARR naming/path mapping.
- Plex scan acceptance is asynchronous and is not treated as item visibility.
- Timed Plex observation searches only the recorded bounded, recently-added
  window. If the expected item does not enter that window, the run times out
  instead of crawling the complete library on every poll.
- A cold first-time metadata match can depend on remote Plex metadata services;
  rich-metadata timing is therefore reported separately.
- Poll intervals and HTTP round trips add observation delay. Use the same values
  and environment when comparing runs.
- Serial phase polling makes intermediate stage timestamps upper bounds. Report
  request-start and SAB-accepted end-to-end metrics separately, state which
  origin an SLO uses, and do not interpret deltas between adjacent intermediate
  stages as exact service time.
- ARR history observation is not an NZBDav durable-receipt measurement. Receipt
  `Imported` requires separate evidence from the receipt/event path.
