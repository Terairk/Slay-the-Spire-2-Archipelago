# RFC: Archipelago multiplayer synchronization

- **Status:** Draft
- **Owners:** Unassigned
- **Reviewers:** Unassigned
- **Target release:** Unassigned
- **Last updated:** 2026-08-18

## 1. Summary

Each Slay the Spire 2 process owns one local Archipelago connection for one
distinct AP slot and one local STS player. Every process also maintains
MegaCrit's replicated copy of the complete run, including remote players.

The central boundary is:

> Archipelago owns why an effect exists. MegaCrit's multiplayer layer owns how
> the resulting Slay the Spire state change is reproduced on every peer.

AP callbacks must not directly mutate replicated game state. They create a
stable grant, resolve any random assignment once, and route the concrete result
through either a native MegaCrit synchronizer or a RitsuLib managed action.

## 2. Goals

- Support one AP connection and AP slot per participating STS player.
- Preserve the existing singleplayer experience.
- Use native card, relic, potion, and gold synchronization where it already
  models the required operation.
- Use RitsuLib managed actions for AP-specific mutations from the beginning of
  multiplayer implementation, rather than growing an ad hoc message layer.
- Make AP grant executors deterministic and idempotent.
- Consume at most one queued AP buff per player per combat, in FIFO order.
- Keep private AP state private while sharing the concrete inputs required to
  reproduce STS state.
- Stage and validate the multiplayer launch contract before Ready is accepted.
- Refuse to launch when peers use incompatible AP multiplayer protocols.

## 3. Non-goals for the first multiplayer release

- Allowing a peer without the Archipelago mod to join.
- Sharing one AP socket among several STS players.
- Replacing MegaCrit networking with an independent networking stack.
- Preserving experimental multiplayer saves across protocol changes.
- Making random assignments stable across different game versions.
- Distributed rollback or an all-peer success acknowledgment protocol.

## 4. Topology and authority

```text
Alice's process                         Bob's process
---------------                         -------------
Local STS player: Alice                 Local STS player: Bob
AP session: Alice's AP slot             AP session: Bob's AP slot
Private AP queue/save                   Private AP queue/save

Replicated RunState:                    Replicated RunState:
  Alice                                  Alice
  Bob                                    Bob
  shared run data                        shared run data
```

`LocalContext.GetMe(runState)` resolves the local STS player. The AP slot to STS
player mapping is established in the lobby and is stable for the run. A process
must never infer ownership from `Players[0]` or from the last
`Player.CreateForNewRun` call.

Authority is split as follows:

| State | Authority | Replication rule |
|---|---|---|
| AP socket, credentials, received history, checks | Owning AP process | Never transmit raw connection state or credentials. |
| AP slot settings | Owning AP process | Keep local unless a derived value is required for launch or a concrete effect. |
| AP slot to STS Net ID mapping | Lobby contract | Every peer needs the same mapping. |
| Effective Ascension set | STS host | Host value overwrites each client's local run value. |
| Character selection | Native STS lobby | Players may choose any character unlocked for their AP slot; choices need not match. |
| Gold, deck, relics, potions, powers | Replicated `RunState` | Every peer must reproduce each mutation. |
| Pending AP buffs and AP acknowledgment | Owning AP process | Restore from the owner's AP save/server. |
| Live applied-grant set | Every process | Updated by the same ordered managed actions. |

See ADR 001 and ADR 002.

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

## 6. Lobby staging with RitsuLib run data

RitsuLib `RunSavedData<T>` and `PlayerRunSavedData<T>` are the selected lobby
staging mechanism. Register the slots early and enable `SyncLobbyOnChange` so
each contribution is visible before launch.

```csharp
public sealed record ApLobbyRunState(
    int ProtocolVersion,
    IReadOnlyList<int> HostEffectiveAscensions);

public sealed record ApLobbyPlayerState(
    int ApSlotId,
    bool ApHistoryComplete,
    IReadOnlyList<int> LocallyCalculatedAscensions,
    string PerPlayerRulesFingerprint);
```

The exact serializer shape may change, but the responsibilities must not:

- `RunSavedData<ApLobbyRunState>` stores the host-owned launch contract:
  protocol version and the effective Ascension set used by the actual run.
- `PlayerRunSavedData<ApLobbyPlayerState>` stores each player's AP slot identity,
  readiness, and a diagnostic summary of locally derived per-player rules. Its
  values are keyed by STS player Net ID.
