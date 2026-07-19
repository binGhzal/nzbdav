# Task 2B Proxy Boundary: Independent Security Review

**Review base:** `173b743c288f69f9f129e66a09dd4a6caed37023`

**Method:** bounded, read-only review of frontend HTTP/WebSocket/authentication
code and backend public/internal authorization boundaries. The seat changed no
file, service, container, database, Git state, Graphify output, or production
resource.

## Findings

### P0

None.

### P1

1. The production listener never executes the draft classifier. Broad proxying
   before `authMiddleware` leaves unapproved ARR mutations and internal Base
   API controllers reachable when a matching key is supplied.
2. All WebSocket paths upgrade before authentication. Any authenticated path
   opens the privileged backend bridge, whose first frame carries the internal
   key.

### P2

1. Decoded prefix matching admits lookalikes and mishandles malformed encodings.
   `URL_BASE` normalization is duplicated and does not reject dot, backslash,
   encoded, or query-like configuration.
2. The frontend has no explicit carrier/header policy. API keys, cookies,
   Authorization, Authentik identity, forwarding headers, `Destination`, `If`,
   and hop-by-hop headers can reach upstream. Backend carrier parsing itself is
   correctly bounded and fail-closed.
3. ARR public authorization throws outside the bounded Base/SAB envelopes; its
   final sanitized 401/500 contract belongs to Task 2C after the proxy boundary.

### P3

SAB version remains intentionally unauthenticated inside its dispatcher, which
permits low-value backend fingerprinting. The frozen V1 public-key scope retains
the complete dispatcher, so this is recorded residual behavior, not widened in
this slice.

## Required zero-upstream RED coverage

- UI principal absence, wrong Authentik source/app, every unlisted route or
  method, key/query/form conflicts, prefix confusion, and security-header
  stripping before exact internal injection.
- Protocol carrier boundaries and proof that a valid public carrier never
  causes internal-key injection.
- Bare/based/prefix-confused/encoded targets and target/header size boundaries.
- Every forbidden WebDAV method; every `Destination` header because its
  consuming methods are forbidden in V1; invalid or cross-boundary tagged
  `If` resources.
- Every WebSocket upgrade except exact `<URL_BASE>/ws` with no query, valid
  principal, and accepted Origin. Rejections create no backend socket.

## Chair resolution before tests-only RED

The phrase “key/query/form conflicts” above is not authority to introduce a
second body parser at the edge. The sealed backend parser remains the sole
semantic carrier authority. The edge rejects only raw repeated, empty, or
oversized `x-api-key` headers it can inspect without consuming a streaming
body. Query/form repetition, spelling, and cross-location conflicts are
preserved without internal-key injection to exactly one private backend call,
where the sealed parser rejects them. A disposable ASP.NET gate must prove the
end-to-end semantic response before acceptance.

The Origin rule is also frozen before implementation: exactly one parsed
`http`/`https` origin, no credentials/path/trailing slash/query/fragment, and
authority equal to the request `Host`. Forwarding headers are never authority
for this decision. These resolutions narrow the edge and do not widen any V1
route.

## Evidence hashes

```text
f8cce18b41a0b5caa25854ead78ebe7c7e4e36da7248c3512f0bd2a3cef32af0  frontend/server.ts
175d83425111c35fb1f810ce1660c1628841334f2b26e9296bfc7f8f8de081bf  frontend/server/app.ts
e2428bd187189b582c3a958077d46e14ced79d47557445d9aa2993b530e4d8de  frontend/server/request-policy.ts
4dd00e3a02b9b35f50fa93c3914dc2b71c9bb7194b1d57c58e0cbbd70e28911d  frontend/server/request-policy.test.ts
9b5a4dad8c2686dfc398089e4579d5d0df8ddff88d4447f11b4b2ea1f612c477  frontend/server/websocket.server.ts
578d4fad0ac337d04ef5740822ad0c70f679fa7c46c7b0e72f432ad355a946b1  frontend/app/auth/authentication.server.ts
5bb6c1f62439a0c4a07214111d9a3b3521fe39634a4d79a1e7aebb4e6f63b295  frontend/app/utils/url-base.ts
3071da818fe854aaa543ac69be6c7fef4bc3b57963c79e195fa3bd3ebd2954c0  backend/Extensions/HttpContextExtensions.cs
522e69fe9dcca9c6c4a75cf599b4e0d136485fdb14a398bb9403de24941509e0  backend/Api/Controllers/BaseApiController.cs
3204122c2e60ecd3952115155890e263716ecb5c055ad9f19690e0a3c2f42ee6  backend/Api/Controllers/Arr/ArrOperationsController.cs
6270aae569b85d5efec072bb2b1f796017b588f8a5cddd4308ab9a8f22e20b8a  backend/Api/SabControllers/SabApiController.cs
98043fab0bdd97a186bcc18d212dea2da0d539265b3c81e79d19daa84c799d0d  backend/Program.cs
```

The SearchNudge quarantine was untouched during review:

```text
998f04a496b0948283a76cfb2b05a94cd4e739b153305fbefbe9c234764f228b  backend/Services/ArrSearchNudgeService.cs
f90a34332d4e3cfba47d9a0931bd1bb0f22ee588fc59c20224c26fbeb3a69eca  backend.Tests/Services/ArrOperationsServiceTests.cs
8bc5d01619f19a1ba291c3f546f5ce3c706ba988a9a194a9c7c9bcfcaeeb9def  backend.Tests/Services/ArrSearchNudgeServiceTests.cs
```
