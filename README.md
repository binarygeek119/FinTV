<p align="center">
  <img src="logo.png" alt="ChannelFlow-Server" width="320" />
</p>

# ChannelFlow-Server

Simulated live TV for [Jellyfin](https://jellyfin.org). This repository is **ChannelFlow-Server** — a .NET 10 app with a red Jellyfin-style Web UI, PostgreSQL, local library playback, WeatherStar, and news.

Home: [github.com/FlowMeadow01/ChannelFlow](https://github.com/FlowMeadow01/ChannelFlow)

The Jellyfin plugin (GUID `f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b`) syncs library metadata/paths/chapters, registers Live TV M3U + XMLTV, and runs blackframe chapter detection.

## What runs where

| ChannelFlow-Server (this repo) | ChannelFlow Plugin |
| --- | --- |
| Channels, lineups, playout, FFmpeg MPEG-TS | Server URL + API key |
| Commercials / CommercialBrainz | Catalog metadata sync (IDs, tags, duration, **path**, **chapters**) |
| EBS, logos, AI lineups | Live TV tuner + XMLTV registration |
| WeatherStar 4000/3000 (native compositor, vendored ws4kp/ws3kp) | Blackframe scan + optional write chapters |
| News RSS + TTS channel | |
| Web UI (username/password) | |

Playback reads **local files**. Configure **path remaps** in Settings (Jellyfin prefix → ChannelFlow-Server prefix), for example `/data/media` → `/media`.

## Requirements

- .NET 10 SDK to build, or a self-contained publish from `scripts/publish-native.sh`
- PostgreSQL (your own instance)
- FFmpeg on PATH (or set `FFMPEG_PATH`)
- Jellyfin 12 + the ChannelFlow plugin
- The same media paths readable by Jellyfin and ChannelFlow-Server

## Run

```bash
cp .env.example .env
# set POSTGRES_* and JELLYFIN_URL for your host
dotnet publish src/ChannelFlow.Server/ChannelFlow.Server.csproj -c Release
# or: bash scripts/publish-native.sh linux-x64
```

Load the `.env` values into the process environment, then run `ChannelFlow.Server` (from `bin/.../publish` or `artifacts/native/linux-x64`). Listen port is `PORT` (default `8097`). Config, logos, weather, and news files live under `CHANNELFLOW_CONFIG` (default `config` next to the app).

Then:

1. Open `http://<host>:8097` and create the admin username and password on first launch
2. Copy the plugin API key from **General** (created automatically on first boot)
3. Add path remaps under General (Jellyfin prefix → the local path ChannelFlow-Server can read)
4. Install the ChannelFlow plugin, set Server URL + API key, run **ChannelFlow Catalog Sync**

Items removed from Jellyfin, or whose remapped local file is gone, are marked missing, then deleted by **Tasks → Catalog cleanup** after the grace period (default 7 days). **Scan Local Files** checks each catalog path after remap.

Set `FFMPEG_HWACCEL=vaapi` and pass `/dev/dri` access for Intel VAAPI encode/decode.

The plugin registers the Live TV tuner and XMLTV guide automatically when you set the ChannelFlow-Server URL and API key.

## Reverse proxy

ChannelFlow expects to sit behind Nginx Proxy Manager, SWAG, Caddy, or Traefik on a hostname such as `https://channelflow.example.duckdns.org`. Forward `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-For`. Leave live MPEG-TS unbuffered (`proxy_buffering off` / Caddy `flush_interval -1`) and raise the read timeout (an hour is enough).

Optional env vars:

- `CHANNELFLOW_PUBLIC_URL` — public origin used in M3U/XMLTV when Jellyfin fetches them from another host (example: `https://channelflow.example.duckdns.org`)
- `CHANNELFLOW_PATH_BASE` — only if the UI is served under a subpath such as `/channelflow`

Legacy `FINTV_*` names for those same variables still work.

You can also set **Public base URL** on the General tab. Login cookies stay valid across restarts (keys live in the config folder under `dataprotection`).

Nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:8097;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
    proxy_buffering off;
    proxy_request_buffering off;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
}
```

Caddy:

```caddy
channelflow.example.duckdns.org {
    reverse_proxy 127.0.0.1:8097 {
        flush_interval -1
    }
}
```

## Weather and news

WeatherStar graphics are vendored from [ws4kp](https://github.com/netbymatt/ws4kp) and [ws3kp](https://github.com/netbymatt/ws3kp) (MIT) and rendered by the native compositor, then encoded to MPEG-TS with optional Jellyfin music as a bed.

News is a 24/7 channel: RSS feeds from the **News** page, optional TTS, FFmpeg overlay, and bed music.

## License

ChannelFlow-Server code follows this repository's license. WeatherStar vendors keep their upstream MIT licenses.
