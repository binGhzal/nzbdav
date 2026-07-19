# Nginx reverse proxy examples

Ready-to-copy Nginx configuration files for running nzbdav behind a reverse
proxy. Pick whichever layout matches your setup.

## Files

- [`subfolder.conf`](./subfolder.conf) — run nzbdav at a subfolder
  (e.g. `https://example.com/nzbdav/`). Requires the `URL_BASE` setting (see
  below).
- [`subdomain.conf`](./subdomain.conf) — run nzbdav on its own subdomain
  (e.g. `https://nzbdav.example.com/`). No special build args required.

## Quick start

1. Choose the config file that matches your layout.
2. Copy it to `/etc/nginx/sites-available/nzbdav.conf`.
3. Update `server_name`, the SSL certificate paths, and the upstream address
   to match your deployment.
4. Enable it:
   ```sh
   sudo ln -s /etc/nginx/sites-available/nzbdav.conf /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl reload nginx
   ```

## nzbdav configuration

### Subfolder setup

Both halves of `URL_BASE` must be set — the Docker **build arg** (so React
Router, Vite, and the client bundle know the prefix) and the **runtime env
var** (so the Express server mounts middleware at the right place). See
[`docs/url-base.md`](../../docs/url-base.md) for the full explanation.

The HTTPS proxy keeps secure cookies enabled and requires one persistent local
session key. Generate it once, capture and validate it without printing it,
store it in your secret manager, and export the same value before every Compose
start:

```sh
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
# docker-compose.yml
services:
  nzbdav:
    build:
      context: .
      args:
        URL_BASE: /nzbdav
    environment:
      URL_BASE: /nzbdav
      AUTH_MODE: local
      SESSION_KEY: ${SESSION_KEY:?generate and export a persistent SESSION_KEY before starting Compose}
      SECURE_COOKIES: "true"
    ports:
      - "127.0.0.1:3000:3000"
```

### Subdomain setup

No `URL_BASE` build arg or runtime variable is needed for the default root
layout. Keep the same required persistent `SESSION_KEY` and secure-cookie
settings, then point nginx at the loopback-bound port `3000`.

## Public protocol base

External SAB/WebDAV clients use one canonical base ending in the exact
`protocol` segment:

- subdomain: `https://nzbdav.example.com/protocol`
- subfolder: `https://example.com/nzbdav/protocol`

Every client API or WebDAV path stays below that base. The example configs
disable proxy authentication only on that exact segment boundary; NzbDAV's
own API-key/WebDAV authentication still applies.

## Sonarr / Radarr settings

In Sonarr/Radarr **Settings → Download Clients → SABnzbd**:

| Field        | Subfolder                          | Subdomain                       |
| ------------ | ---------------------------------- | ------------------------------- |
| Host         | `example.com`                      | `nzbdav.example.com`            |
| Port         | `443`                              | `443`                           |
| URL Base     | `/nzbdav/protocol`                 | `/protocol`                     |
| Use SSL      | ✓                                  | ✓                               |
| API Key      | from nzbdav Settings → SABnzbd     | from nzbdav Settings → SABnzbd  |

## rclone (WebDAV) settings

In your `rclone.conf` remote:

| Field | Subfolder                                | Subdomain                              |
| ----- | ---------------------------------------- | -------------------------------------- |
| url   | `https://example.com/nzbdav/protocol` | `https://nzbdav.example.com/protocol` |
| user  | from nzbdav Settings → WebDAV            | same                                   |
| pass  | from nzbdav Settings → WebDAV (obscured) | same                                   |

Keep the remote at that full protocol root so one mount exposes `.ids`,
`completed-symlinks`, `content`, and `nzbs` together.

## Why these configs look the way they do

A few decisions worth calling out:

- **One regex with an exact `protocol` segment boundary** covers the public
  SAB/WebDAV namespace without exposing similarly prefixed or legacy paths.
- **`auth_basic off` + `auth_request off`** appears only on that protocol
  block. Sonarr/Radarr/rclone then authenticate to NzbDAV directly with the
  API key or WebDAV credentials. The WebUI, including the principal-only view
  route, stays in the authenticated UI block.
- **`proxy_buffering off`** plus the longer `proxy_read_timeout` /
  `proxy_send_timeout` on the protocol block keep range requests (`Range:
  bytes=…`) flowing intact, which is what makes seek work on a streamed
  RAR-archived video.
- **No `sub_filter`, no `proxy_redirect`, no `njs` manifest rewriting** —
  the historical workarounds for nzbdav's lack of native sub-path support
  are gone. nzbdav now emits correctly-prefixed URLs at the source.

## Troubleshooting

See the **Troubleshooting** section of
[`docs/url-base.md`](../../docs/url-base.md#troubleshooting).
