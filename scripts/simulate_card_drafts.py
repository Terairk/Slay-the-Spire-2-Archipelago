"""Toy model of how card strength grows with the number of card rewards a run can draft.

Usage: python scripts/simulate_card_drafts.py [--trials N] [--json]

It compares two ways of receiving the same rewards:

- One at a time, as in a vanilla run or when AP items arrive during a run. The first few picks
  are forced: the run needs frontload to survive, so it takes the best attack-like card.
- Stockpiled, as when a run starts with AP Card Rewards already received. Every reward's three
  cards can be compared before choosing, and nothing is forced.

Each card has a quality, an archetype and a frontload flag. Scaling (non-frontload) cards are
worth more over a whole run. A lean deck keeps at most --keep picks. An archetype adds
--archetype-bonus once the deck holds --online of its cards, scaling up linearly before that.
Results are normalised so the first one-at-a-time pick is worth 1 card strength, matching the
first step of logic_table.CARD_REWARD_STRENGTH.

This shows the shape of the effect under stated assumptions; it is not calibrated against real
StS2 card values. Change the assumptions with the command-line options.
"""
from __future__ import annotations

import argparse
import json
import random
from dataclasses import asdict, dataclass

# Regular card reward rarity odds and rough relative card quality by rarity.
RARITIES = ((0.615, 1.0), (0.355, 1.4), (0.03, 2.0))
SCALING_CARD_MULTIPLIER = 1.2
FRONTLOAD_SHARE = 0.5


@dataclass(frozen=True)
class Card:
    quality: float
    archetype: int
    frontload: bool


@dataclass(frozen=True)
class Assumptions:
    keep: int = 10
    forced: int = 4
    archetypes: int = 3
    online: int = 4
    archetype_bonus: float = 3.0


def random_card(rng: random.Random, archetypes: int) -> Card:
    roll, base = rng.random(), RARITIES[-1][1]
    for odds, rarity_base in RARITIES:
        if roll < odds:
            base = rarity_base
            break
        roll -= odds
    frontload = rng.random() < FRONTLOAD_SHARE
    quality = base * rng.uniform(0.7, 1.3) * (1 if frontload else SCALING_CARD_MULTIPLIER)
    return Card(quality, rng.randrange(archetypes), frontload)


def deck_value(cards: list[Card], assumptions: Assumptions, bonus: float) -> float:
    counts = [0] * assumptions.archetypes
    for card in cards:
        counts[card.archetype] += 1
    return sum(card.quality for card in cards) + bonus * min(1.0, max(counts, default=0) / assumptions.online)


def one_at_a_time(rewards: list[list[Card]], assumptions: Assumptions, bonus: float) -> list[float]:
    """Deck value after each reward, choosing from one reward at a time."""
    kept: list[Card] = []
    counts = [0] * assumptions.archetypes
    values = [0.0]
    for index, reward in enumerate(rewards):
        if len(kept) < assumptions.keep:
            if index < assumptions.forced:
                frontload = [card for card in reward if card.frontload]
                card = max(frontload or reward, key=lambda c: c.quality)
            else:
                target = max(range(assumptions.archetypes), key=lambda a: counts[a])
                step = bonus / assumptions.online if counts[target] < assumptions.online else 0

                def score(c: Card) -> float:
                    return c.quality + (step if c.archetype == target else 0)

                card = max(reward, key=score)
            kept.append(card)
            counts[card.archetype] += 1
        values.append(deck_value(kept, assumptions, bonus))
    return values


def stockpiled(rewards: list[list[Card]], assumptions: Assumptions, bonus: float) -> float:
    """Best deck value when every reward can be compared before choosing one card from each."""
    best_any = [max(card.quality for card in reward) for reward in rewards]
    best = sum(sorted(best_any, reverse=True)[:assumptions.keep])
    for archetype in range(assumptions.archetypes):
        on_archetype = {index: max(card.quality for card in reward if card.archetype == archetype)
                        for index, reward in enumerate(rewards)
                        if any(card.archetype == archetype for card in reward)}
        ranked = sorted(on_archetype, key=on_archetype.get, reverse=True)
        for taken in range(1, min(assumptions.online, len(ranked), assumptions.keep) + 1):
            chosen = set(ranked[:taken])
            rest = sorted((best_any[index] for index in range(len(rewards)) if index not in chosen), reverse=True)
            value = (sum(on_archetype[index] for index in chosen) + sum(rest[:assumptions.keep - taken])
                     + bonus * taken / assumptions.online)
            best = max(best, value)
    return best


def simulate(trials: int, max_rewards: int, assumptions: Assumptions, seed: int) -> dict[str, list[float]]:
    rng = random.Random(seed)
    names = ("one at a time", "stockpiled", "one at a time + archetypes", "stockpiled + archetypes")
    totals = {name: [0.0] * (max_rewards + 1) for name in names}
    for _ in range(trials):
        rewards = [[random_card(rng, assumptions.archetypes) for _ in range(3)] for _ in range(max_rewards)]
        for suffix, bonus in (("", 0.0), (" + archetypes", assumptions.archetype_bonus)):
            for count, value in enumerate(one_at_a_time(rewards, assumptions, bonus)):
                totals["one at a time" + suffix][count] += value
            for count in range(1, max_rewards + 1):
                totals["stockpiled" + suffix][count] += stockpiled(rewards[:count], assumptions, bonus)
    unit = totals["one at a time"][1]
    return {name: [round(value / unit, 2) for value in values] for name, values in totals.items()}


def main() -> None:
    defaults = Assumptions()
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--trials", type=int, default=4000)
    parser.add_argument("--max-rewards", type=int, default=20)
    parser.add_argument("--keep", type=int, default=defaults.keep, help="most picks a lean deck keeps")
    parser.add_argument("--forced", type=int, default=defaults.forced, help="early one-at-a-time picks taken to survive")
    parser.add_argument("--archetypes", type=int, default=defaults.archetypes)
    parser.add_argument("--online", type=int, default=defaults.online, help="cards an archetype needs to come online")
    parser.add_argument("--archetype-bonus", type=float, default=defaults.archetype_bonus,
                        help="value of a working archetype, in average cards")
    parser.add_argument("--seed", type=int, default=2026)
    parser.add_argument("--json", action="store_true", help="print the curves as JSON")
    args = parser.parse_args()
    assumptions = Assumptions(args.keep, args.forced, args.archetypes, args.online, args.archetype_bonus)
    curves = simulate(args.trials, args.max_rewards, assumptions, args.seed)
    if args.json:
        print(json.dumps({"assumptions": asdict(assumptions), "curves": curves}))
        return
    names = list(curves)
    print("Rewards  " + "  ".join(f"{name:>26}" for name in names))
    for count in range(args.max_rewards + 1):
        print(f"{count:7}  " + "  ".join(f"{curves[name][count]:26.2f}" for name in names))


if __name__ == "__main__":
    main()
