# Task 2D internal key startup contract

## Scope

Close the independently confirmed reachable V1 credential defect without
widening protocol authority. Work only in `/opt/pinrail`; preserve the dirty
Task 2 checkpoint and SearchNudge quarantine. This is a separate sequential
slice after Task 2C. Do not stage, commit, push, or mutate production.

The copied `.env.example` currently assigns the public literal
`FRONTEND_BACKEND_API_KEY=replace-with-random-hex`. `entrypoint.sh` accepts any
nonempty value, while the SAB and scoped ARR protocol controllers accept the
same internal key as an alternate credential. A copied example therefore
creates a known bearer for reachable `/protocol` routes.

## RED contract

Extend the hermetic shell entrypoint contract before production edits. Prove:

- omitted or empty `FRONTEND_BACKEND_API_KEY` generates and exports exactly 64
  hexadecimal characters;
- an explicitly configured 64-character hexadecimal key is preserved exactly;
- the historical placeholder, shorter values, longer values, whitespace,
  punctuation, and non-hex characters fail before identity, filesystem,
  database, or child-process side effects;
- rejection returns a fixed configuration status and fixed diagnostic that
  never includes or measures the supplied value;
- invalid maintenance argv still wins and remains side-effect-free;
- `.env.example` does not assign an internal key and explains that omission
  triggers per-start container generation, while an operator-pinned key must be
  independently generated 64-hex material;
- executable container fixtures that explicitly configure the key use valid,
  deterministic, non-key-like test material without printing it.

Record the expected RED failure. Do not put rejected values in test output.

## Minimum GREEN

Add one POSIX-shell validation/generation function and invoke it in `main`
after argv validation but before the first mutable/system-discovery action.
Accept an explicit key only when it is exactly 64 hexadecimal characters;
otherwise return `78` with `FRONTEND_BACKEND_API_KEY must be exactly 64
hexadecimal characters.` Generate 32 random bytes as lowercase hex when the
variable is omitted or empty. Never log the key.

Remove the assignment from `.env.example`; keep only comments. Update the
current setup/security documentation and container-smoke fixtures required by
the executable contract. Do not change backend protocol carrier semantics,
route scope, or frontend direct-unit fixtures that bypass the container
entrypoint.

## Verification

Run the shell contract, `sh -n` on every changed shell file, ShellCheck when
available, the affected entrypoint/front-end production tests, whitespace
checks, and the relevant gitleaks scan. Document commands and outcomes in
`.superpowers/sdd/task-2d-internal-key-startup-report.md`.
