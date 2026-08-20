# RFC: Archipelago multiplayer synchronization

- **Status:** Draft
- **Owners:** Unassigned
- **Reviewers:** Unassigned
- **Target release:** Unassigned
- **Last updated:** 2026-08-20

## 1. Summary

Each Slay the Spire 2 process owns one local STS player. At launch the host
freezes that player as Own AP Slot, AP Guest, or Vanilla Guest. Own-slot players
connect directly to their slots. AP Guests open no AP connection and follow the
fixed STS host's AP slot through a host-relayed receipt catalog. Vanilla Guests
receive ordinary STS rewards. Mixed lobbies are supported.

The fixed STS host owns every player's durable multiplayer AP progress in the
canonical run snapshot. Clients retain only an in-memory view. Full received
history is reconstructed from the appropriate AP connection or, for AP Guests,
from the host's in-memory receipt catalog.

The central boundary is:

> Archipelago owns why an effect exists. MegaCrit's multiplayer layer owns how
> the resulting Slay the Spire state change is reproduced on every peer.

AP callbacks must not directly mutate replicated game state. They create a
stable grant, resolve any random assignment once, and route the concrete result
through either a native MegaCrit synchronizer or a RitsuLib managed action.

## 2. Goals

- Support independent AP slots, one host slot shared by several AP participants,
  and Vanilla Guests in the same lobby.
- Keep AP Guests off the AP server and relay the host slot's settings and
  receipts through the STS host.
- Preserve the existing singleplayer experience.
- Use native card, relic, potion, and gold synchronization where it already
  models the required operation.
- Use RitsuLib managed actions for AP-specific mutations from the beginning of
  multiplayer implementation, rather than growing an ad hoc message layer.
- Make AP grant executors deterministic and idempotent.
- Consume at most one queued AP buff per player per combat, in FIFO order.
- Keep credentials and full AP SDK sessions at the owning connection while the
  host checkpoint stores compact per-player AP progress.
- Reuse one `APProgressUnified` data model for singleplayer and multiplayer
  fields where their semantics match.
- Stage and validate the multiplayer launch contract before Ready is accepted.
- Use the last successful host floor checkpoint as the recovery truth and accept
  bounded rollback before it.
- Accept cooperative assistance from both guest modes and non-host AP players;
  future difficulty compensation is outside this design.

## 3. Non-goals for the first multiplayer release

- Allowing a peer without the Archipelago mod to join.
- Giving AP Guests their own connection to the shared host slot.
- Replacing MegaCrit networking with an independent networking stack.
- Preserving experimental multiplayer saves across incompatible mod versions.
- Making random assignments stable across different game versions.
- Distributed rollback or an all-peer success acknowledgment protocol.
- Converting an in-progress multiplayer `RunState` into a singleplayer save.
- Host migration or loading the host's save on another player's computer.
- Client-local or AP DataStorage multiplayer progress journals.
- A distributed transaction ledger, rollback protocol, or forensic recovery for
  the interval before the next host checkpoint.

## 4. Topology and authority

```text
Alice's host process                    Bob's process
--------------------                    -------------
Local STS player: Alice                 Local STS player: Bob
Mode: Own AP Slot                       Mode: AP Guest
AP connection: host slot                AP connection: none
Host progress: Players[Alice]           Host progress: Players[Bob]
Host receipt catalog                    In-memory host receipt view

Replicated RunState:                    Replicated RunState:
  Alice                                  Alice
  Bob                                    Bob
  shared run data                        shared run data
```

`LocalContext.GetMe(runState)` resolves the local STS player. An opaque `RunId`
and a mapping from STS Net ID to Own AP Slot, AP Guest, or Vanilla Guest are
established in the lobby and frozen for the run. Own-slot records include exact
AP room seed, numeric team ID, and numeric slot ID. AP Guest records refer to
the host's frozen AP source. A process must never infer ownership from
`Players[0]` or from the last `Player.CreateForNewRun` call.

