# NZBDav transfer-v3 Phase 3: complete private source snapshot

Date: 2026-07-13

Status: implemented locally and verified with synthetic fixtures on macOS
arm64 and Linux arm64. Public transfer-v3 and PostgreSQL remain unavailable.

## Objective and phase boundary

Build a complete, deterministic, private, source-side transfer-v3 artifact from
an already stopped, immutable SQLite copy and its validated blob root. Phase 3
ends only when an independent offline verifier can open that artifact without
following links, validate every database frame and blob byte, prove hard blob
reference closure, and construct an importer-owned sealed private stage.

In scope:

- all 27 transferred tables in frozen contract order;
- every physical blob accepted by the existing contract, including validated
  orphans because `includeOrphans` is true;
- exact canonical field, cursor, frame, manifest, and digest rules;
- one retained validation/export session so the source is not validated and then
  reopened unpinned;
- bounded streaming, failure cleanup, mutation detection, and POSIX containment;
- a strict offline reader/verifier and typed sealed stage;
- synthetic fixtures only.

Out of scope:

- PostgreSQL target inspection, connection, bootstrap, or mutation;
- database importer or target blob publication;
- import-state transitions;
- a success/completion marker;
- helper containers or runtime orchestration;
- a successful public `--db-export-v3` or `--db-import-v3` command;
- changes to `Program`, entrypoint v3 allowlists, provider promotion, or runtime
  `BlobStore` behavior.

Phase 4 will require a freshly migrated bootstrap-only target, accept only the
typed sealed stage defined here, apply database rows and blobs, and stop at
`database-verified`. That later state still does not enable PostgreSQL runtime.

This artifact is not a backup feature. Phase 3 adds no scheduling, retention,
rotation, restore command, recovery policy, or runtime backup surface.

The repository is already heavily dirty. Before changing any listed file,
capture its current diff and treat that filesystem version as the baseline.
Use only narrow patches, preserve unrelated edits, and do not reset, checkout,
run broad formatters, stage, or commit files as part of this plan.

## Frozen snapshot file set

The root is a newly created mode-`0700` directory on a supported POSIX platform.
Every regular file is mode `0600`. The allowed files are exactly:

```text
table-001-Accounts.jsonl
...
table-027-RcloneInvalidationItems.jsonl
Blobs.jsonl
manifest.json
```

Table ordinals and names come directly from the embedded source contract. No
case folding, aliases, nested paths, temporary files, hard links, symlinks,
devices, sockets, FIFOs, extra files, or alternate manifest names are accepted.
`manifest.json` is written and durably renamed last. Its presence is the only
snapshot-publication marker; an incomplete root without it is never usable.

The snapshot contains secrets from `Accounts` and `ConfigItems`. Diagnostics
must never include row data, field data, paths below the private root, secrets,
blob bytes, UUIDs, or full digests.

## Canonical database field encoding

Every database row uses `chunked-row-start`, contiguous `field-chunk` frames,
and `chunked-row-end`; the untyped single `row` frame is not used by the Phase 3
exporter. Field count is exactly the contract column count and order.

Each field begins with one marker byte:

- `0x00`: null, with no following bytes;
- `0x01`: non-null, followed by the kind-specific payload.

Payloads are exact:

- UUID: 16 RFC 4122/network-order bytes;
- Boolean: one byte, `0x00` or `0x01`;
- EnumInt32 and Int32: 4-byte signed big-endian two's-complement integer;
- Int64: 8-byte signed big-endian two's-complement integer;
- Instant: the source contract's validated integer representation as 8 signed
  big-endian bytes (`UtcTicks` or `UnixSeconds` remains contract metadata);
- Text: the exact validated UTF-8 SQLite bytes;
- LocalWallTimestamp: 8-byte signed big-endian `DateTime` ticks after strict
  parse as `DateTimeKind.Unspecified`; ticks must remain microsecond-aligned.

