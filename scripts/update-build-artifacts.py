#!/usr/bin/env python3
"""Append bundled logo and Playwright driver paths to build.yaml artifacts."""

from __future__ import annotations

import os
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD_YAML = ROOT / "build.yaml"
LOGOS_DIR = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "logos" / "binarygeek119"
SCRIPTS_DIR = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "scripts"
CSPROJ = ROOT / "Jellyfin.Plugin.FinTV" / "Jellyfin.Plugin.FinTV.csproj"
# Linux driver ships in ghcr.io/binarygeek119/jellyfin-unstable-fintv, not the plugin zip.
# yt-dlp ships in the FinTV Jellyfin Docker image, not the plugin zip.
PLAYWRIGHT_NODE_PLATFORMS = ("win32_x64",)


def read_playwright_version() -> str:
    text = CSPROJ.read_text(encoding="utf-8")
    match = re.search(
        r'<PackageReference Include="Microsoft\.Playwright" Version="([^"]+)"',
        text,
    )
    if not match:
        raise RuntimeError("Could not find Microsoft.Playwright package version in csproj")
    return match.group(1)


def nuget_packages_root() -> Path:
    return Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))


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


def playwright_artifacts(version: str) -> list[str]:
    package_root = nuget_packages_root() / "microsoft.playwright" / version
    browsers_root = package_root / ".playwright"
    artifacts: list[str] = []

    if not browsers_root.exists():
        return artifacts

    license_path = browsers_root / "node" / "LICENSE"
    if license_path.exists():
        rel = license_path.relative_to(package_root).as_posix()
        artifacts.append(f'  - "{rel}"')

    for platform in PLAYWRIGHT_NODE_PLATFORMS:
        platform_root = browsers_root / "node" / platform
        if not platform_root.exists():
            continue

        for path in sorted(platform_root.rglob("*")):
            if path.is_file():
                rel = path.relative_to(package_root).as_posix()
                artifacts.append(f'  - "{rel}"')

    package_root_files = browsers_root / "package"
    if package_root_files.exists():
        for path in sorted(package_root_files.rglob("*")):
            if path.is_file():
                rel = path.relative_to(package_root).as_posix()
                artifacts.append(f'  - "{rel}"')

    script_path = package_root / "buildTransitive" / "playwright.ps1"
    if script_path.exists():
        artifacts.append('  - "playwright.ps1"')

    return artifacts


def main() -> int:
    if not LOGOS_DIR.exists():
        print(f"No bundled logos found at {LOGOS_DIR}")
        return 1

    playwright_version = read_playwright_version()
    logos = logo_artifacts()
    scripts = script_artifacts()
    playwright = playwright_artifacts(playwright_version)
    if not playwright:
        print(
            "No Playwright driver artifacts found. Restore NuGet packages first "
            f"(microsoft.playwright/{playwright_version})."
        )
        return 1

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
        + playwright
        + lines[changelog_index:]
    )
    BUILD_YAML.write_text("\n".join(updated) + "\n", encoding="utf-8")
    print(
        f"Added {len(logos)} logo artifacts, {len(scripts)} script artifacts, "
        f"and {len(playwright)} Playwright artifacts to build.yaml"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
