# Multiplayer implementation roadmap

> Updated participation contract: [ADR 005](adr/005-direct-ap-connections.md) supersedes the guest receipt relay, shared-check scope, and guest routing described below. Every AP participant now connects directly; host-owned per-player progress remains.

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
- Prove RitsuLib `RunSavedData` and `PlayerRunSavedData` `StartRunLobby`
  contributions arrive before Ready validation and commit into the launched
  run.
- Prove an AP-bound host can launch with AP Guests and that a host without an AP
  slot can launch only with own-slot players and Vanilla Guests.
- Verify the host Ascension set can overwrite and validate every client's local
  calculated set while retaining visible mismatch diagnostics.

### Exit evidence

- Host and client logs identifying the same players with opposite local owner.
- A 25-gold test grant applied exactly once on both copies of the owner.
- No combat divergence or duplicate grant after reconnect.
- Managed-action and lobby-staging behavior recorded against the RFC contracts.

## Phase 1: Participation and AP-source ownership

### Objective

Remove ambiguous single-player ownership without changing normal singleplayer
behavior.

### Tasks

- Introduce a local AP run context resolved from `LocalContext`.
- Stop assigning AP ownership from `Players[0]` or every
  `Player.CreateForNewRun` callback.
- Classify all uses of `GameUtility.CurrentPlayer` as local presentation,
  owner-only AP action, or replicated game mutation.
- Model each player as Own AP Slot, AP Guest, or Vanilla Guest. AP Guests follow
  only the fixed host slot and open no AP connection.
- Add the Vanilla Guest/AP Guest choice and `SharedSlotCheckScope` to the normal
  Archipelago settings menu. Freeze their resolved values at launch.
- Give the host one per-Net-ID `APProgressUnified` record. Clients hold only an
  in-memory view and do not write multiplayer journals to local storage or AP
  DataStorage.
- Assign an opaque `RunId` and freeze the participation/AP-source mapping.
- Preserve main-thread item processing.

### Exit evidence

- Existing singleplayer source behavior remains intact.
- Own-slot players bind their direct AP context locally, AP Guests receive the
  host receipt view, and Vanilla Guests retain native rewards.
- Remote player creation does not reset local AP progress or send Press Start.

## Phase 2: Simple out-of-combat grants

### Objective

Synchronize the lowest-risk received items through existing MegaCrit APIs.

### Tasks

- Aggregate gold-button claims via `SyncLocalObtainedGold`; persist the claimant's
  cumulative raw redemption cursor rather than one applied ID per gold receipt.
- Relics via `SyncLocalObtainedRelic`.
- Potions via `SyncLocalObtainedPotion`, preserving the no-slot retry contract.
- Already-selected cards via `SyncLocalObtainedCard` where semantically valid.
- Add host-progress duplicate tests keyed by `RunId`, AP room/team/slot,
  received-item index, and claiming Net ID. Test cumulative cursors per claimant
  for aggregate gold.
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
- Gate AP progress mutation through the host. Own-slot checks use the owning
  connection; shared-slot checks use only the host connection.
- Verify nested and sequential reward-set IDs remain aligned.
- Cover card, gold, potion, rare-card, and boss reward replacement.

### Exit evidence

- Both peers log the same owner, reward-set ID, reward order, and selected index.
- Only the applicable AP source writer sends the check: own-slot claimant or
  fixed host for an AP Guest.
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
- Preserve stable assignments in host progress by AP source, received-item
  index, and claiming Net ID.
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
- Gate campfire check sending to the own-slot process or fixed shared-slot host.
- Define the corresponding native-event spec for Start-of-Act Ancient choices.
- Use final-result relic synchronization for private Anytime choices if ADR
  review selects that design.

### Exit evidence

- Dynamic Dig, Kindle, Cook, Lift, Clone, Hatch, Mend, and AP options have the
  same order for the same owner on both peers.
- Selecting every option produces the same operation on both peers.
- AP checks are sent only by the applicable own-slot process or shared-slot host.
- Shop purchases synchronize gold loss and any concrete standard reward.

## Phase 6: Progressive starters, ascension effects, and combat buffs

### Objective

Convert effects that do not fit the standard reward transport.

### Tasks

- Synchronize concrete Progressive Starter deck/relic transitions through the
  native path where available and a managed action where it is not.
- Initialize from the fixed host's Ascension set. Route only an AP-bound
  host's later Ascension Downs as concrete managed noncombat removals; process
  every non-host Down as a claimant-local no-op.
- Register the host-ordered RitsuLib combat-buff action with `ApGrantId`.
- Persist one FIFO per AP owner and submit at most its head at combat start.
- Apply at most one universal buff per player per combat through the synchronized
  action, with receipts during combat deferred to the next combat.
