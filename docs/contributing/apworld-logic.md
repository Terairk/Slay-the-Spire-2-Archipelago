# APWorld power logic

How the Slay the Spire II APWorld decides what is in logic, the game facts it relies on,
the decisions behind it, and ideas that were considered but not built. Update this file when
the logic changes.

## How it works

Each character has its own chain of regions. A region entrance asks whether that character's
run, starting with every AP item received so far, is strong enough to get there:

    card strength >= the region's minimum card strength
    power         >= the region's base power + the value of any support the run is missing

- **Card strength** counts AP Card Rewards (through a per-count table), the vanilla card
  rewards that half shuffle leaves in a run, rare card rewards and an upgraded starter card.
- **Power** is card strength plus the relics a run can claim by then and an upgraded starter
  relic.
- **Missing support** is what a normal run has that AP items have not restored yet: starting
  card and relic, rests, smiths, card removals, Ancients and shop choices. Its value raises the
  requirement, so extra power can make up for it. Unshuffled support is never missing.

Logic assumes a player restarts with everything they hold, so each gate describes a fresh run
with the current items, not the run in progress.

Every number lives in `world/spire2/logic_table.py`. Starter values are reviewed per character
in `world/spire2/characters.py`. `python3 scripts/print_logic_table.py` prints the tables, and
Universal Tracker's explanation lists each contribution to a check.

### Two tracks per act

- **Path track:** the Early, Mid, Late and Boss regions. They hold every check a run can reach
  while walking around elites: floors, card rewards, gold, potions, campfires and the Act 1
  chest relic.
- **Elite track:** Act 1/2/3 Elites regions, entered from each act's Mid region. Acts 2 and 3
  also need the previous act's Elites region, so relic counts add up across a run.

Relic N is the run's Nth elite, chest or Black Star reward. Relic 1 is the Act 1 chest on the
path. Relics 2-3, 4-6 and 7-10 are in the Act 1, 2 and 3 Elites regions, assuming a run fights
two elites in Acts 1 and 2 and three in Act 3.

Mid and Late Act 1 need 2 and 7 power. With Seeded Run they keep the elite-inclusive 4 and 10:
every run replays one map, which may force an elite. Old logic keeps its original relic
placement and has no Elites regions.

### Claimable relics

A run claims AP Relics past Relic Rewards Available Anytime only after elite, chest or Black Star
rewards, one claim per reward, so a power check counts at most anytime + the relic rewards taken
by then. Walking around elites, those are the chests passed, including the one on each Mid
region's fixed row. When an earlier Elites region is reachable, a region may count its rewards
too: 3 after the Act 1 elites, 6 after Act 2's and 10 after Act 3's, plus later chests. Each
entrance is an either/or of up to four power checks (`region_power` in `rules_new.py`), and
`python3 scripts/print_logic_table.py` prints the counts. The early items of sparse openings
make up any relics Mid Act 1 cannot count with Card Rewards.

### Sparse worlds

A world with floor, gold and potion checks all off has about one location per item, and almost
every item is progression. Generated alone, such a world falls back to old logic. Beside other
players it uses new logic, and its `fill_hook` reorders its own progression so fill places Card
Rewards first and Rests, Ancients, Smiths and Unlocks last (`sparse_placement_rank` in
`world.py`).

### Shop gold

With Shop and Gold Sanity, each act's shop entrance asks for Elite and Boss Gold (30 and 75,
Poverty-safe). A run needs enough gold for the dearest checks that shop can list: two per visit,
or one with Tiered prices. A run already has 99 starting gold plus 31 per earlier act's chest.
Gold needed in Acts 1 / 2 / 3 (`python3 scripts/print_logic_table.py` prints this):

| Cost mode | Default page 2/1/2/2 | Full page 5/2/3/3 + removals |
|---|---|---|
| Fixed, Super Discount | none | none |
| Discount | 131 / 100 / 69 | 59 / 100 / 69 |
| Tiered | 131 / 100 / 69 | 59 / 100 / 69 |

Elite and Boss Gold are `useful`, not progression, whenever no shop needs gold. Without Gold
Sanity, vanilla gold rewards pay for shops, and logic does not count them.

## Game facts the logic relies on

From the 0.111 beta decompile unless stated otherwise.

- Acts have 15, 14 and 13 rooms; with the Ancient and boss that is 17, 16 and 15 floors.
  Multiplayer maps have one room fewer per act.
- Map rows 1-5 cannot hold elites or rest sites (`StandardActMap`), so elites start at floors
  7, 24 and 40. Row 1 is always a monster.
- Each act has one treasure chest on a fixed row seven rows before the boss: floors 10, 26 and
  41. Chests give 42-52 gold as well as a relic.
- ? rooms never become elites; their elite odds are disabled (`UnknownMapPointOdds`).
- An act map has 5 elites (8 with Swarming Elites), but one path usually reaches about 3, and
  4 when lucky. 5 or 6 needs Winged Boots from Neow. (Play experience.)
