# NZBDav Runtime And Migration Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make container maintenance commands reliable and preserve child failures, then make the existing offline SQLite-to-PostgreSQL transfer secure, functional, and independently reproducible.

**Architecture:** The root image remains the single runtime artifact. Its entrypoint distinguishes no-argument service startup from an allowlisted maintenance argv contract and directly `exec`s maintenance work. The existing Python migration helper orchestrates that contract over an explicit Docker network, keeps secrets out of argv/logs, creates a consistent private SQLite staging backup without mounting the rollback source into a container, uses a separate private config mount for PostgreSQL maintenance, runs maintenance as an explicit non-root migration identity, verifies canonical database and blob content, and removes sensitive artifacts by default.

**Tech Stack:** POSIX `sh`, Docker/BuildKit, Alpine Linux, .NET 10, EF Core 10, Python 3.9+ standard library, PostgreSQL 16

## Global Constraints

- Implement exactly the two tasks below; do not add role-split, rclone, proxy, Task 6, or unrelated CI work.
- Do not modify `.github/workflows/*`; the current CI was inspected only to understand the existing Docker build and forced-migration-failure smoke.
- Keep no-argument startup behavior, ports, health polling, generated frontend API key, PUID/PGID handling, and TERM/INT shutdown behavior compatible.
- Maintenance commands are only `--db-migration [target]`, `--db-export-json PATH`, and `--db-import-json PATH [--replace]`.
- Production migration images must be referenced by registry digest (`name@sha256:<64 hex characters>`); mutable-image opt-out is test-only.
- Real PostgreSQL migration commands must use an explicitly named Docker network; `Host=postgres` must resolve on that network.
- NZBDav must be stopped before source capture. Never mount the original SQLite config into a container and never open its database through SQLite, even read-only: the entrypoint changes ownership and a read-only WAL connection can still create or modify WAL/SHM state. Fingerprint and byte-copy the stable `db.sqlite` plus its exact existing `-wal`/`-shm` set into a private raw-capture directory without following links, fingerprint the source set again, and abort before any SQLite open if the set changed. Only then open the private raw copy, use SQLite's backup API to create a canonical staged database, verify it, and prove the original bytes, sidecar set, ownership, and modes did not change.
- Transfer JSON contains sensitive `ConfigItems`, `Accounts`, and `QueueItems`; work directories must be newly created outside the repository with mode `0700`, and snapshot/manifest files must be regular files with mode `0600` on a POSIX filesystem. Explicit work paths must be created through no-follow directory descriptors under a trusted parent; never call `resolve()` before checking for symlinks and never path-chmod/chown a caller-supplied directory.
- Before creating/chowning anything, open source and target config directories through component-wise no-follow directory descriptors and reject same-device/inode aliases, bind aliases, symlink components, or any source/target/work ancestry overlap. Reject source and target `blobs` aliases too. Retain the verified source/target descriptors and revalidate path-to-inode identity before copy/publication; `--replace` must never be able to delete the rollback source tree.
- Every helper-launched NZBDav container must receive an explicit non-root migration `PUID` and `PGID` that own the mode-`0700` staging/work tree. A non-root operator defaults to their own IDs; a root operator must supply the nonzero target-runtime identity and the helper may chown only newly created private staging entries. Seed/smoke invocations must use the same identity; test a deterministic non-default UID/GID through real bind-mount file creation. The operator-owned PostgreSQL config is never container-mounted or chowned; it is only the post-verification blob destination and must already be a non-symlink directory writable by that runtime identity.
- Never persist or print the PostgreSQL connection string, passwords, raw transfer rows, absolute source/target config paths, or Docker argv containing secrets. The helper may print the newly created private work path once to the operator on stderr for crash recovery, but must not persist it in the manifest or other log artifacts.
- Catchable normal, failure, HUP, INT, and TERM paths let the helper remove every current-run container proven by nonce label/private cidfile and attempt every sensitive final JSON, matching atomic-export temporary file, private SQLite capture, and private PostgreSQL maintenance config without following symlinks or masking the primary error. `--retain-snapshots` is an explicit operator choice for completed JSON only. If helper cleanup exceeds the wrapper deadline, the wrapper force-stops it, removes ownership-proven Docker state, marks the target/recovery state explicitly, and prints the known private work path; sensitive files may then require the documented manual audit. SIGKILL, daemon/host loss, and power loss have the same recovery requirement, so never claim cleanup on literally every exit.
- Keep Python 3.9 compatibility: use `datetime.timezone.utc` rather than `datetime.UTC`, avoid Python 3.10 union syntax, and run the helper suite with the declared minimum interpreter before release.
- Existing database transfer models, serialized format, and EF migrations are out of scope. The export service's file-creation path is in scope because post-export `chmod` leaves a disclosure window and cannot repair an already-readable destination atomically.

## File Map

- `Dockerfile`: declare the root script as the image entrypoint so Docker/Compose command arguments reach it.
- `entrypoint.sh`: validate/dispatch maintenance arguments, preserve the first detected child exit status, and retain normal startup/signal behavior.
- `tests/test_entrypoint_contract.sh`: fast shell regression for child status and Dockerfile metadata.
- `tests/test_entrypoint_container.sh`: real-image maintenance and normal shutdown smoke.
- `backend/Database/DatabaseTransferService.cs`: publish exports atomically from a mode-`0600` temporary file on POSIX systems.
- `backend.Tests/Database/DatabaseTransferServiceTests.cs`: prove new and overwritten exports are never left group/world-readable.
- `scripts/nzbdav_migrate_sqlite_to_postgres.py`: secure orchestration, verification, redaction, retention, and cleanup.
- `tests/test_nzbdav_migrate_sqlite_to_postgres.py`: deterministic Python unit coverage.
- `tests/test_nzbdav_migration_container.sh`: real SQLite export to networked PostgreSQL import and verification.
- `tests/test_nzbdav_migration_runbook.sh`: isolated shell lifecycle tests for secrets, signals, ownership, and recovery.
- `docs/setup-guide.md`: operator-safe digest, network, secret, retention, and cleanup instructions.

---

### Task 1: Preserve The Root Container Runtime Contract

**Files:**
- Modify: `Dockerfile:52-69`
- Modify: `entrypoint.sh:1-175`
- Create: `tests/test_entrypoint_contract.sh`
- Create: `tests/test_entrypoint_container.sh`

**Interfaces:**
- Consumes: backend arguments already implemented in `backend/Program.cs:68-96`.
- Produces: image metadata `ENTRYPOINT ["/entrypoint.sh"]`, empty `CMD`, `validate_maintenance_args()`, and `wait_either(pid1, pid2)` that sets `EXITED_PID`/`REMAINING_PID` and returns the reaped child's exact status.

- [x] **Step 1: Write the failing shell contract test**

Create `tests/test_entrypoint_contract.sh`:

