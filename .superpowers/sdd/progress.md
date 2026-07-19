# Pinrail V1 Subagent Development Progress

Canonical continuation authority remains `HANDOFF.md`, the active V1 plan, and
the governing V1 design. This ledger is supplemental and must never be the only
record of a continuation decision.

## Active base

- Branch: `pinrail/v1-backend-wip`
- Current Task 2 slice base: `173b743c288f69f9f129e66a09dd4a6caed37023`.
- The base matched `origin/pinrail/v1-backend-wip` with a clean worktree before
  the Task 2 proxy/symlink-only RED-GREEN slice began.
- Signed carrier implementation commit:
  `5e5c94a21ad26e432fa10160bf955ee2756d76b6`.
- Signed carrier review-fix commit:
  `c550bc61a7d16df17278ec755fc2516015d95b1e`.
- Task 2B carrier-contract parser: implementation, review fixes, independent
  re-review, push, and exact remote CI are complete. Task 2B production proxy/
  symlink-only plus Task 2C-2E and container-identity hardening are locally
  green. The final AST graph and independent combined-diff security review are
  green; formal security, signed checkpoint, and exact remote CI remain.

## Task 2B carrier-contract freeze

- [x] Read-only local route, parser, proxy, WebSocket, and caller inventory.
- [x] Pinned AIOStreams stable and main source research.
- [x] Pinned Sonarr, Radarr, Lidarr, SABnzbd, and rclone source research.
- [x] Controller synthesis: preserve one intentional AIOStreams-compatible
      identical header-plus-query pair; reject every other multi-carrier case.
- [x] Witness focused RED before production-code changes.
- [x] Implement minimal parser GREEN and update canonical plan/design/handoff.
- [x] Run focused, affected, build, and scoped format gates.
- [x] Run final documentation and whitespace gates after the last ledger edit.
- [x] Initial independent specification, quality, and bounded-security reviews;
      no P0/P1/P2 or functional parser defect found.
- [x] Resolve the first review's documentation and characterization findings
      without changing production code.
- [x] Run the review-fix focused, affected, scoped-format, documentation, and
      whitespace gates.
- [x] Independently re-review the signed review-fix commit before push.
- [x] Push safe WIP and verify exact remote CI.

## Local execution evidence (2026-07-18)

- The first suggested focused invocation before restore exited `0` without
  output or test execution because restored `obj` metadata and Release
  artifacts were absent. It is recorded as inconclusive, not RED.
- `dotnet restore backend.Tests/backend.Tests.csproj --nologo` passed.
- Tests-only RED then ran conclusively: `3` failed, `26` passed, `0` skipped.
  The failures were exactly the equal header-plus-form, query-plus-form, and
  header-plus-query-plus-form cases accepted by unchanged production code.
- Minimal GREEN passed the focused class `29/29` with no skips.
- The parser/SAB/ARR/add-file affected filter passed `91/91` with no skips.
- Production and test Release no-incremental builds with `-warnaserror` each
  passed with zero warnings and zero errors.
- Scoped formatter verification for the two edited C# files exited `0` with no
  output or changes.
- The first documentation validator was blocked because Ruby is unavailable;
  its shell invocation continued, so it is not accepted as gate evidence.
- The fail-fast Python relative-link, handoff-schema, carrier/CI consistency,
  stale-text, trailing-whitespace, and `git diff --check` gate exited `0` with
  no output. It was rerun after the final ledger edit.
- No complete backend regression or production proxy matrix was run or claimed
  by this narrow slice.

## Review-fix evidence (2026-07-18)

- The first independent specification, quality, and bounded-security reviews
  found no P0/P1/P2 and no functional parser defect. They identified stale
  source links, handoff/ledger state, continuation wording, and missing boundary
  characterization.
- Characterization added explicit missing-carrier, maximum 512-character
  header/query/form, and mixed-case HTTP header-name coverage. Existing
  production code already passed, so no RED or production-code change is
  claimed.
- The focused parser class passed `34/34`; the same
  parser/SAB/ARR/add-file affected filter passed `96/96`; both had zero skips.
- Tests compiled the affected production/test graph successfully. The prior
  warning-as-error Release builds remain recorded above and were not rerun
  because the review fix required no production compilation change.
- Scoped test-file formatting, fail-fast documentation consistency/link/
  stale-local-path validation, and `git diff --check` passed after the final
  review-fix ledger edit.
