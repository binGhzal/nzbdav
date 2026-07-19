# Task 2 protocol client and operator contract

## Scope

Freeze one executable external ingress contract after the public-failure and
internal-key slices. The canonical client base is
`<origin><normalized URL_BASE>/protocol`: for example
`https://host/protocol` at root and `https://host/nzbdav/protocol` for
`URL_BASE=/nzbdav`. Sonarr/Radarr/Lidarr SAB URL Base, AIOStreams NzbDAV URL,
rclone `url`, and every Pinrail script's NzbDAV base input use that full base.
Logical suffixes stay `/api`, `/content`, `/nzbs`, `/.ids`, and
`/completed-symlinks`.

`<origin><URL_BASE>/view` remains frontend-principal-only. There is never a
`/protocol/view` route or reverse-auth exception. Work only in `/opt/pinrail`,
preserve unrelated dirty changes and the SearchNudge quarantine, use RED-GREEN
TDD, and do not stage, commit, push, or touch production.

## RED matrix

Add focused tests before production/docs edits:

- all three Python tools construct root and nested requests from a base ending
  in the exact `protocol` path segment, accepting at most one trailing slash;
- each tool fails before network/filesystem side effects when the NzbDAV base
  is relative, non-HTTP(S), contains userinfo/query/fragment/control text, or
  omits the final exact `protocol` segment (including prefix-confusable names);
- ARR/Plex external bases retain their existing independent rules;
- a static operator-contract test proves current setup, URL_BASE, benchmark,
  README, `.env.example`, and Nginx examples publish only the canonical ingress
  shape and never exempt `/view`, legacy root `/api`, or root WebDAV namespaces;
- Nginx root and nested examples have one exact segment-boundary `/protocol`
  bypass that preserves the path when proxying; their general UI location stays
  authenticated;
- existing real Express/ASP.NET AIOStreams, Sonarr, ARR event, rclone, legacy
  root negative, `/protocol/view` negative, and anonymous `/view` negative
  controls remain green.

Never place actual API-key material in output or fixtures. Record the expected
RED failures.

## Minimum GREEN

Implement one small repository-native Python validator/normalizer reused by the
three scripts, or byte-identical local helpers if import/package constraints
make sharing unsafe. It must return a canonical base without a trailing slash
and require absolute HTTP(S), a hostname, no userinfo/query/fragment, no control
characters or ambiguous dot/double-slash path, and an exact final decoded path
segment `protocol`. Do not change the scripts' relative logical endpoint paths.

Update CLI/env help and artifact-safe display to call the value the NzbDAV
protocol base. Update `README.md`, `.env.example`, `docs/setup-guide.md`,
`docs/url-base.md`, the two benchmark docs, and `examples/nginx/*` with root and
nested examples. Nginx/Auth proxy guidance may bypass only the exact canonical
protocol segment and descendants; it must not bypass `/view`, browser UI, or
legacy root surfaces. Preserve DAV streaming/range settings and WebSocket UI
settings in their correct locations.

Do not rewrite internal controller paths, architecture-only paths, or
`backend/WebDav/StaticFiles/root/README.md` logical namespace documentation
unless executable evidence proves they are operator-facing external URLs.

## Verification and report

Run focused Python tests first, the static docs/example gate, the exact frontend
proxy/ASP.NET/rclone positives and negatives, all Python tooling tests, Python
compile/format checks used by the repository, frontend typecheck, and
`git diff --check`. Write exact commands/counts and a changed-surface audit to
`.superpowers/sdd/task-2-protocol-client-contract-report.md`.
