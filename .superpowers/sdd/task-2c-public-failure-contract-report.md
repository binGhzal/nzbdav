# Task 2C public failure, log, and persistence contract report

Date: 2026-07-19 UTC

## Outcome

Task 2C is locally GREEN and independently accepted within its affected scope.
Reachable backend and frontend failures now converge on bounded, fixed public
messages/codes and server-generated correlation IDs. Public persistence and
projections collapse raw diagnostics to fixed categories. Production process,
framework, access, and console output use fixed final boundaries. The
SearchNudge incident quarantine was not edited.

This report does not claim that the complete repository release gate, remote
CI, or the V1 release plan is green. Those remain primary-agent checkpoint
work.

## Implemented contract

### Backend public failure boundary

- `backend/Security/PublicFailureContract.cs` owns the closed public failure
  code/message catalog, exact JSON envelope, fixed error/correlation headers,
  and server-generated 32-character lowercase-hex correlation IDs.
- `backend/Api/Controllers/BaseApiController.cs`,
  `backend/Api/Controllers/BaseApiResponse.cs`,
  `backend/Api/SabControllers/SabApiController.cs`, and
  `backend/Api/SabControllers/SabBaseResponse.cs` preserve compatibility fields
  while adding optional failure metadata only on failures.
- `backend/Middlewares/ExceptionMiddleware.cs` maps known DAV and generic
  exceptions to the fixed contract, preserves DAV status and `Retry-After`,
  emits no HEAD body, and rethrows the original exception after response start
  rather than attempting an unsafe rewrite.
- Direct controller failure results were normalized in backup, maintenance,
  repair cancellation, ARR connection, and Usenet connection surfaces.

### Final log and process boundaries

- `backend/Logging/V1SafeConsoleFormatter.cs` emits only UTC timestamp, fixed
  level code, deterministic level-derived event code, validated correlation
  IDs, and allowlisted strongly typed GUID properties. It never renders the
  message template, arbitrary properties, or exception.
- `backend/Program.cs` routes the production console sink through that formatter
  and uses `backend/Hosting/StartupFailureContract.cs` for fixed startup output.
- The queue terminal-failure path now records a fixed structured summary and
  typed queue ID without attaching the injected exception or raw message.
- `frontend/bootstrap.ts` owns the compiled direct-entrypoint fatal process
  boundary. Its direct fatal output is fixed and exits immediately.
- `frontend/server/debug-output.ts` disables wildcard runtime debug output
  before Express/HPM routing so request targets, credentials, and private-hop
  details cannot reach those diagnostic sinks.
- `frontend/app/entry.server.tsx` owns React Router request/stream error output.
  `frontend/app/entry.client.tsx` owns router and React 19 caught, uncaught, and
  recoverable hydration callbacks. Every callback emits only a fixed event.
- Production morgan output is `frontend_http_failure` plus a validated 100-599
  three-digit status. Request method and target are never rendered.
- `entrypoint.sh` emits only fixed lifecycle/failure codes and suppresses
  setup-command stderr before the backend/frontend fixed process boundaries.

### Persistence and projection boundary

- `backend/Security/PublicDiagnosticContract.cs` owns fixed diagnostic
  categories.
- `backend/Database/DavDatabaseContext.cs` sanitizes tracked public diagnostics
  at the save boundary, covering history, maintenance, SearchNudge, ARR import,
  rclone/mount, worker, health, repair, ARR lifecycle, and import-receipt rows.
- Execute-update writers in worker coordination, health quarantine, ARR import,
  import receipts, and rclone invalidation were normalized at their write sites.
- Defensive history, maintenance, status, repair, health, ARR, and rclone
  projections protect legacy/raw rows.
- `QueueItemProcessor` sanitizes the terminal summary before its atomic
  completion commit and reuses the identical value for history, lifecycle,
  reconciliation, HTTP history projection, and visible-history WebSocket
  output.

### Frontend readers and displays

- `frontend/app/utils/public-failure.ts` validates the exact four-field
  envelope, fixed code/message pair, 32-lowerhex correlation ID, UTF-8, and a
  512-byte cap. Streaming readers cancel immediately above the cap.
- Its shared resolver accepts body-only and header-only compatibility. If both
  are valid, code and correlation ID must match exactly; conflicting valid
  pairs fail closed.
- `frontend/app/utils/http-response.ts`, the server-side backend client, and XHR
  upload failure handling all use that resolver. Arbitrary JSON, plain bodies,
  parser text, controls, oversized values, and conflicting identities collapse
  to `HTTP <status>`.