- Independent review-fix re-review accepted
  `c550bc61a7d16df17278ec755fc2516015d95b1e` with no finding.
- The signed commits were pushed without force. Exact-HEAD GitHub Actions run
  `29661598011` completed successfully: full verifier plus native Transfer
  glibc/musl x64/arm64, with zero failed jobs or steps.
- The carrier sub-slice is sealed. No production proxy implementation or final
  release-candidate gate is claimed.

## Gitleaks fixture cleanup (2026-07-18)

- [x] Reproduce exactly eight redacted `generic-api-key` findings in
      deterministic test-only fixtures.
- [x] Replace current fixture literals with behavior-preserving constructors or
      generated values; preserve required lengths, shapes, distinctness, and
      redaction-canary semantics.
- [x] Add exactly eight immutable-history fingerprint exceptions for the single
      introduction commit, with no rule/path/regex/global suppression.
- [x] Prove current-tree and full-history scanner counts are zero.
- [x] Run focused fixture, frontend auth/full unit/type/build, production/test
      Release warning-as-error build, scoped format, and shell gates.
- [x] Complete independent bounded review with no P0/P1 security, semantic,
      runtime-reachability, or production-impact finding.
- [x] Commit with sign-off, push without force, and verify exact-HEAD CI at
      `c925ebc84dc8e8bdd3e10fb7f35ee3ee249bc622`, run `29663552874`, with five
      successful jobs and zero failed steps.

The full local Playwright suite passed `4/5` when cold startup exhausted the
first test's 30-second budget. The isolated health test passed `3/3`, including
the cold repetition, with successful responses and the expected final DOM. No
fixture/product regression or timing change is claimed. Local container build
is environment-blocked because buildx is unavailable; exact remote CI owns the
new container lifecycle result.

## Urgent SearchNudge incident gate (2026-07-18)

- [x] Trace the defect read-only: `RunOnceAsync` loads current normalized
      options but drains persisted pending apply rows without checking current
      mode before report planning.
- [x] Add conclusive RED for enabled current report mode with due pre-existing
      Sonarr/Radarr apply rows: zero command POSTs and rows remain unexecuted.
- [x] Gate only pending apply processing on current apply mode; preserve report
      planning and disabled/report deployment defaults.
- [x] Run focused `1/1`, affected `28/28`, complete local Release `2,868`
      passed/85 deliberate PostgreSQL-only skips, warning-as-error builds,
      scoped format, whitespace, gitleaks, and independent review with no
      P0-P3 finding.
- [x] Commit with sign-off, push without force, and verify exact-HEAD CI at
      `88e05f87e37147bf60b7fad8b0914df43e219eab`, run `29664771054`, with five
      successful jobs and zero failed steps.
- [x] Record the verified incident checkpoint. External signed parent
      `0e9e3583e6b26fedb26c222f06519a02853bc902` added Graphify configuration
      and passed exact run `29663671084`. This historical stop instruction was
      superseded by explicit continuation into the current Task 2 checkpoint.

## Task 2C-2E combined checkpoint and container hardening (2026-07-19)

Resume order is fixed: 2A authentication/session; 2B exact proxy plus
symlink-only runtime; 2C public failure/log/persistence sanitization; 2D
internal-key and normal-startup session/auth preflight; 2E canonical protocol
client/operator contract; then 2F clean-room reference archaeology.

### Task 2B exact proxy and symlink-only runtime

- [x] Regenerate the Task 2B boundary brief and independent specification and
      security reviews against base `173b743c...`; freeze exact UI, SAB/ARR,
      WebDAV, signed-media, health, and pre-101 WebSocket policy.
- [x] Remove reachable STRM generation, conversion, maintenance, configuration,
      migration seed, and UI surfaces. Preserve hard-symlink import behavior,
      canonical `/.ids` targets, cleanup, DFS, ARR, and rclone contracts.
- [x] Wire the raw-target classifier into the production frontend, require the
      selected principal for UI/admin relays, preserve independent protocol
      credentials, strip browser/private-hop authority, and inject the internal
      key only on approved UI-admin calls.
- [x] Bind the backend explicitly to loopback, reject configured Kestrel
      endpoints, expose only frontend port 3000, and prove hostile
      `DOTNET_URLS` cannot create a second listener.
