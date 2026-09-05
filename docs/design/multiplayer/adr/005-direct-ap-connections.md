# ADR 005: Use direct AP connections for every AP participant

- **Status:** Accepted
- **Date:** 2026-08-28
- **Supersedes:** ADR 001 and the guest receipt/check routing portions of the RFC,
  ADRs 002–004, foundation plan, roadmap, and developer-console plan.

## Participation and ownership

There are two participation types: `OwnApSlot` and `VanillaGuest`. OwnApSlot means
the player's process has its own AP connection, not that the slot is exclusive.
Multiple players may enter the same server and slot name, including the STS host's
slot. Independent slots and vanilla guests can coexist in the same lobby. The STS
host still needs a prepared AP connection before hosting.

Every AP participant receives settings and item history directly from AP and sends
their own character's checks. The AP Guest mode, receipt catalog relay, guest
reward setting, and shared-slot check-scope setting are removed. Credentials are
entered locally and are not relayed through the STS session. Participation remains
fixed during a run: loss of an AP connection does not turn an AP player into a
vanilla guest.

The STS host remains authoritative for native action ordering and canonical saves.
Per-player progress snapshots/deltas, mirrored reward actions, stable assignments,
relic receipt reservations, and gold cursors remain. Players sharing one slot keep
independent reward consumption under their STS Net IDs. The full AP item history
is reconstructed from each player's SDK session, not stored in the run save.

Progressive Starters and Ancient receipt progress use each player's own record.
Shared Ascensions remain controlled by the fixed STS host; receiving the same
Ascension Down on another connection does not author a second shared action.

If the host loses its AP socket while the STS session remains alive, other direct
connections can continue receiving AP items and submitting checks. Existing
cached rewards remain available under the existing reward rules.

## Check purchases

The normal local stale-purchase check remains. There is no new reservation across
connections sharing a slot. Two concurrent purchases can spend both players' gold
while AP records the location only once. This is an accepted tradeoff. The host
does not expand a purchase to other players' character-specific checks.

## DeathLink

Each AP callback is relayed to the STS host for its local player only. The host
admits one ordered damage action at a time, targeting exactly that player. It
does not expand a host callback to other participants sharing the slot.

The inbound request preserves the AP timestamp. Deduplication uses STS recipient
Net ID, AP source, and timestamp, in addition to transport event IDs. Thus duplicate
SDK callbacks cannot damage one player twice, but every eligible player can receive
their own copy. Different timestamps within one second are not coalesced by the mod.

The sending process remembers its outgoing source/timestamp pairs for the run,
using the SDK's wire timestamp conversion, to suppress delayed self-echoes even
after another send or an AP reconnect. This local filter does not suppress receipt
by another player. Incoming damage retains the existing per-player active-damage
guard and six-second lethal fallback. Death prevention clears the fallback;
nonlethal incoming damage does not silence later legitimate deaths.

Only the STS host authorizes outgoing DeathLinks. A non-host player sends an
authorized event through their own AP socket. No global source-name filter or
shared-slot cooldown is introduced. The AP SDK's own last-send filter is unchanged.

## Compatibility and implementation

Existing multiplayer saves are unsupported; no migration is provided. The run-data
schema is 8 and DeathLink message/action schemas and keys use v3. All peers need
the same client build. These are internal protocol changes, not a project release
version change. Singleplayer persistence and the Python APWorld are unchanged.

Key implementation points:

- `MultiplayerSupport.BeginMultiplayerEntry`: connected means OwnApSlot; otherwise VanillaGuest.
- `ApPlayerContextResolver` / `MultiplayerLocationChecks`: resolve each player's own state and writer.
- `ApRunData.IsReceiptUsed`: consumption remains per Net ID and received index.
- `DeathLinkMultiplayer`: callback, host admission, one-player action, outgoing authorization.
- `DeathLinkEventLedger`: event deduplication, local self-echo memory, lethal-damage guard.

## Validation

Static evidence: the installed AP SDK 6.7.0 DeathLink implementation and both the
preserved public 0.107.1 and installed beta 0.111.0 `CreatureCmd.SetCurrentHp` /
`Creature.InvokeDiedEvent` paths were inspected. The game death-prevention boundary
and host action admission are retained. This is not in-game confirmation.

The locally excluded `client/StS2AP.AdmissionTests` harness exercises the production event ledger, including duplicate
delivery per recipient, subsecond events, delayed self-echoes, independent recipients,
lethal echo prevention, death prevention, and new-run reset. Existing relic tests
exercise independent consumption of the same receipt index for different players.

Local results: 50/50 harness tests passed; the public-reference compile-only client
build passed with 48 warnings (including the expected Godot generator warning for
missing `GodotProjectDir`, plus nullable/unused-code warnings). No full packaging or
game deployment was run. Python APWorld code and tests are outside this change.

### Required beta runtime checks — NOT RUN

Use beta 0.111.0 with matching RitsuLib variants and this client build on every peer.
Start a new campaign. The local launcher now accepts:

```powershell
.\scripts\test_multiplayer_local.ps1 -HostSlot Alice -ClientSlot Alice
```

| Scenario | Expected result |
| --- | --- |
| Same-slot host/client plus vanilla guest; repeat with different slots | Lobby launches; only AP participants get AP rewards and submit their own checks. |
| Shared receipt, claim on both players, save/continue | Each player claims once; exact assignments and consumption survive the checkpoint. |
| External DeathLink with two same-slot players | One HP change per eligible player, identical on replicas; no host target expansion. |
| Duplicate callback; two distinct events within one second | Duplicate is ignored per recipient; distinct events remain distinct. |
| Local player dies, same-slot peer receives lethal DeathLink | One authorized outgoing event for the original death; caused death is suppressed, with no bounce loop. |
| Death prevention, then a legitimate later death | Prevented death sends nothing; later genuine death sends normally. |
| AP reconnect while another player stays connected | New receipts continue on the connected peer; no stale callback from the replaced session. |
| Concurrent purchase of the same location | AP checks once; both players may spend gold, as accepted. |
| Starter/Ancient receipt and host Ascension Down | Per-player effects remain isolated; shared Ascension changes once. |

Useful logs: `Host queued incoming DeathLink`, `Host admitted incoming DeathLink`,
`Applying host-ordered DeathLink ... to ...`, `Ignored duplicate DeathLink for AP owner`,
`Ignored own DeathLink echo`, and `Suppressing outgoing DeathLink for player`.
Compare event and player IDs across logs, not wall clocks.
