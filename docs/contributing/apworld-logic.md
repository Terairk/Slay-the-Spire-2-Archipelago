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
  card and relic, rests, smiths, card removals, Ancients, shop choices and, with Gold Sanity, the
  gold to buy them. Its value raises the requirement, so extra power can make up for it.
  Unshuffled support is never missing. Only Ancients keep item gates (Start of Act checkpoints).

Logic assumes a player restarts with everything they hold, so each gate describes a fresh run
with the current items, not the run in progress.

Every number lives in `world/spire2/logic_table.py`. Starter values are reviewed per character
in `world/spire2/characters.py`. Modded characters use the default values: they are assumed to
define Archaic Tooth and Touch of Orobas mappings, as every predefined one does. A mod without
them keeps its starters unchanged, and logic would then credit upgrades it never gets. `python3 scripts/print_logic_table.py` prints the tables, and
Universal Tracker's explanation lists each contribution to a check.

### Two tracks per act

- **Path track:** the Early, Mid, Late and Boss regions, plus Act 1's Hallway between Early and
  Mid. They hold every check a run can reach while walking around elites: floors, card rewards,
  gold, potions, campfires and the Act 1 chest relic.
- **Elite track:** one region per relic. Each act's elites are fought in turn (Act 1 Elite 1
  and 2, Act 2 Elite 1 and 2, Act 3 Elite 1 to 3), each entered from the one before and the
  first from the act's Mid region. In Acts 2 and 3, a Chest region entered from Mid holds the
  chest relic a run reaches after the previous act's elites. Each act's chest and first elite
  need the previous act's last elite, so relic counts add up across a run.

Relic N is the run's Nth elite, chest or Black Star reward. Relic 1 is the Act 1 chest on the
path; relics 2-3 are Act 1's elites, 4 the Act 2 chest, 5-6 Act 2's elites, 7 the Act 3 chest
and 8-10 Act 3's elites, assuming a run fights two elites in Acts 1 and 2 and three in Act 3.

Early Act 1 needs only the character: it holds the three weak hallway fights that open the act.
The Act 1 Hallway holds the fourth fight's checks (Combat Gold 4, Card Reward 4 with all card
rewards shuffled, and Reached Floor 6). Its base power is 0, so it asks only for a normal start:
a run missing its starter card, starter relic or Neow needs that much power instead. Mid and
Late Act 1 need 2.5 and 7 power. With Seeded Run they keep the elite-inclusive 4 and 10: every
run replays one map, which may force an elite. Elites climb from about Mid power to Late
power over each act (4.5 and 6.5; 15.5 and 17; 26, 26.5 and 27), and chests take Mid power (14
and 25.5). Elite track rows otherwise copy their act's Late row, including its vanilla card
credit. Old logic keeps its original relic placement and has no elite track.

Combat checks follow the table's budget of seven combat card rewards in Act 1: Late Act 1 holds
the seventh fight's checks, and Early Act 2 the eighth's (Combat Gold 8, and Card Reward 8 with
all card rewards shuffled). The acts hold 7, 7 and 6 fights' checks; half shuffle's Card
Rewards stay 4, 3 and 3. With floor checks, each act's Campfire 1 check is in its Mid region,
where rest sites start; floorless worlds keep it in the Early region, which their openings need.
Old logic keeps 8, 6 and 6 fights and every Campfire 1 in its Early region.

### Claimable relics

A run claims AP Relics past Relic Rewards Available Anytime only after elite, chest or Black Star
rewards, one claim per reward, so a power check counts at most anytime + the relic rewards taken
by then. Walking around elites, those are the chests passed, including the one on each Mid
region's fixed row. When an earlier act's last elite is reachable, a region may count the
rewards up to it too: 3 after Act 1's elites, 6 after Act 2's and 10 after Act 3's, plus later
chests. An elite track region counts the rewards before its relic. Each path entrance is an
either/or of up to four power checks (`region_power` in `rules_new.py`), and
`python3 scripts/print_logic_table.py` prints the counts. The early items of sparse openings
make up any relics Mid Act 1 cannot count with Card Rewards.

### Campfires

A rest site offers one action: rest or smith (`RestSitePolicy` removes whichever is locked). A
port of the map generator (below) counts the rest sites a path passes, and both campfire supports
follow from that.

