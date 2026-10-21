"""Generate many solo Slay the Spire II seeds with random options and count fill failures.

Usage (from the repository root, with the sibling Archipelago 0.6.8 checkout set up):
    .venv/bin/python scripts/fuzz_apworld_generation.py [--seeds N] [--rng-seed N]
        [--sparse | --sparse-new-logic | --option-set NAME ...] [--partner apquest|spire]
        [--spheres]

Each seed follows Archipelago's generation through fill: every generation step, then
distribute_items_restrictive (which also places early items) with the default swap panic
method, then post_fill. A seed fails if fill raises, the goal is unbeatable, or a location is
unreachable. Solo worlds are the hardest case; real multiworlds give fill far more room.

--sparse makes every seed floorless with gold and potion checks off. Alone, such a world falls
back to old logic; --sparse-new-logic forces new logic instead. --partner adds a second player:
APQuest with default options, or another Slay the Spire II world with random normal options.

--option-set replaces random options with a named set from OPTION_SETS. Each one generates
every seed, so the sets, and runs that share --rng-seed, compare on the same seeds.

--spheres reports the sphere in which each region opens, over successful seeds, grouped by
option set (or by logic mode with random options). Sphere 1 is what the starting items reach;
sphere N needs items from the checks of spheres 1 to N-1. It also reports the share of the
world's own progression items found in earlier spheres, which follows power more closely.
"""
from __future__ import annotations

import argparse
import logging
import random
import sys
import time
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY.parent / "Archipelago"))

import Utils  # noqa: E402

Utils.user_path.cached_path = str(REPOSITORY / ".local-tools/data/Archipelago")

from BaseClasses import CollectionState, MultiWorld  # noqa: E402
from test.general import setup_multiworld  # noqa: E402
# Load every world before Fill; some worlds import Fill while loading.
from Fill import FillError, distribute_items_restrictive  # noqa: E402
from worlds.AutoWorld import World, call_all  # noqa: E402
from worlds.spire2 import SlayTheSpire2World  # noqa: E402
from worlds.spire2.characters import character_list  # noqa: E402
from worlds.spire2.logic_table import REGIONS  # noqa: E402
import worlds.spire2.world as spire2_world  # noqa: E402
from worlds.apquest import APQuestWorld  # noqa: E402

logging.disable(logging.WARNING)

# Fixed option sets for --option-set: one Ironclad world, otherwise the options' defaults
# (floor checks, half shuffle, Anytime Ancients, no other sanities).
OPTION_SETS: dict[str, dict] = {
    "default": {},
    "all-cards": {"shuffle_all_cards": True},
    "campfire-shop": {"campfire_sanity": True, "shop_sanity": True},
    "everything": {"shuffle_all_cards": True, "campfire_sanity": True, "shop_sanity": True,
                   "shop_remove_slots": True, "neow_sanity": True, "gold_sanity": True,
                   "potion_sanity": True, "progressive_starter_card": True,
                   "progressive_starter_relic": True},
    "floorless": {"include_floor_checks": False, "gold_sanity": True, "potion_sanity": True},
}


def random_options(rng: random.Random, sparse: bool) -> dict:
    characters = rng.sample(character_list, rng.randint(0, 3))
    modded = ["TestCharacter"] if not characters or rng.random() < 0.25 else []
    floors = not sparse and rng.random() < 0.6
    return {
        "characters": characters,
        "modded_characters": modded,
        "lock_characters": rng.choice((0, 1)),
        "seeded": rng.random() < 0.3,
        "shuffle_all_cards": rng.random() < 0.5,
        "campfire_sanity": rng.random() < 0.5,
        "shop_sanity": rng.random() < 0.5,
        "shop_card_slots": rng.randint(0, 5),
        "shop_neutral_card_slots": rng.randint(0, 2),
        "shop_relic_slots": rng.randint(0, 3),
        "shop_potion_slots": rng.randint(0, 3),
        "shop_remove_slots": rng.random() < 0.5,
        # Shop prices set how much Elite and Boss Gold each shop needs with Gold Sanity.
        "shop_sanity_costs": rng.randrange(4),
        "neow_sanity": rng.random() < 0.5,
        "ancient_relic_location": rng.choice(("start_of_act", "anytime")),
        "progressive_starter_card": rng.random() < 0.5,
        "progressive_starter_relic": rng.random() < 0.5,
        "include_floor_checks": floors,
        # Floorless seeds with gold or potion checks are the sparse cases new logic still handles.
        "gold_sanity": not sparse and rng.random() < (0.5 if floors else 0.7),
        "potion_sanity": not sparse and rng.random() < (0.3 if floors else 0.5),
        # Logic counts only the relics a run can claim, which low anytime counts limit most.
        "relic_rewards_available_anytime": rng.choice((0, 1, 2, 3, 5, 10)),
    }