- Shared mod settings that later become part of run generation belong in the
  host run record. Per-player mod/AP settings may be published as derived
  capabilities or fingerprints when useful for lobby diagnostics; they do not
  become equality requirements merely because RitsuLib can synchronize them.
- A player cannot become Ready until the AP connection has loaded slot data and
  processed received-item history.
- An incompatible multiplayer protocol version blocks launch.
- The host's effective Ascension set overwrites every client's local effective
  set. A disagreement is logged and shown in lobby diagnostics so forceful
  normalization does not silently hide a calculation bug. Failure to apply or
  validate the host set blocks launch.
- Shopsanity, campfire sanity, shuffled card rewards, and other settings that
  mainly add per-player checks may differ. They are not included in the shared
  equality rule.
- Native STS lobby state already communicates selected characters. AP only
  validates locally that the chosen character is currently unlocked for that
  slot; no duplicate AP character payload is required.

Progressive Starter does not need a special lobby payload. Its items ultimately
change the owner's real deck or relic collection. Synchronize those concrete
card/relic transitions when they occur. Prefer the native card/relic path; use a
managed action only for a removal or transformation for which STS has no
appropriate synchronizer.

Lobby run data is not a substitute for live `RunState` synchronization. Its
values are committed as a frozen launch contract. Mid-run AP grants still use
the routing and execution pipeline below.

## 7. Grant identity, payload, and persistence

### 7.1 Stable identity

A grant is globally identified within this multiplayer run by the receiving AP
slot and the AP received-item index:

```csharp
public readonly record struct ApGrantId(
    int ApSlotId,
    int ReceivedItemIndex);
```

The STS Net ID is intentionally not part of the identifier. The lobby mapping
resolves `ApSlotId` to an owning player, while the AP receipt index provides
stable deduplication across callback retries and save/load.

### 7.2 Concrete payload

The managed-action payload is a tagged concrete result:

```csharp
public enum ApGrantKind
{
    NonCombatMutation,
    CombatBuff,
}

public sealed record ApGrantPayload(
    int ProtocolVersion,
    ApGrantId GrantId,
    ApGrantKind Kind,
    string EffectId,
    int Amount,
    string? ModelId,
    string AssignmentDomain);
```

Examples are `EffectId = "strength", Amount = 2` and a concrete model ID for a
mod-specific mutation. Deserializers reject unknown protocol versions, kinds,
or effect IDs before mutation.

Native card/relic/potion/gold paths do not need to serialize this exact record,
but they use the same `ApGrantId`, assignment cache, and owner acknowledgment
rules.

### 7.3 Phase-one persistence

Keep the following equivalent fields in `SerializableAP` initially:

```csharp
public sealed record ApGrantPersistence(
    HashSet<ApGrantId> AppliedGrantIds,
    Queue<QueuedBuffGrant> PendingBuffs,
    IReadOnlyList<ResolvedGrantAssignment> Assignments);
```

- `AppliedGrantIds` is one set for every grant kind. Split ledgers only if a
  proven requirement appears.
- `PendingBuffs` is owner-local FIFO state.
- `Assignments` contains one concrete random result per `ApGrantId`; runtime
  code may index the serialized list as a dictionary.
- The owning process persists these fields in `SerializableAP` and recovers AP
  receipt history from its AP server.
- Every peer also maintains an in-memory applied set for every managed action it
  executes. Replay/rejoin must reconstruct that set while replaying actions.

If two-client save/rejoin testing shows that action replay cannot reliably
reconstruct the shared in-memory set, migrate the shared ledger to RitsuLib run
data. Do not make that migration pre-emptively while `SerializableAP` remains
manageable.

## 8. AP callback and idempotent execution

### 8.1 Callback pipeline

```text
AP callback on owning process
  -> enqueue work on the established main-thread boundary
  -> derive ApGrantId from AP slot ID and received-item index
  -> ignore/ack if owner-persisted AppliedGrantIds already contains it
  -> classify the grant
  -> resolve or load its concrete assignment
  -> persist assignment/pending state
  -> route now, or schedule it for its required safe execution boundary
  -> wait for the synchronized local execution
  -> persist AppliedGrantIds on the owner
  -> perform owner-only AP/DataStorage acknowledgment
```

No remote peer queries AP, acknowledges the receipt, or independently chooses a
model.