```sh
#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
tmp=$(mktemp "${TMPDIR:-/tmp}/nzbdav-entrypoint.XXXXXX")
cleanup() { rm -f "$tmp"; }
trap cleanup EXIT HUP INT TERM

awk '/^wait_either\(\) \{/{copy=1} copy{print} copy && /^}/{exit}' \
  "$repo_root/entrypoint.sh" > "$tmp"
. "$tmp"

(exit 23) & first=$!
(sleep 30) & second=$!
set +e
wait_either "$first" "$second"
status=$?
set -e
[ "$status" -eq 23 ]
[ "$EXITED_PID" -eq "$first" ]
[ "$REMAINING_PID" -eq "$second" ]
kill "$second" 2>/dev/null || true
wait "$second" 2>/dev/null || true

(sleep 30) & first=$!
(exit 29) & second=$!
set +e
wait_either "$first" "$second"
status=$?
set -e
[ "$status" -eq 29 ]
[ "$EXITED_PID" -eq "$second" ]
[ "$REMAINING_PID" -eq "$first" ]
kill "$first" 2>/dev/null || true
wait "$first" 2>/dev/null || true

grep -Fqx 'ENTRYPOINT ["/entrypoint.sh"]' "$repo_root/Dockerfile"
grep -Fqx 'CMD []' "$repo_root/Dockerfile"
echo "entrypoint contract: PASS"
```

- [x] **Step 2: Run it and preserve the failure evidence**

Run:

```bash
/bin/sh tests/test_entrypoint_contract.sh
```

Expected: nonzero. The current `wait_either` returns `0` because assignments overwrite `$?`, and the current Dockerfile has only `CMD ["/entrypoint.sh"]`.

- [x] **Step 3: Make Docker pass command arguments to the entrypoint**

Replace the final Dockerfile instruction with:

```dockerfile
ENTRYPOINT ["/entrypoint.sh"]
CMD []
```

Do not change build stages, packages, exposed ports, or environment defaults.

- [x] **Step 4: Refactor the entrypoint around an explicit main/maintenance boundary**

In `entrypoint.sh`, capture `wait` immediately and add strict maintenance validation:

```sh
wait_either() {
    local pid1=$1
    local pid2=$2
    local child_status

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            child_status=$?
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return "$child_status"
        fi
        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            child_status=$?
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return "$child_status"
        fi
        sleep 0.5
    done
}

maintenance_usage() {
    echo "Usage: /entrypoint.sh [--db-migration [target] | --db-export-json PATH | --db-import-json PATH [--replace]]" >&2
}

validate_maintenance_args() {
    case "${1:-}" in
        --db-migration)
            [ "$#" -le 2 ] && { [ "$#" -eq 1 ] || [ "${2#--}" = "$2" ]; } || return 64
            ;;
        --db-export-json)
            [ "$#" -eq 2 ] && [ -n "$2" ] || return 64
            ;;
        --db-import-json)
            [ "$#" -eq 2 ] || { [ "$#" -eq 3 ] && [ "$3" = "--replace" ]; } || return 64
            [ -n "$2" ] || return 64
            ;;
        *) return 64 ;;
    esac
}

run_maintenance() {
    if ! validate_maintenance_args "$@"; then
        maintenance_usage
        return 64
    fi
    umask 077
    cd /app/backend || return 70
    exec su-exec "$USER_NAME" ./NzbWebDAV "$@"
}
```

Keep all functions at file scope. Delete the current file-scope `trap terminate TERM INT` at line 39, insert `main() {` immediately before the line-41 `# Use env vars or default to 1000` block, and make `trap terminate TERM INT` the first statement in `main`. Leave the existing lines 41-108 in their current order. Immediately after the config-ownership block's closing `fi` at current line 108, insert:

```sh
if [ "$#" -gt 0 ]; then
    run_maintenance "$@"
    return $?
fi
```

Leave the current automatic migration, backend health loop, and frontend startup (current lines 110-158) in their existing order. Replace the current lines 160-175, close `main`, and add the source-only guard with:

```sh
    wait_either "$BACKEND_PID" "$FRONTEND_PID"
    EXIT_CODE=$?
    if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
        echo "The web-frontend has exited. Shutting down the web-backend..."
    else
        echo "The web-backend has exited. Shutting down the web-frontend..."
    fi
    kill "$REMAINING_PID" 2>/dev/null || true
    wait "$REMAINING_PID" 2>/dev/null || true
    return "$EXIT_CODE"
}

if [ "${NZBDAV_ENTRYPOINT_SOURCE_ONLY:-0}" != "1" ]; then
    main "$@"
    exit $?
fi
```

Do not put `set -e` at file scope: maintenance failures and child statuses are captured explicitly, while the existing health-loop control flow remains compatible. Maintenance uses `exec`, so TERM/INT reaches `NzbWebDAV` directly.

- [x] **Step 5: Add the real-image smoke**

Create `tests/test_entrypoint_container.sh` that accepts `NZBDAV_TEST_IMAGE` (default `nzbdav:entrypoint-smoke`) and performs these exact assertions:

```sh
#!/bin/sh
set -eu
image=${NZBDAV_TEST_IMAGE:-nzbdav:entrypoint-smoke}
root=$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-entrypoint-container.XXXXXX")
name="nzbdav-entrypoint-smoke-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$root"; }
trap cleanup EXIT HUP INT TERM
mkdir -m 700 "$root/config" "$root/transfer" "$root/normal-config"

metadata=$(docker image inspect --format '{{json .Config.Entrypoint}} {{json .Config.Cmd}}' "$image")
case "$metadata" in
  '["/entrypoint.sh"] null'|'["/entrypoint.sh"] []') ;;
  *) exit 1 ;;
esac

docker run --rm -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -v "$root/config:/config" -v "$root/transfer:/transfer" \
  "$image" --db-migration
docker run --rm -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -v "$root/config:/config" -v "$root/transfer:/transfer" \
  "$image" --db-export-json /transfer/snapshot.json
python3 - "$root/transfer/snapshot.json" <<'PY'
import pathlib, stat, sys
p = pathlib.Path(sys.argv[1])
assert p.is_file()
assert stat.S_IMODE(p.stat().st_mode) == 0o600
PY

set +e
docker run --rm "$image" /bin/sh >"$root/rejected.log" 2>&1
status=$?
set -e
[ "$status" -eq 64 ]

docker run -d --name "$name" -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -e FRONTEND_BACKEND_API_KEY=entrypoint-smoke \
  -v "$root/normal-config:/config" -p 127.0.0.1::3000 "$image" >/dev/null
port=$(docker port "$name" 3000/tcp | awk -F: 'NR == 1 { print $NF }')
i=0
until curl -fsS "http://127.0.0.1:$port/health" >/dev/null; do
  i=$((i + 1)); [ "$i" -lt 90 ] || exit 1; sleep 1
done
docker stop -t 20 "$name" >/dev/null
[ "$(docker inspect --format '{{.State.ExitCode}}' "$name")" -eq 0 ]
echo "entrypoint container smoke: PASS"
```

- [x] **Step 6: Run the Task 1 gates**

Run:

```bash
/bin/sh tests/test_entrypoint_contract.sh
docker build -t nzbdav:entrypoint-smoke .
NZBDAV_TEST_IMAGE=nzbdav:entrypoint-smoke /bin/sh tests/test_entrypoint_container.sh
```

Expected: `entrypoint contract: PASS`, a successful image build, and `entrypoint container smoke: PASS`. Docker must have no `nzbdav-entrypoint-smoke-*` container afterward.

- [x] **Step 7: Commit Task 1**

```bash
git add Dockerfile entrypoint.sh tests/test_entrypoint_contract.sh tests/test_entrypoint_container.sh
git commit -m "fix: preserve container maintenance and child exit contracts"
```

