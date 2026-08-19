# Multiplayer connection and run-data foundation plan

- **Status:** Scaffold implemented; runtime integration incomplete
- **Depends on:** [Multiplayer synchronization RFC](multiplayer-sync-rfc.md)

This document turns the guest, identity, save, and ledger decisions into an
implementation order. It is intentionally narrower than feature conversion:
the first goal is to make entry state and persistence ownership explicit so
later reward work has one place to attach.

## Target menu and connection flow

The main menu owns three independent actions:

| Action | Disconnected | Connected |
| --- | --- | --- |
| Connect to Archipelago | Opens the connection overlay and returns to the main menu on success or close | Disabled; the status line identifies the active server and slot name |
| AP Singleplayer | Disabled with a defensive connect-first message | Opens the existing AP character-select flow |
| AP Multiplayer | Opens MegaCrit Host/Join as a guest | Opens MegaCrit Host/Join as an AP-bound player after slot history is prepared |

Opening the connection overlay no longer implies a pending game destination.
The normal successful-login callback hides the overlay and stays on the main
menu. The command-line two-process harness is the only intentional automatic
continuation.

Entering AP Multiplayer captures a tentative local participation kind:

- connected and prepared: `Archipelago`;
- disconnected: `Guest`; or
- command-line AP harness: explicitly `Archipelago` before login.

An AP-bound lobby participant that later disconnects remains AP-bound and is
blocked until the same room/team/slot reconnects. It must never silently turn
into a guest.

## Canonical run-data shape

RitsuLib slots are registered at mod initialization with stable keys:

| Slot | Owner and purpose | Initial fields |
| --- | --- | --- |
| `RunSavedData<ApRunSharedState>` / `ap_run` | Host's canonical shared run record | schema, opaque `RunId`, host effective Ascensions, applied shared-effect IDs |
| `PlayerRunSavedData<ApPlayerRunState>` / `ap_players` | Canonical per-Net-ID participant mapping inside the same host snapshot | guest/AP kind, AP room seed/team/slot, AP-history readiness |

`PlayerRunSavedData` is not an owner-private save. It is the per-player-shaped
part of the host's canonical run record and is therefore appropriate for the
frozen participant mapping. Private assignments, pending checks, and prepared
or submitted transactions remain in the AP owner's durable local journal.

The host creates the `RunId` while staging a fresh lobby. The committed run
snapshot is the boundary that makes the participant mapping durable; there are
no separate saved freeze or validation flags. Readiness is derived from the
live lobby contributions before launch. Singleplayer
receives a new `RunId` when RitsuLib prepares a fresh run that has no staged ID.
Loading an existing snapshot restores its existing ID.

Host identity is also derived rather than saved. On a host,
`INetGameService.NetId` is the host ID; on a client,
`NetClientGameService.HostNetId` identifies that same peer. Host migration is
unsupported, so storing another host-ID field would only duplicate native
network state and create a value that could disagree.

The shared ledger exposes storage helpers now, but no reward may call them
directly from an owner-local callback. A later slice must place primary effect
application and ledger insertion inside the same host-ordered operation.

## Work order

### 1. Entry and identity foundation

- Keep connection selection independent from AP Singleplayer/AP Multiplayer.
- Show persistent connection state on the main menu.
- Represent disconnected multiplayer entry as `Guest`.
- Give guests all character choices, no AP readiness gate, no AP initialization,
  and an empty AP reward menu.
- Retain the existing exact-session reconnect guard for AP-bound participants.

This repository contains the source scaffold for these items. It still needs a
main-menu/controller test and a two-process guest/AP lobby test.

### 2. Complete lobby launch contract

- If the host is AP-bound, derive `HostEffectiveAscensions` from the host AP
  character configuration plus already received Ascension Downs.
- If the host is a guest, copy the host's complete manual Ascension selection.
- On the host, require one contribution for every active Net ID and ensure every
  AP identity and AP-history marker is complete. Derive this result at the
  launch boundary; do not persist a validation flag.
- Apply the host Ascension set to the actual launch state. After import, every
  client validates the same `RunId`, participant map, and set. Each process
  derives the host Net ID from its live MegaCrit network service.
