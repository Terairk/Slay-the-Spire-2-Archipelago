# ADR 004: Keep multiplayer AP progress in the host checkpoint

> Updated participation contract: [ADR 005](005-direct-ap-connections.md) supersedes the guest receipt relay, shared-check scope, and guest routing described below. Every AP participant now connects directly; host-owned per-player progress remains.

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

MegaCrit persists an active multiplayer run through the fixed host's canonical
run save. The same snapshot already contains every player's resulting cards,
relics, potions, gold, and other native game state. Maintaining a second durable
AP journal on each client would create multiple recovery authorities and require
reconciliation between the host checkpoint, client-local files, AP DataStorage,
and AP server history.

The desired recovery rule is deliberately simpler: the host checkpoint is the
official multiplayer run. Clients may keep an in-memory AP view, but no client
stores multiplayer AP progress durably in a local file or AP DataStorage.

## Decision

The fixed STS host owns all durable, run-scoped multiplayer AP progress. Store
one progress record per committed STS Net ID in the host-carried run snapshot.
Each record contains:

- the frozen participation mode and AP source;
- consumed received-item indices;
- aggregate cursors such as raw gold redeemed;
- reward-attempt and bank counters needed to reconstruct availability;
- stable card, relic, Ancient, and potion assignments;
- progressive starter state and other run-scoped AP state not already present
  in native `RunState`; and
- outstanding location checks when they must survive a host checkpoint.

Do not persist `AllReceivedItems`. A directly AP-bound process reconstructs that
list from its AP SDK session. An AP Guest reconstructs it from the host's
in-memory receipt snapshot. Receipt metadata remains valid only for the frozen
AP room/team/slot identity.

Use one reusable data-only model, conceptually `APProgressUnified`, for the AP
fields shared by singleplayer and multiplayer. Singleplayer composes it with the
opaque MegaCrit save envelope. Multiplayer places it in the host-owned
`PlayerRunSavedData` entry keyed by STS Net ID. The two modes may add genuinely
mode-specific fields, but shared semantics should not be duplicated.

```text
Singleplayer SerializableAP
|- opaque MegaCrit SaveData
`- APProgressUnified

Host multiplayer snapshot
`- Players[NetId]
   |- frozen AP source or Vanilla Guest
   `- APProgressUnified
