# NZBDav Transfer-v3 Phase 4 Design

**Status:** Approved for implementation planning on 2026-07-14

**Date:** 2026-07-14

**Last implementation reconciliation:** 2026-07-16T22:14:00+04:00. Current
execution state is recorded in [`HANDOFF.md`](../../../HANDOFF.md) and the
[Phase 4 implementation plan](../plans/2026-07-14-nzbdav-transfer-v3-phase-4.md).

## Summary

Transfer-v3 Phase 4 imports the typed, sealed Phase 3 snapshot into one freshly
migrated, bootstrap-only PostgreSQL target, builds and verifies a private
target-side blob stage, independently verifies the committed database, and
atomically changes the reserved import state from `importing` to
`database-verified`.

Phase 4 does not publish blobs to the runtime blob root. It does not write the
external migration-completion marker, enable the PostgreSQL runtime, add public
commands, perform cutover, or touch an existing database or blob tree. The
successful output remains deliberately non-runnable until a later phase
rechecks the stopped source, publishes the sealed blobs, writes the external
completion marker, and explicitly enables the target.

The database hot path uses a hybrid direct-COPY design. An ordinary verified
batch that fits the Phase 4 owned-memory budget uses Npgsql binary COPY. A
batch promoted to a private disk spool uses PostgreSQL text COPY with bounded
streaming and exact escaping. Entity Framework is not used to materialize or
insert transferred rows.

This checkpoint is narrower and newer than the broad provider-migration plan.
For Phase 4 it supersedes that plan's provisional session-temporary-table
reconstruction and its combined blob-publication/completion-marker wording.
The approved boundary is private file-backed spill plus direct COPY, private
blob staging, and a hard stop at `database-verified`. The provider plan must be
amended to this boundary after this design is approved; its later cutover work
is not silently pulled into Phase 4.

## Inputs and trust boundary

The importer accepts only:

- an open `TransferV3SealedSnapshotStage` produced by Phase 3;
- an unopened `TransferV3PostgreSqlTargetDescriptor` built from a private
  connection string/options value whose target schema, server patch, encoding,
  time zone, ownership, server logging, and environment contract pass the
  fail-closed PostgreSQL policies;
- a descriptor-pinned, operator-owned POSIX staging parent on the future target
  configuration filesystem;
- a required positive `MaxPostgreSqlTextPayloadBytes` value that the operator
  has measured and tested with the intended migration container, Npgsql build,
  PostgreSQL patch, and resource limits;
- a required positive `MaxPhase4StagingBytes` logical-write ceiling, with no
  default, that bounds the largest private table spool or target blob stage the
  operator permits on the trusted staging filesystem; and
- a cancellation token.

It does not accept a source snapshot path, a source SQLite path, a caller-supplied
table name, a caller-supplied blob-relative path, a runtime blob root, or a
replace/overwrite flag. It also does not accept a caller-opened or caller-owned
`NpgsqlConnection`. Table, column, key, and path names come only from the
embedded reviewed contracts.

The canonical manifest bytes are copied from the sealed stage and hashed once.
That lowercase SHA-256 digest is the only digest permitted in the import-state
transitions and the target blob-stage receipt. Manifest bytes, row contents,
field contents, source or target paths, credentials, API keys, UUIDs, and full
connection strings must never enter Phase 4-owned logs, metrics, traces, or
returned exception messages. After the server diagnostics preflight, manifest,
row, field, path, API-key, UUID, and digest material must not enter the
PostgreSQL server log. Before that post-authentication preflight, PostgreSQL may
log unavoidable connection identity or authentication metadata such as server,
database, role, client address, fixed application name, and an authentication
failure. Phase 4 never sends transfer payloads or the manifest digest before
preflight and does not claim that a client can retroactively suppress those
server-owned connection records.

The source sealed stage is a non-owning borrow. Phase 4 closes every read it
issues but never disposes the stage or deletes its tree; the caller retains
ownership so a failed disposable target can be recreated from the same source.
The caller must keep the stage open and must not read it concurrently or dispose
it until the Phase 4 call returns. A violation fails the target closed but does
not grant Phase 4 cleanup authority over the source stage.