- Treat lobby `SyncLobbyOnChange` as client-to-host staging only. Do not assume it
  is a general host-to-client or mid-run broadcast.

`ApHistoryComplete` reaches the host through that staging path. Each process
writes its local `PlayerRunSavedData` entry; RitsuLib attaches a client's entry
to MegaCrit's `LobbyPlayerChangedCharacterMessage` and flushes it again with
`LobbyPlayerSetReadyMessage`. The host merges it under the sender's Net ID
before handling Ready. The host's own entry is merged locally.

The host Ready button is disabled until every active player has a complete
record. If a record becomes incomplete after the host readies, the host is
automatically unreadied. A final host prefix on MegaCrit's all-ready launch
method recomputes the same condition to close the race where a client is the
last player to ready. No saved validation flag is involved.

### 3. Move existing grants onto the saved identity

- Expand discrete effect identity from `(slot, received index)` to `(RunId,
  room/team/slot, received index)`.
- Scope aggregate-gold cursors and all stable card/relic/potion/Ancient
  assignments by the same run and AP owner identity.
- Replace the current process-global `multiplayer_grants`/`multiplayer_gold`
  assumptions with an atomic owner-local journal keyed by that identity.
- Persist consumed received-item indices (the multiplayer equivalent of
  singleplayer `UsedItems`) and pending/submitted/confirmed grant state in that
  same journal. Reconstructing `AllReceivedItems` alone must not make consumed
  rewards claimable again during an exact rejoin.
- Keep exact reconstructed card and Linked Ancient selections owner-local; only
  their replicated committed effect IDs belong in the host ledger.

### 4. Host-ordered effect plus ledger commit

- Give each replicated AP effect one ordered execution path.
- Inside that operation, reject a ledger duplicate, apply the concrete primary
  effect, and insert its ID into `ApRunSharedState.AppliedEffectIds`.
- Use native MegaCrit synchronizers where they already define the operation;
  use a RitsuLib managed action where custom host ordering is required.
- Do not treat the `RunSavedData` setter as network transport. Every live peer
  must execute the ordered mutation so their in-memory run documents agree.
- Reconcile owner journal states against the restored host ledger after load or
  rejoin.

### 5. Save, load, and rejoin

- Enable MegaCrit multiplayer load/continue only after the host can
  restore the canonical snapshot and all peers receive the RitsuLib run data.
- Admit only the committed guest identities and exact AP room/team/slot identities.
- Reprocess AP `AllReceivedItems` only for AP-bound owners. Restore the local
  journal first, subtract consumed receipt indices, add new receipts, restore
  stable assignments and grant states, and reconcile shared commits against the
  host ledger before publishing rejoin readiness. Accept the documented lossy
  salvage behavior if the journal is missing.
- Keep the host fixed. Do not add host migration, AP DataStorage
  mirroring, or host-side pending-check outboxes.
- Save at normal safe MegaCrit checkpoints, with optional extra safe saves on
  orderly quit, disconnect, or desynchronization.

## Required proof before enabling save/rejoin

- Connected login returns to the main menu and both AP play buttons behave as
  specified.
- A disconnected guest and an AP-bound peer can launch in either host
  assignment without AP state leaking to the guest.
- On the host, `ap state lobby` lists one contribution per active Net ID and
  shows `apHistoryComplete=yes` for every AP participant. Guest contributions
  have no AP-history blocker.
- Host and client derive the same host Net ID from native networking and import
  the same nonempty `RunId`, participant map, and Ascension set.
- After launch, `ap state run` reports the committed mapping and
  `ap state ledger` reports the shared applied-effect IDs on both peers.
- An effect applied before a saved checkpoint restores with its ledger ID and
  is not replayed; an effect after the checkpoint rolls back with its ID and is
  replayable.
- A same-identity rejoin succeeds and a wrong slot, guest/AP substitution, or
  attempted host migration is rejected.

Until those two-client tests exist, this work is source scaffolding rather than
evidence that multiplayer saves or reconciliation work at runtime.