- Acts 1, 2 and 3 open with 3, 2 and 2 weak hallway encounters.
- Characters start with 99 gold. Poverty multiplies combat and chest gold by 0.75.
- Shop prices: cards 50 / 75 / 150 by rarity (colorless x1.15), relics 175 / 225 / 275
  (shop relics 200), potions 50 / 75 / 100. Card removal costs 75, rising by 25 each time;
  Inflation makes that 100, rising by 50.
- The client counts the first ten eligible elite, chest and Black Star rewards in a run as
  Relic 1-10. Every received AP Card Reward is claimable from the AP menu from the first floor,
  and its three cards stay fixed for the run.
- Each shop visit fills its AP page from the unchecked Shop Slot checks up to the act's ceiling
  (5, 10 or 16 slots). Checks bought in earlier runs are skipped.
- The AP page fills positions in slot order: character cards (up to 5), neutral cards (2),
  relics (3), then potions (3), up to the configured counts. The three card-removal checks take
  otherwise unused positions in the same order (`GetApSlotCounts` in `Patches_ShopSanity.cs`).
- An AP check is priced by its position: cards by the placed item's classification (rare 150 for
  progression; the AP card pool is not colorless), relics as Shop rarity (200) and potions as
  common (50), with the usual random range of cards and potions ±5% and relics ±15%. That makes
  the dearest Tiered checks about 158, 230 and 53. Discount and Super Discount take 50% and 20%;
  Fixed charges 15. Main 0.107.1 prices the same way.
- Chest gold comes from `DoLocalTreasureRoomRewards`, which Gold Sanity does not replace. Gold
  Sanity replaces the first 20 monster and elite gold rewards and both boss gold rewards. AP
  gold items pay 15 (Combat), 40 (Elite) and 100 (Boss), claimable in every run.

## Decisions

- **Ascension is not part of logic.** Players choose ascension to match their skill, so
  requirements stay the same at every ascension and Ascension Down items are not progression.
- **Generation reliability is measured with `scripts/fuzz_apworld_generation.py`.** Normal
  options: no failures in 5000 random solo seeds after each change, until the fuzz began drawing
  Shop Sanity Costs. Its new seed sequence then found 1 in 5000: a floorless solo seed with only
  potion checks, which fails the same way before the shop gold change. Forcing new logic on fully
  sparse solo seeds (`--sparse-new-logic`) failed about 16% before two tracks, 12% after, 15-16%
  once vanilla card rewards counted 0.85, and 8.5% with the sparse placement order. Once the
  fuzz also drew Relic Rewards Available Anytime, its new seed sequence gave 0 failures in 5000
  normal seeds, 0-1 of 3000 for each partner run (`--sparse --partner apquest` and `--sparse
  --partner spire` with `--rng-seed 31`, `--partner spire` with 32), and 7.1% forced sparse with
  the tuning and claimable relics below. Multiworlds give fill much more room than these solo
  seeds.
- **Sparse worlds fall back to old logic only when generated alone.** In a solo sparse world,
  every location behind a gate must hold an item that the gate can do without. New logic's late
  gates leave about half the slack of old logic's (Act 3 Boss 30 of about 35 power with all
  card rewards shuffled, against 27 of 38), and a missing Rest or Smith costs 3-4 power, so
  random fill often strands items behind the gates that need them. Measured on 5000 forced
  sparse solo seeds (`--rng-seed 31`): 14.5% failed as before, 8.5% with the placement order,
  and 2.2% if five Card Rewards per character were also `useful`. Most of what is left needs
  Campfire Sanity with Start of Act Ancients, where Act 1's 11 or so locations must hold
  nearly everything the Act 1 boss and Early Act 2 need. Lowering the late gates by 2-3 power
  helped less (11-10%), and an early Smith made it worse. Beside one other player (APQuest,
  another sparse world or a normal world), new logic with the placement order failed 1-2 in
  3000, about as often as normal options. Choosing logic by player count follows other worlds'
  use of `multiworld.players`; Mega Man 3's `fill_hook`, for example, only acts on solo
  generations. The spoiler header records the logic each world used.
- **The placement order works because fill places items in reverse.** Archipelago's fill takes
  each player's progression from the end of the shuffled pool and puts it in the first reachable
  empty location, reachable with every item still unplaced. Items placed early can land behind
  any gate; items placed late only reach early spheres. The reverse order failed 20.6%.
- **Shop gold asks for the dearest listed checks, not a whole shop.** Logic assumes restarts, so
  one affordable check per run would be enough to be in logic. That can mean five or six runs
  to clear one act's shop with Discount prices, which feels bad. Two checks per visit cut that to
  three. Tiered stays at one per visit: two would need almost every gold item (217-360 in Act 1),
  and with vanilla prices a shop takes several runs to clear anyway. Chest gold counts because
  every path passes the chest; event gold and Combat Gold items do not count. Over 5000 Shop and
  Gold Sanity seeds with random cost modes, generation had no failures.
