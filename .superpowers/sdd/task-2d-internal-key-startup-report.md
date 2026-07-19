# Task 2D internal key startup contract report

Date: 2026-07-19 UTC

## Outcome

Task 2D is locally GREEN and independently accepted within its affected scope.
The combined-container entrypoint now accepts only an explicitly configured
64-character hexadecimal internal key, or generates and exports a fresh
lowercase 64-character hexadecimal value when the variable is omitted or
empty. Invalid explicit values fail with the fixed configuration status and
diagnostic before identity-system discovery/mutation, filesystem, database, or
child-process activity.

Normal combined-container startup also preflights the selected frontend
authentication mode before those side effects. Local mode requires a valid
64-character hexadecimal `SESSION_KEY`; Authentik proxy mode remains session-
key independent. Maintenance invocations remain independent of frontend
session configuration, and invalid maintenance argv retains first precedence.

The example environment no longer publishes an internal-key assignment.
Operator documentation and executable container fixtures now use the frozen
contract without reusing session or public API credentials. Backend carrier,
route, and frontend direct-unit semantics were not changed. The SearchNudge
incident quarantine was not edited.

This report does not claim that the whole-repository release gate, local
container lifecycle gate, remote exact-HEAD CI, or V1 release plan is green.
Those remain primary-agent checkpoint work.

## Frozen startup contract

- `main` validates maintenance argv first, preserving status `64` and the
  existing usage diagnostic when argv and credential input are both invalid.
- Normal startup then validates `AUTH_MODE` and the local session-key contract.
  Missing/invalid local keys return `78` and only
  `SESSION_KEY must be exactly 64 hexadecimal characters.` Invalid modes return
  `78` and only `AUTH_MODE must be either local or authentik-proxy.`
- Authentik proxy normal startup and every valid maintenance invocation do not
  require `SESSION_KEY`.
- Immediately afterward, `configure_internal_api_key` runs before signal
  setup, identity discovery, filesystem ownership, database work, or child
  execution.
- An omitted or empty `FRONTEND_BACKEND_API_KEY` is generated from 32 random
  bytes, encoded as exactly 64 lowercase hexadecimal characters, and exported.
- A supplied key is accepted only when it is exactly 64 hexadecimal
  characters. Valid mixed case is preserved byte-for-byte and exported.
- Rejected explicit values return `78` and only:

  ```text
  FRONTEND_BACKEND_API_KEY must be exactly 64 hexadecimal characters.
  ```

- A failed entropy-encoding command, short output, or invalid output returns
  `70` and only:

  ```text
  entrypoint_failure code=internal_key_generation_failed
  ```

- Validation necessarily checks input length and character shape, but neither
  failure diagnostic logs or echoes supplied material or its measured length.
  Generation failures clear the temporary candidate and unset
  `FRONTEND_BACKEND_API_KEY`, including when it began as an exported empty
  variable. Only a successful, validated candidate becomes usable exported
  material.
- `.env.example` intentionally omits the assignment. Omission means a fresh
  generated value on every combined-container start. Operators who need a
  stable value must independently generate 64 hexadecimal characters and must
  not reuse `SESSION_KEY` or a public API credential.
- A valid mixed-case session key is preserved byte-for-byte and exported to the
  frontend child. Validation does not silently normalize key material.

## RED evidence

The hermetic shell contract was extended before the production change. The
first exact RED command was:

```text
sh -n tests/test_entrypoint_contract.sh && sh tests/test_entrypoint_contract.sh
```

It exited `1` with this safe assertion failure and no candidate value:

```text
invalid internal key did not return the fixed configuration status
```

The matrix covered the historical placeholder, short, long, whitespace,
punctuation, and non-hex explicit values; maintenance-argv precedence; omitted
and empty generation; mixed-case preservation/export; example-file omission;
and deterministic container/workflow fixtures. A positive generation-failure
case was then added to prove the fixed status, fixed diagnostic, and absence of
partial exported material.

### Independent-review correction RED

Independent review then found three gaps in the first GREEN implementation:

- generation wrote directly to `FRONTEND_BACKEND_API_KEY`, so partial output
  could be retained (and remain exported when the input began exported-empty),
  while 64-character lowercase output from a failing encoder could be
  accepted;
- the workflow's forced-migration smoke omitted a valid `SESSION_KEY`, so an
  unrelated authentication startup failure could satisfy its nonzero-only
  assertion;
- the contributor recipe assigned command substitution directly in `export`,
  masking generator failure status. The initial report also incorrectly said
  validation did not “measure” input even though length/shape validation is
  required.

Before correcting production, workflow, or documentation, the review
regressions were added. The exact shell RED command was:

```text
sh -n tests/test_entrypoint_contract.sh && sh tests/test_entrypoint_contract.sh
```

It exited `1` with this exact safe output and no candidate value:

```text
failed internal key generation retained candidate material
```

That executable matrix covers partial lowercase output plus nonzero encoder
status for both initially-unset and initially-exported-empty variables, and
valid-shaped lowercase-64 output plus nonzero status for both initial states.
It records whether the fake encoder received the required exact single-command
arguments and requires the fixed diagnostic, status `70`, an unset shell
variable, no child export, and no candidate disclosure.

Two isolated pre-GREEN static predicates also exited `1` with safe fixed
messages:

```text
migration-failure smoke does not prove authenticated fixed failure
contributor setup masks internal-key generator status during export
```

### Frontend-authentication preflight RED and review correction

The later combined-container review identified a real fresh-image mismatch:
the frontend correctly rejected a missing local `SESSION_KEY`, but the generic
compiled bootstrap boundary intentionally hid that fixed configuration detail
while the container smoke required it. A hermetic entrypoint RED proved that
missing and malformed local session keys could reach identity-system/filesystem
work instead of failing at the entrypoint boundary. The minimum GREEN added the
normal-startup authentication preflight while keeping maintenance independent.

The first focused review found P0/P1/P2/P3 `0/0/3/0`:

- a non-`local` value could bypass the local-key check instead of being limited
  to exact `authentik-proxy`;
- one ordering test supplied a valid internal key and therefore did not prove
  session validation preceded internal-key generation;
- a digits-only session fixture did not prove mixed-case preservation and child
  export.

Tests were extended before the repair. The correction RED covered invalid and
empty `AUTH_MODE`, missing/invalid local keys with omitted or empty internal
key, and mixed-case child export. Minimal GREEN made mode selection exact and
exported an accepted session key unchanged. Valid maintenance argv still
bypasses frontend session validation, invalid argv still wins, and Authentik
proxy startup remains session-key independent.

## Production and fixture changes

- `entrypoint.sh` adds one POSIX-shell validation/generation helper and invokes
  it at the required pre-side-effect boundary. Generation uses one checked
  `hexdump -n 32 ... /dev/urandom` command and validates a temporary candidate.
  Every command/length/shape failure clears that candidate and unsets the
  target variable; assignment/export occurs only after success. The old late
  unvalidated generation block was removed.
- `.env.example` removes the known placeholder assignment and documents
  automatic per-start generation and independently generated pinned values.
- `CONTRIBUTING.md` generates into a temporary shell variable, checks encoder
  status and exact lowercase-64 shape, exports only after validation, clears
  the temporary variable, and states that both split processes share one
  independently generated key without credential reuse.
- `tests/test_entrypoint_container.sh` and the migration-failure job in
  `.github/workflows/verify.yml` construct deterministic valid 64-character
  hexadecimal fixtures without printing them. The migration-failure job uses
  distinct internal/session fixtures, declares local authentication, and
  requires the exact fixed database-migration failure line after rejecting
  success and timeout.
- `tests/test_entrypoint_contract.sh` contains the hermetic executable matrix.
- `entrypoint.sh` now performs the exact session/authentication preflight only
  for normal startup. The fixed check occurs before internal-key generation and
  every identity-system, filesystem, database, or child side effect.
- `tests/test_entrypoint_container.sh` proves a fresh image reports the bounded
  missing/invalid local-session diagnostic and starts with valid independent
  session/internal fixtures. `frontend/bootstrap.ts` retains its generic fatal
  boundary for arbitrary imported errors.

