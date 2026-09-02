# Domain typing and F# opportunity audit

- **Status:** Proposed
- **Audit branch:** `audit/domain-typing`
- **Audited base:** `multiplayer-main-checks` at `b630cc963c31c9c753aceab89f5c7b6d49d587d2`
- **Scope:** C# Godot client, multiplayer persistence and transport, and the Python APWorld contract

## Summary

The project would benefit from richer domain types, but not from wrapping every primitive.
The largest gains are in places where several collections, booleans, nullable values, or magic
strings collectively describe one state. Those representations allow contradictory states and
force callers to reconstruct the same invariants repeatedly.

F# is most useful here as a small, engine-independent domain library. It can provide discriminated
unions, exhaustive pattern matching, immutable state transitions, validation, and deterministic
planning. C# should continue to own Godot nodes, Harmony patches, MegaCrit model interaction, and
plain serialization DTOs.

The recommended boundary is:

```text
JSON / RitsuLib / Godot / Harmony DTOs in C#
                    |
                    v
         validate and convert once
                    |
                    v
     F# domain values and pure decisions
                    |
                    v
       C# performs engine side effects
```

This avoids relying on Godot, Harmony, or `System.Text.Json` to construct F# values directly. It
also gives C# callers a gradual migration path instead of requiring a rewrite.

## Adoption principle: F# is a tool, not the objective

The motivation for exploring F# is partly to learn and use the language. That is a legitimate
reason for a small, reversible experiment, but not a reason to convert working C# merely because a
function happens to be pure.

Pure functions, immutable records, pattern matching, value objects, and closed class hierarchies
can all be written in modern C#. A candidate should move to F# only when F# materially improves at
least one of the following:

- A discriminated union prevents invalid combinations that a C# property bag currently permits.
- Exhaustive pattern matching makes adding a new domain case produce useful compiler errors.
- A sequence of state transitions is substantially clearer as immutable transformations.
- Constrained construction prevents invalid IDs, ranges, revisions, or amounts from entering the
  core logic.
- The code benefits enough from F#-native property testing or computation expressions to justify
  the language boundary.
- The resulting public API remains straightforward for C# teammates to call and review.

Prefer C# when the same improvement is clear with a small record, enum, factory, or pure static
method. Also prefer C# when the code mostly orchestrates Godot, Harmony, RitsuLib, reflection, mutable
MegaCrit models, callbacks, or exceptions from external APIs.

This gives the candidates in this document three practical categories:

| Category | Examples | Recommendation |
| --- | --- | --- |
| F# has a strong structural advantage | Reward variants, reward claim states, progressive starter states, progress operations | Good F# candidates |
| Either language is suitable | Relic selection, shop planning, gold arithmetic, pending-check set reconciliation | Start in pure C# unless an F# experiment or property-testing benefit justifies moving them |
| C# is the clearer owner | Harmony patches, Godot UI, RitsuLib registration, engine mutations, serialization shells | Keep in C# |

Introducing F# should therefore begin with one contained domain problem. It should establish team
conventions for naming, debugging, C# interop, and tests before F# owns critical save or multiplayer
state. If that experiment is harder for the team to maintain than its C# equivalent, retaining the
C# implementation is the correct outcome.

## Priority overview

| Priority | Area | Existing representation | Proposed representation | F# value |
| --- | --- | --- | --- | --- |
| P0 | Reward claims | Assignment maps plus `UsedItems` plus relic claims | One receipt ledger with explicit claim states | Very high |
| P0 | Mirrored rewards | `Kind` plus fields belonging to every possible kind | A discriminated union of reward specifications | Very high |
| P0 | Multiplayer lifecycle | Several pending/active/prepared booleans and nullable globals | One explicit lobby/run state machine | High |
| P0 | Progress replication | Dozens of nullable fields and independent add/remove collections | Typed progress operations or grouped deltas | Very high |
| P1 | Progressive starters | Initialization/support booleans, nullable models, and tier | `Uninitialized`, `Unsupported`, or `Supported` | Very high |
| P1 | Player participation | Participation enum plus conditionally required AP fields | `VanillaGuest` or `OwnApSlot` with its required data | High |
| P1 | Identity primitives | Overlapping `int`, `long`, `ulong`, and `Guid` values | Selective receipt, slot, offset, location, and revision types | Medium-high |
| P1 | Validation outcomes | `bool` plus `out string`, `null`, or `-1` | `Result<'value, 'error>` with typed errors | High |
| P2 | APWorld character and slot data | `str | int` keys and `dict[str, Any]` | Unified character keys and typed wire dictionaries | Medium; implement in Python |

