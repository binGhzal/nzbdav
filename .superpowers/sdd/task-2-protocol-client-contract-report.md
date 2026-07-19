# Task 2E protocol client and operator contract report

Date: 2026-07-19 UTC

## Result

The external client and operator contract is locally GREEN and independently
accepted. The canonical NzbDAV client base is
`<origin><normalized URL_BASE>/protocol`, for example
`https://nzbdav.example.com/protocol` at the origin root and
`https://example.com/nzbdav/protocol` for `URL_BASE=/nzbdav`. SAB, ARR event,
WebDAV, and script paths remain logical suffixes below that base.

`<origin><URL_BASE>/view` remains an authenticated frontend-principal route.
It is not part of the public protocol namespace and is not present in any
reverse-proxy authentication bypass.

## RED evidence

- `python3 -m unittest discover -s tests -p 'test_nzbdav_protocol_base.py' -v`
  initially ran 3 tests and failed all 3 because the shared helper did not
  exist.
- The initial script matrices demonstrated the pre-contract behavior:
  `test_nzbdav_benchmark.py` ran 28 tests and failed with 14 failures and 6
  errors; `test_nzbdav_grab_to_plex_benchmark.py` ran 42 tests and failed with
  3 failures and 1 error. The ARR matrix showed requests and artifact creation
  occurring for invalid bases, a raw invalid-port exception, and the absent
  canonical artifact field.
- `python3 -m unittest discover -s tests -p
  'test_nzbdav_protocol_operator_contract.py' -v` initially ran 4 tests and
  failed with 26 failures: missing root/nested examples, legacy ingress/view
  examples, split Nginx bypasses, nested `^~`, and the absent authenticated
  frontend-principal view boundary.
- Review correction:
  `python3 -m unittest
  tests.test_nzbdav_benchmark.BenchmarkEvaluatorTests.test_run_help_distinguishes_http_suffixes_from_filesystem_paths
  -v` ran 1 test and failed on the stale “absolute URL” help text. The parallel
  static documentation test also ran 1 and failed on the same drift. Both ran
  1/1 GREEN after the wording correction.
- Final path-safety correction:
  `python3 -m unittest
  tests.test_nzbdav_benchmark.BenchmarkEvaluatorTests.test_http_path_cannot_escape_or_reset_the_protocol_base
  tests.test_nzbdav_benchmark.BenchmarkEvaluatorTests.test_http_path_preserves_encoded_literal_percent_in_media_filename
  -v` initially ran 2 tests with 7 failures and 1 error. Raw LF/CR/NUL and
  decoded LF/CR/NUL/DEL were accepted, while a legitimate encoded literal
  percent was rejected. The same command ran 2/2 GREEN after the minimum fix.

No rejected value or credential candidate was printed by these tests.

### Independent-review RED and repair evidence

The initial code review found P0/P1/P2/P3 `0/0/3/1`: effective benchmark paths
were validated lazily, port zero and an IPv6 artifact representation were not
handled correctly, exception chaining retained hidden rejected context, and
the report described literal-percent acceptance too broadly. Tests were added
before repair. The conclusive REDs were helper `2/4`, ARR `3/12`, and benchmark
`14/39`; minimum GREEN then passed helper `4/4`, ARR `12/12`, benchmark `39/39`,
request policy `156/156`, and the complete Python gate `141/141`.

The second code review found P0/P1/P2/P3 `0/0/2/0`: the benchmark validated
surplus explicit parallel paths that execution would never consume, and its C1
control classification disagreed with the production frontend classifier.
Tests again preceded production repair. The final benchmark passed `41/41`,
request policy passed `158/158`, the code-only Python gate passed `143/143`,
and focused code re-review found P0/P1/P2/P3 `0/0/0/0`.

The initial operator review found P0/P1/P2/P3 `0/3/3/0`:

- rclone examples incorrectly mounted a `/protocol/content` subtree instead of
  the full canonical protocol root;
- operative ARR instructions omitted exact URL Base guidance and Lidarr;
- first-run plain-HTTP examples omitted required local-session and cookie
  configuration;
- static tests asserted string presence rather than binding operative blocks;
- benchmark rclone tuning contradicted the setup baseline;
- public setup text exposed private PostgreSQL implementation detail.