Several edited files also contain earlier uncommitted Task 2 work in the shared
checkpoint. This report attributes only the internal-key changes above to Task
2D.

## GREEN verification

### Shell and static contract

- `sh -n tests/test_entrypoint_contract.sh && sh tests/test_entrypoint_contract.sh`:
  PASS; exact output `entrypoint contract: PASS`.
- After adding the positive generator-failure proof,
  `sh tests/test_entrypoint_contract.sh`: PASS; exact output
  `entrypoint contract: PASS`.
- After the independent-review corrections, the fresh exact command
  `sh -n tests/test_entrypoint_contract.sh && sh tests/test_entrypoint_contract.sh`:
  PASS; exact output `entrypoint contract: PASS`. This includes all four
  failing-encoder state/shape combinations and workflow/documentation static
  contracts.
- `sh -n entrypoint.sh tests/test_entrypoint_contract.sh tests/test_entrypoint_container.sh`:
  PASS.
- `shellcheck --severity=error entrypoint.sh tests/test_entrypoint_contract.sh tests/test_entrypoint_container.sh`:
  PASS with no output.
- `actionlint .github/workflows/verify.yml`: PASS with no output.
- `git diff --check`: PASS before the report was written; it is rerun as part
  of the final handoff verification.

The final focused review, including a subsequent POSIX-shell portability
cleanup, found P0/P1/P2/P3 `0/0/0/0`. Fresh `sh -n`, the hermetic shell
contract, severity-error ShellCheck, and `git diff --check` passed after that
cleanup. The review explicitly confirmed maintenance independence, invalid-
argv precedence, exact auth-mode selection, pre-side-effect ordering, and
mixed-case session-key preservation/export.

The first default-severity ShellCheck command exited `1` on warning/info debt:
POSIX `local` declarations, unquoted argument expansion, an `A && B || C`
construct, test `CDPATH`/dynamic-source handling, and intentional literal
canaries. The later portability and container-identity repairs removed or
scoped that debt. Final full ShellCheck passed with no finding.

### Subsequent container-identity hardening

Trivy reported no dependency vulnerability, but initially reported three
DS-0002 image-user findings. Independent triage classified one actionable P2:
V1 must reject zero and padded-zero `PUID`/`PGID`, both children must run as the
resolved non-root user and group, and only PID 1 may remain root. The exact root
`Dockerfile` retains one path-scoped DS-0002 exception because dynamic identity,
owned-path preparation, and two-child supervision require root. The exception
does not cover privileged DFS/FUSE; V1 uses the rclone sidecar.

RED tests preceded the repair. Minimum GREEN normalizes valid nonzero IDs,
rejects every all-zero spelling, invokes `su-exec` with resolved user and group,
and retires `backend/Dockerfile`, `backend/entrypoint.sh`,
`frontend/Dockerfile`, its Dependabot entry, and the unsupported standalone
shipped-path test. The first implementation review found P0/P1/P2/P3
`0/0/1/0`: the smoke inspected client-host `/proc` and only real IDs, which was
not portable to remote/rootless/user-namespace-remapped Docker. A tests-only
RED then required inspection inside the container PID namespace and all four
real/effective/saved/filesystem UID and GID columns. The repaired contract and
re-review passed with P0/P1/P2/P3 `0/0/0/0`.

Final focused gates after this repair: Python `157/157`, packaged runtime
`6/6`, shell contract PASS, typecheck PASS, `sh`/Dash/BusyBox syntax PASS, full
ShellCheck PASS, actionlint PASS, `git diff --check` PASS, Trivy npm
vulnerabilities `0`, and Trivy Dockerfile misconfigurations `0`. A fresh
`npm audit` also reported `0` vulnerabilities, and direct/transitive NuGet
vulnerability listings for both production and test projects reported none.
Local Docker was intentionally not run; exact remote CI owns executable
container proof.

### Affected frontend runtime gates

From `frontend/`:

```text
npm test -- --run server/entrypoint.production.test.ts server/entrypoint.authentication.integration.test.ts server/entrypoint.proxy.test.ts server/packaged-runtime.server.test.ts
```