## P0: reward claims and stable assignments

### Existing shape

`ArchipelagoProgress` maintains separate maps for relic, Ancient, card, and potion assignments.
`UsedItems` independently records consumed received-item indexes. The same information is mirrored
in `ApRunProgressState` and decomposed again into progress-delta upserts and removals.

Relics also have `ApRelicReceiptState.Claim`, which independently stores a destination, consumed
flag, bank requirement, reward number, and optional menu assignment. Reconciliation code repairs
the assignment maps and `UsedItems` from that second representation.

Relevant files:

- `client/StS2AP/Models/ArchipelagoProgress.cs`
- `client/StS2AP/Persistence/ApRunProgressState.cs`
- `client/StS2AP/Persistence/ApProgressDelta.cs`
- `client/StS2AP/Persistence/ApRelicReceiptState.cs`
- `client/StS2AP/Utils/RelicRewardUtility.cs`
- `client/StS2AP/Utils/ApMirroredRewardDispatcher.cs`

### Invalid or ambiguous states

- An index can be present in an assignment map and in `UsedItems`.
- A relic claim can be consumed while its assignment remains available.
- A claim can contain a menu assignment while its destination is a chest.
- A delta can independently add consumption and upsert an assignment.
- A delta can contain the same key in its add/upsert and removal collections.
- A bare received-item index can accidentally be treated as globally unique even though durable
  multiplayer identity is AP slot plus received-item index.

### Proposed model

```fsharp
type ReceivedItemIndex = private ReceivedItemIndex of int

type RewardAssignment =
    | CardAssignment of CardAssignment
    | RelicAssignment of RelicAssignment
    | AncientAssignment of AncientAssignment
    | PotionAssignment of PotionAssignment

type RewardClaim =
    | Unassigned
    | Assigned of RewardAssignment
    | Consumed

type RewardLedger = Map<ReceivedItemIndex, RewardClaim>
```

Under this model, `UsedItems.Contains(index)` becomes a query for `Consumed`, while
`RelicChoiceAssignments[index]` becomes `Assigned (RelicAssignment choices)`. Consumption is a
transition that replaces the assignment; it cannot leave both states active.

Not every received AP item needs an assignment. Immediate items can still move directly from
`Unassigned` to `Consumed`. The transition function should enforce which assignments are valid for
which AP item type.

### Migration

Do not delete the existing collections before their consumers have been identified, but no legacy
serialization adapter is required.

1. Introduce the typed runtime ledger and its transition functions.
2. Move reward-menu queries and mutations behind the ledger API.
3. Replace the single-player serialized progress shape and multiplayer progress messages with
   explicit representations of the ledger.
4. Add or bump an explicit schema/version marker so old saves fail with a clear unsupported-save
   result instead of being partially interpreted as the new model.
5. Remove the old collections and their serialization once there are no direct consumers.

Existing single-player AP saves and mixed-version multiplayer sessions are explicitly out of scope.
Users must begin a new AP run after this schema change.

## P0: mirrored reward variants

`ApMirroredRewardSpec` contains a `Kind` and then fields for card rarity and act, rerollability,
unavailability, materialization strategy, state fingerprints, applied effects, and serialized
models. Most fields are meaningful for only some values of `Kind`.

It can therefore represent a relic with card properties, an available reward with an unavailable
reason, or native materialization without the required strategy and fingerprints.

There is no longer a compatibility reason to retain the current flat wire DTO. The replacement can
use explicit tagged wire variants, decoded into:

