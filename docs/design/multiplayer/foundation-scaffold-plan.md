# Multiplayer connection and run-data foundation plan

- **Status:** Existing scaffold predates the accepted host-owned progress model;
  target integration incomplete
- **Depends on:** [Multiplayer synchronization RFC](multiplayer-sync-rfc.md)

This document turns the accepted participation, receipt relay, settings, and
host-checkpoint decisions into an implementation order. Existing source may
still expose the older undifferentiated Guest mode and applied-effect ledger;
that is scaffold state, not the target contract.

## Target settings and entry flow

The normal Archipelago settings menu owns two multiplayer settings. The names
below are conceptual until the implementation registers the final setting keys:

- `GuestRewardMode`: `VanillaGuest` or `APGuest`;
- `SharedSlotCheckScope`: `HostCharacterOnly` or `AllAPParticipants`.

These are ordinary editable client settings before launch. The resolved values
are contributed to the lobby and frozen into the host-owned run contract. A
settings change during an active run affects only a future run.

The main menu retains independent connection and play actions:

| Local AP state | AP Multiplayer result |
|---|---|
| Connected and prepared | Enter as Own AP Slot |
| Disconnected plus `VanillaGuest` | Enter as Vanilla Guest |
| Disconnected plus `APGuest` | Enter tentatively as AP Guest; launch requires the fixed host to have a prepared AP slot |

An AP Guest never connects to AP. It always follows the fixed STS host, never
another client's slot. An own-slot participant that later disconnects remains
bound to the same AP identity. No mode silently changes during a run.

## Canonical run-data shape

Register the RitsuLib run slots at mod initialization with stable keys:

| Slot | Owner and purpose | Target fields |
|---|---|---|
| `RunSavedData<ApRunSharedState>` / `ap_run` | Fixed host's canonical launch/run record | schema, opaque `RunId`, frozen host client/YAML gameplay settings, host effective Ascensions, frozen `SharedSlotCheckScope` |
| `PlayerRunSavedData<ApPlayerRunState>` / `ap_players` | Host-owned per-Net-ID participant and AP progress | participation mode, AP source, receipt-source readiness, `APProgressUnified` |

`PlayerRunSavedData` is per-player-shaped data in the host-carried snapshot. It
does not mean that each client writes a durable private save. Clients receive
and use an in-memory copy, while the fixed host owns the canonical disk save.

Conceptually:

```text
ApPlayerRunState
|- Participation: OwnApSlot | ApGuest | VanillaGuest
|- ApSource: room/team/slot or host-source reference
|- ReceiptSourceReady
`- Progress
   |- UsedItems
   |- GoldRedeemed
   |- reward attempt/bank counters
   |- stable card/relic/Ancient/potion assignments
   |- progressive starter and pending buff state
   `- pending location checks where required
```

Do not put AP credentials or `AllReceivedItems` in run data. Do not write
multiplayer progress or pending-check journals to client-local files or AP
DataStorage.

## Host receipt relay

The fixed host owns the only AP connection for the shared cooperative slot.
Build a revisioned, in-memory catalog from the host AP SDK's
`AllReceivedItems`:

- send a complete snapshot to AP Guests during lobby preparation and rejoin;
- broadcast small ordered deltas after the host receives new items;
- include only the item/index/source metadata needed by item processing and UI;
- never transmit credentials;
- never persist the full catalog in the host save; and
- reject AP Guest claims until both catalog revision and host progress are ready.

This uses RitsuLib Sidecar's existing typed host-to-peer and request/snapshot
transport. A guest AP socket and a second AP protocol implementation are both
unnecessary.

## Work order

### 1. Participation and settings foundation

- Replace the undifferentiated Guest concept with Own AP Slot, AP Guest, and
  Vanilla Guest.
- Register `GuestRewardMode` and `SharedSlotCheckScope` in the ordinary AP
  settings menu.
