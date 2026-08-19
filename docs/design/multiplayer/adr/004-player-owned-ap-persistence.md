# ADR 004: Keep owner-private AP recovery local and reconstructible

- **Status:** Accepted
- **Date:** 2026-08-19

## Context

MegaCrit persists an active multiplayer run only through the original host's
canonical run save. Each AP-bound process owns a separate AP connection and
slot. Guests have no AP identity or owner-private AP state.

Copying every player's AP journal into the host save would make the host carry
private state it neither owns nor interprets. Mirroring the same journal into
both local storage and AP DataStorage would introduce two recovery authorities.
Neither approach eliminates catastrophic loss; it only adds another layer to a
Swiss-cheese failure model.

The design therefore needs a deliberate boundary between supported recovery
and accepted loss.

## Decision

The original host's save is the only canonical STS run save for an opaque
`RunId`. Host authority cannot transfer during that run. If the host save is
lost, the multiplayer run is lost.

The host save contains only AP data required to interpret the shared run:

- the opaque `RunId`;
- the frozen STS Net ID to `Guest` or AP room/team/slot mapping;
- the applied-effect ledger for every replicated AP effect; and
- AP-derived shared state that MegaCrit does not already serialize.

It does not contain another owner's pending checks, private reward candidates,
raw received history, aggregate cursors, AP acknowledgment state, or AP
credentials.

Each AP-bound process durably writes one schema-versioned owner-local AP journal
scoped by `RunId`, AP room seed, numeric team ID, and numeric slot ID. The
journal may contain:

- prepared concrete reward assignments and pending selectable rewards;
- owner-private aggregate cursors;
- pending buffs and pending owner-only checks; and
- submitted effects awaiting comparison with the host ledger.

Use an atomic local write. Do not mirror the journal into AP DataStorage in the
initial design. The journal improves exact recovery but is reconstructible and
is not a second canonical copy of the shared run.

## Shared commit and save timing

A replicated AP effect and its effect ID are applied by the same host-ordered
operation. They enter the same host checkpoint. The host relies on normal safe
MegaCrit multiplayer save boundaries; it does not force a full save after every
AP effect and does not run an independent periodic save timer.

Orderly quit, disconnect, or desynchronization may request an additional save
only when MegaCrit exposes a safe serialization boundary. That save is
best-effort. A crash before the next checkpoint rolls back both effect and
ledger, so AP reconciliation may replay the effect. A checkpoint containing the
effect also contains its ledger ID and suppresses replay.

The owner writes a concrete `Prepared` record before submitting an effect and
updates it after learning that the host committed it. If the owner loses the
outcome between those points, the record is simply awaiting reconciliation:
host-ledger presence means committed; absence from the restored checkpoint
means it may be retried with the prepared payload.

## Reconnect and salvage

The original host loads the checkpoint for the same `RunId`. Only the frozen
participants may rejoin: an AP-bound player must present the same AP room seed,
team ID, and slot ID, while a guest rejoins as the same guest STS identity. An
AP-bound disconnected player remains bound but AP-suspended; it does not become
a guest.

An AP-bound player reconciles as follows:

1. restore the owner-local journal when available;
2. fetch `AllReceivedItems` and checked locations from the AP server;
3. accept every matching host-ledger ID as already committed;
4. prepare or replay received effects absent from the restored host ledger; and
5. rewrite the local journal from the reconciled result.

If the entire local journal is missing or corrupt, salvage from AP history and
the host ledger. Previously committed shared effects remain protected by the
host ledger. Uncommitted assignments may be regenerated, and checks or private
state that cannot be inferred may be lost. Cross-machine continuation, exact
recovery after owner-local data loss, and protection against every combination
of lost stores are unsupported. The run may become stronger than intended
during salvage; that is accepted rather than adding more persistence replicas.

Failure is isolated to the affected AP owner. That player may remain in STS as
bound but AP-suspended while other players continue.

## Singleplayer run identity

Singleplayer uses the same opaque `RunId` rule even though its MegaCrit and AP
state are normally saved in one envelope. Starting New Run creates a new
in-memory `RunId`, but the previous checkpoint remains the last recoverable
checkpoint until the new run reaches a safe save boundary and replaces it. If
the process fails before that boundary, loading the old checkpoint resumes its
old `RunId`; its AP state never attaches to the unsaved new run. This remains
true even when both runs use the same STS seed in the same AP room.

Leaving multiplayer and selecting singleplayer starts a fresh `RunId` in the
same AP session. It never continues, forks, or converts the multiplayer
`RunState`.

## Consequences

- The host save stays small and contains only shared-run AP facts.
- AP DataStorage is not required for multiplayer recovery.
- A normal owner crash can preserve exact assignments through the local
  journal; catastrophic journal loss degrades to best-effort reconstruction.
- Unsent owner checks are not copied into a host-side outbox. If both the local
  record and any derivable run evidence are lost, that check is lost.
- A different machine is not promised to resume an AP-bound participant.
- The save model has an explicit stopping point instead of adding replicas for
  progressively less likely failures.

## Rejected alternatives

### Put every player's AP state in the host run save

This couples private AP recovery to host save ownership, expands the shared
schema, and still fails if the host save is lost.

### Mirror the owner journal into AP DataStorage

This creates two stores that need precedence and conflict rules. It also cannot
help while AP is unreachable. It may be added later as an explicitly
non-authoritative backup if cross-machine continuation becomes a requirement.

### Put pending owner checks in a host-side outbox

This lowers one failure probability but adds another owner-private persistence
surface to the host. The design instead accepts loss after simultaneous local
record loss and failed reconstruction.

### Convert or rehost the multiplayer run

Converting to singleplayer or transferring host authority requires player
removal, Net ID rebinding, shared RNG/map ownership, and save transfer. It is
outside the supported model.

## Validation required

- Crash before a host checkpoint: effect and ledger both roll back, then replay
  once.
- Crash after a host checkpoint: effect and ledger both restore, with no replay.
- Rejoin restores the exact frozen guest/AP mapping and rejects another slot.
- Missing owner journal salvages committed effects from the host ledger and new
  receipts from `AllReceivedItems` without claiming exact recovery.
- Starting a new singleplayer run uses a new `RunId`, preserves the previous
  checkpoint until the first safe replacement save, and never mixes their AP
  state.
- No credentials, raw AP history, pending-check outbox, or remote private
  journal appears in host run data.