```fsharp
type MirroredReward =
    | Card of CardRewardSpec
    | Relic of RelicRewardSpec
    | Potion of PotionRewardSpec
    | Ancient of AncientRewardSpec
    | Unavailable of UnavailableRewardSpec

val decodeReward : MirroredRewardInput -> Result<MirroredReward, RewardSpecError>
```

`MaterializationStrategyId` should decode into a closed strategy type with an explicit unsupported
wire value, rather than being compared against strings throughout the dispatcher.

This is the best first F# production experiment because it is mostly pure input validation and
does not need to own a Godot node or Harmony method.

Relevant files:

- `client/StS2AP/Models/ApGrantModels.cs`
- `client/StS2AP/Persistence/ApCardAssignmentState.cs`
- `client/StS2AP/Utils/ApMirroredRewardDispatcher.cs`

## P0: multiplayer lifecycle

`MultiplayerSupport` separately stores pending destination, pending participation, active
participation, whether a real multiplayer run exists, prepared AP identity, whether AP history was
prepared, and whether claims were invalidated. Derived properties choose between pending and active
values depending on the other flags.

Possible representable combinations include a single-player destination with own-slot
participation, an active multiplayer flag with no active participation, or prepared-history state
without a prepared identity.

A domain state machine could resemble:

```fsharp
type PreparedOwnSlot =
    { Identity: ApSessionIdentity
      ReceiptSource: ReceiptSource }

type MultiplayerContext =
    | Idle
    | EnteringSingleplayer
    | EnteringMultiplayer of PendingParticipant
    | ActiveMultiplayer of ActiveParticipant * ClaimCapability
```

This is valuable but invasive: many Harmony patches read the existing static properties. Preserve
those properties initially as projections over the new state and migrate writers first.

Relevant file: `client/StS2AP/Multiplayer/MultiplayerSupport.cs`.

## P0: progress snapshots, deltas, and revisions

`ApProgressDelta` is a wide DTO containing nullable counter replacements, complete map
replacements, dictionary upserts/removals, and set additions/removals. A new field must be added to
`HasChanges`, `Between`, `ApplyToCopy`, cloning, and equality code. Omitting one occurrence can
silently lose replicated state.

A typed internal change stream would make the set of mutations explicit:

```fsharp
type ProgressChange =
    | SetRewardCounter of RewardCounter * int
    | AssignReward of ReceivedItemIndex * RewardAssignment
    | ConsumeReward of ReceivedItemIndex
    | QueueLocation of ApLocationId
    | ConfirmLocation of ApLocationId
    | SetStarter of StarterKind * StarterState

type NonEmptyList<'value> =
    private
        { Head: 'value
          Tail: 'value list }

type ProgressTransition =
    private
        { Base: ProgressRevision
          Next: ProgressRevision
          Changes: NonEmptyList<ProgressChange> }
```

The constructor can require `Next = Base + 1` and at least one change. Because compatibility with
the unreleased delta protocol is not required, these operations can replace the current wire format
rather than compiling back into its wide DTO.

An F# discriminated union is a strong fit for the meaning of this update. An enum alone is not
enough: an enum can identify `SetGoldRedeemed` or `AssignReward`, but it cannot give each case a
different payload type. Pairing an enum with a large nullable payload object would recreate the
current invalid-state problem.

### Multiplayer protocol and save compatibility decision

The experimental multiplayer implementation is not released and mixed mod versions are not a
supported scenario. Players are expected to update the mod together. It is therefore acceptable to
replace the existing progress-delta protocol, change its message key, and bump the run schema rather
than carrying a compatibility adapter for `player_ap_progress_delta_v1`.

Existing single-player AP save compatibility is also not required. `SerializableAP` persists the
same `ApRunProgressState`, so the snapshot and reward-ledger representation may be replaced directly
without a legacy adapter. The new format should still carry an explicit version marker and reject
old saves clearly; permission to break compatibility is not permission to silently misread old
state. Users must begin a new AP run after the schema change.

Initial snapshots should remain separate from live operations: a snapshot establishes or repairs
the complete baseline, while an ordered operation transaction advances one known revision to the
next.

