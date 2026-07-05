# Jellyfin Unstable (FinTV-ready)

Custom Docker image extending official [`jellyfin/jellyfin:unstable`](https://hub.docker.com/r/jellyfin/jellyfin/tags) with:

- **Docker CLI 29.5.2** (static binary from download.docker.com, matches Docker Engine 29.x API 1.54)
- **yt-dlp** at `/usr/local/bin/yt-dlp` (CommercialBrainz YouTube commercial streaming)
- **fpcalc** at `/usr/bin/fpcalc` (`chromaprint-tools`, audio fingerprinting)
- **Automatic rebuilds** published to GHCR when upstream Jellyfin unstable changes

Weather channel capture uses a separate **`fintv-playwright-chromium`** container (Playwright's official image). This Jellyfin image does not bundle Chromium.

## Pull

```bash
docker pull ghcr.io/binarygeek119/jellyfin-unstable-fintv:unstable
```

Tags:

| Tag | Meaning |
|-----|---------|
| `unstable` | Latest rebuild from upstream `jellyfin/jellyfin:unstable` |
| `<12-char-digest>` | Pin to a specific upstream Jellyfin base digest |

## Run

See [docker-compose.example.yml](./docker-compose.example.yml). Required for FinTV Docker features:

1. Mount the host Docker socket: `/var/run/docker.sock:/var/run/docker.sock`
2. Add the socket group GID so the Jellyfin user can run `docker`:

```bash
stat -c '%g' /var/run/docker.sock
```

Use that value in `group_add` in compose.

3. Add `extra_hosts: ["host.docker.internal:host-gateway"]` when using local ws4kp/ws3kp URLs (bridge networking)

## FinTV behavior with this image

| Feature | How it works |
|---------|----------------|
| Weather channel capture | Starts `fintv-playwright-chromium` via Docker and connects over CDP (requires docker.sock) |
| CommercialBrainz YouTube ads | Uses `yt-dlp` from `/usr/local/bin/yt-dlp` in this image |
| Audio fingerprinting | Uses `fpcalc` from `/usr/bin/fpcalc` (`chromaprint-tools`) |
| Weather tab Docker buttons | Uses in-container `docker` CLI against the mounted host socket |
| FinTV plugin install | Install from the FinTV catalog as usual; not bundled in this image |

Stock Jellyfin images without this layer still need the bundled `scripts/install-docker-cli-jellyfin.sh` and the Playwright sidecar container.

## Build locally

From the repository root (build context must include `scripts/yt-dlp-version.txt`):

```bash
docker build -f docker/jellyfin-unstable/Dockerfile -t jellyfin-unstable-fintv:local .
```

## Updates

GitHub Actions (`.github/workflows/jellyfin-unstable-docker.yaml`) checks upstream `jellyfin/jellyfin:unstable` daily. When the base digest changes, it rebuilds and pushes to GHCR.

On your server, pull and restart:

```bash
docker compose pull jellyfin
docker compose up -d jellyfin
```

After updating Jellyfin, remove a stale sidecar if weather capture fails:

```bash
docker rm -f fintv-playwright-chromium
```

Or enable Watchtower in the example compose file for hands-off pulls.
