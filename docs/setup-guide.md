# Comprehensive NzbDav Setup Guide

This guide is an opinionated, step-by-step walkthrough to setting up NzbDav for maximum performance ("infinite library" style) with Radarr, Sonarr, Plex/Jellyfin and Stremio.

## How the "Infinite Library" Works
Before configuring, it helps to understand the flow:

### Path A: The Automation Flow (Radarr/Sonarr + Plex/Jellyfin)
1. **Radarr** sends an `.nzb` file to NzbDav (acting as a download client) to "download".
2. **NzbDav** mounts the nzb onto the webdav without actually downloading it.
3. **NzbDav** tells Radarr the "download" is finished and points to a folder of **Symlinks** at `/mnt/remote/nzbdav/completed-symlinks`.
    * The **Symlinks** always point to the `/mnt/remote/nzbdav/.ids` folder which contains the streamable content.
4. **Radarr** imports these Symlinks into your library. For eg: `/mnt/media/movies`.
5. **Plex** reads the Symlink -> Rclone Mount -> WebDAV Stream -> Usenet Provider.
    * **RClone** will make the nzb contents available to your filesystem by streaming, without using any storage space on your server.

### Path B: The On-Demand Flow (Stremio)
1. **Stremio (via AIOStreams)** searches your indexers using the `Newznab` addon and finds a release.
2. **AIOStreams** sends the `.nzb` to NzbDav's API to mount it.
3. **NzbDav** mounts the file instantly via WebDAV.
4. **AIOStreams** generates a streamable URL.
   * *Note: If using the recommended Proxy setup, this URL points to AIOStreams, which tunnels the traffic from NzbDav.*
5. **Stremio** plays the video from that URL (bypassing Rclone/Symlinks entirely).

## Phase 1: Prerequisites

