# ADR 004: Keep AP persistence player-owned across run modes

- **Status:** Proposed
- **Date:** 2026-08-18

## Context

MegaCrit persists an active multiplayer run through the host's canonical run
save, while every process owns a separate AP connection and slot. Host ownership
of the STS save must not make the host responsible for another player's AP
receipt history, private reward accounting, or server acknowledgments.

A player may also leave a disposable multiplayer run and return to the existing
singleplayer AP flow. That transition must preserve the AP session and deferred
receipts without pretending the discarded multiplayer run is a valid solo save.

## Decision

Each process owns and durably restores its private AP overlay through its AP
server, slot-scoped DataStorage, or a local client save. Existing
`SerializableAP` fields may be reused or split into a dedicated overlay, but
that owner-private document is not the canonical STS run save and its ownership
is independent of the host's MegaCrit run save.

The gold spike uses a RitsuLib `SaveScope.Global` slot with cloud sync disabled.
Its logical path is
`<STS account save root>/mod_data/Archipelago/multiplayer_gold.json`; records
inside the document are further scoped by AP room seed, numeric AP team ID,
numeric AP slot ID, and STS run identity.

Private owner state includes, where applicable:

- deferred received items and pending owner-only work;
- resolved private reward assignments;
- discrete receipt consumption and AP acknowledgment state; and
- aggregate accounting such as the raw gold redemption cursor.

Actual decks, relics, potions, wallet gold, map state, combat state, and other
MegaCrit gameplay state remain in replicated `RunState`. Shared AP launch
contracts or AP-derived state that every peer must reproduce may use RitsuLib
run data, but credentials and raw AP history never do.

## Run-mode handoff

Leaving multiplayer and selecting singleplayer starts a fresh AP run in the same
AP session. The owner replays received history and deferred items through the
normal item processor according to the fresh run's reset rules.

The handoff does not:

- continue, fork, or convert the multiplayer `RunState`;
- copy the multiplayer deck, wallet, map, RNG, or remote players; or
- transfer AP authority to the former STS host.

## Consequences

- Host and client AP owners use the same private persistence contract.
- A host run-save failure does not erase another player's recoverable AP state.
- The owner-private overlay must be scoped by AP room/seed, team, slot, and any
  run identity needed to prevent state leaking between unrelated sessions.
- Shared STS effects still require native synchronization or an ordered RitsuLib
  managed action; private persistence is not a networking mechanism.
- Save/rejoin tests must restore both the host-provided `RunState` and each
  process's independently owned AP overlay.

## Rejected alternatives

### Put every player's AP state in the host run save

This couples private AP recovery to host save ownership and makes the host carry
state it does not interpret or own.

### Convert the multiplayer run into a singleplayer continuation

This would require a separate design for player removal, Net ID rebinding,
shared RNG/map ownership, and remote-player state. It is outside the intended
fresh-run handoff.

## Validation required

- Host and client owners restore their own AP overlays after process restart.
- Replayed AP receipt history does not duplicate consumed discrete rewards or an
  aggregate gold claim.
- Leaving multiplayer and starting a fresh singleplayer run processes deferred
  items once and never loads the discarded multiplayer `RunState`.
- No credentials or raw AP session objects appear in peer messages or the host's
  shared run data.
