#!/usr/bin/env bash

# Create a Slay the Spire II Archipelago release.
# This intentionally performs commits, tags, builds, pushes, and a GitHub
# release unless --skip-github is supplied. Do not invoke it for ordinary work.

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
ARCHIPELAGO_ROOT="${ARCHIPELAGO_ROOT:-$REPO_ROOT/../Archipelago}"
PYTHON_BIN="${PYTHON_BIN:-}"
PYTHON_WAS_EXPLICIT=false
VERSION=""
SKIP_GITHUB=false
RELEASE_TEMP_DIR=""
RELEASE_NOTES_FILE=""

usage() {
    cat <<'EOF'
Usage: scripts/release.sh VERSION [--skip-github] [options]
       scripts/release.sh --version VERSION [--skip-github] [options]

Options:
  --version VERSION          Release/tag label; X.Y.Z is extracted from it.
  --skip-github              Create the local commit/tag and artifacts only.
  --archipelago-root PATH    Archipelago source checkout.
  --python PATH              Python executable.
  -h, --help                 Show this help.

Environment overrides:
  ARCHIPELAGO_ROOT           Archipelago checkout (default: ../Archipelago)
  PYTHON_BIN                 Python executable (default: python3.13, python3)

This script requires a clean tracked working tree on branch main. It updates
the four synchronized public version files, optionally updates local.props,
commits the public files, builds the APWorld and client, creates dist assets,
tags the commit, and—unless skipped—pushes main/tag and creates a GitHub release.
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

warn() {
    printf 'warning: %s\n' "$*" >&2
}

cleanup() {
    if [[ -n "$RELEASE_TEMP_DIR" && -d "$RELEASE_TEMP_DIR" ]]; then
        rm -rf -- "$RELEASE_TEMP_DIR"
    fi
    if [[ -n "$RELEASE_NOTES_FILE" && -f "$RELEASE_NOTES_FILE" ]]; then
        rm -f -- "$RELEASE_NOTES_FILE"
    fi
}
trap cleanup EXIT

while (($#)); do
    case "$1" in
        --version)
            (($# >= 2)) || die "--version requires a value"
            [[ -z "$VERSION" ]] || die "version was specified more than once"
            VERSION="$2"
            shift 2
            ;;
        --skip-github|-skipGitHub)
            SKIP_GITHUB=true
            shift
            ;;
        --archipelago-root)
            (($# >= 2)) || die "--archipelago-root requires a path"
            ARCHIPELAGO_ROOT="$2"
            shift 2
            ;;
        --python)
            (($# >= 2)) || die "--python requires an executable path"
            PYTHON_BIN="$2"
            PYTHON_WAS_EXPLICIT=true
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        -*)
            die "unknown argument: $1"
            ;;
        *)
            [[ -z "$VERSION" ]] || die "unexpected positional argument: $1"
            VERSION="$1"
            shift
            ;;
    esac
done

if [[ -n "$PYTHON_BIN" ]]; then
    PYTHON_WAS_EXPLICIT=true
fi

[[ -n "$VERSION" ]] || die "a release version is required"
if [[ "$VERSION" =~ ([0-9]+\.[0-9]+\.[0-9]+) ]]; then
    SEMVER="${BASH_REMATCH[1]}"
else
    die "version '$VERSION' does not contain X.Y.Z"
fi

for command_name in git dotnet zip; do
    command -v "$command_name" >/dev/null 2>&1 || die "$command_name is required"
done

if [[ -z "$PYTHON_BIN" ]]; then
    if command -v python3.13 >/dev/null 2>&1; then
        PYTHON_BIN="$(command -v python3.13)"
    elif command -v python3 >/dev/null 2>&1; then
        PYTHON_BIN="$(command -v python3)"
    else
        die "python3.13 or python3 is required"
    fi
elif [[ "$PYTHON_BIN" != */* ]]; then
    PYTHON_BIN="$(command -v "$PYTHON_BIN")" || die "Python executable not found: $PYTHON_BIN"
fi

CURRENT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)"
[[ "$CURRENT_BRANCH" == main ]] || die "release requires branch main; current branch is $CURRENT_BRANCH"
git -C "$REPO_ROOT" diff --quiet || die "tracked working tree has unstaged changes"
git -C "$REPO_ROOT" diff --cached --quiet || die "index has staged changes"

printf 'Input version: %s\nExtracted semver: %s\n' "$VERSION" "$SEMVER"

"$PYTHON_BIN" - "$REPO_ROOT" "$SEMVER" <<'PY'
from __future__ import annotations

import re
import sys
from pathlib import Path


repo_root = Path(sys.argv[1])
version = sys.argv[2]
replacements = [
    (
        repo_root / "client/StS2AP/StS2AP.csproj",
        r'<ModVersion Condition="[^"]*">[^<]*</ModVersion>',
        f'<ModVersion Condition="\'$(ModVersion)\' == \'\'">{version}</ModVersion>',
        True,
    ),
    (
        repo_root / "client/StS2AP/local.props",
        r"<ModVersion>[^<]*</ModVersion>",
        f"<ModVersion>{version}</ModVersion>",
        False,
    ),
    (
        repo_root / "world/spire2/archipelago.json",
        r'"world_version"\s*:\s*"[^"]+"',
        f'"world_version": "{version}"',
        True,
    ),
    (
        repo_root / "client/StS2AP/Archipelago.json",
        r'"version"\s*:\s*"[^"]+"',
        f'"version": "{version}"',
        True,
    ),
    (
        repo_root / "world/spire2/world.py",
        r'(mod_compat_version\s*=\s*")[^"]+"',
        rf'\g<1>{version}"',
        True,
    ),
]

for path, pattern, replacement, required in replacements:
    if not path.exists():
        if required:
            raise SystemExit(f"required file not found: {path}")
        print(f"Skipping optional file: {path}")
        continue
    original = path.read_text(encoding="utf-8")
    updated, count = re.subn(pattern, replacement, original, count=1)
    if count != 1:
        raise SystemExit(f"expected exactly one version match in {path}, found {count}")
    if updated == original:
        print(f"Already up to date: {path}")
    else:
        path.write_text(updated, encoding="utf-8")
        print(f"Updated: {path}")
PY

VERSION_FILES=(
    "client/StS2AP/StS2AP.csproj"
    "client/StS2AP/Archipelago.json"
    "world/spire2/archipelago.json"
    "world/spire2/world.py"
)

git -C "$REPO_ROOT" add -- "${VERSION_FILES[@]}"
git -C "$REPO_ROOT" diff --cached --quiet && die "version files were already at $SEMVER; nothing to commit"
git -C "$REPO_ROOT" commit --message "$VERSION"

BUILD_WORLD_ARGS=(--archipelago-root "$ARCHIPELAGO_ROOT")
if [[ "$PYTHON_WAS_EXPLICIT" == true ]]; then
    BUILD_WORLD_ARGS+=(--python "$PYTHON_BIN")
fi
"$SCRIPT_DIR/build_world.sh" "${BUILD_WORLD_ARGS[@]}"

CSPROJ="$REPO_ROOT/client/StS2AP/StS2AP.csproj"
printf 'Building C# client in Release configuration\n'
dotnet build "$CSPROJ" -c Release

MSBUILD_JSON="$(dotnet msbuild "$CSPROJ" -getProperty:ModsOutputDir -getProperty:ModName)"
MODS_OUTPUT_DIR="$(printf '%s' "$MSBUILD_JSON" | "$PYTHON_BIN" -c 'import json, sys; print(json.load(sys.stdin)["Properties"]["ModsOutputDir"])')"
MOD_NAME="$(printf '%s' "$MSBUILD_JSON" | "$PYTHON_BIN" -c 'import json, sys; print(json.load(sys.stdin)["Properties"]["ModName"])')"
PCK_PATH="$MODS_OUTPUT_DIR/$MOD_NAME.pck"
if [[ ! -f "$PCK_PATH" ]]; then
    warn ".pck not found at $PCK_PATH; it will not be included"
fi

OUTPUT_DIR="$REPO_ROOT/client/StS2AP/bin/Release/net9.0"
DIST_DIR="$REPO_ROOT/dist"
ZIP_PATH="$DIST_DIR/sts2-client.zip"
[[ -d "$OUTPUT_DIR" ]] || die "build output directory not found: $OUTPUT_DIR"
mkdir -p -- "$DIST_DIR"
rm -f -- "$ZIP_PATH"

RELEASE_TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/sts2release.XXXXXX")"
ARCHIVE_DIR="$RELEASE_TEMP_DIR/Archipelago"
mkdir -p -- "$ARCHIVE_DIR"
FILE_COUNT=0
while IFS= read -r -d '' file; do
    name="$(basename -- "$file")"
    case "$name" in
        *.pdb|*.xml|*.deps.json|sts2.dll|0Harmony.dll|GodotSharp.dll)
            continue
            ;;
    esac
    cp -- "$file" "$ARCHIVE_DIR/$name"
    ((FILE_COUNT += 1))
done < <(find "$OUTPUT_DIR" -maxdepth 1 -type f -print0)

if [[ -f "$PCK_PATH" ]]; then
    cp -- "$PCK_PATH" "$ARCHIVE_DIR/$(basename -- "$PCK_PATH")"
    ((FILE_COUNT += 1))
fi
((FILE_COUNT > 0)) || die "no client release files were found"

(
    cd -- "$RELEASE_TEMP_DIR"
    zip -qr "$ZIP_PATH" Archipelago
)
[[ -f "$ZIP_PATH" ]] || die "failed to create $ZIP_PATH"
[[ -f "$DIST_DIR/spire2.apworld" ]] || die "spire2.apworld is missing from dist"
printf 'Created %s with %d files\n' "$ZIP_PATH" "$FILE_COUNT"

git -C "$REPO_ROOT" tag "$VERSION" HEAD
printf 'Tagged HEAD as %s\n' "$VERSION"

if [[ "$SKIP_GITHUB" == true ]]; then
    printf 'Skipping GitHub push and release; commit and tag are local only.\n'
    exit 0
fi

command -v gh >/dev/null 2>&1 || die "GitHub CLI (gh) is required"
git -C "$REPO_ROOT" push origin main
git -C "$REPO_ROOT" push origin "$VERSION"

TEMPLATE_PATH="$SCRIPT_DIR/release-notes-template.md"
[[ -f "$TEMPLATE_PATH" ]] || die "release notes template not found: $TEMPLATE_PATH"
RELEASE_NOTES_FILE="$(mktemp "${TMPDIR:-/tmp}/sts2-release-notes.XXXXXX.md")"
"$PYTHON_BIN" - "$TEMPLATE_PATH" "$RELEASE_NOTES_FILE" "$VERSION" <<'PY'
import sys
from pathlib import Path

template = Path(sys.argv[1]).read_text(encoding="utf-8")
Path(sys.argv[2]).write_text(template.replace("{{VERSION}}", sys.argv[3]), encoding="utf-8")
PY

ASSETS=()
while IFS= read -r -d '' asset; do
    ASSETS+=("$asset")
done < <(find "$DIST_DIR" -maxdepth 1 -type f -print0)
((${#ASSETS[@]} > 0)) || die "no release assets found in $DIST_DIR"

gh release create "$VERSION" "${ASSETS[@]}" \
    --title "$VERSION" \
    --notes-file "$RELEASE_NOTES_FILE" \
    --latest

printf 'Created GitHub release %s. Update the changelist in its notes if needed.\n' "$VERSION"