```

`PlayerRunSavedData` is per-player-shaped data inside the host-carried shared
snapshot. It does not imply a durable save on each client. Replicated clients may
hold the snapshot in memory because MegaCrit needs it for live play and rejoin,
but only the fixed host writes the canonical run save.

## Receipt identity and shared-slot consumption

A received-item index is stable only within one AP source. The complete source
identity is room seed, team, slot, and received-item index. Because the launch
contract freezes each player's source, a player's compact `UsedItems` collection
may store only indices.

Consumption is stored per STS player rather than as one player set attached to
every item. In a shared host slot, receipt 74 may therefore be consumed for
Alice and unconsumed for Bob. Alice and Bob may also hold different stable
assignments for that same receipt. Vanilla Guests have no AP progress record
beyond their frozen participation marker.

## Assignment timing

When a concrete assignment is first exposed to a player, send it to the host and
make it usable only after the host accepts it into that player's live progress.
This immediately stabilizes reopening the reward UI and a client disconnect/
rejoin while the host remains alive. Other peers need only the transient
concrete reward specification and the resulting native game state.

The next normal floor checkpoint makes the assignment durable. If the host
crashes before that checkpoint, the assignment rolls back and may be generated
again. This is the same accepted rollback boundary as consumption and the
primary effect; no additional assignment journal is added.

## Grant and save timing

For a claimable AP reward:

1. resolve or load the claimant's stable concrete assignment;
2. synchronize and apply the concrete native game effect;
3. after successful application, immediately update the claimant's consumed
   index or aggregate cursor in the host's live progress;
4. update the claimant's in-memory view; and
5. let the next normal STS2 multiplayer floor save persist both native state and
   AP progress in the same canonical snapshot.

The host update must not wait for the floor save, because the same receipt must
not be claimable twice during one floor. The design does not force an additional
disk save after every AP action.

The last successful host checkpoint is authoritative. A host crash before the
next checkpoint may roll back both the native effect and its consumption or
assignment. After restore, the AP receipt becomes claimable again. This bounded
rollback is accepted instead of adding distributed transactions, ledgers,
rollback messages, or forensic recovery.

## Location checks

Multiplayer clients do not use durable local pending-check files or AP
DataStorage outboxes.

- For the shared host slot, only the host submits checks. Outstanding checks may
  live in the host's per-player/run progress until the AP server confirms them.
- For an independent AP slot, the owning client transmits through its own AP
  connection, but any run-scoped outstanding-check state that must survive a
  checkpoint remains in its host-owned player progress. On reconnect, AP
  checked-location history is used to remove already confirmed entries.

If a check is earned and all evidence of it is lost before a successful host
checkpoint or AP server acknowledgment, the check may be lost. That is part of
the same good-enough checkpoint boundary; no client-local durable exception is
introduced.

## Load and rejoin

The host loads the canonical snapshot for the same `RunId`. MegaCrit admits
only the original run-scoped Net IDs. The mod restores each player's frozen
participation mode, AP source, consumed indices, cursors, assignments, and
pending checks from the host snapshot.

A directly AP-bound player then retrieves current `AllReceivedItems` and checked
locations from its own AP connection. An AP Guest receives the current host-slot
receipt catalog from the STS host. Item callbacks and reward UI remain paused
until that receipt source and the restored host progress are both available.
The process subtracts the host-authoritative consumed indices, restores stable
assignments/cursors, and only then exposes genuinely pending receipts.

If the AP source is temporarily unavailable, ordinary STS play may continue.
Cached previously received rewards remain claimable. No genuinely new receipt
or unconfirmed check can flow until the corresponding AP connection returns.

On a client disconnect, the host retains that player's live and checkpointed AP
progress. Rejoining the same Net ID simply replaces the client's in-memory view
with the host state and current receipt catalog. There is no merge with a local
journal and no AP-slot-based player rebinding.

## Singleplayer run identity

Singleplayer continues to persist its AP progress in its own combined save
envelope. Leaving multiplayer and selecting singleplayer starts a fresh
`RunId`; it never continues, forks, or converts the multiplayer `RunState`.
Whether an AP session remains connected does not transfer multiplayer progress
into the fresh singleplayer run.

## Consequences

- The host save is the one recovery authority for the multiplayer run.
- Client restart and cross-machine rejoin are possible when the same native Net
  ID and AP identity can reconnect, because no private local journal is needed.
- Stable assignments survive successful floor checkpoints.
- Host-save loss means multiplayer-run loss.
- The checkpoint rule permits bounded rollback or loss before the next host
  save/AP acknowledgment.
- AP credentials and full received history remain outside the host save.
- Host migration remains unsupported.

## Rejected alternatives

### Durable owner-local journals

They create an additional recovery authority and require reconciliation with
the host checkpoint. Multiplayer clients intentionally retain only in-memory AP
views.

### Persist all received items in the host save

The AP server or host receipt relay can reconstruct them. Persisting full
receipt history unnecessarily enlarges and couples the run schema.

### AP DataStorage as a second journal

This adds precedence and conflict rules without improving the host-owned run's
canonical boundary.

### Force a host save after every AP action

Immediate in-memory updates prevent same-floor duplication. Normal multiplayer
floor checkpoints provide adequate durability without introducing new save
timing risks.

## Validation required

- Claim twice during one floor and prove the immediate host in-memory update
  rejects the duplicate before any disk save.
- Save on the next floor, load, and prove consumed indices, aggregate cursors,
  stable assignments, and native effects restore together.
- Crash before a floor checkpoint and confirm the prior host checkpoint wins,
  with the rolled-back receipt claimable again.
- Rejoin an own-slot player and an AP Guest with empty local storage; both
  reconstruct their in-memory views from host progress plus their proper receipt
  source.
- Confirm no multiplayer progress, assignment, or pending-check journal is
  written to a client-local file or AP DataStorage.
- Confirm full `AllReceivedItems` and AP credentials never appear in the host
  save.