- **Rest** is worth 3, 3.5 and 4 power in its act's Mid, Late, elite and boss regions. A run that
  can smith but not rest there smiths at every rest site instead, so a missing Rest costs half
  (`REST_SHARE_WHILE_SMITHING`).
- **Smith** tiers are worth 2.5, 2.5 and 1.25: about one power per upgrade, two or three
  upgrades an act for a run that seeks rest sites, less in Act 3 because the best cards are
  already upgraded. Upgrades add up as a run passes rest sites, so an act's own tier counts for
  the share of its rest sites passed by each region: Early 0, Mid 0.25 (and the chest), first
  elite 0.4, Late and later elites 0.6, boss 1 (`SMITHS_*`). Earlier acts' tiers count in full.
  No item gate remains: a run without Smith tier 1 needs 2.5 more power at the Act 1 boss.

### Shop choices

With Shop Sanity, missing slots cost their values in `SHOP_SLOT_VALUES` (4.5 for a full page),
scaled by 1/3, 2/3 and 1 in Acts 1, 2 and 3: purchases stay for the run, so a run without shop
choices falls further behind each act, as card removals do (1, 2.5 and 4.5).

A shop also needs gold. With Gold Sanity, whose checks replace vanilla fight gold, the choices a
shop offers count only as far as a run's gold reaches a normal run's by that act's Late region:
185, 368 and 551 (walking around elites, Poverty-safe). A run counts 99 starting gold, 31 per
chest, every Combat Gold (13 x 11, assumed held), Elite Gold (30) and Boss Gold (75), so it has
273, 304 and 335 before Elite and Boss Gold. Act 1 needs none of them; a run holding none needs
about 0.5 more power in Act 2 and 1.8 in Act 3 with every slot available. Universal Tracker shows
this as "Missing shop gold". Without Gold Sanity, vanilla gold pays and logic does not count it.

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

With Gold Sanity, Elite and Boss Gold are always progression in new logic, because shop choices
count them too (see "Shop choices"). Combat Gold stays `useful`.

## Game facts the logic relies on

From the 0.111 beta decompile unless stated otherwise.

- Acts have 15, 14 and 13 rooms; with the Ancient and boss that is 17, 16 and 15 floors.
  Multiplayer maps have one room fewer per act.
- Map rows 1-5 cannot hold elites or rest sites (`StandardActMap`), so both start at floors
  7, 24 and 40, and no Early region has a rest site. Row 1 is always a monster; rows 2-5 hold
  monsters, ? rooms and shops. The row before the boss is all rest sites.
- Each act has one treasure chest on a fixed row seven rows before the boss: floors 10, 26 and
  41. Chests give 42-52 gold as well as a relic.
- ? rooms never become elites; their elite odds are disabled (`UnknownMapPointOdds`).
- An act map has 5 elites (8 with Swarming Elites), but one path usually reaches about 3, and
  4 when lucky. 5 or 6 needs Winged Boots from Neow. (Play experience.)
- Acts 1, 2 and 3 open with 3, 2 and 2 weak hallway encounters.
- A rough port of `StandardActMap` (without its pruning pass; each ? room counted as about 0.17
  fights, from `UnknownMapPointOdds`) gives these fights on paths that avoid elites: about 6, 8
  and 10 in Act 1 (fewest, average, most), 6, 7.4 and 9 in Act 2, and 5, 6.6 and 8 in Act 3.
  The port was a throwaway script.
- Each act map gets 6-7 random rest sites (5-6 in Act 3), 3 shops and 5 elites (`GetMapPointTypes`
  of each act; `MapPointTypeCounts`). Rest sites cannot sit on rows 1-5 or the two rows before the
  all-rest row. A second port, with the pruning pass (2026-10-23, 2000 maps per act), counts the
  rest sites a path passes before the all-rest row (that row adds one more):

  | Act | Average path | Most rest sites, no elites | Most rest sites |
  |---|---|---|---|
  | 1 | 1.4 (Mid 1.0, Late 0.5) | 2.4 (Mid 1.7, Late 0.7) | 2.8 |
  | 2 | 1.3 (0.8, 0.5) | 2.1 (1.4, 0.8) | 2.5 |
  | 3 | 1.1 (0.9, 0.3) | 2.0 (1.5, 0.5) | 2.3 |

  A path seeking rest sites while avoiding elites passes three or more before the all-rest row in
  44%, 26% and 19% of maps. The average path passes 0.7 shops per act, one seeking shops about 2.
