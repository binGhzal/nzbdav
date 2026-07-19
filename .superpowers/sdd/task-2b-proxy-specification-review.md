# Task 2B Proxy Boundary: Independent Specification Review

**Review base:** `173b743c288f69f9f129e66a09dd4a6caed37023`

**Method:** bounded, read-only inspection of the live frontend server, draft
classifier/tests, WebSocket bridge, current browser callers, Task 2B plan, and
canonical handoff. The seat changed no file, service, container, database, Git
state, Graphify output, or production resource.

## Findings

### P0

None.

### P1

1. `frontend/server/app.ts` does not import or run `classifyBackendRequest`.
   Production forwards every `PROPFIND`/`OPTIONS` and broad decoded
   `startsWith` path before frontend authentication. Unapproved admin paths,
   prefix lookalikes, and legacy WebDAV roots can reach the private backend.
2. A client query `apikey`/`apiKey` or `x-api-key` suppresses internal-key
   injection and is forwarded. UI authority must be checked first; client
   carriers must be removed; the internal key must then be injected exactly
   once only for an approved UI-admin relay.
3. `WebSocketServer({ server })` accepts all upgrade paths. Authentication is
   performed after HTTP 101, without exact `URL_BASE/ws` gating.
4. The draft classifier requires POST for UI mutations, but the browser still
   uses GET for queue pause/resume, one-item queue delete, priority change, and
   one-item history delete. Those callers must migrate atomically with GREEN.
5. Exact API paths currently accept trailing slashes because classification
   removes a trailing slash for segment matching while preserving it upstream.
6. `Destination`/tagged-resource handling is absent, and the server compression
   exclusion recognizes legacy WebDAV roots but not `/protocol`.

### P2

1. Exact WebSocket Origin policy is absent.
2. STRM maintenance callers remain live while the draft classifier denies
   them. Symlink-only removal must precede proxy GREEN as ordered by the handoff.
3. The live unguarded `decodeURIComponent(req.path)` cannot return the required
   stable 400 for malformed targets.

### P3

The pure tests do not prove production wiring, capture upstream call count,
header stripping/preservation, response bodies, or exact `Allow` behavior.

## Required RED coverage

- Generate the complete UI, SAB, ARR, WebDAV, signed-media, health, and
  WebSocket path/method/credential matrix.
- Cover empty, single, and nested `URL_BASE`; trailing slash; prefix confusion;
  malformed, encoded, and double-encoded targets; control/backslash/fragment;
  and the 8192/8193 target boundary.
- Cover local and Authentik principals, client carrier stripping, exact internal
  injection, public-carrier preservation, 512/513 API-key headers, and zero
  upstream calls for every denied class.
- Cover exact WebDAV read/write namespaces, Basic auth, range semantics,
  forbidden methods, resource headers, and foreign targets.
- Cover exact pre-101 WebSocket path/principal/Origin denial with zero backend
  WebSocket connections.
- Exercise the real production middleware with a disposable capture backend;
  pure classifier success is insufficient.

## Chair resolution before tests-only RED

The reviewed matrix uses exact immediate-child NZB writes
`/nzbs/<category>/<file>`, rejects every `Destination` header regardless of
method, rewrites every bounded same-listener tagged `If` URI, and rejects
trailing slashes only on exact SAB/ARR API routes. WebDAV `/protocol/` remains
the intentional root exception. Browser mutation callers retain their exact
query contract and move to bodyless POST atomically with proxy GREEN.

Origin is exactly one `http`/`https` origin whose authority equals `Host`, with
no reliance on forwarding headers. Runtime `URL_BASE` rejects ambiguous
configuration before binding. Edge rejects use stable bounded JSON and exact
`Allow`; semantic public query/form carrier conflicts remain the sealed
backend parser's responsibility so streaming bodies are not consumed twice.

## Evidence hashes

```text
175d83425111c35fb1f810ce1660c1628841334f2b26e9296bfc7f8f8de081bf  frontend/server/app.ts
e2428bd187189b582c3a958077d46e14ced79d47557445d9aa2993b530e4d8de  frontend/server/request-policy.ts
4dd00e3a02b9b35f50fa93c3914dc2b71c9bb7194b1d57c58e0cbbd70e28911d  frontend/server/request-policy.test.ts
f8cce18b41a0b5caa25854ead78ebe7c7e4e36da7248c3512f0bd2a3cef32af0  frontend/server.ts
9b5a4dad8c2686dfc398089e4579d5d0df8ddff88d4447f11b4b2ea1f612c477  frontend/server/websocket.server.ts
5bb6c1f62439a0c4a07214111d9a3b3521fe39634a4d79a1e7aebb4e6f63b295  frontend/app/utils/url-base.ts
2077a95b82d6e1858109fa0a58b6de609391513af99f2b6bf6e0e9c5f4f24f04  frontend/app/routes/queue/components/history-table/history-table.tsx
37c2c44ca4711491463379d078c2414ff682e094c541b17ebab06d8e2e5b6e56  frontend/app/routes/queue/components/queue-table/queue-table.tsx
56d49a2b1e48455284514518e313775c689d35b0f6164d80860828970f84cd21  frontend/app/routes/settings/maintenance/strm-to-symlinks/strm-to-symlinks.tsx
df61fea08fe46ec85d799778e0044fe6889a333b86687e82e0f286f37f1d8fa8  frontend/app/routes/settings/maintenance/recreate-strm-files/recreate-strm-files.tsx
5dcd09ca3d6bd958bfaafc4fc5a2099cb7b6db1e55ac500c6501b567a18ba384  docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md
91e503f25a21bef3d618e0970e38dee6727f267f4b59f633a2eb20b83d647fd7  HANDOFF.md
```