Null and empty text are therefore distinct. The field limit includes the marker.
Every emitted chunk is at most 1 MiB and fields may not exceed the manifest's
positive `maxFieldBytes`, itself no greater than the existing 16 GiB ceiling.
No whole large text value is materialized merely to export it: SQLite bytes are
read and copied in bounded chunks from the sequential reader/native column view.

The cursor is derived from the row's exact contract keyset with the existing
cursor codec: UUID network bytes, signed integer, or exact text. Export paging
uses `scratch.scan_ordinals`, retained from preflight, joined back to the same
attached read-only source. Cursors must still strictly increase according to the
contract; ordinals are not serialized as identity.

`HealthCheckStats` remains excluded and is rebuilt by Phase 4. Phase 3 records a
canonical expected derived-table row count and digest in the manifest so the
later rebuild can be compared exactly. The reserved `database.import-state`
configuration row remains forbidden and is never exported.

## Canonical blob bundle

`Blobs.jsonl` is a framed pseudo-table named `Blobs`, sorted by one UUID cursor
component in network-byte order. It contains every physical file in the
validated scratch inventory, including orphans. Cleanup tombstone references
may point to absent bytes exactly as allowed by the source contract; every hard
live reference must resolve.

Each blob row contains:

1. field 0: exactly 40 bytes: nonnegative 8-byte signed big-endian content
   length followed by raw 32-byte SHA-256;
2. fields 1 through N: raw content. Blob pseudo-fields do **not** use the
   database null/value marker. Starting at byte zero, every non-final content
   field is exactly `maxFieldBytes`; the final field is the nonempty remainder.
   An empty blob has exactly one zero-length content field.

The existing 1024-field frame ceiling is retained. A blob requiring more than
1023 content fields fails closed before publication. Each content field is still
split into at most 1 MiB frames.

The blob descriptor in the manifest records:

- `file`, `rows`, `batches`, decoded framed bytes, and table SHA-256;
- physical blob `count` and `totalBytes`;
- the existing inventory SHA-256 over sorted UUID network bytes, 8-byte
  big-endian length, and raw content SHA-256.

Blob export reopens every file descriptor-relatively, rejects non-regular files,
streams and rehashes it, compares length/digest to the retained scratch row, and
rechecks file and directory fingerprints. It recomputes the aggregate and proves
it equals preflight before manifest publication. Runtime `BlobStore` APIs are
never called because some of their recovery behavior mutates malformed entries.

## Canonical manifest

`manifest.json` is compact UTF-8 without BOM, indentation, insignificant
whitespace, or trailing newline, and is at most 256 KiB. Its complete nested
shape and property order are exact:

```json
{"formatVersion":3,"sourceProvider":"Microsoft.EntityFrameworkCore.Sqlite","sourceContractSha256":"<64-lower-hex>","sourceSchemaSha256":"<64-lower-hex>","migrationContractSha256":"<64-lower-hex>","sourceTimeZoneId":"<validated-IANA-or-platform-id>","limits":{"maxFieldBytes":1048576,"maxBatchRows":256,"maxBatchBytes":4194304},"tables":[{"name":"Accounts","file":"table-001-Accounts.jsonl","batches":1,"rows":1,"decodedBytes":1,"sha256":"<64-lower-hex>"}],"derivedTables":[{"name":"HealthCheckStats","rows":0,"logicalSha256":"<64-lower-hex>"}],"informationalReferences":[{"name":"<contract-name>","unresolvedCount":0,"unresolvedSha256":"<64-lower-hex>"}],"blobs":{"name":"Blobs","file":"Blobs.jsonl","batches":1,"rows":1,"decodedBytes":40,"sha256":"<64-lower-hex>","count":1,"totalBytes":0,"inventorySha256":"<64-lower-hex>"}}
```

The golden codec test uses all 27 real table entries and every informational
reference; the one-entry example above documents nesting without weakening the
required cardinalities. Property order is:

1. `formatVersion` (integer 3);
2. `sourceProvider` (exact SQLite provider name);
3. `sourceContractSha256`;
4. `sourceSchemaSha256`;
5. `migrationContractSha256`;
6. `sourceTimeZoneId`, supplied by typed source-capture provenance and validated
   with `TimeZoneInfo.FindSystemTimeZoneById`;
7. `limits` with `maxFieldBytes`, `maxBatchRows`, `maxBatchBytes`;
8. `tables`, exactly 27 entries in contract order;
9. `derivedTables`, exactly the reviewed excluded derived tables;
10. `informationalReferences`, in contract order;
11. `blobs`.

Each table entry has exact `name`, `file`, `batches`, `rows`, `decodedBytes`, and
lowercase `sha256`. Derived and informational entries use exact names, counts,
and lowercase digests. Numeric fields are nonnegative canonical JSON integers.
Unknown, duplicate, missing, reordered, escaped property names, alternate number
spellings, BOM, comments, trailing JSON, and trailing newline are rejected.

`manifestSha256` means SHA-256 of the exact canonical manifest bytes. It is not a
self-referential manifest property. Phase 4 will use that external digest in the
reserved import-state value.

For table and blob entries, `decodedBytes` is exactly the corresponding
`TransferV3TableEndFrame.Bytes`: the checked sum of decoded row field payload
bytes, including database marker bytes or the blob descriptor/content bytes,
and excluding frame JSON, base64 expansion, and line feeds. `sha256` is exactly
the existing table-end digest: canonical header, batch-start/data, and batch-end
JSON lines each followed by LF, excluding the table-end line itself.

Informational summaries preserve the current contract preimage. The hash starts
with the exact UTF-8 reference name. A non-polymorphic unresolved item then
appends owner UUID network bytes and referenced UUID network bytes, sorted by
that pair. A polymorphic item appends owner UUID network bytes, 4-byte signed
big-endian discriminator, and referenced UUID network bytes, sorted by that
triple. The manifest count and digest must match an independent verifier rebuild.

Phase 3 never substitutes exporter-local `TimeZoneInfo.Local.Id`. The export
session requires a typed `TransferV3SourceProvenance` whose database and blob-root
identities match the descriptor-pinned inputs and whose explicit time-zone ID was
captured with the stopped source. Synthetic tests create that provenance
directly; a later helper phase must durably capture and pass it on the source
host. Missing, mismatched, or unknown provenance is a pre-export failure.

The canonical derived-table digest is SHA-256 over rows in contract key order.
For each row append: 4-byte big-endian cursor ASCII-byte length, cursor ASCII
bytes, 4-byte big-endian field count, then for every field an 8-byte big-endian
field length followed by the exact database field bytes defined above. No row
separator, JSON, frame, or platform newline participates. The manifest records
the row count separately.

## Sealed importer-owned stage

The verifier never authorizes import directly from a caller path. It:

1. opens the snapshot root component-by-component with no-follow descriptors;
2. validates root identity, modes, exact file set, manifest, every table, and the
   full embedded source contract: normalized unique keys, declared/application/
   state-aware references, cleanup policies, metadata type/subtype rules,
   bootstrap roots and keys, the reserved-key exclusion, derived expectation,
   and informational digests;
3. creates a new unpredictable mode-`0700` directory beneath a trusted,
   descriptor-pinned parent;
4. descriptor-copies and re-verifies table files and the exact manifest;
5. parses `Blobs.jsonl`, derives `blobs/aa/bb/<lowercase-D-uuid>` itself, writes
   with no-replace descriptor-relative calls, fsyncs, closes, reopens, and
   rehashes every file;
6. proves all hard blob references observed from verified table rows resolve,
   accepts contract-permitted cleanup tombstones, and retains physical orphans;
7. rechecks every identity/fingerprint and seals files/directories to `0400` and
   `0500` where supported;
8. returns an opaque `TransferV3SealedSnapshotStage` exposing descriptor-backed
   read-only table/blob opens and the typed manifest, but no runtime publication
   API or arbitrary filesystem path.