Execution note (2026-07-11): completed as commit `d7395a5`. RED proved that a
child exit of 23 was returned as 0 and that option-looking export/import paths
could dispatch a different maintenance operation. The final implementation also
rejects path arguments beginning with `--`, bounds and names every smoke
container, exits through cleanup on signals, and applies a per-attempt curl
timeout. Docker/OCI may normalize source `CMD []` to runtime `Cmd: null`; the
source contract requires literal `CMD []`, while the image test accepts either
equivalent empty representation. Fast shell, rebuilt-image maintenance/export,
mode-`0600`, rejection, health, TERM shutdown, diff, and cleanup gates passed.

---

### Task 2: Secure And Prove SQLite-To-PostgreSQL Migration

**Execution gate:** do not start this task until the PostgreSQL migration
architecture is chosen. The current shared SQLite-scaffolded chain leaves at
least 28 identifier-like PostgreSQL columns as `text` and cannot complete the
required real transfer smoke safely. The user must confirm whether any real
PostgreSQL database contains data and define legacy timestamp timezone
semantics; the implementation must then use either a native greenfield
PostgreSQL baseline or an offline clone/bridge/cutover design. Unit-only helper
work must not be presented as completing this task. Before any schema code or
real transfer execution, create and independently review a separate
provider-migration design/rehearsal/validation/rollback plan; this runtime plan
intentionally does not authorize EF migration changes.

**Files:**
- Modify: `backend/Database/DatabaseTransferService.cs:14-58`
- Modify: `backend.Tests/Database/DatabaseTransferServiceTests.cs`
- Modify: `scripts/nzbdav_migrate_sqlite_to_postgres.py:1-244`
- Modify: `tests/test_nzbdav_migrate_sqlite_to_postgres.py:1-103`
- Create: `tests/test_nzbdav_migration_container.sh`
- Create: `tests/test_nzbdav_migration_runbook.sh`
- Modify: `docs/setup-guide.md:112-158`

**Interfaces:**
- Consumes: Task 1's maintenance argv contract and the existing `DatabaseTransferService` JSON format.
- Produces: atomic mode-`0600` export publication on POSIX, a verified private SQLite backup, a private PostgreSQL maintenance config, explicit non-root `--migration-uid`/`--migration-gid`, `--docker-network`, required `--image`, test-only `--allow-mutable-image`, optional `--work-dir`, optional `--retain-snapshots`, private `manifest.json`, end-of-run source stability proof, and content-equivalent PostgreSQL/database-blob verification.

- [ ] **Step 1: Add failing backend tests for secure atomic export publication**

In `backend.Tests/Database/DatabaseTransferServiceTests.cs`, add POSIX-only tests that export to a new path and over an existing mode-`0644` path, then assert the published regular file is mode `0600` and deserializes successfully. The overwrite case is mandatory: relying on process `umask` with `File.Create` does not tighten an existing file. Keep the serialized snapshot contract unchanged.

Run:

```bash
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~DatabaseTransferServiceTests
```

Expected: the new overwrite-mode assertion fails against `File.Create`.

- [ ] **Step 2: Publish backend exports atomically with mode `0600`**

In `DatabaseTransferService.ExportJsonAsync`, serialize to a uniquely named `CreateNew` temporary file in the destination directory. Use the deterministic cleanup-recognizable shape `.<destination-file-name>.<guid>.tmp`. On POSIX, create it with `FileStreamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite`; on Windows, leave `UnixCreateMode` unset. Flush and close the stream before atomically replacing the requested destination with `File.Move(tempPath, outputPath, overwrite: true)`, and remove the temporary file in `finally`. Do not write sensitive bytes to the old destination before its permissions are tightened, do not change the JSON schema, and do not chmod the containing operator-owned directory.

Re-run the focused backend tests. Expected: all `DatabaseTransferServiceTests` pass, including new-file and overwrite mode assertions on POSIX.

- [ ] **Step 3: Add failing helper unit tests for every security boundary**

Extend `tests/test_nzbdav_migrate_sqlite_to_postgres.py` with tests that assert:

```python
def test_rejects_mutable_production_image(self):
    with self.assertRaises(SystemExit):
        migrate.validate_image("ghcr.io/binghzal/nzbdav:latest", allow_mutable=False)
    migrate.validate_image(
        "ghcr.io/binghzal/nzbdav@sha256:" + "a" * 64,
        allow_mutable=False,
    )

def test_postgres_invocation_uses_network_without_secret_in_argv(self):
    invocation = migrate.docker_invocation(
        image="nzbdav:test",
        config_dir=pathlib.Path("/tmp/config"),
        work_dir=pathlib.Path("/tmp/work"),
        app_args=["--db-export-json", "/transfer/verify.json"],
        docker_network="migration-net",
        postgres_connection_string="Host=postgres;Password=sentinel-password",
        host_uid=501,
        host_gid=20,
    )
    self.assertIn("--network", invocation.argv)
    self.assertIn("migration-net", invocation.argv)
    self.assertNotIn("sentinel-password", " ".join(invocation.argv))
    self.assertEqual(
        "Host=postgres;Password=sentinel-password",
        invocation.environment["NZBDAV_DATABASE_CONNECTION_STRING"],
    )
    self.assertEqual("501", invocation.environment["PUID"])
    self.assertEqual("20", invocation.environment["PGID"])
    self.assertIn("PUID", invocation.argv)
    self.assertIn("PGID", invocation.argv)
    self.assertNotIn("PUID=501", invocation.argv)

def test_private_work_dir_is_outside_repo_and_mode_0700(self):
    uid = os.geteuid() or 12345
    gid = os.getegid() or 12345
    work = migrate.create_private_work_dir(None, uid=uid, gid=gid)
    self.addCleanup(migrate.destroy_private_work_dir, work)
    self.assertNotIn(migrate.REPO_ROOT, work.path.parents)
    self.assertEqual(stat.S_IMODE(os.fstat(work.dir_fd).st_mode), 0o700)

def test_secure_json_is_mode_0600_and_manifest_is_redacted(self):
    with tempfile.TemporaryDirectory() as directory:
        path = pathlib.Path(directory) / "manifest.json"
        migrate.secure_write_json(path, {"status": "passed", "total_rows": 3})
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertNotIn("sentinel-secret", path.read_text(encoding="utf-8"))

def test_content_verification_rejects_equal_total_with_changed_row(self):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        source = root / "source.json"
        target = root / "target.json"
        source.write_text(json.dumps({"ConfigItems": [{"ConfigName": "api.key", "ConfigValue": "a"}]}))
        target.write_text(json.dumps({"ConfigItems": [{"ConfigName": "api.key", "ConfigValue": "b"}]}))
        with self.assertRaises(SystemExit):
            migrate.verify_snapshots(source, target)

def test_default_cleanup_removes_sensitive_snapshots_only(self):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        for name in ("nzbdav-transfer.json", "nzbdav-postgres-verify.json", "manifest.json"):
            (root / name).write_text("{}", encoding="utf-8")
        migrate.cleanup_sensitive_artifacts(root, retain_snapshots=False)
        self.assertFalse((root / "nzbdav-transfer.json").exists())
        self.assertFalse((root / "nzbdav-postgres-verify.json").exists())
        self.assertTrue((root / "manifest.json").exists())

def test_cleanup_attempts_all_paths_without_following_symlinks(self):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        outside = root / "outside-secret.json"
        outside.write_text("secret", encoding="utf-8")
        (root / "nzbdav-transfer.json").symlink_to(outside)
        (root / "nzbdav-postgres-verify.json").write_text("secret", encoding="utf-8")
        errors = migrate.cleanup_sensitive_artifacts(root, retain_snapshots=False)
        self.assertTrue(errors)
        self.assertTrue(outside.exists())
        self.assertFalse((root / "nzbdav-transfer.json").exists())
        self.assertFalse((root / "nzbdav-postgres-verify.json").exists())
```

