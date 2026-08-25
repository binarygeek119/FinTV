#!/usr/bin/env python3
"""Download ChannelFlow channel logos from FlowMeadow01/ChannelFlow-logo."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO = "FlowMeadow01/ChannelFlow-logo"
GIT_REF = "main"
TREE_URL = f"https://api.github.com/repos/{REPO}/git/trees/{GIT_REF}?recursive=1"
RAW_BASE = f"https://raw.githubusercontent.com/{REPO}/{GIT_REF}/"
LOGO_PREFIXES = (
    "EBS/",
    "OFFLINE/",
    "Movies/",
    "News/",
    "Shows/",
    "Music Videos Channels/",
    "The Holiday Channel/",
    "The_Holiday_Channel_Filler/",
    "Weather/",
)
IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp"}
AUDIO_SUFFIXES = {".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".opus"}


def is_image(path: str) -> bool:
    return Path(path).suffix.lower() in IMAGE_SUFFIXES


def is_news_audio(path: str) -> bool:
    return path.startswith("News/") and Path(path).suffix.lower() in AUDIO_SUFFIXES


def is_logo_path(path: str) -> bool:
    return any(path.startswith(prefix) for prefix in LOGO_PREFIXES)


def destination_relative(repo_path: str) -> str:
    # Off-air stills live in OFFLINE/ on GitHub; ChannelFlow looks them up under EBS/.
    if repo_path.startswith("OFFLINE/"):
        return "EBS/" + Path(repo_path).name
    return repo_path


def build_request(url: str) -> urllib.request.Request:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "ChannelFlow-Server",
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
        and (is_image(item["path"]) or is_news_audio(item["path"]))
    ]

    print(f"Bundling {len(files)} ChannelFlow assets from {REPO}@{GIT_REF} into {output_dir}")
    for item in files:
        repo_path = item["path"]
        relative = destination_relative(repo_path)
        destination = output_dir / relative.replace("/", "\\") if sys.platform == "win32" else output_dir / relative
        if destination.exists():
            continue
        download_file(repo_path, destination)
        print(f"  {repo_path} -> {relative}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
