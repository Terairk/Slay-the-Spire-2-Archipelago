# ADR 002: Separate AP source authority from replicated game effects

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

An AP item first appears at one authoritative AP source: either an independently
AP-bound player's connection or the fixed STS host's shared-slot connection. A
MegaCrit run is replicated: every peer contains every player's deck, relics,
potions, gold, combat state, and shared run data.

Synchronizing the complete AP SDK session would expose credentials and couple
the game protocol to AP implementation details. Keeping every AP effect local
would cause MegaCrit state divergence. Persisting client-local AP journals would
also conflict with the host checkpoint as the one multiplayer recovery authority.

## Decision

Keep AP server access at the owning connection, keep durable multiplayer AP
progress in the host snapshot, and synchronize the smallest concrete
consequence that changes MegaCrit state.

```text
AP source                       Durable progress              Replicated effect
---------                       ----------------              -----------------
Alice's own AP connection       Host Players[Alice]           Alice gains Vajra
Host shared AP connection       Host Players[Bob AP Guest]    Bob gains Vajra
No source for Vanilla Guest     No AP progress                Native STS reward
```

For an independently AP-bound player, that process reads its slot and sends its
own AP checks. For an AP Guest, only the STS host reads the shared slot and sends
checks; the guest receives an in-memory receipt catalog from the host. AP Guests
never open an AP connection. Vanilla Guests have neither AP operations nor AP
progress.

Replicated operations include every mutation that changes a player's or the
run's MegaCrit state. Examples:

```text
AP cause                              Replicated STS consequence
--------                              --------------------------
Alice claims aggregate gold           Alice gains the concrete wallet amount
Bob claims host-slot receipt 74        Bob obtains the assigned relic
Alice buys an AP shop location         Alice loses the concrete gold amount
Bob receives a combat buff             Bob gains the concrete power
```

The claimant or host may prepare the concrete model, but the host validates the
receipt source, claiming Net ID, current assignment, and unused state before
accepting the operation. Every peer reproduces only the concrete STS result.

## Transaction identity and per-player consumption

A discrete grant is identified conceptually by:

```csharp
ApGrantId(RunId, RoomSeed, Team, Slot, ReceivedItemIndex, ClaimingNetId)
```

The claiming player is required because a host-slot receipt is independently
claimable once by the host and every AP Guest. The host stores consumed indices
inside each player's frozen AP-source record, so an implementation may omit
redundant source fields from that compact collection while retaining the full
identity at message and validation boundaries.

Aggregate claims use an equivalent stable identity derived from the `RunId`, AP
source, claiming player, effect kind, and cumulative cursor. Gold receipts form
one raw bank per claiming player and the UI materializes currently unredeemed
gold as one aggregate claim. Only the concrete wallet amount is replicated; the
raw redemption cursor is updated immediately in that player's host-owned
progress.

## Assignment and commit boundary

Stable card, relic, potion, and Ancient assignments live in the claimant's
host-owned AP progress. When an assignment is first exposed, the host accepts it
into live progress before the player can use it. Other peers receive only the
concrete assignment needed for the native reward flow.

For a consumed reward, the order is:

1. load or host-accept the claimant's stable assignment;
2. apply the concrete synchronized game effect;
3. after successful application, immediately mark the receipt or aggregate
   cursor consumed in the host's live per-player progress; and
4. let the next normal multiplayer floor checkpoint persist the native effect
   and AP progress together.

The immediate host update prevents same-floor duplicate claims. It does not
force an extra disk save after every action.

## Location checks

The host is the only AP check writer for its shared slot. The launch-frozen
`SharedSlotCheckScope` client setting chooses whether it sends checks only for
the host character or automatically loops over all host-slot AP participants.
The host already knows every committed character from native run state; an AP
Guest sends no custom check-forwarding message. The host resolves and
deduplicates character-specific location IDs.

An independently AP-bound process continues to submit checks to its own slot.
Any pending check state that must survive a multiplayer checkpoint is stored in
that player's host-owned progress, not a durable client-local file or AP
DataStorage.

## Consequences

- Remote peers do not need AP credentials or another slot's SDK session.
- AP Guests need a host-relayed, in-memory receipt snapshot and deltas.
- Every claim is validated against the host's canonical per-player progress.
- Private candidate generation is allowed only when the resulting assignment is
  accepted by the host before exposure and does not mutate replicated RNG or
  pools inconsistently.
- Leaving multiplayer starts a fresh singleplayer run and does not copy the
  host-owned multiplayer progress.
- Host migration remains unsupported.

## Failure rule

An AP connection failure must not cause peers to apply different MegaCrit
effects. Cached already-received rewards remain claimable. No new receipt or
shared-slot check flows while its authoritative AP connection is unavailable.

The last successful host checkpoint is the recovery authority. A host crash
before the next floor checkpoint may roll back the concrete effect, assignment,
and consumed marker together, after which the receipt is claimable again. A
checkpoint containing them restores them together and suppresses a duplicate.
Do not add distributed rollback, client-local journals, an applied-effect
ledger, or all-peer transaction recovery to close the accepted checkpoint
window.

## Validation required

- The same shared-slot receipt can be claimed once by each host/AP Guest and is
  rejected on the same player's second attempt.
- A Vanilla Guest receives native rewards and never appears in AP claim/check
  state.
- Concrete card, relic, potion, gold, and buff effects match on every peer.
- The host immediately blocks same-floor duplicate claims before a disk save.
- Save/load restores native effects and per-player AP progress together.
- AP Guest logs contain no AP connection or check submission.
- Both shared-slot check scopes produce only the host-submitted, deduplicated
  location IDs.
