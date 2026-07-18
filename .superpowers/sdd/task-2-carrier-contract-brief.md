# Task 2B Slice Brief: Freeze the Public API-Key Carrier Contract

## Authority and scope

Work only from this repository root, represented as `.`, on the checkout whose
task base is `df41e0c15504ad87fb2aaa211c59700a26917b7c`. Read `AGENTS.md`,
`HANDOFF.md`, Task 2 of the canonical V1 plan, and the governing design before
editing.

This slice is deliberately narrower than the complete Task 2B proxy work. It
freezes the backend public/protocol API-key parser and reconciles the canonical
plan/design/handoff. Do not edit the production frontend proxy, route policy,
WebSocket implementation, STRM surfaces, PostgreSQL/Transfer-v3 code, services,
containers, databases, Git refs, or production state.

## Pinned client evidence

1. AIOStreams stable `v2.31.1`, commit
   `ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0`, and observed main commit
   `4f0c4aace62d5981f495f42659b1ae4e83764b11` have byte-identical NZBDAV
   integration files. Its SAB request builder always issues `GET`, appends the
   lowercase query field `apikey`, and adds the same value in the literal
   header `x-api-key`. `addurl` and `history` both use that path. It never emits
   a form API-key carrier.
2. Sonarr `v4.0.19.2979` (`4ff1b780010d3d9ec76a4864dce96b6494e9caea`),
   Radarr `v6.3.0.10514` (`7827e5368947f158ad06f757334f5cde6c406411`),
   and Lidarr `v3.1.0.4875` (`350860e524029b7fb4165ed14fbcabb11217ada2`)
   have byte-identical `BuildRequest` lowercase query-carrier logic: exactly
   one lowercase query `apikey`. Their multipart `DownloadNzb` category
   properties differ, but carrier placement is structurally equivalent: the
   key remains in the query and the NZB is in form field `name`. No API-key
   header or camel-case key is emitted.
3. SABnzbd `5.0.4` (`128e0d03d7cc61af7e73b18376b880219fbc3596`)
   documents lowercase query `apikey`. Form support remains a Pinrail
   compatibility carrier already present in the frozen candidate contract; no
   researched current client requires a form duplicate.
4. rclone `v1.74.4` (`5bc93a2a7ab0ebd0a11352bc4968eabeffb18027`)
   uses WebDAV Basic `Authorization`, not the public API-key parser.

Primary evidence links belong in the canonical documentation update:

- `https://github.com/Viren070/AIOStreams/blob/ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0/packages/core/src/debrid/usenet-stream-base.ts#L132-L173`
- `https://github.com/Viren070/AIOStreams/blob/ccc25bc65d3abbc9d0cd61c547b2725bfbe20fe0/packages/core/src/debrid/usenet-stream-base.ts#L273-L347`
- `https://github.com/Sonarr/Sonarr/blob/v4.0.19.2979/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L162-L184`
- `https://github.com/Sonarr/Sonarr/blob/v4.0.19.2979/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs#L46-L53`
- equivalent pinned Radarr and Lidarr sources, plus
  `https://sabnzbd.org/wiki/configuration/5.0/api`.

## Exact contract to freeze

Individually valid public/protocol carriers:

- exactly one HTTP header named `x-api-key` (header-name casing follows HTTP
  case-insensitivity);
- exactly one query field whose name is exact lowercase `apikey`;
- exactly one form field whose name is exact lowercase `apikey`, only for a
  form content type.

One and only one multi-location exception is valid:

- exactly one header plus exactly one query carrier, no form carrier, and the
  two values must be equal under the existing constant-time comparison. This
  is the pinned AIOStreams compatibility shape.

Fail closed with the existing stable malformed-carrier exception for:

- repeated values within any one location, whether equal or conflicting;
- header plus form, query plus form, or header plus query plus form, even when
  every value is identical;
- a conflicting header-plus-query pair;
- noncanonical query/form names such as `apiKey` or `APIKEY`;
- empty values or values longer than the existing 512-character bound.

Missing carriers continue to return null. Keep the internal parser separate
and header-only. Do not add precedence semantics, fallbacks, logging, new public
routes, or new error detail.

## TDD and files

Expected production/test files:

- `backend/Extensions/HttpContextExtensions.cs`
- `backend.Tests/Extensions/HttpContextExtensionsTests.cs`

Expected canonical documentation files:

- `docs/superpowers/plans/2026-07-17-nzbdav-v1-backend-release-plan.md`
- `docs/superpowers/specs/2026-07-17-nzbdav-v1-backend-release-design.md`
- `HANDOFF.md`

First change tests only. Replace the current three-carrier acceptance with
rejection and add exhaustive same-value multi-location rejection cases while
retaining the exact AIOStreams header-plus-query positive. Name or structure
the positives so the *arr query-only and form-only contracts remain explicit.
Run the focused Release test and capture the expected failure against the
unchanged production parser. Only then implement the smallest GREEN change.

Suggested focused command:

```bash
dotnet test backend.Tests/backend.Tests.csproj --configuration Release \
  --no-restore --filter 'FullyQualifiedName~HttpContextExtensionsTests' \
  --logger 'console;verbosity=minimal'
```

After GREEN, run the focused class, affected SAB/ARR authorization tests found
from the repository, a Release warning-as-error backend build, formatter check
for edited C# files, relevant documentation validation, and `git diff --check`.
Do not claim the complete Task 2B proxy matrix or complete backend regression is
sealed by this narrow slice.

## Documentation outcome

Correct the stale statement that every duplicate carrier fails closed. State
the narrow AIOStreams exception and its pinned source evidence, state that all
other repetition/multi-location ambiguity fails closed, and leave production
proxy work blocked on the RED route/method/credential matrix. Also correct the
stale claim that CI lacks container lifecycle smoke: exact-HEAD Actions run
`29658476416` passed the container lifecycle smoke and all stated native
Transfer glibc/musl x64/arm64 jobs at the slice base.

## Commit and handoff

Self-review the diff for permissive fallthrough and secret leakage. Update this
ledger and the canonical handoff with exact commands/results. Commit only this
slice with `git commit -s` after verification. Do not push; the controller will
perform independent review before deciding whether the commit is safe to push.