Within that run, Net ID is stable across an active-run disconnect/rejoin.
MegaCrit keeps every original `Player` (and its immutable `Player.NetId`) in
`RunState`, removes only the disconnected entry from `RunLobby`'s connected set,
and accepts a rejoin only when the transport sender ID resolves to an existing
run player. Steam derives the value from the account Steam ID; local ENet uses
the explicitly supplied `clientId`. The mod may therefore key and restore the
frozen participant mapping directly by Net ID. A temporarily absent observer
does not block an AP owner whose own process is still connected from completing
a serializable grant: MegaCrit sends the returning player a fresh host-authored
`SerializableRun` and combat snapshot before enabling normal broadcasts to that
peer. The live connected Net ID set is diagnostic state, not an AP claim gate;
loss of the claiming process's own host/client transport still blocks its claim.

Net ID is not globally durable and is not assumed to survive across unrelated
runs, accounts, or changed ENet `clientId` values. Within a run it identifies
the claimant. Durable receipt reasoning uses `RunId`, AP room/team/slot,
received-item index, and claiming Net ID because one host-slot receipt may be
claimed independently by several players.

The STS host remains host and save owner for the lifetime of that `RunId`.
There is no host migration. Host identity is not persisted as a second AP
field: the host reads its own `INetGameService.NetId`, while each client reads
`NetClientGameService.HostNetId`. An AP Guest requires an AP-bound host. The
host slot is authoritative for the shared receipt catalog, the initial shared
Ascension set, later Ascension Downs, and shared-slot checks. A host without an
AP slot may launch with independently AP-bound players and Vanilla Guests, but
not AP Guests.

Authority is split as follows:

| State | Authority | Replication rule |
|---|---|---|
| AP sockets and credentials | Host for the shared slot; own-slot process otherwise | Never send credentials through STS multiplayer. AP Guests open no AP socket. |
| Received history | Owning AP connection, plus host in-memory relay for AP Guests | Do not persist `AllReceivedItems`; send AP Guests revisioned snapshots/deltas. |
| AP slot settings | Owning AP connection; host relays its settings to AP Guests | Freeze derived run settings in the host launch contract. |
| Participation/AP source mapping and `RunId` | Host launch contract | Freeze one mode and source per STS Net ID. |
| Effective Ascension set | Fixed STS host | AP-bound host uses its AP state; otherwise the host chooses manually. Host value overwrites every client. |
| Character selection | Native STS lobby plus AP patches | Own-slot players follow their slot unlocks; AP Guests follow host-slot settings; Vanilla Guests may choose any character. |
| Gold, deck, relics, potions, powers | Replicated `RunState` | Every peer must reproduce each mutation. |
| Consumed indices, cursors, assignments, pending checks | Host-owned per-player run data | Update live immediately; persist at normal multiplayer floor checkpoints. |
| Shared-slot checks | Fixed STS host | Apply launch-frozen `SharedSlotCheckScope`; resolve all selected characters locally and deduplicate IDs. |

See ADR 001, ADR 002, and ADR 004.

## 5. MegaCrit and RitsuLib execution model

### 5.1 Replicated local execution

Every peer contains a replica of every `Player`. Commands such as
`PlayerCmd.GainGold`, `RelicCmd.Obtain`, and `CardPileCmd.Add` only mutate the
copy on the process where they run. Synchronization therefore means arranging
for every peer to execute the same concrete mutation against its replica of the
same owning player.

### 5.2 Native synchronizers

Use MegaCrit's existing reward and choice protocols when their lifecycle fits:

- `RewardSynchronizer` for obtained or skipped cards, relics, potions, and gold;
- `RewardsSetSynchronizer` for native selectable reward sets;
- player-choice IDs for asynchronous selections; and
- native index messages for rest-site and event choices after every peer has
  constructed the same ordered option list.

Reward and choice sequence IDs are per player. Peers must create them in the
same logical order for a given player.

### 5.3 RitsuLib managed actions

Use `RitsuLibManagedNetActions` for concrete AP effects that have no suitable
native transport. Registration uses a stable module ID and action key. A local
owner calls `Request`; the host orders the action through the vanilla action
queue, broadcasts it, and every peer executes a local copy. The initiating
client does not separately apply the effect before its queued copy arrives.

The executor receives `context.Player`, which is the owning player's replica on
that process. The payload must contain a resolved result, not instructions to
query AP or roll randomness.

