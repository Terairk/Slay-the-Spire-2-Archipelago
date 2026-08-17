# ADR 001: One AP slot per STS player

- **Status:** Proposed
- **Date:** 2026-08-17

## Context

Slay the Spire 2 multiplayer represents every STS player in every process, but
each process has one local player identity. The current mod also has one
process-global AP session and progress object.

Possible topologies include:

1. one AP slot per local STS player;
2. one host AP slot controlling every STS player; or
3. every peer connecting to the same AP slot.

## Decision

Support one AP slot per local STS player.

Each process owns:

- its local AP connection;
- its local AP slot and progress;
- its local STS player resolved through MegaCrit `LocalContext`; and
- owner-only AP external operations.

Every peer still reproduces the resulting STS game-state changes for all
players through MegaCrit synchronization.

## Rationale

- It matches the natural AP model of one game client per slot.
- It matches MegaCrit's explicit local-player identity.
- AP credentials and server state remain local.
- A player's AP disconnect does not transfer AP authority to the host.
- It avoids several peers writing the same AP data-storage keys.
- It allows different AP slots to receive different items without pretending
  that the external state is deterministic.

## Consequences

- All peers must receive compact derived payloads for AP effects that change
  replicated STS state.
- Per-slot gameplay settings that alter index-based native structures need a
  per-owner derived specification.
- Slot-data settings that alter shared map, room, encounter, or run generation
  need a pre-run compatibility profile and may be required to match.
- The host is authoritative for MegaCrit action ordering, not for another
  player's AP session.
- Multiplayer save/reconnect must restore the association between local Net ID
  and local AP state.
- The first release should require every peer to run a compatible mod protocol.

## Rejected alternatives

### Host-only AP authority

This simplifies AP connectivity but makes the host responsible for other
players' AP identities, persistence, and failure handling. It also changes the
expected AP experience for non-host players.

### Every peer connects to the same AP slot

This risks duplicate item application, duplicate AP operations, conflicting
data-storage writes, and unclear ownership of received rewards.

## Validation required

- Confirm `LocalContext` remains stable across host/client start, load, and
  reconnect on the supported public game build.
- Confirm the mod can reject incompatible peer protocol versions before AP
  models or messages are deserialized.
