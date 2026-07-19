# Task 2B Slice Brief: Wire the Exact Production Proxy Boundary

## Authority and base

This slice continues Task 2B of the canonical V1 plan from clean checkpoint
`173b743c288f69f9f129e66a09dd4a6caed37023` on
`pinrail/v1-backend-wip`. It is governed by `AGENTS.md`, `HANDOFF.md`,
`docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md`,
and `docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md`.

V1 remains Docker-first, clean-install-only, SQLite-only, one control owner,
and `role=all`. PostgreSQL, Transfer-v3 Phase 4, production state, release
publication, visual changes, and Figma mutation are outside this slice.

The public API-key carrier contract sealed at `c550bc61a7d16df17278ec755fc2516015d95b1e`
is unchanged. The SearchNudge report-mode quarantine sealed at
`88e05f87e37147bf60b7fad8b0914df43e219eab` is a mandatory regression gate.

## Reviewed-source identity

The council and matrix were regenerated against these exact pre-RED files:

| File | SHA-256 |
| --- | --- |
| `frontend/server/app.ts` | `175d83425111c35fb1f810ce1660c1628841334f2b26e9296bfc7f8f8de081bf` |
| `frontend/server/request-policy.ts` | `e2428bd187189b582c3a958077d46e14ced79d47557445d9aa2993b530e4d8de` |
| `frontend/server/request-policy.test.ts` | `4dd00e3a02b9b35f50fa93c3914dc2b71c9bb7194b1d57c58e0cbbd70e28911d` |
| `frontend/server.ts` | `f8cce18b41a0b5caa25854ead78ebe7c7e4e36da7248c3512f0bd2a3cef32af0` |
| `frontend/server/websocket.server.ts` | `9b5a4dad8c2686dfc398089e4579d5d0df8ddff88d4447f11b4b2ea1f612c477` |
| `frontend/server/websocket.server.test.ts` | `89c681b3540dc47b72ef756029b85783a305c64a5c471377fdb4a374a32ae0a9` |
| `frontend/app/auth/authentication.server.ts` | `578d4fad0ac337d04ef5740822ad0c70f679fa7c46c7b0e72f432ad355a946b1` |
| `frontend/app/auth/auth-middleware.server.ts` | `ac6afaee091c786ceb45d0b9fb70c060759074b37c6527382603a820e51b3fef` |
| `backend/Program.cs` | `98043fab0bdd97a186bcc18d212dea2da0d539265b3c81e79d19daa84c799d0d` |
| `backend/Api/SabControllers/SabApiController.cs` | `6270aae569b85d5efec072bb2b1f796017b588f8a5cddd4308ab9a8f22e20b8a` |
| `backend/Api/Controllers/Arr/ArrOperationsController.cs` | `3204122c2e60ecd3952115155890e263716ecb5c055ad9f19690e0a3c2f42ee6` |
| `backend/Extensions/HttpContextExtensions.cs` | `3071da818fe854aaa543ac69be6c7fef4bc3b57963c79e195fa3bd3ebd2954c0` |
| `backend/Extensions/NWebDavOptionsExtensions.cs` | `fe68804d48e2093be1f663708dbc20b4af8fddda8abe18f14a46f74dec770498` |

## Frozen listener and mount contract

- All browser, API, protocol, and WebSocket traffic uses the existing frontend
  listener. Backend port 8080 remains private.
- `URL_BASE` may be empty, one segment, or nested. Every application path below
  is mount-relative; the browser-visible path is `<URL_BASE><path>`.
- Runtime `URL_BASE` accepts root (`""` or `/`) or a literal origin-path made
  from nonempty segments. Surrounding whitespace, an omitted leading slash,
  and trailing slashes normalize away. Dot segments, an internal repeated
  slash, backslash, percent encoding, query/fragment syntax, and control
  characters fail startup before the listener binds.
- `/protocol` is the only Authentik unauthenticated-path exception. It confers
  no browser principal and never receives `FRONTEND_BACKEND_API_KEY`.
- Root `/healthz` is frontend-process liveness. Root `/health` is the bounded
  backend liveness relay for non-HTML probes. The browser health page remains a
  principal-protected React route at `<URL_BASE>/health`.
- `/healthz` and root `/health` accept only `GET`/`HEAD`; other methods return
  `405` with exact `Allow: GET, HEAD` and never fall through to the application.
- The only WebSocket upgrade target is exact `<URL_BASE>/ws`, without a query.
  A valid local or Authentik principal and same-listener browser Origin are
  required before HTTP 101.