`Request(...) == true` means that the enqueue request was issued. It does not
mean that the executor completed successfully.

Recommended registrations are deliberately few and typed:

```text
module: sts2ap
action: grant.non_combat     GameActionType.NonCombat
action: grant.combat_buff    GameActionType.Combat
```

Add a new action key only when its ordering or execution contract is genuinely
different.

### 5.4 Checksum and failure behavior

The base action executor logs an action exception and still finishes/pops the
failed action. During combat, the game sends a post-action checksum covering
replicated combat state, RNG state, and sequence counters. A mismatch enters
the game's divergence flow and may disconnect the mismatched peer. The checksum
detects divergence; it does not repair it.

`NonCombat` actions do not receive the same immediate post-action combat
checksum. Their executors therefore require explicit validation and focused
two-client tests.

## 6. Lobby lifecycle and RitsuLib run data

MegaCrit's lobby layer is not only the pre-run waiting screen. `StartRunLobby`
handles players beginning a new run, `LoadRunLobby` handles players assembling
to resume a saved run, and `RunLobby` handles connection, disconnection, and
rejoin while a run is active. The three lobby types have different messages and
responsibilities.

For a new run, RitsuLib `RunSavedData<T>` and `PlayerRunSavedData<T>` are the
selected `StartRunLobby` staging mechanism. Register the slots early and enable
`SyncLobbyOnChange` so each contribution is visible before launch. The option
only pushes writes made through the pre-run `.Lobby` accessor; it is not a
general mid-run broadcast mechanism.

```csharp
public sealed record ApLobbyRunState(
    Guid RunId,
    IReadOnlyList<int> HostEffectiveAscensions,
    SharedSlotCheckScope SharedSlotCheckScope);

public sealed record ApLobbyPlayerState(
    ApParticipationKind Participation,
    string? ApRoomSeed,
    int? ApTeamId,
    int? ApSlotId,
    bool ReceiptSourceReady,
    APProgressUnified Progress);
```

The exact serializer shape may change, but the responsibilities must not:

- `RunSavedData<ApLobbyRunState>` stores the host-owned launch contract: opaque
  `RunId`, the effective Ascension set used by the actual run, and the
  launch-frozen `SharedSlotCheckScope` selected in the ordinary AP settings
  menu.
- `PlayerRunSavedData<ApLobbyPlayerState>` stores each player's frozen
  participation/AP source and host-owned `APProgressUnified`. Its values are
  keyed by STS player Net ID and are part of the host-carried snapshot, not
  durable client-local saves.
- Own AP Slot players cannot become Ready until their AP connection has loaded
  slot data and initial received history. AP Guests cannot become Ready until
  the host is AP-bound and its host-slot receipt catalog is ready. Vanilla
  Guests have no AP receipt-readiness gate.
- The host validates that every AP Guest resolves specifically to the fixed
  host's AP source. It never binds a guest to another client's slot.
- Shared mod settings that affect run generation belong in the host run record.
  The settings-menu value is mutable before launch; the resolved run value is
  immutable after launch.
- With `SyncLobbyOnChange`, each client sends its per-player contribution to the
  host in RitsuLib's data attached to `LobbyPlayerChangedCharacterMessage` and
  flushes it with `LobbyPlayerSetReadyMessage`; the host merges it under that
  client's Net ID before handling Ready. The host contribution is merged
  locally. The host Ready button is disabled until every active player record
  is complete and is automatically reset if one becomes incomplete. The host
  recomputes the same derived condition immediately before launch. This staging
  is not broadcast back to clients.
- An AP-bound host's effective Ascension set comes from its AP configuration.
  AP Guests use those same host settings. A host with no AP slot may manually
  select any configuration, but then AP Guest participation is invalid. The host
  set overwrites every client's local effective set. Failure to apply or
  validate it blocks launch.
- Shopsanity, campfire sanity, shuffled card rewards, and other settings that
  mainly add per-player checks may differ. They are not included in the shared
  equality rule.
- Native STS lobby state already communicates every selected character. Own-slot
  players validate against their slots, AP Guests validate against host-slot
  settings, and Vanilla Guests use ordinary guest character availability. The
  host therefore needs no AP Guest check-forwarding message: with
  `AllAPParticipants`, it loops over the committed host/AP Guest players,
  resolves their character-specific location IDs, deduplicates, and submits the
  checks itself.

