# Baby's first multiplayer steps

- **Status:** Source implementation updated; two-client runtime validation not run
- **Branch:** `multiplayer-gold-spike`
- **Scope:** Fresh disposable multiplayer runs and synchronized AP gold
- **Depends on:** [Multiplayer synchronization RFC](multiplayer-sync-rfc.md)

This is the smallest useful multiplayer milestone for the Archipelago client:

> Alice and Bob enter a normal Slay the Spire 2 multiplayer run. Alice claims
> an `EliteGold` reward from her private AP reward menu. Both game processes
> update their copy of Alice's gold by the calculated amount, while Bob's gold
> is unchanged.

`EliteGold` currently contributes 40 source gold. The existing Poverty
calculation remains authoritative, so the amount displayed and granted may be
lower. The milestone intentionally does not hard-code 25 gold. Removing Poverty
does not refund previously withheld multiplayer gold in this first implementation.

## Relationship to the synchronization RFC

This document describes the implemented gold transport spike and its aggregate
dispatcher/cursor update. Discrete `ApGrantId`, assignment, managed-action,
lobby-staging, and acknowledgment requirements remain later work. Its successful
parts are:

- gold is a standard STS result and should continue through
  `RewardSynchronizer`, not a RitsuLib managed action;
- AP ownership remains local to the receiving process; and
- every peer must mutate its replica of the same owning player.

The original gold proof requires both test processes to bind AP slots. The
source now also scaffolds ADR 001's guest entry and run-data shapes, but that
does not constitute a two-client guest or save/rejoin proof.

The gold path now sits behind the common AP grant dispatcher:

```text
AP gold receipts update the owner's raw bank
  -> button click materializes one aggregate offer
  -> owner redemption-cursor check and concrete amount resolution
  -> native gold route
  -> synchronized local execution
  -> owner raw redemption-cursor persistence
```

This milestone does not exercise RitsuLib managed actions, `StartRunLobby`
run-data staging, the host Ascension contract, or reconstruction of the
host-persisted applied-effect ledger. Those remain separate runtime proofs.

## Agreed first-spike contract

- A visible RitsuLib setting, `Enable Experimental Multiplayer`, defaults off.
- With the setting off, the mod continues hiding the Multiplayer button.
- With it on, `AP Multiplayer` opens MegaCrit's Host/Join submenu. A disconnected
  process enters as a guest; a connected process enters as its prepared AP slot.
- `Connect to Archipelago` opens login independently. Successful login returns
  to the main menu, whose status line shows the active server and slot name.
- `AP Singleplayer` requires a prepared AP connection.
- The original gold test uses one AP slot per process in the same AP room/seed.
- The prepared owner identity uses the AP server's numeric team and slot IDs;
  the player name remains only the human-readable AP slot login name.
- In this narrow spike, MegaCrit owns the lobby, player list, ascension
  selection, character selection, `RunState`, network transport, and
  synchronization. The target architecture adds the RFC's AP `StartRunLobby`
  staging and host-forced effective Ascension set before launch.
- Each process may select only characters unlocked by its own AP slot. Existing
  live unlock updates remain active in the lobby.
- The supported AP capabilities are hard-coded in an enum/profile. Initially
  they are character unlocks, the local Press Start check, and gold rewards.
- Only the exact local character's Press Start check is sent. Other location
  producers are disabled.
- Unsupported received items are retained by received-item index, shown as
  disabled in the AP reward menu, and included in its pending badge count.
- Leaving the disposable multiplayer run and starting a fresh singleplayer run
  in the same AP session replays those deferred items through the existing item
  processor. The multiplayer `RunState` is discarded, not converted or resumed.
- A gold reward can be claimed only outside combat and while all MegaCrit peers
  are connected. The row remains visible with an explanation while blocked.
- Disconnecting from the AP server does not invalidate already received items.
- An AP disconnect in the lobby automatically unreadies the local player and
  disables Embark. The client retries after 5, 10, 20, and 30 seconds, then
  every 30 seconds; five minutes raises a warning but does not stop STS play.
- The host cannot Ready until every lobby player has supplied a complete guest
  or AP record. A later incomplete record automatically unreaddies the host,
  and the same condition is checked again immediately before native launch.
- Reconnect accepts only the same AP room/seed, AP team ID, and AP slot ID.
- Any MegaCrit peer disconnect permanently disables further AP claims for that
  run. The underlying run is not forcibly terminated.
- Runs are fresh and disposable. AP multiplayer saves, load, continue, and
  rejoin are not supported in this spike.
- The implementation accepts MegaCrit's supported player counts, although the
  first runtime matrix uses two players.

## Ownership and synchronization

The AP item and menu remain private to the receiving process. Only the standard
game mutation is replicated:

```text
Alice's AP slot receives EliteGold
             |
             v
Alice's private AP reward menu
             |
             v
PlayerCmd.GainGold(amount, Alice) on Alice's process
             |
             v
RewardSynchronizer.SyncLocalObtainedGold(amount)
             |
             v
PlayerCmd.GainGold(amount, Alice) on every remote process
```

MegaCrit associates the reward message's sender ID with Alice's serialized STS
player. Bob therefore updates his copy of Alice, not his own local player. The
mod does not synchronize Alice's AP item, AP menu state, or resulting total.

## Source implementation

### Entry flow and feature profile

- `Models/MultiplayerFeature.cs` names each capability independently.
- `Utils/MultiplayerSupport.cs` owns the initial allowlist, selected play
  destination, deferred unsupported items, run-local safety state, and peer
  disconnect handling.
- `ModSettingsRegistration.cs` exposes the opt-in warning and setting.
- `Patches_MainMenuBehavior.cs` owns the independent connection button, AP
  Singleplayer/AP Multiplayer entry, guest selection, connection status, and
  disposable-run Load/Abandon hiding.
- `ApRunData.cs` registers the shared and per-player canonical run-data shapes;
  their full validation and ledger integration remain later slices.

### Local-player binding

`Player.CreateForNewRun` remains the singleplayer setup boundary. Multiplayer
instead binds once after `RunManager.Launch`, when `LocalContext.GetMe` can
identify the local player among all players in the shared `RunState`.

| Process | AP session owns | `GameUtility.CurrentPlayer` |
|---|---|---|
| Alice's process | Alice's AP slot | Alice |
| Bob's process | Bob's AP slot | Bob |

Each process initializes only its local AP trackers and sends only its local
Press Start check.

### Gold grant boundary

`GameUtility.GrantGold` now:

1. verifies the active player is local;
2. rejects claims during combat or after a MegaCrit disconnect;
3. applies `PlayerCmd.GainGold` locally;
4. calls `RewardSynchronizer.SyncLocalObtainedGold` only in real multiplayer;
5. logs the local Net ID, amount, and before/after totals.

The reward UI materializes one immutable aggregate offer while it is open. The
dispatcher validates the expected raw cursor, invokes the command, advances the
cursor, and persists it in RitsuLib's local-only `multiplayer_gold.json`. If
local application fails, the offer remains unconsumed. If synchronization or
cursor persistence unexpectedly fails after local application, later claims
fail closed; retrying would duplicate Alice's already-applied gold. Crash
recovery across that mutation/persistence window remains explicitly unresolved.

### Gold-only safety gates

The initial profile bypasses AP combat reward replacement, card/relic/potion
claims, floorsanity, shops, rest sites, Ancients, progressive starters,
ascension effects, universal combat buffs, Death Link, victory checks, and
save/rejoin behavior. These features remain unchanged in singleplayer.

This is scaffolding, not a claim that every future AP feature is multiplayer
safe. Each enum entry must receive its own ownership and synchronization review
before moving into the allowlist.

### Requirements before expanding the allowlist

Before another received-item category builds on this spike:

1. Extend the common AP grant dispatcher with `RunId`-scoped `ApGrantId`
   handling and a host-owned applied-effect ledger for discrete receipt-backed
   grants.
2. Keep aggregate gold on `RewardSynchronizer.SyncLocalObtainedGold`; one button
   click synchronizes one wallet grant, not one action per gold receipt.
3. Add owner-local prepared assignments where discrete grants require them.
   Gold's cumulative raw cursor is already local and owner-owned.
4. Stage the `RunId`, guest/AP identity mapping, AP-owner history readiness,
   and host effective Ascension set through RitsuLib `StartRunLobby` run data.
   Derive the host Net ID from MegaCrit networking rather than saving it again.
5. Prove duplicate callback and acknowledgment boundaries in both
   host-recipient and client-recipient directions.

Save/rejoin durability remains a later roadmap phase and is not a prerequisite
for the next disposable-run capability. Until that phase succeeds, the spike's
fresh-run restriction remains in force.

RitsuLib managed actions should be introduced in the same early architecture
work, but the first use should be an AP effect without a suitable native
synchronizer rather than gold.

## Two-client runtime matrix

### One-machine beta harness

The beta game's `fastmp` transport can run independent host and client game
processes on one Windows machine. The AP client adds a one-shot `-apFastmp`
dispatcher so native host/join automation waits until that process has connected
to and prepared its own AP slot.

Build the mod first. Its build target creates `steam_appid.txt` containing
`2868840` in the configured StS2 directory. Enable Experimental Multiplayer once
for each `clientId` used by the harness; each ID has separate account-scoped
RitsuLib settings and multiplayer-gold persistence.

