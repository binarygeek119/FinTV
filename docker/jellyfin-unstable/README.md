# Jellyfin Unstable (FinTV-ready)

Custom Docker image extending official [`jellyfin/jellyfin:unstable`](https://hub.docker.com/r/jellyfin/jellyfin/tags) with:

- **Docker CLI 29.5.2** (static binary from download.docker.com, matches Docker Engine 29.x API 1.54)
- **Playwright Chromium** at `/ms-playwright` (matches FinTV `Microsoft.Playwright` 1.49.0)
- **Automatic rebuilds** published to GHCR when upstream Jellyfin unstable changes

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

## FinTV behavior with this image

| Feature | How it works |
|---------|----------------|
| Weather channel capture | Uses baked-in Chromium via `PLAYWRIGHT_BROWSERS_PATH=/ms-playwright` (no `fintv-playwright-chromium` sidecar) |
| Weather tab Docker buttons | Uses in-container `docker` CLI against the mounted host socket |
| FinTV plugin install | Install from the FinTV catalog as usual; not bundled in this image |

Stock Jellyfin images without this layer still need the bundled `scripts/install-docker-cli-jellyfin.sh` and the Playwright sidecar container.

## Build locally

```bash
docker build -t jellyfin-unstable-fintv:local docker/jellyfin-unstable
```

## Updates

GitHub Actions (`.github/workflows/jellyfin-unstable-docker.yaml`) checks upstream `jellyfin/jellyfin:unstable` daily. When the base digest changes, it rebuilds and pushes to GHCR.

On your server, pull and restart:

```bash
docker compose pull jellyfin
docker compose up -d jellyfin
```

Or enable Watchtower in the example compose file for hands-off pulls.
