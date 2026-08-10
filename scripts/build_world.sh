#!/usr/bin/env bash

# Build the Slay the Spire II APWorld from an Archipelago source checkout.

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
ARCHIPELAGO_ROOT="${ARCHIPELAGO_ROOT:-$REPO_ROOT/../Archipelago}"
PYTHON_BIN="${PYTHON_BIN:-}"
PYTHON_WAS_EXPLICIT=false
STAGING_DIR=""

usage() {
    cat <<'EOF'
Usage: scripts/build_world.sh [--archipelago-root PATH] [--python PATH]

Synchronize world/spire2 into an Archipelago source checkout, invoke
Launcher.py "Build APWorlds" "Slay the Spire II", and copy the resulting
spire2.apworld into this repository's dist directory.

Environment overrides:
  ARCHIPELAGO_ROOT  Archipelago source checkout (default: ../Archipelago)
  PYTHON_BIN        Existing environment's Python executable

Without PYTHON_BIN/--python, the script uses Archipelago/.venv. If that
environment does not exist, uv is used to create it with a supported Python
and seed packages. Archipelago's ModuleUpdate.py --yes then installs or checks
the checkout's pinned requirements inside that isolated environment.
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

cleanup() {
    if [[ -n "$STAGING_DIR" && -e "$STAGING_DIR" ]]; then
        rm -rf -- "$STAGING_DIR"
    fi
}
trap cleanup EXIT

while (($#)); do
    case "$1" in
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
        *)
            die "unknown argument: $1"
            ;;
    esac
done

if [[ -n "$PYTHON_BIN" ]]; then
    PYTHON_WAS_EXPLICIT=true
fi

if [[ "$PYTHON_WAS_EXPLICIT" == true && "$PYTHON_BIN" != */* ]]; then
    PYTHON_BIN="$(command -v "$PYTHON_BIN")" || die "Python executable not found: $PYTHON_BIN"
fi

[[ -d "$ARCHIPELAGO_ROOT" ]] || die "Archipelago checkout not found: $ARCHIPELAGO_ROOT"
ARCHIPELAGO_ROOT="$(cd -- "$ARCHIPELAGO_ROOT" && pwd -P)"

LOCAL_WORLD="$REPO_ROOT/world/spire2"
WORLDS_DIR="$ARCHIPELAGO_ROOT/worlds"
TARGET_WORLD="$WORLDS_DIR/spire2"
LAUNCHER="$ARCHIPELAGO_ROOT/Launcher.py"
BUILT_APWORLD="$ARCHIPELAGO_ROOT/build/apworlds/spire2.apworld"
DIST_DIR="$REPO_ROOT/dist"
MODULE_UPDATE="$ARCHIPELAGO_ROOT/ModuleUpdate.py"

[[ -d "$LOCAL_WORLD" ]] || die "local APWorld source not found: $LOCAL_WORLD"
[[ -d "$WORLDS_DIR" ]] || die "Archipelago worlds directory not found: $WORLDS_DIR"
[[ -f "$LAUNCHER" ]] || die "Launcher.py not found: $LAUNCHER"
[[ -f "$MODULE_UPDATE" ]] || die "ModuleUpdate.py not found: $MODULE_UPDATE"

if [[ "$PYTHON_WAS_EXPLICIT" != true ]]; then
    VENV_PYTHON="$ARCHIPELAGO_ROOT/.venv/bin/python"
    if [[ ! -x "$VENV_PYTHON" ]]; then
        command -v uv >/dev/null 2>&1 || die \
            "Archipelago/.venv does not exist and uv is unavailable; create a virtual environment first"

        if command -v python3.13 >/dev/null 2>&1; then
            BASE_PYTHON="$(command -v python3.13)"
        elif command -v python3.12 >/dev/null 2>&1; then
            BASE_PYTHON="$(command -v python3.12)"
        elif command -v python3.11 >/dev/null 2>&1; then
            BASE_PYTHON="$(command -v python3.11)"
        else
            # Let uv locate or download a supported interpreter instead of
            # accidentally selecting an incompatible system Python 3.14+.
            BASE_PYTHON="3.13"
        fi

        printf 'Creating isolated Archipelago environment at %s\n' "$ARCHIPELAGO_ROOT/.venv"
        uv venv --seed --python "$BASE_PYTHON" "$ARCHIPELAGO_ROOT/.venv"
    fi
    PYTHON_BIN="$VENV_PYTHON"
fi

[[ -x "$PYTHON_BIN" ]] || die "Python executable is not runnable: $PYTHON_BIN"
"$PYTHON_BIN" -c 'import sys; raise SystemExit(0 if (3, 11) <= sys.version_info[:2] < (3, 14) else 1)' || \
    die "Archipelago requires Python 3.11-3.13: $PYTHON_BIN"

printf 'Syncing world/spire2 into %s\n' "$ARCHIPELAGO_ROOT"
STAGING_DIR="$WORLDS_DIR/.spire2.sync.$$"
[[ ! -e "$STAGING_DIR" ]] || die "temporary sync path already exists: $STAGING_DIR"
cp -R -- "$LOCAL_WORLD" "$STAGING_DIR"
if [[ -e "$TARGET_WORLD" ]]; then
    rm -rf -- "$TARGET_WORLD"
fi
mv -- "$STAGING_DIR" "$TARGET_WORLD"
STAGING_DIR=""

printf 'Checking Archipelago requirements inside %s\n' "$PYTHON_BIN"
if ! "$PYTHON_BIN" -c 'import pkg_resources' >/dev/null 2>&1; then
    if [[ "$PYTHON_WAS_EXPLICIT" == true ]] && \
        ! "$PYTHON_BIN" -c 'import sys; raise SystemExit(0 if sys.prefix != sys.base_prefix else 1)'; then
        die "the selected global Python lacks pkg_resources; pass a virtual-environment Python or omit --python"
    fi
fi
(
    cd -- "$ARCHIPELAGO_ROOT"
    "$PYTHON_BIN" "$MODULE_UPDATE" --yes
)

printf 'Building Slay the Spire II APWorld with %s\n' "$PYTHON_BIN"
(
    cd -- "$ARCHIPELAGO_ROOT"
    SKIP_REQUIREMENTS_UPDATE=1 "$PYTHON_BIN" "$LAUNCHER" "Build APWorlds" "Slay the Spire II"
)

[[ -f "$BUILT_APWORLD" ]] || die "build succeeded but output was not found: $BUILT_APWORLD"
mkdir -p -- "$DIST_DIR"
cp -- "$BUILT_APWORLD" "$DIST_DIR/spire2.apworld"

printf 'Built %s\n' "$DIST_DIR/spire2.apworld"
