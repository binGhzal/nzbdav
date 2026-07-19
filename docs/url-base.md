# Hosting under a sub-path (`URL_BASE`)

Out of the box nzbdav serves its UI at the **root** of the configured port
(e.g. `http://nzbdav:3000/`). Public SAB-compatible and WebDAV clients enter
through the exact `protocol` namespace. If you want the UI to
share a domain with Sonarr/Radarr/Organizr/etc. — for example
`https://example.com/nzbdav/` — you'll want native sub-path support.

`URL_BASE` rewrites every emitted URL (HTML attributes, JS bundle imports,
Vite asset URLs, CSS `url()`, React Router routes, SAB API calls, the
WebSocket connection, and the auth-middleware redirect) so the app works
correctly under that prefix without any response rewriting in your reverse
proxy.

## Pick a layout

| Layout                  | URL example                          | `URL_BASE`    |
| ----------------------- | ------------------------------------ | ------------- |
| **Subfolder**           | `https://example.com/nzbdav/`        | `/nzbdav`     |
| **Subdomain** (default) | `https://nzbdav.example.com/`        | *(unset)*     |

The corresponding canonical client bases are
`https://nzbdav.example.com/protocol` and
`https://example.com/nzbdav/protocol`. A protocol base is an absolute HTTP(S)
URL ending in the exact, case-sensitive segment `protocol`, without a trailing
slash, query, fragment, credentials, dot segments, encoded separators, or
ambiguous double encoding. Every public API/WebDAV path is appended below that
base.

Subfolder is the natural fit if you already run an
Organizr/Heimdall/Homepage-style dashboard on one domain. Subdomain is
simpler at the proxy level but needs a DNS record per app.

Ready-to-copy nginx configs for both layouts live at
[`examples/nginx/subfolder.conf`](../examples/nginx/subfolder.conf) and
[`examples/nginx/subdomain.conf`](../examples/nginx/subdomain.conf).

## Configuring `URL_BASE`

`URL_BASE` must be set at **both** Docker build time and container runtime —
they configure two halves of the same setting:

| Stage     | What it controls                                                        |
| --------- | ----------------------------------------------------------------------- |
| Build     | React Router basename, Vite asset paths, the `__URL_BASE__` JS constant |
| Runtime   | Express middleware mount prefix, server-issued redirects                |

The two values **must match**. Mismatched values will silently break
navigation — the HTML and bundled JS will reference one prefix while the
Express server only answers requests at the other.

### Why is this a build arg, not just an env var?

React Router v7's `basename` and Vite's asset `base` must be known at build
time so they can be embedded into the emitted HTML and bundled JS. There's
no supported way to swap them at process start without rebuilding the
client bundle. The runtime env var on top is what tells the Express server
which prefix to mount middleware under so requests reach the right
handlers.

### Accepted values

| Input              | Normalized to | Meaning                       |
| ------------------ | ------------- | ----------------------------- |
| (unset) / `""`     | `""`          | App at root (default)         |
| `"/"`              | `""`          | App at root                   |
| `"/nzbdav"`        | `"/nzbdav"`   | App under `/nzbdav`           |
| `"/nzbdav/"`       | `"/nzbdav"`   | Trailing slash dropped        |
| `"nzbdav"`         | `"/nzbdav"`   | Leading slash added           |

Nested paths (e.g. `/apps/nzbdav`) are supported.

### Docker Compose example

An HTTPS reverse proxy still uses local application authentication. Generate a
persistent session key once, capture and validate it without printing it, store
it in your secret manager, and export the same value before every Compose
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
    volumes:
      - ./config:/config
```

### Plain Docker example

```sh
docker build --build-arg URL_BASE=/nzbdav -t nzbdav-local .
docker run --rm \
  -p 127.0.0.1:3000:3000 \
  -e URL_BASE=/nzbdav \
  -e AUTH_MODE=local \
  -e SESSION_KEY \
  -e SECURE_COOKIES=false \
  -e ALLOW_INSECURE_COOKIES=true \
  nzbdav-local