### 8.2 Executor pseudocode

```csharp
async Task ExecuteManagedGrant(RitsuLibManagedNetActionContext<ApGrantPayload> context)
{
    var grant = context.Message;
    ValidateProtocolAndPayload(grant);
    ValidateSlotOwnsPlayer(grant.GrantId.ApSlotId, context.Player.NetId);

    if (LiveAppliedGrantIds.Contains(grant.GrantId))
        return;

    // Uses only the concrete payload and context.Player. No AP reads or RNG.
    await ApplyConcreteEffect(context.Player, grant);

    LiveAppliedGrantIds.Add(grant.GrantId);

    if (IsLocalApOwner(grant.GrantId.ApSlotId))
    {
        Progress.AppliedGrantIds.Add(grant.GrantId);
        PersistSerializableAp();
        AcknowledgeApGrant(grant.GrantId);
    }
}
```

The ledger entry is written only after the primary effect succeeds. Secondary
visuals, notifications, or telemetry are best effort and must not reopen or
duplicate the grant.

### 8.3 Owner acknowledgment table

| Boundary | Owner state | Remote peer state | AP acknowledgment |
|---|---|---|---|
| Receipt observed | Create `ApGrantId`; do not mark applied | None | No |
| Assignment resolved | Cache concrete assignment | None | No |
| Managed request issued | Retain pending grant | Wait for ordered action | No; `Request == true` is not completion |
| Executor succeeds | Add live ID; persist owner ID | Add live ID | Owner acknowledges once |
| Selectable card/potion is skipped or cannot be taken | Keep claimable under existing item semantics | Apply the synchronized no-selection result if required | No |
| Duplicate callback after success | Do not execute again | No new action | Reassert owner acknowledgment if needed |
| Executor throws | Keep grant pending and log context | Game logs/pops action; combat may checksum-diverge | No |

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
| AP location in native reward set | Matching reward lifecycle | `RewardsSetSynchronizer` plus RitsuLib custom reward | Deterministic reward-set entry and owner-only AP check |
| Rest-site/AP option list | Before native option indexes are used | Publish compact per-owner option specification, then native index synchronization | Ordered AP additions/removals |
| Ascension Down | Next safe noncombat boundary | RitsuLib managed `NonCombat` action | Concrete Ascension level to remove from the host-authoritative shared set |
| AP combat buff | Next combat start | RitsuLib managed `Combat` action | Concrete power/effect ID and amount |
| AP-only UI, checks, scouting, notifications | Any safe local boundary | Local only | No replicated mutation |

If a native synchronizer rejects execution during combat, the grant remains
pending until its next supported noncombat boundary.

## 10. Combat buff contract

Each player independently consumes at most one buff per combat from that
player's own AP received-item queue:

```text
AP receipt arrives for Alice
  -> Alice appends the concrete buff to her persisted FIFO

At the next combat start
  -> Alice marks that she has attempted a buff for this combat
  -> Alice peeks at most one FIFO entry
  -> Alice requests grant.combat_buff owned by Alice's STS Net ID
  -> host orders Alice's request with any other players' requests
  -> every peer applies Alice's concrete buff to its Alice replica
  -> Alice removes the FIFO head, persists AppliedGrantIds, and acknowledges AP
```

A buff received after combat has begun waits until the next combat. Five queued
buffs for Alice are consumed over five successive combats, while Bob's queue is
consumed independently. A failed attempt does not advance to a second buff in
the same combat.

The combat-start integration must use a stable combat identity or an equivalent
one-shot guard so scene re-entry and repeated callbacks cannot submit two buffs
for the same player and combat.

### 10.1 Ascension Down boundary

The lobby initializes every peer from the host's effective Ascension set. An
Ascension Down received after launch is a requested transition of that shared
set, not permission for a client to replace the set with its private AP view:

```text
Alice receives Ascension Down: Poverty
  -> Alice records the pending concrete remove-Poverty grant
  -> if combat is active, the grant waits
  -> at the next safe noncombat boundary Alice requests grant.non_combat
  -> the host orders the accepted action
  -> every peer removes Poverty from the shared effective set
  -> Alice persists and acknowledges the grant
```

This preserves host authority over the actual run while allowing an Ascension
Down delivered to any participating AP slot to affect that run. Duplicate
removal is rejected by `ApGrantId`, not by assuming that removing an already
absent level is harmless.