Sealing prevents accidental mutation through this library; it is not claimed to
defend against a hostile same-UID owner able to chmod entries. Disposal and
failed construction remove only nonce-created, identity-proven entries and
surface any deliberate empty residue for operator audit.

## Task 1: manifest and row codecs, strict RED first

Create tests:

- `backend.Tests/Database/Transfer/TransferV3ManifestCodecTests.cs`
- `backend.Tests/Database/Transfer/TransferV3RowCodecTests.cs`

Then create:

- `backend/Database/Transfer/TransferV3Manifest.cs`
- `backend/Database/Transfer/TransferV3ManifestCodec.cs`
- `backend/Database/Transfer/TransferV3RowCodec.cs`

Tests cover every logical type, all 235 transferred column contracts, null versus
empty, strict UTF-8, UUID byte order, integer boundaries, local-wall formats and
microseconds, instant encodings, field/chunk boundaries, giant fields, canonical
manifest bytes, every strict-parser mutation, maximum input size, exact ordering,
and redacted failures.

Extend `TransferV3JsonlWriter` test-first so ending a batch/table returns its
typed end frame without changing emitted bytes. The exporter must obtain exact
counts/digests from the validated writer state, not by reparsing its own output.

## Task 2: retained validated export session

Create tests:

- `backend.Tests/Database/Transfer/TransferV3SqliteExportSessionTests.cs`

Then create/modify:

- `backend/Database/Transfer/TransferV3SqliteExportSession.cs`
- `backend/Database/Transfer/TransferV3SqlitePreflightValidator.cs`
- `backend/Database/Transfer/TransferV3SqliteRawScanner.cs`
- `backend/Database/Transfer/TransferV3SqliteSourceGuard.cs`
- `backend/Database/Transfer/TransferV3BlobSourceGuard.cs`
- `backend/Database/Transfer/TransferV3BlobInventoryScanner.cs`
- `backend/Database/Transfer/TransferV3Posix.cs`

Add `OpenValidatedExportSessionAsync`, which requires explicit typed source
provenance and retains the source guard, attached source, transaction, scratch
key/ordinal/blob/field-integrity indexes, contract, validation summary, typed
source provenance, and an open descriptor plus baseline identity for the blob
root until export finishes. Blob export is relative to that retained root; it
never closes preflight and later trusts a reopened path.

Preserve the existing `ValidateAsync(sqlitePath, options)` signature as a
validation-only path. It uses the shared validation core, carries no export
provenance, returns only the current validation summary, and disposes every
session resource. It cannot be passed to an exporter. No wrapper or overload
synthesizes `TimeZoneInfo.Local`; only the explicit export-session API can mint
an export-capable typed session.

SQLite must not attach the caller pathname after opening the guard. Extend the
source guard to expose a platform-tested descriptor route (`/proc/self/fd/<n>` on
Linux, `/dev/fd/<n>` on macOS) while retaining the original handle, and build the
immutable read-only URI only from that descriptor route. Fail closed when the
route cannot be feature-probed. Verify the attached database contract and keep
the same read transaction open through every table and derived read. Final
transaction completion plus source/blob guard verification occurs before the
manifest temporary file is created. Tests replace the containing directory and
every ancestor between guard open and attach and during export; SQLite must
still read the pinned inode or refuse, never the replacement path.

The descriptor-route helper accepts only the retained integer descriptor and a
hard-coded platform choice; it never accepts an arbitrary route string. On
Linux, open `/proc/self/fd/<n>` for the feature probe without applying the
ordinary `O_NOFOLLOW` pathname rule to procfs's descriptor link. On macOS, open
`/dev/fd/<n>`. In both cases compare `fstat` of the newly opened descriptor to
the guarded source identity; pathname `stat` of `/dev/fd/<n>` is not identity
proof. The only SQLite URIs are
`file:///proc/self/fd/<n>?mode=ro&immutable=1&cache=private` and
`file:///dev/fd/<n>?mode=ro&immutable=1&cache=private`. Because `immutable=1`
disables normal change detection and locking, the stopped-source precondition
and final fingerprint checks remain mandatory.