Progressive Starter does not need a special lobby payload. Its items ultimately
change the owner's real deck or relic collection. Synchronize those concrete
card/relic transitions when they occur. Prefer the native card/relic path; use a
managed action only for a removal or transformation for which STS has no
appropriate synchronizer.

When the run launches, the staged values are committed into the run snapshot.
RitsuLib also preserves registered run data through `LoadRunLobby` save loading
and `RunLobby` rejoin snapshots. This makes it useful for durable shared run
state, but not a substitute for live `RunState` synchronization: mid-run writes
must still occur through a native synchronizer or ordered managed action.

The `RunId`, participation/AP-source mapping, shared-slot check scope, and
initial host effective Ascension set are immutable after the run snapshot is
committed. No saved `Frozen` or `Validated` boolean represents that invariant.
Only an AP-bound host's later Ascension Downs may change the current shared set.

## 7. Grant identity, payload, and persistence

### 7.1 Stable identity

A discrete receipt-backed grant is globally identified by the opaque run, the
receiving AP identity, and the AP received-item index:

```csharp
public readonly record struct ApGrantId(
    Guid RunId,
    string ApRoomSeed,
    int ApTeamId,
    int ApSlotId,
    int ReceivedItemIndex,
    ulong ClaimingNetId);
```

The full source identity remains explicit at protocol and validation boundaries.
Within one frozen player progress record, `UsedItems` may contain only received
indices because its room/team/slot and claiming Net ID are already fixed. The
claiming player is part of the full identity because host-slot receipt 74 may be
consumed for Alice and still available to Bob. `RunId` prevents leakage between
two runs that reuse the same AP source.

Aggregate claims use the `RunId`, AP source, claiming Net ID, effect kind, and
cumulative cursor that materialized the claim.

### 7.2 Concrete payload

The managed-action payload is a tagged concrete result:

```csharp
public enum ApGrantKind
{
    NonCombatMutation,
    CombatBuff,
}

public sealed record ApGrantPayload(
    int SchemaVersion,
    ApGrantId GrantId,
    ApGrantKind Kind,
    string EffectId,
    int Amount,
    string? ModelId,
    string AssignmentDomain);
```

Examples are `EffectId = "strength", Amount = 2` and a concrete model ID for a
mod-specific mutation. Deserializers reject unknown payload schema versions,
kinds, or effect IDs before mutation. This per-message serialization marker is
not a global multiplayer protocol field saved in the run.

Native card, relic, and potion paths do not need to serialize this exact record,
but discrete claims use the same `ApGrantId`, assignment cache, and host
consumption rules. Gold is intentionally different: multiple gold receipts
are materialized by one aggregate button claim and use the cumulative redemption
contract below rather than one applied ID per receipt.

### 7.3 Host-owned per-player AP progress

The host-carried run snapshot contains one `APProgressUnified` record per STS
Net ID. It reuses the semantic fields already present in singleplayer
`SerializableAP` without embedding the opaque singleplayer `SaveData` envelope:

```csharp
public sealed record APProgressUnified(
    IReadOnlySet<int> UsedItems,
    int GoldRedeemed,
    RewardAttemptCounters Counters,
    IReadOnlyDictionary<int, ResolvedGrantAssignment> Assignments,
    IReadOnlyList<QueuedBuffGrant> PendingBuffs,
    IReadOnlySet<long> PendingLocationIds,
    ProgressiveStarterState ProgressiveStarters);
```

The exact DTO may keep separate card, relic, Ancient, and potion assignment maps
to match existing serialization. The requirements are:

- `UsedItems` and aggregate cursors are scoped to the claiming player. In the
  shared-slot mode, the same received index may appear in several players'
  progress independently.
- Stable assignments are stored in the claiming player's host progress before
  they are exposed in the reward UI. Other peers need only the transient
  concrete reward specification and resulting native game state.
- Pending checks that must survive a checkpoint are host-owned. Multiplayer
  clients do not write durable local check outboxes or AP DataStorage journals.