The repository includes a launcher that applies the Steam/account isolation,
native fastmp transport, AP launch roles, distinct client IDs, and AP login
prefill. The first time each client ID is used, launch its settings windows:

```powershell
.\scripts\test_multiplayer_local.ps1 -SettingsOnly
```

Enable Experimental Multiplayer in both windows, close them, and then start the
two-process test:

```powershell
.\scripts\test_multiplayer_local.ps1
```

The defaults use `localhost:38281`, Alice, Bob, client ID `1`, and client ID
`1000`. Override them when the test room uses different values:

```powershell
.\scripts\test_multiplayer_local.ps1 `
    -ApServer localhost:38281 `
    -HostSlot MyHostSlot `
    -ClientSlot MyClientSlot
```

The launcher discovers `SlayTheSpire2.exe` from `client/StS2AP/local.props` or
the standard Steam installation. Use `-ExePath` for any other installation.

Each process opens the existing AP login with its command-line server and slot
prefilled. Passwords deliberately remain interactive rather than appearing in
the process list. Connect Alice first; after AP preparation, that process calls
the beta game's native `FastHost(GameMode.Standard)`. Then connect Bob; after
its independent preparation, that process opens the native fastmp join screen,
which joins `127.0.0.1:33771`.

`-apFastmp` accepts only `host_standard` and `join`, requires
`-force-steam off` plus bare `-fastmp`, and consumes the action once. Without
the Steam override, `fastmp` would isolate network IDs but both processes could
still resolve RitsuLib data through the same Steam account root. Invalid
arguments, a disabled experimental setting, or failed AP preparation do not
fall through to native host/join automation.
Without `-apFastmp`, StS2 retains its normal `-fastmp host_standard` and
`-fastmp join` behavior.

Run once with the host receiving gold and once with the client receiving gold:

1. Enable Experimental Multiplayer on both game processes.
2. Connect Alice and Bob to distinct slots in the same AP room.
3. Host/join through MegaCrit and start a fresh run.
4. Verify each log binds the correct local Net ID, character, and AP slot.
5. Confirm each process sent only its own Press Start check.
6. Deliver `EliteGold` to Alice's AP slot.
7. Record Alice's and Bob's gold on both processes.
8. Open Alice's AP reward menu outside combat and claim the offer.
9. Before another combat, verify both processes show the same increase for
   Alice and no change for Bob.
10. Reopen Alice's reward menu and verify the offer cannot be claimed twice.
11. Replay the same gold receipt history and verify the aggregate redemption
    cursor prevents an already claimed button from being offered again.
12. Repeat with Bob as the recipient.
13. Repeat an equivalent claim in singleplayer as a regression test.

Additional safety cases:

- During combat, verify the gold row is visible but disabled, then becomes
  claimable after combat ends.
- Disconnect AP after receiving gold and verify the banked reward remains
  claimable.
- Disconnect a MegaCrit peer and verify all later AP claims stay disabled even
  if that peer reconnects.
- Deliver an unsupported item and verify it is disabled, counted, and neither
  mutates state nor consumes RNG/pools.
- Leave the multiplayer run, start a fresh singleplayer run in the same AP
  session, and verify deferred items are processed once without restoring the
  discarded multiplayer `RunState`.

## Expected diagnostic evidence

Useful non-secret log fields are:

```text
Experimental AP multiplayer launched:
enabled=true
netType=Host|Client
localNetId=<id>
players=[<ids>]

Bound local AP multiplayer player:
netId=<id>
character=<official character name>
slot=<AP slot name>

Prepared AP multiplayer session:
<room seed>/ap-team-<numeric AP team ID>/ap-slot-<numeric AP slot ID>

AP gold claim applied:
localNetId=<id>
amount=<calculated amount>
goldBefore=<value>
goldAfter=<value>
syncSent=true
```

Do not log AP passwords or credentials.

## Explicit non-goals

- AP reward-set or private-choice synchronization
- card, potion, relic, or linked-relic claims
- shops, rest sites, Ancient events, or combat-reward locations
- combat-affecting AP items or Death Link
- AP-controlled multiplayer ascension
- MegaCrit saves, continue, run rejoin, late join, or rollback
- mixed mod/RitsuLib versions or players without the mod

## What this proves

This milestone tests one architectural rule:

> The local AP client decides and consumes an AP-owned reward. MegaCrit's
> existing multiplayer infrastructure replicates the resulting standard game
> mutation.

Runtime success in both host/client directions is required before enabling the
next capability. Decompiled-source and static-diff evidence alone do not prove
the multiplayer behavior works.

It proves only the source-level native gold route and owner cursor design. It
does not by itself prove the discrete idempotent executor, RitsuLib managed
actions, `StartRunLobby` staging, combat buffs, Ascension transitions, or any
two-client/reconnect behavior described by the RFC.
