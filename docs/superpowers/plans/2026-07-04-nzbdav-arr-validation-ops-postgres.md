# NZBDav ARR Validation, Operations, And PostgreSQL Migration Plan

## Summary

- Current `main` is clean, pushed, and `dotnet build backend/NzbWebDAV.csproj --no-restore` passes. Existing code already includes ARR operations APIs/UI, custom-script ingestion, hard duplicate reject mode, and JSON SQLite-to-PostgreSQL transfer.
- Next pass should harden and verify those surfaces, add missing integration coverage, add a production report-mode validation runner, and make PostgreSQL migration operationally safe.
- References checked for ARR custom-script payloads: [Sonarr](https://wiki.servarr.com/en/sonarr/custom-scripts), [Radarr](https://wiki.servarr.com/radarr/custom-scripts), [Lidarr](https://wiki.servarr.com/lidarr/custom-scripts), and [Lidarr API docs](https://lidarr.audio/docs/api/).

## Key Changes

- [ ] Save this plan to `docs/superpowers/plans/2026-07-04-nzbdav-arr-validation-ops-postgres.md`, create `codex/arr-validation-ops-postgres`, and keep `main` clean until all gates pass.
- [ ] Add a production report-mode validation runner that calls the deployed NZBDav APIs with `NZBDAV_BASE_URL` and `NZBDAV_API_KEY`, writes redacted artifacts under `artifacts/arr-validation/`, and fails unless Sonarr/Radarr are configured, search nudge mode is `report`, duplicate rejection is not enabled, validation has no errors, failed nudges are zero, and active queue correlation is at least 90% or explicitly explained.
- [ ] Harden ARR search command history: add filters for app/status/mode/command/search, a detail endpoint or row expansion, and make retry real by moving commands through `pending_apply -> executing -> executed|failed` with cooldown, rate-limit, and per-instance concurrency enforcement.
- [ ] Harden manual correlation correction: add explicit `source=auto|custom-script|manual` and `manual_lock` persistence, prevent automatic polling/custom-script updates from overwriting locked media/download IDs, and add UI edit/prefill for existing rows.
- [ ] Extend ARR custom-script ingestion to normalize official Sonarr/Radarr lowercase variables and Lidarr Title_Case variables, treat `Test` events as no-op success, persist lifecycle events for grab/import/delete/health, and document curl/script examples using `X-Api-Key`.
- [ ] Keep hard duplicate rejection opt-in and default-off, but add SAB addfile coverage proving `reject` returns a clean 400 and does not write a blob/queue row, while `increment` remains the default.
- [ ] Keep Lidarr apply/search deferred. Lidarr remains correlation/custom-script/report-only until command payloads are verified separately.
- [ ] Keep rclone as the production mount. Do not promote DFS; leave DFS prototype benchmark-gated and ensure examples/defaults stay `MOUNT_TYPE=rclone`.
- [ ] Keep CineSync/Riven/FileBot organizer features deferred as research/design only; no runtime organizer behavior in this pass.
- [ ] Build the SQLite-to-PostgreSQL migration path into a first-class offline workflow: a repo-local migration script/runbook that exports SQLite JSON, imports into PostgreSQL, copies `/config/blobs`, validates row counts, refuses dirty target DBs without `--replace`, writes a manifest, and never copies temporary cache files.

## Public Interfaces

- Additive ARR API behavior only:
  - `GET /api/arr/search-nudges` gains `app`, `status`, `mode`, `command`, `search`, and clamped `limit`.
  - `POST /api/arr/search-nudges/{id}/retry` must enqueue or execute a valid retry instead of leaving an inert planned row.
  - `GET/POST/DELETE /api/arr/correlations` expose `source` and `manual_lock`; secrets stay redacted.
  - `POST /api/arr/events/{sonarr|radarr|lidarr}` accepts form or JSON custom-script payloads and returns no-op success for ARR `Test` events.
- No new default settings. Existing `api.duplicate-nzb-behavior=increment|mark-failed|reject` remains the only duplicate behavior switch.
- Migration remains offline CLI/script based, not an HTTP API.

## Test Plan

- Backend tests: ARR validation thresholds, command history filtering, retry state machine, Sonarr/Radarr report/apply cooldowns, rate limits, command failures, active-media exclusions, manual-lock preservation, official custom-script variable parsing, `Test` event no-op, and SAB duplicate reject behavior.
- Database tests: JSON transfer round-trip plus PostgreSQL import smoke with ARR tables, repair tables, queue priority hints, and blob-copy manifest validation.
- Frontend tests: Health operations command filtering/details/retry/clear, manual correlation edit/delete, degraded banners, and settings default-off duplicate rejection.
- E2E/manual gates: `dotnet test`, backend build, frontend typecheck/test/build/server build, Playwright, Docker build, migration smoke, `git diff --check`, then production report-mode validation against real Sonarr/Radarr before enabling apply or hard reject.

## Assumptions

- Production validation is read-only/report-mode first; no ARR apply commands or hard duplicate rejection until the artifact passes.
- Sonarr and Radarr are the only apply-mode ARR targets in this pass.
- PostgreSQL migration requires NZBDav downtime and a backup; SQLite DB rows and `/config/blobs` move, cache files do not.
- Rclone remains the supported production filesystem path.
