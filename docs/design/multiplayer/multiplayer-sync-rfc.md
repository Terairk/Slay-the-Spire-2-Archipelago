# RFC: Archipelago multiplayer synchronization

- **Status:** Draft
- **Owners:** Unassigned
- **Reviewers:** Unassigned
- **Target release:** Unassigned
- **Last updated:** 2026-08-17

## 1. Summary

Add Slay the Spire 2 multiplayer support without maintaining a separate
multiplayer client. Each game process owns one local Archipelago session and
one local STS player. Every process still maintains MegaCrit's replicated copy
of the complete run, including remote players.

The central boundary is:

> Archipelago owns why an effect exists. MegaCrit's multiplayer layer should
> own how the resulting Slay the Spire state change is reproduced on every
> peer.

Private AP state should remain private. Only the smallest deterministic result
needed to reproduce shared game state should cross the STS peer connection.

## 2. Motivation

The current client deliberately starts and loads singleplayer runs. Its AP
session, progress, current-player reference, reward menu, and item processing
were designed around one STS player in one process.

MegaCrit multiplayer does not continuously replicate authoritative snapshots.
It combines:

- a full `RunState` on every peer;
- host-ordered combat actions;
- per-player choice and reward sequence IDs;
- index-based reward, rest-site, and event selection messages;
- explicit concrete payloads for non-deterministic reward contexts;
- shared RNG streams and counters; and
- combat-state checksums that detect, but do not repair, divergence.

AP item delivery is external, asynchronous, and known initially only to the
receiving AP client. The port must convert that private event into a MegaCrit
operation that every peer can reproduce.

## 3. Goals

- Support one AP connection per participating STS player.
- Preserve the existing singleplayer experience.
- Use MegaCrit synchronization where its lifecycle matches the AP feature.
- Keep credentials, AP socket state, scouted data, and unrelated AP progress
  local to the owning process.
- Make every AP-originated game-state effect deterministic or explicitly
  synchronized.
- Provide stable transaction identity for duplicate prevention and reconnect.
- Keep reward, rest-site, event, and nested-choice sequence state aligned.
- Fail safely when a peer has an incompatible mod protocol.

## 4. Non-goals for the first multiplayer release

- Allowing a peer without the Archipelago mod to join.
- Sharing one AP socket among several STS players.
- Host migration while an AP-specific synchronized operation is in flight.
- Supporting different AP client protocol versions in the same party.
- Preserving experimental multiplayer saves across protocol changes.
- Replacing MegaCrit networking with an independent networking stack.

## 5. Proposed topology

Each process owns the AP session associated with its local STS player:

```text
Alice's process                         Bob's process
---------------                         -------------
Local STS player: Alice                 Local STS player: Bob
AP session: Alice's AP slot             AP session: Bob's AP slot
AP progress: Alice's progress           AP progress: Bob's progress

Replicated RunState:                    Replicated RunState:
  Alice                                  Alice
  Bob                                    Bob
  shared run data                        shared run data
```

`LocalContext.GetMe(runState)` is the authoritative MegaCrit mechanism for
resolving the local STS player. A process-global AP context is acceptable when
it deliberately represents that local player. It must not be assigned from
`Players[0]` or whichever `Player.CreateForNewRun` happens to run last.

See ADR 001.

## 6. MegaCrit synchronization concepts

### 6.1 Replicated execution

Every peer contains all players and applies shared game-state mutations to its
own copy. `PlayerCmd.GainGold`, `RelicCmd.Obtain`, and similar commands mutate
the local copy; they do not inherently broadcast themselves. They are safe
when invoked by a synchronized caller or paired with the correct synchronizer.

### 6.2 Reward-set IDs

Each player has an independent, increasing reward-set sequence. Beginning a
`RewardsSet` assigns the next ID for that player. A reward-selection message is
conceptually:

```text
(owning player, reward-set ID, reward index)
```

The ID distinguishes nested or temporally overlapping reward screens and lets
a slower peer buffer a selection until it constructs the matching backend set.
Peers must begin the same reward sets for a player in the same order.

### 6.3 Player-choice IDs

Each player also has an independent, increasing choice sequence. A choice ID
correlates an asynchronous answer with a question such as:

- which card was selected;
- which relic was selected;
- which player Mend targeted; or
- which event option was chosen.