The blob guard retains descriptor-pinned parent/root handles and baseline
identity. Preflight persists every first/second shard and file identity plus
fingerprint in scratch, not just UUID/length/digest. Export reopens only relative
to the retained root and requires those identities/fingerprints before and after
reading. Any directory or file replacement is rejected even when the replacement
bytes and digest are identical. Tests cover root ancestors, root entry, shards,
and files replaced between validation and export.

The retained scratch schema is explicit and content-free:

- `validated_fields(table_name, source_rowid, column_name, length_bytes, content_sha256)`;
- `blob_first_shards(first_name, fingerprint)`;
- `blob_second_shards(first_name, second_name, fingerprint)`;
- `blob_inventory(normalized_uuid, first_name, second_name, length_bytes, content_sha256, file_fingerprint)`.

Use binary keys/digests, nonnegative length checks, exact 32-byte content hashes,
fixed-width canonical fingerprint encodings, and `WITHOUT ROWID`. Separate shard
tables retain canonical empty first- and second-level directories that cannot be
represented by blob rows alone.

Add an unnamed scratch `validated_fields` table keyed by exact table name,
source rowid, and column name. During preflight, the raw scanner records the
checked byte length and full 32-byte SHA-256 for every non-null text field while
it is already performing strict UTF-8 validation; nulls are represented by
absence plus the frozen column nullability/value scan. Fixed-width kinds are
revalidated directly from their canonical value. Export compares every emitted
text field's length/full digest to this retained index. The index is bounded by
the number of source text cells and stores no field content.

Tests prove one attachment/transaction, unchanged source verification after
export, mutation and replacement rejection, cancellation, correct async cleanup,
no caller-path attach, no live-source/WAL opening, exact source-provenance identity
matching, retained blob descriptors/fingerprints, and no target/provider
connection. Tests also prove full field digests (not diagnostic prefixes) are
retained without content and that validation-only results cannot authorize
export. A session is single-use and serial; dispose after a failed export remains
safe.

`TransferV3SourceProvenance` carries exactly the guarded database identity, the
guarded blob-root identity, and an explicit source time-zone ID. Reject default
or mismatched identities, blank/control-character or unknown time-zone IDs, and
never substitute the exporter host's local zone. The session exposes export only
through one serialized callback over a typed context; it does not expose caller
paths. Its lifecycle is `Ready -> Running -> Completed` or `Ready -> Running ->
Faulted`, with disposal valid and idempotent from every state. A concurrent or
second export fails closed. Before a successful callback returns, verify the
source and complete retained blob inventory, commit the single transaction, and
verify both again. Cleanup orders transaction, connection, blob guard, then
source guard without masking a primary exception.

## Task 3: deterministic 27-table exporter

Create tests:

- `backend.Tests/Database/Transfer/TransferV3SqliteTableExporterTests.cs`

Then create:

- `backend/Database/Transfer/TransferV3SqliteTableExporter.cs`

Export through scratch ordinals in bounded batches, emit exact typed fields and
cursors, compute the derived-table digest, durably close each file, and collect
typed manifest entries. Tests include all 27 tables, zero/multi-batch tables,
composite keysets, current operational tables, reserved-key exclusion, derived
exclusion/digest, two byte-identical exports of the same source/timezone/options,
large text bounded-memory metrics, cancellation and injected read/write/fsync/
ENOSPC failures, and source mutation before finalization.

Large text extraction is concrete: first obtain bounded row IDs plus validated
storage/byte-length metadata, close that reader, and read text as
`substr(CAST(column AS BLOB), offset, count)` slices no larger than 1 MiB inside
the retained transaction. Compare total length and digest to preflight. Never use
`sqlite3_column_blob(...).ToArray()`, `GetString`, or a JSON DOM for an unbounded
field. Metrics report transfer-owned managed/native buffer maxima separately
from SQLite engine-internal page/value memory; no claim is made that SQLite
itself never materializes an expression internally.

