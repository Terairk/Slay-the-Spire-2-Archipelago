# Multiplayer implementation roadmap

- **Status:** Draft
- **Depends on:** [Multiplayer synchronization RFC](multiplayer-sync-rfc.md)

This roadmap favors narrow, observable vertical slices. A phase is complete
only when its stated two-client evidence exists; compilation or source review
alone is not runtime proof.

## Phase 0: Multiplayer API spike

### Objective

Prove the minimum supported path without converting AP features.

### Tasks

- Start an unmodified two-player run with the mod loaded on both peers behind a
  development-only entry point.
- Resolve the local player through `LocalContext` on host and client.
- Record net game type, local Net ID, all player Net IDs, and run location.
- Prove a standard out-of-combat gold grant can follow MegaCrit's local apply +
  `RewardSynchronizer.SyncLocalObtainedGold` pattern.
- Prove both peers observe the same resulting gold.
- Capture the behavior on peer disconnect and rejoin.
- Prove one RitsuLib managed `NonCombat` action and one managed `Combat` action,
  including requester-local execution, host ordering, and executor failure.
- Prove RitsuLib `RunSavedData` and `PlayerRunSavedData` lobby contributions
  arrive before Ready validation and commit into the launched run.
- Verify the host Ascension set can overwrite and validate every client's local
  calculated set while retaining visible mismatch diagnostics.

### Exit evidence

- Host and client logs identifying the same players with opposite local owner.
- A 25-gold test grant applied exactly once on both copies of the owner.
- No combat divergence or duplicate grant after reconnect.
- Managed-action and lobby-staging behavior recorded against the RFC contracts.

## Phase 1: Local AP player ownership

### Objective

Remove ambiguous single-player ownership without changing normal singleplayer
behavior.

### Tasks

- Introduce a local AP run context resolved from `LocalContext`.
- Stop assigning AP ownership from `Players[0]` or every
  `Player.CreateForNewRun` callback.
- Classify all uses of `GameUtility.CurrentPlayer` as local presentation,
  owner-only AP action, or replicated game mutation.
- Ensure one AP session and progress object belong to the local STS player.
- Preserve main-thread item processing.

### Exit evidence

- Existing singleplayer source behavior remains intact.
- In a two-client run, each process binds its AP context to its own player.
- Remote player creation does not reset local AP progress or send Press Start.

## Phase 2: Simple out-of-combat grants

### Objective

Synchronize the lowest-risk received items through existing MegaCrit APIs.

### Tasks

- Gold via `SyncLocalObtainedGold`.
- Relics via `SyncLocalObtainedRelic`.
- Potions via `SyncLocalObtainedPotion`, preserving the no-slot retry contract.
- Already-selected cards via `SyncLocalObtainedCard` where semantically valid.
- Add owner-side duplicate tests keyed by AP slot ID plus received-item index.
- Document failure behavior for AP disconnect during a grant.

### Exit evidence

- Each item is applied exactly once to the owner on both peers.
- Reopening the AP menu does not duplicate an applied grant.
- Save/load and reconnect preserve the result.

## Phase 3: AP locations in native reward sets

### Objective

Make combat reward replacement participate in MegaCrit's reward lifecycle.

### Tasks

- Register an AP location reward through RitsuLib.
- Define a primitive save payload for owner, location, and presentation data.
- Establish how the AP reward spec reaches remote peers before set creation.
- Construct identical reward sets on host and client.
- Gate AP check sending and AP progress mutation to the owner.
- Verify nested and sequential reward-set IDs remain aligned.
- Cover card, gold, potion, rare-card, and boss reward replacement.

### Exit evidence

- Both peers log the same owner, reward-set ID, reward order, and selected index.
- Only the owner sends the AP check.
- The original vanilla reward is not granted on either peer.
- Closing/reopening and save/load preserve the same custom reward.

## Phase 4: Received choices

### Objective

Handle card and relic choices without private MegaCrit RNG or pool divergence.

### Tasks

- Convert received card selection away from `SelectUnsynchronized`.
- Decide whether linked relic choices use native choices or synchronize only
  the final selected relic.
- Isolate private candidate generation from replicated RNG and relic bags, or
  make all peers execute the same generation.
- Preserve stable assignments by AP slot ID plus received-item index.
- Verify card-selection choice IDs advance identically.

### Exit evidence

- The owner sees the intended candidates after reopen and reload.
- Remote peers apply the selected result without seeing private UI when that is
  the chosen design.
- Player-choice IDs and resulting deck/relic state match.

## Phase 5: Shops, rest sites, and Ancients

### Objective

Synchronize the consequences of AP capabilities without copying unnecessary AP
state.

### Tasks

- Keep shop slot capabilities local.
- Add MegaCrit gold-loss synchronization to AP shop purchases.
- Confirm AP fake inventory entries never become remote obtained rewards.
- Define and transport `ApRestSiteSpec` before rest-site list construction.
- Apply the AP transform after deterministic vanilla/model option generation.
- Gate campfire check sending to the owner.
- Define the corresponding native-event spec for Start-of-Act Ancient choices.
- Use final-result relic synchronization for private Anytime choices if ADR
  review selects that design.

