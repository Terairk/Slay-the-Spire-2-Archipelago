# ADR 002: Separate AP authority from replicated game effects

- **Status:** Proposed
- **Date:** 2026-08-17

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
Alice received item index 73         Alice gains 50 gold
Alice selected a private relic       Alice obtains Vajra
Alice bought an AP shop location     Alice loses 120 gold
Alice received a combat buff         Alice gains Strength 2
```

Owner-only operations include:

- sending AP checks;
- updating local checked/used state;
- AP data-storage writes;
- local AP notifications; and
- private candidate caching.

Replicated operations include every mutation that changes a player's or the
run's MegaCrit state.

## Transaction identity

Custom AP transports should carry an idempotency key derived from the AP
receipt and owner, conceptually:

```csharp
ApGrantId(OwnerNetId, Team, Slot, ReceivedItemIndex)
```

The final identity format remains open pending save/reconnect testing. Existing
MegaCrit `RewardSynchronizer` messages do not carry this field; owner-side AP
deduplication and MegaCrit's reliable message lifecycle may be sufficient for
those standard operations.

## Consequences

- Remote peers do not need another player's complete AP item history.
- Private choice candidates may remain private if their generation does not
  mutate replicated RNG, pools, or state.
- AP capability values need not be replicated when only their final concrete
  consequence matters.
- If a capability changes an index-based backend list, peers need the derived
  ordered-list specification even if they do not receive the raw capability.
- AP persistence and MegaCrit run persistence remain separate but coordinated.

## Failure rule

An AP server failure must not cause peers to apply different MegaCrit effects.
The implementation must define the commit point for each operation without
introducing speculative rollback of authoritative AP items.

## Validation required

- Duplicate receipt callbacks apply each grant once.
- Disconnect/rejoin does not reapply a completed grant.
- Owner-only AP operations are absent from remote logs.
- Both peers finish with identical relevant `RunState` fields.
