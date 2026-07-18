# Transfer v3 source contract

This directory contains the reviewed source contracts used to validate and
export a private SQLite source through Transfer-v3 Phase 3. It does not define
or authorize destination mutation.

## Checked-in evidence

- `sqlite-migration-contract.json` freezes the ordered 49-migration history and
  the reviewed EF `ProductVersion` suffix allowlists.
- `sqlite-source-schema-manifest.json` freezes the raw SQLite physical schema,
  including tables, columns, indexes, foreign keys, triggers, and SQL text.
- `transfer-v3-source-contract.json` freezes the logical transfer shape:
  28 modeled tables and 240 columns, of which 27 tables and 235 columns are
  transferred. `HealthCheckStats` is derived and must be rebuilt rather than
  transferred. `database.import-state` is reserved and excluded.

The contract records every stable keyset and unique-key component with explicit
provider-neutral ordering: SQLite `BINARY`, PostgreSQL `C`, RFC 4122 network
UUID bytes, or signed numeric order. It also records raw storage classes,
nullability, enum domains, maximum Unicode scalar counts, timestamp encodings,
bootstrap rows, blob layout, metadata rules, and the complete application-level
reference-policy matrix. Policy rationales are data in the contract, not
implicit validator behavior. The WorkerJobs polymorphic mapping is explicit:
Kind 1 resolves only to QueueItems, while Kinds 2 and 3 resolve only to
DavItems. The DavItems domain is also bidirectional: Type 1 permits only
SubTypes 101 through 106, and Type 2 permits only SubTypes 201 through 203.

## Phase 1 validator behavior

`TransferV3SqlitePreflightValidator` accepts only a stopped, sidecar-free,
private canonical SQLite copy. It refuses retained `-wal`, `-shm`, or
`-journal` files, pins and fingerprints the database and containing directory,
attaches with `mode=ro&immutable=1`, and rechecks the source fingerprint and
sidecar set before accepting the result. Never point it at the live application
database: first capture WAL correctly into a private canonical database while
the owner is stopped. It validates migration history and the physical manifest
before scanning values. The raw pass does not use EF materialization or OFFSET
pagination. It validates strict UTF-8, U+0000 exclusion, canonical booleans,
numeric and enum ranges, UUID shape and normalized collisions, microsecond-safe
timestamps, target text lengths, normalized uniqueness, bootstrap state,
declared foreign keys, application references, metadata transitions, blob
layout/content inventory (including orphans), and derived health-stat
consistency. Every blob file is fingerprinted by descriptor using device,
inode, size, modification time, and change time around hashing; every opened
blob directory is likewise fingerprinted around its complete enumeration.
In-place content changes and directory-entry replacement therefore fail closed.

UUID identities, normalized unique keys, canonical scan ordinals, and the blob
inventory are kept in an SQLite-owned unnamed temporary database attached as
`scratch`. A tiny private in-memory control connection enables URI handling;
the O(n) state is in `scratch`, uses a bounded suggested page cache, may spill
to disk, and is deleted by SQLite when the non-pooled owning connection closes.
There is no caller-selected validation workspace and no pathname cleanup. The
startup runtime gate requires `TEMP_STORE=1`; validation also fails closed
unless scratch has an empty filename, scratch is writable, and the immutable
source attachment is natively reported read-only. Diagnostics contain only a stable
error code, table, column, real canonical row ordinal, and digest prefix;
source values and secrets are not logged.

Migration history is streamed in canonical order with an expected-plus-one SQL
row limit. The raw byte length and storage class of each ID and version are
checked before managed string materialization. Physical schema validation first
streams `sqlite_schema` with the same expected-plus-one rule and bounded raw
text reads; only after an exact match does it query table-valued PRAGMAs, and it
queries only table and index names from the reviewed manifest. This prevents an
attacker from driving unbounded row materialization or PRAGMA fanout.

Validation accepts 1 through 4096 rows per batch and a positive byte budget;
defaults are 256 rows and 4 MiB. Raw UTF-8 decoding uses a 16 KiB scratch
buffer. Text that must be retained for a key or reference is capped at 64 KiB;
larger input is still hashed and UTF-8-validated before a bounded redacted
failure. These limits bound validator-owned batching and scratch/capture
allocations. They are not a claim that total process memory is capped, and a
single SQLite value may exceed the requested batch-byte budget.

Native descriptor operations are supported only on Linux x64/arm64 and macOS arm64.
macOS x64 fails closed: Apple maps `fstat`, `fdopendir`, and `readdir` to their
`$INODE64` entry points for the 64-bit inode layouts; `fstatat` has the same
inode-layout dependency. The current bindings
use unsuffixed symbols. Do not enable macOS x64 until separate bindings and
native x64 tests prove the matching structure layouts.

## Phase 2 state-safety foundation

Phase 2 defines the exact canonical `database.import-state` values, the
digest-bound finite transition rules, provider-neutral exact-byte CAS behavior,
generic-configuration containment, exact maintenance-argument classification,
and normal SQLite startup refusal when any marker is present. These are safety
foundations only: the public backend and container entrypoint still refuse every
Transfer-v3 command, and PostgreSQL runtime/maintenance remains disabled.

## Phase 3 private source snapshot and sealed stage

Phase 3 retains the validated immutable SQLite attachment and blob descriptors
for one export session, writes all 27 transferred tables plus the complete
accepted blob inventory into a private canonical snapshot, and publishes the
byte-exact manifest last. A separate reader and verifier reopen that snapshot
without following links, validate its framing, digests, schema semantics, and
hard-reference closure, and return an opaque verified-snapshot capability. The
sealed-stage builder accepts only that capability, reconstructs the exact blob
layout, revalidates every byte and identity, and seals files to mode `0400` and
directories to mode `0500`.

No Phase 3 type accepts a target database connection or context, a target blob
store, or a runtime publication service. The stage retains ownership until it
is disposed and exposes only typed read access; there is no ownership-transfer
API. The public backend and container entrypoint still have no successful
Transfer-v3 command, and Phase 3 makes no successful public command or
transfer-completion claim. PostgreSQL runtime, migrations, legacy v2 transfer,
and v3 transfer remain unavailable through the public process boundary.

Stage disposal restores owner-only modes and removes identity-proven entries
descriptor-relatively. Unknown, replaced, nonempty, or externally retained
entries are preserved or reported rather than followed or broadly deleted.
Restart residue audit enumerates only the trusted parent, no-follow opens
nonce-shaped candidate roots only for classification, and reports candidates,
unknown-prefixed entries, and unreadable entries separately. It never
enumerates or opens candidate contents and never deletes anything. This
lifecycle is not a backup feature and makes no cleanup claim after SIGKILL,
daemon loss, or host power loss.

## Still deliberately deferred

Phase 3 does not inspect, connect to, migrate, or mutate an operator target; run
an importer; transition target import state; publish blobs into the runtime
store; transfer stage ownership; expose a successful v3 CLI command; or perform
a real source-to-target transfer. Those operations require a later independently
reviewed Transfer-v3 phase and an explicit PostgreSQL promotion decision.
Do not weaken this contract to accommodate an unreviewed source: regenerate the
evidence, review every schema and policy change, and update the contract and its
exact tests together.