- WebSocket Origin means exactly one header containing an `http` or `https`
  origin with no credentials, explicit path/trailing slash, query, or fragment.
  Its authority must equal the request `Host`. `Forwarded` and every
  `X-Forwarded-*` field are ignored for this comparison. Repeated, empty,
  opaque, malformed, foreign, or values over 512 characters fail before
  principal evaluation and before 101. `Host` must itself occur exactly once,
  be nonempty, and contain only a parsed authority with no credentials, path,
  query, fragment, or whitespace.

## Council synthesis and conflict resolution

Both regenerated seats report no P0 and agree that the inert production HTTP
classifier and post-upgrade WebSocket checks are reachable P1 blockers. Both
require a capture-backend RED, exact upstream-call assertions, caller migration,
and pre-101 WebSocket denial.

The chair resolves three differences from source evidence:

1. The plan's `/nzbs/<category>/...` shorthand does not authorize nested
   collections. `DatabaseStoreCategoryWatchFolder` creates or deletes one queue
   item by immediate child name, and current clients upload one NZB filename.
   V1 therefore permits exactly `/nzbs/<category>/<file>`.
2. Every method that consumes WebDAV `Destination` is forbidden. Any
   `Destination` header is therefore a zero-upstream 400; unreachable rewrite
   code is not added. A tagged-resource `If` URI is rewritten only when it is a
   bounded same-listener URI inside exact `<URL_BASE>/protocol`; all other tags
   are rejected before upstream.
3. Exact SAB/ARR API paths reject a trailing slash. WebDAV root `/protocol/`
   remains the intentional exception.

## Frozen HTTP matrix

### Principal-protected UI relays

Every positive below requires the selected frontend principal. The edge removes
client `x-api-key`, cookie, Authentik identity, `Forwarded`/`X-Forwarded-*`, and
unneeded `Authorization` values before the private hop. It injects the internal
key only for `ui-admin`, never for `ui-view`.

| Methods | Exact mount-relative target | Query contract |
| --- | --- | --- |
| `POST` | `/api` SAB UI actions | exact supported `mode`/`name` keys for add-file, pause/resume, queue delete/priority, and history delete |
| `GET` | `/api/download-nzb` | one required `nzbBlobId` |
| `GET` | `/api/get-health-check-queue` | optional single `pageSize` |
| `POST` | `/api/test-rclone-connection`, `/api/test-usenet-connection`, `/api/test-usenet-pipelining`, `/api/test-arr-connection` | none |
| `GET` | `/api/maintenance/status` | optional single `kind` |
| `POST` | `/api/remove-unlinked-files`, `/api/remove-unlinked-files/dry-run` | none |
| `GET` | `/api/remove-unlinked-files/audit` | none |
| `GET`, `HEAD` | `/view/<nonempty-path>` | one required `downloadKey`; optional single `extension` and `download` |

Every other direct `/api` or `/view` target, extra or repeated control query,
wrong method, and `/protocol/view` is rejected before an upstream call. Range,
If-Range, and response streaming semantics remain intact for an authorized
`/view` request.

The existing browser uses GET for four mutations: queue pause/resume, one-item
queue delete, priority change, and one-item history delete. Those callers and
their tests must move to POST in the same GREEN that activates the policy. A
classifier-only wiring that breaks these actions is not acceptable.

The narrow caller contract is `fetch(url, { method: "POST" })` with the current
query ordering and values unchanged. These single-item/status calls add no
body or content type. Existing bulk queue/history deletes remain distinct POST
JSON calls.

### Independently authenticated protocol API

These routes do not require or inherit a frontend principal. The edge preserves
their public carrier and sends no internal key.

| Methods | Exact target | Scope |
| --- | --- | --- |
| `GET`, `POST` | `/protocol/api` | complete existing SAB-compatible dispatcher |
| `GET` | `/protocol/api/arr/validation` | derived non-secret validation |
| `GET` | `/protocol/api/arr/search-nudges` | read-only report |
| `GET` | `/protocol/api/arr/correlations` | read-only report |
| `POST` | `/protocol/api/arr/events/sonarr`, `/radarr`, `/lidarr` | event ingestion only |

ARR retry/clear, correlation mutation, generic configuration, maintenance,
database, and every unknown `/protocol/api/*` path are zero-upstream 404/405
negatives. Carrier validation remains authoritative in the sealed backend
parser; the edge must not invent a second, wider carrier contract.