- [x] Bridge mounted WebDAV paths through a bounded internal PathBase header,
      validate it before NWebDav, and strip it before logical store lookup.
      Disposable ASP.NET passed `34/34`; pinned rclone passed `7/7`, including
      listing, range reads, upload/removal, content deletion, and completed
      symlink acknowledgement.
- [x] Fail closed on wildcard runtime `DEBUG`, eject unsafe default proxy
      plugins, return bounded proxy errors, remove private redirect/cookie
      response headers, and reject ambiguous, overlong, dynamic, or
      case-confused `URL_BASE` mounts before binding.
- [x] Migrate UI mutations to the reviewed POST contract and seal public
      response/log/persistence projections against raw exception and secret
      material in the affected backend/frontend surfaces.
- [x] Independent proxy re-review closed one wildcard-debug P1 and two
      consolidated literal-mount/configuration P2 findings; final counts are
      P0/P1/P2/P3 `0/0/0/0`. SearchNudge quarantine hashes remained exact.

### Task 2C public failure, log, and persistence contract

- [x] Converge reachable backend/frontend failures on bounded fixed public
      codes/messages and server-generated correlation IDs; sanitize public
      persistence/projections and fixed final process/log boundaries.
- [x] Focused and affected regressions, Release builds, scoped formatting,
      frontend type/build, shell, and whitespace gates passed as recorded in
      `.superpowers/sdd/task-2c-public-failure-contract-report.md`.
- [x] Independent final review P0/P1/P2/P3 `0/0/0/0`. Retained terminology:
      `Repaired` means the ARR command was accepted. Retained future hazard:
      maintenance `ExecuteUpdate` string extensions require renewed review if
      they ever admit external text.

### Task 2D internal-key and startup-auth preflight

- [x] Generate a fresh lowercase 64-hex internal key only when omitted/empty;
      preserve valid explicit mixed-case material; fail invalid input before
      identity-system/filesystem/database/child side effects without echo.
- [x] Preflight exact `AUTH_MODE` and local `SESSION_KEY` only on normal
      startup. Maintenance is session-key independent; invalid argv retains
      first precedence; Authentik proxy startup remains session-key independent.
- [x] Initial preflight review P0/P1/P2/P3 `0/0/3/0` closed non-local bypass,
      masked ordering, and mixed-case preservation/export proof gaps. Final
      review plus shell portability cleanup is `0/0/0/0`.
- [x] Final boundary hashes: `entrypoint.sh`
      `8cba409aa4f11f779c37961931180f87e327260167ee4c2c8fd8d1e85cc1e712`,
      shell contract
      `bb5d34591bdb05b7504be1413899c0f1a4b408e78448efa1ff6d2d84a173721f`,
      container contract
      `f7b64ba193117a163c93c70ba1a658dedc97c2bfed645956598620c50008d943`,
      bootstrap
      `a84748e46103747b85da0991e5afcfd21952ad1ec532b722f2fb6ca300c848fc`.

### Task 2E protocol client and operator contract

- [x] Freeze the external base as `<origin><URL_BASE>/protocol`; full-root
      rclone mounts expose `/.ids`, `/completed-symlinks`, `/content`, and
      `/nzbs`; `<origin><URL_BASE>/view` remains frontend-principal-only.
- [x] Final focused counts: helper `4/4`, ARR report `12/12`, benchmark `41/41`,
      grab-to-Plex `42/42`, operator contract `17/17`, request policy `158/158`,
      and complete Python `155/155`.
- [x] Initial code review `0/0/3/1`, first repair GREEN helper `4/4`, ARR
      `12/12`, benchmark `39/39`, request policy `156/156`, Python `141/141`;
      second review `0/0/2/0`; final code review `0/0/0/0`.
- [x] Initial operator review `0/3/3/0` closed the wrong rclone subtree,
      incomplete ARR/Lidarr guidance, first-run auth omission, weak static
      bindings, benchmark-profile conflict, and public PostgreSQL internals.
      Final operator review is `0/0/0/0`.

### Combined local checkpoint gates

- [x] NuGet audited restore plus direct/transitive vulnerable-package listings
      for `backend/NzbWebDAV.csproj` and `backend.Tests/backend.Tests.csproj`:
      no vulnerable package reported.
