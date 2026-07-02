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
- For WeatherStar channel: Playwright Chromium (auto-installed on first weather tune; Linux may need OS deps — see below)

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

**First tune:** FinTV downloads Chromium automatically into `{JellyfinData}/plugins/configurations/FinTV/playwright-browsers`.

**Headless Linux server:** install Playwright OS dependencies once (requires sudo):

```bash
sudo bash scripts/install-playwright-linux-deps.sh
```

Or: `sudo npx playwright install-deps chromium`

**Windows/Linux service account:** install FinTV from the plugin catalog, restart Jellyfin, then tune a weather channel once so Chromium can download under the Jellyfin data folder.

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

## Credits

- [ErsatzTV/legacy](https://github.com/ErsatzTV/legacy) — scheduling / playout architecture inspiration
- [binarygeek119/open-channel-logos](https://github.com/binarygeek119/open-channel-logos) — channel logo set
- [thornjad/weatherstar4k](https://github.com/thornjad/weatherstar4k) — WeatherStar 4000 display
- [binarygeek119/Open-Commercial-Pack](https://github.com/binarygeek119/Open-Commercial-Pack) — commercial content packs

## License

MIT
