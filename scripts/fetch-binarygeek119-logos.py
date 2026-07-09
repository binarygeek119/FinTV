#!/usr/bin/env python3
"""Download FinTV channel logos from the open-channel-logos fintv2 branch."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO = "binarygeek119/open-channel-logos"
GIT_REF = "fintv2"
TREE_URL = f"https://api.github.com/repos/{REPO}/git/trees/{GIT_REF}?recursive=1"
RAW_BASE = f"https://raw.githubusercontent.com/{REPO}/{GIT_REF}/"
LOGO_PREFIXES = (
    "EBS/",
    "Movies/",
    "News/",
    "Shows/",
    "Music Videos Channels/",
    "The Holiday Channel/",
    "Weather/",
)
IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp"}


def is_image(path: str) -> bool:
    return Path(path).suffix.lower() in IMAGE_SUFFIXES


def is_logo_path(path: str) -> bool:
    return any(path.startswith(prefix) for prefix in LOGO_PREFIXES)


def build_request(url: str) -> urllib.request.Request:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "FinTV-Jellyfin-Plugin",
    }
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return urllib.request.Request(url, headers=headers)


def urlopen_with_retry(request: urllib.request.Request, timeout: int = 120) -> object:
    max_attempts = 8
    for attempt in range(max_attempts):
        try:
            return urllib.request.urlopen(request, timeout=timeout)
        except urllib.error.HTTPError as ex:
            if ex.code in {429, 500, 502, 503, 504} and attempt < max_attempts - 1:
                time.sleep(min(60, 2 ** attempt))
                continue
            raise


def fetch_tree() -> list[dict]:
    request = build_request(TREE_URL)
    with urlopen_with_retry(request) as response:
        payload = json.load(response)
    return payload.get("tree") or []


def download_file(repo_path: str, destination: Path) -> None:
    encoded = "/".join(urllib.parse.quote(part) for part in repo_path.split("/"))
    url = RAW_BASE + encoded
    request = build_request(url)
    with urlopen_with_retry(request) as response:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(response.read())


def main() -> int:
    output_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "Jellyfin.Plugin.FinTV/Assets/logos/binarygeek119")
    output_dir.mkdir(parents=True, exist_ok=True)

    files = [
        item
        for item in fetch_tree()
        if item.get("type") == "blob"
        and is_logo_path(item.get("path", ""))
        and is_image(item["path"])
    ]

    print(f"Bundling {len(files)} logos from {REPO}@{GIT_REF} into {output_dir}")
    for item in files:
        relative = item["path"]
        destination = output_dir / relative.replace("/", "\\") if sys.platform == "win32" else output_dir / relative
        if destination.exists():
            continue
        download_file(item["path"], destination)
        print(f"  {relative}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