- `AllReceivedItems` is never persisted. Own-slot players rebuild it from their
  AP connection; AP Guests receive the host's revisioned in-memory catalog.
- Clients may mirror their progress in memory, but host state replaces that view
  on load or rejoin. There is no merge or reconciliation with a local journal.

Assignment, consumption, and cursor changes update the live host record
immediately. The next normal multiplayer floor save persists them with the
native `RunState`. If the host crashes before that checkpoint, the preceding
checkpoint wins and the rolled-back receipt may become claimable again.

### 7.4 Aggregate gold redemption

Gold receipts accumulate in the claimant's raw per-character AP bank. The reward
menu materializes all currently unredeemed raw gold as one immutable button
claim:

```csharp
public sealed record ApGoldClaim(
    int SourceAmount,
    int GrantedAmount,
    int RedeemedRawAfter);
```

`SourceAmount` is the raw AP bank consumed by this click. `GrantedAmount` is the
concrete wallet mutation after run-specific effects such as Poverty.
`RedeemedRawAfter` is the claimant's cumulative redemption cursor. Only
`GrantedAmount` is sent through `RewardSynchronizer.SyncLocalObtainedGold`. The
host validates the expected cursor before applying the wallet mutation and then
updates the claimant's live cursor after successful application.

The cursor persists in the claimant's host-owned `APProgressUnified`, so AP
history replay cannot recreate an already claimed button. Other peers do not
need the raw calculation. A later gold receipt simply creates a new aggregate
claim for the remaining raw balance. The first multiplayer implementation does
not refund previously withheld gold when Poverty is removed; adding that
correction requires a later explicit contract.

## 8. AP callback and idempotent execution

### 8.1 Callback pipeline

The following pipeline is for discrete receipt-backed grants. Aggregate gold
uses the button-claim and redemption-cursor contract in section 7.4.

```text
AP receipt appears at an own-slot connection or the host shared-slot connection
  -> enqueue work on the established main-thread boundary
  -> own-slot process records an in-memory receipt; host relays shared-slot receipt deltas
  -> derive ApGrantId from RunId, AP source, received-item index, and claimant
  -> compare with the claimant's host-owned UsedItems/cursor
  -> classify the grant
  -> resolve or load a concrete assignment and have the host accept it
  -> route now, or schedule it for its required safe execution boundary
  -> synchronize and apply the concrete native effect
  -> host immediately records consumption in the claimant's live progress
  -> claimant updates its in-memory view
  -> the next floor checkpoint persists native state and AP progress together
```

No AP Guest or unrelated remote peer queries AP or sends checks. An own-slot
player may query and send through its own AP connection. All claims remain
host-validated.

### 8.2 Executor pseudocode

```csharp
async Task ExecuteManagedGrant(RitsuLibManagedNetActionContext<ApGrantPayload> context)
{
    var grant = context.Message;
    ValidatePayloadSchemaAndValues(grant);
    ValidateSourceAndClaimant(grant.GrantId, context.Player.NetId);

    var progress = HostProgress.For(grant.GrantId.ClaimingNetId);
    if (progress.UsedItems.Contains(grant.GrantId.ReceivedItemIndex))
        return;

    // Uses only the concrete payload and context.Player. No AP reads or RNG.
    await ApplyConcreteEffect(context.Player, grant);

    // Update immediately in host memory; the next floor save makes it durable.
    progress.UsedItems.Add(grant.GrantId.ReceivedItemIndex);
    PublishProgressView(grant.GrantId.ClaimingNetId, progress);
}
```

Consumption is written only after the primary effect succeeds. Both native
state and host AP progress enter the next normal floor checkpoint. Reliable
message delivery alone is not persistence. Secondary visuals, notifications,
or telemetry are best effort and must not reopen or duplicate the grant.

### 8.3 Claim-state table

