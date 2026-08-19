# ADR 002: Separate AP authority from replicated game effects

- **Status:** Accepted
- **Date:** 2026-08-19

## Context

An AP item is initially known only to the receiving AP process. A MegaCrit run
is replicated: every peer contains every player's deck, relics, potions, gold,
combat state, and shared run data.

Synchronizing the complete AP progress object would expose unnecessary state
and couple the game protocol to AP implementation details. Keeping every AP
effect local would cause MegaCrit state divergence.

## Decision

Keep AP authority local and synchronize the smallest concrete consequence that
changes MegaCrit state.

Examples:

```text
Private AP cause                     Replicated STS consequence
----------------                     --------------------------
Alice clicks her aggregate gold row  Alice gains the concrete wallet amount
Alice selected a private relic       Alice obtains Vajra
Alice bought an AP shop location     Alice loses 120 gold
Alice received a combat buff         Alice gains Strength 2
```

Owner-only operations include:

- sending AP checks;
- updating local checked/used state;
- AP data-storage writes;
- maintaining aggregate source accounting such as the raw gold redemption
  cursor and the Poverty calculation applied when a claim is materialized;
- local AP notifications; and
- private candidate caching.

Replicated operations include every mutation that changes a player's or the
run's MegaCrit state.

## Transaction identity

Custom AP transports should carry an idempotency key derived from the AP
receipt and owner, conceptually:

```csharp
ApGrantId(RunId, Team, Slot, ReceivedItemIndex)
```

Aggregate claims and other effects that are not one-to-one with a received item
use an equivalent stable effect ID derived from the `RunId`, AP owner, effect
kind, and owner-private sequence or cursor.

The host-owned run state contains the applied-effect ledger for every
replicated AP effect. Applying the concrete effect and adding its effect ID must
be one host-ordered operation and must reach the same MegaCrit checkpoint.
Owner-side persistence is useful for preparing exact payloads and AP
acknowledgment, but it is not the canonical answer to whether an effect belongs
to the restored shared run.

Not every native reward maps one-to-one to an AP receipt. Gold receipts form one
owner-private raw bank and the UI materializes all currently unredeemed gold as
one aggregate claim. Only the resulting wallet amount is replicated. The owner
persists a cumulative raw redemption cursor so receipt-history replay cannot
offer the same aggregate twice. The first implementation does not refund gold
when Poverty is later removed; that correction is deliberately unsupported
rather than inferred from remote-private accounting.

## Consequences

- Remote peers do not need another player's complete AP item history.
- Remote peers do not need another player's raw gold consumption calculation;
  they reproduce only the concrete wallet mutation.
- Private choice candidates may remain private if their generation does not
  mutate replicated RNG, pools, or state.
- AP capability values need not be replicated when only their final concrete
  consequence matters.
- If a capability changes an index-based backend list, peers need the derived
  ordered-list specification even if they do not receive the raw capability.
- AP persistence and MegaCrit run persistence remain separate but coordinated.
- On reconnect, the AP owner compares AP received history and its local journal
  with the host ledger. A host-ledger hit is committed; an absent ID is prepared
  or replayed against the restored checkpoint.
- Leaving multiplayer preserves the owner's AP session and deferred receipts but
  starts a fresh singleplayer run; it does not convert the multiplayer save.

## Failure rule

An AP server failure must not cause peers to apply different MegaCrit effects.
The commit point is the host-ordered application of the concrete effect plus its
shared ledger ID. A submitted owner transaction whose outcome is not yet known
waits for reconciliation; after a restored checkpoint, ledger presence means
committed and absence means the exact prepared payload may be retried.

The host persists at normal safe MegaCrit checkpoints rather than forcing a
full disk save after every AP effect. A crash before the next checkpoint rolls
back both effect and ledger, allowing replay. Orderly quit, disconnect, or
desynchronization may request an extra safe save on a best-effort basis.

## Validation required

- Duplicate receipt callbacks apply each grant once.
- Disconnect/rejoin does not reapply a completed grant.
- A host crash before and after a checkpoint produces respectively one replay
  or no replay.
- Owner-only AP operations are absent from remote logs.
- Both peers finish with identical relevant `RunState` fields.