## 11. RNG and assignment stability

### 11.1 Default: AP-owned keyed RNG

Use an AP-owned, order-independent RNG for assignments tied to a grant. Derive
it from stable inputs such as:

```text
run seed
+ AP slot ID
+ received-item index
+ assignment domain/version
```

Example domains:

```text
sts2ap/reward/relic/v1
sts2ap/reward/potion/v1
sts2ap/reward/card/v1
sts2ap/buff/v1
```

This gives the mod control over assignment rules and avoids coupling a result
to callback timing or to another player's random draws. Cache the resolved
assignment by `ApGrantId` before requesting execution. The cache, not repeated
rolling, is the primary stability guarantee. This follows the existing Ancient
choice pattern of stable seed material plus an assignment cache.

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
    var material = $"{domain}|{RunSeed}|{grantId.ApSlotId}|{grantId.ReceivedItemIndex}";
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
    var selected = candidates[(int)(ReadStableUInt64(digest) % (ulong)candidates.Length)];

    var assignment = new ResolvedGrantAssignment(grantId, domain, selected);
    Progress.Assignments.Add(grantId, assignment);
    PersistSerializableAp();
    return assignment;
}
```

Assignments must remain stable within the same supported game version. No
cross-game-version stability promise is required; bump the assignment domain
when a compatibility change intentionally alters the algorithm or pool.

### 11.2 RitsuLib named streams

Use `RitsuLibFramework.GetModRunRng` or `GetModPlayerRng` only when the mechanic
is inherently sequential and should participate in a named run/player stream.
Stable example stream IDs are:

```text
sts2ap/sequential/run/v1
sts2ap/sequential/player/v1
```

All peers must consume a replicated sequential stream in the same synchronized
order. Private AP callback timing must never advance a shared stream. Even when
a RitsuLib stream is used, persist the concrete assignment by `ApGrantId` before
exposing it to execution or UI.

## 12. Crash windows and recovery

| Crash/failure window | Recovery rule | Residual risk |
|---|---|---|
| Before assignment is persisted | Recompute with keyed RNG or load the existing cache | Sequential RNG must not be used unless its state is safely committed. |
| After assignment persistence, before request | Retry the same concrete payload | None beyond normal reconnect delay. |
| After request, before executor | Do not acknowledge; allow queue/replay or owner retry to deliver it | Transport behavior requires two-client proof. |
| After primary effect, before owner ledger persistence | Live peers' in-memory set rejects a duplicate request | Owner process loss is the highest-risk window; replay/rejoin must be tested for duplicate effects. |
| After ledger persistence, before AP acknowledgment | Owner sees the applied ID and reasserts acknowledgment without reapplying | AP/DataStorage acknowledgment must be idempotent. |
| One peer's executor throws | Do not roll back successful peers | Combat checksum detects divergence and follows native failure UX; noncombat needs explicit validation. |

The first implementation should follow native STS2 error handling and observe
player reaction before designing custom recovery UI.

## 13. Persistence, reconnect, and compatibility

- Restore shared STS state from the MegaCrit run snapshot.
- Restore owner-private pending buffs, assignments, applied IDs, and AP
  acknowledgment state from `SerializableAP` plus the AP server.
- Rebuild live peer ledgers through recorded managed-action replay.
- Never let multiple peers write the same AP DataStorage acknowledgment.
- Reject unknown grant kinds, model IDs, and action protocol versions.
- Refuse AP multiplayer when peers use incompatible multiplayer protocols.
- Let native STS2 compatibility rules own game-version compatibility.
- Log AP slot ID, received-item index, owner Net ID, effect kind, execution
  boundary, and protocol version without logging credentials.

## 14. Open implementation proofs

1. Verify managed `Combat` and `NonCombat` actions with two real clients,
   including requester-local execution, host ordering, replay, and exceptions.
2. Prove the exact safe combat-start point and one-shot combat identity for
   submitting one buff per owner.
3. Verify native synchronization for each Progressive Starter transition,
   especially removal and Orobas transformations during run initialization.
4. Verify that RitsuLib lobby contributions arrive before Ready validation and
   that the host Ascension set is committed identically on every peer.
5. Test every crash window in the table, especially owner loss after effect but
   before `SerializableAP` persistence.
6. Decide the detailed synchronization contract for Death Link separately.

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