The proposed wire flow is:

```text
explicit operation-list JSON
        -> decode every case
validated non-empty ProgressChange transaction
        -> apply atomically as a pure fold
new typed progress state and revision
```

The operation list needs an explicit stable JSON codec, unknown-operation behavior,
duplicate/conflict rules, and an atomic application contract. Do not make the wire contract depend
accidentally on the default runtime representation of an F# union. Either configure and test one
specific F# JSON representation or write a small explicit codec owned by this project.

### Expected message size

An operation list should be substantially smaller for ordinary one-field updates, but the saving
comes from sending only present operations, not from F# itself.

The current `ApProgressDelta` initializes sixteen collection properties. Those properties are not
annotated to omit empty collections, so a small update carries their property names and empty
`[]`/`{}` values. Using the current property names, an illustrative `UsedItemsAdded = [42]` message
is approximately 649 bytes of JSON before any RitsuLib framing. A readable operation equivalent:

```json
{
  "RunId": "00000000-0000-0000-0000-000000000001",
  "OwnerNetId": 123456789,
  "BaseRevision": 10,
  "Revision": 11,
  "Changes": [
    { "Case": "RewardConsumed", "Index": 42 }
  ]
}
```

is approximately 152 bytes when minified: about 76% smaller in this example. This is a structural
estimate from the source properties, not a captured RitsuLib packet. Actual framing and serializer
configuration still need measurement in the game build.

Large card/relic assignments contain serialized models, so their payload dominates and the
percentage reduction will be smaller. A C# operation hierarchy or tagged-operation DTO can produce
the same compact JSON. The reason to prefer the F# union is compile-time case modelling and
exhaustive application, not bandwidth alone.

### Other multiplayer messages that can now be redesigned directly

The absence of mixed-version compatibility removes earlier migration caution from several other
message families:

| Message family | Current ambiguity | Direct redesign |
| --- | --- | --- |
| Reward menu | `ApMirroredRewardSpec.Kind` plus fields for every reward kind | Encode each reward variant as its own tagged payload |
| Relic receipt request | Nullable `RoomKey` distinguishes a chest request from a menu request | `RequestChestDecision` or `RequestMenuReservations` |
| Relic receipt reply | Nullable `Chest` distinguishes a chest decision from menu indexes | `ChestDecision` or `MenuReservationDecision` |
| Materialization acknowledgement | `Success` plus a failure string | `Materialized digest` or `MaterializationFailed error` |
| Materialization decision | `Approved` plus a failure string | `Approved digest` or `Rejected error` |
| Treasure readiness | One DTO changes meaning through `AllPlayersReady` | Separate `PlayerReady` and host `AllPlayersReady` cases |
| Progressive Starter action | `Reason` controls whether index and character offset must be present | `InitializeStarters targets` or `ApplyStarterReceipt receipt targets` |
| Run/lobby player contribution | Participation enum controls several nullable identity/settings fields | Tagged guest or own-slot contribution payload |
| Shared ascension state | Initialization boolean controls nullable offset and several lists | Absent/invalid/ready ascension payload |

`DeathLinkInboundRequestMessage`, `DeathLinkSendInstructionMessage`, and the managed damage action
already represent genuinely different stages as separate message types. They would benefit from
typed validation and bounded percentage/HP values, but protocol compatibility was not the main
reason to leave their envelopes separate.

`ApAscensionDownActionMessage` is similarly a single coherent action rather than a flattened choice
among variants. Its values can be validated into richer domain types without inventing a union
solely to use F#.

Relevant files:

- `client/StS2AP/Persistence/ApProgressDelta.cs`
- `client/StS2AP/Multiplayer/Messages/ApProgressDeltaMessage.cs`
- `client/StS2AP/Multiplayer/ApRunData.cs`

## P1: progressive starter state

`ApProgressiveStarterKindState` combines `Initialized`, `Supported`, nullable base and upgraded
identities, nullable serialized models, and `AppliedTier`. `ArchipelagoProgress` and
`ApRunProgressState` also store card and relic IDs and tiers as parallel fields.

Examples of currently representable invalid states include:

- `Initialized = false` with populated models.
- `Supported = false` with a Basic or Upgraded tier.
- Upgraded tier without an upgraded model.
- Unsupported tier with valid starter identities.

Suggested shape:

```fsharp
type StarterState<'recipe> =
    | Uninitialized
    | Unsupported
    | Supported of recipe: 'recipe * appliedTier: ProgressiveStarterTier
```

Card and relic recipes should remain different types because they serialize and apply different
MegaCrit models.

Relevant files:

- `client/StS2AP/Persistence/ApProgressiveStarterKindState.cs`
- `client/StS2AP/Persistence/ApProgressiveStarterState.cs`
- `client/StS2AP/Utils/ProgressiveStarterMultiplayer.cs`
- `client/StS2AP/Utils/ProgressiveStarterUtility.cs`

## P1: player participation and AP identity

`ApPlayerRunState` stores `Participation` alongside nullable room seed, team ID, slot ID, slot
settings, receipt data, and readiness. A vanilla guest should have none of the AP-owned fields; an
own-slot participant requires them before launch.

Suggested shape:

```fsharp
type PlayerContribution =
    | VanillaGuest of VanillaProgress
    | OwnApSlot of OwnSlotContribution

type OwnSlotContribution =
    { Identity: ApSlotIdentity
      Settings: FrozenSlotSettings
      ReceiptSource: ReceiptSourceState
      Progress: PlayerProgress }
```

This also simplifies `ApPlayerContextResolver`: callers can match the resolved context instead of
repeating participation and null checks.

There are two related session-identity structures today. The durable outbox identity includes
server authority; the lobby identity does not. They should not be blindly unified, but their names
and relationship should make the scope difference explicit.

Relevant files:

- `client/StS2AP/Persistence/ApPlayerRunState.cs`
- `client/StS2AP/Multiplayer/ApPlayerContextResolver.cs`
- `client/StS2AP/Utils/ApSessionIdentity.cs`
- `client/StS2AP/Multiplayer/MultiplayerSupport.cs`

## P1: selective identity and bounded-value types

The client uses overlapping primitive representations for several unrelated concepts:

- AP slot ID and AP team ID: `int`
- Received-item index: `int`
- Card-reward act index and gameplay act number: `int`
- Character offset and AP location ID: `long`
- MegaCrit player Net ID: `ulong`
- Run, menu, event, and action IDs: `Guid`
- Progress revision: `long`

Useful wrappers include:

```fsharp
type ApSlotId = private ApSlotId of int
type ReceivedItemIndex = private ReceivedItemIndex of int
type CharacterOffset = private CharacterOffset of int64
type ApLocationId = private ApLocationId of int64
type ActIndex = private ActIndex of int
type ActNumber = private ActNumber of int
type ProgressRevision = private ProgressRevision of int64
```

`ApGrantId` already demonstrates the benefit of pairing slot ID with received-item index. It should
be propagated into domain code instead of repeatedly splitting that identity into two fields.

Do not wrap values solely because they are primitive. `NetId`, `RunId`, and other external IDs can
remain primitive at engine and wire boundaries where their names already prevent confusion. Wrap
them when the value crosses into domain logic or can plausibly be exchanged with another value of
the same primitive type.

## P1: typed failures rather than sentinel values

Several APIs use `bool` plus `out string reason`; location resolution and gold calculation also use
`-1` as failure. These approaches lose the reason when callers only retain the boolean and permit a
sentinel to enter normal arithmetic.

Examples:

- `MultiplayerSupport.CanClaimGold`
- `MultiplayerSupport.CanClaimReceivedReward`
- `ApRunData.TryValidateHostLobbyContributions`
- `MultiplayerLocationChecks.ResolveLocationId`
- `ArchipelagoProgress.GoldRemaining`
- `CampaignEntry`, which has independently nullable metadata and error fields

Suggested result types:

```fsharp
type ClaimBlocker =
    | FeatureDisabled
    | ClaimsInvalidated
    | MultiplayerDisconnected
    | CombatActive

type LocationResolutionError =
    | NotCheckWriter
    | UnknownLocation of string

type CampaignLoadResult =
    | Loaded of CampaignMetadata
    | Invalid of CampaignId * CampaignLoadError
```