Also add an orchestration test that forces the import/verification runner to
fail after both sensitive paths exist, then proves both paths are removed, the
primary controlled failure is preserved, and the mode-`0600` failure manifest
and captured logs contain no sentinel. Import the module as `migrate` and add
`os`, `shutil`, and `stat` imports. Replace the old command-redaction test; connection
strings must never enter argv in the first place.

Add identity and source-safety tests that:

- reject effective UID 0 unless an explicit nonzero migration UID/GID pair is
  supplied;
- reject a partial pair or a different identity that a non-root operator cannot
  own;
- create/chown every newly created private staging directory/file to the
  resolved migration identity without changing either operator-owned config;
- reject an explicit work directory that exists, traverses a symlink, has an
  untrusted writable parent, or is swapped between creation and use; prove
  chmod/chown uses the opened directory descriptor rather than a resolved path;
- use a private PostgreSQL maintenance config for migration/import/verify and
  prove the operator `--postgres-config` path is never mounted or chowned;
- reject symlinked or non-regular source database/sidecar paths;
- byte-copy a stopped SQLite database and its exact existing WAL/SHM set into a
  private raw-capture directory without ever passing an original source path to
  `sqlite3.connect`;
- cover both an absent-sidecar database and a retained-WAL database with
  committed rows, then open only the private raw copy and use SQLite's backup
  API to create the canonical staged config;
- run `PRAGMA integrity_check` on the staged database; and
- compare pre/post SHA-256, size, UID, GID, and mode for `db.sqlite` and any
  existing `db.sqlite-wal`/`db.sqlite-shm`, rejecting any source mutation or
  sidecar-set change before the raw copy is opened through SQLite.

Add lifecycle and end-state tests that:

- capture both database/sidecar and blob-tree baselines before the first Docker
  operation, mutate either source after export but before import or verification
  completes, and prove the run cannot report success;
- reject symlink/non-regular entries in the source blob tree, verify a
  deterministic relative-path/size/SHA-256 tree digest at source and target,
  and reject a source blob mutation during the run;
- verify root execution never changes ownership/mode of the real PostgreSQL
  config and newly copied blobs are created by the target runtime identity;
- replace the target pathname after descriptor validation and prove publication
  aborts on the device/inode mismatch without writing/chowning the replacement;
- assign every Docker operation a controlled name/label, simulate HUP, INT, and
  TERM, and prove cleanup force-removes active helper containers; and
- leave matching `.<snapshot>.<guid>.tmp` files at multiple phases, then prove
  cleanup attempts all of them plus both private config trees while preserving
  the primary failure and not following a planted link.

Before any creation, add same-directory, bind-alias, symlink-component,
source/target nesting, target-blob-to-source-blob alias, and explicit-work-path-
under-source/target tests. Every case must fail before mkdir, chmod/chown, or
Docker, and `--replace` must leave the source database and blob tree byte-for-byte
unchanged.

- [ ] **Step 4: Run the helper tests and confirm the missing contracts**

Run:

```bash
python3 -m unittest tests.test_nzbdav_migrate_sqlite_to_postgres -v
```

Expected: failures for the new interfaces; the existing seven tests remain green.

- [ ] **Step 5: Implement private files, digest validation, networked invocations, and sanitized failures**

In `scripts/nzbdav_migrate_sqlite_to_postgres.py`:

```python
import hashlib
import re
import secrets
import signal
import stat
import tempfile
from dataclasses import dataclass
from typing import Any, Optional

REPO_ROOT = Path(__file__).resolve().parents[1]
IMAGE_DIGEST = re.compile(r"^.+@sha256:[0-9a-f]{64}$")
SENSITIVE_SNAPSHOT_NAMES = ("nzbdav-transfer.json", "nzbdav-postgres-verify.json")
STAGED_SQLITE_DIR_NAME = "sqlite-source-stage"
POSTGRES_MAINTENANCE_CONFIG_DIR_NAME = "postgres-maintenance-config"
ATOMIC_TEMP_FILE = re.compile(
    r"^\.(nzbdav-transfer\.json|nzbdav-postgres-verify\.json|manifest\.json)\.[0-9a-f-]+\.tmp$"
)
PRIVATE_CIDFILE = re.compile(r"^\.container-[a-z0-9-]+\.cid$")
CONTAINER_NAME = re.compile(r"^nzbdav-migration-[a-z0-9-]+$")

@dataclass(frozen=True)
class DockerInvocation:
    operation: str
    container_name: str
    run_nonce: str
    cidfile_name: str
    argv: list[str]
    environment: dict[str, str]

@dataclass
class PrivateWorkDir:
    path: Path
    dir_fd: int
    device: int
    inode: int

def validate_image(image: str, *, allow_mutable: bool) -> None:
    if not allow_mutable and not IMAGE_DIGEST.fullmatch(image):
        raise SystemExit("--image must use an immutable @sha256 digest; --allow-mutable-image is test-only.")
```

Before `create_private_work_dir`, open and retain the source/target config
directory descriptors using a component-wise no-follow walk. Compare device,
inode, and ancestor chains (including existing blob directories), and validate
the proposed explicit work parent; reject all aliases/overlap before any leaf is
created. Implement `create_private_work_dir` around `PrivateWorkDir`, not a
resolved `Path`. The default uses `mkdtemp`; an explicit path must not exist and
its parent is walked component-by-component from an open directory descriptor
with `O_DIRECTORY | O_NOFOLLOW`. Reject a symlink component, a parent writable by
an untrusted user, and any path within the repository, source, or target tree.
Create the leaf with
descriptor-relative `mkdir`, open it with `O_DIRECTORY | O_NOFOLLOW`, compare
`lstat` with `fstat`, and use only `fchmod`/`fchown` on that descriptor. Record
its device/inode and revalidate that identity before every Docker bind. Create,
replace, enumerate, and remove sensitive children relative to `dir_fd`; never
chmod/chown them by a caller-re-resolved pathname. `destroy_private_work_dir`
closes the descriptor only after descriptor-relative cleanup is complete.

Implement `secure_write_json` as a mode-`0600` `O_CREAT | O_EXCL | O_NOFOLLOW`
temporary beneath `dir_fd`, fsync it, rename it within the same directory, and
fsync the directory. `cleanup_sensitive_artifacts` must use descriptor-relative
`lstat`/unlink/tree removal to attempt both private config directory names, all
unretained final snapshots, and every `ATOMIC_TEMP_FILE`/`PRIVATE_CIDFILE`
match. It must unlink a
link itself without following it, continue after each cleanup error, and always
remove matching temporary files even when completed snapshots are retained.
Track nonce-owned blob stage/rollback directories separately under the retained
target descriptor: remove unpublished stage directories on all supported exits,
restore a prior rollback tree on pre-success failure, and delete the rollback
tree only during successful finalization. Never glob/delete an unproven target
entry and never follow a replacement pathname.