| Boundary | Claimant view | Host state | Other peers |
|---|---|---|---|
| Receipt observed | Add to in-memory source catalog | Host already knows shared receipts; own-slot claims remain gated by validation | No mutation |
| Assignment resolved | Wait for host acceptance before exposure | Store stable assignment in claimant progress | Receive only concrete spec when needed |
| Request issued | Keep UI pending | Validate source, assignment, and unused state | Wait for synchronized action |
| Executor succeeds | Mark in-memory view consumed | Update live `UsedItems`/cursor immediately | Apply concrete native effect |
| Selectable card/potion is skipped or unavailable | Keep claimable under existing semantics | Do not consume | Apply synchronized no-selection result if required |
| Duplicate request after success | Show consumed | Reject from live progress | No new action |
| Executor throws | Keep pending and log context | Do not consume | Native failure/checksum behavior applies |
| Claimant rejoins | Replace local view | Send host progress and proper receipt snapshot | Native rejoin snapshot restores game state |

Gold and relic grants retain their existing infallible reward-boundary
semantics. Do not add speculative distributed rollback.

## 9. Routing table

| AP-derived effect | When it may execute | Transport | Payload/result |
|---|---|---|---|
| Gold obtained/lost | Safe noncombat reward boundary | `RewardSynchronizer` | Concrete amount |
| Relic obtained/skipped | Native reward boundary | `RewardSynchronizer` or synchronized reward set | Concrete relic ID/result |
| Potion obtained/skipped | Native reward boundary | `RewardSynchronizer` or synchronized reward set | Concrete potion ID/result |
| Card obtained/skipped | Native card reward/choice boundary | Native reward/choice synchronization | Concrete card/result |
| Progressive Starter card/relic | Run initialization or live receipt | Synchronize the concrete deck/relic transition; managed action only where native removal/transform support is absent | Concrete model IDs and transition |
| Linked/private reward choice | After owner chooses | Native final-result synchronization | Chosen concrete model, never rejected candidates |
| AP location in native reward set | Matching reward lifecycle | `RewardsSetSynchronizer` plus RitsuLib custom reward | Deterministic reward-set entry; check writer follows the player's AP source mode |
| Rest-site/AP option list | Before native option indexes are used | Publish compact per-owner option specification, then native index synchronization | Ordered AP additions/removals |
| Host's Ascension Down | Next safe noncombat boundary | RitsuLib managed `NonCombat` action | Concrete Ascension level to remove from the shared set |
| Non-host Ascension Down | Owner-local processing | Local no-op | Mark handled for that owner; never mutate the shared set |
| AP combat buff | Next combat start | RitsuLib managed `Combat` action | Concrete power/effect ID and amount |
| AP-only UI, scouting, notifications | Any safe local boundary | Local or host-relayed view | No replicated mutation |

If a native synchronizer rejects execution during combat, the grant remains
pending until its next supported noncombat boundary.

## 10. Combat buff contract

Each player independently consumes at most one buff per combat from that
player's own AP received-item queue:

```text
AP receipt becomes available to Alice
  -> host accepts the concrete buff into Alice's progress FIFO

At the next combat start
  -> Alice marks that she has attempted a buff for this combat
  -> Alice peeks at most one FIFO entry
  -> Alice requests grant.combat_buff owned by Alice's STS Net ID
  -> host orders Alice's request with any other players' requests
  -> every peer applies Alice's concrete buff
  -> host removes Alice's FIFO head and records consumption immediately
```

A buff received after combat has begun waits until the next combat. Five queued
buffs for Alice are consumed over five successive combats, while Bob's queue is
consumed independently. A failed attempt does not advance to a second buff in
the same combat.

The combat-start integration must use a stable combat identity or an equivalent
one-shot guard so scene re-entry and repeated callbacks cannot submit two buffs
for the same player and combat.

### 10.1 Ascension Down boundary

The lobby initializes every peer from the fixed host's effective Ascension
set. If that host is AP-bound, its later Ascension Downs request transitions of
the shared set for both the host and its AP Guests. A host without an AP slot
has no AP Ascension Downs and cannot have AP Guests. A non-host own-slot
Ascension Down is a claimant-local no-op for this run and is never submitted as
a shared effect:

```text
AP-bound host Alice receives Ascension Down: Poverty
  -> host records the pending concrete remove-Poverty grant
  -> if combat is active, the grant waits
  -> at the next safe noncombat boundary Alice requests grant.non_combat
  -> the host orders the accepted action
  -> every peer removes Poverty from the shared effective set
  -> no retrospective multiplayer gold refund is issued in the first implementation
  -> host records consumption in Alice's live progress
```

