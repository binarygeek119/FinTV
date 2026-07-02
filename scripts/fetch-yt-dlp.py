#!/usr/bin/env python3
"""Download yt-dlp standalone binaries for Windows and Linux."""

from __future__ import annotations

import os
import stat
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION_FILE = ROOT / "scripts" / "yt-dlp-version.txt"
DEFAULT_OUTPUT = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "tools" / "yt-dlp"
USER_AGENT = "FinTV-Jellyfin-Plugin"

PLATFORMS = {
    "win-x64": ("yt-dlp.exe", "yt-dlp.exe"),
    "linux-x64": ("yt-dlp_linux", "yt-dlp"),
}


def read_version() -> str:
    if not VERSION_FILE.exists():
        raise RuntimeError(f"Missing yt-dlp version file: {VERSION_FILE}")
    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    if not version:
        raise RuntimeError(f"yt-dlp version file is empty: {VERSION_FILE}")
    return version


def download(url: str, destination: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request) as response, destination.open("wb") as handle:
        handle.write(response.read())


def fetch_platform(version: str, platform: str, asset_name: str, output_name: str, output_root: Path) -> None:
    platform_dir = output_root / platform
    platform_dir.mkdir(parents=True, exist_ok=True)
    destination = platform_dir / output_name
    url = f"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/{asset_name}"
    print(f"Downloading {url} -> {destination}")
    download(url, destination)
    if platform.startswith("linux"):
        destination.chmod(destination.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def main() -> int:
    output_root = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUTPUT
    version = read_version()
    output_root.mkdir(parents=True, exist_ok=True)

    for platform, (asset_name, output_name) in PLATFORMS.items():
        fetch_platform(version, platform, asset_name, output_name, output_root)

    (output_root / "VERSION.txt").write_text(version + os.linesep, encoding="utf-8")
    print(f"Bundled yt-dlp {version} for {', '.join(PLATFORMS)} into {output_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