Change CLI parsing so `--image` is required, `--docker-network` is required for non-dry runs, the connection string comes only from `NZBDAV_DATABASE_CONNECTION_STRING`, and `--allow-mutable-image`/`--retain-snapshots` are explicit flags. Reject `--replace` without `--confirm-disposable-target`. Generate a 128-bit `secrets.token_hex` nonce per run, or accept a wrapper-supplied `--run-nonce` only when it is exactly 32 lowercase hex characters. Replace `docker_command`/`run` with a `DockerInvocation` builder that adds the explicit network, a nonce-derived controlled `--name`, `--label nzbdav.migration.run=<nonce>`, an absent descriptor-validated `--cidfile` path inside the private work directory, and `--rm`; passes `-e NZBDAV_DATABASE_CONNECTION_STRING` without a value; and places the actual connection string only in the child environment. After Docker creates the cidfile, require a no-follow regular-file read from the private directory. Catch nonzero Docker exits and report only the controlled operation name and exit code; never stringify argv or `CalledProcessError`.

Container cleanup must inspect ownership before removal. Require the exact
nonce label; if the private cidfile exists, also require its valid container ID
to match the inspected ID. A missing cidfile may fall back to the unguessable
current-run label to cover interruption during Docker creation, but a missing or
mismatched label is never removed. Therefore an exact pre-existing `--name`
collision survives a failed `docker run`. Remove/unlink the cidfile only after
the owned container is gone, and test exact-name collision, lookalike name,
mismatched label, missing cidfile, and matching cidfile/label cases.

Install HUP/INT/TERM handlers that raise a controlled interruption into the
single orchestration path. Its outermost `finally` first force-removes only
nonce/cidfile-proven helper containers, then attempts all sensitive artifact
cleanup. Preserve the first migration/interruption status even if container or
file cleanup fails; if there was no primary failure, return a controlled cleanup
failure. Unit tests must mock delivery while `docker run` is active. Document
honestly that SIGKILL or host/power loss bypasses process cleanup and requires
the recovery audit in the runbook.

For the operator wrapper, accept `NZBDAV_MIGRATION_READY_FILE`: validate the
pre-created mode-`0600` regular file without following links, install the Python
signal handlers, make repeat signals no-ops once cleanup starts, then write and
fsync `ready\n` before creating/chowning work state or invoking Docker. The
wrapper defers any early signal until this handshake, forwards it exactly once,
and removes the ready file. This prevents an async Bash child from losing an
early SIGINT inherited as ignored and prevents a repeated signal from re-entering
Python cleanup.

Add `resolve_migration_identity(uid_arg, gid_arg)` so a non-root operator uses
their own nonzero IDs and cannot request a different owner, while effective UID
0 must provide a complete explicit nonzero pair. The builder adds `-e PUID -e
PGID` without inline values and places the resolved IDs only in the child
environment. Create and chown only newly created private staging/work entries to
that pair.

Create `postgres-maintenance-config` inside the private work tree and use it as
`/config` for the PostgreSQL `--db-migration`, import, and verification export
containers. The operator `--postgres-config` path is never passed to Docker;
it is only the verified post-database-migration blob destination. Require that
path to pre-exist as a non-symlink directory owned and writable by the selected
target-runtime UID/GID. Open every target path component with no-follow
directory descriptors, retain the final device/inode, perform blob staging and
publication descriptor-relatively, and revalidate the pathname against that
same device/inode before and after publication. Also prove before Docker that
this identity can traverse/read the retained source database/blob inputs needed
for the final comparisons. A root helper must permanently drop supplementary
groups, GID, and UID to that identity after the last Docker operation and before
copying blobs or writing final artifacts, so it cannot chown or bypass
permissions on the existing target config. Require the tracked-container set to
be empty before that irreversible privilege drop. Immediately after the final
PostgreSQL Docker operation—and in the outer failure cleanup—remove
`NZBDAV_DATABASE_CONNECTION_STRING` from `os.environ`, delete/clear every local
connection-string reference and each `DockerInvocation.environment`, and retain
only redacted operation/status metadata. Python cannot promise physical memory
zeroization, but no live secret reference may cross the privilege drop or enter
blob/final-manifest work. The operation order is: initial database/sidecar/blob
baseline, canonical SQLite export, PostgreSQL migration, PostgreSQL import,
PostgreSQL verification export, canonical table comparison, baseline recheck,
verified staged blob copy/publication, final source-stability check, cleanup,
then final audited manifest.

Implement `stage_sqlite_backup` before any container invocation, with two
separate private locations below `sqlite-source-stage`: `raw/` for the physical
capture and `config/` for the canonical database mounted into NZBDav.

1. With NZBDav stopped, open the source config by component-wise no-follow
   directory descriptors. Enumerate only `db.sqlite`, `db.sqlite-wal`, and
   `db.sqlite-shm`; require the main file, reject symlinks/non-regular files,
   and capture each existing file's SHA-256/size/UID/GID/mode plus the exact
   name set. In the same initial baseline phase, descriptor-walk `blobs`, reject
   links/non-regular entries, and retain its relative-path/size/SHA-256 tree
   digest. Use descriptor-relative, no-follow file opens plus `fstat`; do not
   re-resolve attacker-swappable source pathnames during a pass. Use no SQLite
   connection against these paths.
2. Byte-copy from those verified descriptors into `raw/` and verify the copied
   bytes against the first hashes. Fsync every copied file and the raw
   directory.
3. Recapture the complete database/sidecar fingerprint, exact name set, and blob
   tree digest. If anything differs, remove the raw capture and abort. This
   check happens before any copied database is opened through SQLite; it detects
   a source that was not actually quiescent without risking mutation of the
   rollback files.
4. Open only `raw/db.sqlite` in the writable private directory. Use
   `sqlite3.Connection.backup` to create `config/db.sqlite`, so committed WAL
   pages are incorporated into a canonical database. Never use `immutable=1`
   to ignore a WAL and never checkpoint the original source.
5. Run `PRAGMA integrity_check` against `config/db.sqlite`, fsync the canonical
   file and config directory, and repeat both database/sidecar and blob-tree
   baselines immediately before the first Docker invocation. Abort if either
   source changed at any point.
6. Recheck the blob baseline before copy, verify the published target digest,
   and repeat both source baselines after PostgreSQL verification/blob
   publication, immediately before final cleanup and manifest publication. If
   bytes, names, ownership, modes, or blob content changed during any phase,
   report a controlled failure; never claim success from a stale capture.

Only `sqlite-source-stage/config` and `postgres-maintenance-config` are ever
bind-mounted at `/config`; neither operator-owned config nor `raw/` is passed to
Docker. Every supported cleanup path attempts all three private locations.
Replace the helper's existing `dt.UTC` usage with `dt.timezone.utc` and use
Python-3.9-compatible type syntax.

