# Pinrail V1 Subagent Development Progress

Canonical continuation authority remains `HANDOFF.md`, the active V1 plan, and
the governing V1 design. This ledger is supplemental and must never be the only
record of a continuation decision.

## Active base

- Branch: `pinrail/v1-backend-wip`
- Slice base: `df41e0c15504ad87fb2aaa211c59700a26917b7c`
- Base matched `origin/pinrail/v1-backend-wip` with a clean worktree.
- Signed carrier implementation commit:
  `5e5c94a21ad26e432fa10160bf955ee2756d76b6`.
- Task 2B carrier-contract parser: initial review and local review-fix
  verification are complete; review-fix re-review, push, and exact remote CI
  remain pending.

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
- [ ] Independently re-review the signed review-fix commit before push.
- [ ] Push safe WIP and verify exact remote CI.

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
- The complete combined backend regression remains pending and unsealed. Exact
  remote CI must follow an accepted review-fix re-review and authorized push.