- Vanilla gold: monsters 10-20, elites 35-45, bosses 100 (`EncounterModel`), chests 42-52. Gold
  Sanity's pool has 13 Combat Gold (15), 7 Elite Gold (40) and 2 Boss Gold (100) per character.
- Combat Gold N and Card Reward N count every non-boss combat in a run, including elites and ?
  room fights (`Patches_InjectAPRewards.cs`). The Act 1 and 2 bosses' card rewards are Rare Card
  Reward 1 and 2 instead.
- A rest site offers every unchecked campfire check of its act and earlier acts
  (`RestSitePolicy.GetAvailableChecks`), so an act's first campfire check needs a rest site in
  that act or a later one.
- Characters start with 99 gold. Poverty multiplies combat and chest gold by 0.75.
- Shop prices: cards 50 / 75 / 150 by rarity (colorless x1.15), relics 175 / 225 / 275
  (shop relics 200), potions 50 / 75 / 100. Card removal costs 75, rising by 25 each time;
  Inflation makes that 100, rising by 50.
- The client counts the first ten eligible elite, chest and Black Star rewards in a run as
  Relic 1-10. Every received AP Card Reward is claimable from the AP menu from the first floor,
  and its three cards stay fixed for the run. With half shuffle, every odd combat card reward is
  a check, so Card Reward N is the run's card reward 2N - 1.
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
  normal seeds, 0 of 3000 for each partner run (`--sparse --partner apquest` and `--sparse
  --partner spire` with `--rng-seed 31`, `--partner spire` with 32), and 6.9% forced sparse with
  the tuning, elite track and claimable relics below (9.0% for the previous logic on the same
  seeds).
  Multiworlds give fill much more room than these solo seeds.
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
- **The elite track has one region per relic** (user's choices, 2026-10-21). One Elites region
  per act held two to four relics behind one number: Act 1's two elites opened with Late Act 1
  in every seed (both 7), and the Act 2 and 3 chest relics waited for elite power although a
  run reaches them without fighting that act's elites. A single Act 1 Elites region at 5.5 was
  tried first: it opened a sphere before Late Act 1 in 22-30% of seeds, while 8.5 delayed later
  acts by about 0.1 sphere and raised forced sparse failures from 8.5% to 9.5%. The split gives
  Act 1 4.5 and 6.5, keeping 5.5 on average: at 7 the second elite opened with Late Act 1 in
  every seed, at 6.5 a sphere earlier in about 9%. By progression found (default set), relics 2
  and 3 now open at 14% and 19% instead of both at 16% with one region at 5.5, relics 4-6 at 43%,
  47% and 53% instead of all at 46%, and relic 10 at 83% instead of 79% with relics 7-9. Path
  regions moved by at most 0.07 sphere, and generation was unchanged: 0 failures in every normal
  and partner run, and 347 of 5000 forced sparse seeds (356 before the split, 343 with one
  region at 5.5). The first Act 2 elite keeps 15.5: raising the whole
  Act 2 Elites region to 17 or 18 had made more runs end in Late Act 2 (0.69 and 0.74 against
  0.63) and raised forced sparse failures to 8.8% and 9.8%. Because elite track rows take the
  Late row's vanilla card credit, with half shuffle Act 3's first two elites (26 and 26.5 at
  19 rewards) ask for less AP power than Mid Act 3 (25.5 at 17) and open with it.
- **Logic counts only claimable relics** (user's choice, 2026-10-21). Before, a power check
  counted every received relic. At the moment each region opened, a check limited to claimable
  relics would have failed Early Act 2 in 5-20% of seeds and Early Act 3 in 9-18% at the default
  anytime count of 2, and 39-65% of both at 0. Counting only the chests on the path cannot reach
  the Act 3 boss with a low anytime count (69-85% of random seeds failed at anytime 0-3),
  because boss checks assume elite relics; hence the either/or with the elite track. The chest
  inside each Mid region must count: without it, anytime 0 failed 80% of floorless seeds. With
  the current table, at anytime 0, 1 and 2, 5000 random seeds, 10000 fixed-set seeds and 3000
  each beside APQuest, beside another Spire world and sparse beside Spire had 0-1 failures
  each, and regions opened within 0.3 sphere of before. The either/or rules make a solo
  generation through fill about 40% slower (25 ms against 18 ms). Relic Rewards Available
  Anytime was already in slot data and restored for Universal Tracker; the fuzz now draws it.
  The client lets a player override the count locally, and a lower override than the slot's
  leaves logic expecting relics that player cannot claim.
- **The fourth Act 1 fight waits for a normal start** (user's choice, 2026-10-22). Early Act 1
  needed only the character but held Combat Gold 4, Card Reward 4 (all shuffled) and Reached
  Floor 6: a fourth fight, the first from the normal pool. Without Neow, the starter card and
  the starter relic that fight is close to impossible (play experience). The Act 1 Hallway now
  holds those checks at base power 0, so only a missing normal start costs power: with Neow
  Sanity and both starters shuffled, 6 for Ironclad and 4-7.5 across the vanilla characters.
  Mid Act 1 rose from 2 to 3 at the same time. On the usual fuzz sequences: 0 of 5000 normal
  seeds failed, 0 of 3000 sparse beside APQuest, 1 of 3000 sparse beside Spire (0 before), 0 of
  3000 beside Spire with `--rng-seed 32`, and 319 of 5000 forced sparse (347 before). Measured
  with Mid Act 1 at 2, moving the fourth fight into Mid Act 1 instead failed 3, 2 and 598 of
  those normal, sparse-beside-Spire and forced sparse seeds; the Hallway alone 0, 0 and 342.
- **Modded starters use the default starter power** (user's choice, 2026-10-22). New logic used
  to require a modded character's shuffled Progressive Starter Card and Relic before Late Act 1
  and credit no upgrade, because the APWorld cannot see whether a mod defines Archaic Tooth and
  Touch of Orobas mappings. Every predefined modded character does, so all modded characters
  are now assumed to and use the default starter power. Old logic keeps its gates.
- **The default starter power follows the vanilla average** (user's choice, 2026-10-23). It was
  a guess: a missing card or relic cost 1 or 2.5, and each upgrade added 4. The five reviewed
  vanilla characters average 1.2 / 3.9 for the card and 2.3 / 3.3 for the relic; the user rounded
  that to card 1 / 4 and relic 2 / 3.5. Necrobinder's pair penalty stays out, because it only fits
  a known card and relic pair. Recheck the average by hand when vanilla values change; no test
  enforces it, so tuning a character never fails the suite.
- **Act 1 holds seven fights' checks, and Campfire 1 waits for a rest site with floor checks**
  (user's choice, 2026-10-23). Late Act 1 held the eighth fight's checks, although
  `logic_table.REGIONS` budgets seven combat card rewards before the Act 1 boss; on paths that
  avoid elites, 8 is about the average and 6 the fewest. Each act's Campfire 1 sat in its Early
  region, which has no rest site. Measured in-process on the usual fuzz sequences (normal,
  sparse beside APQuest, sparse beside Spire, beside Spire with `--rng-seed 32`, forced sparse;
  before: 0, 0, 1, 0 and 319):
  - Combat 8 in Early Act 2 (chosen): 0, 0, 1, 0 and 365. Half shuffle does not change: Card
    Reward 4 is the seventh fight's.
  - Act 1 also matching the table inside the act (Mid 5, Late 6-7): 0, 0, 1, 0 and 382.
  - Six fights in Act 1 (Mid 5, Late 6, Early Act 2 7-10): 0, 5, 1, 0 and 626. Half shuffle's
    Card Reward 4 leaves Act 1 too.
  - Act 1 Campfire 1 always in Mid Act 1: 0, 6, 1, 0 and 692. Every failure in every suite was a
    floorless world, so with floor checks only, the move changed no result, for Act 1 alone or
    for all three acts (chosen).

  With both, regions open within 0.1 sphere of before. Old logic keeps 8, 6 and 6 fights and
  every Campfire 1 in its Early region.
- **Balance decides logic numbers; generation failures are secondary** (user, 2026-10-23). The
  remaining fill failures are confined to solo seeds without floor checks, so options are judged
  on how well they model the game: normal-run parity, map facts, the gold economy and the runs a
  gate implies. Failure counts and sphere shifts are still reported alongside.
- **A missing Rest costs half while that act's Smith works** (user's choice, 2026-10-23). A rest
  site offers one action, so a run that can smith but not rest smiths at every rest site instead.
  Missing both still costs the sum. Considered: Act 1 Rest 2 instead of 3 whether or not Smith
  works, and also halving the act's Smith tier while resting works, rejected because extra heals
  are rarely needed while upgrades last the whole run. With the shop and starter changes below, a
  trial on 3000 forced sparse seeds (`--rng-seed 31`) went from 221 to 181 failures, and
  campfire and shop worlds opened regions 0.1-0.2 sphere earlier.
- **Missing shop choices add up across acts** (user's choice, 2026-10-23). A full missing page
  cost 3, 1.5 and 0.75 power in Acts 1, 2 and 3 (scale 2/3, 1/3, 1/6); it now costs 1.5, 3 and
  4.5. One Act 1 shop visit buys about one item, about 330 vanilla gold per act buys roughly two
  besides a removal, and purchases stay for the run, as removals already did. Considered: 1.5 /
  1.5 / 0.75 and 1.5 / 2.25 / 3. On a full page each opened the Act 1 boss about 0.25 sphere
  earlier; by Act 3 they opened 0.17 and 0.12 sphere earlier and the chosen scale as before.
  Generation did not change.
- **Mid Act 1 needs 2.5** (user's choice, 2026-10-23). A normal run entering Mid Act 1 has taken
  three card rewards, 3 x 0.85 = 2.55, so 2.5 is parity; 3 asked half a point more, and the
  earliest gate deserves the smallest buffer. With half shuffle one Relic opens it, one Card
  Reward does not (1.275 + 1). Sparse openings without anytime relics now hold a Relic and no
  Card Reward. Measured: 24-26 more forced sparse failures per 3000, and floorless worlds' later
  regions open 0.1-0.4 sphere later, because a lower gate lets fill put its last-placed items in
  Mid Act 1 instead of the opening.
- **Smith tiers count the rest sites passed, at 2.5 / 2.5 / 1.25, with no Smith gate in Act 1**
  (user's idea and choices, 2026-10-23). One Smith does not add its value at once: upgrades add up
  as a run passes rest sites. Before, a new act's tier counted in full from the act's Early
  region, which has no rest sites, and tier 1 never counted because the Act 1 boss required Smith
  as an item. The map port gives the shares (see "Campfires") and two or three upgrades an act
  for a run that seeks rest sites; at about one power per upgrade the tiers are 2.5 / 2.5 / 1.25
  (3.5 / 3 / 1.5 before). Considered: 2 / 2 / 1 and 3 / 3 / 1.5. Measured: the Act 2-3 ramp alone
  took forced sparse failures from 181 to 103 of 3000; Act 1's ramp and the dropped gate kept
  regions within 0.1 sphere and opened Late Act 1 about 0.1 later in campfire worlds. One solo
  seed with floor checks in 5000 now fails because of Act 1's smith charge (Necrobinder, Seeded,
  Campfire Sanity, both starters, Start of Act Ancients); the next seeds with its options generate.
- **With Gold Sanity, shop choices need gold** (user's idea and choice, 2026-10-23). Shop choices
  turn gold into power, and Gold Sanity replaces vanilla fight gold with items. Elite and Boss
  Gold count; Combat Gold is assumed held, because counting it would make 13 more items per
  character progression for little power each. Considered: counting all gold items (a Gold
  Sanity world without Shop Sanity would have 46 of 94 items progression instead of 24, and its
  Acts 2-3 opened 0.1-0.25 sphere later in a trial), and Elite and Boss Gold standing in for
  combat gold too (Act 3 up to 0.4 sphere later). The chosen version makes Elite and Boss Gold
  progression whenever Gold Sanity is on: 33 of 94 in that world, and no change with Shop Sanity,
  where they already were.
- **Refactors are checked for equivalence** by loading the previous `rules_new.py` (and
  `logic_table.py`) beside the new code under renamed rule classes, then comparing every
  entrance result and power total for random item counts across random option combinations.
  That check is a throwaway script for each refactor, because it freezes old code.

## Where regions open

`scripts/fuzz_apworld_generation.py --spheres` reports the sphere in which each region opens
over successful seeds, and the share of the world's own progression items found before it.
`--option-set` replaces random options with fixed one-Ironclad option sets on the same seeds:
`default` (the options' defaults: floor checks, half shuffle, Anytime Ancients, Relic Rewards
Available Anytime 2), `all-cards`, `campfire-shop`, `everything` (all shuffled, every sanity,
Neow, starters) and `floorless` (with gold and potion checks). Mean opening sphere, 2000 seeds
per set (`--rng-seed 2026`):

| Region | Power | default | all-cards | everything | floorless | Progression before (default) |
|---|---|---|---|---|---|---|
| Act 1 Hallway | 0 | 1.0 | 1.0 | 2.2 | 1.0 | 0% |
| Mid Act 1 | 2.5 | 2.0 | 2.0 | 3.1 | 2.0 | 13% |
| Late Act 1 | 7 | 2.6 | 2.6 | 4.3 | 2.3 | 20% |
| Act 1 Elite 1 | 4.5 | 2.1 | 2.2 | 4.1 | 2.0 | 14% |
| Act 1 Elite 2 | 6.5 | 2.5 | 2.5 | 4.2 | 2.2 | 19% |
| Act 1 Boss Arena | 11.5 | 3.3 | 3.3 | 5.1 | 3.0 | 34% |
| Early Act 2 | 12 | 3.9 | 3.9 | 5.9 | 3.7 | 41% |
| Mid Act 2 | 14 | 4.2 | 4.6 | 6.9 | 4.0 | 43% |
| Act 2 Chest | 14 | 4.2 | 4.6 | 7.0 | 4.0 | 43% |
| Act 2 Elite 1 | 15.5 | 4.4 | 4.9 | 7.2 | 4.1 | 47% |
| Act 2 Elite 2 | 17 | 4.7 | 5.1 | 7.3 | 4.5 | 54% |
| Late Act 2 | 18 | 4.9 | 5.3 | 7.5 | 4.6 | 57% |
| Act 2 Boss Arena | 22 | 5.3 | 5.8 | 8.1 | 5.1 | 65% |
| Early Act 3 | 23 | 6.1 | 6.5 | 8.6 | 5.8 | 74% |
| Mid Act 3 | 25.5 | 6.6 | 7.2 | 9.2 | 6.3 | 79% |
| Act 3 Chest | 25.5 | 6.7 | 7.3 | 9.3 | 6.4 | 79% |
| Act 3 Elite 1 | 26 | 6.6 | 7.4 | 9.3 | 6.3 | 79% |
| Act 3 Elite 2 | 26.5 | 6.6 | 7.6 | 9.4 | 6.3 | 79% |
| Act 3 Elite 3 | 27 | 6.9 | 7.6 | 9.5 | 6.6 | 83% |
| Late Act 3 | 27 | 6.9 | 7.6 | 9.5 | 6.6 | 83% |
| Act 3 Boss Arena | 30 | 7.4 | 8.2 | 10.0 | 7.1 | 94% |

How to read it:

- A solo world has only 7-9 spheres, so a gate's step shows as a fraction of a sphere. A player
  at the logic minimum plays about one run per sphere, so the sphere gap between a path region
  and the next is roughly how many runs end there: 0.3-0.7 in Acts 1 and 2 (default set) and
  0.2-0.7 in Act 3.
- Each act's relic checks open in steps: the first elite near Mid power, the last near Late.
- By progression found, the largest steps are Late Act 1 to the Act 1 boss and Mid to Late
  Act 2 (13-14 points each), then the Act 3 boss (11). Early Act 3, which also asks for the
  third Ancient, opens about 0.8 sphere after the Act 2 boss.
- The Act 1 Hallway opens in sphere 1 unless starters or Neow are shuffled. In `everything`
  it waits for them about one sphere, and every later region opens 0.5-0.7 sphere later than
  before the Hallway.

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

- **Rest by rest sites passed.** Rest counts its act's full value from Mid (3 in Mid Act 1),
  although a run has passed about one rest site by the end of Mid. Heals are spent within the act,
  so a ramp like the smiths', perhaps gentler because the first heal matters most, would follow
  the same map facts.

- **Logic leniency option.** Parked. It only changes when checks are expected, not how hard
  the game is once everything has arrived; difficulty belongs to ascension and the item pool.
- **Aggressive restarts.** A strong run can fight every elite it can reach and take fight-heavy
  paths, so per-run counters (Relic N, Card Reward N) rise sooner. This could be modelled as
  extra elite tiers with either/or rules. Low value: only about one extra elite per act is
  realistic, and it only loosens checks that already come late.
- **Calibrate from run data.** AP decides what a run starts with, so starting items against
  how far each run got is close to a randomized experiment. Logging those per run would allow
  fitting each value instead of setting it by feel. Abandoned runs and player skill need care.