Every peer must reserve the same choice ID at the same logical point. The local
owner publishes the result while remote peers wait for it.

### 6.4 Option indexes

Rest-site and event messages communicate option indexes. Every process must
therefore construct the same ordered backend option list for a particular
player and interaction. The lists may differ between players; they may not
differ between peers for the same player.

### 6.5 Checksums

Combat checksums include replicated combat state, RNG state, and next reward
and choice IDs. They detect divergence after it occurs. They are not a state
repair mechanism and cannot make mismatched reward or option lists safe.

## 7. State ownership

| State | Authority | Needed on remote peers? | Notes |
|---|---|---:|---|
| AP socket and credentials | Local AP process | No | Never transmit credentials. |
| AP slot settings | Local AP process | Only derived gameplay inputs | Raw settings may remain private. |
| Received item history | Local AP process | No | Synchronize resulting game effect instead. |
| Scouted location details | Local AP process | Only display payload if required | Do not make remote simulation depend on local scouting calls. |
| AP checked locations | Local AP process | Usually no | Index-based option/reward structures still need matching derived specs. |
| AP consumed item indexes | Local AP process | Usually no | Custom grant transport may mirror transaction IDs for idempotency. |
| STS player gold/deck/relics/potions | MegaCrit `RunState` | Yes | Every process must reproduce mutations. |
| Reward-set and choice sequences | MegaCrit synchronizers | Yes | Must advance in the same logical order. |
| Rest-site/event backend option order | MegaCrit synchronizers | Yes | Build from replicated vanilla state plus an AP-derived spec. |
| AP notification/UI state | Local AP process | No | Presentation only. |

See ADR 002.

### 7.1 Shared-run compatibility profile

One AP slot per player does not imply that every slot-data value may differ.
Slay the Spire co-op has one shared run seed, map, act sequence, room state, and
set of shared gameplay rules. Before the run begins, the mod must compare or
negotiate a compatibility profile containing every AP setting that affects
shared generation or replicated hooks.

Likely profile fields include:

- mod multiplayer protocol version;
- APWorld/mod compatibility version;
- game build and required library versions;
- shared seed policy;
- shared ascension or modifier policy;
- act and map-generation settings; and
- any AP option that changes shared encounters, rewards, or room structure.

Per-player capability and location settings may differ when their consequences
are expressed through per-owner specs. A shared-generation setting must either
match, be resolved by an accepted host policy, or reject the party before run
creation. The exact profile is an open design item and must be proven against
the APWorld slot-data contract.

## 8. Proposed data contracts

These records describe intent, not a finalized wire format.

```csharp
public readonly record struct ApGrantId(
    ulong OwnerNetId,
    int Team,
    int Slot,
    int ReceivedItemIndex);

public readonly record struct ApGrantSpec(
    ApGrantId Id,
    ApGrantKind Kind,
    int? Amount,
    string? ModelId);

public readonly record struct ApLocationRewardSpec(
    ulong OwnerNetId,
    long LocationId,
    string DisplayClassification,
    string DisplayText);

public readonly record struct ApRestSiteSpec(
    ulong OwnerNetId,
    bool RestUnlocked,
    bool SmithUnlocked,
    IReadOnlyList<ApCampfireCheckSpec> AvailableChecks);
```

Before adopting these contracts, decide:

- whether AP team and slot numbers are sufficiently stable for save/reconnect;
- whether an opaque per-run owner identity is preferable;
- which payloads need persistence in the MegaCrit run save;
- how protocol versions are negotiated; and
- whether transport messages can be registered through a supported mod API.

## 9. Synchronization strategies

### 9.1 Local-only AP operations

Use for effects that do not mutate shared STS state:

- send an AP location check;
- update local checked/used state;
- display notifications;
- cache private reward candidates generated without MegaCrit state; and
- save local AP connection state.

### 9.2 `RewardSynchronizer`

Use for concrete, out-of-combat results already supported by MegaCrit:

- card obtained or skipped;
- relic obtained or skipped;
- potion obtained or skipped;
- gold obtained; and
- gold lost.

The local process applies the operation and calls the corresponding
`SyncLocal...` method, mirroring MegaCrit merchant behavior. It does not carry
an AP transaction ID and rejects reward synchronization during combat.