This preserves the host as the sole Ascension authority. Bob's AP
configuration and Ascension Downs never affect Alice's run while Bob is a
client. There is no later authority transfer that needs to preserve Bob's Downs
as dormant shared effects. Duplicate host removal is rejected by `ApGrantId`,
not by assuming that removing an already absent level is harmless.

## 11. RNG and assignment stability

### 11.1 Default: AP-owned keyed RNG

Use an AP-owned, order-independent RNG for assignments tied to a grant. Derive
it from stable inputs such as:

```text
opaque RunId
+ run seed
+ AP room/team/slot
+ received-item index
+ claiming STS Net ID
+ assignment domain/version
```

Example domains:

```text
sts2ap/reward/relic/v1
sts2ap/reward/potion/v1
sts2ap/reward/card/v1
sts2ap/buff/v1
```

This gives the mod control over assignment rules, independently supports several
claimants for one shared receipt, and avoids coupling a result to callback timing
or another player's random draws. Cache the resolved assignment in that
claimant's host-owned progress before exposing it or requesting execution. The
cache, not repeated rolling, is the primary stability guarantee.

```csharp
ResolvedGrantAssignment ResolveAssignment(
    ApGrantId grantId,
    string domain,
    IEnumerable<string> candidateModelIds)
{
    if (Progress.Assignments.TryGetValue(grantId, out var cached))
        return cached;

    var candidates = candidateModelIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    Require(candidates.Length > 0);
    var material = $"{domain}|{grantId.RunId}|{RunSeed}|{grantId.ApRoomSeed}|{grantId.ApTeamId}|{grantId.ApSlotId}|{grantId.ReceivedItemIndex}|{grantId.ClaimingNetId}";
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
    var selected = candidates[(int)(ReadStableUInt64(digest) % (ulong)candidates.Length)];

    var assignment = new ResolvedGrantAssignment(grantId, domain, selected);
    Progress.Assignments.Add(grantId, assignment);
    HostProgress.AcceptAssignment(grantId.ClaimingNetId, assignment);
    return assignment;
}
```

Assignments must remain stable within the same supported game version. No
cross-game-version stability promise is required; bump the assignment domain
when a compatibility change intentionally alters the algorithm or pool. A host
crash before the next floor checkpoint may roll back a newly accepted
assignment; after a successful checkpoint it restores from host progress.

### 11.2 RitsuLib named streams

Use `RitsuLibFramework.GetModRunRng` or `GetModPlayerRng` only when the mechanic
is inherently sequential and should participate in a named run/player stream.
Stable example stream IDs are:

```text
sts2ap/sequential/run/v1
sts2ap/sequential/player/v1
```

All peers must consume a replicated sequential stream in the same synchronized
order. AP receipt callback timing must never advance a shared stream. Even when
a RitsuLib stream is used, persist the concrete assignment by `ApGrantId` before
exposing it to execution or UI.

## 12. Crash windows and recovery

| Crash/failure window | Recovery rule | Residual risk |
|---|---|---|
| Before the host accepts an assignment | Recompute; it was never exposed as stable | Sequential RNG must not be used unless its state is safely committed. |
| After host accepts an assignment, before request | Reuse the live host assignment | A host crash before the floor checkpoint may roll it back and allow a reroll. |
| After request, before executor | Do not consume; claimant may retry after the native action path resolves | Transport behavior requires two-client proof. |
| After primary effect and live host consumption, before floor checkpoint | Host crash restores the preceding checkpoint; effect, consumption, and assignment may roll back together | The receipt becomes claimable again. |
| After the floor checkpoint | Restore native effect and host progress together; do not replay | Requires source/runtime proof that both share the same checkpoint. |
| One peer's executor throws | Do not roll back successful peers | Combat checksum detects divergence and follows native failure UX; noncombat needs explicit validation. |
| Client local storage is empty | Replace its in-memory view from host progress, then overlay the appropriate receipt source | No client-local multiplayer journal exists. |
| Check lost before host checkpoint and AP acknowledgment | Accept loss | This is the deliberate good-enough checkpoint boundary. |

