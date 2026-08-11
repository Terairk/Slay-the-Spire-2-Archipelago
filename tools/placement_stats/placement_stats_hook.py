"""Archipelago-fuzzer hook that writes one placement CSV shard per generation.

Put this directory on PYTHONPATH and load the hook with:

    --hook placement_stats_hook:Hook

The hook deliberately has no third-party dependencies.  Each successful
generation is written to a unique temporary file and atomically renamed, so
multiple fuzzer worker processes never append to the same file.
"""

from __future__ import annotations

import csv
import os
import re
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping

from fuzz import BaseHook


SCHEMA_VERSION = 1
OUTPUT_DIR_ENV = "AP_PLACEMENT_STATS_DIR"
DATASET_LABEL_ENV = "AP_PLACEMENT_STATS_LABEL"


@dataclass(frozen=True)
class PlacementContext:
    """Values available to optional placement-specific custom columns."""

    multiworld: Any
    location: Any
    item: Any
    sphere: int | None
    reachable: bool


# Add cheap, placement-level metadata here when needed.  For example:
#
# CUSTOM_COLUMNS = {
#     "act": lambda context: context.location.parent_region.name.split("Act ", 1)[-1][:1],
# }
#
# Seed-level metrics such as "first Progressive Relic sphere" are better
# calculated from the raw rows in analyze.py so generation remains cheap.
CUSTOM_COLUMNS: Mapping[str, Callable[[PlacementContext], Any]] = {}


BASE_COLUMNS = (
    "schema_version",
    "dataset_label",
    "generation_id",
    "seed",
    "seed_name",
    "archipelago_version",
    "player_count",
    "item_name",
    "item_player",
    "item_player_name",
    "item_game",
    "item_code",
    "item_classification",
    "item_is_progression",
    "item_is_useful",
    "item_is_trap",
    "location_name",
    "location_player",
    "location_player_name",
    "location_game",
    "location_address",
    "location_is_event",
    "location_locked",
    "region",
    "sphere",
    "reachable",
)


def _archipelago_version() -> str:
    try:
        from Utils import __version__

        return str(__version__)
    except (ImportError, AttributeError):
        return "unknown"


def _sphere_lookup(multiworld: Any) -> dict[Any, tuple[int | None, bool]]:
    """Map filled locations to Archipelago's 1-based logical spheres.

    MultiWorld.get_spheres() yields an empty set as a sentinel before a final
    set of unreachable locations.  Those locations get a blank sphere and
    reachable=false rather than a misleading numeric sphere.
    """

    result: dict[Any, tuple[int | None, bool]] = {}
    sphere_number = 1
    unreachable_follows = False

    for sphere in multiworld.get_spheres():
        if not sphere:
            unreachable_follows = True
            continue

        if unreachable_follows:
            result.update((location, (None, False)) for location in sphere)
            unreachable_follows = False
        else:
            result.update((location, (sphere_number, True)) for location in sphere)
            sphere_number += 1

    filled_locations = set(multiworld.get_filled_locations())
    missing = filled_locations.difference(result)
    if missing:
        names = sorted(f"P{location.player}:{location.name}" for location in missing)
        raise RuntimeError(f"get_spheres() omitted filled locations: {names}")

    return result


def _player_name(multiworld: Any, player: int) -> str:
    try:
        return str(multiworld.get_player_name(player))
    except (AttributeError, KeyError):
        return str(player)


def _placement_rows(
    multiworld: Any,
    generation_id: str,
    dataset_label: str,
) -> Iterable[dict[str, Any]]:
    sphere_by_location = _sphere_lookup(multiworld)
    archipelago_version = _archipelago_version()
    player_names = {
        player: _player_name(multiworld, player)
        for player in multiworld.get_all_ids()
    }
    locations = sorted(
        multiworld.get_filled_locations(),
        key=lambda location: (
            location.player,
            location.name,
            location.item.player,
            location.item.name,
        ),
    )

    for location in locations:
        item = location.item
        sphere, reachable = sphere_by_location[location]
        region = location.parent_region.name if location.parent_region else ""
        context = PlacementContext(multiworld, location, item, sphere, reachable)

        row: dict[str, Any] = {
            "schema_version": SCHEMA_VERSION,
            "dataset_label": dataset_label,
            "generation_id": generation_id,
            "seed": multiworld.seed,
            "seed_name": multiworld.seed_name,
            "archipelago_version": archipelago_version,
            "player_count": multiworld.players,
            "item_name": item.name,
            "item_player": item.player,
            "item_player_name": player_names.get(item.player, str(item.player)),
            "item_game": item.game,
            "item_code": item.code,
            "item_classification": int(item.classification),
            "item_is_progression": item.advancement,
            "item_is_useful": item.useful,
            "item_is_trap": item.trap,
            "location_name": location.name,
            "location_player": location.player,
            "location_player_name": player_names.get(location.player, str(location.player)),
            "location_game": location.game,
            "location_address": location.address,
            "location_is_event": location.is_event,
            "location_locked": location.locked,
            "region": region,
            "sphere": sphere,
            "reachable": reachable,
        }
        row.update((name, extractor(context)) for name, extractor in CUSTOM_COLUMNS.items())
        yield row


def _safe_filename_component(value: object) -> str:
    component = re.sub(r"[^A-Za-z0-9_.-]+", "-", str(value)).strip("-.")
    return component[:80] or "seed"


class Hook(BaseHook):
    """Write successful MultiWorld placements without sharing output files."""

    def __init__(self) -> None:
        configured = os.environ.get(OUTPUT_DIR_ENV)
        self.output_dir = Path(configured) if configured else Path("fuzz_output/placement_stats")
        self.runs_dir = self.output_dir / "runs"
        self.dataset_label = os.environ.get(DATASET_LABEL_ENV, "default")

    def before_generate(self, generator_args: Any) -> None:
        # Archipelago 0.6.8 added this Generate.main Namespace field after the
        # pinned fuzzer's call_generate() argument list was last updated. The
        # fixed experiment YAML does not use quantity, so supply the normal
        # disabled value only when the fuzzer has not learned the field yet.
        if not hasattr(generator_args, "allow_quantity"):
            generator_args.allow_quantity = False

    def setup_main(self, _args: Any) -> None:
        self.runs_dir.mkdir(parents=True, exist_ok=True)

    def setup_worker(self, _args: Any) -> None:
        self.runs_dir.mkdir(parents=True, exist_ok=True)

    def after_generate(self, multiworld: Any, _output_dir: str) -> None:
        if multiworld is None:
            return

        unique_part = uuid.uuid4().hex
        generation_id = f"{multiworld.seed_name}-{unique_part}"
        basename = (
            f"{_safe_filename_component(multiworld.seed_name)}-"
            f"{os.getpid()}-{unique_part}"
        )
        final_path = self.runs_dir / f"{basename}.csv"
        temporary_path = self.runs_dir / f".{basename}.tmp"
        fieldnames = [*BASE_COLUMNS, *CUSTOM_COLUMNS]

        try:
            with temporary_path.open("x", encoding="utf-8", newline="") as output:
                writer = csv.DictWriter(output, fieldnames=fieldnames, extrasaction="raise")
                writer.writeheader()
                writer.writerows(
                    _placement_rows(multiworld, generation_id, self.dataset_label)
                )
                output.flush()

            os.replace(temporary_path, final_path)
        except BaseException:
            temporary_path.unlink(missing_ok=True)
            raise