- Queue/history mutations, maintenance, settings, connection tests, health,
  onboarding/login, root error display, and WebSocket logging no longer render
  raw error bodies or exception text.

### Provenance

The two owned React Router entry files are adapted from the installed
`@react-router/dev` 7.18.1 default Node/client templates. The repository's
declared compatible caret ranges begin at 7.5.3 and 7.16.0; the lockfile
currently resolves 7.18.1. React Router's MIT copyright and permission notice
is preserved in `THIRD_PARTY_NOTICES.md`, and each adapted entry contains an
explicit provenance pointer.

## RED evidence

The following failures were observed before their corresponding production
changes:

| Boundary | RED command/evidence | Observed failure |
|---|---|---|
| Base/SAB compatibility headers | Focused public-envelope controller matrix | 2 failed, 13 skipped before the shared header helper |
| Exception middleware | Focused middleware matrix | 13 failed, 2 passed before fixed mappings/body/log handling |
| Public persistence | Focused diagnostic projection matrix | 7 failed, 1 passed; a direct context case persisted raw history detail |
| Connection UI | Focused ARR/rclone/Usenet UI matrix | 3 failed, 7 passed while raw response bodies were still rendered |
| Queue cross-layer terminal failure | `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~QueueItemProcessorVerificationTests.ProcessAsync_SanitizesOneTerminalFailureAcrossPersistenceLifecycleAndWebsocket' --logger 'console;verbosity=minimal'` | 1 failed because the final formatter sink was empty |
| Body/header identity conflict | `npm test -- --run app/utils/http-response.test.ts app/clients/backend-client.server.test.ts app/routes/queue/controllers/nzb-upload-controller.test.ts` | 3 failed, 29 passed; all three accepted the body despite conflicting valid headers |
| Framework default logging | `npm test -- --run app/entry.server.test.ts app/entry.client.test.tsx` | 2 suites failed because no project-owned entries existed |
| Request-controlled method logging | `npm test -- --run server/entrypoint.production.test.ts -t 'does not render a request-controlled valid custom method'` | 1 failed, 8 skipped; actual log was `M-SEARCH 404` instead of the fixed event |

The first attempted arbitrary custom-method fixture was rejected by Node's HTTP
parser before Express and therefore produced no access log. It was corrected to
the valid, parser-recognized `M-SEARCH` token before recording the meaningful
RED above.

## GREEN verification

### Focused and consolidated tests

- Queue cross-layer test after implementation: 1 passed, 0 failed.
- Shared reader/backend-client/upload resolver matrix: 32 passed, 0 failed.
- Owned React Router entry tests: 2 passed, 0 failed.
- Production entrypoint/access-log suite: 9 passed, 0 failed.
- Earlier focused groups retained during the slice:
  - middleware: 13/13 passed;
  - persistence/queue/maintenance: 51/51 passed;
  - console formatter/startup: 34/34 passed;
  - process boundary: 2/2 passed;
  - frontend focused error matrix: 58/58 passed;
  - shell entrypoint contract: PASS.
- Final consolidated backend command:

  ```text
  dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore --no-build --filter 'FullyQualifiedName~ExceptionMiddlewareTests|FullyQualifiedName~PublicFailureEnvelopeTests|FullyQualifiedName~DirectFailureControllerTests|FullyQualifiedName~PublicDiagnosticProjectionTests|FullyQualifiedName~QueueItemProcessorVerificationTests|FullyQualifiedName~MaintenanceRunServiceTests|FullyQualifiedName~MaintenanceRunTransitionTests|FullyQualifiedName~V1SafeConsoleFormatterTests|FullyQualifiedName~NzbdavRoleStartupTests|FullyQualifiedName~WebsocketManagerTests|FullyQualifiedName~TestUsenetConnectionControllerTests|FullyQualifiedName~TestArrConnectionControllerTests|FullyQualifiedName~TestRcloneConnectionControllerTests|FullyQualifiedName~WorkerJobLeaseTests.DatabaseCoordinator_RetryFailureIsIdempotentAfterAmbiguousSuccess|FullyQualifiedName~WorkerJobLeaseTests.FailWorkerJobAsync_RetriesThenQuarantinesAtMaxAttempts|FullyQualifiedName~PostDownloadConfirmedMissingWithoutRepairQuarantinesImportDomainAndFencesStaleArrLease|FullyQualifiedName~PostDownloadQuarantineSurvivesSabHistoryRemovalWithoutRemovalBroadcast|FullyQualifiedName~PostDownloadConfirmedMissingAfterHistoryRemovalKeepsDurableDiagnostics' --logger 'console;verbosity=minimal'
  ```

  Result: 122 passed, 0 failed, 0 skipped.

