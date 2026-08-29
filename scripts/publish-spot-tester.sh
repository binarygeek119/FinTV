#!/usr/bin/env bash
# Publish self-contained Commercial Spot Tester for Linux and Windows.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-all}"
version="${CHANNELFLOW_VERSION:-1.0.0}"
revision="${CHANNELFLOW_REVISION:-dev}"
project="$root/src/ChannelFlow.CommercialSpotTester/ChannelFlow.CommercialSpotTester.csproj"
artifacts="$root/artifacts/spot-tester"

publish_rid() {
  local target="$1"
  local out="$artifacts/$target"
  echo "Publishing Commercial Spot Tester $target → $out"
  rm -rf "$out"
  dotnet publish "$project" \
    -c Release \
    -r "$target" \
    --self-contained true \
    -o "$out" \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:EnableCompressionInSingleFile=true \
    /p:Version="$version" \
    /p:InformationalVersion="$version+$target.$revision" \
    /p:DebugType=none \
    /p:DebugSymbols=false
}

package_rid() {
  local target="$1"
  mkdir -p "$artifacts"
  if [[ "$target" == win-x64 ]]; then
    (cd "$artifacts" && rm -f CommercialSpotTester-win-x64.zip && zip -qr CommercialSpotTester-win-x64.zip win-x64)
  else
    tar -C "$artifacts" -czf "$artifacts/CommercialSpotTester-linux-x64.tar.gz" linux-x64
  fi
}

case "$rid" in
  linux-x64|win-x64)
    publish_rid "$rid"
    package_rid "$rid"
    ;;
  all)
    publish_rid linux-x64
    package_rid linux-x64
    publish_rid win-x64
    package_rid win-x64
    ;;
  *)
    echo "Usage: $0 [linux-x64|win-x64|all]" >&2
    exit 1
    ;;
esac