### 1. Usenet Provider
You need an usenet provider to download content. Consult the [Usenet Providers Wiki](https://www.reddit.com/r/usenet/wiki/providerdeals/) for a full list.

### 2. Indexers
You need usenet indexers to find content. Consult the [Usenet Indexers Wiki](https://www.reddit.com/r/usenet/wiki/indexers/) for a full list.

Add these to Prowlarr and sync them to your Radarr/Sonarr instances.

---

## Phase 2: Initial Deployment

We start with a basic NzbDav container.

### 1. `docker-compose.yml` (Part 1)

Create the file structure like below:
```
your-root-docker-folder/
├── apps
│   ├── nzbdav
│   │   └── docker-compose.yml   👈 Create this file now
│   └── ...
```

Update `PUID`, `PGID`, `TZ`, and volume paths as needed.
You can get your PUID/PGID by running `id` in your terminal.

Local authentication requires one persistent 64-character lowercase
hexadecimal `SESSION_KEY`. Generate and validate it without printing it, store
it in your secret manager, and export that same value before every Compose
start so existing sessions survive restarts:

```bash
configure_nzbdav_session_key() {
  nzbdav_session_key_candidate=
  if ! nzbdav_session_key_candidate="$(hexdump -n 32 -ve '1/1 "%.2x"' /dev/urandom)"; then
    printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
    unset nzbdav_session_key_candidate
    return 1
  fi
  if [ "${#nzbdav_session_key_candidate}" -ne 64 ]; then
    printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
    unset nzbdav_session_key_candidate
    return 1
  fi
  case "$nzbdav_session_key_candidate" in
    *[!0-9a-f]*)
      printf '%s\n' 'Failed to generate SESSION_KEY.' >&2
      unset nzbdav_session_key_candidate
      return 1
      ;;
  esac
  export SESSION_KEY="$nzbdav_session_key_candidate"
  unset nzbdav_session_key_candidate
}
configure_nzbdav_session_key || exit 1
```

```yaml
services:
  nzbdav:
    image: ghcr.io/binghzal/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    healthcheck:
      test: curl -f http://localhost:3000/health || exit 1
      # Check every 1 minute
      interval: 1m
      # If it fails 3 times (3 minutes total), restart it
      retries: 3
      # Give it 5 seconds to boot up
      start_period: 5s
      # If it doesn't answer in 5 seconds, assume it's frozen
      timeout: 5s
    ports:
      - "3000:3000"
    environment:
      # Change these IDs to match your Docker user that you got from above
      - PUID=1000
      - PGID=1000
      - AUTH_MODE=local
      - SESSION_KEY=${SESSION_KEY:?generate and export SESSION_KEY before starting Compose}
      # This example is accessed directly over HTTP during first-run setup.
      - SECURE_COOKIES=false
      - ALLOW_INSECURE_COOKIES=true
      # SQLite is the default database provider and writes /config/db.sqlite.
      - NZBDAV_DATABASE_PROVIDER=sqlite
    volumes:
      - ./config:/config
      - /mnt:/mnt
```

Run the container
```bash
docker compose up -d
```

### 2. Core Configuration

Navigate to `http://your-server-ip:3000`.

**A. Create Admin Account**

Set your username and password.

**B. Usenet Settings (`Settings` > `Usenet`)**

* **Host:** `news.newshosting.com` (put your provider here).
* **Port:** `563`
* **Username / Password:** Your Usenet creds.
* **Max Connections:** `100` (Set to your provider's max allowed).
* **Type:** `Pool Connections`.
* **Use SSL:** Checked.

**C. WebDAV Settings (`Settings` > `WebDAV`)**

* **Set WebDAV Password:** Create a password (you will need this for Rclone).
* **Enforce Read-Only:** Uncheck it if you'd like to delete files from terminal. Otherwise, leave it checked.
* **Temporary Cache:** NZBDav now keeps a sparse segment cache under `/config/cache/segments` by default. This is temporary acceleration data, not permanent media storage. Keep `/config` on a disk with enough headroom for the default 64 GiB cache, or lower `cache.max-bytes` through the config API before heavy streaming.

**D. Database Settings**

SQLite is the only supported V1 runtime and stores its database at
`/config/db.sqlite`. NZBDav rejects `NZBDAV_DATABASE_PROVIDER=postgres` at
startup; PostgreSQL is not an operator-facing V1 option.

**E. ARR Operations**

Configure Radarr and Sonarr in `Settings` > `Radarr/Sonarr`. NZBDav keeps ARR as the source of truth: ARR still owns wanted state, quality decisions, collections, imports, naming, and library paths. NZBDav only uses ARR metadata to order already-requested downloads, report lifecycle state through SAB-compatible queue/history/status responses, and optionally nudge ARR to search selected missing media through ARR APIs.

Keep search nudging in `report` mode first. The Health page shows ARR validation, command history, correlation coverage, duplicate-loop diagnostics, and manual correlation repair. After report mode shows correct Sonarr/Radarr command plans and stable correlation coverage, switch search nudging to apply mode only if you want NZBDav to ask ARR to run bounded `EpisodeSearch` or `MoviesSearch` commands.

If ARR repeats the same download request, leave duplicate handling in diagnostic mode while validating. The SAB settings page also has an explicit `Reject duplicate add requests` option for operators who want hard duplicate suppression after confirming the diagnostics are accurate.

Before enabling ARR apply mode or hard duplicate rejection in production, run the read-only report-mode validation helper against the deployed instance:

```bash
NZBDAV_BASE_URL=https://nzbdav.example.com/protocol \
NZBDAV_API_KEY=replace-with-api-key \
python3 scripts/nzbdav_arr_report_validation.py
```

`NZBDAV_BASE_URL` is the canonical NzbDAV protocol base. It must be an
absolute HTTP(S) URL whose final path segment is exactly `protocol`, with no
query, fragment, credentials, dot segments, or ambiguous encoding. For an
in-network root deployment use `http://nzbdav:3000/protocol`; for
`URL_BASE=/nzbdav`, use the external form
`https://example.com/nzbdav/protocol`.

The helper writes a redacted JSON artifact under `artifacts/arr-validation/` and fails if Sonarr/Radarr are missing, search nudging is not in `report` mode, hard duplicate rejection is already enabled, validation errors exist, failed search nudges exist, or active queue correlation is below 90% without `--low-correlation-reason`.

ARR custom-script ingestion is optional, but it can improve lifecycle correlation when polling is delayed. Configure Sonarr, Radarr, or Lidarr custom scripts to POST form or JSON data to:

```bash
curl -fsS \
  -H "X-Api-Key: $NZBDAV_API_KEY" \
  -d "sonarr_eventtype=$sonarr_eventtype" \
  -d "sonarr_applicationurl=$sonarr_applicationurl" \
  -d "sonarr_download_id=$sonarr_download_id" \
  -d "sonarr_series_id=$sonarr_series_id" \
  -d "sonarr_episode_id=$sonarr_episode_id" \
  -d "sonarr_release_title=$sonarr_release_title" \
  "$NZBDAV_BASE_URL/api/arr/events/sonarr"
```

Use `/api/arr/events/radarr` and `/api/arr/events/lidarr` for Radarr and Lidarr variables. Include the official `<app>_applicationurl` variable so NZBDav can route completion refreshes back to the exact ARR instance instead of broadcasting. ARR `Test` events return success without creating correlation rows. Lidarr remains correlation/report-only; NZBDav does not execute Lidarr search commands in this pass.

### 3. Speed Tuning (Optional)

_Note: The default Max Download Connections setting of `15` works perfectly for most users (handling ~1Gbps). You only need to touch this if you are experiencing speed issues._

You can find the optimal **Max Download Connections** for your network (`Settings > WebDAV > Max Download Connections`) using the steps below:

1. **Baseline Test:** Run this on your server to check raw bandwidth.
   ```bash
   wget -O /dev/null https://ash-speed.hetzner.com/10GB.bin --report-speed=bits
   ```
2. **NzbDav Internal Test:**
   * In one Terminal window, run below command to monitor CPU usage:
     ```bash
     docker stats nzbdav
     ```
   * Download a movie `.nzb` via your indexer website and upload it to NzbDav.
   * In NzbDav UI: Go to `Dav Explore` > `Content` > `Movies` > Pick the movie you just downloaded > Right click the **video file** and click `Copy Link Address`. Now paste it in a text editor where you can see the whole thing.
   * Open the copied link through the authenticated WebUI session and observe
     throughput in the browser/player while watching `docker stats`. The
     frontend view route is principal-authenticated and is not a public
     protocol or download-key bypass.
3. **Adjust & Repeat:**
   * Set `Max Download Connections` to `10`. Test speed. (e.g., 500Mbps @ 70% CPU)
   * Set `Max Download Connections` to `15`. Test speed. (e.g., 1Gbps @ 85% CPU)
   * *Sweet Spot:* Stop when speed plateaus. For me, **15** (the default value) was the magic number.

### 4. Repair And Operations

Open `Health` in the WebUI for live repair, cache, provider, worker, mount, and rclone invalidation status. The page can start a repair verification run, cancel an active run, and clear broken-file repair records after review.

For an external rclone sidecar, NZBDav reports mount state
`external-unverified` and `ready=false`: process-local status cannot prove that
the host mount and link traversal work. Use the sidecar healthcheck below as the
readiness authority. The Health page separately shows pending versus **Due
Invalidations**, oldest pending age, whether RC and its host are configured,
and the last successful configured RC call. A new invalidation is not treated
as degraded immediately; an aged backlog, failed invalidation, failed RC call,
or required fence with RC disabled/missing a host is degraded.

`Mount:Type` changes update the running visibility-fence topology. The config
callback is serialized behind any in-flight visibility publication, so a
rclone-to-DFS (or DFS-to-rclone) change cannot complete until that publication's
database transaction commits. The fence generation, worker wakes, and active
state change together under the same topology gate. This is a database safety
boundary, not proof that an external sidecar or host mount is ready; coordinate
the actual mount transition and its healthcheck before changing the setting.

Repair checking uses a separate connection budget so continuous background verification does not steal active streaming slots. The defaults are conservative: `repair.connection-budget-percent=20` with at least one connection. Fresh post-download verify jobs are prioritized separately and can use the full automatic NNTP check budget, bounded by CPU/runtime pressure, so ARR-triggered downloads do not sit in slow verification behind the repair budget. Provider errors and unknown results are retried and reported as degraded state; NZBDav only queues repair for definitive missing-on-all-provider cases.

Download, verify, and repair work run as separate queue lanes. In `Settings` > `WebDAV` > `Advanced Throughput`, set `queue.max-concurrent-downloads`, `queue.max-concurrent-verify`, and `queue.max-concurrent-repair` independently. Use `0` for automatic sizing, or set a positive value as a hard per-lane cap. A saturated verify lane will not consume repair slots, and a saturated repair lane will not consume download slots.

The SAB-compatible `status` and `fullstatus` responses include additive `cache`, `mount`, `repair_runs`, `provider_diagnostics`, `worker_queues`, and `rclone_invalidations` fields for dashboards and ARR/Plex operational checks.

---

## Phase 3: The Full Stack (Rclone Sidecar)

Now we mount the NzbDav web dav to the host file system using a sidecar container.

### 1. Prepare Host Directory

```bash
sudo mkdir -p /mnt/remote/nzbdav # Create mount folder
sudo chown -R $(id -u):$(id -g) -R /mnt/remote/nzbdav # Give ownership of the folder to your user
```

### 2. Generate Rclone Config

```
your-root-docker-folder/
├── apps
│   ├── nzbdav
│   │   ├── docker-compose.yml
│   │   └── rclone.conf          👈 Create this empty file now
│   └── ...
```

*Generate obscured password:* `docker run --rm -it rclone/rclone obscure "<the-webdav-password-you-set-in-nzbdav-earlier>"`

Now populate `rclone.conf` with:
```ini
[nzbdav]
type = webdav
url = http://nzbdav:3000/protocol
vendor = other
user = admin
pass = <PASTE_OBSCURED_PASSWORD_HERE_WITHOUT_ANGLE_BRACKETS>
```

Keep the remote URL at the full protocol root. The mounted remote must expose
`.ids`, `completed-symlinks`, `content`, and `nzbs` together; do not append
`/content` or another child directory to the configured URL.

### 3. Update `docker-compose.yml`

Add the Rclone sidecar service to your existing `apps/nzbdav/docker-compose.yml`.

Update `PUID`, `PGID`, `TZ`, and volume paths as needed.
You can get your PUID/PGID by running `id` in your terminal.

```yaml
nzbdav_rclone:
  image: rclone/rclone:latest
  container_name: nzbdav_rclone
  restart: unless-stopped
  environment:
    # Change these IDs to match your Docker user that you got from above
    - PUID=1000
    - PGID=1000
    # Set the time zone to match your location
    - TZ=America/New_York
  volumes:
    # Host Path : Container Path : Propagation
    - /mnt:/mnt:rshared
    - ./rclone.conf:/config/rclone/rclone.conf
  cap_add:
    - SYS_ADMIN
  security_opt:
    - apparmor:unconfined
  devices:
    - /dev/fuse:/dev/fuse:rwm
  depends_on:
    nzbdav:
      condition: service_healthy
      restart: true
  # Optimized mounting flags for streaming.
  # RC is required so NZBDav can invalidate rclone VFS cache entries after
  # imports, deletes, repairs, and library changes.
  # 0M buffer size prevents large per-open-file RAM buffering
  # Parallel VFS chunk streams improve high-latency read throughput
  # 512M read-ahead is disk-backed when vfs-cache-mode=full
  command: >
    mount nzbdav: /mnt/remote/nzbdav
      --rc
      --rc-addr=:5572
      --rc-user=nzbdav
      --rc-pass=replace-with-long-random-password
      --uid=1000
      --gid=1000
      --allow-other
      --links
      --use-cookies
      --vfs-cache-mode=full
      --vfs-cache-max-size=20G
      --vfs-cache-max-age=6h
      --vfs-cache-poll-interval=1m
      --buffer-size=0M
      --vfs-read-ahead=512M
      --vfs-read-chunk-size=4M
      --vfs-read-chunk-streams=16
      --dir-cache-time=20s
```

After the sidecar starts, enable rclone remote control in NZBDav:

* **Settings** > **Rclone**
* **Remote Control:** enabled
* **Host:** `http://nzbdav_rclone:5572`
* **VFS Selector:** leave empty when this RC server owns only the NZBDav mount; otherwise set `nzbdav:` (or the exact name returned by `rclone rc vfs/list`)
* **Username:** `nzbdav`
* **Password:** the same value you used in `--rc-pass`

When RC is enabled, NZBDav keeps the invalidation fence pending while RC is
unreachable or the target VFS is ambiguous. That is intentional: ARR must not
import against a stale mount. When RC is disabled, NZBDav runs in compatibility
mode: ARR history and import dispatch are not held behind rclone invalidations.
Use compatibility mode only when mount visibility is refreshed externally,
because NZBDav cannot prove that rclone has observed a completed download.
The optional VFS selector is required when one RC server has multiple active
VFS instances; rclone rejects an unqualified `vfs/forget` request in that case.
Each configured RC request captures host, credentials, enabled state, VFS
selector, and configuration generation together. If those settings change
while a request is in flight, its result is treated as stale and the durable
invalidation is retried instead of deleted. Durable invalidation errors use
bounded categories such as `rclone_rc_http_500`, `rclone_rc_timeout`, or
`rclone_rc_malformed_response`; response bodies, exception messages, and
server-echoed credentials are never copied into the database or status API.

The invalidation absence check and `VisibleAt` publication are one transaction
on one fresh context. SQLite starts it with non-deferred `BEGIN IMMEDIATE` and
recreates the context, connection, and transaction for every BUSY/LOCKED retry;
it never publishes visibility from an incomplete attempt.

The visibility fence is staged even when no ARR instance is currently
configured. History remains hidden until required invalidations drain. After
visibility is safe, a missing ARR configuration is recorded as terminal
`NoRoute` rather than retried forever; it does not mean an NZBDav import receipt
was marked `Imported`. If ARR configuration later transitions from unavailable
to available, the worker requeues those `NoRoute` commands once for normal
routing instead of polling them continuously. Each worker pass captures one
immutable ARR-client snapshot and uses it for both transition reconciliation
and target selection, so a clients-empty-clients flap cannot strand a command
between two different configuration observations.

For an exact persisted Sonarr or Radarr correlation, NZBDav reauthorizes the
current outbox lease, reads that same instance's queue, and may submit a guarded
`DownloadedEpisodesScan` or `DownloadedMoviesScan` using ARR's unchanged queue
output path and download ID. Any missing, ambiguous, unsupported, malformed, or
unhealthy case uses `RefreshMonitoredDownloads`; a fast targeted-scan failure
also uses that refresh fallback when its bounded deadline still has time. This
is a wake-up optimization only. An accepted ARR command ID does not prove that
ARR imported or renamed the item, obtained metadata, notified Plex, or made the
item visible. Use ARR's `downloadFolderImported` history plus exact Plex path and
metadata observations for end-to-end evidence.

Start and update `nzbdav_rclone` with the safe updater from this repository.
Use this instead of raw `docker compose up -d nzbdav_rclone` for routine
deploys. The helper fingerprints the rendered compose service plus
`rclone.conf`, records only the SHA-256 fingerprint and update time in
`.nzbdav-rclone-state.json`, and skips `docker compose up` entirely when
nothing effective changed. The state is atomically replaced as a mode-`0600`
regular file; rendered Compose content and watched-file paths or values are
never persisted. An unchanged legacy state containing the old `payload` object
is rewritten privately without restarting rclone:
```bash
$ python3 scripts/nzbdav_safe_rclone_up.py \
    --project-dir /path/to/apps/nzbdav \
    --compose-file docker-compose.yml \
    --watch-file rclone.conf
```

Run the same command after editing `rclone.conf` or the compose sidecar
definition. If the rendered service and watched config content are unchanged,
the helper exits without touching the live rclone container or mount:
```bash
$ python3 scripts/nzbdav_safe_rclone_up.py \
    --project-dir /path/to/apps/nzbdav \
    --compose-file docker-compose.yml \
    --watch-file rclone.conf
```

If `.nzbdav-rclone-state.json` is missing but an `nzbdav_rclone` container is
already running, the helper checks Docker Compose's live service hash and the
watched config file timestamps. When the running container matches the rendered
service and `rclone.conf` has not changed since the container started, it records
the current fingerprint and still skips `docker compose up`. If `rclone.conf` is
newer than the running container, it applies the update so rclone can reload the
changed config.

Do not use `--force-recreate` for routine restarts or unchanged settings. A
forced recreate tears down the mount even when nothing changed, which can make
Plex/Radarr/Sonarr briefly see an empty or stale library. Only force recreate
the rclone sidecar when you intentionally need a clean remount and the media
apps are stopped or gated behind the mount healthcheck below.

Check out the mount is working
```bash
ls -la /mnt/remote/nzbdav
# Should show: .ids, completed-symlinks, content, nzbs
```

#### Mount Health And Fail-Closed Behavior

Media apps should depend on the rclone mount being healthy, not just the
NZBDav container being started. Otherwise Plex/Radarr/Sonarr can scan an
empty host directory while rclone is restarting and treat the whole library
as deleted.

Recommended guardrails:

* Add a healthcheck to the rclone sidecar that verifies both RC and the
  mounted NZBDav root:

```yaml
healthcheck:
  test:
    - CMD-SHELL
    - >
      rclone rc --rc-addr=http://127.0.0.1:5572 --rc-user=nzbdav --rc-pass=replace-with-long-random-password core/version >/dev/null
      && test -d /mnt/remote/nzbdav/.ids
      && test -d /mnt/remote/nzbdav/content
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 30s
```

* Make Plex/Radarr/Sonarr/Lidarr depend on that healthcheck before scanning
  the mounted library:

```yaml
depends_on:
  nzbdav_rclone:
    condition: service_healthy
    restart: true
```

* Mount media apps to the rclone mount path itself, not to an always-present
  empty fallback directory. If the mount is unhealthy, the app should fail
  closed instead of scanning stale or empty content.

#### Understanding the Flags
* **`--links`**: **Crucial**. This allows `*.rclonelink` files within the webdav to be translated to symlinks when mounted onto your filesystem.
  > *Note: Requires Rclone v1.70.3+.*
* **`--use-cookies`**: **Performance**. Without this, Rclone re-authenticates on every single request, causing massive slowdowns.
* **`--allow-other`**: **Permissions**. Ensures other containers (like Radarr/Plex) can see the mounted files.
* **`--vfs-cache-mode=full`**: **Performance**. Enables the full VFS cache, which is required for seeking and proper file handling.
* **`--buffer-size=0M`**: **Memory Management**. Prevents large per-open-file RAM buffering. With full VFS cache, use disk-backed read-ahead for smoothing instead.
* **`--vfs-read-ahead=512M`**: **Smooth Playback**. Buffers 512MB into VFS disk cache ahead of the current position to handle high-bitrate spikes without stuttering.
* **`--vfs-read-chunk-size=4M`**: **Chunk Granularity**. Uses smaller source reads so parallel chunk streams can fill the VFS cache quickly instead of waiting on one large serial read.
* **`--vfs-read-chunk-streams=16`**: **Parallel Chunking**. Reads multiple VFS chunks concurrently, which is useful on high-latency links. Start here, then adjust based on provider/server limits.
* **`--vfs-cache-max-size=20G`**: **Disk Management**. Limits the local disk space used by the cache. Adjust based on your available storage.
* **`--vfs-cache-max-age=6h`**: **Temporary Storage**. Expires idle cache entries so storage usage stays temporary.
* **`--dir-cache-time=20s`**: **Responsiveness**. Keeps the directory cache short so new downloads/links appear quickly in the mount.

These flags are optimized for streaming. 

Remember: `unnecessary flags = potential pitfalls`.

#### Rclone flags reference
* [Rclone mount VFS documentation](https://rclone.org/commands/rclone_mount/)
* [Rclone Forum Discussion on Buffer Size](https://forum.rclone.org/t/whats-the-suitable-value-to-set-for-buffer-size-with-vfs-read-ahead/39971/4)

## Native DFS Prototype

`Mount:Type=dfs` is a Linux x64 prototype and remains behind the benchmark gate. Keep `MOUNT_TYPE=rclone` for production unless manual artifacts in `artifacts/benchmarks/` show DFS beats tuned rclone by the documented seek-latency, CPU/RSS, restart, invalidation, and ARR import/delete criteria. The current `Mono.Fuse.NETStandard` package only ships a Linux x64 native helper, so arm64 containers report DFS as unavailable and fail closed.

To test DFS in a container, the NZBDav container must have access to FUSE:

```yaml
services:
  nzbdav:
    environment:
      - MOUNT_TYPE=dfs
      - MOUNT_DIR=/mnt/remote/nzbdav
    devices:
      - /dev/fuse:/dev/fuse:rwm
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
```

The mount fails closed: if the host is not Linux x64, `/dev/fuse` is unavailable, or FUSE cannot mount the target directory, `/api/mount/status` and SAB `fullstatus.mount` report `ready=false` instead of exposing an empty library. Media apps should not scan the mount until `ready=true`.

DFS exposes the same content and `.ids` logical items as WebDAV, and exposes completed imports as real symlinks so ARR copy-delete imports can unlink the virtual symlink without deleting underlying content. Rclone remains the default because it is battle-tested; DFS should replace it only after benchmark evidence wins on the same host and media workload.

---

## Phase 4: Integrations

### 1. Add NzbDav Download Client to Radarr/Sonarr/Lidarr

In Sonarr, Radarr, and Lidarr, go to `Settings` > `Download Clients` >
`Add Download Client`.

* Client: **SABnzbd**
* Name: `NzbDav`
* Host: `nzbdav` 
* Port: `3000`
* URL Base: `/protocol`
* Nested URL Base: `/nzbdav/protocol` when the application is using the
  externally proxied `URL_BASE=/nzbdav` deployment instead of the internal
  root host above.
* API Key: Found in NzbDav `Settings` > `SABnzbd`.

### 2. Configure NzbDav for Radarr/Sonarr/Lidarr

Go to NzbDav `Settings` > `Radarr/Sonarr`.

1. **Radarr Instances > Add**
   * **Host:** `http://radarr:7878`
   * **API Key:** (Radarr > Settings > General > Security > API Key)
2. **Sonarr Instances > Add**
   * **Host:** `http://sonarr:8989`
   * **API Key:** (Sonarr > Settings > General > Security > API Key)
3. **Lidarr Instances > Add**
   * **Host:** `http://lidarr:8686`
   * **API Key:** (Lidarr > Settings > General > Security > API Key)
   * Lidarr remains correlation/report-only; NZBDav does not execute Lidarr
     search commands in V1.
4. **Automatic Queue Management:**

   Configure these rules to handle failed or bad releases, keeping your queue clean with as little manual intervention as possible. 
   Feel free to experiment and adjust these rules to your liking.

   * **Do Nothing:**
       * Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible.
       * Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.
       * Episode was not found in the grabbed release.
       * Episode was unexpected considering the folder name.
       * Invalid season or episode.
       * Unable to determine if file is a sample.
   * **Remove, Blocklist, and Search:**
       * No files found are eligible for import.
       * No audio tracks detected.
       * Sample.
   * **Remove and Blocklist:**
       * Not an upgrade for existing episode file(s).
       * Not an upgrade for existing movie file.
       * Not a Custom Format upgrade.
   * **Remove:**
       * Episode file already imported.

### 3. Configure Mount & Repairs

1. **Mount Directory (`Settings` > `SABnzbd`):**
   * **Rclone Mount Directory:** `/mnt/remote/nzbdav`
   * *Note: This tells NzbDav where the files physically exist on your host system so it can pass the correct path to Radarr/Sonarr.*
2. **Repairs (`Settings` > `Repairs`):**
   * **Library Directory:** `/mnt/media`
     *(Point this to the root folder where your actual Movie/TV libraries live on the host)*.
   * **Enable Background Repairs:** Checked.
     *(This allows NzbDav to monitor for dead links in your library and trigger redownloads automatically).*

### 4. Configure the production Plex scan trigger

NZBDav finishes the virtual download and wakes ARR, but ARR owns the final
renamed library path. Configure each Sonarr/Radarr instance under
`Settings` > `Connect` with its Plex Media Server connection (or configure an
import-complete AutoPulse equivalent) and test it from ARR. Do not depend on
Plex filesystem change detection for the rclone/FUSE mount: Plex documents that
automatic updates generally do not work for network-style shares.

Keep normal library scans targeted and disable broad periodic scans while the
NZBDav/rclone health gate is degraded. Validate the real notification path with
the default observe-only command in `docs/grab-to-plex-benchmark.md`. Use its
`--force-plex-scan` option only to diagnose whether delay is in the production
notification hook or inside Plex scanning/metadata; forced runs are excluded
from production percentiles.

References:

* [Plex scanning versus refreshing](https://support.plex.tv/articles/200289306-scanning-vs-refreshing-a-library/)
* [Sonarr settings and connections](https://wiki.servarr.com/en/sonarr/settings)
* [Radarr settings and connections](https://wiki.servarr.com/radarr/settings)

---

## Phase 5: Usenet Streaming in Stremio (via AIOStreams)

You can stream your Usenet content directly in Stremio using [AIOStreams](https://github.com/Viren070/AIOStreams).

For more info, check out their [Usenet Wiki](https://github.com/Viren070/AIOStreams/wiki/Usenet).

### 1. Configure NzbDav Service

In the AIOStreams UI:

1. Go to the **Services** menu and select **NzbDav**.
2. Enter the details:
   * **NzbDAV URL:** `http://nzbdav:3000/protocol` (For a nested public deployment, use `https://example.com/nzbdav/protocol`).
   * **NzbDAV API Key:** (From NzbDav `Settings` > `SABnzbd`).
   * **NzbDAV WebDAV Username:** (From NzbDav `Settings` > `WebDAV`).
   * **NzbDAV WebDAV Password:** (From NzbDav `Settings` > `WebDAV`).
   * **AIOStreams Auth Token (Recommended):** Get it from your self-hosted AIOStreams' `.env` file's `AIOSTREAMS_AUTH` environment variable. (e.g., `user:pass`).

### 2. Configure Newznab Addon

In the AIOStreams UI:

1. Go to **Addons** > **Marketplace** > From the Types dropdown, select **Usenet**.
2. Find the **Newznab** addon and click **Configure**.
3. Add your indexers (repeat for each one):
   * **Name:** `NZBGeek` (or similar).
   * **Newznab URL:** Select `NZBgeek` from dropdown.
   * **API Key:** Your indexer's API key.
   * **AIOStreams Proxy Auth (Recommended):** Get it from your self-hosted AIOStreams' `.env` file's `AIOSTREAMS_AUTH` environment variable. (e.g., `user:pass`).
   * **Search Mode:** **Forced Query** (was `Auto` by default)
   * **Timeout:** `5000` ms (was `7000` by default)
4. Leave everything else as default and click **Install**

### 3. Install to Stremio

Go to the **Save & Install** tab, click **Save**, and then install the addon to Stremio.