This task is table-only and exposes typed table/derived descriptors. It neither
creates the complete snapshot coordinator nor publishes a manifest.

## Task 4: complete blob bundle exporter

Create tests:

- `backend.Tests/Database/Transfer/TransferV3BlobBundleWriterTests.cs`
- `backend.Tests/Database/Transfer/TransferV3SnapshotExporterTests.cs`

Then create/modify:

- `backend/Database/Transfer/TransferV3BlobBundleWriter.cs`
- `backend/Database/Transfer/TransferV3SnapshotExporter.cs`
- `backend/Database/Transfer/TransferV3BlobInventoryScanner.cs`
- `backend/Database/Transfer/TransferV3SnapshotDirectory.cs`
- `backend.Tests/Database/Transfer/TransferV3SnapshotDirectoryTests.cs`
- narrowly required descriptor helpers in `TransferV3Posix.cs`

Task 4 deliberately extends the snapshot-directory internals here rather than
reopening `RootPath` or duplicating directory ownership in the coordinator. Keep
the canonical `TransferV3FileFingerprint` encoding exactly 56 bytes and retain
the Task 2 scratch schema unchanged. Add a separate typed file-stat result from
one `fstat` for mode and link count.

Every data output also records a private receipt containing its create-time
identity, final fingerprint and size, and SHA-256 of the exact complete file
bytes. This raw-file digest is only pre-manifest verification evidence; it is
not the logical frame-table digest and is not added to the canonical manifest.

Reject duplicate `(device,inode)` identities anywhere inside the retained
source blob root, including cross-shard aliases. Do not reject a sole source
inventory entry merely because the inode has an external hard link. Snapshot
output files still require `st_nlink == 1`.

Snapshot-directory failure cleanup becomes identity-aware in Task 4: track each
created file identity, unlink only a currently matching owned entry, detect and
remove identity-proven aliases inside the owned root where safe, and report
unknown replacements or unremovable identity-proven residue without deleting
an entry by name alone. Preserve the exact primary failure and attach sanitized
cleanup evidence.

That conditional-unlink guarantee is for a quiescent private snapshot directory.
POSIX exposes pathname-based `unlinkat`, not an atomic unlink-if-descriptor-
identity-matches operation. An active same-UID actor able to replace an entry in
the final descriptor-check-to-unlink interval is outside the transfer threat
model; the implementation and tests must not claim otherwise.

Tests cover empty, one-chunk, multi-chunk, multi-field, and maximum-bound blobs;
all shard/path rules; orphans; shared IDs; duplicate identity; symlink/FIFO/device
rejection; file/directory/root mutation; replacement with same path; descriptor,
row, table, and inventory digests; cancellation/ENOSPC/fsync failure; bounded
buffers; and no runtime `BlobStore` call.

The coordinator creates the table files and blob bundle in one session and
publishes the canonical manifest only after every data file is durably closed,
source guard/blob fingerprints are unchanged, and all manifest totals match.
End-to-end tests inject failure before, during, and after each table/blob close
and prove the manifest is always last.

Before creating the manifest temporary file, enumerate the root and require the
exact 28 data files. Reopen each no-follow, require regular mode `0600`,
`st_nlink == 1`, the captured identity/fingerprint/size, and its recorded digest;
reject every extra or temporary entry and fsync the directory. Extend the POSIX
stat contract with mode and link count. Reject an oversized blob before emitting
its row-start: `maxFieldBytes` is at least 40, content field count is checked as
`max(1, ceil(length / maxFieldBytes))`, and descriptor plus content fields must
be at most 1024. An empty blob contributes 40 decoded row bytes and its content
hash is SHA-256 of zero bytes.

## Task 5: strict reader, verifier, and reference closure

Create tests:

- `backend.Tests/Database/Transfer/TransferV3SnapshotVerifierTests.cs`
- `backend.Tests/Database/Transfer/TransferV3BlobReferenceIndexTests.cs`

Then create:

