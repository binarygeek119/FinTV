#!/usr/bin/env bash
# Start local Postgres (podman) and run ChannelFlow-Server from source.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [[ ! -f .env ]]; then
  cp .env.example .env
  python3 - <<'PY'
from pathlib import Path
p = Path(".env")
text = p.read_text()
text = text.replace("POSTGRES_PORT=5432", "POSTGRES_PORT=5433")
p.write_text(text)
PY
  {
    echo "CHANNELFLOW_CONFIG=$root/config"
    echo "FFMPEG_PATH=$root/tools/ffmpeg"
    echo "CHANNELFLOW_YTDLP_PATH=${CHANNELFLOW_YTDLP_PATH:-/usr/bin/yt-dlp}"
  } >> .env
fi

if [[ -x "$root/tools/ffmpeg" ]]; then
  export PATH="$root/tools:$PATH"
fi

if ! podman ps --format '{{.Names}}' | grep -qx channelflow-postgres; then
  if podman ps -a --format '{{.Names}}' | grep -qx channelflow-postgres; then
    echo "Starting channelflow-postgres"
    podman start channelflow-postgres >/dev/null
  else
    echo "Creating channelflow-postgres on 127.0.0.1:5433"
    podman run -d --name channelflow-postgres \
      -e POSTGRES_USER=fintv \
      -e POSTGRES_PASSWORD=fintv \
      -e POSTGRES_DB=fintv \
      -p 127.0.0.1:5433:5432 \
      -v channelflow-pgdata:/var/lib/postgresql/data \
      docker.io/library/postgres:16-alpine >/dev/null
  fi
fi

echo "Waiting for Postgres…"
for _ in $(seq 1 40); do
  if podman exec channelflow-postgres pg_isready -U fintv -d fintv >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
done
podman exec channelflow-postgres pg_isready -U fintv -d fintv >/dev/null

set -a
# shellcheck disable=SC1091
source "$root/.env"
set +a

export CHANNELFLOW_CONFIG="${CHANNELFLOW_CONFIG:-$root/config}"
export FFMPEG_PATH="${FFMPEG_PATH:-$root/tools/ffmpeg}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export DOTNET_hostBuilder__reloadConfigOnChange="${DOTNET_hostBuilder__reloadConfigOnChange:-false}"
mkdir -p "$CHANNELFLOW_CONFIG"

echo "ChannelFlow-Server → http://127.0.0.1:${PORT:-8097}"
if [[ -z "${POSTGRES_HOST:-}" ]]; then
  echo "Postgres is on 127.0.0.1:5433 (user/db fintv). Configure it in the web UI on first launch."
fi
exec dotnet run --project "$root/src/ChannelFlow.Server/ChannelFlow.Server.csproj" --no-launch-profile
