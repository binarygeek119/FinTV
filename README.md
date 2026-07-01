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

- Jellyfin **10.11+**
- FFmpeg (bundled with Jellyfin)
- For WeatherStar channel: Playwright Chromium (installed on first use)

## Install from GitHub

1. Open Jellyfin **Dashboard → Plugins → Repositories**
2. Add repository URL:
   ```
   https://raw.githubusercontent.com/binarygeek119/FinTV/master/manifest.json
   ```
3. Open **Catalog**, install **FinTV**, restart Jellyfin

### Manual install

1. Download `Jellyfin.Plugin.FinTV.zip` from [Releases](https://github.com/binarygeek119/FinTV/releases)
2. Extract to `{JellyfinData}/plugins/FinTV/`
3. Restart Jellyfin

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

Create a channel with content type **Weather**, set latitude/longitude, enable it, and rebuild playout. Uses [weatherstar4k](https://github.com/thornjad/weatherstar4k) styling via embedded weather display.

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