- Define Death Link ownership, incoming effect synchronization, and
  feedback-loop suppression.
- Implement host-owned per-player consumption, FIFO, and stable-assignment
  state, with immediate live updates and floor-checkpoint persistence.

### Exit evidence

- Starter decks/relics and ascension behavior match on both peers.
- Every universal combat buff appears once on both copies of its owner.
- Death Link produces one agreed effect without an AP feedback loop.
- Post-action checksums match across repeated and reconnected scenarios.

## Phase 7: Saves, reconnect, compatibility, and release hardening

### Objective

Make the supported topology durable rather than demo-only.

### Tasks

- Stage each player's Own AP Slot, AP Guest, or Vanilla Guest participation and
  receipt-source readiness through RitsuLib. Require a valid contribution for
  every active Net ID before launch.
- Block Ready for an own-slot player until its direct slot data and receipt
  history are ready. Block an AP Guest until the fixed host is AP-bound and its
  host receipt catalog is ready. Vanilla Guests have no AP receipt gate.
- Implement a revisioned host receipt catalog: full snapshot at lobby/rejoin,
  incremental deltas for new items, and a gate that prevents AP Guest claims
  until host progress and catalog revision are both ready. Do not persist the
  catalog or send AP credentials.
- Extract the shared fields of `SerializableAP` into `APProgressUnified` and
  store one record per Net ID in the host-carried run snapshot. Include consumed
  indices, aggregate cursors, counters, stable assignments, pending buffs, and
  pending checks where required.
- Update assignments and consumption immediately in host memory, then persist
  them with native game state at the next normal multiplayer floor save.
- Derive the host Net ID from `INetGameService.NetId` on the host and
  `NetClientGameService.HostNetId` on clients; do not persist a duplicate.
- Force the fixed host's effective Ascension set: AP-derived when AP-bound and
  manually selected otherwise. Reject AP Guest participation for a host without
  an AP slot.
- Freeze the `RunId`, participation/AP-source mapping,
  `SharedSlotCheckScope`, and host-derived shared settings in host run data.
- Implement `HostCharacterOnly` and `AllAPParticipants`. In the latter, the
  host loops over the host and AP Guests using native character state, resolves
  character-specific locations, deduplicates IDs, and submits them without a
  guest forwarding message.
- Remove multiplayer use of durable client-local progress and pending-check
  outboxes. Do not mirror multiplayer progress into AP DataStorage.
- Restore active AP-derived reward/option conversations on rejoin.
- Verify the host restores canonical `RunState` and per-player AP progress.
  Own-slot processes then overlay current AP history; AP Guests overlay the host
  receipt snapshot; Vanilla Guests restore no AP view.
- Accept rejoins only from the frozen participants. Do not support host
  migration, rehosting, or a different AP room/team/slot. Treat each original
  `Player.NetId` as stable within the run and never remap an AP identity to a new
  Net ID. Keep connected/missing IDs observable, but rely on MegaCrit's
  host-authored rejoin snapshot instead of blocking unrelated completed grants.
- Save through the normal per-floor multiplayer checkpoints. Accept rollback to
  the last successful host checkpoint rather than adding a transaction ledger
  or forcing a save after every AP action.
- Verify leaving multiplayer and starting singleplayer creates a fresh `RunId`
  without loading, converting, or copying host-owned multiplayer progress.
- Add targeted diagnostics for owner, transaction, run location, and sequence
  IDs without logging credentials.
- Document unsupported mixed-version and missing-mod parties.
- Keep singleplayer and multiplayer on the same `APProgressUnified` semantics
  where possible, while allowing mode-specific storage envelopes and fields.

### Exit evidence

- The complete validation matrix passes on the supported beta game build.
- No singleplayer regression is observed in the same scenarios.
- Multiplayer limitations and required mod versions are documented.

## Two-client validation matrix

Run each relevant row with both host ownership assignments when possible.

