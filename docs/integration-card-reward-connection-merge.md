# Integration merge: bonus rewards and connection fixes

Source: `upstream/integration/ap-card-reward-connection-fixes` at `64a77823c1515133b076f645a71059e9b651d413`.
Target before merging: `experiment/replicated-card-reward-generation` at `513cfd081956c28a03c37a43059488fe5af82339`.

## Bonus rewards

`world/spire2/items.py` defines universal item **600**, Bonus Wax Relic. The ordered `bonus_items` slot data selects each receipt's definition. The upstream APWorld changes need no multiplayer-specific contract change. Client version 2.1.0, APWorld version 1.1.0, and CompatFlag retain the target branch's values.

`Patches_ItemProcessor.HandleUniversalItem` registers the indexed receipt in the existing ledger without character-offset decoding. Initial multiplayer history goes through `MultiplayerSupport.PrepareApSession`.

`ArchipelagoProgress.IsAvailableInRewardMenu` exposes it to any configured character. `BonusRewardUtility.GetOrAssign` uses the category ordinal and owning player's settings. Pool selection uses a stable hash, rejects pickup-effect and run-ineligible relics, and leaves game RNG untouched. Explicit values receive the same eligibility checks. An unresolved definition stays unavailable without consuming the receipt.

The mutable relic has `IsWax=true` and is serialized by receipt index into `BonusRelicAssignments`. Assignment deltas, checkpoint loading, and new-run reset use the existing progress system. `ApMirroredRewardDispatcher` sends the concrete model through the owner-authored menu and native `RewardsSetSynchronizer`. Every replica grants it; only the owner marks the receipt used.

`ApMirroredRewardKind.Bonus` and the F# bonus shape bypass ordinary relic-coupon reservations and consumption. Native relic acquisition retains its normal game effects. Menu schema is **9**, requiring matching builds on all peers.

## Wax lifecycle and feature switch

`Patches_WaxRelics` awaits native `Hook.AfterCombatEnd`, then processes eligible players in `runState.Players` order. Only AP players with configured wax definitions are controlled. The first unmelted wax relic melts after three completed combats containing an unmelted wax relic; no wax resets the counter.

Singleplayer stores its counter in `ArchipelagoProgress` / `ApRunProgressState`. Multiplayer stores it independently on `ApPlayerRunState`, keyed by NetId, through `ApRunData.SetWaxCombatCount`. Owner progress messages cannot advance another replica's counter. Continuation restores the host checkpoint; a new run starts fresh.

Toy Box's native melting and displayed counter are suppressed for those same players. Its pickup behavior remains native. Guests and AP slots without bonus wax retain native Toy Box behavior.

**Switch:** `MultiplayerFeature.BonusItems` in `MultiplayerSupport.EnabledExperimentalFeatures` is enabled. Removing it converts multiplayer bonus receipts through the existing five-gold bank, both on history rebuild and live delivery. This is five raw gold shared across configured characters with cumulative rounding, matching universal buff conversion. Singleplayer still grants wax relics. Change this switch for a fresh run and use the same build on every peer.

Useful logs:

- `Assigned bonus wax relic ...: player=..., receipt=..., ordinal=...`
- `Melted AP wax relic ...: player=...`
- `Wax melt failed: player=..., relic=..., combatCount=...` includes the exception.

## Other merge decisions

- Preserve replicated card generation, first-reveal lifecycle, deferred rewards, and Egg refresh. The lifecycle/deferred files are unchanged from the target.
- Preserve multiplayer loading and session-callback ownership. Older-APWorld consent is keyed by authenticated identity, version, and CompatFlag, retained over recoverable disconnects, and cleared when leaving the slot. Stale warnings cannot affect a replacement session. Newer major/minor APWorld versions are rejected before settings parsing.
- Keep the linked-reward callback repair and single signal-driven removal. Add teardown guards, remove redundant freeing, and remove the reward screen through its overlay stack.
- Add `relic.IsAllowed(player.RunState)` to Ancient collection, preserving Neow, progressive-starter, and Golden Compass exclusions.
- Kaleidoscope shares `CrossCharacterCardPoolUtility` with Prismatic Gem and Colourful Philosophers. Its small native construction sequence retains native shuffle, RNG, generation, flags, and presentation. The public/beta reroll-option difference is compiled explicitly. Colourful Philosophers calls the publicized `OfferRewards` directly instead of reflection.
- Move new reward helpers/patches into the refactored directories. Keep the current publicizer, dual-build, and Python release architecture.
- Ship catalogs as `data/*.data` in compatibility bundles and archives, with lookup support for DLLs under `lib/<version>`. Normalize both catalog and game IDs for blacklist/pool matching.
- Upstream website changes are included by the merge; validation focused on client, world, and required packaging changes.

## Validation

- DLL-only Release builds passed for public 0.107.1 and beta 0.111.0, each with two Godot source-generator warnings (CS8785/CS0436).
- C# regression suite: 222 passed with the public artifact supplied, including built manifests and bonus-definition JSON serialization. These two artifact checks also passed against the beta build. Full compatibility-bundle loading was skipped because no assembled bundle was supplied.
- APWorld framework tests: 111 passed in an isolated temporary copy using the sibling Archipelago 0.6.7 environment. Its installed world was not replaced. New tests cover universal IDs, ordering, and filler replacement.
- Release packaging tests: 11 passed. Python AST checks, both build-output catalog comparisons, and `git diff --check` passed.
- Full release assembly/publish, website build, in-game loading, and two-client runtime verification: **NOT RUN**.

## In-game test matrix

Use matching builds and installed character mods on all peers. Compare logs and actual relic state on both clients.

| Scenario | Expected result |
| --- | --- |
| Singleplayer: explicit and pool-based bonus entries | Ordered receipts and stable wax choices across menu reopen/save/load; one grant per receipt per run. |
| Two own-slot players with different definitions | Correct recipient and relic on both replicas; coupon availability unchanged. |
| Two players sharing one AP slot | Each claims their own copy independently. |
| Own-slot player plus vanilla guest | AP grants/melts agree on both replicas; guest Toy Box remains native. |
| Multiple wax relics, with and without Toy Box | Only the leftmost unmelted relic melts on each third eligible combat, without a second Toy Box melt. |
| Save/rejoin after one or two eligible combats | Original melt cadence survives on all replicas; no reroll or duplicate grant. |
| Fresh multiplayer run with BonusItems disabled | Live and rebuilt gold totals agree; singleplayer still grants wax. |
| Kaleidoscope, Prismatic Gem, Colourful Philosophers | Built-in pools survive AP locks; selected installed modded pools work; native guest behavior and event pool-isolation flags remain. |
| Singleplayer Ancient Anytime rewards | Multiplayer-only relics are absent, including Neow choices; linked rewards can be claimed/closed without double removal. |
| Older APWorld: accept, disconnect, reconnect | Same identity/version/CompatFlag reconnects without a prompt; changed compatibility needs confirmation; stale popups cannot affect a new session. |
