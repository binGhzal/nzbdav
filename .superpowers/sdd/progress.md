# Pinrail V1 Subagent Development Progress

Canonical continuation authority remains `HANDOFF.md`, the active V1 plan, and
the governing V1 design. This ledger is supplemental and must never be the only
record of a continuation decision.

## Active base

- Branch: `pinrail/v1-backend-wip`
- Slice base: `df41e0c15504ad87fb2aaa211c59700a26917b7c`
- Base matched `origin/pinrail/v1-backend-wip` with a clean worktree.
- Task 2B carrier-contract freeze: implementation and local verification
  complete; independent controller review and push remain pending.

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
- [ ] Independent specification, quality, and bounded security review.
- [ ] Resolve review findings, push safe WIP, and verify exact CI.

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