The documentation matrix was strengthened before the minimum operator repair.
Final operator tests passed `17/17`, the complete Python gate passed `155/155`,
and focused operator re-review found P0/P1/P2/P3 `0/0/0/0`.

## Minimum GREEN

- `scripts/nzbdav_protocol_base.py` supplies the shared dependency-free
  `normalize_nzbdav_protocol_base(value: str) -> str` API. It emits one fixed
  safe failure message and canonicalizes one optional trailing slash and
  percent-encoded unreserved path segments.
- The helper requires an absolute HTTP(S) URL, hostname, valid port, no
  credentials/query/fragment/whitespace/control text, no empty/dot/dot-dot or
  decoded-separator segments, safe prefix-segment grammar, and a final decoded
  exact case-sensitive `protocol` segment. Malformed and ambiguous double
  encoding fails closed.
- ARR report validation normalizes before API-key validation, network access,
  or artifact creation. Grab-to-Plex normalizes at the beginning of
  `config_from_args`, before secret-file resolution or NZB/output/network work.
  The benchmark validates a supplied base before probes; HTTP requires it and
  filesystem mode may omit it.
- Benchmark HTTP paths are logical suffixes below the normalized protocol base.
  Absolute/scheme-relative resets, query/fragment ambiguity, empty/dot
  segments, decoded separators/C0/DEL controls, and unresolved ambiguous or
  excessive encodings fail closed. Terminal literal encodings `%25`, `%2541`,
  and `%2525literal` are accepted; C1 values accepted by the frontend remain
  accepted for parity. Percent-encoded spaces and Unicode remain valid. Generic
  rclone RC joins remain independent of the NzbDAV base validator.
- Artifacts add `nzbdav_protocol_base` while preserving deprecated compatibility
  aliases: ARR and DFS benchmark `base_url`, and Grab-to-Plex
  `nzbdav_base_url`.
- Both Nginx examples now contain one case-sensitive exact-segment protocol
  regex, path-preserving `proxy_pass`, long stream/upload settings, and no
  WebSocket headers. Their authenticated UI location retains WebSocket headers;
  legacy root namespaces and view are not bypassed.
- rclone mounts the complete canonical protocol base, not a `/content`
  subtree, so `/.ids`, `/completed-symlinks`, `/content`, and `/nzbs` remain
  available through one reviewed remote. Root and nested URL Base examples are
  explicit.
- Sonarr, Radarr, and Lidarr instructions use URL Base `/protocol` at the
  origin root or `<URL_BASE>/protocol` when nested. First-run local plain-HTTP
  examples require a stable 64-hex session key plus the explicit insecure-
  cookie development opt-in; reverse-proxy HTTPS keeps secure cookies.

## Changed-surface audit

| Surface | Task 2 protocol-contract change |
| --- | --- |
| `scripts/nzbdav_protocol_base.py` | New shared strict normalizer and fixed diagnostic. |
| Three `scripts/nzbdav_*` tools | Early normalization, safe URL construction, help text, and additive artifact label. |
| Three existing Python tool tests | Root/nested positives, fail-before-side-effect matrices, suffix safety, and compatibility aliases. |
| `tests/protocol_base_contract_cases.py` | Shared valid/invalid protocol-base fixture matrix. |
| `tests/test_nzbdav_protocol_base.py` | Direct helper contract and no-echo proof. |
| `tests/test_nzbdav_protocol_operator_contract.py` | Static docs/Nginx/ingress/view contract. |
| `README.md`, `.env.example`, setup and benchmark docs | Canonical root/nested client examples, executable first-run auth guidance, one aligned rclone profile, and fail-closed operator guidance. |
| `docs/url-base.md`, `examples/nginx/*` | One exact protocol bypass, authenticated UI/view boundary, path preservation, and correct stream/WebSocket separation. |
| `.superpowers/sdd/task-2-protocol-client-contract-report.md` | This local checkpoint evidence report. |

No backend or frontend production source was edited in this sub-slice. Existing
internal controller paths and architecture-only logical namespace documents
were left unchanged.

## GREEN verification

### Focused Python and operator contracts

- `python3 -m unittest discover -s tests -p 'test_nzbdav_protocol_base.py'`:
  4 passed.
- `python3 -m unittest discover -s tests -p
  'test_nzbdav_arr_report_validation.py'`: 12 passed.
