"""Generate many solo Slay the Spire II seeds with random options and count fill failures.

Usage (from the repository root, with the sibling Archipelago 0.6.8 checkout set up):
    .venv/bin/python scripts/fuzz_apworld_generation.py [--seeds N] [--rng-seed N] [--sparse-new-logic]

Each seed follows Archipelago's generation through fill: every generation step, then
distribute_items_restrictive (which also places early items) with the default swap panic
method, then post_fill. A seed fails if fill raises, the goal is unbeatable, or a location is
unreachable. Solo worlds are the hardest case; real multiworlds give fill far more room.

--sparse-new-logic makes every seed floorless with gold and potion checks off, and forces new
logic instead of the old-logic fallback those seeds normally use.
"""
from __future__ import annotations

import argparse
import logging
import random
import sys
import time
from collections import Counter
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY.parent / "Archipelago"))

import Utils  # noqa: E402

Utils.user_path.cached_path = str(REPOSITORY / ".local-tools/data/Archipelago")

from BaseClasses import CollectionState  # noqa: E402
from test.general import setup_multiworld  # noqa: E402
# Load every world before Fill; some worlds import Fill while loading.
from Fill import FillError, distribute_items_restrictive  # noqa: E402
from worlds.AutoWorld import call_all  # noqa: E402
from worlds.spire2 import SlayTheSpire2World  # noqa: E402
from worlds.spire2.characters import character_list  # noqa: E402
import worlds.spire2.world as spire2_world  # noqa: E402

logging.disable(logging.WARNING)


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
    }


def force_new_logic(world: SlayTheSpire2World) -> None:
    world.effective_logic = "new"
    world.logic_fallback_reason = None


def generate(options: dict, seed: int) -> tuple[str, str]:
    multiworld = setup_multiworld(SlayTheSpire2World, seed=seed, options=options)
    world = multiworld.worlds[1]
    mode = world.effective_logic + ("" if options["include_floor_checks"] else " floorless")
    try:
        distribute_items_restrictive(multiworld)
        call_all(multiworld, "post_fill")
        if not multiworld.can_beat_game(CollectionState(multiworld)):
            return mode, "unbeatable"
        if not multiworld.fulfills_accessibility():
            return mode, "inaccessible"
        return mode, "ok"
    except FillError:
        return mode, "fill error"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--seeds", type=int, default=1000)
    parser.add_argument("--rng-seed", type=int, default=2026)
    parser.add_argument("--sparse-new-logic", action="store_true")
    args = parser.parse_args()
    if args.sparse_new_logic:
        spire2_world.select_logic = force_new_logic

    rng = random.Random(args.rng_seed)
    outcomes: Counter[tuple[str, str]] = Counter()
    failures = []
    started = time.time()
    for _ in range(args.seeds):
        options = random_options(rng, args.sparse_new_logic)
        seed = rng.randint(1, 10 ** 9)
        mode, result = generate(options, seed)
        outcomes[mode, result] += 1
        if result != "ok":
            failures.append((seed, result, options))

    failed = sum(count for (_, result), count in outcomes.items() if result != "ok")
    print(f"{args.seeds} seeds in {time.time() - started:.0f}s, {failed} failed")
    for (mode, result), count in sorted(outcomes.items()):
        print(f"  {mode}: {result} x{count}")
    for seed, result, options in failures[:10]:
        enabled = {key: value for key, value in options.items() if value not in (False, 0, [], "")}
        print(f"  {result}, seed {seed}: {enabled}")


if __name__ == "__main__":
    main()