| Scenario | Host | Client | Expected evidence |
|---|---|---|---|
| Local ownership | AP Alice | AP Bob | Each process resolves itself as local and the other as remote. |
| AP Guest client | AP Alice | AP Guest Bob | Bob opens no AP socket, follows Alice's settings/receipts, and may claim each shared receipt independently. |
| Vanilla Guest client | AP Alice | Vanilla Guest Bob | Bob receives native rewards, has no AP progress, and sends no AP operations. |
| Host without AP | Vanilla-mode Alice | AP Bob | Alice may host Bob's independent slot but cannot admit an AP Guest; her manual Ascension set is shared. |
| Mixed lobby | AP Alice | AP Guest Bob plus own-slot Carol plus Vanilla Guest Dave | Every Net ID freezes the correct source and no guest follows Carol's non-host slot. |
| Shared receipt fan-out | AP Alice | AP Guest Bob | The same host receipt is consumed once for Alice and once for Bob; their assignments may differ. |
| Gold grant | Recipient | Observer | Both copies of recipient gain the same amount once. |
| Relic grant | Observer | Recipient | Both copies obtain the same relic once. |
| Potion with slot | Recipient | Observer | Both copies obtain the potion. |
| Potion without slot | Recipient | Observer | No consumption; reward remains claimable. |
| Card select | Observer | Recipient | One UI, one synchronized choice, matching decks. |
| AP combat location | Recipient | Observer | Same reward order; own-slot check uses its owner, shared-slot check uses the host. |
| Nested reward | Recipient | Observer | Matching reward-set IDs before and after nesting. |
| Rest with Shovel/Candle/Cleaver | Mixed relic ownership | Mixed | Same per-owner option order on both peers. |
| Six campfire checks | Recipient | Observer | Same AP option count/order and selected operation. |
| Shop AP purchase | Recipient | Observer | Owner-only check, matching gold loss. |
| Ancient native choice | Recipient | Observer | Same event options and selected relic. |
| Lobby receipt source complete | Host | Own-slot or AP Guest client | Host sees the appropriate direct or relayed receipt source ready before launch. |
| Lobby receipt source incomplete | Host | Disconnected own-slot client or unprepared AP Guest | Host automatically unreadies and refuses launch; the frozen mode does not change. |
| Client-last Ready race | Ready host | AP client with newly incomplete record | Final host guard refuses launch and schedules host auto-unready. |
| Host identity derivation | Host | Client | Host sees `hostNetId=localNetId`; client derives that same host ID through `HostNetId`. |
| Lobby Ascension authority | AP or non-AP host | Client | Client is overwritten with the host's effective set; AP Guests require the AP-host case. |
| Combat buff | Recipient | Observer | Same power and checksum after ordered action. |
| Five queued combat buffs | Recipient | Observer | Exactly one FIFO buff applies per combat for five combats. |
| Buff received during combat | Recipient | Observer | No current-combat mutation; buff applies once next combat. |
| Host Ascension Down during combat | Recipient host | Observer | Shared set changes once at the next safe noncombat boundary. |
| Non-host Ascension Down | Observer host | Recipient client | Client processes a local no-op; the shared set does not change. |
| Death Link receive | Recipient | Observer | Same HP/death state and no outgoing feedback loop. |
| Shared checks: host only | AP host | AP Guest | Only host-character locations are submitted. |
| Shared checks: all participants | AP host | AP Guest with different character | Host automatically submits deduplicated host and guest character locations; guest sends no check message. |
| Shared checks: duplicate character | AP host | AP Guest with same character | Host resolves the duplicate location ID and submits it once. |
| Independent checks | AP host | Own-slot AP client | Each direct AP connection submits only its own slot's locations. |
| Host AP disconnect | AP host | AP Guest | Cached receipts remain claimable; no new receipt or shared check flows until host reconnects. |
| Duplicate AP callback | Recipient | Observer | Grant applied once. |
| Disconnect before grant | Either | Either | Defined retry/no-apply behavior. |
| Disconnect after grant | Either | Either | No duplicate after rejoin. |
| Peer reconnect after another owner claims | Original participant absent | Same Steam account or ENet `clientId` | Exact original Net ID set is restored; the native snapshot contains the completed effect without replay. |
| Different Net ID reconnect | Original participant absent | Different ENet `clientId` | Vanilla refuses the nonparticipant; the AP mapping is not rebound. |
| Save and continue | Both | Both | `RunState`, per-player consumption, cursors, and stable assignments restore from the host floor checkpoint. |
| Empty client storage | Host checkpoint present | Rejoining own-slot player or AP Guest | In-memory AP view rebuilds from host progress plus the proper receipt source; no local journal is needed. |
| Crash before floor checkpoint | Claim completed in live host state | Relaunched party | Previous host checkpoint wins and the rolled-back receipt is claimable again. |
| Wrong-slot rejoin | Original host | Different AP identity | Rejoin is refused without rebinding the player. |
| Leave multiplayer for singleplayer | Client | N/A | A fresh solo run starts and no multiplayer `RunState` or host-owned AP progress is loaded. |
| Singleplayer regression | Singleplayer | N/A | Existing grant and choice behavior preserved. |

## Review gates

Before merging implementation for a phase:

- Static control-flow review against the supported decompiled API.
- C# compilation in the required Windows/Godot/game dependency environment.
- Singleplayer smoke test.
- Two-client host/client test for the phase.
- Save/reconnect test where state persists.
- Log review for duplicate AP checks or grants.
- Documentation update for any changed payload schema or limitation.