The edge performs only header framing checks it can make without consuming the
body: an `x-api-key` header must be present at most once, nonempty, and no more
than 512 characters. It preserves header-only, query-only, form-only, and the
equal header-plus-query shape without adding the internal key. Query/form
repetition, noncanonical names, unequal cross-location values, and form
conflicts reach exactly one private backend request, where the sealed parser
returns the authoritative semantic rejection. The edge never parses multipart
NZB bodies or duplicates the backend carrier parser.

On WebDAV, the edge permits at most one `Authorization` value of at most 8192
characters and forwards it only on that lane. It does not authenticate the
credential: a missing or wrong single value reaches the private ASP.NET Basic
handler, whose 401 behavior is part of the disposable real-backend gate. The
same 8192-character structural ceiling applies to `If`; every `Destination`
value is denied regardless.

### Independently authenticated WebDAV

The edge preserves WebDAV Basic `Authorization` and required WebDAV/range
headers, strips browser authority and API-key headers, and removes exactly the
mount-relative `/protocol` prefix before the private hop.

| Methods | Exact semantic namespace |
| --- | --- |
| `OPTIONS`, `PROPFIND`, `GET`, `HEAD` | root, `/README`, and any existing path below `/.ids`, `/nzbs`, `/content`, `/completed-symlinks` |
| `PUT` | exactly `/nzbs/<category>/<file>` |
| `DELETE` | exactly `/nzbs/<category>/<file>` or a non-root descendant of `/content` or `/completed-symlinks` |

`COPY`, `MOVE`, `MKCOL`, `PROPPATCH`, `LOCK`, `UNLOCK`, root deletion, writes to
`/.ids` or `/content`, unknown roots, and prefix-confused roots are rejected
before proxying. Every `Destination` header is rejected. A bounded WebDAV
tagged-resource `If` URI is rewritten only when it resolves to the same
listener, `URL_BASE`, `/protocol`, and approved namespace; every other tagged
resource is rejected. No approved V1 method requires widening the method
allowlist for those headers.

## Normalization and abuse contract

- Classify the raw mount-relative target before Express-decoded prefix checks.
- Reject empty/non-origin-form targets, controls, fragments, backslashes,
  malformed percent encoding, encoded or double-encoded separators/dot
  segments, repeated slash ambiguity, reserved-root encoding, and targets over
  8192 characters with stable bounded 400.
- Match reserved paths by exact segment, never `startsWith` prefix. Case remains
  exact for route names.
- Reject repeated or oversized security-sensitive headers before proxying.
  Authentik identity fields retain their 256-character bound; the public
  `x-api-key` header retains the sealed 512-character bound.
- Unknown admin/protocol paths, unsupported WebDAV methods, malformed targets,
  unauthenticated UI relays, and wrong WebSocket upgrades make zero private
  upstream calls.
- Proxy failures return bounded stable responses without echoing request target,
  headers, credentials, backend URL, or exception text.
- Edge policy rejections use a bounded JSON object with only `error` and the
  classifier's stable code. A private-hop connection failure uses status `502`
  and `{"error":"upstream_unavailable"}`. Policy 405 responses emit the
  matrix's exact `Allow` value.

## Required RED evidence

1. Retain and extend the pure classifier matrix.
2. Run the real production Express middleware against a uniquely owned capture
   backend. Prove principal decisions, exact target rewriting, key
   strip/injection, Basic-auth preservation, body/range streaming, bounded
   rejects, and upstream call counts for empty/single/nested mounts.
3. Exercise exact WebSocket upgrade path and pre-101 principal rejection against
   a disposable HTTP server.
4. Add a disposable real-backend/client gate for the accepted SAB, ARR, WebDAV,
   and signed-media positives before acceptance; never use production state.
5. Record conclusive failures against unchanged production wiring. The current
   103/103 classifier baseline is orientation only, not production evidence.
6. Obtain independent review of the tests-only RED diff before production
   implementation. Only then implement the minimum complete GREEN.

The RED additionally exercises the real authentication module through actual
loopback sockets. It composes a trusted and untrusted Authentik proxy source,
the inclusive 256/257 identity-header boundary, repeated/malformed identity
headers, and a genuinely signed local session with the capture-backend gate.
The actual `server.ts` entrypoint is imported with a disposable port and driven
by raw WebSocket handshakes, so denial is asserted before 101 rather than by
constructor shape alone.

The RED must fail because current `frontend/server/app.ts` does not import the
classifier, forwards broad decoded prefixes before authentication, preserves a
client key instead of unconditionally replacing it for UI authority, and does
not expose the new `/protocol` ingress. Current `frontend/server.ts` also
attaches `WebSocketServer({ server })` without an exact path gate, so path and
principal rejection occur only after an upgrade.