The staging-parent input is opened from one absolute path exactly once, before
any target connection or filesystem mutation. The open walks without following
symlinks and accepts only a directory whose owner UID equals `geteuid()`, whose
owner bits contain `0700`, and whose group/other write bits `0022` are clear.
Group/other read or execute bits are permitted. The retained object stores only
the directory descriptor and its device/inode identity, never the path. Pathname
replacement after that open is therefore defined as descriptor pinning: later
operations continue to address the original retained directory and cannot be
redirected to the replacement. Phase 4 does not claim to detect a replacement
of a path it deliberately no longer retains. Before every duplicate or capacity
query it re-stats the retained descriptor and fails closed on identity, owner,
or exact mode drift. Duplication, stat/capacity queries, and disposal are
serialized so a concurrent close cannot turn a recycled descriptor number into
authority. Every duplicate uses atomic `fcntl(F_DUPFD_CLOEXEC, 0)`—command 1030
on Linux x64/arm64 and 67 on macOS arm64—and must retain `FD_CLOEXEC`. Linux
invokes `fcntl` directly. Because the [Apple arm64 ABI places variadic arguments
on the stack](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms),
macOS invokes the pinned .NET 10.0.9 nonvariadic
`libSystem.Native!SystemNative_Dup` bridge; the [frozen runtime source performs
the same atomic `fcntl(F_DUPFD_CLOEXEC, 0)` operation and retries
`EINTR`](https://raw.githubusercontent.com/dotnet/runtime/v10.0.9/src/native/libs/System.Native/pal_io.c).
A fixed-three-argument `libc!fcntl` P/Invoke on Darwin and every
`dup`-then-`F_SETFD` fork/exec race are prohibited.

The owner UID is decoded as an unsigned 32-bit value at byte 28 on Linux x64,
byte 24 on Linux arm64, and byte 16 on macOS arm64. Available staging capacity
comes only from `fstatvfs` on the retained descriptor and is exactly checked
`f_bavail * f_frsize`; zero fragment size, overflow beyond `long.MaxValue`, an
unsupported ABI, or any syscall failure fails at the POSIX boundary. Linux
x64/arm64 use unsigned 64-bit `f_frsize` at byte 8 and `f_bavail` at byte 32 of
the 112-byte layout. macOS arm64 uses unsigned 64-bit `f_frsize` at byte 8 and
unsigned 32-bit `f_bavail` at byte 24 of the 64-byte layout. There is no path,
`statvfs`, or `f_bfree` fallback.

These shared `TransferV3Posix` additions retain the existing low-level raw
internal exception behavior so Phase 1-3 does not acquire a Phase 4 dependency.
Only `TransferV3Phase4StagingParent` catches those failures and maps them to the
fixed `phase4-posix` boundary before they can leave Phase 4.

The Phase 4 options object takes ownership of the staging-parent descriptor
only after all constructor validation and allocation succeeds. Constructor
failure leaves ownership with the caller. The options object is disposable and
preallocates one sealed, non-copyable consumed-options owner. `Consume()` and
`Dispose()` race through one atomic exchange: one consume may transfer that
same owner without a post-transfer allocation; if disposal wins it closes the
parent; every later consume fails with the fixed argument code; and disposal
after a successful consume cannot affect the transferred parent. The consumed
owner is itself disposable and owns the staging parent until the coordinator
finishes with it. It is single-owner/non-concurrent, disposal is idempotent, and
property use after disposal fails with the same fixed argument code. Null
objects, empty/relative/non-POSIX-root staging path shapes, NUL or `.`/`..`
components, and nonpositive numeric options are argument failures; an absolute
path that cannot be opened or trusted and an unsupported ABI are POSIX failures.

The staging ledger has one positive explicit ceiling, starts at zero, and
permits exactly one bounded stage scope at a time. A scope debit is exactly
checked `logicalBytes + 512 * entries`; it tracks logical bytes and entry count
independently, permits `(positive, 0)` incremental writes and `(0, positive)`
entry creation, rejects negative or `(0, 0)` values, and occurs before the
corresponding filesystem mutation. There is no pairwise aggregate `Release`
API that a stale caller could apply to another writer after an ABA sequence.
Instead, the scope releases its complete aggregate exactly once only after the
caller has proven the entire work/blob scope absent and synced its parent. A
stale/released scope cannot affect a later scope, and cleanup residue remains
charged. The sealed successful blob scope remains charged because its tree
still exists; stage disposal alone is not proof of removal. Bad numeric/options
input, an already-consumed/disposed option, or an operator staging-ceiling
refusal maps to `phase4-argument`. Unsupported ABI, syscall/capacity failure,
UID/mode rejection, or retained-descriptor drift maps to `phase4-posix`.
Accounting overflow, stale/double scope release, duplicate allocation marking,
or a required managed reservation that exceeds the frozen ceiling maps to
`phase4-unexpected`.

The target database and staging parent must remain in one explicit quiescent
operator window for the whole call: no runtime, migrator, administrator, second
importer outside the cooperative lock, DDL, manual DML, or filesystem mutation
may act on them, and the PostgreSQL postmaster, endpoint, DNS resolution,
primary role, and cluster topology may not restart, fail over, or change.
Relation locks and the advisory lock enforce cooperating data writers, but
PostgreSQL cannot make an uncooperative owner or superuser harmless and table
locks do not serialize replacement of a trigger function. Phase 4 fails on
advisory/table-lock contention or observed drift, but it cannot prove that an
uncooperative owner will stay away. The operator must guarantee the window; the
design does not claim protection from a hostile administrator or split brain.

Before opening a target connection, the Phase 4 connection policy parses the
Npgsql connection string and requires `Persist Security Info`, `Log Parameters`,
`Include Error Detail`, and `Include Failed Batched Command` all to resolve to
`false`; any `true` value is rejected rather than rewritten. Every Phase 4-owned
connection is pooling-disabled and parameter logging remains prohibited. The
descriptor also requires exactly one nonempty `Host` entry, one separately
explicit port in `1..65535`, and explicit nonempty database, username, and password values,
`Target Session Attributes=any` (the absent/default single-host value),
`Load Balance Hosts=false`, `Multiplexing=false`, and `Enlist=false`.
Npgsql 10.0.3 rejects a non-`Any` target-session attribute for a single host, so
Phase 4 proves primary/read-write state with the server checks below instead of
requesting driver routing. Comma-separated/multi-host routing, failover, load
balancing, and an inferred target are rejected before opening. This is in
addition to, not a replacement for, the existing provider, credential,
time-zone, ownership, disposable-target, and environment checks.

Connection keys are compared only after Npgsql canonicalizes them. A raw Npgsql-
supported alias is accepted when it maps to one allowlisted canonical key with
the exact required semantic value; any disallowed canonical key is rejected.
The descriptor accepts one DNS name, IPv4 literal, bare or bracketed IPv6
literal, rooted Unix-socket path, or abstract Unix-socket name. It rejects commas
and Npgsql's embedded `host:port` grammar before build, but does not resolve DNS
or attest loopback. The owned Task 20 runner alone generates and proves the
actual loopback route.

Phase 4 is currently executable only by the Task 20 runner against its owned
loopback PostgreSQL instance. Its exact reviewed authentication/transport
contract is `Client Encoding=UTF8`, `SSL Mode=Disable`,
`SSL Negotiation=Postgres`, `GSS Encryption Mode=Disable`,
`Require Auth=ScramSHA256`, and `Channel Binding=Disable`. Passfiles,
certificate/key/root-certificate inputs, integrated authentication, and every
connection key outside the reviewed Task 4 allowlist are rejected before the
data source is built. This deliberately narrow contract is not a recommendation
for a remote production database. Phase 5 must perform a new transport and
credential review before it can broaden the private helper-only target.

The private normalized connection policy sets exact finite defaults of
`Timeout=5` seconds, `Command Timeout=300` seconds, and
`Cancellation Timeout=2000` milliseconds and rejects zero, negative, infinite,
or caller-selected alternatives. Npgsql's zero cancellation timeout means an
infinite wait after command cancellation, so it is never inherited. Every
command and COPY operation also receives an explicit finite timeout appropriate
to that bounded operation rather than inheriting an unbounded driver value.
Commit reconciliation further overrides commands with its shorter remaining
deadline, as defined below; the 300-second ordinary default never expands
reconciliation.

Every bounded Phase 4 lifecycle uses one `TransferV3PostgreSqlDeadline` created
from exactly one `TimeProvider.GetTimestamp()` sample; it never reads wall-clock
time. Durations must be positive and no greater than exact
`TimeSpan.FromMilliseconds(0xfffffffe)`. Invalid arguments fail before provider
access, while timestamp, frequency, elapsed-time, or timer failures map to the
fixed unexpected boundary. Remaining time is clamped to the original interval
and zero, atomically retains the minimum ever observed, and can never increase
under concurrency or a regressing provider.

A caller cannot independently sample a command timeout and cancellation token.
It requests one disposable command fence that samples remaining time once,
integer-ceiling rounds and caps the positive command timeout, and owns a source
scheduled for that same remaining interval on the original `TimeProvider`.
Exact expiry returns timeout one plus a synchronously cancelled source without a
timer; the caller must refuse provider work. The source is cooperative rather
than a physical exact-tick cutoff, so every provider race is followed by an
authoritative deadline recheck before its result can be accepted.

The pre-open policy also requires the exact fixed ASCII application name
`nzbdav-transfer-v3-phase4` and writes an explicit empty Npgsql `Options` value
into the private normalized builder. Exact Npgsql 10.0.3 source and runtime
behavior show that `Build()` clones through the connection string and
canonicalizes that empty value back to `null`; therefore the built data source
must expose no startup options (`null` or empty), without private reflection.
The policy rejects every nonempty
ambient value that exact Npgsql 10.0.3 can read during open: `PGUSER`,
`PGPASSWORD`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT`,
`PGCLIENTENCODING`, `PGTZ`, `PGOPTIONS`, `PGTARGETSESSIONATTRS`,
`PGSSLNEGOTIATION`, `PGGSSENCMODE`, `PGREQUIREAUTH`, and `PGAPPNAME`.
Explicit password authentication prevents `.pgpass` fallback and disabled SSL
prevents default certificate-file loading; the dedicated runner additionally
gives every ordinary Phase-4-executing child fresh runner-owned empty mode-0700
`HOME` and `APPDATA` directories. Those two variables are permitted process-home
bindings, not Npgsql configuration variables; one owned completion canary uses
a runner-owned nonempty home to prove its invalid default files are inert.
Merely omitting a connection-string key is not accepted as proof
that an Npgsql environment/default-file fallback is inert. Any other application
name, startup option, credential source, or transport source is rejected before
a connection is opened, so caller/environment-controlled startup metadata
cannot reach PostgreSQL before the read-only logging preflight.

The descriptor copies and normalizes the still-unopened private value once,
never exposes it again, and builds one private, nonpooling `NpgsqlDataSource`
whose exact safe ASCII name is `nzbdav-transfer-v3-phase4`. It uses
`NullLoggerFactory`, explicitly disables parameter logging, and calls one
`ConfigureTracing` block whose command, batch, and COPY filters return `false`
and whose physical-open tracing switch is `false`. The restore-time NuGet range
is the exact closed range `[10.0.3]`, and restored assets must carry the reviewed
package content hash. Runtime gates require assembly version `10.0.3.0` and the
full informational version
`10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc`; null, malformed,
prerelease, different-version, or different-build metadata fails before data-
source construction. Any package content/source-build change invalidates this
review and fails before opening.
The descriptor exclusively constructs every import, verification, failed-state,
and commit-reconciliation connection from that data source. Phase 4 owns every
data source and connection, closes/disposes them only after the lifecycle proofs
below, and exits its dedicated helper without further disposal when a provider
call is abandoned. Later connections never derive
credentials or options from an opened connection, so
`Persist Security Info=false` cannot make reconciliation impossible.

The exact Npgsql 10.0.3 source review also freezes a limitation rather than
overclaiming around it. The tracing filters suppress `ActivitySource` spans,
but `NpgsqlCommand` independently forwards command text to the separate
`Npgsql.Sql` `EventSource` whenever an in-process `EventListener` or EventPipe
consumer enables it; Npgsql exposes no data-source switch for that path. The
approved guarantee therefore covers every Phase 4-owned sink and the dedicated
helper process with no injected diagnostics observer. The helper is launched
with .NET diagnostics disabled, rejects startup-hook/profiler/diagnostic-port
injection, and registers no `EventListener`. All command text is fixed reviewed
SQL with values carried only in parameters or COPY payloads, so no credential,
manifest digest, row, field, API key, UUID, path, or blob byte is embedded in
command text. A deliberately injected listener/profiler is outside the software
trust boundary; Phase 4 does not claim to make Npgsql's provider-owned
`EventSource` disappear. Replacing or privately patching Npgsql is not part of
this phase.

Npgsql 10.0.3 likewise exposes no metrics-disable switch. An intentionally
installed private test `MeterListener` can therefore observe Npgsql instrument
metadata, numeric values, the fixed data-source name and `postgresql` system
tag, generated loopback host/port, and fixed connection-state literals. Those
are the complete allowlisted provider-owned metrics fields; any payload,
credential, path, digest, exception, arbitrary tag/value, or canary fails. The
capture remains private and Phase 4 does not claim that zero Npgsql metrics are
emitted.

Opening and validation use an explicit ownership handoff. An instance-scoped
internal provider-operations adapter is the sole deterministic test seam for
unopened connection creation, open, validation, connection disposal, and data-
source disposal; production delegates directly to the exact Npgsql operations,
and no process-global hook exists. Before acquiring a provider resource, the
descriptor allocates one unpublished `TransferV3PostgreSqlOpenAttempt` owner
shell. Under the descriptor lifecycle gate it then creates one connection from
the private data source, attaches that connection through an allocation-free,
nonthrowing direct assignment, and increments the prevalidated lifecycle lease
count as the publication linearization point. Connection-state validation moves
into the attempt before the open provider call, so an invalid returned connection
remains attempt-owned and caller-deadline closeable. The caller therefore holds
the owner before it invokes or awaits `OpenAsync`; allocation failure, open
success, fault, cancellation, or late completion can never create an ownerless
connection or registered ownerless lease. The attempt exposes no payload-capable connection to the
coordinator. After open, it validates diagnostics, time zone, environment,
server state, and target identity and can then be consumed exactly once into a validated
`TransferV3PostgreSqlSession`; connection and lifecycle lease transfer together
without an unowned interval. If validation fails, the caller still owns the
attempt and closes it under the first coordinator-owned cleanup deadline; an
open helper never starts a hidden second lifecycle fence. The validated session
is the only owner of its connection. Access through the session fails after
quarantine, and Phase 4 code may not retain or use a raw connection after
quarantine. Quarantine cannot revoke a raw reference already retained by a bug
or cancel an already-running provider call; abandoning such a call requires the
dedicated helper to exit. An abandoned open permanently quarantines its attempt,
retains the descriptor's active-attempt registration, forbids descriptor
disposal or further cleanup awaits, and cannot publish a late session; the
helper exits immediately. Open, validation, close, abandonment, and their races
form one linearizable state machine. Only the first open, first validation, and
one close provider call may start; repeated/concurrent open or validation and a
concurrent close fail with fixed `phase4-unexpected`. Repeated close after proven
success is a no-op returning the same success; a sequential close retry after a
catchable fault is legal only under the same live deadline; repeated close after
abandonment returns the same cleanup result without awaiting. First abandonment
of an owned attempt wins, repeated abandonment is a no-op, and abandonment after
close or session transfer throws fixed `phase4-unexpected`. When abandonment
wins a race, late provider completion enters only the quarantined non-publishing
terminal; the coordinator records `deadline-abandoned-provider-task` and helper
exit. A nonlogging observer consumes and drops any late raw result or exception;
the abandoned public operation can complete only with fixed
`phase4-unexpected`, never a fabricated session or raw detail. No transition
starts a second provider call or duplicates/releases the lifecycle lease twice.
Session/attempt bounded close owns exactly one connection `DisposeAsync`
operation, which both closes the nonpooled physical connection and disposes its
wrapper. It derives a non-caller operation fence from the supplied authoritative
deadline; callers cannot inject a token from another deadline. Close is legal
only from a quiescent owned state, and a session becomes quarantined as soon as
close wins. A catchable retry must reuse the same live deadline object, and its
later success cannot erase the coordinator's already-recorded helper-exit fact.
Retry sampling reserves ownership before invoking the potentially re-entrant
time provider and revalidates state after sampling. Fence creation occurs while
the owner is in a preparation state; the provider may start only through an
owner-gated preparation-to-running transition, so abandonment during fence
creation cannot be followed by a provider call. Proven provider disposal is
published and the lifecycle lease is released before operation-fence disposal
or any other re-entrant cleanup. A later fence-disposal fault is contained and
cannot downgrade success or authorize a second provider call.
The close provider boundary includes invocation, `ValueTask` status inspection,
conversion, and await; a catchable fault at any of those stages is the same
fixed close failure and retains the lifecycle lease for a same-deadline retry.
Every component that owns an `NpgsqlTransaction` must rollback and dispose that
transaction first under the same supplied deadline.

Ordinary open validation assigns the explicit 300-second timeout before each
command. Each query explicitly disposes its reader and then its command. Once a
provider, read, or cancellation primary is captured, both disposals are
best-effort and cannot replace that primary; when the query otherwise succeeds,
the first disposal failure is sanitized as a PostgreSQL command failure even if
it is an `OperationCanceledException` carrying a now-canceled caller token. It
must not be misclassified as caller cancellation. Relying on `await using`
unwinding is insufficient because cleanup can replace the original exception.
Reconciliation uses a separate validation path: every logging, environment,
server-setting, time-zone, and identity command receives a newly created command
fence capped by that ordinary maximum and refuses an expired fence. It rechecks
the authoritative reconciliation deadline only after command success and before
accepting the result. After a provider primary it captures and sanitizes that
primary immediately, best-effort disposes the command fence, and performs no
secondary authoritative-deadline sample. One cancellation token or one timeout
sampled for the whole multi-command preflight does not satisfy this contract.

The descriptor lifecycle lease is released exactly once and only after proven
connection disposal. Successful bounded close returns no cleanup codes. A catchable
close fault reports `connection-close-failed`; an already-expired deadline starts
no close and reports `cleanup-deadline-exceeded`; a close call abandoned at
expiry reports `deadline-abandoned-provider-task` then
`cleanup-deadline-exceeded`. Every non-success retains the lease, suppresses
data-source disposal, and requires helper exit. A non-abandoned close may retry
only within the same still-active supplied deadline object. Descriptor creation
and disposal share an atomic lifecycle gate. Descriptor disposal with a retained
lease fails as fixed cleanup without invoking the data source. A data-source
disposal fault is cached and replayed without calling the provider again,
because Npgsql marks the source disposed before asynchronous cleanup and a
second provider call could otherwise mask the first failure.

The coordinator mirrors any catchable retained-lease close failure into its
preallocated terminal disposition through fixed non-caller-valued
`MarkDatabaseLifecycleRetained()`, which sets helper exit and terminal
`connection-close-failed`. Cleanup-deadline expiry and commit-outcome unknown use
their existing more-specific terminal transitions. This signal is required
before return/rethrow on pre-admission, ordinary-failure/not-committed, and
post-success paths so the helper never reuses a process with an unclosed database
lifecycle.

Phase 4 may run only in a dedicated migration/helper process that does not
initialize application telemetry, register an `EventListener`, install a
startup hook/profiler, enable EventPipe, or use a globally configured Npgsql
data source. The Phase 4 implementation does not add a public host in this
checkpoint; the disposable integration-test helper is the only host now, and
Phase 5 must preserve this process boundary when it adds public orchestration.
This is a closed-process trust assumption, not something a library call can
prove against arbitrary injected managed code. Source-level isolation tests and
adversarial `ActivityListener`, logger, and metrics negative controls must prove
the controllable paths capture no canary. The checked-in Npgsql source audit and
the helper-launch contract own the separate `Npgsql.Sql` EventSource limitation.

The first SQL command after open is one fixed, noninterpolated, zero-parameter
settings/identity query. It follows only the in-process source/environment/
local/descriptor time-zone comparison and precedes the parameterized environment
query, so no schema or transfer value reaches a server whose logging contract
has not been proved. The current role is a non-superuser with immediately usable
inherited `pg_read_all_settings`; the owned completion harness grants only that
predefined visibility role and proves missing membership refuses before payload.
PostgreSQL 16.14 leaves `pg_control_system()` on the ordinary default function
ACL: `pg_proc.dat` defines no custom ACL and `system_functions.sql` does not
revoke it, while the PostgreSQL privilege contract gives functions default
`PUBLIC EXECUTE`. The completion harness therefore asserts that effective
function privilege directly and never grants `pg_monitor`; an isolated negative
target revokes only that function from `PUBLIC` and must be refused before
payload.

The first owned connection captures an immutable target identity consisting of
the `pg_control_system().system_identifier` as canonical unsigned decimal text,
exact postmaster start time,
current database name and OID, target schema name and OID, current role name and
OID, exact server version, recovery/read-write state, and actual server address
and port (including the reviewed null spelling for a Unix-domain socket). Every
later connection, including failed-state and commit-reconciliation connections,
must match every component before it receives a digest, state value, row, or
COPY payload. PostgreSQL 16.14 exposes the underlying unsigned identifier
through signed `bigint`, so a negative exposed value is converted by exact
numeric addition of `18446744073709551616` before text conversion; signed
narrowing is forbidden. `pg_is_in_recovery()` must be false and both session and
transaction read-only settings must be off. Any mismatch before an ordinary
commit is a redacted failure; any mismatch while resolving an uncertain commit
is commit-outcome-unknown and forbids destructive action.

Before any manifest digest, row value, import-state value, or COPY data is sent
to PostgreSQL, the first owned connection performs a read-only
`current_setting`/`SHOW` logging preflight. Every later owned connection repeats
it before use. The reviewed safe session contract is exact:

- `log_min_messages=panic` and `log_min_error_statement=panic`;
- `log_error_verbosity=terse`;
- `log_statement=none`, `log_duration=off`,
  `log_min_duration_statement=-1`, `log_min_duration_sample=-1`, and
  `log_transaction_sample_rate=0`;
- `log_parameter_max_length=0` and
  `log_parameter_max_length_on_error=0`; and
- `debug_print_parse=off`, `debug_print_rewritten=off`,
  `debug_print_plan=off`, with empty `shared_preload_libraries`,
  `session_preload_libraries`, and `local_preload_libraries`; and
- `log_destination=stderr` and `logging_collector=off`; and
- `fsync=on`, `full_page_writes=on`, and exact
  `synchronous_commit=on`.

`DateStyle` accepts exactly PostgreSQL's canonical `ISO, MDY`, `ISO, DMY`, or
`ISO, YMD` spellings; the transfer uses no ambiguous date text. TCP endpoint
identity requires canonical unscoped IPv4/IPv6 plus a port in `1..65535`, while
only both-null address/port is the Unix-domain spelling. Phase 4 rejects any
mismatch and never issues `SET` to repair it. The logging
settings prevent ordinary/error statement text, bind values, error detail/
context, and debug/audit hooks from recording transfer values for the migration
sessions. The WAL settings make an acknowledged local commit crash-durable under
PostgreSQL's documented contract; Phase 4 does not call a visible but
`synchronous_commit=off` state durable.
The completion harness must provision and exclusively own its dedicated
PostgreSQL 16.14 server process/container from startup through shutdown, pin the
single `stderr` destination with the collector off, capture that stderr byte
stream from process creation, stop the server cleanly after the test run, drain
the stream to EOF, and only then prove adversarial canaries are absent. A mere
connection string cannot establish this server-log proof. Future real
orchestration must require the same operator-owned startup/configuration/log-
capture attestation; without it Phase 4 can still protect transfer payloads by
withholding them until preflight, but it cannot promise that connection identity
metadata was absent from pre-authentication server records. A hostile
administrator, unreviewed server build, kernel/platform tracing, or PostgreSQL
`PANIC`/defect is outside this software trust boundary and cannot honestly be
hidden by an application-level guarantee.

## Phase boundary

### In scope

- exact PostgreSQL head-catalog and migration-history validation;
- atomic bootstrap-only target admission;
- the exact 27 transferred tables in frozen contract order;
- replacement of the generated target API keys with the source API keys;
- preservation, rather than delete/reinsert, of the five identical bootstrap
  roots;
- bounded, verified-batch import with active constraints and triggers;
- independent logical row, count, catalog, and derived-table verification;
- reconstruction and sealing of every manifest blob, including accepted
  physical orphans, in an importer-owned target staging tree;
- `fresh -> importing(A) -> database-verified(A)` and
  `importing(A) -> failed(A)` state handling;
- cancellation, crash, partial-commit, sensitive-buffer, and residue behavior;
- synthetic fixtures and uniquely owned disposable PostgreSQL targets only.

### Out of scope

- public `--db-export-v3` or `--db-import-v3` commands;
- `Program`, entrypoint, Compose, release workflow, or runtime-provider changes;
- PostgreSQL runtime enablement or provider promotion;
- publishing or renaming into `CONFIG_PATH/blobs`;
- `.nzbdav-migration-complete.json`;
- final source database, sidecar, or blob stability rechecks;
- resuming an interrupted import in place;
- replace/merge import, rollback to a usable target, cutover, or deployment;
- existing or real PostgreSQL databases, real SQLite data, ARR, Plex, mounts,
  or production services;
- backup, restore, scheduling, retention, or disaster-recovery behavior;
- changing the separately defined grab-to-Plex benchmark or claiming its
  five-second objective is met.

## Why direct COPY, not EF row insertion

EF row-by-row insertion would add change tracking, object materialization, and
per-row command overhead without improving the frozen transfer contract. A
permanent unlogged staging schema would improve some bulk-load cases but would
double storage, expand catalog and crash cleanup, and create another persistent
state machine.

Direct `COPY FROM STDIN` is the narrow target-side mechanism. PostgreSQL applies
table constraints and triggers during `COPY FROM`, which is required for
foreign keys and rebuilding `HealthCheckStats`. Npgsql's binary importer gives
the ordinary path efficient typed writes and cancels an incomplete COPY when it
is disposed without completion. Its documented typed writer does not provide a
streaming text-value contract, so a spilled batch uses text COPY rather than
materializing an arbitrarily large managed string or depending on undocumented
behavior.

References:

- PostgreSQL 16 `COPY`: <https://www.postgresql.org/docs/16/sql-copy.html>
- PostgreSQL 16 field limits: <https://www.postgresql.org/docs/16/limits.html>
- PostgreSQL 16 TOAST limits: <https://www.postgresql.org/docs/16/storage-toast.html>
- PostgreSQL 16 error reporting and logging:
  <https://www.postgresql.org/docs/16/runtime-config-logging.html>
- PostgreSQL 16 write-ahead-log durability:
  <https://www.postgresql.org/docs/16/runtime-config-wal.html>
- PostgreSQL 16 control/system information:
  <https://www.postgresql.org/docs/16/functions-info.html>
- PostgreSQL 16 default function privileges:
  <https://www.postgresql.org/docs/16/ddl-priv.html>
- PostgreSQL 16.14 `pg_control_system` catalog declaration:
  <https://raw.githubusercontent.com/postgres/postgres/REL_16_14/src/include/catalog/pg_proc.dat>
- PostgreSQL 16.14 restricted-function grants/revokes:
  <https://raw.githubusercontent.com/postgres/postgres/REL_16_14/src/backend/catalog/system_functions.sql>
- Npgsql binary importer: <https://www.npgsql.org/doc/api/Npgsql.NpgsqlBinaryImporter.html>
- Npgsql text COPY writer: <https://www.npgsql.org/doc/api/Npgsql.NpgsqlCopyTextWriter.html>
- Npgsql logging: <https://www.npgsql.org/doc/diagnostics/logging.html>
- Npgsql tracing: <https://www.npgsql.org/doc/diagnostics/tracing.html>

## Frozen PostgreSQL target mapping

The embedded PostgreSQL target contract has exact top-level JSON shape
`{ formatVersion, tables, derivedHealthCheckStats }`, with `formatVersion` equal
to integer `3`. Table objects contain exactly `ordinal`, `name`, `columns`,
`keyColumns`, `preserveBootstrapRoots`, and `filtersReservedImportState`.
Column objects contain exactly `ordinal`, `name`, `sourceKind`,
`postgreSqlType`, `collation`, `nullable`, and `binaryCopyType`. Property names
and enum strings are case-sensitive camelCase; enum integers are forbidden.
The only source-kind strings are `uuid`, `boolean`, `enumInt32`, `int32`,
`int64`, `text`, `localWallTimestamp`, and `instant`; the only binary-COPY-type
strings are `uuid`, `boolean`, `integer`, `bigint`, `text`, and `timestamp`.
Arrays are order-significant, while object property order and insignificant
JSON whitespace are not part of the contract. Duplicate, missing, unknown, or
null non-nullable properties; comments; trailing commas; unknown or numeric
enums; non-integral ordinals; and any other shape fail closed.

After validation, every exposed table, column, and key-column collection is a
deeply immutable defensive snapshot. No mutable array, list, JSON DTO, or
caller-owned collection backs an `IReadOnlyList` property. A cloned or
caller-constructed record cannot mutate a contract-owned record or any
precomputed fragment and remains reference-foreign to the fragment APIs.

`tables` contains exactly the 27 transferred source tables in source-contract
order. Table and column ordinals are zero-based positions in the source-contract
arrays, not PostgreSQL physical `attnum` values. Columns remain in source order
even when physical target order differs. `keyColumns` is the source keyset's
column sequence in its exact order. The separately stored derived
`HealthCheckStats` contract is excluded from `tables`, has table ordinal `27`,
uses zero-based derived-source column order, has both behavior flags false, and
can be selected/ordered for verification but never receives a COPY list.

`PreserveBootstrapRoots` is true only for `DavItems` and
`FiltersReservedImportState` is true only for `ConfigItems`; every other
transferred table has both false. These flags describe the row-level rules in
this design and are not inferred from a caller or mutable target state.

Each target column name is joined by name to both the checked-in PostgreSQL EF
head model and the physical head catalog. `PostgreSqlType` is the catalog's
exact `format_type`: a source Text column therefore stores either exact `text`
or exact `character varying(n)` as physically modeled, while its binary COPY
type remains `NpgsqlDbType.Text`. The other exact source-kind/type/COPY maps are
UUID to `uuid`/`Uuid`; Boolean to `boolean`/`Boolean`; EnumInt32 and Int32 to
`integer`/`Integer`; Int64 and Instant to `bigint`/`Bigint`; and
LocalWallTimestamp to `timestamp without time zone`/`Timestamp`. No type is
guessed or defaulted. Nullability must agree across source, EF model, and
catalog. `Collation` is exactly string `C` only for columns explicitly modeled
with C collation and is null otherwise; validation translates that to
`pg_catalog.C` for explicit C, `pg_catalog.default` for Text without an
explicit collation, and the catalog's empty spelling for noncollatable
columns.

The target contains no transferred identity or computed/generated physical
column. `WorkerJobs.LeaseGeneration` is the one transferred column with a
modeled server default (`0`) and EF `ValueGeneratedOnAdd` metadata. It remains
ordinary source data for transfer: the contract includes it and every COPY
explicitly supplies its source value, so Phase 4 never invokes or relies on the
default. A future added or changed default, identity, or generated column fails
the contract review rather than silently changing COPY membership.

Every table and column identifier must match ASCII
`^[A-Za-z_][A-Za-z0-9_]{0,62}$` and is always emitted double-quoted. SQL
fragments are obtained only from the loaded target-contract instance by passing
one of its exact table objects; reference-foreign or cloned records are rejected
even if value-equal. No fragment API accepts a name or arbitrary SQL string.
The exact fragments are:

- quoted table name: the reviewed table identifier in double quotes;
- COPY column list: every column in source order, double-quoted and separated
  by comma-space; unavailable for derived `HealthCheckStats`;
- ORDER list: key columns in key order, double-quoted and separated by
  comma-space, with exact ` COLLATE pg_catalog."C"` appended when the column's
  contract collation is `C`; and
- SELECT projection: each non-Text column as its double-quoted identifier and
  each Text column as `pg_catalog.octet_length("column"), "column"`, preserving
  source column order and comma-space separation.

These fragments are precomputed or rendered only from successfully validated
embedded values. The physical catalog's column order is validation evidence,
not permission to change transfer field order.

## Read-only PostgreSQL representability preflight

Before the target admission transaction or any import-state mutation, Phase 4
performs one bounded sequential pass over all 27 sealed table streams. This pass
uses the embedded target mapping to prove that every source field can be
represented by the pinned PostgreSQL contract. It validates table and column
cardinality, nullability, target kind, fixed-width payloads, integer and
timestamp domains, text UTF-8/rune/NUL rules, and the actual byte length of each
text field against the PostgreSQL field ceiling.

The pass retains only counters, current field state, and fixed-size digests. It
does not materialize a row or text value, does not create a spill, does not read
or copy blob content, and does not connect to or mutate the target. The sealed
stage remains the authority for canonical frame, source-semantic, reference,
and blob-closure verification; this extra pass answers only the target-specific
representability question that Phase 3 deliberately did not own.

The effective text-payload ceiling is the smaller of the required operator
value and `1_073_741_819` bytes (`2^30 - 5`). The latter is a theoretical
upper bound derived from PostgreSQL's documented `2^30 - 1`-byte logical
varlena datum limit including the four-byte header; it is not a promise that a
deployment can accept a value that large through COPY. The explicit operator
ceiling may be lower and has no default. Phase 4 rejects a value above the
effective ceiling before target mutation. The future orchestration phase, not
this internal phase, will bind the provider plan's
`NZBDAV_TRANSFER_MAX_FIELD_BYTES` setting to this required typed input. Both
values count the strict UTF-8 text payload only; the transfer field's one-byte
null/value marker is outside this PostgreSQL payload count.

This does not retroactively claim that the completed internal Phase 3 exporter
enforced an operator deployment ceiling. For an already sealed artifact, Phase
4's read-only pass is the first operator-ceiling gate and still occurs before
target mutation. The future public orchestration must require the same value at
export as well, so newly initiated workflows reject earlier. The provider plan's
before-export requirement is therefore preserved as explicit later wiring, not
silently declared complete by Phase 4.

Any deterministic representability failure therefore leaves the target in
canonical `fresh` state. Import still repeats the same checks while staging each
batch as defense in depth, because the later pass must not trust a stale counter
or bypass the parser.

This source-only pass also proves that the actual sealed source contains the
five exact bootstrap roots and two valid, distinct source API keys, and returns
that proof in its typed preflight result. The coordinator requires the proof
before opening the first target session. Target admission does not accept a bare
Boolean or a source-stage input and cannot recreate this source proof.

The same read-only pass computes a checked upper bound for Phase 4's private
staging writes. For every table batch it computes the exact internal spool
record bytes that promotion would require without creating the spool. For the
blob stage it uses the manifest total blob bytes, the maximum canonical receipt
length, and a conservative fixed 512-byte logical reservation for each possible
root, directory, blob-file, receipt, work-root, and spool entry. At most two
shard directories and one file are reserved per blob even when prefixes are
shared. Because the table work root is durably removed before the blob root is
created, the required peak is the larger of the maximum one-batch spool bound
and the complete blob-stage bound, not their sum.

All arithmetic is checked and a bound above the required
`MaxPhase4StagingBytes` fails while the target is still canonical `fresh`.
Writers separately debit actual spool, blob, and receipt bytes plus the same
entry reservations before creation, so a malformed stream cannot outrun the
preflight. The importer also performs a read-only available-space check against
the logical bound, but this is an early diagnostic rather than a free-space
reservation: filesystem allocation, quotas, concurrent unrelated filesystem
writes, and later `ENOSPC` can still differ. Such a failure remains a normal
redacted import failure with identity-scoped cleanup. The ceiling has no default
and does not claim to equal physical allocated blocks.

## Pre-admission time-zone identity

Local-wall timestamps are meaningful only under the source deployment's exact
zone identity. After the source-only pass and before the admission transaction,
Phase 4 requires ordinal, byte-for-byte equality among all five values:

1. `TransferV3Manifest.SourceTimeZoneId`;
2. `NZBDAV_LEGACY_TIMESTAMP_TIMEZONE`;
3. `TimeZoneInfo.Local.Id`;
4. the unopened descriptor's explicit Npgsql `Timezone`; and
5. the first owned connection's actual `SHOW TimeZone` result.

Aliases are not normalized or accepted even when they currently share an
offset/ruleset. The connection's server-logging contract is validated in the
same read-only pre-admission session. A mismatch or unreadable setting closes
the connection and leaves the target in exact canonical `fresh` state. Every
later owned connection revalidates its actual time zone before use.

## Target admission

Admission happens before any transferred row or blob is staged. It requires an
exact `READ COMMITTED` transaction. The importer first uses
`pg_try_advisory_xact_lock` for a component-wise key derived from the current
database, the descriptor-validated configured target schema, and a fixed
transfer-v3 namespace seed. It never waits. The lock serializes cooperating
admissions and releases automatically on transaction end or process death;
failure to obtain it is a redacted, non-mutating refusal. The transaction then
executes one configured-target-schema-qualified, identifier-safe
`LOCK TABLE ... IN EXCLUSIVE MODE NOWAIT` statement. Its exact
deterministic order is the 27 transferred tables in contract order, the derived
`HealthCheckStats` table, then `__EFMigrationsHistory_PostgreSql`. It validates
only after all 29 locks are held. `EXCLUSIVE` blocks locking readers, ordinary
writers, and conflicting DDL including standard `CREATE INDEX`, while allowing
plain readers. Blocking `ROW SHARE` prevents a prior `SELECT ... FOR UPDATE`
from retaining the reserved row past the table-lock gate and making the later
admission CAS wait or deadlock; `NOWAIT` converts contention into a bounded
refusal.
`READ COMMITTED` is mandatory because the advisory-lock query is a `SELECT` and
therefore would freeze a `REPEATABLE READ` or `SERIALIZABLE` snapshot before the
relation locks.

While those locks and the quiescent operator window remain in force, admission
proves all of the following:

1. The exact embedded PostgreSQL head physical catalog matches, including
   server patch, tables, columns, types, collations, constraints, indexes,
   functions, triggers, ACLs, and absence of unexpected objects.
2. The session uses UTF-8 client encoding, the reviewed local time zone,
   ISO date style, `session_replication_role=origin`, no temporary schema, and
   no setting that makes the target or transaction read-only. Phase 4 neither
   accepts nor sets a trigger/constraint bypass.
3. The PostgreSQL migration history contains exactly the native baseline and
   operational-trigger migration IDs with their reviewed product versions,
   read from the locked provider-specific history table.
4. `DavItems` contains exactly the five contract bootstrap roots, byte-for-byte
   in every transferred field, and no other rows.
5. `ConfigItems` contains exactly two valid, distinct generated keys named
   `api.key` and `api.strm-key`, plus exactly one reserved
   `database.import-state` row whose value is the canonical `fresh` JSON.
6. `HealthCheckStats` and every other application table contain zero rows.

The source-only preflight proof described above is a coordinator precondition,
not a target-admission query. A production-shaped Phase 4 import has no zero-row
or missing-bootstrap bypass.

The exact bootstrap validator must be shared with the native migrator or
factored from it so migration and transfer cannot drift into two definitions of
"fresh". Aggregate counts alone are insufficient; values, exact UTF-8 names,
reserved-state bytes, root identities, and the absence of every other row are
all part of admission.

The shared definition freezes two compact UTF-8 JSON arrays. The five
`DavItems` rows are UUID-ordered and contain all fifteen target-contract
columns in exact target order; finite `CreatedAt` uses six fractional digits and
an independent AD-era predicate so provider infinity conversion or a BC value
cannot alias the reviewed minimum timestamp. The
three `ConfigItems` rows are C-byte-ordered and contain exact `ConfigName`, then
`ConfigValue`, properties. Valid documents have fixed byte lengths, no BOM,
whitespace, unnecessary escapes, or trailing bytes. The embedded reserved-state
JSON string uses only its mandatory JSON quote escapes; every other reviewed
string is unescaped. Live capture checks cardinality through one sentinel row,
checks `octet_length`, lazily converts only in-cap text, and returns empty
`bytea` for an oversized value so server-side conversion cannot materialize the
full corrupt value. The two generated secrets are checked byte by byte
as distinct 32-byte lowercase ASCII hexadecimal values. Captured snapshots own
their buffers and zero the API-key document on disposal; the combined
capture-and-validate operation always disposes it.

Expected and captured config serialization use a fixed-capacity, non-growing
`IBufferWriter<byte>` sized above worst-case escaping for all bounded fields.
Its complete backing array is zeroed on every exit, so no writer resize can
abandon a secret-bearing managed buffer.

Migration preflight accepts only the zero-, one-, or two-row ordered prefix of
the same exact `(MigrationId, ProductVersion)` head contract used by admission;
post-migration validation requires the exact two-row head. Native preflight and
final validation each use their own explicit read-only repeatable-read
transaction and positive per-command timeout. The shared validators themselves
accept an already-held read/write transaction because Phase 4 invokes them only
after its admission locks and proves exact connection ownership plus provider
readiness. On pinned Npgsql 10.0.3, `Connection` remains available after
commit/rollback, so each shared validator additionally probes the public
`IsolationLevel` getter that invokes the provider's completed/disposed check.
Under the already-owned advisory lock, native preflight probes history-relation
existence before opening its repeatable-read validation transaction. When the
relation exists, `SHARE` is the first non-control statement in that transaction,
followed by exact shape validation and the history read; final validation also
locks before its first history/catalog read. This keeps a lock wait from freezing
and later validating a stale transaction snapshot. Ambiguous advisory-lock
acquisition failure quarantines the possibly locked session by close, pool
eviction, and disposal; command cleanup preserves its primary and transaction,
acquire, and release cleanup failures retain distinct first-wins diagnostic
slots. The operational-trigger migration's direct-EF guard
also checks the baseline product version, so no alternate migration entry point
retains an ID-only definition.

While the table locks remain held, the dedicated import-state store performs a
compare-and-swap from canonical `fresh` to canonical `importing(A)`, where `A`
is the sealed manifest digest. Exactly one row must change. The transaction
commits the marker before any import batch can commit. If commit reports an
error, the commit-outcome reconciliation fence below must first prove that the
original transaction ended and only then read the exact canonical state. Exact
`importing(A)` continues; exact `fresh` is a non-mutating admission failure. A
timeout, identity drift, unreadable row, or any other state is
commit-outcome-unknown and is never guessed. A proven failure before admission
commit is not converted to `failed`.

Admission receives the same `TransferV3Phase4ManagedBudget` that owns the
caller-provided digest and proves that ownership before borrowing the session
connection or doing provider work. Only after every locked preflight succeeds,
it reserves two exact `Copy` leases, 35 bytes for canonical `fresh` and 123
bytes for canonical `importing(A)`, before allocating either byte array. Each
array's exact element storage is acknowledged to its lease. Allocation-free
codec span seams write the fixed framing and expose only the 64-byte digest
slice, so admission creates no intermediate digest string or state object.
Both arrays are zeroed and their references cleared before the leases are
released in reverse order on every exit. These exact leases prove the owned
array storage; bounded provider, validator, runtime, and JSON allocations remain
inside the existing 8-MiB runtime reserve and must be measured by Task 20 rather
than being mislabeled as exact per-array accounting.

The import-state store remains the only code allowed to update the reserved
row. Phase 4 must add a PostgreSQL-only transaction-bound operation that accepts
the already-open `NpgsqlConnection` and `NpgsqlTransaction`, validates the same
legal transition graph, and reuses the existing canonical codec and byte-exact
CAS predicate. It must also add the transaction-bound locking read used only by
commit reconciliation. That read proves row cardinality, text storage, the
byte-exact reserved key and value, and canonical decoding. The existing
cross-provider owned-connection operation retains its closed-context,
outside-transaction contract; no caller bypasses either store with ad hoc SQL.

The Task 8 admission path uses a specialized additive overload that borrows the
already-canonical fresh/importing byte arrays, independently revalidates the
exact legal canonical transition before provider access, and reuses the same
private PostgreSQL CAS executor. The store neither allocates, serializes, zeros,
retains, nor disposes those borrowed arrays. The Task 7 state-object overload
and its behavior remain unchanged.

Both transaction-bound operations validate a positive timeout and nonnull
arguments before provider access. The CAS preserves the existing illegal-edge
semantics: it returns zero before cancellation, connection/transaction property
access, serialization, or command creation. A legal CAS and every locking read
require an already-open supplied connection, then exact
`ReferenceEquals(transaction.Connection, connection)` ownership, then the
pinned Npgsql 10.0.3 public `IsolationLevel` readiness probe. They borrow these
objects and never open, begin, commit, roll back, close, or dispose either one.
Every command binds that exact transaction and timeout and uses explicit
`Text`, `Bytea`, or `Integer` parameter types.

The CAS requires native `text` columns and nests each `convert_to` comparison
inside a searched `CASE` whose preceding branch checks the byte length. A plain
sequence of `AND` predicates is not a bound because PostgreSQL may reorder
Boolean evaluation. The locking read is one parameterized
`SELECT ... LIMIT 2 FOR SHARE OF` statement. It proves native `text` storage and
bounds the database-encoding byte length before conversion; after bounded
conversion it separately proves the UTF-8 length is no greater than
`MaxCanonicalUtf8Bytes`. It does not use `SingleRow` or `SingleResult`, reads a
second sentinel row, accepts exactly one bounded byte array, and invokes
`TransferV3ImportStateCodec.ParseCanonical`. Missing, duplicate, wrong-storage,
byte-different, oversized, malformed, or noncanonical data returns one fixed
value-free malformed-row failure. Reader then command are explicitly disposed
without replacing an execution/read primary, and all temporary state bytes are
zeroed after use.

## Commit-outcome reconciliation fence

A plain read after a commit error is insufficient: under MVCC it can observe
the old state while the original transaction is still committing. Admission
and final-verification transactions therefore hold the same transaction-level
advisory lock from before their state CAS until transaction end. If either
commit reports an error, Phase 4 starts one monotonic, non-caller-cancellable
ten-second deadline immediately, quarantines the failed connection from further
use, and opens a fresh descriptor-owned reconciliation connection. The same
deadline and linked internal token cover failed-connection close, DNS/connect,
open/authentication, every diagnostics/time-zone/environment/identity preflight,
transaction start, advisory-lock polling, post-lock identity validation,
locking state read, transaction close/rollback, and reconciliation-connection
close. Each provider operation receives a fresh single-observation command fence;
its timeout is reduced to the remaining deadline, an already-expired fence
refuses work, and the deadline is rechecked before accepting a raced result. A
driver call that ignores cooperative cancellation is abandoned at the deadline
with only a bounded safe diagnostic; it cannot hold the coordinator indefinitely.

Within that deadline, the reconciler begins a `READ COMMITTED` transaction and
repeatedly calls `pg_try_advisory_xact_lock` for the same key with a bounded
delay. Failure to acquire it by the deadline is commit-outcome-unknown.
Acquiring it proves that the original same-postmaster transaction released its
transaction lock and has ended. While holding the lock, and only in a statement
issued after successful acquisition so it receives a fresh `READ COMMITTED`
snapshot, the reconciler revalidates target identity and performs the exact
reserved-row read with `SELECT ... FOR SHARE`. It never uses a
`REPEATABLE READ` snapshot created before the fence. The observed outcome is
provisional and the proof sink remains untouched. The reconciliation operation
must rollback/dispose its transaction, thereby release the advisory and row
locks, close its session, and return an explicit successful
`TransactionEndedFenceReleasedAndConnectionClosed` proof with no cleanup code
inside the same deadline. Only then may provisional committed/not-committed state
be published. Missing proof, rollback/release/close failure, any cleanup code,
timeout, or abandonment discards the provisional result and publishes only
unknown; the stage-backed unknown sink preserves commit-outcome-unknown before
any result mutation.

For admission reconciliation, exact canonical `importing(A)` proves commit and
continues on a newly opened import connection; exact canonical `fresh` proves
non-commit. For final reconciliation, exact canonical
`database-verified(A)` proves success and transfers ownership of the sealed
blob stage; exact canonical `importing(A)` proves non-commit and permits the
ordinary failure path. Missing, duplicate, non-text, noncanonical, mismatched-
digest, `failed`, or otherwise unexpected state is unknown. An identity change,
timeout, connection failure, or locking-read failure is also unknown. Unknown
never runs a failed-state CAS and never deletes or alters the sealed blob stage.

## Asynchronous parser observer

The current parser observer is synchronous, while PostgreSQL COPY, transaction
commit/rollback, and durable spill cleanup are asynchronous. Phase 4 adds an
async observer contract with `ValueTask` operations for observe, verified-batch
commit, table completion, and abort. The parser awaits each operation before it
clears the frame payload buffer.

The existing synchronous parser and observer behavior remains unchanged for
Phase 1-3. A narrow synchronous-observer-to-async adapter lets non-database
consumers use the new overload without acquiring blocking database behavior.
There is still only one in-flight frame.

The async overload invokes abort exactly once for any parse, observer,
cancellation, or EOF failure. It first passes the primary through the central
failure mapper, retaining an already-sanitized primary or replacing an unsafe
one before abort can run. Its abort context contains only a reviewed reason
enum, never the primary exception. Abort uses a non-cancelled, internally
bounded cleanup token and returns only allowlisted redacted outcome codes. If a
bug makes abort throw, the parser records the single safe code
`observer-abort-failed` in preallocated bounded parse-diagnostic slots;
unlike the current synchronous parser it does not construct an
`AggregateException` or replace/wrap the primary. It rethrows the sanitized
primary through `ExceptionDispatchInfo`, and the coordinator repeats the
fail-closed mapping before anything is returned or logged.

No observer operation may retain a frame's aliased payload after its awaited
operation completes.

## Bounded current-batch stage

The PostgreSQL observer stages only the current uncommitted batch. Earlier
verified batches are already committed and are never retained in memory. The
same managed-memory counter remains active through coordinator, verification,
and blob-stage work; it is not reset into an unaccounted second budget.

- Phase 4 pins `Phase4OwnedManagedBudgetBytes` to exactly 32 MiB. A new counter
  starts with both current and peak equal to a synthetic, non-releasable 8-MiB
  runtime reserve. Therefore exactly 24 MiB of additional charged capacity may
  be live; reaching 32 MiB succeeds and the next byte is refused before its
  backing allocation. This is an importer-owned accounting limit, not a claim
  about Npgsql, PostgreSQL, or total process working set.
- Every positive reservation is debited atomically before allocation. A normal
  required reservation that would exceed the ceiling is an invariant failure;
  optional growth uses a nonthrowing refusal that leaves current and peak
  unchanged. Lease disposal is thread-safe, idempotent, and releases exactly
  once. Byte buffers are charged at complete backing capacity and char buffers
  at checked two-times capacity. Fixed stack values do not count.
- The live counter includes decoded field and cursor storage, parser dispatch,
  strict UTF-8 decoder and COPY escape buffers, manifest/receipt/digest storage,
  directory buffers, and conservative metadata reservations of exactly 256
  bytes per retained row slot and 64 bytes per retained field slot. A slot array
  is charged as capacity times that reservation and does not add its measured
  element size again; cursor, decoded byte/char, and other backing buffers are
  separate charges. Tasks that introduce the real row and field value types
  must prove both `RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false`
  and `Unsafe.SizeOf<T>() <= reservation` on every supported target; otherwise
  the constants increase and promotion happens earlier.
- Phase 4 uses exact-sized sensitive arrays allocated only after their lease.
  Direct `ArrayPool<T>.Shared` use is prohibited because an unrestricted pool
  can return an oversized capacity that was unknowable before `Rent`. A future
  pool wrapper is admissible only if it reserves a deterministic maximum before
  renting and proves the returned capacity never exceeds that maximum. Every
  Phase 4 call into shared Phase 1-3 code must likewise prove that the selected
  path contains no unaccounted pool-backed allocation.
- Materialized strings and transient UTF-8-to-UTF-16 duplication count for full
  owned capacity before allocation. A digest is created only from an exact
  32-byte span after obtaining its `Digest` lease, owns one exact-sized array,
  clears it before releasing the lease, writes lowercase hex only into a caller
  buffer, and exposes no array or string copy. Digest span access and disposal
  are explicitly single-owner and non-concurrent. Canonical receipt bytes are
  charged separately as `Receipt`; a digest embedded in a receipt remains a
  `Digest` charge. A digest parameter is a synchronous/awaited borrow unless an
  API explicitly says `CompleteAndTransfer`; the callee never disposes or
  retains a borrowed digest. In particular, blob construction borrows the
  coordinator-owned manifest digest, creates a second charged digest from its
  span for the receipt, and transfers only that clone to the candidate/stage.
  Failure before or after cloning disposes the clone while leaving the borrowed
  original valid. The coordinator disposes its original on every terminal path;
  success/unknown retains only the receipt-owned clone.
- The 8-MiB runtime reserve covers only reviewed opaque runtime/JSON object
  overhead. `GC.GetTotalMemory(false)` is an approximate process sample and is
  never presented as an exact live-heap proof; `GC.GetTotalAllocatedBytes(true)`
  is allocation volume, not live size. Each parser, COPY, blob, and composite
  adversarial scenario therefore runs in its own warmed helper process. The
  hard proof remains deterministic lease/capacity/layout accounting. As a
  separate fail-closed canary, the helper captures precise process-lifetime managed
  allocation `A` and cumulative acknowledged managed array element storage `C`
  on both sides of the interval. It requires monotonic `A`/`C`, requires
  `(A1-A0) >= (C1-C0)`, and defines the unclamped opaque allocation upper bound
  as `(A1-A0)-(C1-C0)`, which must remain within 8 MiB. Only byte-array length,
  checked two-times char-array length, checked `Unsafe.SizeOf<T>() * array
  length`, or checked `string.Length * sizeof(char)` for a newly allocated
  measured-interval string may be acknowledged. Array/string/object headers,
  alignment, leases, state machines, native/unmanaged memory, stack storage,
  interned or pre-baseline allocations, the synthetic runtime reserve, and
  unused conservative slot slack are never acknowledged or subtracted.
- The helper also reports signed `(H1-H0)-(L1-L0)`, where `H` is
  `GC.GetTotalMemory(false)` and `L` is current acknowledged managed element
  storage. It never clamps the result. Any collection-count change or negative
  result makes this approximate heap diagnostic indeterminate; it is reporting
  only, never an acceptance or high-water gate. Acceptance combines the exact
  managed peak, the allocation-volume upper bound, and independent helper
  RSS/PostgreSQL memory caps. No probe forces a collection. Task 3 supplies only
  the accounting primitives; Tasks
  10, 12, 13, and 15-16 add component evidence, Task 19 supplies process
  isolation, and Tasks 20-21 own the composite and native-matrix gates.
- Pure measurement uses one cross-platform owned runner and exactly six fresh
  helper processes: budget, parser, current batch, COPY codec/escape without an
  Npgsql session, blob build, and blob verify. The runner accepts no scenario or
  fixture path, requires PostgreSQL environment absent, creates a mode-0700
  descriptor-pinned harness/staging root, validates canonical numeric output,
  and binds both its full-lifetime RSS sampler and the helper's numeric
  `Process.PeakWorkingSet64` to the same retained child identity rather than PID
  alone. Both values are required, validated, and normalized to bytes; the RSS
  high-water is their maximum and must be at most 384 MiB. Missing or invalid
  values, identity mismatch/reuse, invalid unit conversion, or sampler failure
  fails closed; the two samplers are not required to agree exactly. The runner
  then performs identity-proven no-follow cleanup. Lifecycle/roundtrip/activity/
  pause/reconciliation scenarios remain exclusively inside Task 20's owned
  PostgreSQL runner. The final matrix is native Linux x64 glibc and musl,
  emulated-QEMU Linux arm64 glibc and musl, and native `macos-15` arm64; every
  entry uses the same immutable scenario/test/runtime manifest with zero skips.
- Blob construction and verification include parser/decoder buffers, hashing
  state, receipt bytes, directory-enumeration buffers, path-segment buffers,
  and ownership/cleanup cursors in the same counter. They may not retain an
  `OwnedFile`, dictionary, digest, descriptor, or path object per blob. The
  target tree's fixed four-level grammar permits bounded descriptor-relative
  traversal with one current entry and fixed-depth cursor state.
- Fixed-width fields and ordinary text remain in typed or exact-sized charged
  buffers only while that counter stays within the ceiling. Before an allocation would cross
  it, the entire current batch is promoted to a sequential private spool:
  already retained rows are encoded into it, their sensitive buffers are
  cleared, and later chunks append directly. Only fixed counters and one
  bounded I/O buffer remain live.
- The work root is exactly a nonce-created mode-`0700` sibling named
  `.nzbdav-transfer-v3-import-work-<random-nonce>` beneath the same pinned
  trusted staging parent. Each promoted batch uses a no-replace mode-`0600`
  regular spool whose name contains only table ordinal, batch ordinal, and a
  random nonce, never table keys, UUIDs, paths, or field values.
- Spills use no-follow, no-replace descriptor-relative creation; symlinks,
  hard links, non-regular files, unexpected link counts, and identity changes
  fail closed.
- Sensitive buffers are cleared before their leases are released. Spill files are closed, identity
  checked, removed, and parent directories synced after commit or abort.
- Cleanup acts only on nonce-created, identity-proven entries. Unprovable
  residue is surfaced as a redacted audit code and is never recursively deleted
  by path.

After the last table commits, the importer proves the work root contains no
entry other than any currently identity-tracked spool, removes those entries
and the exact owned root descriptor-relatively, and syncs the trusted parent
before blob-stage construction. Success cannot return with a work root; an
unprovable or failed removal is a redacted failure/residue, not permission to
delete a broader path.

The spool is an internal append-only replay format, not a second transfer
artifact and not resumable after a crash. Its length and records are checked
while rewinding after the canonical batch-end verifies. During text-COPY replay,
the observer independently recomputes the row count, decoded bytes, last cursor,
and canonical batch digest from the spool and compares them with the verified
batch-end before completing COPY. Identity, mode, link count, length, and final
offset are checked before and after replay; any mismatch cancels COPY and rolls
back the batch. If metadata plus the fixed parser/encoder reservation could
exceed the 32-MiB counter even after promotion, the batch fails without an
optimistic over-budget allocation.

The stage validates every field marker, nullability rule, type domain, integer
range, timestamp representation, UTF-8 sequence, NUL prohibition, text rune
limit, and PostgreSQL representability boundary again while reconstructing the
batch. Any mismatch fails before COPY. The effective payload ceiling is the
required operator-tested value capped by the theoretical PostgreSQL bound
defined above. The transfer format's larger 16-GiB defensive ceiling does not
imply PostgreSQL representability.

## Canonical expected logical digests

Import computes an expected logical digest for every transferred table from the
sealed encoded fields, including the source bootstrap roots and API keys. It
uses the same canonical row preimage already defined for Phase 3 derived-table
verification:

1. four-byte big-endian cursor ASCII-byte length;
2. exact cursor ASCII bytes;
3. four-byte big-endian field count;
4. for each field, eight-byte big-endian encoded-field length followed by the
   exact marker-plus-payload bytes.

Rows are appended in the sealed table's verified contract order. Field bytes
may be fed from the bounded batch stage or replayed from an identity-proven
spill; no whole large text value is required. Row count and digest are retained
as in-memory verification receipts only. They are not logged or added to the
Phase 3 manifest.

## Verified-batch database commit

The parser calls batch commit only after the batch-end row count, decoded byte
count, last cursor, and canonical digest verify. At that point the observer:

1. opens a new PostgreSQL transaction on the dedicated import connection;
2. performs any one-time bootstrap preparation for that table;
3. chooses binary or streaming text COPY for the entire batch;
4. writes every non-bootstrap-preserved row in the exact target column order;
5. completes COPY;
6. commits the transaction; and
7. clears and removes the current batch stage.

An ordinary in-memory batch uses `NpgsqlBinaryImporter` with explicit
`NpgsqlDbType` values. A promoted batch uses PostgreSQL text COPY. The text
writer streams UTF-8 through a bounded decoder and escapes backslash, tab,
newline, carriage return, backspace, form feed, and vertical tab according to
the PostgreSQL text-COPY contract. `NULL` is `\\N`; an empty string remains an
empty field; literal `\\N` and `\\.` data are escaped as data. Numeric, Boolean,
UUID, instant, and local-wall timestamp representations are invariant and
explicit. Session `DateStyle`, encoding, and time-zone assumptions are
validated before admission.

The exact non-text text-COPY spellings are: Boolean `t` or `f`; EnumInt32,
Int32, Int64, and contract `Instant` values as invariant base-10 ASCII with `0`
as the only zero spelling and a leading `-` only for negatives; UUID as
lowercase canonical `D`; and `LocalWallTimestamp` as exactly
`yyyy-MM-dd HH:mm:ss.ffffff`, with six microsecond digits and no `T`, offset,
zone, or kind suffix. An `Instant` remains the contract's validated UTC-ticks or
Unix-seconds integer because its reviewed PostgreSQL target column is `bigint`;
it is not rendered as a timestamp. The explicit mapping contract rejects a
future target-type change rather than reinterpreting a value.

The importer always supplies the reviewed explicit column list. It never uses
defaults, generated values, session replication mode, trigger disabling,
constraint disabling, `COPY FREEZE`, `ON CONFLICT`, or provider-generated SQL
from untrusted names.

COPY cancellation and transaction rollback are mandatory on error or caller
cancellation. For binary COPY, disposal without `CompleteAsync` cancels the
import. For text COPY, the code must call `CancelAsync` before disposal because
normal text-writer disposal completes the COPY operation. A successful binary
batch calls `CompleteAsync`; a successful text batch flushes and normally
disposes its writer; only then may the outer transaction commit. A later batch,
table-end, EOF, blob, or final-verification failure does not undo earlier
committed batches; the reserved state makes the partially imported target
unusable and recreation is the only retry.

## Bootstrap replacement rules

The five target bootstrap roots already equal the five source roots. Deleting
and reinserting them would invoke operational delete triggers and add risk with
no data change. Therefore the importer recognizes each exact source bootstrap
row, includes it in the expected source digest, proves it equals the admitted
target row, and omits only that row from COPY. A root with any differing field
is an import failure.

The generated target API keys must be replaced. In the transaction for the
first `ConfigItems` batch, before COPY begins, the importer deletes exactly the
two admitted generated key rows and proves the reserved import-state row still
exists as canonical `importing(A)`. It then imports all source ConfigItems,
including the two source API keys but excluding the reserved key, which Phase 3
already forbids. The delete and the first batch COPY commit atomically; failure
restores the generated keys for that batch transaction. The target stays
non-runnable throughout later batches even if the two source key rows fall in
different batches.

## Dependency order, constraints, and derived state

Tables are imported only in the embedded 27-table contract order. This order
places every physical foreign-key principal before its dependent table.
Application-level and state-aware references were already proven by the sealed
source artifact, while exact target digests prove their values were preserved.

All physical constraints and operational triggers remain enabled. In
particular, `HealthCheckResults` is imported while the three reviewed
`HealthCheckStats` triggers are active. `HealthCheckStats` itself is never
copied. Its final row count and canonical logical digest must exactly match the
single derived-table expectation in the manifest.

The physical catalog currently has no transferable identity or generated
columns. Every target column is written explicitly. A future identity,
generated column, new table, new column, changed type, changed trigger, or
changed migration history invalidates the embedded head-catalog check and
requires a reviewed contract update; Phase 4 does not guess or repair drift.

## Private target blob stage

After all database table files complete but while state remains
`importing(A)`, Phase 4 creates a second nonce-owned target stage beneath the
trusted target staging parent. It is separate from the Phase 3 source stage and
separate from the runtime blob root.

Its exact shape is:

```text
nzbdav-transfer-v3-target-<random-nonce>/
  blobs/
    aa/
      bb/
        <lowercase-D-uuid>
  blob-stage.json
```

The blob bundle is parsed again with the canonical async parser. The observer
derives each UUID and `aa/bb` path from the verified cursor; no source path is
copied. Descriptor bytes are validated first, content is streamed from the
sealed bundle into a no-replace destination, and SHA-256 and length are checked
while writing. Every file is fsynced, closed, reopened without following links,
identity checked, and rehashed. Empty blobs, multi-field blobs, accepted
orphans, and the exact manifest count, total bytes, table digest, and inventory
digest are all preserved.

The builder retains only the pinned root/parent descriptors and bounded current
entry state. It does not keep an in-memory collection of created blobs or shard
directories. Exact enumeration, verification, and failure cleanup walk the
fixed `blobs/aa/bb/uuid` grammar descriptor-relatively with no-follow opens,
bounded directory buffers, and fixed recursion depth. A name, type, link count,
mode, identity, or grammar mismatch leaves redacted residue instead of granting
authority to traverse or delete an unexpected object.

Namespace durability is descriptor-based and bottom-up. After each blob file is
durably closed, its `bb` leaf directory is synced so the new name cannot be lost
while the inode survives. After the last blob, the builder exact-enumerates the
tree and prepares the receipt under a no-replace temporary ordinal name while
the root is still writable. It then seals every blob file to mode `0400` and
syncs it; seals and syncs every `bb`, `aa`, and `blobs` directory to mode `0500`
in deepest-first order; writes and syncs the receipt file; seals the receipt to
mode `0400` and syncs it again; no-replace renames it to `blob-stage.json`;
syncs the still-writable stage root; seals and syncs that root to mode `0500`;
and finally syncs the trusted parent containing the stage-root entry. Every
created directory entry and final permission change is therefore covered by a
file or parent-directory sync before success.

`blob-stage.json` is canonical compact UTF-8 and contains only:

```json
{"formatVersion":3,"manifestSha256":"<64-lower-hex>","blobInventorySha256":"<64-lower-hex>","count":0,"totalBytes":0}
```

The zero values illustrate canonical numeric shape; an actual receipt carries
the manifest's verified nonnegative blob count and total bytes.

Receipt presence identifies only a completion candidate. Reopening requires a
fresh descriptor-based proof of canonical receipt bytes, current root identity,
exact tree shape, file and directory types, link counts, pairwise-distinct
current identities, modes, lengths, content hashes, count, total bytes, and
recomputed inventory digest. Every opened entry is statted before and after its
bounded read and compared with its descriptor-relative name, so replacement
during one verification pass fails. Because neither the receipt nor database
state stores every original inode identity, Phase 4 does not claim to detect a
byte-identical, mode-identical replacement that completes entirely while the
stage is closed; the quiescent ownership contract forbids such mutation, and a
later reopen treats it as content-equivalent after a complete current-tree
proof. A receipt alone never marks a stage complete, is not the runtime
migration-completion marker, and cannot authorize startup.

Final regular files are mode `0400` and directories mode `0500` on supported
POSIX platforms. Construction uses `0600`/`0700`. The implementation retains
the existing verified Linux x64/arm64 and macOS arm64 ABI boundary and fails
closed elsewhere. It does not call `BlobStore`, inspect an existing runtime
blob tree, create hard links, or expose a publication/rename method.

Success returns an opaque `TransferV3DatabaseVerifiedStage` that owns the open
trusted-parent/root descriptors and the exact receipt bytes. It exposes no
arbitrary root path. A later phase may consume that typed object for
publication. The owner object and all state needed to return it are allocated
before the final CAS; proven commit only flips a nonthrowing ownership flag and
returns the already-created object, so allocation cannot turn durable success
into an observed failure. Before the final database commit, the builder owns
cleanup authority and removes only
identity-proven entries on failure. If a pre-commit failure occurs after
sealing, cleanup may use the retained descriptors to restore construction mode
only on the proven-owned root/directories, remove proven-owned files and
directories deepest-first, and sync every changed parent; it never uses
pathname-recursive deletion, and an identity/mode failure becomes residue rather
than broader cleanup authority. After `database-verified(A)` commits, the
stage is durable: disposal closes handles but must not delete it or make the
committed database state lose its corresponding blobs. A process restart may
leave the recognizable sealed stage for Phase 5 to reopen only after a new
full proof described above; an incomplete residue is never treated as complete
or resumable.

## Independent target verification and success commit

After the private blob stage is sealed, a new pooling-disabled PostgreSQL
connection performs verification independently from the COPY connection. In an
exact `READ COMMITTED` transaction it reacquires the same transaction-level
advisory lock and the same exact 29-relation
`EXCLUSIVE MODE NOWAIT` lock set and order used by admission.
Migration IDs and product versions are read while the history relation is
locked. Snapshot-isolating levels are prohibited because the advisory query
precedes the relation lock. With the quiescent operator window still in force,
it then proves:

- the exact head physical catalog and migration history still match;
- the reserved state is exactly canonical `importing(A)`;
- all 27 logical table row counts match the sealed source counts, with the five
  preserved roots included;
- for `ConfigItems`, both the count query and ordered logical-digest query
  exclude exactly `database.import-state` with the byte-exact reserved-key
  predicate; the unfiltered physical count is exactly source count plus one,
  and that one extra row is separately proven canonical `importing(A)`;
- every table's target logical digest, including the filtered `ConfigItems`
  digest, matches the expected receipt computed from the sealed source;
- target key ordering follows the contract, including bytewise `C` collation;
- text is read and re-encoded in bounded chunks rather than materialized as a
  whole large value;
- `HealthCheckStats` matches the manifest derived row count and digest;
- constraints and triggers remain enabled and valid; and
- a fresh descriptor enumeration of the private blob stage, not a cached
  receipt comparison, matches `A` and the manifest blob inventory.

Target text verification selects `octet_length` before each corresponding text
value. Every command uses `CommandBehavior.SequentialAccess`; columns are read
strictly in increasing ordinal order, and unbounded text uses Npgsql's text
reader rather than `GetString`. The verifier re-encodes through a strict bounded
UTF-8 encoder and records allocation/accounting metrics. Target values pass
through an independent provider-to-contract encoder; verification must not
reuse the COPY text serialization as its source of truth.

Immediately before the final state CAS, the verifier reparses the receipt and
descriptor-enumerates the exact sealed tree from its pinned root. It revalidates
the retained root identity plus current file/directory identity distinctness and
within-pass stability, types, link counts, modes, names, lengths, and absence of
extras; streams and rehashes every blob; and recomputes count, total bytes, and
inventory digest. The result must match both the canonical receipt and manifest.
This final proof occurs while the trusted filesystem is still in the declared
quiescent window; neither receipt presence nor construction-time hashes are
accepted as current proof.

While the table locks remain held, the import-state store performs the exact
CAS `importing(A) -> database-verified(A)` in the same transaction. Exactly one
row must change. Transaction commit is the Phase 4 success point.

Cancellation is checked before this final CAS, and final commit uses a bounded
internal token rather than a token that the caller can cancel mid-commit. If
commit reports an error, the coordinator uses the commit-outcome reconciliation
fence above, not a plain or stale-snapshot state read. Exact
`database-verified(A)` after the fence is committed success and retains the
stage. Exact `importing(A)` after the fence follows the ordinary failure path.
If the fence cannot prove transaction end, target identity, or an allowed exact
state, the coordinator reports commit-outcome-unknown and preserves the sealed
stage because deleting it could destroy the blobs for a transaction that
actually committed.

After proven final commit, the preallocated stage object's ownership flag flips
first. Database/COPY transactions and connections are then closed, the owned
`NpgsqlDataSource` is disposed only after its last connection, and temporary
source reads are closed, all best-effort. The trusted-parent and target-stage
root descriptors remain owned by the returned
`TransferV3DatabaseVerifiedStage`; the data source never transfers to it. A
database/data-source/source-handle closure failure is recorded only in
preallocated bounded diagnostic slots on that object; failure to record is
ignored. Cleanup does not throw, allocate an unbounded collection, observe
caller cancellation, close the target-stage ownership descriptors, delete the
durable stage, or change the successful outcome. This prevents a caller from
observing failure after durable success and then losing the blob stage that
corresponds to `database-verified`.

`database-verified` remains non-usable. No runtime process may start from it
without the future external completion marker carrying the same manifest
digest and the later runtime gate explicitly accepting that pair.

## Failure, cancellation, and crash semantics

Before `fresh -> importing(A)` commits, every failure is non-mutating.

After admission commits, every catchable failure or caller cancellation before
the final commit point normally:

1. aborts the current parser observer exactly once;
2. cancels/disposes incomplete COPY and rolls back the current transaction;
3. clears sensitive buffers and removes identity-proven work spills;
4. removes an incomplete or sealed target blob stage if success ownership has
   not transferred;
5. best-effort CASes `importing(A) -> failed(A)` using an internal bounded token;
6. closes all database connections and then disposes the owned data source
   best-effort, using only preallocated safe diagnostics;
7. preserves and rethrows the already-sanitized primary with its stack; and
8. exposes only allowlisted cleanup/CAS outcome codes as secondary diagnostics.

Cleanup or failed-state CAS errors never replace the primary failure. No code
may transition `failed`, `importing`, or `database-verified` back to `fresh` or
usable. No import resumes from committed table batches. Recovery is to remove
the uniquely owned disposable target and staging residue, recreate it, reapply
migrations, and start from the same sealed source or a newly captured source.

Every provider, parser, COPY, transaction, codec, and POSIX boundary translates
an unsafe exception immediately into a `TransferV3Phase4Exception` created by a
single reviewed factory. Its public shape is only a stable allowlisted failure
code, a fixed message, optional five-character SQLSTATE, and allowlisted
secondary codes. It has no raw `InnerException`, no copied `Data`, no SQL,
parameter, server detail, hint, context, internal query, schema/table/column/
constraint field, path, value, UUID, digest, or connection component. The raw
exception is used ephemerally only to select the safe code/SQLSTATE and is then
dropped; it is never logged, returned, retained on the typed stage, or passed to
observer abort.

The primary boundary-to-code mapping is exact and is implemented with an
explicit switch, never `Enum.ToString()` or another derived spelling:

| Boundary | Exact code |
|---|---|
| `Argument` | `phase4-argument` |
| `Parser` | `phase4-parser` |
| `Codec` | `phase4-codec` |
| `PostgreSqlOpen` | `phase4-postgresql-open` |
| `PostgreSqlCommand` | `phase4-postgresql-command` |
| `PostgreSqlCopy` | `phase4-postgresql-copy` |
| `PostgreSqlCommit` | `phase4-postgresql-commit` |
| `Posix` | `phase4-posix` |
| `Cleanup` | `phase4-cleanup` |
| `Unexpected` or any invalid enum value | `phase4-unexpected` |

Every `TransferV3Phase4Exception` message is exactly
`Transfer-v3 Phase 4 failed.`; the code is exposed only through its dedicated
property and is not duplicated into prose. The caller-cancellation message is
exactly `Transfer-v3 Phase 4 was canceled.` and is constructed with that
message, a null inner exception, and the caller token. Cancellation is treated
as caller cancellation only when the caller token can be cancelled, is already
requested, and exactly equals the raw `OperationCanceledException` token.
Otherwise the cancellation is an ordinary raw failure at the supplied boundary.

`Sanitize` first returns an existing `TransferV3Phase4Exception` by reference,
unchanged. It then applies the exact caller-cancellation rule, and only then
maps an ordinary raw failure. A SQLSTATE is retained only on that ordinary path
when the raw exception is an `Npgsql.PostgresException`, the boundary is one of
`PostgreSqlOpen`, `PostgreSqlCommand`, `PostgreSqlCopy`, or `PostgreSqlCommit`,
and the value is exactly five ASCII `0-9`/`A-Z` characters. Every other source,
boundary, length, character set, or casing yields a null SQLSTATE; Phase 4 does
not trim, normalize, reflect over, or search inner/data fields for one.

The secondary-code serialization is also exact:

| Secondary enum | Exact code |
|---|---|
| `ObserverAbortFailed` | `observer-abort-failed` |
| `CopyCancelFailed` | `copy-cancel-failed` |
| `TransactionRollbackFailed` | `transaction-rollback-failed` |
| `SpoolResidue` | `spool-residue` |
| `BlobStageResidue` | `blob-stage-residue` |
| `FailedStateCasZeroRows` | `failed-state-cas-zero-rows` |
| `FailedStateCasUnknown` | `failed-state-cas-unknown` |
| `ConnectionCloseFailed` | `connection-close-failed` |
| `DataSourceDisposeFailed` | `data-source-dispose-failed` |
| `SourceReadCloseFailed` | `source-read-close-failed` |
| `DeadlineAbandonedProviderTask` | `deadline-abandoned-provider-task` |
| `CleanupDeadlineExceeded` | `cleanup-deadline-exceeded` |
| `CommitOutcomeUnknown` | `commit-outcome-unknown` |

`None` and invalid secondary enum values are non-throwing no-ops. A duplicate,
invalid value, or fifth distinct code returns false, consumes no slot, and
changes no existing code. A successful insertion uses the first free one of
four fixed slots and preserves insertion order. No secondary spelling is
derived from an enum name.

The coordinator rethrows only that reviewed exception type via
`ExceptionDispatchInfo`, preserving the sanitized exception's stack. A caller-
token cancellation is normalized to a new fixed-message
`OperationCanceledException` carrying only that token and no inner exception.
Any unclassified exception becomes the fixed `phase4-unexpected` exception.
Cleanup failures are reduced independently to allowlisted codes and cannot
smuggle a raw exception through `Exception.Data`. The connection-setting rules
above reduce leakage risk at the driver, but translation remains mandatory even
when every setting is safe.

Commit ambiguity is the exception to ordinary cleanup. A per-batch ambiguous
commit may still clean its transient batch stage because the whole target is
then marked failed. An ambiguous final state must be reconciled first; if it
cannot be reconciled, the sealed blob stage is preserved and no failed-state CAS
or destructive cleanup is attempted. This keeps both possible durable outcomes
non-runnable and recoverable by explicit inspection. Unknown-outcome handling
still quarantines/closes database handles and then disposes the owned data
source best-effort, but those failures produce only bounded safe diagnostics.
If a provider call ignored the reconciliation deadline and had to be abandoned,
the dedicated helper treats the result as terminal and exits after returning the
sanitized outcome to its launcher so no orphaned provider task remains in a
long-lived runtime process.

Expected process-kill states are:

| Kill point | Durable database state | Blob residue | Recovery |
| --- | --- | --- | --- |
| Before admission CAS commit | `fresh` | none or incomplete owned work | retry after residue audit |
| After admission, during any table batch | `importing(A)` | none/work residue | drop and recreate |
| After DB rows, during blob stage | `importing(A)` | incomplete private stage | drop, clean owned residue, recreate |
| After blob seal, before final CAS | `importing(A)` | sealed private stage | drop, clean owned residue, recreate |
| After final CAS commit | `database-verified(A)` | sealed private stage | future Phase 5 only |

SIGKILL cannot run cleanup, so startup and maintenance gates—not cleanup
optimism—must provide safety.

## API shape

Phase 4 adds internal components with single responsibilities rather than one
public orchestration command:

- target contract/admission validator;
- private PostgreSQL target descriptor/owned-connection factory;
- pre-admission time-zone and server-logging validator;
- transaction-bound import-state CAS support;
- async parser observer adapter;
- bounded sensitive batch/spill stage;
- central redacted failure mapper and allowlisted diagnostics;
- PostgreSQL binary and text COPY sinks;
- table import coordinator and source logical-digest receipts;
- independent PostgreSQL target verifier;
- private target blob-stage builder/owner; and
- a narrow internal Phase 4 coordinator returning the typed verified stage.

The exact class names may change during the implementation plan, but these
boundaries may not be collapsed in a way that lets COPY code validate itself,
lets a path stand in for a sealed stage, or lets blob staging publish to
runtime.

## Verification strategy

Implementation is test-driven. No existing application database/schema,
PostgreSQL process/container, target config, or blob tree is reused, mutated, or
cleaned. Ordinary local integration runs may remain environment-gated and may
use only an explicitly attested disposable server plus a uniquely owned target
schema. Such a connection-only run cannot satisfy the server-log proof.

The Phase 4 completion harness is stricter: it provisions and exclusively owns
a dedicated pinned PostgreSQL 16.14 process/container, its configuration,
single captured `stderr` log stream, database/schema, and staging entries for
the whole run, then destroys only those uniquely owned resources. Its launcher
passes the generated sanitized connection string to the isolated helper and
runs with `NZBDAV_REQUIRE_POSTGRES_TESTS=1`; missing ownership attestation,
connection data, log capture, clean shutdown/EOF drain, or any required fixture
is a setup failure, not a skip. The focused completion run must report zero
skipped PostgreSQL tests.

Required proof includes:

### Pure and fixture tests

- exact target type/column mapping for all 27 tables and 235 columns;
- refusal on contract, catalog, migration, table, column, or type drift;
- refusal when any of the four prohibited Npgsql diagnostic settings resolves
  true, refusal of caller-opened/caller-owned connections and nonfixed startup
  metadata/options, every inherited Npgsql `PG*` fallback, a non-`Any` target-
  session attribute, multi-host/failover/load-balancing/multiplexing, ambient or
  file-based credentials/certificates, and an unreviewed Npgsql package content
  or source build; the normalized private clone must carry explicit database,
  username/password, UTF8, fixed SCRAM/disabled-SSL transport values, an
  explicit pre-build empty `Options` assignment even when the input omitted it,
  and no built-data-source startup options (`null` or empty);
- exact normalized finite connection, command, and cancellation timeouts;
  refusal of caller-selected, zero, or unbounded values; and a reconciliation
  deadline that covers stalled DNS/open, each preflight, transaction start, lock
  polling, state read, rollback, and close;
- proof that the private descriptor creates every owned connection from its
  fixed-name data source with pooling/enlistment/parameter logging disabled,
  `NullLoggerFactory`, every command/batch/COPY tracing filter false, and
  physical-open tracing off, including reconciliation after earlier opens
  close; ownership must exist before every asynchronous open and survive fault,
  cancellation, or abandoned late completion; adversarial `ActivityListener`,
  logger, and metrics controls must capture no payload, credential, exception,
  path, digest, connection-string, or canary, while metrics may contain only the
  exact provider-owned safe tag allowlist and
  the helper process separately proves diagnostics/EventPipe and in-process
  `EventListener` registration are disabled/prohibited;
- exact pinned target identity capture and comparison, including cluster system
  identifier, postmaster incarnation, database/schema/role names and OIDs,
  server address/port, recovery/read-write state, and mismatch refusal on every
  connection kind;
- exact source/environment/process/descriptor/session time-zone equality,
  including different-zone and equivalent-alias refusal before admission;
- typed server-logging setting parsing and fail-closed refusal for every unsafe
  value in the reviewed logging contract, plus refusal of `fsync=off`,
  `full_page_writes=off`, or any `synchronous_commit` value other than exact
  `on`;
- read-only representability preflight rejecting an oversized target text field
  while the target remains exact canonical `fresh`, required operator ceiling
  has no default, and theoretical/effective limit arithmetic is overflow-safe;
- checked spool/blob-stage upper-bound accounting, no default staging ceiling,
  conservative entry reservations, available-space diagnostics, actual writer
  debits, overflow rejection, and `ENOSPC` cleanup behavior;
- async observer ordering, single-frame ownership, single abort, and payload
  clearing, including abort failure preserving the sanitized primary identity
  and stack while adding only one redacted secondary code;
- binary COPY type mapping and explicit column lists;
- text COPY escaping for null, empty, backslash, tabs, every line/control escape,
  literal `\\N`, literal `\\.`, multi-byte UTF-8 split across chunks, and NUL
  rejection;
- exact 32-MiB accounting for buffer capacities, chars, cursors, retained row/
  field reservations, the 8-MiB parser/runtime reserve, parser dispatch,
  decoder, and escape buffers; whole-batch spool promotion; mode/identity
  checks; spool replay mutation of content/cursor/length/digest; clearing;
  cancellation; cleanup failures; and residue codes;
- PostgreSQL text representability boundary without allocating a one-GiB test
  string;
- bootstrap root preservation and exact API-key replacement behavior;
- canonical source logical digests and independent target encoding, including
  byte-exact reserved-key exclusion from both ConfigItems count and digest;
- blob-stage exact layout, no-follow/no-replace behavior, orphan retention,
  empty and multi-field blobs, receipt canonicalization, every leaf/ancestor
  fsync including the receipt's post-`chmod` fsync, bottom-up sealing,
  completion-candidate reopen proof, bounded fixed-depth traversal without
  per-blob retained metadata, high-count tiny-blob pressure, disposal, and crash
  residue audit;
- redaction canaries for keys, values, UUIDs, paths, full digests, unsafe inner
  exceptions, `Data`, and non-allowlisted diagnostics.

### Disposable PostgreSQL integration tests

- exact fresh target admission and every non-bootstrap refusal before mutation;
- two concurrent importers with exactly one admission winner;
- exact 29-relation `EXCLUSIVE MODE NOWAIT` admission/final lock sets, locked
  migration IDs and product versions, advisory/table-lock contention refusal,
  and catalog revalidation in the declared quiescent window;
- single-target pinning across admission, import, final verification,
  failed-state CAS, and reconciliation, with DNS/endpoint, postmaster,
  recovery-role, database, schema, role, and system-identifier drift refusing
  before sensitive writes or resolving uncertain commits as unknown;
- generated key replacement, reserved-state preservation, bootstrap-root
  equivalence, physical ConfigItems source-count-plus-one, and filtered logical
  digest equality;
- multi-batch import larger than the configured batch budget;
- binary ordinary batches and streaming spilled batches;
- all physical foreign keys with principals imported first;
- active health-stat triggers and exact derived digest;
- active delete/update triggers without accidental bootstrap cleanup rows;
- corruption or injected observer/COPY failure before and after a committed
  batch;
- adversarial COPY constraint/trigger failures whose PostgreSQL detail contains
  source values, and injected filesystem failures whose raw messages contain
  paths, proving no raw provider/POSIX exception escapes or is logged; the
  harness-owned server's startup-to-EOF captured `stderr` and the helper's
  Activity/logger/metrics negative-control captures must also omit every
  canary;
- a failed-authentication/preflight case proving that no transfer payload or
  manifest-digest canary is sent before validation, while explicitly treating
  server-owned role/database/client/fixed-application connection records as the
  documented pre-authentication exception rather than a false no-log claim;
- cancellation during parse, COPY, transaction commit, blob write, target
  verification, and pre-final-CAS boundaries;
- final verification catching a deliberately wrong target value, count,
  collation/order result, disabled trigger, catalog mutation, receipt-only
  spoof, extra blob entry, identity/mode change during an open verification
  pass, or changed blob content; a byte-identical replacement completed wholly
  between closed sessions is explicitly outside the inode-continuity claim;
- `failed(A)` durability after catchable failures;
- helper-process termination after arbitrary committed batches proving
  `importing(A)` survives and blocks reuse;
- abrupt harness-owned PostgreSQL postmaster/container termination immediately
  after acknowledged admission and final commits, followed by restart of the
  same owned data directory and proof that `importing(A)` or
  `database-verified(A)` plus corresponding rows survive without corruption;
- final CAS atomicity and the rule that no cancellation is observed after its
  commit;
- injected lost-acknowledgement admission commits resolving separately to
  `fresh`, `importing(A)`, and unreadable/other, plus final commits resolving to
  `database-verified(A)`, `importing(A)`, and unreadable/other; each asserts
  exact stage retention/deletion, failed-CAS suppression, and post-commit
  cleanup behavior;
- lost-acknowledgement cases that hold the original commit transaction in flight
  while a plain read could still see the old state, proving reconciliation waits
  for the advisory-lock fence, uses a later `READ COMMITTED` locking snapshot,
  and on timeout or identity drift returns unknown without failed CAS or
  destructive stage cleanup;
- exact round trip of all 27 tables plus blobs, including maximum practical
  synthetic text and blob fixtures;
- bounded-memory assertions and no retained sensitive spill after success or
  handled failure.

### Regression and isolation gates

- the complete existing Transfer-v3 Phase 1-3 suite;
- full backend build and test suite;
- exact PostgreSQL migration/catalog tests;
- Linux arm64 and macOS arm64 POSIX stage tests;
- source-level isolation canaries proving no Phase 4 reference from `Program`,
  entrypoint, runtime provider selection, `BlobStore`, public commands, or
  release workflows.

## Acceptance criteria

Phase 4 is complete only when all of the following are true:

1. A typed sealed Phase 3 stage can be imported into a new exact bootstrap-only
   disposable PostgreSQL target without EF row insertion.
2. Phase 4-owned managed memory obeys the exact 32-MiB accounting contract; any
   batch that would exceed it is promoted to the private spool and uses
   streaming COPY, blob handling retains no per-blob collection, and private
   logical staging writes remain within the required operator ceiling.
3. Earlier verified batches persist after a later failure, and the target is
   durably `failed(A)` or remains kill-durable `importing(A)`.
4. Source API keys replace generated keys, roots remain exact, all 27 table
   counts/digests match, and derived health statistics match.
5. Every manifest blob is durably reconstructed into a sealed private target
   stage whose receipt and freshly re-enumerated/rehashed tree match the
   manifest, with no runtime publication.
6. Independent verification and `importing(A) -> database-verified(A)` commit
   atomically under the exact 29-relation lock set and declared quiescent
   operator window, with `fsync`, full-page writes, and local synchronous commit
   enabled and crash-restart durability proven.
7. Source, environment, process, descriptor, and session time-zone identities
   match exactly; every connection matches the pinned single target; and unsafe
   client/server diagnostics settings refuse before sensitive target mutation.
8. `database-verified(A)` alone cannot start PostgreSQL runtime or authorize a
   transferred deployment.
9. No public v3 command, provider promotion, completion marker, runtime blob
   mutation, live database, or production service is introduced or touched.
10. All required synthetic, disposable PostgreSQL, regression, architecture,
    redaction, cancellation, and crash tests pass with zero unexpected warnings
    or failures, and the required PostgreSQL completion run has zero skips.
11. Admission and final lost-acknowledgement paths fence the original
    transaction before reading state; an unfenced, stale, drifted, or timed-out
    outcome is unknown and cannot trigger destructive cleanup.

## Follow-on boundary

Phase 5, not Phase 4, will define source stability rechecks, consumption of the
typed private blob stage, atomic publication into the target runtime blob root,
external completion-marker creation, sensitive-work cleanup, public migration
orchestration, and the final runtime gate. PostgreSQL remains disabled until
that later design is separately approved and production-like tests justify
promotion.