### 9.3 `RewardsSetSynchronizer` and RitsuLib custom rewards

Use when the AP interaction genuinely participates in a reward lifecycle:

- an AP location replacing a combat card/gold/potion reward;
- a received card reward using the native card picker; or
- another selectable/skippable reward whose backend set should be tracked.

Every peer must construct the same reward set and payload for the owning
player. Only the owner displays `NRewardsScreen`; every peer executes the
logical selection. Owner-only branches send AP checks and update AP progress.

RitsuLib supplies custom reward registration, presentation, and save payload
support. It does not automatically broadcast an AP-derived payload to peers.

### 9.4 Final-result synchronization for private choices

Use when rejected candidates never affect MegaCrit RNG, pools, or `RunState`:

```text
local AP UI chooses Vajra
-> synchronize concrete "Alice obtained Vajra"
-> every peer applies Vajra to Alice
```

This is a strong candidate for linked relic choices. Candidate generation must
not privately advance replicated RNG or remove models from replicated bags.

### 9.5 AP-derived option specifications

Use for index-based native interactions whose AP contribution differs by
owner, especially campfires and Start-of-Act Ancients.

Every peer first generates the vanilla list from replicated `RunState`. Vanilla
relic/card additions such as Dig, Kindle, Cook, Lift, Clone, and Hatch should
already agree. Every peer then applies the same compact AP specification to
that list in a deterministic order.

Do not hard-code a universal fixed option list. Preserve whatever vanilla and
other deterministic model hooks generated.

### 9.6 Synchronized combat actions

AP combat buffs cannot use `RewardSynchronizer`: its payload is limited to
standard rewards and it rejects combat use. Strength, Dexterity, Buffer,
Artifact, Free Attack, and similar effects need a host-ordered action or an
equivalent deterministic hook entered by every peer.

## 10. Feature assessment

| Feature | Current boundary | Proposed multiplayer direction | Initial risk |
|---|---|---|---|
| AP connection and item queue | Local process/main-thread queue | Retain; bind to local STS player | Medium |
| Gold claim | Local AP reward UI | Apply locally plus `SyncLocalObtainedGold` | Low |
| Relic claim | Local AP reward UI | Apply locally plus `SyncLocalObtainedRelic` | Medium |
| Potion claim | Local AP reward UI | Apply locally plus `SyncLocalObtainedPotion` | Medium |
| Card claim | Calls `SelectUnsynchronized` | Native reward/choice synchronization or explicit final card payload | High |
| Linked relic choice | Local AP UI | Keep candidates private; synchronize final relic if generation is isolated | Medium |
| AP combat reward location | Replaces a native reward | Same custom backend reward on every peer; owner sends check | High |
| Shop slot unlocks | Local merchant generation | Keep private; synchronize gold loss and any obtained standard item | Medium |
| AP shop purchase | Owner sends check and loses gold locally | Owner-only check plus `SyncLocalGoldLost` | Medium |
| Progressive Rest/Smith | Mutates generated option list from local AP state | Publish/apply per-owner `ApRestSiteSpec` | High |
| Campfire AP checks | Custom `RestSiteOption`s | Same ordered backend options; owner-only AP check | High |
| Progressive Ancient | Mutates native event choices or private AP choice | Event spec for native choices; final-result sync for private choices | High |
| Progressive starters | Reconciles deck/relic from local progress | Synchronize concrete deck/relic transition and ownership | High |
| Ascension AP items | Modifies gameplay configuration | Define per-player versus shared scope; synchronize derived rule state | High |
| Universal combat buffs | Applies powers at turn start | Host-ordered synchronized action | Very high |
| Death Link | External AP event mutates HP/death | Owner-only AP send/receive plus synchronized concrete STS effect | Very high |
| Floor and goal checks | Owner sends AP checks from shared progression | Keep AP operation owner-only; verify exactly-once local lifecycle | Medium |
| Save/load/reconnect | Singleplayer service and first player | Resolve local player by Net ID; persist protocol state | Very high |

## 11. Detailed flows

### 11.1 Simple gold grant

```text
Alice AP client receives item index 73
Alice validates that 73 is not consumed
Alice applies GainGold(50, Alice)
Alice calls RewardSynchronizer.SyncLocalObtainedGold(50)
Bob receives the concrete amount and applies GainGold(50, Alice)
Alice commits AP item 73 as consumed
```