```

That loopback command is a direct-HTTP development example. The Compose
example above is the HTTPS reverse-proxy contract and deliberately keeps secure
cookies enabled without the insecure-cookie opt-in.

Skipping `--build-arg` while setting `-e URL_BASE` (or vice versa) is the
single most common misconfiguration. If pages 404 or assets won't load,
check that **both** halves were set to the **same** value.

## Reverse-proxy configuration

The full configs are under [`examples/nginx/`](../examples/nginx/). Below
is the minimum needed inside an existing server block, so you can paste it
into something you already maintain.

### Nginx — subfolder

```nginx
# Authenticated WebUI and WebSocket location.
location /nzbdav/ {
    proxy_pass http://127.0.0.1:3000;
    proxy_http_version 1.1;
    proxy_set_header Host              $host;
    proxy_set_header X-Real-IP         $remote_addr;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade           $http_upgrade;
    proxy_set_header Connection        $http_connection;
    proxy_buffering    off;
    proxy_read_timeout 1h;
}

# The only proxy-auth bypass: exact NzbDAV protocol segment boundary.
location ~ ^/nzbdav/protocol(?:/|$) {
    auth_basic   off;
    auth_request off;
    proxy_pass http://127.0.0.1:3000;
    proxy_http_version 1.1;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 6h;
    proxy_send_timeout 6h;
    client_max_body_size 0;
}
```

Use `location ~ ^/protocol(?:/|$)` for the subdomain layout. Keep
`proxy_pass` free of a URI suffix so Nginx preserves the complete request path.
Do not forward WebSocket `Upgrade`/`Connection` headers in the protocol block;
those belong only in the authenticated UI block. The `/view` route is a frontend-principal authenticated UI route and must not be added to the public protocol bypass.

### Nginx — subdomain

```nginx
server {
    server_name nzbdav.example.com;
    location / {
        proxy_pass http://127.0.0.1:3000;
        # …same proxy_set_header / Upgrade / Connection / buffering lines as above…
    }
}
```

### Caddy

```caddyfile
example.com {
    @protocol path /nzbdav/protocol /nzbdav/protocol/*
    handle @protocol {
        reverse_proxy 127.0.0.1:3000
    }
    handle /nzbdav/* {
        reverse_proxy 127.0.0.1:3000
    }
}
```

### Traefik (subfolder)

```yaml
labels:
  - "traefik.enable=true"
  - "traefik.http.routers.nzbdav.rule=Host(`example.com`) && PathPrefix(`/nzbdav`)"
  - "traefik.http.routers.nzbdav.entrypoints=websecure"
  - "traefik.http.routers.nzbdav.tls=true"
  - "traefik.http.services.nzbdav.loadbalancer.server.port=3000"

  # Attach no proxy-auth middleware only to this exact protocol boundary.
  - "traefik.http.routers.nzbdav-protocol.rule=Host(`example.com`) && (Path(`/nzbdav/protocol`) || PathPrefix(`/nzbdav/protocol/`))"
  - "traefik.http.routers.nzbdav-protocol.priority=20"
  - "traefik.http.routers.nzbdav-protocol.service=nzbdav"
```

Don't add a `StripPrefix` middleware — nzbdav needs to see the
`/nzbdav` prefix so its own URL_BASE wiring lines up. Just pass it
through.

## Configuring downstream clients

### Sonarr / Radarr (SABnzbd download client)

Settings → Download Clients → SABnzbd:

| Field        | Subfolder              | Subdomain              |
| ------------ | ---------------------- | ---------------------- |
| Host         | `example.com`          | `nzbdav.example.com`   |
| Port         | `443`                  | `443`                  |
| **URL Base** | `/nzbdav/protocol`     | `/protocol`            |
| Use SSL      | ✓                      | ✓                      |
| API Key      | nzbdav → Settings → SABnzbd | nzbdav → Settings → SABnzbd |

### rclone (WebDAV)

```ini
[nzbdav]
type = webdav
url = https://example.com/nzbdav/protocol    # subfolder
# url = https://nzbdav.example.com/protocol  # subdomain
vendor = other
user = …
pass = … (obscured)
```

The logical mount directories inherit the protocol base. Point rclone at the
full protocol root so one mount exposes `/nzbs`, `/content`,
`/completed-symlinks`, and `/.ids`; never append one of those child directories
to the configured remote URL.

## What `URL_BASE` actually rewrites

For reference / debugging, here's everything that picks up the prefix once
`URL_BASE` is set. If something in this list ends up at the wrong path,
that's a bug — please file it.

| Surface                                      | How it's prefixed                          |
| -------------------------------------------- | ------------------------------------------ |
| `<Link>` / `<NavLink>` / `<Form action>`     | React Router basename                      |
| `redirect()` in route loaders/actions        | React Router basename                      |
| `useNavigate()(path)`                        | React Router basename                      |
| `/__manifest` lazy route discovery + `p=`    | React Router basename                      |
| Express auth-middleware redirect to `/login` | `URL_BASE` prepended manually              |
| `fetch("/api/...")`, `fetch("/settings/...")` | `withUrlBase(...)` helper                  |
| `new WebSocket(...)`                         | `getWebsocketUrl()` helper                 |
| `<img src="/logo.svg">`, `<link rel="icon">` | `withUrlBase("/logo.svg")`                 |
| `/assets/*.js`, `/assets/*.css`, modulepreload | Vite `base` config                       |
| `url(/...)` in CSS                           | Vite `base` config                         |
| Protocol API/WebDAV suffixes                 | Appended below `URL_BASE/protocol`          |
| `window.__reactRouterContext.basename`       | React Router basename                      |

## Troubleshooting

**Bare host root returns 302 to `/nzbdav/`.** Working as intended — the
Express server redirects `/` to `URL_BASE/` so users typing the bare host
land at the app.

**Pages 404 or assets fail to load after enabling `URL_BASE`.** The most
common cause is mismatched build arg and env var. Rebuild with the same
value on both sides:

```sh
docker compose build --no-cache
docker compose up -d
```

**Login bounces back to `/login` (not `/nzbdav/login`).** The
auth-middleware redirect is missing the prefix — that means the runtime
`URL_BASE` env var isn't set. Confirm with `docker exec <container> env |
grep URL_BASE`. If it's empty or missing, set it in the compose file or
the `-e URL_BASE=...` flag.

**Login goes to `/nzbdav/login` but the page is unstyled or breaks.** The
build arg is missing or differs from the runtime env var. The HTML
references assets at one prefix while the server only serves them at the
other. Rebuild with the matching `--build-arg URL_BASE=…`.

**Sonarr/Radarr says "Unable to connect" to nzbdav.** Three things to
check:
1. **URL Base** in the SABnzbd download-client settings is the app
   `URL_BASE` plus `/protocol` (without a trailing slash).
2. Your reverse proxy isn't applying basic-auth or forward-auth to the
   exact `/nzbdav/protocol` boundary — the *arrs authenticate with the
   NzbDAV API key, not with your proxy's auth.
3. Set `PROTOCOL_BASE=https://example.com/nzbdav/protocol`, then run
   `curl -i -H 'X-Api-Key: …' "${PROTOCOL_BASE}/api?mode=version"`. It should
   return the version JSON. If you get a `401 WWW-Authenticate: Basic`, that's
   the proxy intercepting — use the exact protocol block shown above.

**WebSocket / live-update panels stay stuck on "connecting…"** The
`Upgrade` and `Connection` headers need to be forwarded on the web-UI
block. Both example configs in `examples/nginx/` include them; if you
trimmed them out, add them back.

**rclone WebDAV connection works for listing but seeks fail or stall.**
Set `proxy_buffering off` and a generous `proxy_read_timeout` (1h or more)
on the exact protocol location block. Range requests need to reach nzbdav with
their headers intact and the connection has to stay open long enough to
stream the response.

**I want to change `URL_BASE` after deploying.** You need to rebuild the
image — the basename and asset paths are baked into the client bundle at
build time. There's no in-place runtime swap.