- [x] Complete hermetic backend Release: `2,986` passed, `85` deliberate
      PostgreSQL-only skips, `0` failed (`3,071` total). The strict TRX
      validator expectedly returns nonzero only because skips are present;
      exact remote CI owns the required PostgreSQL matrix.
- [x] Production and test no-incremental Release builds with `-warnaserror`:
      `0` warnings, `0` errors.
- [x] Scoped formatter found and fixed whitespace in two test files; affected
      regression `62/62` passed. Final production/test formatter verification
      covered all 106 added/modified C# files and passed without edits. A whole-
      project verify remains nonzero only on pre-existing unchanged baseline
      formatting outside this checkpoint; no whole-project pass is claimed.
- [x] `npm ci` audited `374` packages with `0` vulnerabilities and the fresh
      `npm audit` result is also `0`; typecheck, `50` files/`928` frontend
      tests, client/server builds, packaged smoke, and Playwright `5/5` passed.
- [x] Python compile/unit gate: `157/157` after the additive container-release
      contracts; Task 2E's focused complete gate remains `155/155`.
- [x] Entrypoint contract, `sh -n`, severity-error ShellCheck, actionlint,
      PostgreSQL-runtime refusal, and `git diff --check` passed.
- [x] Gitleaks current tree scanned about `18.86 MB` with `0` findings and full
      history scanned `639` commits/about `11.90 MB` with `0` findings. The only
      current-scan exclusion was an exact ephemeral ignored-generated-path
      exclusion for `graphify-out/`; no global/rule/source suppression exists.
- [x] Close Trivy/container identity. Dependency vulnerabilities and Dockerfile
      misconfigurations are both `0`. Zero/padded-zero IDs fail; valid IDs are
      normalized; `su-exec` uses resolved user+group; root PID 1 supervises
      non-root children; legacy standalone backend/frontend image paths are
      retired. The exact root-Dockerfile DS-0002 exception covers only dynamic
      identity/ownership/supervision, not privileged DFS; V1 uses the rclone
      sidecar.
- [x] Initial implementation review `0/0/1/0` found client-host `/proc` plus
      real-ID-only inspection. Tests-only RED required container-namespace
      inspection of all real/effective/saved/filesystem UID/GID columns; repair
      GREEN and re-review are `0/0/0/0`. Final packaged runtime `6/6`, shell
      contract, typecheck, `sh`/Dash/BusyBox syntax, full ShellCheck, actionlint,
      Trivy, and diff checks passed.
- [x] Run Graphify 0.9.19 AST-only after documentation stabilizes: `15,770`
      nodes, `41,044` edges, `728` communities; update the external `pinrail`
      entry and pass query/path/explain canaries. Generated output is ignored.
- [ ] Run the formal Codex Security diff scan against the final combined diff.
      The native workspace opened, but its setup wait expired before Start Scan
      was submitted, so no scan started. Independent read-only combined-diff
      review is `0/0/0/0` and does not replace the formal gate.
- [ ] Create one lowercase imperative signed-off checkpoint, push without
      force, and verify its exact remote CI/container lifecycle/native matrix.
      Historical container-smoke runs are not current-checkpoint evidence.

Local Docker build/container smoke remains environment-blocked because this
host has no buildx component. Exact remote CI owns that current gate.

## Task 2F reference archaeology gate — only after signed push and exact CI

- [ ] Clone `qooode/nzbdavex`, `Gaisberg/streamnzb`, and `javi11/altmount`
      read-only beneath an ephemeral path outside `/opt/pinrail`; never create a
      nested repository.
- [ ] Audit complete source, relevant history/tags/releases, docs, tests, CI,
      containers, dependencies, and exact licenses. Treat streamnzb's reported
      GPL-3.0 provenance as an explicit compatibility gate; copy no code or
      asset without a documented compatible license and provenance chain.
- [ ] Build AST-only Graphify graphs for all three reference clones and use
      query/path/explain to trace ingestion, streaming, cache, recovery,
      protocol, security, observability, and operator flows.
- [ ] Produce a source-cited comparison matrix classifying every candidate as
      implemented, partial, missing in approved V1, intentionally post-V1, or
      unsuitable/unsafe; rank actionable gaps P0/P1/P2.
- [ ] Implement only verified, license-safe V1 gaps through Pinrail's existing
      abstractions with focused RED, minimum GREEN, regression gates,
      independent review, documentation, signed push, and exact remote CI.
