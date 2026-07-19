# Task 2C public failure, log, and persistence contract

## Context

This is the first unfinished release-blocking subtask inside the existing Task 2
proxy/symlink-only checkpoint. The checkout is already intentionally dirty with
reviewed Task 2 work. Preserve every unrelated change and the SearchNudge
incident quarantine. Work only in `/opt/pinrail`; never create or use
`/opt/pinrail/repo`.

Read `AGENTS.md`, `HANDOFF.md`, Task 2C of the canonical V1 plan, and sections
5.8 and 7.3 of the governing design before editing. Use red-green TDD. Do not
stage, commit, push, mutate services/containers/databases outside disposable
test fixtures, or touch production.

## Binding security invariant

Every reachable public failure must use one bounded, stable contract: a fixed
code, a fixed safe message, and a server-generated 32-character lowercase hex
correlation ID. No response, header, process output, persisted diagnostic,
WebSocket payload, or frontend error display may contain an exception message,
stack, credential, connection value, request/provider body, provider host/user,
URL userinfo, local path, SQL detail, CR/LF, terminal escape, control character,
or oversized attacker-controlled text.

Preserve success payloads byte-for-byte/schema-for-schema by making new error
metadata optional and omitted when null. Preserve existing HTTP status codes,
DAV `Retry-After`, HEAD/no-body behavior, SAB `status`/`error`, history
`fail_message`, maintenance/ARR `error`, rclone safe error categories, and
existing WebSocket topic/message schemas. Defensive projection must protect
legacy/raw rows. Sanitize a queue terminal failure once before its atomic
completion commit and reuse the identical safe value for history, lifecycle,
and reconciliation.

## Required RED coverage

Build one composite synthetic canary from separate, non-secret fragments that
covers a credential marker, absolute path, URL with userinfo, SQL/connection
text, provider body, nested exception, CR/LF, ESC/control bytes, and more than
4 KiB of text. Do not put the whole canary in test names or assertion messages.
Assert absence with boolean/length assertions so failing output does not print
the canary.

Before production edits, add focused failing tests through the real boundaries:

1. `BaseApiController` and `SabApiController` 400/401/500 paths: status,
   unchanged compatibility fields, fixed code/message, 32-hex correlation ID,
   matching response header, and no canary.
2. `ExceptionMiddleware`: bad request, DAV missing/retry/range/generic failures,
   a generic non-DAV 500, response-started behavior, DAV status/`Retry-After`,
   HEAD behavior, and captured logs with no canary.
3. `TestArrConnectionController` HTTP-200 connection failure and
   `TestUsenetConnectionController` logging: no provider/host/user/raw exception,
   while success/connected semantics remain compatible.
4. `QueueItemProcessor`: an injected non-retryable canary is absent from logs,
   `HistoryItem.FailMessage`, ARR lifecycle reason, history HTTP projection, and
   visible history WebSocket payload; the stored/projected summary is stable.
5. Maintenance failure persistence and DTO projection; seeded legacy
   SearchNudge, ARR-import, rclone/mount, repair, and health diagnostic
   projections.
6. Backend and frontend WebSocket error/close logging does not render raw
   exception or close-reason text and retains reconnect/subscription behavior.
7. `frontend/app/clients/backend-client.server.ts` and
   `frontend/app/utils/http-response.ts`: accept only a structurally validated,
   bounded stable envelope; arbitrary JSON fields, plain bodies, parser text,
   controls, and oversized values fall back to `HTTP <status>`.
8. The real Express proxy's own policy/auth/upstream errors use the same bounded
   envelope and correlation header without changing zero-upstream behavior.

Include at least one legitimate positive control at every modified boundary.
Run each new test before implementation and record the expected failure.

## Required GREEN scope

Prefer one Phase-4-independent repository-native contract/helper shared by API
controllers and middleware. Add optional error code/correlation fields to
`BaseApiResponse` and `SabBaseResponse` with null omission. Generate correlation
IDs on the server; never accept or reflect a request-supplied correlation ID.
Use structured stable log fields only. A V1 console-output formatter or
equivalent final output boundary may be used to guarantee that untouched legacy
log events (including the quarantined SearchNudge service) cannot render raw
templates, properties, or exceptions, but it must retain a deterministic event
code and allowlisted operational IDs and have direct tests.

At minimum, trace and close the inventory in:

- `backend/Api/Controllers/BaseApiController.cs`
- `backend/Api/SabControllers/SabApiController.cs`
- `backend/Middlewares/ExceptionMiddleware.cs`
- `backend/Queue/QueueItemProcessor.cs`
- `backend/Services/HistoryVisibilityService.cs`
- `backend/Api/SabControllers/GetHistory/GetHistoryResponse.cs`
- `backend/Services/MaintenanceRunService.cs`
- `backend/Services/MaintenanceRunTransitions.cs`
- `backend/Api/Controllers/Maintenance/MaintenanceRunResponses.cs`
- `backend/Services/ArrOperationsService.cs` and its public DTO projection
- `backend/Api/SabControllers/StatusDiagnostics.cs`
- public repair/health projections
- `backend/Api/Controllers/TestArrConnection/TestArrConnectionController.cs`
- `backend/Api/Controllers/TestUsenetConnection/TestUsenetConnectionController.cs`
- raw error persistence paths in `HealthCheckService`/worker coordination
- `backend/Websocket/WebsocketManager.cs`
- `frontend/server/websocket.server.ts`
- `frontend/server/app.ts`
- both frontend HTTP error readers and their direct consumers as needed

Do not edit these quarantined files; their exact current SHA-256 hashes must
remain unchanged:

- `backend/Services/ArrSearchNudgeService.cs`
- `backend.Tests/Services/ArrOperationsServiceTests.cs`
- `backend.Tests/Services/ArrSearchNudgeServiceTests.cs`

If safe SearchNudge persistence requires enforcement outside the quarantined
writer, place it at the model/persistence boundary and test it directly.

## Verification and report

Run the focused backend/frontend tests first, then affected controller,
middleware, queue, maintenance, status, WebSocket, and frontend suites. Run
Release builds with `-warnaserror`, frontend typecheck, and scoped formatting or
lint checks for changed files. Do not run the full repository release gate in
this subtask unless needed for diagnosis; the primary agent will run the broad
checkpoint gate after all review fixes.

Write the detailed RED/GREEN commands, counts, changed files, self-review,
remaining concerns, and exact quarantine hashes to
`.superpowers/sdd/task-2c-public-failure-contract-report.md`. Return only a short
status and test summary to the parent.
