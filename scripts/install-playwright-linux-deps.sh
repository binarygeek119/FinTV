#!/usr/bin/env bash
set -euo pipefail

cat <<'EOF'
FinTV on Linux uses Playwright's official Docker image for weather channels.

Requirements:
  - Docker installed and running
  - Jellyfin service user can run docker (for example: sudo usermod -aG docker jellyfin)
  - Port 9222 available on localhost

On the first weather tune, FinTV starts container:
  fintv-playwright-chromium

Manual pull (optional):
  docker pull mcr.microsoft.com/playwright:v1.49.0-jammy

Check container:
  docker logs fintv-playwright-chromium

Legacy OS-level Playwright deps are no longer required when Docker is used.
EOF
