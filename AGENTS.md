# Repository Agent Context

## Active work, resume contract

- Canonical handoff: `HANDOFF.md`
- Active implementation plan: `docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md`
- Governing design: `docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md`
- Resolve the immutable initial handoff publication commit with
  `git log --diff-filter=A -1 --format=%H -- AGENTS.md`.
- Before editing code, verify that initial publication commit is an ancestor of
  `HEAD`, then compare branch and worktree status with the handoff snapshot.
- Continue at the handoff's first exact next action and the plan's first unfinished task.
- Update the handoff and plan before ending every working session.
- V1 is Docker-first, clean-install-only, SQLite-only, one control owner, and
  `role=all`. Full visual rebrand work is blocked until the backend freeze and
  release-candidate gates in the active plan pass.
- Transfer-v3 Phase 4 is preserved-worktree-only and deferred post-V1. Do not
  represent Task 8 as sealed: independent review found a pre-budget,
  unbounded PostgreSQL catalog-materialization path.

## Stable repository constraints

- Preserve the dirty shared worktree. Do not reset, clean, restore, checkout,
  stash, stage, commit, push, switch branches, or delete unexplained files
  without explicit authorization.
- SQLite and the one-control-owner `role=all` topology are the only supported V1
  production contract. PostgreSQL, split roles, upgrades from pre-V1 tags, and
  in-place downgrade are disabled, private, and post-V1.
- Never use a real database, blob tree, service, container, production host, or
  user configuration for tests. Use only uniquely owned disposable fixtures
  explicitly authorized by the active plan.
- Follow red-green TDD for implementation. Run the narrow gate first, then the
  affected regressions, Release builds with `-warnaserror`, formatting, and
  whitespace checks. Never claim a gate passed unless it ran in the current
  worktree.
- Keep secrets, credentials, connection values, local paths, database files,
  caches, and generated artifacts out of documentation and Git.
- Do not expose Phase 4 or PostgreSQL through `backend/Program.cs`,
  `entrypoint.sh`, provider selection, Compose, controllers, UI, or runtime
  service registration.

## graphify

This project uses a clone-local knowledge graph at `graphify-out/` with god
nodes, community structure, and cross-file relationships. Generated graph files
stay ignored and must not be staged.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- If `graphify-out/graph.json` is absent, run
  `graphify extract . --code-only --global --as pinrail` before graph-backed work.
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Graph updates are expected after hooks or incremental extraction. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
