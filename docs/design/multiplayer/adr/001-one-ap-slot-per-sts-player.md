# ADR 001: At most one AP slot per STS player

- **Status:** Accepted
- **Date:** 2026-08-19

## Context

Slay the Spire 2 multiplayer represents every STS player in every process, but
each process has one local player identity. The current mod also has one
process-global AP session and progress object.

Possible topologies include:

1. one AP slot per local STS player;
2. a modded STS player participating as a guest without an AP identity;
3. one host AP slot controlling every STS player; or
4. every peer connecting to the same AP slot.

## Decision

Support zero or one AP slot per local STS player.

An AP-bound process owns:

- its local AP connection;
- its local AP slot and progress;
- its local STS player resolved through MegaCrit `LocalContext`; and
- owner-only AP external operations.

Every peer still reproduces the resulting STS game-state changes for all
players through MegaCrit synchronization.

A guest has the mod and participates fully in replicated STS gameplay, but has
no AP identity. A guest receives no AP items, sends no AP checks or
acknowledgments, and naturally has an empty AP reward menu. Existing unlock
patches make every character available to a guest. Cooperative assistance from
guests is accepted; any future difficulty compensation is a separate design.

The launch contract freezes a mapping from STS Net ID to either `Guest` or the
exact AP room seed, numeric team ID, and numeric slot ID. It also assigns an
opaque `RunId`. An AP-bound player cannot become a guest, change AP identity,
or bind a different slot while that run exists.

## Rationale

- It matches the natural AP model of one game client per slot.
- It matches MegaCrit's explicit local-player identity.
- AP credentials and server state remain local.
- A player's AP disconnect does not transfer AP authority to the host.
- It avoids several peers writing the same AP data-storage keys.
- It allows different AP slots to receive different items without pretending
  that the external state is deterministic.
- It permits a modded friend to join without reserving or impersonating an AP
  slot.

## Consequences

- All peers must receive compact derived payloads for AP effects that change
  replicated STS state.
- Per-slot gameplay settings that alter index-based native structures need a
  per-owner derived specification.
- Slot-data settings that alter shared map, room, encounter, or run generation
  need a pre-run compatibility profile and may be required to match.
- The host is authoritative for MegaCrit action ordering, not for another
  player's AP session.
- Multiplayer save/reconnect must restore the frozen guest/AP association for
  the same `RunId`.
- Private AP persistence remains owned by each process rather than becoming
  host-owned merely because the MegaCrit run save is host-owned.
- The first release should require every peer to run a compatible mod protocol.
- The original STS host remains the host for the lifetime of the run. Rehosting
  or transferring a saved run to another player is unsupported.

See ADR 004 for persistence across multiplayer and fresh singleplayer runs.

## Rejected alternatives

### Host-only AP authority

This simplifies AP connectivity but makes the host responsible for other
players' AP identities, persistence, and failure handling. It also changes the
expected AP experience for non-host players.

### Every peer connects to the same AP slot

This risks duplicate item application, duplicate AP operations, conflicting
data-storage writes, and unclear ownership of received rewards.

### Require every STS player to bind an AP slot

This needlessly prevents modded guests from joining. A guest contributes only
ordinary replicated STS gameplay and never becomes an AP authority.

## Validation required

- Confirm `LocalContext` remains stable across host/client start, load, and
  reconnect on the supported beta game build.
- Confirm the mod can reject incompatible peer protocol versions before AP
  models or messages are deserialized.
- Confirm the existing unlock patches let guests select every character.
