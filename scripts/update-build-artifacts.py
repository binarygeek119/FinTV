#!/usr/bin/env python3
"""Append bundled logo and script paths to build.yaml artifacts."""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD_YAML = ROOT / "build.yaml"
LOGOS_DIR = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "logos" / "binarygeek119"
SCRIPTS_DIR = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "scripts"


def logo_artifacts() -> list[str]:
    if not LOGOS_DIR.exists():
        return []

    return [
        f'  - "logos/binarygeek119/{path.relative_to(LOGOS_DIR).as_posix()}"'
        for path in sorted(LOGOS_DIR.rglob("*"))
        if path.is_file()
    ]


def script_artifacts() -> list[str]:
    if not SCRIPTS_DIR.exists():
        return []

    return [
        f'  - "scripts/{path.relative_to(SCRIPTS_DIR).as_posix()}"'
        for path in sorted(SCRIPTS_DIR.rglob("*"))
        if path.is_file()
    ]


def main() -> int:
    if not LOGOS_DIR.exists():
        print(f"No bundled logos found at {LOGOS_DIR}")
        return 1

    logos = logo_artifacts()
    scripts = script_artifacts()

    text = BUILD_YAML.read_text(encoding="utf-8")
    lines = text.splitlines()
    artifacts_index = next(i for i, line in enumerate(lines) if line.strip() == "artifacts:")
    changelog_index = next(i for i, line in enumerate(lines) if line.strip().startswith("changelog:"))

    base_artifacts = [
        line
        for line in lines[artifacts_index + 1 : changelog_index]
        if line.strip()
        and not line.strip().startswith('- "logos/binarygeek119/')
        and not line.strip().startswith('- "tools/yt-dlp/')
        and not line.strip().startswith('- "scripts/')
        and not line.strip().startswith('- ".playwright/')
        and line.strip() not in ('- "playwright.ps1"', '- "playwright.sh"')
    ]

    updated = (
        lines[: artifacts_index + 1]
        + base_artifacts
        + logos
        + scripts
        + lines[changelog_index:]
    )
    BUILD_YAML.write_text("\n".join(updated) + "\n", encoding="utf-8")
    print(
        f"Added {len(logos)} logo artifacts and {len(scripts)} script artifacts to build.yaml"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