- Contribute the resolved mode during lobby staging and freeze it at launch.
- Reject an AP Guest when the fixed host has no prepared AP slot.
- Preserve exact-session reconnect guards for own-slot players.

### 2. Lobby launch contract

- Require one complete contribution per active Net ID.
- Own-slot readiness requires that player's direct AP slot data and initial
  receipt history.
- AP Guest readiness requires the host AP source, frozen host settings, and host
  receipt catalog.
- Vanilla Guest has no AP receipt-readiness gate.
- Freeze `RunId`, participant/AP-source mapping, host client/YAML gameplay
  settings, host effective Ascensions, and `SharedSlotCheckScope` in the
  committed run snapshot.
- Treat `SyncLobbyOnChange` as client-to-host staging only, not general mid-run
  transport.

### 3. Shared progress model

- Extract `APProgressUnified` from the shared semantic fields of
  singleplayer `SerializableAP`; keep the opaque singleplayer `SaveData` outside
  it.
- Put one `APProgressUnified` in every AP-participating player's host-owned
  `PlayerRunSavedData` entry.
- Keep per-player `UsedItems` sets. In shared-slot mode, the same received index
  may be consumed independently for several Net IDs.
- Key stable assignments by received index within the player's frozen AP
  source. The full protocol identity includes `RunId`, room/team/slot, index,
  and claiming Net ID.

### 4. Assignment and grant boundary

- Resolve or load a concrete assignment and send it to the host before exposing
  it as stable in UI.
- Make the assignment usable only after the host accepts it into the claimant's
  live progress.
- Apply the concrete effect through the appropriate native synchronizer or
  narrow RitsuLib action.
- After successful application, immediately update the claimant's consumed
  index or aggregate cursor in host memory.
- Publish the resulting in-memory progress view to the claimant.
- Do not wait for a floor save to block a same-floor duplicate.

### 5. Shared-slot checks

- In `HostCharacterOnly`, send only locations produced for the host character.
- In `AllAPParticipants`, have the host loop over the host and AP Guests using
  native committed character state, resolve their character-specific location
  IDs, deduplicate them, and submit them through the host AP connection.
- Do not add an AP Guest check-forwarding message.
- Keep own-slot clients sending checks through their own AP connections.
- Store outstanding run-scoped checks in host-owned progress when they must
  survive a floor checkpoint.

### 6. Save, load, and rejoin

- Update host progress immediately, but use the normal STS2 multiplayer floor
  checkpoints for durability.
- Restore native `RunState` and all per-player AP progress from the host
  checkpoint.
- Own-slot players then fetch current AP received/checked history. AP Guests
  receive a fresh host receipt snapshot. Vanilla Guests reconstruct no AP view.
- Keep callbacks and reward UI paused until restored host progress and the
  applicable receipt source are both ready.
- Replace client in-memory state from the host; never merge a local journal.
- Accept the last successful host checkpoint as truth. A pre-checkpoint effect,
  consumption, assignment, or check may roll back or be lost.
- Keep the host fixed. Do not add host migration or run conversion.

## Required proof before enabling save/rejoin

- Mixed Own AP Slot, AP Guest, and Vanilla Guest lobbies freeze the correct
  sources, and no AP Guest follows a non-host slot.
- AP Guests open no AP socket and receive full/delta host receipt catalogs.
- One host-slot receipt is independently claimable once per host/AP Guest.
- Both shared check scopes work for different and duplicate characters, with
  only the host writing shared-slot checks.
- A host AP disconnect leaves cached rewards claimable but stops new shared
  receipts/checks until the same slot reconnects.
- Stable assignments and consumption update immediately in host memory and
  restore after the next floor checkpoint.
- A host crash before that checkpoint restores the preceding checkpoint and
  makes rolled-back receipts claimable again.
- Own-slot and AP Guest clients can rejoin with empty local storage.
- No client-local or AP DataStorage multiplayer journal is created.

Until these two-client tests exist, the repository contains source scaffolding
and design intent rather than runtime proof of the corrected architecture.