- **The climb from Act 2 to Act 3 is split between the boss and Early Act 3.** The user chose
  (2026-10-21) to smooth the largest step by the pool's power: Late Act 2 (18) to the Act 2
  boss (23.5, card strength 5) asked for 17-18% more of the full pool, and Early Act 3 for no
  more power. Early Act 3 still opened 0.4-0.7 sphere after the boss, because it asks for the
  third Ancient (4.5 power while missing with Anytime, an item gate with Start of Act), so
  lowering only the boss moves the wall there. The boss now needs 22 and card strength 4, and
  Early Act 3 23. On the same seeds, progression found per step (default set) went from +10.4
  and +6.9 to +7.8 and +8.8, and runs ending at the Act 2 boss or in Early Act 3 from 1.27 to
  1.15 per solo world. Alternatives measured: boss 22.5 (+9.1 / +8.3) changed less, boss 21.5
  (+7.4 / +10.1) moved the wall onto Early Act 3, and raising Late Act 2 enlarged Mid to Late
  Act 2, already one of the largest steps.
- **Logic counts only claimable relics** (user's choice, 2026-10-21). Before, a power check
  counted every received relic. At the moment each region opened, a check limited to claimable
  relics would have failed Early Act 2 in 5-20% of seeds and Early Act 3 in 9-18% at the default
  anytime count of 2, and 39-65% of both at 0. Counting only the chests on the path cannot reach
  the Act 3 boss with a low anytime count (69-85% of random seeds failed at anytime 0-3),
  because boss checks assume elite relics; hence the either/or with Elites regions. The chest
  inside each Mid region must count: without it, anytime 0 failed 80% of floorless seeds. With
  the current table, at anytime 0, 1 and 2, 5000 random seeds, 10000 fixed-set seeds and 3000
  each beside APQuest, beside another Spire world and sparse beside Spire had 0-1 failures
  each, and regions opened within 0.3 sphere of before. The either/or rules make a solo
  generation through fill about 40% slower (25 ms against 18 ms). Relic Rewards Available
  Anytime was already in slot data and restored for Universal Tracker; the fuzz now draws it.
  The client lets a player override the count locally, and a lower override than the slot's
  leaves logic expecting relics that player cannot claim.
- **Refactors are checked for equivalence** by loading the previous `rules_new.py` (and
  `logic_table.py`) beside the new code under renamed rule classes, then comparing every
  entrance result and power total for random item counts across random option combinations.
  That check is a throwaway script for each refactor, because it freezes old code.

## Card rewards: stockpile versus one at a time

In a normal run the first four or five card rewards are often survival picks, and later ones
are taken one at a time. In AP a run can start with many Card Rewards and compare all their
cards first, so it can draft toward an archetype and skip freely. Items that arrive during a
run behave more like the vanilla case.

`python3 scripts/simulate_card_drafts.py` models this. Its defaults: 3 archetypes, a lean
deck keeps 10 picks, the first 4 one-at-a-time picks must be attacks, scaling cards are worth
20% more over a run, and an archetype adds 3 cards' worth once it has 4 cards.

What it shows:

- Drafting from a stockpile is never worse than one at a time. Under the defaults it is about
  19% better per reward from the first reward; that size comes from the forced-pick and
  scaling assumptions, not from the model.
- Before the deck is full, value grows roughly linearly. After that, a one-at-a-time drafter
  stops gaining (the model has no removals or swaps), while a stockpile keeps improving through
  choice, by less with each reward. This declining tail is the most robust result.
- The keep limit decides where the curve bends (8 bends early, 14 near 14 rewards). With the
  defaults the late steps fall from about 0.35 to 0.12.
- Archetypes add about 1.5-2 and reach the same strength two or three rewards sooner. They
  depend on skill, so treat them with the same caution as ascension.

The region requirements were tuned against the card tables, so reshaping a table moves every
gate. Keep a full pool's total the same (8.5 with half shuffle, 17.3 with all card rewards
shuffled) unless the region numbers are retuned too, and re-run the generation fuzz.

What the logic uses now:

- **A declining tail with the same totals.** After the full-value rewards (5 with half shuffle,
  11 with all shuffled), steps fall from 0.9 to 0.5 instead of a flat 0.7. Endgame gates do not
  move; mid-pool gates open slightly earlier. It made no measurable difference to generation.
- **Vanilla card rewards count 0.85** (`VANILLA_CARD_POWER`), because they arrive one at a time
  with forced early picks. This affects half shuffle only and asks for up to about 1.6 more AP
  power by the Act 3 boss.

## Ideas not built yet

- **Logic leniency option.** Parked. It only changes when checks are expected, not how hard
  the game is once everything has arrived; difficulty belongs to ascension and the item pool.
- **Aggressive restarts.** A strong run can fight every elite it can reach and take fight-heavy
  paths, so per-run counters (Relic N, Card Reward N) rise sooner. This could be modelled as
  extra elite tiers with either/or rules. Low value: only about one extra elite per act is
  realistic, and it only loosens checks that already come late.
- **Calibrate from run data.** AP decides what a run starts with, so starting items against
  how far each run got is close to a randomized experiment. Logging those per run would allow
  fitting each value instead of setting it by feel. Abandoned runs and player skill need care.