The WAL source-safety rationale is grounded in SQLite's official
[WAL documentation](https://www.sqlite.org/wal.html), and canonicalization uses
the official [online backup API](https://www.sqlite.org/backup.html) only after
the physical source has been captured and proven stable.

- [ ] **Step 6: Verify canonical table contents and write only a redacted manifest**

Replace total-only checking with order-independent table fingerprints:

```python
def snapshot_fingerprints(path: Path) -> dict[str, tuple[int, str]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    result: dict[str, tuple[int, str]] = {}
    for table, rows in document.items():
        if not isinstance(rows, list):
            continue
        canonical = sorted(
            json.dumps(row, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
            for row in rows
        )
        digest = hashlib.sha256("\n".join(canonical).encode("utf-8")).hexdigest()
        result[table] = (len(rows), digest)
    return result

def verify_snapshots(source: Path, target: Path) -> dict[str, Any]:
    source_tables = snapshot_fingerprints(source)
    target_tables = snapshot_fingerprints(target)
    if source_tables != target_tables:
        raise SystemExit("PostgreSQL verification failed: table counts or canonical row content differ.")
    return {
        "status": "passed",
        "total_rows": sum(count for count, _ in source_tables.values()),
        "table_counts": {name: value[0] for name, value in sorted(source_tables.items())},
        "content_match": True,
    }
```

Preflight both final snapshot names and all cleanup-recognized temporary names
before the first container runs. After each container export, reject
symlinks/non-regular files and verify mode `0600`; do not rely on a post-write
chmod to close disclosure. Build `manifest.json` only from controlled fields:
run ID, timestamp, digest image reference, status/failure phase, verification
summary, blob file/byte counts and aggregate content digest, cache-excluded
boolean, dry-run boolean, whether snapshots were retained, `target_tainted`,
and redacted cleanup counts. Remove from the manifest all absolute config/work
paths, raw commands, container IDs,
connection strings, raw rows, and blob source/target paths.

Before blob copy, compare the host source `<sqlite-config>/blobs` to the initial
baseline. Copy without following links as the target runtime identity into a
nonce-named staging directory beneath the already-open target config descriptor
on the same filesystem. Verify its order-independent relative-path/size/SHA-256
digest before publication. Publish with descriptor-relative atomic renames and
fsync the target config directory. If an old `blobs` tree is allowed, rename it
to a nonce-owned rollback name first and restore it on any pre-success failure;
never recursively delete the old tree before the new tree is durable. Recheck
the source baseline and target digest after publication.

The database import commits before blob publication, so the target must remain
offline and is considered tainted after its first PostgreSQL mutation until the
entire run succeeds. Default runs require an empty disposable target.
`--replace` is rejected unless paired with an explicit
`--confirm-disposable-target`; it is never an authorization to overwrite a real
PostgreSQL deployment with data. On any later failure, publish
`target_tainted=true`, restore/quarantine blob directories, and require the
runbook's drop/recreate/reapply-schema reset of the disposable PostgreSQL target
before retry. Never start or reuse that target in place. Existing-data cutover
and rollback belong to the separately reviewed provider-migration plan. Preserve
cache exclusion; never report success until target verification and the final
database/sidecar/blob source fingerprints are unchanged.

Hold controlled manifest fields in memory while work is active. In the outermost
finalization, remove only ownership-proven containers, clear database secrets,
and call `cleanup_sensitive_artifacts`; completed JSON is removed unless
`--retain-snapshots` was supplied, while cidfiles, atomic temps, and both private
config trees are always attempted. Only after cleanup finishes, derive the final
status and publish `manifest.json` atomically with redacted cleanup counts. A
primary failure, target-tainted state, or any sensitive cleanup failure can
never leave `status=passed`. The manifest writer must remove its own temporary
file in a nested `finally` on write/signal failure. Preserve the original
operation/signal exit status after attempting the final failure manifest; if
there was no primary failure, return a controlled cleanup or manifest-publication
failure.

- [ ] **Step 7: Make the unit suite green**

Run:

```bash
python3 -m unittest tests.test_nzbdav_migrate_sqlite_to_postgres -v
```

Expected: all migration-helper tests pass; no test output contains `sentinel-password`.

- [ ] **Step 8: Add the real networked transfer smoke**

Create `tests/test_nzbdav_migration_container.sh`. It must:

1. Require/use `NZBDAV_TEST_IMAGE` (default `nzbdav:migration-smoke`).
2. Create a mode-`0700` `mktemp -d` tree outside the repository and an explicit unique Docker network. Select explicit `MIGRATION_TEST_UID`/`MIGRATION_TEST_GID` values that are nonzero and not 1000; prepare/chown only new private test entries through a root init container when the host runner does not already own that deterministic pair. Pre-create the real target config with that identity and record its inode, ownership, and mode.
3. Pull `postgres:16-alpine`, resolve its `RepoDigest`, and start it on that network with alias `postgres`; wait for `pg_isready`.
4. Write a mode-`0600` version-2 seed JSON containing one `Account`, one sensitive `ConfigItem`, and one `QueueItem` with an archive-password sentinel. Import it into a source SQLite config using the deterministic non-default IDs, stop that container, and prepare two source cases: one clean database with no sidecars and one database with committed rows retained in WAL plus its exact sidecar set.
5. For each source case, record SHA-256, size, UID, GID, and mode for `db.sqlite` and every existing WAL/SHM sidecar. Run the helper with the deterministic `--migration-uid`/`--migration-gid`, `Host=postgres`, the explicit network, `--allow-mutable-image`, and `--retain-snapshots`. Assert no original source path was opened through SQLite or mounted into an NZBDav container, the private raw capture matched the first fingerprint before normalization, the committed WAL row was present in the canonical export, and the original complete pre/post fingerprint and sidecar name set were identical through the final success check.
6. Capture each helper invocation's mount specification before its `--rm` container exits. Assert SQLite export used only the canonical private config; PostgreSQL migrate/import/verify used only `postgres-maintenance-config`; neither real operator config was mounted; and the target config inode, ownership, and mode stayed unchanged. Assert source/verify JSON and manifest are `0600`, work dir is `0700`, both exports contain identical sentinel rows, the source/target blob tree digests match, newly copied blobs have the target runtime identity, and manifest/logs contain none of the account/config/archive-password/database-password sentinels.
7. Prove `--replace` alone is rejected before mutation. Then reset the disposable target and run a second helper pass with `--replace --confirm-disposable-target` and without `--retain-snapshots`; assert only `manifest.json` remains and reports `status=passed`, `target_tainted=false`, `content_match=true`, and counts of one for `Accounts`, `ConfigItems`, and `QueueItems`.
8. Assert the staged SQLite database is absent after both retained-JSON and
   default-retention success runs. Use a root inspection helper where necessary
   to prove the mode-`0700` bind tree was traversed and the exported JSON was
   created mode `0600` by the deterministic non-default NZBDav UID/GID.
9. Run forced-failure passes for an invalid PostgreSQL credential, a controlled
   verification mismatch, and source database/blob mutation after export. With
   default retention, assert final snapshots, matching atomic temp files, both
   private configs, ownership-proven target blob stage/rollback entries, and
   every current-run helper container are absent or the prior blob tree has been
   restored as required; the redacted
   manifest reports the correct failure phase; the primary exit remains
   nonzero; a post-target-mutation failure reports `target_tainted=true` and
   blocks reuse until the disposable database/config reset is proven; and
   logs/manifest contain none of the database, account, config, or
   archive-password sentinels.
10. Deliver HUP, INT, and TERM while a named helper container is active. Assert
    the exact signal-derived status is preserved, the active container is
    force-removed, all sensitive artifacts are attempted, and unrelated
    lookalike and exact-name/wrong-label containers are untouched. Cover
    matching/missing/mismatched private cidfiles. Also test an explicit work path beneath a
    planted symlink and a directory replacement before bind; both must fail
    before Docker starts and must not chmod/chown the replacement target.
11. In a single trap, force-remove the PostgreSQL container and any test-owned
    helper containers, remove the Docker network, unset the connection string,
    restore test-tree ownership if needed, and recursively remove the private
    temp tree. Separately document/test stale-container and work-directory audit
    commands for uncatchable SIGKILL or host loss; do not claim a trap covers
    those cases.

End with `echo "sqlite-postgres container smoke: PASS"`. Do not enable shell tracing and do not print the connection string on failure.

- [ ] **Step 9: Correct the operator runbook**

Replace `docs/setup-guide.md:124-158` with a helper-first workflow that:

```bash
(
set +x
set -eu

MIGRATION_RUN_ID="$(python3 -c 'import secrets; print(secrets.token_hex(16))')"
MIGRATION_NETWORK="nzbdav-migration-$MIGRATION_RUN_ID"
: "${NZBDAV_MIGRATION_WORK_PARENT:?Set NZBDAV_MIGRATION_WORK_PARENT to a trusted existing mode-0700 directory}"
MIGRATION_WORK_DIR="${NZBDAV_MIGRATION_WORK_PARENT%/}/$MIGRATION_RUN_ID"
READY_FILE="$(mktemp "${TMPDIR:-/tmp}/nzbdav-migration-ready.XXXXXX")"
NETWORK_ID_FILE="$(mktemp "${TMPDIR:-/tmp}/nzbdav-migration-network.XXXXXX")"
chmod 600 "$READY_FILE" "$NETWORK_ID_FILE"
export NZBDAV_MIGRATION_READY_FILE="$READY_FILE"
helper_pid=""
helper_ready=0
signal_forwarded=0
forced_recovery=0
pending_signal=""
pending_status=""

forward_signal_once() {
  [ -n "$pending_signal" ] || return 0
  [ "$helper_ready" -eq 1 ] || return 0
  [ -n "$helper_pid" ] || return 0
  [ "$signal_forwarded" -eq 0 ] || return 0
  signal_forwarded=1
  kill -s "$pending_signal" "$helper_pid" 2>/dev/null || true
}
on_migration_signal() {
  [ -n "$pending_signal" ] || {
    pending_signal="$1"
    pending_status="$2"
  }
  forward_signal_once
}
abort_if_signalled() {
  [ -z "$pending_status" ] || exit "$pending_status"
}
remove_owned_helper_containers() {
  for cidfile in "$MIGRATION_WORK_DIR"/.container-*.cid; do
    [ -f "$cidfile" ] && [ ! -L "$cidfile" ] || continue
    IFS= read -r container_id < "$cidfile" || continue
    [ "${#container_id}" -eq 64 ] || continue
    case "$container_id" in *[!0-9a-f]*) continue ;; esac
    owner="$(docker inspect --format '{{index .Config.Labels "nzbdav.migration.run"}}' "$container_id" 2>/dev/null || true)"
    if [ "$owner" = "$MIGRATION_RUN_ID" ]; then
      docker rm -f "$container_id" >/dev/null 2>&1 || true
    fi
  done
}
stop_helper() {
  [ -n "$helper_pid" ] || return 0
  if [ -n "$pending_signal" ]; then
    forward_signal_once
  elif [ "$signal_forwarded" -eq 0 ]; then
    signal_forwarded=1
    kill -TERM "$helper_pid" 2>/dev/null || true
  fi
  i=0
  while kill -0 "$helper_pid" 2>/dev/null; do
    i=$((i + 1))
    if [ "$i" -ge 60 ]; then
      forced_recovery=1
      kill -KILL "$helper_pid" 2>/dev/null || true
      break
    fi
    sleep 1
  done
  wait "$helper_pid" 2>/dev/null || true
  helper_pid=""
}
cleanup_migration() {
  status=$?
  trap - EXIT
  trap '' HUP INT TERM
  stop_helper
  remove_owned_helper_containers
  unset POSTGRES_PASSWORD NZBDAV_DATABASE_CONNECTION_STRING NZBDAV_MIGRATION_READY_FILE
  network_id=""
  IFS= read -r network_id < "$NETWORK_ID_FILE" || true
  if [ "${#network_id}" -eq 64 ] && ! printf '%s' "$network_id" | grep -q '[^0-9a-f]'; then
    owner="$(docker network inspect --format '{{index .Labels "nzbdav.migration.run"}}' "$network_id" 2>/dev/null || true)"
    if [ "$owner" = "$MIGRATION_RUN_ID" ]; then
      docker network disconnect "$network_id" postgres >/dev/null 2>&1 || true
      docker network rm "$network_id" >/dev/null 2>&1 || true
    fi
  else
    owner="$(docker network inspect --format '{{index .Labels "nzbdav.migration.run"}}' "$MIGRATION_NETWORK" 2>/dev/null || true)"
    [ "$owner" != "$MIGRATION_RUN_ID" ] || forced_recovery=1
  fi
  rm -f "$READY_FILE" "$NETWORK_ID_FILE"
  if [ "$forced_recovery" -eq 1 ]; then
    printf 'RECOVERY REQUIRED: target is tainted; audit private work path %s and run nonce %s before retry.\n' \
      "$MIGRATION_WORK_DIR" "$MIGRATION_RUN_ID" >&2
  fi
  exit "$status"
}
trap cleanup_migration EXIT
trap 'on_migration_signal HUP 129' HUP
trap 'on_migration_signal INT 130' INT
trap 'on_migration_signal TERM 143' TERM

TAG="ghcr.io/binghzal/nzbdav:sha-$(git rev-parse HEAD)"
docker pull "$TAG"
abort_if_signalled
IMAGE="$(docker image inspect --format '{{index .RepoDigests 0}}' "$TAG")"
abort_if_signalled
case "$IMAGE" in *@sha256:*) ;; *) echo "Image digest resolution failed" >&2; exit 1 ;; esac
: "${NZBDAV_PUID:?Set NZBDAV_PUID to the nonzero target-runtime UID}"
: "${NZBDAV_PGID:?Set NZBDAV_PGID to the nonzero target-runtime GID}"
case "$NZBDAV_PUID" in
  0*|*[!0-9]*) echo "NZBDAV_PUID must be a nonzero integer without leading zeros" >&2; exit 1 ;;
esac
case "$NZBDAV_PGID" in
  0*|*[!0-9]*) echo "NZBDAV_PGID must be a nonzero integer without leading zeros" >&2; exit 1 ;;
esac

if ! docker inspect postgres >/dev/null 2>&1; then
  echo "The PostgreSQL container named postgres is not running" >&2
  exit 1
fi
abort_if_signalled
docker network create \
  --label "nzbdav.migration.run=$MIGRATION_RUN_ID" \
  "$MIGRATION_NETWORK" >"$NETWORK_ID_FILE"
abort_if_signalled
docker network connect --alias postgres "$MIGRATION_NETWORK" postgres
abort_if_signalled

set +e
read -r -s -p 'PostgreSQL password: ' POSTGRES_PASSWORD
read_status=$?
set -e
printf '\n'
abort_if_signalled
[ "$read_status" -eq 0 ] || exit "$read_status"
export NZBDAV_DATABASE_CONNECTION_STRING="Host=postgres;Port=5432;Database=nzbdav;Username=nzbdav;Password=$POSTGRES_PASSWORD"
abort_if_signalled
(
  trap - HUP INT TERM
  exec python3 scripts/nzbdav_migrate_sqlite_to_postgres.py \
    --sqlite-config /srv/nzbdav-sqlite/config \
    --postgres-config /srv/nzbdav-postgres/config \
    --docker-network "$MIGRATION_NETWORK" \
    --work-dir "$MIGRATION_WORK_DIR" \
    --run-nonce "$MIGRATION_RUN_ID" \
    --migration-uid "$NZBDAV_PUID" \
    --migration-gid "$NZBDAV_PGID" \
    --image "$IMAGE"
) &
helper_pid=$!
ready_attempt=0
while [ "$helper_ready" -eq 0 ]; do
  ready_value=""
  if IFS= read -r ready_value < "$READY_FILE" && [ "$ready_value" = ready ]; then
    helper_ready=1
    break
  fi
  kill -0 "$helper_pid" 2>/dev/null || break
  ready_attempt=$((ready_attempt + 1))
  if [ "$ready_attempt" -ge 300 ]; then
    forced_recovery=1
    signal_forwarded=1
    kill -TERM "$helper_pid" 2>/dev/null || true
    break
  fi
  sleep 0.1 || true
done
forward_signal_once
if [ "$forced_recovery" -eq 1 ]; then
  stop_helper
  helper_status=70
elif [ -n "$pending_signal" ]; then
  stop_helper
  helper_status=$pending_status
else
  while kill -0 "$helper_pid" 2>/dev/null; do
    [ -z "$pending_signal" ] || break
    sleep 0.1 || true
  done
  if [ -n "$pending_signal" ]; then
    stop_helper
    helper_status=$pending_status
  else
    set +e
    wait "$helper_pid"
    helper_status=$?
    set -e
    helper_pid=""
  fi
fi
trap '' HUP INT TERM
final_status=$helper_status
[ -z "$pending_status" ] || final_status=$pending_status
exit "$final_status"
)
```

State that the complete example disables inherited xtrace before any secret,
then runs in a terminating subshell so a pasted interactive invocation cannot
retain exported secrets after the helper returns. State that NZBDav must be
stopped; the helper byte-copies and re-fingerprints the stable source set without
opening it through SQLite, creates a canonical backup solely from that private
raw copy, and never mounts the original config into NZBDav; `postgres` is
attached to the named network when needed; the wrapper chooses a nonce leaf
below the required trusted `NZBDAV_MIGRATION_WORK_PARENT` outside the repo;
snapshots are deleted on normal, failure, HUP, INT, and TERM exits when helper
cleanup completes while the redacted `0600` manifest remains;
`--retain-snapshots` is only for encrypted/offline retention and requires prompt
deletion after validation; the staged SQLite database is removed on those
completed cleanup paths; `/config/blobs` is copied only after content
verification; `/config/cache` is never copied. Remove the unsafe `$PWD` transfer
examples and mutable `:latest` helper example. The cleanup trap must always
unset both secret variables. Use a
cryptographic per-run network name/label and capture Docker's returned network
ID in a private mode-`0600` file; cleanup must verify both ID and label before
disconnecting `postgres` or removing the network. An absent/partial ID enters
recovery instead of deleting by name. This makes cleanup derivable from Docker
state even if a signal arrives after create or connect succeeds but before the
shell advances. Add shell tests for normal,
nonzero, and signal exits—including signals at those two exact boundaries—
proving the subshell terminates, secrets never enter the parent shell, the
original status is preserved, owned resources are removed, and a same-name or
xtrace-enabled parent and assert the password/connection sentinel never appears.
lookalike network with the wrong label—and an exact-name/matching-label network
not represented by the captured ID—are untouched. Invoke the snippet from an
xtrace-enabled parent and assert the password/connection sentinel never appears.
xtrace-enabled parent and assert the password/connection sentinel never appears.
Signal the wrapper PID directly while its background helper is active; prove the
same HUP/INT/TERM reaches the helper, the wrapper waits for the helper's own
container/artifact rollback, no child remains, and the wrapper returns
129/130/143 respectively. Ignore additional HUP/INT/TERM only after cleanup has
started so a second signal cannot skip secret/network cleanup. If helper cleanup
misses the 60-second wrapper deadline, force-KILL only after preserving the
known run nonce/work path, remove only cidfile/label-proven Docker state, emit
`RECOVERY REQUIRED`, treat the target as tainted, and require the same manual
audit as SIGKILL/daemon/host failure; do not claim sensitive files were removed.
Test a signal before the Python ready handshake, during `wait`, during cleanup,
and immediately before final exit. Assert one delivery only, repeat signals do
not re-enter cleanup, the late boundary returns the signal-derived status, and a
readiness/cleanup timeout prints the exact recovery path without claiming file
deletion.

Require the target config to exist, be a non-symlink directory, and be owned and
writable by `NZBDAV_PUID:NZBDAV_PGID` before the example starts; the helper must
refuse instead of chowning it. Explain that it is only a blob destination and is
never a maintenance-container mount. The shown default is only for an empty,
offline, disposable PostgreSQL target. Document the exact reset procedure for
that target when a failure manifest says `target_tainted=true`: keep it offline,
drop/recreate the disposable database, reapply the approved provider schema,
restore/remove nonce blob staging/rollback entries as directed, and rerun from
the untouched SQLite source. Do not show `--replace` for a real database; the
separate provider-migration runbook owns that case. Add post-run commands that verify the
source database/sidecar and blob-tree fingerprints, target config ownership,
no containers with the exact `nzbdav-migration-<run-id>-` prefix, and no matching
atomic temp/private config entries in the printed private work path. Document a
separate recovery audit for SIGKILL, daemon/host failure, or power loss; cleanup
claims apply only when helper cleanup completes, and deadline escalation must
report recovery/taint instead.

- [ ] **Step 10: Run the complete scoped gates and cleanup audit**

Run:

```bash
/bin/sh tests/test_entrypoint_contract.sh
/bin/bash tests/test_nzbdav_migration_runbook.sh
python3 -m unittest tests.test_nzbdav_migrate_sqlite_to_postgres -v
python3.9 -m unittest tests.test_nzbdav_migrate_sqlite_to_postgres -v
dotnet test backend.Tests/backend.Tests.csproj --filter FullyQualifiedName~DatabaseTransferServiceTests
docker build -t nzbdav:migration-smoke .
NZBDAV_TEST_IMAGE=nzbdav:migration-smoke /bin/sh tests/test_entrypoint_container.sh
NZBDAV_TEST_IMAGE=nzbdav:migration-smoke /bin/sh tests/test_nzbdav_migration_container.sh
git diff --check
git diff --name-only -- .github
docker ps -a --filter name=nzbdav- --format '{{.Names}}'
docker network ls --filter name=nzbdav-migration --format '{{.Name}}'
```

Expected: all three shell PASS markers, the default and declared-minimum Python 3.9
runs report `OK`, `DatabaseTransferServiceTests` pass, `git diff --check` is
silent, `.github` diff is empty, and the final two Docker inventory commands
show no smoke resources created by these tests.

- [ ] **Step 11: Commit Task 2**

```bash
git add backend/Database/DatabaseTransferService.cs backend.Tests/Database/DatabaseTransferServiceTests.cs scripts/nzbdav_migrate_sqlite_to_postgres.py tests/test_nzbdav_migrate_sqlite_to_postgres.py tests/test_nzbdav_migration_container.sh tests/test_nzbdav_migration_runbook.sh docs/setup-guide.md
git commit -m "fix: secure sqlite to postgres container migration"
```