Result: 4 files passed; 110 tests passed, 0 failed.

- `npm run typecheck`: PASS.
- `npm run build`: PASS; client and SSR production builds completed.
- `npm run build:server`: PASS.
- `npm run test:packaged`: PASS.

### Secret scanning

- A raw `gitleaks dir --redact --no-banner .` scan reported 22 findings, all in
  ignored generated `graphify-out/cache/stat-index.json`; redacted metadata
  inspection confirmed no Git-visible or Task 2D finding. No candidate values
  were printed and no global rule suppression was added.
- The current-tree scan was rerun with the default rules plus one ephemeral,
  exact ignored-generated-path allowlist for `^graphify-out/`. It scanned about
  14.24 MB and exited `0` with no leaks.
- `gitleaks git --redact --no-banner .` scanned 639 commits (about 11.90 MB) and
  exited `0` with no leaks.

The path allowlist existed only in the process environment for the current
scan. Repository configuration and generated Graphify output were not edited
to suppress findings.

### Graph and container boundary

- `graphify update .`: PASS for the AST-only graph; 15,668 nodes, 40,837 edges,
  and 711 communities. It warned that 11 JSON/data sources yielded zero nodes
  and skipped the oversized HTML visualization. `graphify-out/graph.json` and
  `graphify-out/GRAPH_REPORT.md` were regenerated and remain ignored by
  `.gitignore`.
- Docker was not run locally because this host's known buildx environment
  blocker prevents a trustworthy container gate. Exact-HEAD remote CI owns the
  container lifecycle smoke after the reviewed checkpoint is committed and
  pushed. No local or production container/service was mutated.

## Integrity hashes

Final Task 2D boundary SHA-256 values at checkpoint reconciliation:

```text
8cba409aa4f11f779c37961931180f87e327260167ee4c2c8fd8d1e85cc1e712  entrypoint.sh
bb5d34591bdb05b7504be1413899c0f1a4b408e78448efa1ff6d2d84a173721f  tests/test_entrypoint_contract.sh
f7b64ba193117a163c93c70ba1a658dedc97c2bfed645956598620c50008d943  tests/test_entrypoint_container.sh
a84748e46103747b85da0991e5afcfd21952ad1ec532b722f2fb6ca300c848fc  frontend/bootstrap.ts
```

Final adjacent container-hardening hashes:

```text
1c0aee433739162e7e3054c2bc372e09d4883c816d0daf40a0af482e12fe89f6  .github/dependabot.yml
975b4c1659d718eea80586defff09cfc385deb4830b7d86a5f2919ebb8865d53  Dockerfile
d456a84121f3ffb7b5deec81c238621ae8b2c83d92a61d203617f4af0dd62697  frontend/server/packaged-runtime.server.test.ts
e0a406d5895dbb4bc25622cb991f80573431b20eb83b54f43bd95b7b18e6f973  tests/test_runtime_release_contract.py
3f601a7a979b829d591bf6bde70af2f96e62db7da615760e075110f52a53fe75  tests/test_release_workflow_contract.py
```

Operator documentation and workflow files legitimately received later Task 2E
or combined-checkpoint edits, so this final integrity block does not present
their moving hashes as Task 2D evidence.

SearchNudge quarantine SHA-256 values remain exact:

```text
998f04a496b0948283a76cfb2b05a94cd4e739b153305fbefbe9c234764f228b  backend/Services/ArrSearchNudgeService.cs
f90a34332d4e3cfba47d9a0931bd1bb0f22ee588fc59c20224c26fbeb3a69eca  backend.Tests/Services/ArrOperationsServiceTests.cs
8bc5d01619f19a1ba291c3f546f5ce3c706ba988a9a194a9c7c9bcfcaeeb9def  backend.Tests/Services/ArrSearchNudgeServiceTests.cs
```

## Scope guard

No production host, database, blob tree, service, container, Figma file, Git
ref, remote, or deployment was mutated. No Git staging, commit, or push was
performed. Task 2E subsequently completed as a separate reviewed sub-slice.
The combined signed checkpoint, formal security scan, push, and exact remote CI
remain outside this report.
