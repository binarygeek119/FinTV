#!/usr/bin/env bash
# Publish self-contained ChannelFlow-Server apps for Linux and Windows.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-all}"
version="${CHANNELFLOW_VERSION:-1.0.0}"
revision="${CHANNELFLOW_REVISION:-dev}"
project="$root/src/ChannelFlow.Server/ChannelFlow.Server.csproj"

publish_rid() {
  local target="$1"
  local out="$root/artifacts/native/$target"
  echo "Publishing $target → $out"
  dotnet publish "$project" \
    -c Release \
    -r "$target" \
    --self-contained true \
    -o "$out" \
    /p:Version="$version" \
    /p:InformationalVersion="$version+$target.$revision" \
    /p:PublishReadyToRun=false
}

case "$rid" in
  linux-x64|win-x64)
    publish_rid "$rid"
    ;;
  all)
    publish_rid linux-x64
    publish_rid win-x64
    ;;
  *)
    echo "Usage: $0 [linux-x64|win-x64|all]" >&2
    exit 1
    ;;
esac