Messages for logs and UI should be rendered at the boundary. Domain logic should branch on error
cases, not compare or propagate prose strings.

## Additional areas where F# would help

### Deterministic reward planning

`StandardRelicPool` contains hashing, rarity selection, exclusions, fallback order, and stable
choice ordering. The decision portion can be a pure function over a snapshot of candidates:

```fsharp
val selectRelics :
    seed: ChoiceSeed ->
    requested: PositiveInt ->
    candidates: RelicCandidate list ->
    Result<SelectedRelics, RelicSelectionError>
```

C# would collect allowed MegaCrit relic models, pass immutable candidate descriptions to F#, then
resolve the returned IDs and perform grab-bag mutations. The pure selector becomes easy to test for
determinism, uniqueness, exclusion handling, and fallback behavior.

Relevant file: `client/StS2AP/Utils/StandardRelicPool.cs`.

### Pending-check reconciliation

`PendingCheckUtility` combines identity binding, disk persistence, AP acknowledgement, recognized
locations, local pending state, and sending. File and network effects should stay in C#, but set
reconciliation is naturally pure:

```fsharp
type CheckReconciliation =
    { Confirmed: Set<ApLocationId>
      ReadyToSend: Set<ApLocationId>
      RetainedUnknown: Set<ApLocationId> }

val reconcileChecks :
    pending: Set<ApLocationId> ->
    acknowledged: Set<ApLocationId> ->
    recognized: Set<ApLocationId> ->
    CheckReconciliation
```

That would make “confirmed by server”, “eligible for replay”, and “retained because this slot does
not recognize it” separate types of outcome rather than mutations interleaved with I/O.

Relevant file: `client/StS2AP/Utils/PendingCheckUtility.cs`.

### DeathLink validation and planning

DeathLink currently has large compound validation conditions that simultaneously resolve the run,
owner, settings, percentage, targets, and message authority. A typed validator could return a
`ValidatedDeathLinkAction` containing exactly the facts execution needs.

Target HP and damage percentages are also bounded values. Creating `DamagePercent` only for 0-100
and `HitPoints` only within the target's maximum would remove later range checks.

Keep the RitsuLib action descriptor and HP mutation in C#. Move message validation, authorization
decisions, target planning, and deduplication transitions into the pure domain layer.

Relevant files:

- `client/StS2AP/Utils/DeathLinkMultiplayer.cs`
- `client/StS2AP/Multiplayer/Messages/DeathLinkActionMessage.cs`
- `client/StS2AP/Multiplayer/Messages/DeathLinkInboundRequestMessage.cs`

### Ascension projection and receipt transitions

Multiplayer ascension state currently uses readiness booleans, nullable character offsets, several
lists, handled receipt indexes, and process-local construction globals. F# can express an
unprepared/failed/ready projection and make receipt application a pure transition:

```fsharp
type AscensionProjection =
    | NotPrepared
    | PreparationFailed of AscensionError
    | Ready of PreparedAscensions

val applyAscensionDown :
    receipt: AscensionReceipt ->
    PreparedAscensions ->
    Result<PreparedAscensions * AscensionEffect option, AscensionError>
```

Godot/MegaCrit effects remain in C# after the transition has been approved.

Relevant files:

- `client/StS2AP/Persistence/ApRunSharedState.cs`
- `client/StS2AP/Utils/AscensionMultiplayer.cs`
- `client/StS2AP/Utils/AscensionManager.cs`

### Shop inventory planning

Shop slot configuration and received unlock counts are stored in several parallel fields and maps.
The current population patch calculates clamped capacities, removal overflow, available vanilla
slots, and AP check slots before mutating the native inventory.

The calculation can be separated into:

```fsharp
val planShop : ShopConfiguration -> ShopProgress -> ActNumber -> ShopPlan
```

`ShopPlan` would contain category capacities, native availability, AP check positions, remove
availability, and prices. The Harmony patch would execute that plan. This would allow exhaustive
tests without constructing `MerchantInventory`.