The first implementation should follow native STS2 error handling and observe
player reaction before designing custom recovery UI.

## 13. Persistence, reconnect, and compatibility

- Restore shared STS state from the MegaCrit run snapshot.
- Restore the opaque `RunId`, launch-frozen settings, participation/AP-source
  mapping, and every player's `APProgressUnified` from the host snapshot.
- Own-slot players enumerate current `AllReceivedItems` and checked locations
  from their direct AP connection. AP Guests receive the host's current
  revisioned receipt catalog. Vanilla Guests require neither.
- Rejoin preparation order is: restore host progress; obtain the correct receipt
  source; subtract host-authoritative consumed indices; restore assignments,
  cursors, pending buffs, and pending checks; add genuinely new receipts; then
  publish readiness. AP processing and reward UI remain paused until both host
  progress and the receipt source are ready.
- Reject a different room seed, team, or slot for an own-slot rejoin. An AP Guest
  remains bound to the same host source and never becomes a Vanilla Guest.
- Require the reconnecting process to retain its original run-scoped STS Net ID.
  Compare the live connected Net ID set with `RunState.Players` for diagnostics,
  but rely on MegaCrit's admission check and rejoin snapshot for native state
  recovery. Do not match or rebind a participant by AP slot identity.
- Replace every reconnecting client's in-memory AP view from the host snapshot;
  do not merge a client-local journal.
- Do not use client-local files or AP DataStorage as multiplayer journals.
  Pending checks that must survive a checkpoint belong in host-owned per-player
  progress.
- Update consumption, cursors, and exposed assignments immediately in host
  memory. Persist them at normal STS2 multiplayer floor checkpoints; do not force
  a full save after every AP effect.
- The host remains host. Loss of the host save ends the run.
- Leaving multiplayer discards that multiplayer `RunState` and its host-owned AP
  progress. Starting singleplayer creates a fresh `RunId`; it does not convert,
  fork, or copy the multiplayer save. A directly connected AP session may remain
  available as a connection, but multiplayer consumption does not become
  singleplayer progress.
- Reject unknown grant kinds, model IDs, and payload schema versions.
- Let native STS2 compatibility rules own game-version compatibility.
- Log AP slot ID, received-item index, owner Net ID, effect kind, execution
  boundary, and payload schema version without logging credentials.

## 14. Open implementation proofs

1. Verify managed `Combat` and `NonCombat` actions with two real clients,
   including requester-local execution, host ordering, replay, and exceptions.
2. Prove the exact safe combat-start point and one-shot combat identity for
   submitting one buff per owner.
3. Verify native synchronization for each Progressive Starter transition,
   especially removal and Orobas transformations during run initialization.
4. Verify with `ap state lobby` on the host that every active Own AP Slot,
   AP Guest, and Vanilla Guest contribution arrives before Ready validation with
   the proper receipt-source state, and that frozen settings match every peer.
5. Test every crash window in the table, especially host crash before and after
   a floor checkpoint containing native effect and per-player AP progress.
6. Prove own-slot players and AP Guests can rejoin with empty local storage and
   rebuild their views from host progress plus the correct receipt source.
7. Leave a multiplayer run, start a fresh singleplayer run, and verify it uses a
   new `RunId` and normal singleplayer AP reconstruction without loading or
   copying the discarded multiplayer `RunState` or host progress.
8. Prove host receipt snapshots/deltas and both `SharedSlotCheckScope` values,
   including duplicate character-location IDs and host AP disconnection.
9. Decide the detailed synchronization contract for Death Link separately.

## 15. Evidence and validation boundaries

This architecture is based on static inspection of the current game,
RitsuLib, and client sources, including:

- `ActionQueueSynchronizer`
- `RewardSynchronizer`
- `RewardsSetSynchronizer`
- `ChecksumTracker`
- `RitsuLibManagedNetActions`
- `RunSavedData<T>` and `PlayerRunSavedData<T>`
- `RitsuLibFramework.GetModRunRng` and `GetModPlayerRng`

Static evidence does not prove that modded payloads serialize or execute
correctly between real peers. C# compilation and two-client in-game validation
are not available from this design-only update. The RFC remains Draft until the
runtime proof matrix succeeds.
