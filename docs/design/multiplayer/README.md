# Multiplayer design documents

## Current participation decision

[ADR 005: Direct AP connections](adr/005-direct-ap-connections.md) replaces AP Guest
participation and its relay/check-routing design. Every AP participant connects
directly, optionally to the same slot as the host. Vanilla guests remain supported.
It includes the current behavior, code mapping, and required runtime test matrix.

The older documents below retain historical design details. ADR 005 takes precedence
where they describe guest receipt relays, shared check scopes, or save compatibility.

These documents define the proposed architecture for adding Slay the Spire 2
multiplayer support to the Archipelago client. They are design inputs, not a
claim that multiplayer currently works.

## Documents

- [Baby's first multiplayer steps](babies-first-steps.md) records the agreed
  gold-only contract, current source scaffold, and required two-client runtime
  matrix.
- [Multiplayer synchronization RFC](multiplayer-sync-rfc.md) describes the
  overall execution model, state ownership, synchronization boundaries, and
  unresolved questions.
- [Multiplayer developer-console requirements](multiplayer-dev-console.md)
  defines safe grant simulation and extensible state inspection, with
  implementation deferred until the multiplayer grant pipeline exists.
- [Implementation roadmap](implementation-roadmap.md) breaks the design into
  reviewable phases and supplies a two-client validation matrix.
- [Connection and run-data foundation plan](foundation-scaffold-plan.md) maps
  the agreed guest/login/save decisions to concrete UI, RitsuLib schema, and
  dependency-ordered implementation slices.
- [ADR 001: Give each STS player one AP reward source](adr/001-one-ap-slot-per-sts-player.md)
  defines Own AP Slot, AP Guest, and Vanilla Guest participation, including
  shared-slot receipt and check routing.
- [ADR 002: Separate AP source authority from replicated game effects](adr/002-ap-authority-and-game-effects.md)
  defines the main state-ownership boundary.
- [ADR 003: Use MegaCrit synchronizers before custom transport](adr/003-megacrit-synchronizers-first.md)
  describes when to use native synchronization and when an AP-specific message
  is justified.
- [ADR 004: Keep multiplayer AP progress in the host checkpoint](adr/004-player-owned-ap-persistence.md)
  defines host-owned per-player progress, floor-checkpoint recovery, stable
  assignments, and the fresh-singleplayer handoff.

## Status vocabulary

- **Draft**: incomplete and open for broad discussion.
- **Proposed**: concrete enough for review, but not yet accepted.
- **Accepted**: approved as the implementation direction.
- **Superseded**: replaced by a later decision.

The RFC is the system-level source of truth. ADRs record individual decisions
and their rationale. If implementation evidence changes a decision, update the
RFC and add or supersede an ADR rather than silently changing the architecture.

## Review workflow

1. Review the four ADRs first.
2. Resolve the open questions in the RFC.
3. Complete the Phase 0 two-client spike from the roadmap.
4. Amend the RFC with runtime findings.
5. Mark accepted ADRs before beginning broad feature conversion.

All runtime-sensitive statements must distinguish decompiled-source evidence
from two-client in-game evidence. The supported beta game build should be
rechecked before implementation because multiplayer internals are not a stable
mod API.