### Exit evidence

- Dynamic Dig, Kindle, Cook, Lift, Clone, Hatch, Mend, and AP options have the
  same order for the same owner on both peers.
- Selecting every option produces the same operation on both peers.
- AP checks are sent by the owner only.
- Shop purchases synchronize gold loss and any concrete standard reward.

## Phase 6: Progressive starters, ascension effects, and combat buffs

### Objective

Convert effects that do not fit the standard reward transport.

### Tasks

- Synchronize concrete Progressive Starter deck/relic transitions through the
  native path where available and a managed action where it is not.
- Initialize from the host Ascension set and route each later Ascension Down as
  a concrete managed noncombat removal at the next safe boundary.
- Register the host-ordered RitsuLib combat-buff action with `ApGrantId`.
- Persist one FIFO per AP owner and submit at most its head at combat start.
- Apply at most one universal buff per player per combat through the synchronized
  action, with receipts during combat deferred to the next combat.
- Define Death Link ownership, incoming effect synchronization, and
  feedback-loop suppression.
- Implement the single applied-grant set, assignment cache, owner-only AP
  acknowledgment, and crash-window diagnostics from the RFC.

### Exit evidence

- Starter decks/relics and ascension behavior match on both peers.
- Every universal combat buff appears once on both copies of its owner.
- Death Link produces one agreed effect without an AP feedback loop.
- Post-action checksums match across repeated and reconnected scenarios.

## Phase 7: Saves, reconnect, compatibility, and release hardening

### Objective

Make the supported topology durable rather than demo-only.

### Tasks

- Add the RitsuLib lobby protocol contribution and refuse incompatible peers.
- Block Ready until local AP slot data and received-item history are complete.
- Force the host effective Ascension set while surfacing client mismatches.
- Persist pending buffs, concrete assignments, and the applied-grant set in the
  equivalent `SerializableAP` fields.
- Restore active AP-derived reward/option conversations on rejoin.
- Verify host and client save ownership.
- Add targeted diagnostics for owner, transaction, run location, and sequence
  IDs without logging credentials.
- Document unsupported mixed-version and missing-mod parties.
- Keep singleplayer on the same feature pathways where possible.

### Exit evidence

- The complete validation matrix passes on the supported public game build.
- No singleplayer regression is observed in the same scenarios.
- Multiplayer limitations and required mod versions are documented.

## Two-client validation matrix

Run each relevant row with both host ownership assignments when possible.

| Scenario | Host | Client | Expected evidence |
|---|---|---|---|
| Local ownership | AP Alice | AP Bob | Each process resolves itself as local and the other as remote. |
| Gold grant | Recipient | Observer | Both copies of recipient gain the same amount once. |
| Relic grant | Observer | Recipient | Both copies obtain the same relic once. |
| Potion with slot | Recipient | Observer | Both copies obtain the potion. |
| Potion without slot | Recipient | Observer | No consumption; reward remains claimable. |
| Card select | Observer | Recipient | One UI, one synchronized choice, matching decks. |
| AP combat location | Recipient | Observer | Same reward order; owner-only check. |
| Nested reward | Recipient | Observer | Matching reward-set IDs before and after nesting. |
| Rest with Shovel/Candle/Cleaver | Mixed relic ownership | Mixed | Same per-owner option order on both peers. |
| Six campfire checks | Recipient | Observer | Same AP option count/order and selected operation. |
| Shop AP purchase | Recipient | Observer | Owner-only check, matching gold loss. |
| Ancient native choice | Recipient | Observer | Same event options and selected relic. |
| Lobby protocol mismatch | Host | Client | Ready/run launch is refused with a clear reason. |
| Lobby Ascension mismatch | Host set A | Client calculates B | Client is overwritten with A and the mismatch remains visible in diagnostics. |
| Combat buff | Recipient | Observer | Same power and checksum after ordered action. |
| Five queued combat buffs | Recipient | Observer | Exactly one FIFO buff applies per combat for five combats. |
| Buff received during combat | Recipient | Observer | No current-combat mutation; buff applies once next combat. |
| Ascension Down during combat | Recipient | Observer | Shared set changes once at the next safe noncombat boundary. |
| Death Link receive | Recipient | Observer | Same HP/death state and no outgoing feedback loop. |
| Floor/goal check | Both | Both | Each AP owner reports its own location exactly once. |
| Duplicate AP callback | Recipient | Observer | Grant applied once. |
| Disconnect before grant | Either | Either | Defined retry/no-apply behavior. |
| Disconnect after grant | Either | Either | No duplicate after rejoin. |
| Save and continue | Both | Both | Ownership, AP state, and RunState restored. |
| Singleplayer regression | Singleplayer | N/A | Existing grant and choice behavior preserved. |

## Review gates

Before merging implementation for a phase:

- Static control-flow review against the supported decompiled API.
- C# compilation in the required Windows/Godot/game dependency environment.
- Singleplayer smoke test.
- Two-client host/client test for the phase.
- Save/reconnect test where state persists.
- Log review for duplicate AP checks or grants.
- Documentation update for any changed protocol or limitation.