- `backend/Database/Transfer/TransferV3SnapshotReader.cs`
- `backend/Database/Transfer/TransferV3SnapshotVerifier.cs`
- `backend/Database/Transfer/TransferV3BlobReferenceIndex.cs`

Use the existing frame parser for offline verification, but bind table name,
field count, column decoding, cursor, frame totals, table totals, and EOF to the
embedded contract and manifest. Feed every decoded hard blob reference into an
unnamed private SQLite validation index. Independently rebuild and enforce all
normalized unique keys, declared and application references, state-aware and
cleanup/tombstone policies, metadata rules, bootstrap roots/config secrets,
reserved-key exclusion, informational digests, hard blob closure, and the
derived-table expectation. Exporter-side preflight is not trusted as proof for
an imported artifact. Only bounded keys, reference facts, and reviewed bootstrap
values may be retained; large row fields remain streaming.

Tamper tests cover every missing/extra/renamed file, mode, symlink/hard-link,
root replacement, manifest mutation, cursor reorder, frame/batch/row/count/
digest corruption, duplicate table/blob, truncated and trailing data, unknown
columns/types, reference failures, permitted tombstones, and redacted errors.

## Task 6: private reconstruction and sealed stage

Create tests:

- `backend.Tests/Database/Transfer/TransferV3SealedSnapshotStageTests.cs`

Then create/modify:

- `backend/Database/Transfer/TransferV3SealedSnapshotStage.cs`
- `backend/Database/Transfer/TransferV3SnapshotDirectory.cs`
- narrowly required descriptor helpers in `TransferV3Posix.cs`

Tests prove private ownership, exact modes, descriptor-relative no-follow writes,
no-replace behavior, reconstructed layout and bytes, close/reopen/rehash, root and
component replacement rejection, sealing, read-only typed access, cancellation
and cleanup at every boundary, residue reporting, and absence of any target DB or
runtime blob publication API.

The exact sealed-stage set is the 27 table files, `Blobs.jsonl`, the byte-exact
`manifest.json`, and one nested `blobs/aa/bb/<lowercase-D-uuid>` regular file for
every manifest blob—nothing else. `Blobs.jsonl` is retained because the manifest
binds it. The stage object owns pinned parent/root and descendant identities and
all cleanup until explicit disposal; Phase 3 exposes no ownership-transfer API.
Sealing chmods files to `0400` and directories to `0500`, fsyncs each changed
file/directory and its parent, closes every writable handle, and then reopens for
read-only verification.

Disposal temporarily restores owner modes through retained descriptors, removes
files and directories bottom-up with descriptor-relative no-follow operations
only after current entry identity matches the owned record, fsyncs parents, and
reports every unremovable identity-proven residue without masking a primary
failure. Add the required recursive `openat`/`unlinkat` helpers and tests for
partial sealing, cancellation, identity replacement, cleanup failure, and
process-restart audit residue. Do not claim cleanup after SIGKILL, daemon loss,
or host power loss.

## Task 7: isolation and documentation canaries

Modify:

- `backend/Database/Transfer/Contracts/README.md`
- `docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md`

Add/retain tests proving:

- `Program` still refuses both v3 forms before provider/path I/O;
- entrypoint still rejects them;
- PostgreSQL runtime/migration/v2/v3 paths remain unavailable;
- `DatabaseTransferService` remains legacy v2 and does not call Phase 3;
- no Phase 3 component accepts a target connection/context;
- no successful public command or completion claim exists.

## Verification gate

After every task, run its focused tests and `git diff --check`. At the end run:

1. all `backend.Tests.Database.Transfer` tests;
2. SQLite source/migration/runtime-gate tests;
3. PostgreSQL refusal/startup/provider-selection tests;
4. backend build;
5. full backend suite;
6. shell entrypoint contract tests;
7. `git diff --check` and a secret/path diagnostic scan.

Use only synthetic private fixtures. Do not touch a live SQLite database, a real
blob tree, a deployed service, ARR/Plex, or any pre-existing PostgreSQL
container. No commit, push, public command enablement, or deployment is
authorized by this plan.
