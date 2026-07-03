<p align="center">
  <img src="logo.png" alt="FinTV" width="320" />
</p>

# FinTV

Simulated live TV for [Jellyfin](https://jellyfin.org). FinTV turns your media library into always-on broadcast channels with a full electronic program guide, commercial support, retro 4:3 presentation options, and IPTV integration.

Inspired by [ErsatzTV/legacy](https://github.com/ErsatzTV/legacy) scheduling concepts, built as a native Jellyfin plugin.

## Features

- **Virtual channels** for TV shows, movies, music videos, music (audio + album art), and a dedicated WeatherStar 4000 weather channel
- **48-slot daily lineups** (30-minute blocks) with multiple candidates per slot and smart selection
- **Override lineups** for special days (Friday movie night, holidays, etc.)
- **Commercial library** with chapter-marker breaks first, timer fallback second
- **Blackframe chapter detection** scheduled task for commercial files
- **Channel branding** using the [Binarygeek119 Set](https://github.com/binarygeek119/open-channel-logos/tree/master/Binarygeek119%20Set) from [open-channel-logos](https://github.com/binarygeek119/open-channel-logos)
- **4:3 / 16:9** aspect ratios, optional CRT scanlines, auto-positioned channel bug
- **M3U + XMLTV** endpoints for Jellyfin Live TV
- **Admin UI** in Dashboard → Plugins → FinTV

## Requirements

- Jellyfin **12.0+** (including 12.0 RC builds)
- FFmpeg (bundled with Jellyfin)
- For WeatherStar channel: **Windows** uses bundled Playwright Chromium; **Linux Docker** can use the [FinTV-ready Jellyfin unstable image](docker/jellyfin-unstable/README.md) or Playwright's sidecar container (Docker required)

> **Jellyfin 10.11 users:** use [FinTV v0.0.1.3](https://github.com/binarygeek119/FinTV/releases/tag/v0.0.1.3) instead. v0.0.2.0+ targets Jellyfin 12 on .NET 10.

## Install from GitHub

FinTV is **not** in Jellyfin’s official plugin repository. You must add this repo manually, then browse **Available** plugins (not Installed).

### 1. Add the plugin repository

1. Open **Dashboard → Plugins → Repositories**
2. Click **+** (New Repository)
3. Set:
   - **Name:** `FinTV` (any name is fine)
   - **URL:** `https://raw.githubusercontent.com/binarygeek119/FinTV/master/manifest.json`
4. Save. Ensure the new repository is **enabled**.

### 2. Install from the catalog

1. Open **Dashboard → Plugins**
2. Click **Available** (Jellyfin 10.11 defaults to **Installed**, which hides catalog plugins)
3. Optional: filter category **Live TV**, or search **FinTV**
4. Install **FinTV 0.0.1.0**, then restart Jellyfin when prompted

**Requirements:** Jellyfin **12.0.0** or newer (including RC builds). For Jellyfin 10.11, install [v0.0.1.3](https://github.com/binarygeek119/FinTV/releases/tag/v0.0.1.3).

### Troubleshooting

| Symptom | Fix |
|--------|-----|
| Plugin not listed at all | Confirm Jellyfin is **10.11+** and the custom repository URL is exact (must end in `/manifest.json`) |
| Only see installed plugins | Switch the filter from **Installed** to **Available** |
| Repository added but still empty | Check **Dashboard → Plugins → Repositories** — repo must be enabled. Restart Jellyfin after adding it |
| Install fails checksum error | Re-run the [Publish Plugin](https://github.com/binarygeek119/FinTV/actions/workflows/publish.yaml) workflow for your release tag |
| Server cannot reach GitHub | Jellyfin must download the manifest and zip from the internet; check server logs for `An error occurred while accessing the plugin manifest` |


### Manual install

1. Download `fintv_<version>.zip` from [Releases](https://github.com/binarygeek119/FinTV/releases) (for example `fintv_0.0.1.0.zip`)
2. Extract to `{JellyfinData}/plugins/FinTV/`
3. Restart Jellyfin

## Releasing

1. Bump `version` and `changelog` in [`build.yaml`](build.yaml)
2. Commit and push to `master`
3. Create a GitHub Release from a tag like `v0.0.1.0`
4. The **Publish Plugin** workflow will:
   - Build the plugin zip with JPRM
   - Attach `fintv_<version>.zip` to the release
   - Update [`manifest.json`](manifest.json) with the MD5 checksum and download URL

The **Build Plugin** workflow on push only validates the build and stores a temporary Actions artifact. Release zips come from the publish workflow.

## Live TV setup

1. Open **Dashboard → Plugins → FinTV** and note the URLs on the **Live TV Setup** tab
2. Set **Public Base URL** to the address Jellyfin/clients use (e.g. `http://192.168.1.10:8096`)
3. **Dashboard → Live TV → Add Tuner**
   - Type: **M3U Tuner**
   - URL: `http://YOUR-SERVER:8096/FinTV/iptv/channels.m3u`
4. **Dashboard → Live TV → Add Guide Provider**
   - Type: **XMLTV**
   - URL: `http://YOUR-SERVER:8096/FinTV/iptv/epg.xml`
5. Run scheduled tasks in order:
   - **Refresh Channels**
   - **Refresh Guide**

## Quick start

1. Create a channel in the FinTV settings tab
2. Edit the **Lineups** grid — click a 30-minute slot and add Jellyfin item IDs as candidates
3. Click **Rebuild Playout**
4. Refresh Jellyfin Live TV guide

### Commercials

Tag items with `fintv-commercial` in Jellyfin, or point the plugin at a dedicated commercials library. Run **Sync Commercial Library**, then optionally **Run Blackframe Scan** to detect ad segments.

Compatible with community packs such as [Open-Commercial-Pack](https://github.com/binarygeek119/Open-Commercial-Pack).

### Weather channel

Create a channel with content type **Weather**, set latitude/longitude, enable it, and rebuild playout. FinTV captures a headless WeatherStar page with Playwright and streams it as MPEG-TS.

**Self-hosted WeatherStar (Docker):** On the FinTV **Weather** tab you can start official containers:

| Container | Image | Default port |
|-----------|-------|--------------|
| ws4kp (WeatherStar 4000+) | `ghcr.io/netbymatt/ws4kp` | 8080 |
| ws3kp (WeatherStar 3000+) | `ghcr.io/netbymatt/ws3kp` | 8083 |

Click **Start** then **Use URL** to set the base URL to `http://127.0.0.1:8080` or `:8083`. FinTV auto-starts the matching container when you tune a weather channel with that local URL.

**Windows:** FinTV downloads Chromium automatically into `{JellyfinData}/plugins/configurations/FinTV/playwright-browsers` on the first weather tune.

**Linux:** FinTV starts Chromium from Playwright's official Docker image (`mcr.microsoft.com/playwright:v1.49.0-jammy`) and connects over CDP. Requirements:

- Docker installed and running
- The Jellyfin service user can run `docker` (for example, add the user to the `docker` group)
- Port **9222** available on localhost for the FinTV browser container

On first weather tune, FinTV creates a container named `fintv-playwright-chromium`. If your WeatherStar URL uses `localhost` or `127.0.0.1`, FinTV rewrites it to `host.docker.internal` so the container can reach WeatherStar on the Jellyfin host.

If Jellyfin itself runs in Docker, mount the Docker socket into the Jellyfin container (for example `-v /var/run/docker.sock:/var/run/docker.sock`) so FinTV can start the Playwright browser and WeatherStar containers.

**FinTV-ready Jellyfin Docker image (recommended for Linux):** Use [`ghcr.io/binarygeek119/jellyfin-unstable-fintv:unstable`](docker/jellyfin-unstable/README.md) — includes Docker CLI **29.5.2**, Playwright Chromium, and auto-rebuilds when Jellyfin unstable updates.

**Stock Jellyfin images:** mount the Docker socket and run the bundled install script from the **host**:

```bash
# volumes: - /var/run/docker.sock:/var/run/docker.sock
bash /config/plugins/FinTV_<version>/scripts/install-docker-cli-jellyfin.sh jellyfin
```

The script installs Docker CLI **29.5.2** (static binary). If that fails, it falls back to apt `docker.io`.

If `docker version` fails with permission denied, add the host Docker socket group to Jellyfin and restart:

```yaml
group_add:
  - "999"   # use: stat -c '%g' /var/run/docker.sock on the host
```

**Install FinTV, restart Jellyfin, then tune a weather channel once** to initialize the browser runtime on either platform.

### AI lineup generation

The **AI** tab can auto-build 48-slot daily lineups from your tagged Jellyfin library (OpenAI or Venice). Features include:

- **Playout templates** — daypart schedules (Classic Cable, Kids All Day, Movie Marathon) guide what airs when
- **14-day rolling playout** — applied lineups rebuild up to 14 days ahead; FinTV auto-extends the schedule hourly
- Per-channel content mix (TV / movies / both) and fine-tune prompts

### Music channels

Create a channel with content type **Music**. FinTV streams audio with channel logo and album art slides.

## Development

```powershell
cd FinTV
dotnet restore Jellyfin.Plugin.FinTV/Jellyfin.Plugin.FinTV.csproj
dotnet build Jellyfin.Plugin.FinTV/Jellyfin.Plugin.FinTV.csproj
```

Copy build output to your Jellyfin plugins folder and restart the server.

## API endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /FinTV/iptv/channels.m3u` | M3U playlist |
| `GET /FinTV/iptv/epg.xml` | XMLTV guide |
| `GET /FinTV/iptv/stream/{channelId}` | Live MPEG-TS stream |
| `GET /FinTV/api/channels` | Channel CRUD |
| `GET /FinTV/api/lineups/{channelId}` | Lineup editor |
| `GET /FinTV/api/commercials` | Commercial library |
| `GET /FinTV/api/setup/urls` | Live TV setup helper |
| `GET /FinTV/api/weather/docker/status` | ws4kp/ws3kp Docker status |
| `POST /FinTV/api/weather/docker/ws4kp/start` | Start WeatherStar 4000+ container |
| `POST /FinTV/api/weather/docker/ws3kp/start` | Start WeatherStar 3000+ container |
| `GET /FinTV/api/ai/playout-templates` | Built-in AI daypart templates |
| `POST /FinTV/api/ai/channels/{id}/generate` | AI lineup preview |

## Credits

- [ErsatzTV/legacy](https://github.com/ErsatzTV/legacy) — scheduling / playout architecture inspiration
- [binarygeek119/open-channel-logos](https://github.com/binarygeek119/open-channel-logos) — channel logo set
- [netbymatt/ws4kp](https://github.com/netbymatt/ws4kp) / [netbymatt/ws3kp](https://github.com/netbymatt/ws3kp) — self-hosted WeatherStar Docker images
- [thornjad/weatherstar4k](https://github.com/thornjad/weatherstar4k) — WeatherStar 4000 display
- [binarygeek119/Open-Commercial-Pack](https://github.com/binarygeek119/Open-Commercial-Pack) — commercial content packs

## License

MIT