- Final consolidated frontend command covered 21 files including both owned
  entries, both readers, upload, queue/history mutation consumers, maintenance,
  settings connection surfaces, root boundary, process output, WebSocket,
  proxy/authentication, and production entrypoint.

  Result: 21 files passed; 432 tests passed, 0 failed.

### Build and static gates

- `dotnet build backend/NzbWebDAV.csproj -c Release --no-restore -warnaserror`:
  PASS, 0 warnings, 0 errors.
- `dotnet build backend.Tests/backend.Tests.csproj -c Release --no-restore -warnaserror`:
  PASS, 0 warnings, 0 errors.
- `npm run typecheck`: PASS (`react-router typegen && tsc -b`).
- `npm run build`: PASS; client and SSR production builds both completed.
- Scoped `dotnet format whitespace ... --verify-no-changes` for the Task 2C
  backend production file inventory: PASS.
- Scoped `dotnet format whitespace ... --verify-no-changes` for the Task 2C
  backend test file inventory: PASS.
- `sh -n entrypoint.sh`: PASS.
- `sh tests/test_entrypoint_contract.sh`: `entrypoint contract: PASS`.
- `git diff --check`: PASS.
- `graphify update .`: AST-only graph refreshed. It warned that 11 JSON/data
  sources produced zero nodes; `graphify-out/graph.json` and the graph report
  were regenerated and remain ignored.

A whole-project formatter verification was also attempted. It exited 2 on
whitespace debt in files outside this Task 2C formatting inventory in the
shared dirty worktree. Task 2C files were then mechanically formatted and both
scoped verify-no-changes commands passed. No whole-repository formatting claim
is made.

### Independent review

The final focused Task 2C review found P0/P1/P2/P3 `0/0/0/0`. It accepted the
response, log, persistence, and frontend-display boundaries and retained two
explicitly classified residuals:

- a repair result named `Repaired` means the ARR command was accepted; it does
  not claim that an external ARR completed the repair;
- maintenance `ExecuteUpdate` calls currently write only fixed code-owned
  strings, but a future extension that admits request/provider text would need
  a renewed write-site sanitizer review.

Neither residual is a reachable Task 2C release blocker in the reviewed diff.
The final whole-checkpoint Codex Security diff scan remains a separate pending
gate and must run against the stable combined Task 2C-2E diff.

## Self-review notes and residual scope

- The response-started middleware proof uses a real `IHttpResponseFeature` unit
  boundary. It is not a full Kestrel socket test.
- The rclone direct controller may return `result.Error`, but `RcloneClient`
  classifies it to a fixed safe category; adversarial controller tests prove a
  raw Basic-auth value is not returned.
- `readHttpActionResult` is intentionally limited to the tiny SAB
  queue/history mutation success contract (`{ status: true }`). It is not a
  general response reader.
- Express's health method rejection retains fixed framework 405 text; it does
  not include request/provider content.
- `QueuePriorityHint.StaleReason` is code-owned fixed text. It is not populated
  from provider/user response bodies.
- Entrypoint setup subprocess stderr is suppressed. The eventual `su-exec`
  child is a validated backend/frontend executable whose process boundaries are
  fixed by this slice.
- Legacy raw Serilog events can still exist internally. The only production
  console sink is the directly tested final safe formatter, which drops raw
  templates, arbitrary properties, and exceptions. New terminal queue logs are
  fixed before reaching that boundary.
- No production service, container, database, Figma file, Git ref, remote, or
  deployment was mutated by this subtask.
- The full repository test suite, container lifecycle gate, complete release
  gate, and exact remote CI were intentionally not run by the Task 2C
  sub-slice. Combined local regression is recorded in the canonical handoff;
  exact remote CI still belongs to the signed checkpoint.

## SearchNudge quarantine integrity

Exact SHA-256 values after all edits and verification:

```text
998f04a496b0948283a76cfb2b05a94cd4e739b153305fbefbe9c234764f228b  backend/Services/ArrSearchNudgeService.cs
f90a34332d4e3cfba47d9a0931bd1bb0f22ee588fc59c20224c26fbeb3a69eca  backend.Tests/Services/ArrOperationsServiceTests.cs
8bc5d01619f19a1ba291c3f546f5ce3c706ba988a9a194a9c7c9bcfcaeeb9def  backend.Tests/Services/ArrSearchNudgeServiceTests.cs
```
