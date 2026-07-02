#!/usr/bin/env bash
set -euo pipefail

if ! command -v npx >/dev/null 2>&1; then
  echo "npx is required. Install Node.js/npm, then rerun this script."
  exit 1
fi

echo "Installing Playwright Chromium OS dependencies for headless Linux servers..."
sudo npx --yes playwright install-deps chromium
echo "Done. Restart Jellyfin, then tune a FinTV weather channel once to download Chromium."