Relevant files:

- `client/StS2AP/Models/ArchipelagoSettings.cs`
- `client/StS2AP/Models/ArchipelagoProgress.cs`
- `client/StS2AP/Patches/Patches_ShopSanity.cs`

### Gold calculations

Gold calculation has clear invariants: source, granted, and withheld amounts are non-negative;
granted plus withheld equals source; the redeemed cursor cannot move backwards. `GoldRemaining`
currently returns `-1` on failure, and that value can reach offer arithmetic.

A pure `prepareGoldOffer` function using non-negative amounts and a typed failure is a small,
low-risk F# candidate. The C# grant dispatcher would remain responsible for invoking the game
command and recording consumption at the established boundary.

### Managed-action admission and scheduling

`NonCombatActionAdmissionState` is already a useful aggregation, but it contains ten booleans and
returns a prose reason. F# could calculate `Admitted` or a typed, priority-ordered blocker.

`ManagedActionRequestScheduler` itself is tied to Godot frame callbacks and delegates, so moving the
whole scheduler would add interop complexity. Only its decision model—waiting, stale, admitted,
timed out, failed—should move if it grows more complicated.

### Parsing and configuration normalization

Slot data currently arrives as weakly typed JSON tokens and is gradually assigned into mutable
settings. A pure decoder could accumulate multiple configuration errors instead of failing on the
first missing or malformed value. It can also produce grouped settings such as:

```fsharp
type ShopSanity =
    | Disabled
    | Enabled of ShopConfiguration

type DeathLinkSetting =
    | Disabled
    | Enabled of DamagePercent * DeathFragmentSetting
```

The raw JSON and compatibility handling should remain a C# boundary unless a separate shared
contracts project is introduced.

### Property-based testing

F# also brings a particularly natural fit for FsCheck-style property tests. Useful properties
include:

- Assigning then consuming a receipt never leaves it claimable.
- Encoding and decoding a valid reward ledger is lossless.
- Applying a generated progress delta produces the target snapshot.
- Applying the same idempotent reconciliation twice has the same result as applying it once.
- Stable relic selection gives identical output for identical input.
- Stable relic selection never chooses an excluded relic or duplicates a choice when alternatives
  exist.
- Shop plans never exceed native category capacity.
- Gold granted plus gold withheld always equals source gold.
- Invalid wire messages never produce an executable domain action.

This may deliver as much value as F# production code because these state spaces are otherwise
tedious to cover example by example.

## APWorld opportunities and limits

The Python APWorld must remain Python because it is loaded by the Archipelago framework. F# cannot
replace its generation and logic implementation without adding an impractical cross-process or
generated-code boundary.

Typing improvements should therefore be made natively in Python:

- Replace the mutable `CharacterConfig` plus `**kwargs` with a validated dataclass.
- Replace `dict[str, Any]` slot data with `TypedDict` definitions for character, shop, and complete
  slot-data payloads.
- Replace vanilla-string/modded-integer lookup keys with one explicit `CharacterTableKey` shape or
  simply a normalized string key.
- Consider separating addressed items/locations from event items/locations instead of coupling
  `Optional[int]` codes with `event` flags.
- Give `fill_slot_data` and `interpret_slot_data` precise return types.

The client and APWorld types cannot literally be shared across Python and .NET. Their wire field
names and invariants should instead be documented once and tested on both sides with representative
JSON fixtures.

Relevant files:

- `world/spire2/characters.py`
- `world/spire2/items.py`
- `world/spire2/locations.py`
- `world/spire2/world.py`
- `client/StS2AP/Models/CharacterConfig.cs`
- `client/StS2AP/ArchipelagoClient.cs`

## Areas that should remain C#

F# provides little benefit for code whose main job is adapting an external object model:

- Harmony patch declarations and prefix/postfix signatures.
- Godot node creation, scene-tree navigation, signals, and deferred calls.
- Direct MegaCrit command/model/factory calls.
- RitsuLib descriptor registration and callbacks.
- Reflection-based compatibility shims.
- Small UI formatting and diagnostic snapshot code.
- Serialization DTOs that require parameterless construction and mutable public properties.

