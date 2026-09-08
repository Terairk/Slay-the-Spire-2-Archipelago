# Replicated AP card rewards: a guided walkthrough

This explains commit [`24bd923`](https://github.com/Terairk/Slay-the-Spire-2-Archipelago/commit/24bd9232eecb449d20d098ba8e6d97c0b76170e7), **Generate AP card rewards on every multiplayer replica**, on `experiment/replicated-card-reward-generation`. Its parent is `2e72c9f`. The explanation is a snapshot of that change, not a promise that later revisions behave identically.

Read Part I for the overall flow. Part II walks through the implementation in small sections, including removed code. Part III explains every changed test and gives a runtime checklist. The file inventory at the end accounts for all 19 changed files. Source links open files in this branch; the commit link above preserves the exact original diff.

## Contents

- [Part I: Overview](#part-i-overview)
- [1. What changed](#1-what-changed)
- [2. The lifecycle from menu construction to claiming](#2-the-lifecycle-from-menu-construction-to-claiming)
- [3. Determinism and ordering](#3-determinism-and-ordering)
- [Part II: Detailed implementation](#part-ii-detailed-implementation)
- [4. The reward contract](#4-the-reward-contract)
- [5. Constructing the menu](#5-constructing-the-menu)
- [6. Preparing one card offer](#6-preparing-one-card-offer)
- [7. Running the native generation hooks](#7-running-the-native-generation-hooks)
- [8. Reopening and the Egg exception](#8-reopening-and-the-egg-exception)
- [9. Comparing independently generated offers](#9-comparing-independently-generated-offers)
- [10. Ordering asynchronous selections](#10-ordering-asynchronous-selections)
- [11. Connecting the queue to the game](#11-connecting-the-queue-to-the-game)
- [12. What reaches the host and what gets saved](#12-what-reaches-the-host-and-what-gets-saved)
- [13. Removed machinery and remaining special cases](#13-removed-machinery-and-remaining-special-cases)
- [Part III: Tests, limits, and file inventory](#part-iii-tests-limits-and-file-inventory)
- [14. Test changes one by one](#14-test-changes-one-by-one)
- [15. Validation and an in-game checklist](#15-validation-and-an-in-game-checklist)
- [16. Complete changed-file inventory](#16-complete-changed-file-inventory)

## Part I: Overview

### 1. What changed

Previously, the reward's owner generated the cards. The other machines received those cards and separately replayed the recorded effects of particular relics. For example, the implementation knew how to reproduce a Silken Tress state transition on a replica that had not run its generation hook.

Now every machine generates the offer for the same player. Each machine calls the native card factory and native modification callbacks. The owner sends a digest: a compact fingerprint of the offer and relevant player state. Other machines compare it with their own result before entering their native picker path.

Here, **owner means the player receiving this reward**. If Alice is a client and Bob hosts the game, Alice owns Alice's rewards. Bob's machine holds a replica of Alice's player, as do the other peers. Generating on Bob's machine means generating for that replica of Alice, using Alice's relics and settings.

| Stage | Before this commit | After this commit |
| --- | --- | --- |
| Open the AP reward list | Unopened card rewards are recipes | Same |
| Open one card picker for the first time | Owner generates; replicas receive final cards | Every machine generates for the owning player |
| Generation callbacks change a relic | Record selected effects and replay them elsewhere | Each replica executes native callbacks |
| Check agreement during reveal | Transfer cards plus effect metadata | Compare owner digest with locally computed digest |
| Reopen a revealed offer | Preserve cards and refresh Eggs | Same intended semantics, now performed locally on every replica |
| Save an assignment | Store final cards and associated state | Store final cards with the new strategy and no effect-replay records |

The first-reveal timing and the Egg exception predate this commit. This commit changes how multiplayer executes that lifecycle.

**The important limitation:** this is an experiment. Removing the hook allowlist lets more native behavior participate, but identical seeds alone cannot guarantee that arbitrary hooks are deterministic. The digest detects covered disagreements; it does not repair them or prove that all possible game state agrees.

### 2. The lifecycle from menu construction to claiming

There are three distinct moments:

1. **Menu construction:** create a reward row saying “receipt 81 is a regular card reward assigned to this act.” No hidden cards need to exist yet.
2. **First reveal:** select that row. Generate the actual card choices and run generation effects. Skipping afterward still counts as having revealed the offer.
3. **Claim:** choose a card or complete the native reward action. Only successful consumption marks the AP receipt used.

The approximate call chain is below. Indentation means “calls or enters”; the native network dispatcher between the menu and reward selection is abbreviated.

```text
Open the AP reward menu
  BuildOwnerMenuSpec
    BuildAssignedSpec
      new card receipt -> empty card recipe
      revealed receipt -> existing serialized assignment
  publish the menu to the other machines
  BuildRewardsSet -> BuildNativeReward -> BuildCardReward
  RewardsSetSynchronizer.BeginRewardsSet

Select one card reward row
  native reward selection reaches Reward.SelectUnsynchronized
  Patches_APRewardSelectionOrder queues this AP selection in multiplayer
    ApNativeCardReward.OnSelect
      ApDeferredCardReward.OnSelect
        ApNativeCardReward.PrepareCards
          first reveal -> GenerateCardChoices
          existing offer -> RefreshCardChoices (Eggs only)
          calculate digest; remote replicas compare with owner
          PublishRevealedAssignment
        ApNativeCardReward.SelectCards
          bind the already-reserved first picker choice ID
          native CardReward.OnSelect
            show/interpret native choices and grant the result
      false -> preserve the revealed assignment
      true -> finish the established AP consumption path
```

The queue covers the whole asynchronous selection, including time spent waiting for native choices and callbacks. It does not just protect the few synchronous lines that create cards.

The native picker still provides the UI and its keyboard/controller behavior. We prepare its cards and coordinate execution; we have not replaced it with a custom card-selection interface.

### 3. Determinism and ordering

The receipt-specific RNG was already present. Its seed input includes:

```text
fixed protocol label | reward domain (card/potion) | run seed
| AP slot | native player slot index | AP character number | received item index
```

`CreateApRewardRng` hashes that string and takes a shared 32-bit seed representation accepted by the supported APIs. This creates an independent random stream for a particular reward. It does not make different game versions interchangeable; multiplayer testing still needs matching builds.

Receipt 81 can legitimately be revealed before receipt 80. The requirement is that all replicas process **81 then 80**, not that receipt numbers are ascending. The menu's display ordering and the actual selection ordering are different things.

Even with independent reward RNGs, order matters for mutable state. Suppose a one-use relic affects the next offer. If one machine finishes its effect on 81 before generating 80, while another overlaps both generations, the same seed for 80 can meet different relic state.

The queue added here preserves the order in which AP selections enter it for one player. It waits through asynchronous completion. It assumes that native transport delivers the same selection order to the replicas; it is not a new network sequencing protocol that sorts or repairs differently ordered messages.

Different players have separate queues. This avoids blocking Bob's independent reward on Alice's picker, but it also means arbitrary shared run-wide effects still need scrutiny.

## Part II: Detailed implementation

### 4. The reward contract

#### 4.1 `RewardMaterialization.fs`: name the new execution strategy

Source: [RewardMaterialization.fs](../../client/StS2AP.Domain/RewardMaterialization.fs).

This F# type describes how an assignment was materialized. A discriminated union is a closed set of named cases; the new case is `ReplicatedCard`.

The edits, in source order:

1. The comment now says completed assignments restore final models without rerolling. That statement applies to the new strategy too: continuing a run should restore an existing offer, not generate it again.
2. Add `ReplicatedCard` alongside `OwnerFinal` and `RestoredReplicaNative`.
3. Map it to the wire string `ap_rng_replicated_card_v1` in `StrategyId`.
4. Set `AllowsPersistentEffects` to false for this case.
5. Add a third callback to `Match`, and invoke it when the value is `ReplicatedCard`.
6. Recognize the new string in `Decode`; unknown strings still produce an error.

The name `AllowsPersistentEffects` needs careful reading. Here it means “allows our serialized **effect-replay records**.” It does **not** mean that a native relic is forbidden from changing persistent state. Native callbacks now change that state themselves.

The C#-friendly `Match` method is approximately this switch:

```csharp
switch (strategy)
{
    case OwnerFinal: return ownerFinal();
    case RestoredReplicaNative: return restoredReplicaNative();
    case ReplicatedCard: return replicatedCard();
}
```

Callers must supply the third handler, which explains several small interop/test changes later.

#### 4.2 `MirroredReward.fs`: reject contradictory payloads

Source: [MirroredReward.fs](../../client/StS2AP.Domain/MirroredReward.fs), `DecodeCardData` and `Decode`.

An empty model array means a deferred recipe. Such a recipe is accepted only when deferred cards are allowed, it is unrevealed, it cannot reroll, it contains no effect records, and it uses the new replicated strategy. The changed condition replaces the old owner-final strategy requirement.

A second check rejects **nonempty replicated cards marked unrevealed**. If this strategy has already produced actual cards, its first reveal has happened. This prevents contradictory lifecycle state from slipping through decoding.

The potion branch rejects the replicated-card strategy. Potions retain their existing owner-materialization path; this experiment does not change potion generation into replicated generation.

These checks validate data shape. They do not run the game or prove that two native factories produced the same cards.

#### 4.3 The two spec files

Sources: [ApMirroredRewardSpec.cs](../../client/StS2AP/Models/Rewards/Specs/ApMirroredRewardSpec.cs) and [ApRewardMenuSpec.cs](../../client/StS2AP/Models/Rewards/Specs/ApRewardMenuSpec.cs).

`ApMirroredRewardSpec` changes comments, not fields:

- `RequiresNativeMaterialization` remains a rejected legacy flag. Its old comment could incorrectly suggest that *all* replica generation is forbidden. It now identifies the former menu-time generation contract specifically.
- `AppliedEffects` is described as legacy owner-final effect records. New replicated card offers require an empty list.

`ApRewardMenuSpec.CurrentSchemaVersion` changes from 7 to 8. Old peers expect different reveal messages and reserve a different number of choice IDs, so accepting them as compatible would be incorrect.

**Old saves:** this commit adds no migration or fallback generation path. Pre-existing legacy decoding cases and DTO fields remain. `PrepareCards` explicitly rejects a previous card-generation contract and asks for a new run. Decoding an old shape is not a guarantee that the old run can continue. Saving and continuing a run created under this new implementation remains part of the intended behavior.

### 5. Constructing the menu

Source: [ApMirroredRewardDispatcher.cs](../../client/StS2AP/Utils/Rewards/ApMirroredRewardDispatcher.cs), `BuildAssignedSpec`.

The dispatcher adds a constant for `ap_rng_replicated_card_v1`. In the card case, a newly constructed spec receives this strategy.

If `CardAssignments` has no entry for this received item index, the case exits with an empty model list. Creating the menu therefore does not run card-generation hooks for every row in a backlog.

For an existing assignment, the existing code copies the stored cards and configuration. It preserves the assignment's actual strategy rather than relabeling an old assignment as newly generated. That matters to the old-contract rejection later.

The surrounding construction flow is unchanged: the owner publishes the menu, each peer creates native reward objects, and native reward-set synchronization tracks selections. `BuildCardReward` caches per-player, per-receipt reward objects. A newly cached deferred object can exist with zero cards; the concrete choices arrive at first reveal.

The potion case still uses `OwnerFinalApRngStrategyId`. Relic reservation, gold grants, and APWorld item definitions were not redesigned by this commit.

### 6. Preparing one card offer

Source: [ApMirroredRewardDispatcher.cs](../../client/StS2AP/Utils/Rewards/ApMirroredRewardDispatcher.cs), `ApNativeCardReward.PrepareCards`.

This is the central changed method. Read its blocks in this order.

#### 6.1 Check the contract and capture context

```csharp
if (MaterializationStrategyId != ReplicatedCardStrategyId || AppliedEffects.Count != 0)
    throw new InvalidOperationException("AP card offer uses a previous generation contract; start a new run.");
bool hasAssignment = _cards.Count > 0;
var run = Player.RunState;
```

The guard rejects the previous protocol, rather than attempting conversion. `hasAssignment` chooses between first generation and refreshing an existing offer. Capturing `run` lets the method detect a run change after an asynchronous wait.

The next block builds a temporary spec from the receipt identity, player, rarity, assigned act, reveal/reroll state, strategy, and current serialized cards. It will describe the prepared offer once preparation completes.

#### 6.2 Reserve message identities before awaiting

In multiplayer, the method reserves two choice IDs:

1. `verificationChoice`, for the digest message.
2. `_firstPickerChoice`, for the first native picker choice.

Previously there were three reservations: metadata, transferred cards, and picker choice. The separate card-transfer reservation is removed.

A choice ID is a correlation identifier in the native synchronization system. It is not an AP receipt, a card index, or a relic use. Reserving these IDs before any awaited callback means asynchronous work cannot move the first picker's reservation to a different relative position on different replicas.

The existing [Patches_APCardRevealChoice.cs](../../client/StS2AP/Patches/Rewards/Patches_APCardRevealChoice.cs) binds that reserved picker ID to the native picker's first reservation. It restores its temporary scope as soon as native selection returns its task; later native choices reserve normally.

#### 6.3 Prepare on every machine

```csharp
string before = CaptureGenerationState(Player);
List<CardCreationResult> preparedCards = hasAssignment
    ? RefreshCardChoices(this)
    : await GenerateCardChoices(spec, Player);
```

The old `if (LocalContext.IsMe(Player))` wrapped generation. It no longer does. Owner and remote replicas execute this block for the same reward player.

After preparation, the method checks that the current run is still the captured run. It marks the spec revealed and serializes the resulting cards into it.

#### 6.4 Compare with the owner

`ApCardRevealCodec.Encode` receives the prepared spec, player state before and after preparation, and `firstReveal: !hasAssignment`.

The owner calls `SyncLocalChoice` with the digest represented as integer indexes. A remote replica waits for that choice, with a 15-second timeout, checks the run again, and calls `Verify` with both digests.

The owner **does not wait for acknowledgments from every replica**. A replica verifies before proceeding with its own picker execution. This is not a distributed transaction in which everyone votes before any machine can advance.

Also, native generation callbacks have already run by the time verification happens. A mismatch prevents the disagreeing replica from continuing to its picker, but does not roll back native side effects already performed. The code treats the mismatch as a failed operation and invalidates AP claims instead of retrying or rerolling.

#### 6.5 Install and publish the prepared assignment

The method replaces `_cards` with the prepared `CardCreationResult` objects, updates configuration, and calls `PublishRevealedAssignment`.

The resulting log is:

```text
Prepared replicated AP card offer ... for player ... (firstReveal=..., localOwner=...)
```

On failure inside the preparation block, the catch logs the receipt/player and exception, invalidates multiplayer claims, and rethrows. The earlier contract guard is outside that try block; the outer ordered-selection observer still handles its failure in multiplayer.

### 7. Running the native generation hooks

#### 7.1 `GenerateCardChoices`: preserve native result objects

Source: [ApMirroredRewardDispatcher.cs](../../client/StS2AP/Utils/Rewards/ApMirroredRewardDispatcher.cs).

The return type changes from `List<CardModel>` to `List<CardCreationResult>`. A creation result holds the card together with information about modifications made during generation. Keeping these objects preserves native modifier provenance in memory instead of discarding it during a serialization/reload cycle.

The method still constructs AP reward options and a receipt-local RNG. Its generation sequence is:

```text
enter AP RNG scope and assigned-act scope
  temporarily defer the factory's final option-modification dispatch
    CardFactory.CreateForReward(player, 3, options)
  leave the temporary deferral
  Hook.TryModifyCardRewardOptions(..., out modifiers)
leave RNG and assigned-act scopes
if any modification occurred:
  await Hook.AfterModifyingCardRewardOptions(..., modifiers)
return native CardCreationResult objects
```

The requested initial count is three. Native hooks can change the final offer size; the domain does not require the final list to contain exactly three cards.

The explicit awaiting of post-modification callbacks existed before this commit. We retain it while broadening which native hooks can execute. It ensures the “after” fingerprint and assignment publication happen after these callbacks finish.

#### 7.2 Why the temporary deferral remains

Source: [Patches_APCardRewardUpgradeOdds.cs](../../client/StS2AP/Patches/Rewards/Patches_APCardRewardUpgradeOdds.cs).

The factory's ordinary path does not provide our caller an awaited completion boundary for its post-modification callbacks. Our scoped patch therefore temporarily makes its `TryModifyCardRewardOptions` call report no modification. Then `GenerateCardChoices` invokes that dispatch explicitly and awaits the follow-up callbacks.

For a Harmony prefix, `return false` means “skip the original method”; assigning `__result = false` supplies the method's boolean result. Those are separate operations.

This suppression applies only while `RunDeferringOptionHooks` has set its temporary flag. The explicit call afterward happens with deferral off, so native hooks run. The intention is one modification pass with an awaited completion boundary, not permanent suppression of relic effects.

The RNG and act scopes use thread-local fields and `IDisposable` restoration. Their lifetime ends before the asynchronous post-callback await. This prevents the scope leaking into unrelated work while the task is suspended. It also means we cannot claim that every random call made by an arbitrary asynchronous callback automatically uses the AP RNG.

#### 7.3 Remove the hook allowlist

The commit deletes `SupportedApHookTypes` and `ShouldRunHook`. Previously the patch manually iterated listeners and ran only explicitly listed model types. That list included Eggs, Tress, Fresnel Lens, Glitter, Prismatic Gem, Dingy Rug, and several other known types.

The manually filtered early/late final-option loops are removed. Outside the short deferral scope, the prefix now returns true so MegaCrit's original dispatch runs.

This is the central reduction in maintenance: a newly added hook is no longer automatically excluded just because its type is absent from our list. Its behavior still has to satisfy the state, ordering, and RNG assumptions of replicated execution.

#### 7.4 Preserve the RNG after native pool hooks

`FilterApCardCreationOptionHooks` is replaced by `PreserveApRewardRng`.

The old prefix bypassed native dispatch and manually ran allowed creation-option hooks. The replacement is a postfix: native creation-option hooks run first, and then the patch reattaches `s_apRewardRng` to the returned options if an AP generation scope is active.

This allows pool changes through the native path while keeping their resulting options tied to this receipt's RNG. Outside the AP scope, the postfix does nothing.

The existing `PropagateApRewardRng` prefix also remains. It attaches the active RNG to nested `CardFactory.CreateForReward` calls, such as nested generation initiated by Lasting Candy.

#### 7.5 Let upgrade-odds hooks run

`OverrideAssignedActUpgradeOdds` still substitutes the AP reward's assigned-act base odds for a non-rare card. It uses the existing scaling: `0.25` per act index, or `0.125` under Scarcity.

Previously, during AP generation, it could return those odds directly and skip the original hook dispatcher. That suppression is removed. The prefix now updates the incoming base value when appropriate and returns true, allowing native modifiers to act on it. Rare cards and calls without an assigned-act override also proceed through the native dispatcher.

#### 7.6 Let alternatives use native dispatch

`FilterApCardRewardAlternatives` is deleted. It previously filtered alternatives through the same allowlist. The native alternative lifecycle is now allowed to run without that filter.

This does not mean that every alternative is applied once at generation. Alternatives belong to the native picker lifecycle. The “first reveal only” rule specifically prevents rerunning broad card-generation modifications when an already-generated offer is reopened.

The removed `using` directives for card alternatives and modifiers are ordinary cleanup after deleting those implementations. Comments throughout the patch are updated to describe replicated rather than reviewed owner-only generation.

### 8. Reopening and the Egg exception

Source: `RefreshCardChoices` in [ApMirroredRewardDispatcher.cs](../../client/StS2AP/Utils/Rewards/ApMirroredRewardDispatcher.cs). Existing supporting files: [ApCardRewardLifecycle.cs](../../client/StS2AP/Utils/Rewards/ApCardRewardLifecycle.cs) and [ApDeferredCardReward.cs](../../client/StS2AP/Utils/Rewards/ApDeferredCardReward.cs).

`RefreshCardChoices` now copies the list of existing `CardCreationResult` objects, calls `RefreshEggUpgrades`, and returns it. Previously it stripped the results down to cards and sometimes normalized them through serialization/reloading.

The existing Egg helper filters to unupgraded cards and invokes the native late hook for `MoltenEgg`, `ToxicEgg`, and `FrozenEgg` found on the player. Native eligibility checks still decide whether a particular card is affected. Filtering already-upgraded cards prevents repeated reopen from incrementally upgrading a modded card with multiple upgrade levels.

Therefore the intended sequence is:

```text
reveal receipt 81 -> generate once, including applicable first-reveal effects
skip             -> keep that assignment; do not consume receipt 81
obtain Toxic Egg -> no broad regeneration of the frozen offer
reopen 81        -> refresh eligible unupgraded skills with the native Egg hook
skip/reopen      -> same identities and order, no new generation pass
claim            -> grant the displayed result; consume once
```

Fresnel Lens and Glitter participate at first generation. Obtaining them after that first reveal does not trigger their generation effects on this existing offer. Prismatic Gem and Dingy Rug can affect the pool at first reveal; obtaining them afterward does not reroll an existing assignment's identities. These are intended semantics, with runtime verification still outstanding.

**Why `Freeze` is still necessary:** the deferred reward constructor detaches `reward.OnRelicObtained` from `player.RelicObtained`. Otherwise the native reward could react broadly to relic acquisition outside our chosen reveal/refresh boundary. Freeze does not strip relics from the player or disable explicit factory hooks; it removes that event subscription. First-generation hooks still execute when we explicitly generate, and the Egg helper explicitly refreshes on reopen.

Freeze and the Egg-only selection policy were already implemented in the parent commit. They were not newly introduced by this experiment.

### 9. Comparing independently generated offers

Sources: [ApCardRevealCodec.cs](../../client/StS2AP/DomainAdapters/ApCardRevealCodec.cs), and `CaptureGenerationState` in the dispatcher.

#### 9.1 What goes into the fingerprint

`CaptureGenerationState` calls `Player.ToSerializable()` and serializes these selected native gameplay fields:

| Fields | Reason for including them |
| --- | --- |
| Character ID and `NetId` | Identify the replicated player |
| Current/max HP, max energy, potion slots, orb slots, gold | Detect covered gameplay-state disagreement |
| Deck, relics, potions | Include serialized objects and saved relic properties |
| RNG and odds | Detect differences in the represented random-state machinery |
| Relic grab bag, extra fields, unlock state | Include additional serialized gameplay inputs/state |

Local discovery lists are deliberately excluded because local UI bookkeeping can differ. Arbitrary external mod state, transient unsaved fields, all other players, and all shared run state are not comprehensively captured by this selected serialization.

`Encode` adds receipt and offer information: AP slot, received item index, owner `NetId`, rare flag, assigned act, reroll flag, first-reveal flag, and ordered serialized cards. It includes the player-state snapshot both before and after preparation.

Comparing “before” helps catch different starting conditions even if the final cards happen to match. Comparing “after” helps catch divergent side effects even if both factories picked the same cards. These are generic comparisons of serialized state, not instructions such as “set Tress from unused to used.”

#### 9.2 `Encode`, block by block

1. Reject anything that is not a revealed, nonempty replicated card offer, or that carries effect-replay records.
2. Run normal domain decoding for the spec's remaining invariants.
3. Parse the card and player-state JSON into JSON values rather than hashing raw JSON strings directly.
4. Write those values canonically to a memory stream.
5. Compute SHA-256 over the resulting bytes.
6. Emit version `2`, followed by eight 32-bit integers read from the digest using an explicit little-endian interpretation.

The payload is nine integers: one version plus 256 bits of digest. Representing the hash as integers lets the existing `PlayerChoiceResult.FromIndexes` transport carry it. These values do not represent card choices, even though they use the same integer-list container.

The protocol version here is 2; the containing menu schema is 8. They version different structures.

#### 9.3 `WriteCanonical`: object order versus gameplay order

The recursive helper sorts JSON **object properties** by ordinal name. These are equivalent:

```json
{"id":"CARD.A","upgrade":1}
{"upgrade":1,"id":"CARD.A"}
```

It preserves **array order**. `[A, B]` and `[B, A]` must differ because picker index zero would select a different card. Preserving arrays also avoids hiding a different ordering of relics or deck entries.

Other JSON values are written directly. This is canonicalization for the serialization shapes we use, not a universal semantic equivalence algorithm for every possible JSON number representation.

#### 9.4 `Verify`: reject, do not mutate

`Verify` checks that both payloads have nine integers, both use version 2, and the sequences are identical. Otherwise it throws:

```text
Replicated AP card offer ... disagreed with the owner
(receipt, cards, or native player state). No picker choice was applied.
```

It does not reveal the exact differing field, download replacement cards, apply relic transitions, or roll anything back. It is a compact disagreement detector. Debugging a runtime mismatch would need targeted additional evidence.

The old `DecodeInto` method is removed. It decoded receipt metadata plus Tress/Crucible effect instructions, validated transitions, and mutated a replica spec. None of those instructions belongs in this new reveal protocol.

### 10. Ordering asynchronous selections

New file: [ApRewardSelectionQueue.cs](../../client/StS2AP/Utils/Rewards/ApRewardSelectionQueue.cs).

This small class has two fields:

- `_tail`: a task representing completion of the most recently enqueued selection.
- `_failure`: the first exception that invalidated this queue.

It assumes calls on the game main thread and preserves the synchronization context across awaits. It is not a lock-based queue for arbitrary worker-thread producers.

#### 10.1 `Run`

```csharp
Task previous = _tail;
var completion = new TaskCompletionSource();
_tail = completion.Task;
return Execute(previous, select, completion);
```

Each new selection remembers the old tail and installs its own completion marker immediately. The following selection will therefore wait for this one, even if this one has not reached its actual native action yet.

The caller receives the result of `Execute`, which eventually contains the native `bool`: true for successful application, false for an expected non-consumption such as skipping.

#### 10.2 `Execute`

It awaits the preceding marker, checks whether a previous selection failed, and then awaits `select()` through completion.

If an exception occurs, `_failure ??= ex` remembers the first failure and the exception is rethrown. In `finally`, `completion.SetResult()` releases later waiters.

Why release later waiters after failure? So they can wake up, see `_failure`, and fail without executing their actions. The completion marker means “the earlier operation has finished,” not “it succeeded.” This avoids leaving them permanently suspended.

A returned false is not an exception. Skipping finishes normally and lets the next queued selection execute.

#### 10.3 `WhenIdle`

This awaits the current tail and then rejects an earlier queue failure. It is used when a remote menu is being reconstructed.

Strictly, it waits for the tail captured when called. It is not a global barrier against all future enqueues or unrelated game actions. Its use here relies on the surrounding native menu/selection lifecycle.

### 11. Connecting the queue to the game

New file: [Patches_APRewardSelectionOrder.cs](../../client/StS2AP/Patches/Rewards/Patches_APRewardSelectionOrder.cs).

#### 11.1 Scope the Harmony prefix

The patch targets `Reward.SelectUnsynchronized`. It returns true and lets the original execute immediately when:

- this is not a real multiplayer run;
- the reward does not implement the dispatcher's `IApNativeReward` interface; or
- this is the immediate recursive invocation used to enter the original method from the queue.

Despite the word “Unsynchronized,” this is a native execution entry point used by the surrounding synchronized reward machinery. The patch does not invent a second reward-message protocol.

For an AP multiplayer reward, the prefix assigns `__result` to the queued task and returns false. The original call is skipped **at that moment**; the queue's delegate calls the native method when it is that reward's turn.

#### 11.2 One queue per player

`ConditionalWeakTable<Player, ApRewardSelectionQueue>` associates a queue with each native player object without making that player permanently live through an ordinary strong-key dictionary. Runtime ordering is per player; persistent reward identity still uses the existing `NetId` and received item index.

Before starting the queued action, the delegate checks that its captured run is still active and AP claims have not already been invalidated. Stale work throws instead of acting in a new run.

#### 11.3 Avoid queuing the same call recursively

Calling `reward.SelectUnsynchronized()` inside its own prefix would ordinarily hit the prefix again and queue itself forever.

The thread-local `s_entering` field marks only this immediate invocation. The inner call sees the same reward reference and proceeds to the original. A `finally` restores the previous marker as soon as the call returns its task; it does not leave the bypass enabled while the asynchronous task is unfinished.

Native nested rewards do not implement the AP interface and stay outside this queue. That lets a parent AP relic action wait for its native nested choice to finish without putting the child behind the parent in the same queue. This is a scoped solution for native nested rewards, not a general promise about arbitrary recursively created AP rewards.

#### 11.4 Observe failure and reset at run boundaries

`ObserveSelection` awaits the queued task. On exception, it invalidates claims if the reward still belongs to the current run, logs `Ordered AP reward selection failed for player ...`, and rethrows.

The dispatcher's `EndRun` now calls `Patches_APRewardSelectionOrder.Reset()`, which clears the table. A new run does not inherit a failed queue. Clearing the table is not cancellation of every existing task; the run checks guard queued work as it resumes.

#### 11.5 Wait before reconstructing a remote menu

In the remote menu path, after locating the owning player, the dispatcher now awaits `Patches_APRewardSelectionOrder.WhenIdle(owner)` and checks the run again. Only then does it decode/validate the menu and build the next native reward set.

This covers a fast sequence such as reveal, skip, close list, reopen list while a remote replica is still finishing the earlier selection. Without the wait, reconstruction could compare the owner's newly published assignment against a replica that has not installed its preceding result yet.

The previous `ApplyOwnerFinalEffects(rewards, owner)` call at this boundary is removed. Menu reconstruction no longer replays those manually encoded relic transitions.

### 12. What reaches the host and what gets saved

There are three distinct communications. Keeping them separate makes the lifecycle easier to follow:

| Communication | Purpose |
| --- | --- |
| AP reward menu spec | Establish matching reward rows, recipes, and existing assignments |
| Native choice carrying the digest | Check independently prepared offers during this picker opening |
| AP progress snapshot/delta | Persist the owner's assignments and consumption in replicated run data |

`PublishRevealedAssignment` is existing infrastructure retained by this commit. Each machine updates its in-memory `ReplicaCardAssignments[(Player.NetId, itemIndex)]`. Only the local owner then writes `ArchipelagoClient.Progress.CardAssignments` and calls `ApRunData.PublishLocalProgress`.

In [ApRunData.cs](../../client/StS2AP/Multiplayer/ApRunData.cs), `PublishLocalProgress` constructs a full baseline initially, then revisioned deltas. A non-host owner sends this to the host. The receiving handlers check ownership, run identity, and revision/baseline conditions before storing accepted progress. The host rebroadcasts accepted progress. If the owner is the host, it updates its run data and broadcasts from there.

That mechanism was not replaced by the digest. **Serialized final cards still exist in saves/progress and in menus carrying existing assignments.** What disappeared is the dedicated owner-to-replica mutable-card transfer during each reveal.

A successful client `PublishLocalProgress` call means the local publication/send path succeeded; it is not a synchronous acknowledgment that the host has durably written a checkpoint. Durable saves still use the existing campaign/checkpoint system. Likewise, the reveal digest is not an all-peer save confirmation.

After native selection returns false, the owner retains the assignment and publishes progress as needed. On success, the established `OnSelect` path removes the replica's pending assignment and the owner reaches `CommitDiscreteReward`. This experiment does not redefine receiving an AP item as consuming it.

### 13. Removed machinery and remaining special cases

#### 13.1 Removed from the dispatcher

`GenerateCardChoices` no longer captures `SilkenTress.IsUsedUp` and `SilverCrucible.TimesUsed` before and after callbacks, builds `RewardEffect` transitions, or puts those transitions into `AppliedEffects`.

`ApplyOwnerFinalEffects` is deleted. It used to find those relics on the remote player, validate the recorded transition against local state, invoke callbacks to reproduce it, and check the expected resulting counter. Every replica now executes native generation callbacks directly.

`NormalizeCardChoices` is deleted. It previously tracked existing cards in the run, serialized final choices, removed temporary generated/cloned cards, and loaded the serialized finals back into the run. That made the owner's representation resemble replicas receiving cards. With local generation on every replica, the method returns and keeps native creation results instead. Existing serialization-based restoration for saved assignments remains.

The owner-only reveal branch, `FromMutableCards` send, and `AsMutableCards` receive branch are removed from `PrepareCards`. They are replaced by local preparation on all machines and the digest branch described above.

#### 13.2 Still deliberately custom

- **Egg refresh on reopen:** explicitly limited to the three Eggs, preserving the requested semantics.
- **Assigned-act upgrade base odds:** a pending AP reward keeps its assigned-act meaning.
- **Fixed AP rarity odds:** the existing patch uses 57/37/6 common/uncommon/rare, or 60/37/3 under Scarcity; rare AP rewards remain rare, subject to the existing allowed-rarity handling.
- **Wing Charm RNG routing:** its scoped patch replaces its native Niche RNG selection with the AP reward RNG while retaining its card-enchantment behavior.
- **AP character/pool integration:** the previously integrated Prismatic/Colorful pool work remains in the branch; this commit did not replace that separate compatibility work.
- **Native callback completion and choice-ID binding:** AP still needs an explicit preparation boundary before handing cards to the picker.

We therefore reduced the need for per-relic state replay; we did not eliminate all AP-specific semantics. A future hook using unsynchronized state, a separate RNG source, local-only information, or shared run-wide mutation may still need investigation.

Silver Crucible is excluded from ordinary native multiplayer. Its removal from the replay code is real, but it is not a normal multiplayer test scenario. Driftwood is explicitly disallowed by the mod; preserving the native reroll plumbing does not enable it.

## Part III: Tests, limits, and file inventory

### 14. Test changes one by one

#### 14.1 `ApCardRevealTests.cs`

Source: [ApCardRevealTests.cs](../../client/StS2AP.RegressionTests/ApCardRevealTests.cs).

The fixtures now use the replicated strategy and opaque card/player JSON. They are deliberately not fake implementations of the native relics.

1. **Twelve unopened rewards:** round-trip a menu containing empty recipes. They remain deferred, have no effects, and cannot be decoded as completed saved assignments. This covers data representation, not opening twelve real game rows.
2. **Independent offers agree:** construct separate owner/replica fixtures, reorder JSON object properties, and confirm equal digests. Assert the nine-integer payload and empty effect records.
3. **Disagreement rejection:** change first-reveal status, receipt, owner, assigned act, slot, rarity, card order, card identity, enchantment, reroll status, initial state, final counter state, or RNG state. Each change must fail verification. The fixture remains unchanged by `Verify`; this does not claim native effects before verification are rolled back.
4. **Incomplete/invalid protocol rejection:** reject an unrevealed recipe, old generation strategy, replicated offer with effect instructions, truncated digest, and old digest version.
5. **Old menu schemas:** reject versions 5, 6, and now 7 before native choices are started.
6. **Refreshed assignment through save/delta:** manually change an upgrade in the opaque fixture, verify it as a reopen, round-trip an `ApProgressDelta`, restore the assignment, and confirm no receipt consumption or replay instructions were added. This tests the persistence plumbing, not the actual Egg hook.

The old reveal-transfer tests and Crucible replay-order test were removed/replaced because the runtime protocol no longer transmits those instructions. The new tests do not establish native factory determinism.

#### 14.2 `ApRewardSelectionQueueTests.cs`

New source: [ApRewardSelectionQueueTests.cs](../../client/StS2AP.RegressionTests/ApRewardSelectionQueueTests.cs).

Five tests exercise the production queue with controllable tasks:

1. A delayed relic grant finishes before a following reveal reads the relic state.
2. Separate owner/replica queues preserve the invocation sequence `81, 80, 81, 2`, even when the replica starts behind. They do not sort receipts.
3. A false result lets later work proceed, whereas an exception prevents the following delegate from running.
4. `WhenIdle` waits for an outstanding selection before allowing a simulated reopened menu to proceed.
5. One player's waiting queue does not block another player's queue.

These tests avoid timing sleeps by explicitly releasing a `TaskCompletionSource`. They test queue mechanics, not Harmony interception or real network delivery order.

#### 14.3 F# domain tests

In [MirroredRewardTests.fs](../../client/StS2AP.Domain.Tests/MirroredRewardTests.fs), deferred fixtures switch to the new strategy; old strategies are added to invalid deferred cases. A new test rejects unrevealed replicated final cards, accepts revealed ones, and rejects the replicated strategy for potions. Existing `Match` calls gain the third callback.

In [RewardMaterializationTests.fs](../../client/StS2AP.Domain.Tests/RewardMaterializationTests.fs), the list of known strategy IDs gains the new ID. The selected-handler test includes the third case and checks that unselected callbacks are not invoked. The round-trip property covers all three distinct strategies.

Pre-existing historical decoding tests remain. Their presence is not new old-save migration behavior.

#### 14.4 Smaller test and project edits

- [MirroredRewardAdapterTests.cs](../../client/StS2AP.RegressionTests/MirroredRewardAdapterTests.cs): update two JSON schema fixtures from 7 to 8 and add the third `Match` handler. Some fixtures intentionally retain historical strategy data to exercise existing decoding boundaries.
- [RewardMaterializationInteropTests.cs](../../client/StS2AP.RegressionTests/RewardMaterializationInteropTests.cs): add a third callback that throws if incorrectly selected while testing the owner-final case.
- [PackagingTests.cs](../../client/StS2AP.RegressionTests/PackagingTests.cs): change the schema fixture to 8. This changes a test input, not the packaging mechanism.
- [StS2AP.RegressionTests.csproj](../../client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj): link the production `ApRewardSelectionQueue.cs` into the test assembly. Tests execute that implementation directly without requiring the game client to load.

#### 14.5 Documentation edits in the implementation commit

The [regression-test README](../../client/StS2AP.RegressionTests/README.md) now describes digest and queue coverage, distinguishes helper tests from game integration, and lists reveal/reopen/delayed-peer scenarios and diagnostic messages. It records the new-run requirement and absence of an all-peer acknowledgment barrier.

The existing [mirrored-reward-domain.md](mirrored-reward-domain.md) gets a notice explaining that its owner-final description is historical and superseded on this experimental branch. The body remains a historical account; it was not silently rewritten to describe the new implementation.

### 15. Validation and an in-game checklist

The following were completed during implementation of `24bd923`; this documentation-only follow-up does not constitute another runtime validation pass:

| Validation layer | Result recorded for the implementation |
| --- | --- |
| DLL-only compilation against 0.107.1 | Passed, with existing Godot generator/type-conflict warnings |
| DLL-only compilation against 0.111.0 | Passed, with existing Godot generator/type-conflict warnings |
| C# regression tests | 216 passed; 2 packaging tests skipped |
| F# domain tests | 67 passed |
| Diff whitespace/source checks | Passed |
| In-game singleplayer and two-client behavior | NOT RUN |

Compilation checks API compatibility, and helper tests check the specific contracts above. Neither proves that the mod loads, the hook timing is correct in the installed game, or the native player snapshots agree at runtime.

When game access is available, use fresh runs and matching game/mod builds. A small focused matrix is:

| Scenario | What to observe |
| --- | --- |
| Open a backlog while holding unused Silken Tress | Merely opening the list does not consume it |
| Reveal 81 before 80, then skip/reopen 81 | Replicas agree; generation effects happen on the appropriate first reveal and do not repeat on reopen |
| Reveal, skip, obtain each relevant Egg, reopen | Eligible previously unupgraded choices gain upgrades; card identity/order and other first-reveal effects remain stable |
| Acquire Fresnel Lens/Glitter before versus after first reveal | Apply at first generation when present; do not newly apply on reopen of an existing offer |
| Acquire Prismatic Gem/Dingy Rug before first reveal | First generation uses the intended pool; later reopen preserves the assignment |
| Delay a peer during AP relic grant, immediate reveal, skip, close/reopen | Subsequent AP work and menu reconstruction wait for preceding completion |
| Use keyboard/controller, skip repeatedly, then claim | Native picker controls function and receipt consumption occurs once |
| Save/continue a run created with this build | Revealed assignments survive without regenerating or replaying generation effects |
| Cause a controlled covered-state mismatch | Disagreeing replica reports the digest failure and stops claims; no automatic repair is attempted |

Compare both replicas' `Prepared replicated AP card offer` logs, including `firstReveal` and `localOwner`. Also inspect the displayed offer, actual deck result, relic state, and save continuation. A matching log line alone is not proof of all of those behaviors.

### 16. Complete changed-file inventory

This is the full file list for the implementation commit, including new files. Section references point to the detailed explanation above.

| File | Where explained |
| --- | --- |
| `client/StS2AP.Domain/RewardMaterialization.fs` | 4.1 |
| `client/StS2AP.Domain/MirroredReward.fs` | 4.2 |
| `client/StS2AP/Models/Rewards/Specs/ApMirroredRewardSpec.cs` | 4.3 |
| `client/StS2AP/Models/Rewards/Specs/ApRewardMenuSpec.cs` | 4.3 |
| `client/StS2AP/Utils/Rewards/ApMirroredRewardDispatcher.cs` | 5–8, 11.4–13.1 |
| `client/StS2AP/Patches/Rewards/Patches_APCardRewardUpgradeOdds.cs` | 7, 13.2 |
| `client/StS2AP/DomainAdapters/ApCardRevealCodec.cs` | 9 |
| `client/StS2AP/Utils/Rewards/ApRewardSelectionQueue.cs` (new) | 10 |
| `client/StS2AP/Patches/Rewards/Patches_APRewardSelectionOrder.cs` (new) | 11 |
| `client/StS2AP.RegressionTests/ApCardRevealTests.cs` | 14.1 |
| `client/StS2AP.RegressionTests/ApRewardSelectionQueueTests.cs` (new) | 14.2 |
| `client/StS2AP.Domain.Tests/MirroredRewardTests.fs` | 14.3 |
| `client/StS2AP.Domain.Tests/RewardMaterializationTests.fs` | 14.3 |
| `client/StS2AP.RegressionTests/MirroredRewardAdapterTests.cs` | 14.4 |
| `client/StS2AP.RegressionTests/RewardMaterializationInteropTests.cs` | 14.4 |
| `client/StS2AP.RegressionTests/PackagingTests.cs` | 14.4 |
| `client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj` | 14.4 |
| `client/StS2AP.RegressionTests/README.md` | 14.5 |
| `docs/design/mirrored-reward-domain.md` | 14.5 |

For later reading: resume at section 6 for the reveal call, section 7 for the native hook boundary, section 8 for Freeze/Eggs, section 10 for ordering, or section 12 for host storage.