def force_new_logic(world: SlayTheSpire2World) -> None:
    world.effective_logic = "new"
    world.logic_fallback_reason = None


@dataclass(frozen=True)
class Opening:
    sphere: int
    # Share of the player's own progression items found in earlier spheres.
    progression_before: float


@dataclass
class Generation:
    mode: str
    result: str
    # When each of the first player's regions opens, by region name without the character.
    openings: dict[str, list[Opening]] = field(default_factory=dict)
    last_sphere: int = 0


def region_openings(multiworld: MultiWorld, player: int) -> tuple[dict[str, list[Opening]], int]:
    """When each of a player's regions opens, in map order, and the last sphere with any checks.

    Event locations are collected as soon as they are reachable, so they never form a sphere. A
    region with only events, such as a floorless Act 3 Boss Arena, may open one sphere after the
    last checks.
    """
    world = multiworld.worlds[player]
    names = {}
    for region in multiworld.get_regions(player):
        if region.name == world.origin_region_name:
            continue
        prefix = next((config.name for config in world.characters if region.name.startswith(f"{config.name} ")), None)
        names[region] = region.name[len(prefix) + 1:] if prefix else region.name
    locations = set(multiworld.get_filled_locations())
    events = {location for location in locations if location.address is None}
    locations -= events
    def own_progression(location) -> bool:
        return location.item.player == player and location.item.advancement

    total_progression = max(1, sum(map(own_progression, locations)))
    found = 0
    state = CollectionState(multiworld)
    # Characters share region names; the first character's creation order gives the map order.
    openings: dict[str, list[Opening]] = {name: [] for name in names.values()}
    closed = set(names)
    sphere = 0
    while True:
        sphere += 1
        collected_event = True
        while collected_event:
            collected_event = False
            for event in [event for event in events if event.can_reach(state)]:
                state.collect(event.item, True, event)
                events.remove(event)
                collected_event = True
        for region in [region for region in closed if region.can_reach(state)]:
            openings[names[region]].append(Opening(sphere, found / total_progression))
            closed.remove(region)
        reachable = {location for location in locations if location.can_reach(state)}
        if not reachable:
            return openings, sphere - 1
        for location in reachable:
            state.collect(location.item, True, location)
            found += own_progression(location)
        locations -= reachable


def generate(options: dict, seed: int, partner: tuple[type[World], dict] | None = None,
             spheres: bool = False) -> Generation:
    worlds: list[type[World]] = [SlayTheSpire2World]
    all_options = [options]
    if partner is not None:
        worlds.append(partner[0])
        all_options.append(partner[1])
    multiworld = setup_multiworld(worlds, seed=seed, options=all_options)
    world = multiworld.worlds[1]
    mode = world.effective_logic + ("" if world.options.include_floor_checks else " floorless")
    try:
        distribute_items_restrictive(multiworld)
        call_all(multiworld, "post_fill")
        if not multiworld.can_beat_game(CollectionState(multiworld)):
            return Generation(mode, "unbeatable")
        if not multiworld.fulfills_accessibility():
            return Generation(mode, "inaccessible")
    except FillError:
        return Generation(mode, "fill error")
    generation = Generation(mode, "ok")
    if spheres:
        generation.openings, generation.last_sphere = region_openings(multiworld, 1)
    return generation


@dataclass
class FuzzResults:
    seeds: int
    seconds: float
    outcomes: Counter[tuple[str, str]]
    failures: list[tuple[str, int, str, dict]]
    # Region openings by group (option set, or logic mode with random options), then region.
    openings: dict[str, dict[str, list[Opening]]]
    last_spheres: dict[str, list[int]]

    @property
    def failed(self) -> int:
        return sum(count for (_, result), count in self.outcomes.items() if result != "ok")


