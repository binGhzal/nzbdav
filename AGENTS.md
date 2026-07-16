# Repository Agent Context

## Active work, resume contract

- Canonical handoff: `HANDOFF.md`
- Active implementation plan: `docs/superpowers/plans/2026-07-14-nzbdav-transfer-v3-phase-4.md`
- Governing design: `docs/superpowers/specs/2026-07-14-nzbdav-transfer-v3-phase-4-design.md`
- Resolve the immutable initial handoff publication commit with
  `git log --diff-filter=A -1 --format=%H -- AGENTS.md`.
- Before editing code, verify that initial publication commit is an ancestor of
  `HEAD`, then compare branch and worktree status with the handoff snapshot.
- Continue at the handoff's first exact next action and the plan's first unfinished task.
- Update the handoff and plan before ending every working session.
- Canonical documentation is Git-durable. Current Phase 4 implementation is
  preserved-worktree-only until a separate implementation commit is authorized.

## Stable repository constraints

- Preserve the dirty shared worktree. Do not reset, clean, restore, checkout,
  stash, stage, commit, push, switch branches, or delete unexplained files
  without explicit authorization.
- SQLite and the one-control-owner topology remain production defaults.
  PostgreSQL is disabled and private until the active plan's completion gates
  pass.
- Never use a real database, blob tree, service, container, production host, or
  user configuration for tests. Use only uniquely owned disposable fixtures
  explicitly authorized by the active plan.
- Follow red-green TDD for implementation. Run the narrow gate first, then the
  affected regressions, Release builds with `-warnaserror`, formatting, and
  whitespace checks. Never claim a gate passed unless it ran in the current
  worktree.
- Keep secrets, credentials, connection values, local paths, database files,
  caches, and generated artifacts out of documentation and Git.
- Do not expose Phase 4 through `backend/Program.cs`, `entrypoint.sh`, provider
  selection, Compose, controllers, or runtime service registration.