These areas can call a typed F# core, but moving their orchestration into F# would make unfamiliar
interop syntax the dominant concern and reduce readability for the rest of the team.

## Suggested project structure

C# and F# source files cannot be compiled in the same project. A separate F# project is required:

```text
client/
  StS2AP.Domain/
    StS2AP.Domain.fsproj
    Primitives.fs
    Results.fs
    RewardClaims.fs
    MirroredRewards.fs
    Progress.fs
    Planning/
      RelicSelection.fs
      ShopPlanning.fs
      GoldPlanning.fs

  StS2AP/
    StS2AP.csproj
    DomainAdapters/
      MirroredRewardAdapter.cs
      RewardLedgerAdapter.cs
    Models/                 # explicit boundary wire/save DTOs
    Patches/                # remains C#
    UI/                     # remains C#
```

`StS2AP.csproj` would reference `StS2AP.Domain.fsproj`. The domain project should not reference the
C# client project, Godot, Harmony, `sts2.dll`, or RitsuLib; doing so would either create a circular
reference or destroy the pure boundary.

The C# project reference is the normal MSBuild dependency declaration:

```xml
<ItemGroup>
  <ProjectReference Include="..\StS2AP.Domain\StS2AP.Domain.fsproj" />
</ItemGroup>
```

Building `StS2AP.csproj` then builds `StS2AP.Domain.fsproj` first and references its output. Rider
understands this project-reference dependency graph, so building the C# project or the containing
solution builds the required F# project automatically. Both projects must target a compatible
framework; for this repository they should initially target `net9.0`.

This repository currently has no tracked solution file. Rider can still open the project or
directory, but a solution containing both projects would make navigation and explicit solution
builds clearer. The existing post-build target copies all dependency DLLs from the C# output
directory, so it should pick up the domain assembly and `FSharp.Core`; packaging must nevertheless
be verified on Windows rather than assumed.

F# files compile in declaration order, so `StS2AP.Domain.fsproj` must list primitives before the
modules using them. Public F# APIs should expose a small C#-friendly facade rather than requiring
ordinary patch code to manually construct nested `FSharpOption` or union representations.

If the domain library eventually needs shared wire DTO types, introduce a third small C# contracts
project referenced by both projects. Do not make the domain project reference the full mod.

## Recommended implementation order

1. Add a minimal `StS2AP.Domain` project and confirm it packages and loads with the mod.
2. Implement mirrored-reward decoding as the first production slice.
3. Add property tests for the decoder and one existing deterministic algorithm.
4. Introduce selective ID and bounded-value types used by that slice.
5. Introduce the reward-ledger runtime model and replace the existing progress save schema.
6. Convert progressive-starter state.
7. Convert player participation and context resolution.
8. Replace the current progress-delta wire format with an explicit operation-list protocol and bump
   the run schema.
9. Consider the multiplayer lifecycle state machine only after the smaller migrations establish
   acceptable C#/F# interop conventions for the team.

Each slice should remain independently reviewable. The first pull request should demonstrate that
the team can read, debug, package, and call the F# code before F# owns critical save or multiplayer
state.

## Existing examples to preserve

The client already contains several good domain-oriented patterns:

- `ApGrantId` pairs the two components of durable receipt identity.
- `ApParticipationKind` and `ApPlayDestination` replace ambiguous booleans.
- `ApFastMpLaunchController` names launch roles and lifecycle states explicitly.
- `ApSessionIdentity.Create` validates durable outbox identity at construction.
- `NonCombatActionAdmissionState` gathers a previously scattered admission snapshot.
- `Patches_ShopSanity.ApSlotCounts` gives a calculated group of counts a name.

These are useful evidence that stronger types need not make the C# side difficult to read.

## Validation status

This is a source-level design audit. No client compilation, Godot execution, Harmony loading,
multiplayer run, or in-game validation was performed. Any F# project addition must be validated in
the real Windows game build and packaging environment before adopting it for saved or replicated
state.
