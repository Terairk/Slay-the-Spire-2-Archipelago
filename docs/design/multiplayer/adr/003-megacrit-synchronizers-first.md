# ADR 003: Use MegaCrit synchronizers before custom transport

- **Status:** Proposed
- **Date:** 2026-08-20

## Context

MegaCrit already supplies different synchronization mechanisms for different
lifecycles. A single custom AP message bus for every effect would duplicate
ordering, reward completion, player choices, reconnect buffering, and standard
model serialization. Conversely, forcing every AP feature through
`RewardSynchronizer` is impossible because it supports only standard
out-of-combat reward payloads.

## Decision

Choose the narrowest existing MegaCrit synchronizer that matches the effect.
Create AP-specific transport only for information that no existing mechanism
can represent.

| Need | Preferred mechanism |
|---|---|
| Concrete out-of-combat card/relic/potion/gold result | `RewardSynchronizer` |
| Select or skip a reward | `RewardsSetSynchronizer` |
| Nested card/relic/target choice | `PlayerChoiceSynchronizer` |
| Rest-site choice | `RestSiteSynchronizer` with identical per-owner option list |
| Event choice | `EventSynchronizer` with identical per-owner option list |
| Combat mutation | Host-ordered `GameAction`/`ActionQueueSynchronizer` path |
| AP socket operation | Owning direct connection: fixed host for shared slot, own-slot process otherwise |
| Multiplayer AP progress update | Host-owned per-player run data plus in-memory claimant view |
| AP Guest receipt catalog | Minimal revisioned RitsuLib Sidecar snapshot/delta from host |
| AP-derived payload needed before native structure exists | Minimal AP-specific peer message |

`PlayerCmd`, `RelicCmd`, `PotionCmd`, and `PowerCmd` are mutation APIs, not
general broadcast APIs. They must be called from a synchronized context or
paired with the applicable synchronizer.

## RitsuLib custom rewards

Use `ModCustomReward` for custom reward registration, native presentation, and
save payloads when an AP feature genuinely behaves like a reward. Do not assume
that its JSON payload is automatically sent from an own-slot player or the
shared-slot host to STS peers.

Every peer must construct the same custom reward before MegaCrit broadcasts a
selected reward index.

For the custom action-queue path, use RitsuLib managed net actions rather than
an AP-owned message implementation. Register stable `sts2ap` action keys early,
carry only concrete resolved payloads, and retain MegaCrit host ordering and
replay behavior.

## When custom AP transport is justified

- Publishing an AP-derived reward spec before remote reward-set construction.
- Relaying the host slot's in-memory receipt snapshot/deltas to AP Guests.
- Returning a claimant request to the host for canonical progress validation
  and immediate consumption.
- Publishing a per-owner AP rest-site or event transformation.
- Carrying transaction identity for an unsupported grant kind.

Custom transport should carry primitive IDs and amounts, not Godot nodes,
mutable game models, AP sessions, or credentials.

## Consequences

- Singleplayer and multiplayer can share most feature code because MegaCrit
  supplies a singleplayer network service.
- The implementation should centralize transport decisions instead of
  scattering `if (multiplayer)` branches.
- Some features use two layers: AP transport publishes a spec, then a MegaCrit
  synchronizer owns the selection or action lifecycle.
- Unsupported combat effects require a deliberate action design rather than a
  `RewardSynchronizer` workaround.

## Validation required

- Verify each selected MegaCrit API is callable and stable on the supported
  beta game build.
- Verify custom models and payloads serialize between real peers.
- Verify sequence IDs remain aligned through nested reward and choice flows.
- Verify singleplayer continues through the same high-level dispatcher.