def fuzz(seeds: int, rng_seed: int, sparse: bool = False, sparse_new_logic: bool = False,
         partner: str | None = None, option_sets: list[str] | None = None, spheres: bool = False) -> FuzzResults:
    """Generate the seeds and collect outcomes. Callers may patch logic_table first to compare variants."""
    original_select_logic = spire2_world.select_logic
    if sparse_new_logic:
        spire2_world.select_logic = force_new_logic
    rng = random.Random(rng_seed)
    outcomes: Counter[tuple[str, str]] = Counter()
    failures = []
    openings: dict[str, dict[str, list[Opening]]] = defaultdict(lambda: defaultdict(list))
    last_spheres: dict[str, list[int]] = defaultdict(list)
    started = time.time()
    try:
        for _ in range(seeds):
            if option_sets:
                drawn = [(name, OPTION_SETS[name]) for name in option_sets]
            else:
                drawn = [(None, random_options(rng, sparse or sparse_new_logic))]
            partner_world = None
            if partner == "apquest":
                partner_world = APQuestWorld, {}
            elif partner == "spire":
                partner_world = SlayTheSpire2World, random_options(rng, False)
            seed = rng.randint(1, 10 ** 9)
            for name, options in drawn:
                generation = generate(options, seed, partner_world, spheres)
                group = name or generation.mode
                outcomes[group, generation.result] += 1
                if generation.result != "ok":
                    failures.append((group, seed, generation.result, options))
                    continue
                for region, region_openings_ in generation.openings.items():
                    openings[group][region] += region_openings_
                if spheres:
                    last_spheres[group].append(generation.last_sphere)
    finally:
        spire2_world.select_logic = original_select_logic
    return FuzzResults(seeds, time.time() - started, outcomes, failures, openings, last_spheres)


def percentile(sorted_values: list[float], share: float) -> float:
    return sorted_values[min(len(sorted_values) - 1, int(share * len(sorted_values)))]


def summarize(values: list[float], number_format: str) -> str:
    """Mean with the 10th to 90th percentile range."""
    if not values:
        return "-"
    ordered = sorted(values)
    low, high = (format(percentile(ordered, share), number_format) for share in (0.1, 0.9))
    spread = low if low == high else f"{low}-{high}"
    return f"{format(sum(values) / len(values), number_format.replace('.0', '.1'))} ({spread})"


def format_columns(rows: list[tuple[str, ...]]) -> list[str]:
    widths = [max(len(row[column]) for row in rows) for column in range(len(rows[0]))]
    lines = []
    for row in rows:
        cells = [row[0].ljust(widths[0])] + [cell.rjust(width) for cell, width in zip(row[1:], widths[1:])]
        lines.append("  " + "  ".join(cells).rstrip())
    return lines


def format_sphere_report(results: FuzzResults) -> list[str]:
    groups = list(results.openings)
    regions: list[str] = []
    for group in groups:
        regions += [region for region in results.openings[group] if region not in regions]

    def table(value, number_format: str, last_row: tuple[str, ...] | None = None) -> list[str]:
        rows = [("Region", "Power", *groups)]
        for region in regions:
            check = REGIONS.get(region)
            rows.append((region, f"{check.power:g}" if check else "",
                         *(summarize([value(opening) for opening in results.openings[group].get(region, [])],
                                     number_format) for group in groups)))
        if last_row:
            rows.append(last_row)
        return format_columns(rows)

    last_sphere = ("Last sphere", "", *(summarize(results.last_spheres[group], ".0f") for group in groups))
    return [
        "Sphere each region opens in: mean (10th-90th percentile) over successful seeds",
        *table(lambda opening: opening.sphere, ".0f", last_sphere),
        "",
        "Share of the world's progression items found before each region opens (%)",
        *table(lambda opening: 100 * opening.progression_before, ".0f"),
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--seeds", type=int, default=1000)
    parser.add_argument("--rng-seed", type=int, default=2026)
    options = parser.add_mutually_exclusive_group()
    options.add_argument("--sparse", action="store_true")
    options.add_argument("--sparse-new-logic", action="store_true")
    options.add_argument("--option-set", action="append", choices=OPTION_SETS, dest="option_sets")
    parser.add_argument("--partner", choices=("apquest", "spire"))
    parser.add_argument("--spheres", action="store_true")
    args = parser.parse_args()

    results = fuzz(args.seeds, args.rng_seed, args.sparse, args.sparse_new_logic, args.partner,
                   args.option_sets, args.spheres)
    generations = sum(results.outcomes.values())
    print(f"{generations} generations in {results.seconds:.0f}s, {results.failed} failed")
    for (group, result), count in sorted(results.outcomes.items()):
        print(f"  {group}: {result} x{count}")
    for group, seed, result, options in results.failures[:10]:
        enabled = {key: value for key, value in options.items() if value not in (False, 0, [], "")}
        print(f"  {group} {result}, seed {seed}: {enabled}")
    if args.spheres:
        print()
        print("\n".join(format_sphere_report(results)))


if __name__ == "__main__":
    main()