The exact commit order and failure behavior must preserve the existing
authoritative-item semantics and avoid speculative rollback.

### 11.2 AP location replacing a combat reward

```text
All peers generate Alice's combat rewards
Alice publishes or all peers derive the same AP location reward spec
All peers replace the same reward at the same index
All peers begin the same Alice reward-set ID
Alice selects the AP reward index
All peers complete that logical reward
Only Alice sends the AP location check
```

The AP item delivered in response is a later, independent AP receipt. It may be
delivered to a different AP slot.

### 11.3 Rest site

```text
All peers generate vanilla options for every player from replicated RunState
Local AP owner publishes the current AP rest-site spec
All peers apply that spec to the matching player's vanilla option list
Local owner displays and selects an option
RestSiteSynchronizer broadcasts the selected index
All peers execute the same logical option
Only the owner sends an AP check or updates AP progress
```

The spec must be available before `RestSiteSynchronizer.BeginRestSite()` or the
message must be buffered until the matching run location is ready.

### 11.4 Combat buff

```text
Alice AP client queues received buff with ApGrantId
At the supported combat boundary, Alice requests a synchronized AP buff action
Host establishes action order and broadcasts it
Every peer applies the same power to Alice
Applied transaction state prevents duplicate execution
Alice commits AP consumption/data-storage state
```

## 12. Persistence and reconnect

The design must specify persistence for:

- pending private AP reward assignments;
- consumed AP receipt indexes;
- custom applied-grant IDs, if custom transport is used;
- AP-derived option or reward specs that can remain open across a save;
- protocol version; and
- any in-flight operation that can survive a peer reconnect.

Prefer restoring shared STS state from MegaCrit's serialized run and restoring
private AP state from the owning AP save/server. Do not make multiple peers
write the same AP data-storage key.

Joining or rejoining peers must obtain enough current AP-derived backend state
to reconstruct any active reward, rest-site, or event conversation before
buffered index messages are processed.

## 13. Compatibility and failure policy

- Require every peer to run the same accepted AP multiplayer protocol version.
- Reject or disable AP multiplayer before a run begins when versions differ.
- Never silently apply an unknown model ID or grant kind.
- Owner-only AP network failures must not cause remote peers to apply a
  different STS result.
- Compatibility patches should fail open only when doing so cannot corrupt
  replicated state.
- Log owner Net ID, AP receipt index, run location, operation kind, and protocol
  version without logging credentials.

## 14. Open questions

1. What supported mod API can transport AP-specific peer messages?
2. Can existing `RewardSynchronizer` calls be used safely from mod code in all
   supported public game builds?
3. Does RitsuLib expose a peer-payload channel, or only reward save payloads?
4. Should linked relic choices remain private or become native reward choices?
5. Should progressive Rest/Smith remove backend options, disable them, or use
   AP placeholders with semantic IDs?
6. How are AP specs restored for a peer joining while a choice is active?
7. Which ascension effects are per-player, and which change shared generation?
8. What happens when the AP owner disconnects after STS peers apply a grant but
   before AP consumption is persisted?
9. Can an opaque owner/session identity avoid persisting AP team and slot
   numbers in shared run data?
10. What version handshake is available before custom models/messages are
    deserialized?
11. Which slot-data fields must match across participating AP slots?
12. Which seed owns shared map generation when AP slots specify different
    character or seed configuration?
13. Is Death Link scoped to the local STS player, the full co-op party, or a
    configurable policy, and how is feedback-loop suppression shared?

## 15. Evidence and validation boundaries

The initial model is based on static inspection of the public-branch game API,
including these MegaCrit types:

- `ActionQueueSynchronizer`
- `PlayerChoiceSynchronizer`
- `RewardSynchronizer`
- `RewardsSetSynchronizer`
- `RestSiteSynchronizer`
- `EventSynchronizer`
- `CombatStateSynchronizer`
- `ChecksumTracker`
- `NetFullCombatState`
- `LocalContext`

Static evidence does not prove modded rewards, models, or messages serialize
correctly between real peers. The RFC remains Draft until the Phase 0 spike
captures two-client runtime evidence.
