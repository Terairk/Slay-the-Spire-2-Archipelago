# ADR 003: Use MegaCrit synchronizers before custom transport

- **Status:** Proposed
- **Date:** 2026-08-17

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
| Private AP socket/check/progress operation | Local AP code only |
| AP-derived payload needed before native structure exists | Minimal AP-specific peer message |

`PlayerCmd`, `RelicCmd`, `PotionCmd`, and `PowerCmd` are mutation APIs, not
general broadcast APIs. They must be called from a synchronized context or
paired with the applicable synchronizer.

## RitsuLib custom rewards

Use `ModCustomReward` for custom reward registration, native presentation, and
save payloads when an AP feature genuinely behaves like a reward. Do not assume
that its JSON payload is automatically sent from the AP owner to STS peers.

Every peer must construct the same custom reward before MegaCrit broadcasts a
selected reward index.

## When custom AP transport is justified

- Publishing an AP-derived reward spec before remote reward-set construction.
- Publishing a per-owner AP rest-site or event transformation.
- Carrying transaction identity for an unsupported grant kind.
- Negotiating the AP multiplayer protocol version.

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
  public game build.
- Verify custom models and payloads serialize between real peers.
- Verify sequence IDs remain aligned through nested reward and choice flows.
- Verify singleplayer continues through the same high-level dispatcher.
