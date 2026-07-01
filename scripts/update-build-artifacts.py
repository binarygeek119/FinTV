#!/usr/bin/env python3
"""Append bundled logo paths to build.yaml artifacts."""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD_YAML = ROOT / "build.yaml"
LOGOS_DIR = ROOT / "Jellyfin.Plugin.FinTV" / "Assets" / "logos" / "binarygeek119"


def main() -> int:
    if not LOGOS_DIR.exists():
        print(f"No bundled logos found at {LOGOS_DIR}")
        return 1

    text = BUILD_YAML.read_text(encoding="utf-8")
    lines = text.splitlines()
    artifacts_index = next(i for i, line in enumerate(lines) if line.strip() == "artifacts:")
    changelog_index = next(i for i, line in enumerate(lines) if line.strip().startswith("changelog:"))

    base_artifacts = [
        line
        for line in lines[artifacts_index + 1 : changelog_index]
        if line.strip() and not line.strip().startswith("- \"logos/binarygeek119/")
    ]

    logo_artifacts = [
        f'  - "logos/binarygeek119/{path.relative_to(LOGOS_DIR).as_posix()}"'
        for path in sorted(LOGOS_DIR.rglob("*"))
        if path.is_file()
    ]

    updated = (
        lines[: artifacts_index + 1]
        + base_artifacts
        + logo_artifacts
        + lines[changelog_index:]
    )
    BUILD_YAML.write_text("\n".join(updated) + "\n", encoding="utf-8")
    print(f"Added {len(logo_artifacts)} logo artifacts to build.yaml")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
