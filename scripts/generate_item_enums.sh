#!/usr/bin/env bash

# Generate the same raw enum proposal as generate_item_enums.ps1.
# Preview is the default because the legacy generator replaces ItemTable.cs
# wholesale and its RawItemID/RawCharacterID shape is not the current client API.

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
OUTPUT_PATH="$REPO_ROOT/client/StS2AP/Data/ItemTable.cs"
PYTHON_BIN="${PYTHON_BIN:-}"
WRITE=false
BACKUP=true
TEMP_OUTPUT=""

usage() {
    cat <<'EOF'
Usage: scripts/generate_item_enums.sh [options]

Generate the legacy RawItemID and RawCharacterID proposal from world/spire2.
By default the script prints a diff and does not modify ItemTable.cs.

Options:
  --write          Replace the output after showing what is being generated.
  --output PATH    Compare/write a different output file.
  --no-backup      Do not create OUTPUT.bak before --write.
  --python PATH    Python executable (default: python3.13, then python3).
  -h, --help       Show this help.

Environment:
  PYTHON_BIN       Same as --python.
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

cleanup() {
    if [[ -n "$TEMP_OUTPUT" && -e "$TEMP_OUTPUT" ]]; then
        rm -f -- "$TEMP_OUTPUT"
    fi
}
trap cleanup EXIT

while (($#)); do
    case "$1" in
        --write)
            WRITE=true
            shift
            ;;
        --output)
            (($# >= 2)) || die "--output requires a path"
            OUTPUT_PATH="$2"
            shift 2
            ;;
        --no-backup)
            BACKUP=false
            shift
            ;;
        --python)
            (($# >= 2)) || die "--python requires an executable path"
            PYTHON_BIN="$2"
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

OUTPUT_DIR="$(dirname -- "$OUTPUT_PATH")"
mkdir -p -- "$OUTPUT_DIR"
OUTPUT_DIR="$(cd -- "$OUTPUT_DIR" && pwd -P)"
OUTPUT_PATH="$OUTPUT_DIR/$(basename -- "$OUTPUT_PATH")"
TEMP_OUTPUT="$(mktemp "$OUTPUT_DIR/.generated-item-enums.XXXXXX")"

"$PYTHON_BIN" - "$REPO_ROOT" "$TEMP_OUTPUT" <<'PY'
from __future__ import annotations

import importlib.util
import re
import sys
import types
import typing
from enum import IntFlag
from pathlib import Path


repo_root = Path(sys.argv[1])
output_path = Path(sys.argv[2])
world_dir = repo_root / "world" / "spire2"


class ItemClassification(IntFlag):
    filler = 0b00000
    progression = 0b00001
    useful = 0b00010
    trap = 0b00100
    skip_balancing = 0b01000
    deprioritized = 0b10000
    progression_deprioritized_skip_balancing = 0b11001
    progression_skip_balancing = 0b01001
    progression_deprioritized = 0b10001


# items.py only needs these two names from BaseClasses. Supplying a narrow
# stand-in keeps this generator usable without an Archipelago installation.
base_classes = types.ModuleType("BaseClasses")
base_classes.ItemClassification = ItemClassification
base_classes.Optional = typing.Optional
sys.modules["BaseClasses"] = base_classes

worlds_package = types.ModuleType("worlds")
worlds_package.__path__ = [str(repo_root / "world")]
sys.modules["worlds"] = worlds_package
spire_package = types.ModuleType("worlds.spire2")
spire_package.__path__ = [str(world_dir)]
sys.modules["worlds.spire2"] = spire_package


def load_module_as(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {name} from {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


characters = load_module_as("worlds.spire2.characters", world_dir / "characters.py")
constants = load_module_as("worlds.spire2.constants", world_dir / "constants.py")
items = load_module_as("worlds.spire2.items", world_dir / "items.py")

character_offset = int(getattr(constants, "CHAR_OFFSET", 10_000))
character_list = list(getattr(characters, "character_list", []))

raw_entries: list[tuple[str, int]] = []
for table_name in ("universal_items", "base_item_table"):
    for item_name, item_data in getattr(items, table_name, {}).items():
        code = getattr(item_data, "code", None)
        if code is not None:
            raw_entries.append((item_name, int(code)))


def sanitize(name: str) -> str:
    result = re.sub(r"[^0-9A-Za-z]", "", name)
    if re.match(r"^[0-9]", result):
        result = "_" + result
    return result or "Item"


seen: set[str] = set()
members: list[tuple[str, int, str]] = []
for original_name, code in raw_entries:
    base_name = sanitize(original_name)
    candidate = base_name
    suffix = 2
    while candidate in seen:
        candidate = f"{base_name}_{suffix}"
        suffix += 1
    seen.add(candidate)
    members.append((candidate, code, original_name))

character_members = [
    (sanitize(character_name), index * character_offset, character_name)
    for index, character_name in enumerate(character_list, start=1)
]

lines = [
    "namespace StS2AP.Data",
    "{",
    "    public enum RawItemID",
    "    {",
]
for member_name, code, original_name in sorted(members, key=lambda value: (value[1], value[0].lower())):
    lines.append(f"        {member_name} = {code}, // {original_name}")
lines.extend([
    "    }",
    "",
    "    public enum RawCharacterID",
    "    {",
])
for member_name, value, original_name in character_members:
    lines.append(f"        {member_name} = {value}, // {original_name}")
lines.extend(["    }", "}", ""])

output_path.write_text("\n".join(lines), encoding="utf-8")
PY

if [[ -f "$OUTPUT_PATH" ]] && cmp -s -- "$TEMP_OUTPUT" "$OUTPUT_PATH"; then
    printf 'Already up to date: %s\n' "$OUTPUT_PATH"
    exit 0
fi

if [[ -f "$OUTPUT_PATH" ]]; then
    diff -u -- "$OUTPUT_PATH" "$TEMP_OUTPUT" || true
else
    cat -- "$TEMP_OUTPUT"
fi

if [[ "$WRITE" != true ]]; then
    printf '\nPreview only; no files changed. Pass --write to replace %s.\n' "$OUTPUT_PATH" >&2
    exit 0
fi

if [[ "$BACKUP" == true && -f "$OUTPUT_PATH" ]]; then
    cp -p -- "$OUTPUT_PATH" "$OUTPUT_PATH.bak"
    printf 'Backed up: %s\n' "$OUTPUT_PATH.bak"
fi
mv -- "$TEMP_OUTPUT" "$OUTPUT_PATH"
TEMP_OUTPUT=""
printf 'Wrote: %s\n' "$OUTPUT_PATH"