- `python3 -m unittest discover -s tests -p 'test_nzbdav_benchmark.py'`:
  41 passed.
- `python3 -m unittest discover -s tests -p
  'test_nzbdav_grab_to_plex_benchmark.py'`: 42 passed.
- `python3 -m unittest discover -s tests -p
  'test_nzbdav_protocol_operator_contract.py'`: 17 passed.

### Complete Python tooling gate

```text
python3 -m compileall -q scripts tests &&
python3 -m unittest discover -s tests -p 'test_*.py'
```

Task 2E result after code and operator review repairs: 155 passed, 0 failed.
The later container-identity retirement contract added two Python tests; the
final combined checkpoint gate is 157 passed, 0 failed without changing the
Task 2E focused counts above.

### Executable proxy/client gate

From `frontend/`:

```text
npm test -- --run server/request-policy.test.ts server/app.proxy.test.ts server/app.aspnet.integration.test.ts server/app.rclone.integration.test.ts server/entrypoint.production.test.ts
```

The final focused request-policy result is `158/158`. Earlier five-file proxy
evidence passed 551 tests with zero failures. This includes the real
Express/ASP.NET protocol positives and negatives, ARR reads/events, pinned
rclone WebDAV behavior, legacy-root negatives, anonymous view denial, and
protocol-view denial.

- `npm run typecheck`: PASS (`react-router typegen && tsc -b`).
- `git diff --check`: PASS after report creation.
- An intermediate `graphify update .` passed AST-only at 15,712 nodes, 40,913
  edges, and 730
  communities. Eleven known JSON/data sources produced zero nodes and the
  oversized HTML visualization was skipped; `graphify-out/` remains ignored.

The final combined-checkpoint Graphify 0.9.19 AST-only update completed after
the stable documentation diff at `15,770` nodes, `41,044` edges, and `728`
communities; external graph update and query/path/explain canaries passed. The
formal Codex Security diff scan, signed checkpoint, push, and exact remote CI/
container jobs remain pending and are not claimed by this Task 2E report.

## Independent review result and integrity hashes

Final code and operator re-reviews each found P0/P1/P2/P3 `0/0/0/0`.
Checkpoint SHA-256 values for the core executable contract are:

```text
358839ccc114b5a4796bd39b64cb060ecc6fb732ee023426770f687a8e60eafc  scripts/nzbdav_protocol_base.py
922e82a036c9d1bed97b8beae1e2da4e590089a164db7138a030eb64866c86b3  scripts/nzbdav_arr_report_validation.py
078bd3644f70399bb6f522523ba26fc32357a539480dd7d56ad80b6ab8efc0e2  scripts/nzbdav_benchmark.py
bf52cfbc4c046a096ba2411ccbc85c16697e650be67a438c9427489e3158364a  scripts/nzbdav_grab_to_plex_benchmark.py
a11fe22d4a7eda3dc86fe881e177c68f87cf010c7e0f275ff708746fd922f134  tests/test_nzbdav_benchmark.py
72c71e40f3f1f06c2e1bcc7e5861e5fc9a84c546547f2cad5a4fd6ee7c705767  tests/test_nzbdav_protocol_operator_contract.py
45ed3fc3ddc4b18a8f4ec3c912c6f117b23274a4ffbb817561041c936e0e163e  frontend/server/request-policy.ts
80513084b5b75733ea13d2dcf7491de7f2330e8466d966f24054cc7debe82e85  frontend/server/request-policy.test.ts
```

No local Docker/container gate was run. The executable tests used only uniquely
owned disposable fixtures and loopback processes.

## SearchNudge quarantine integrity

```text
998f04a496b0948283a76cfb2b05a94cd4e739b153305fbefbe9c234764f228b  backend/Services/ArrSearchNudgeService.cs
f90a34332d4e3cfba47d9a0931bd1bb0f22ee588fc59c20224c26fbeb3a69eca  backend.Tests/Services/ArrOperationsServiceTests.cs
8bc5d01619f19a1ba291c3f546f5ce3c706ba988a9a194a9c7c9bcfcaeeb9def  backend.Tests/Services/ArrSearchNudgeServiceTests.cs
```

## Scope guard

No Git staging, commit, push, ref change, Docker mutation, production host,
service, container, database, blob tree, Figma file, release, or deployment was
performed by this sub-slice.
