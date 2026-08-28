# ADR 001: Give each STS player one AP reward source

- **Status:** Superseded by [ADR 005](005-direct-ap-connections.md)
- **Date:** 2026-08-20

## Context

Slay the Spire 2 multiplayer represents every STS player in every process, but
each process has one local player identity. The mod must support both of these
Archipelago play styles in the same architecture:

1. each STS player uses an independent AP slot; and
2. several STS players cooperatively use the STS host's one AP slot.

A player may also join without participating in AP rewards. Calling every such
player simply `Guest` loses the important distinction between ordinary vanilla
rewards and following the host's AP rewards.

## Decision

Freeze exactly one participation mode for each STS Net ID when the run launches:

- **Own AP Slot**: the process connects directly to its own AP slot, uses that
  slot's settings and receipts, and sends checks for that slot.
- **AP Guest**: the process has no AP socket. It follows the STS host's AP slot,
  settings, and receipts through host-relayed STS multiplayer messages. It
  claims each shared receipt independently, but never sends an AP check.
- **Vanilla Guest**: the process has no AP socket or AP progress. It receives
  ordinary Slay the Spire 2 rewards and sends no AP operations.

`Vanilla Guest` versus `AP Guest` is selected in the normal Archipelago client
settings menu. The selected value is a client setting before launch. Its
resolved participation mode is contributed to the lobby and frozen into the
host-owned launch contract; changing the setting later does not alter the
current run.

An AP Guest is valid only when the fixed STS host is connected to an AP slot and
has completed its initial slot-data and received-item preparation. An AP Guest
always follows the host slot, never another non-host player's slot. A lobby may
mix all three modes: for example, an AP-bound host, an AP Guest following the
host, an independently AP-bound player, and a Vanilla Guest.

The host's resolved client/YAML gameplay settings are stored in the lobby run
data and frozen at launch. They are then saved in the host-owned run snapshot,
so AP Guests use the same host settings after load or rejoin.

The host creates an in-memory, revisioned catalog from its AP SDK
`AllReceivedItems`. It sends AP Guests a full catalog snapshot at lobby
preparation or rejoin and small deltas for later receipts. The catalog contains
only the receipt metadata required for local item processing and UI; it contains
no AP credentials and is not persisted in the multiplayer save. A guest must
have both the current catalog and its host-authoritative progress before it may
expose a reward.

The launch contract freezes an opaque `RunId` and maps each STS Net ID to its
participation mode and AP source. An own-slot player stores its exact AP room
seed, numeric team ID, and numeric slot ID. An AP Guest stores a reference to
the frozen host AP source. A Vanilla Guest has no AP source. None of these modes
may change while that run exists.

Treat a player's STS Net ID as stable for the lifetime of that MegaCrit run,
including active-run disconnect and rejoin. `Player.NetId` is immutable;
`RunLobby` removes a disconnected player only from its connected-player list,
and the host accepts a rejoin only when the transport sender ID already matches
a player in the preserved `RunState`. Steam uses the same account Steam ID, and
the local ENet harness must be relaunched with the same `clientId`. Do not search
for or rebind a returning participant by AP slot identity.

Net ID is run-scoped rather than globally durable. Durable receipt reasoning
uses `RunId`, AP room/team/slot, received-item index, and the claiming player's
Net ID. The player component is necessary because one host-slot receipt may be
claimed once by every AP Guest and by the host.

## Shared-slot checks

The shared-slot check-scope setting is edited in the normal Archipelago settings
menu and frozen into the host-owned run contract at launch. This document uses
`SharedSlotCheckScope` as its conceptual name:

- `HostCharacterOnly`: only the host character's events produce checks for the
  shared AP slot.
- `AllAPParticipants`: the host loops over the host and every AP Guest and sends
  the applicable character-specific checks for their characters. AP Guests do
  not forward a custom check message; the host already has every player's
  character and observes the synchronized run event.

The host resolves the character-specific location IDs and deduplicates them
before submitting to AP. Different characters normally use different
character-offset location IDs. Two participants using the same character may
resolve to the same location, which is submitted only once. Independently
AP-bound players continue to send checks to their own AP slots.

## Disconnection

If the host loses its AP connection, ordinary STS multiplayer play continues.
The host and its AP Guests keep their cached, previously received rewards
claimable, but receive no new receipts and submit no new shared-slot checks until
the same host AP identity reconnects. An AP Guest never falls back to Vanilla
Guest. An independently AP-bound player is suspended only with respect to its
own slot and may reconnect that exact identity.

## Rationale

- One AP connection for a shared cooperative slot gives the host a single,
  unambiguous receipt order and check writer.
- AP Guests cannot observe a receipt before the host or continue receiving new
  receipts while the host is disconnected.
- AP credentials never need to be sent through STS multiplayer.
- Host-to-peer receipt snapshots and deltas fit the existing RitsuLib Sidecar
  request/snapshot transport.
- Independent AP slots remain independent and may coexist with shared-slot and
  vanilla participants.
- The model supports native cooperative AP without requiring every person to
  generate or impersonate a separate AP slot.

## Consequences

- The host validates every AP Guest claim against its own receipt catalog and
  that player's host-owned progress.
- Per-player settings and assignments remain scoped to the player's frozen AP
  source. Shared run-generation settings use the fixed host's resolved values.
- A host without an AP slot cannot launch with an AP Guest.
- Host migration and rebinding an AP Guest to another slot are unsupported.
- Every peer must run a compatible mod protocol because AP Guest receipt
  snapshots and concrete effects use mod networking.

## Rejected alternatives

### Every AP Guest connects directly to the host slot

Archipelago permits several clients on one slot, but this creates independent
callback and reconnect timing, exposes shared credentials, and still needs a
host high-watermark to prevent a guest acting on a receipt before the host knows
about it. Because guests never send checks and intentionally stop receiving new
items when the host disconnects, the extra AP sockets provide no authority.

### Host controls every AP slot

This would require the host to hold credentials and sessions for independently
AP-bound players. Own-slot players instead retain their direct AP connection;
only the host's cooperative slot is relayed.

### One undifferentiated Guest mode

This cannot express whether the player receives vanilla rewards or independently
claims the host slot's AP rewards.

## Validation required

- Launch mixed lobbies containing Own AP Slot, AP Guest, and Vanilla Guest
  participants in both host/client arrangements allowed by the decision.
- Confirm AP Guests open no AP socket, receive the host receipt snapshot and
  deltas, and cannot claim before the host catalog/progress gate is complete.
- Confirm every shared receipt is independently claimable once per host/AP Guest
  and never claimable by a Vanilla Guest.
- Exercise both `SharedSlotCheckScope` values with different and duplicate
  character selections and verify only the host submits deduplicated checks.
- Disconnect and reconnect the host AP socket; cached receipts remain claimable,
  no new shared receipts/checks flow while disconnected, and the same slot
  resumes afterward.
- Confirm `LocalContext` resolves the same run-scoped Net ID across start, load,
  and active-run rejoin.
