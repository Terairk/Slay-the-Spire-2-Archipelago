# Baby's first multiplayer steps

- **Status:** Source scaffold implemented; two-client runtime validation not run
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
lower. The milestone intentionally does not hard-code 25 gold.

## Agreed first-spike contract

- A visible RitsuLib setting, `Enable Experimental Multiplayer`, defaults off.
- With the setting off, the mod continues hiding the Multiplayer button.
- With it on, Multiplayer opens the existing AP login directly. A successful
  login continues immediately into MegaCrit's normal Host/Join submenu.
- Every local process connects to its own AP slot in the same AP room/seed.
- MegaCrit owns the lobby, player list, ascension selection, character
  selection, `RunState`, network transport, and synchronization.
- Each process may select only characters unlocked by its own AP slot. Existing
  live unlock updates remain active in the lobby.
- The supported AP capabilities are hard-coded in an enum/profile. Initially
  they are character unlocks, the local Press Start check, and gold rewards.
- Only the exact local character's Press Start check is sent. Other location
  producers are disabled.
- Unsupported received items are retained by received-item index, shown as
  disabled in the AP reward menu, and included in its pending badge count.
- Switching back to the singleplayer flow in the same AP session replays those
  deferred items through the existing item processor.
- A gold reward can be claimed only outside combat and while all MegaCrit peers
  are connected. The row remains visible with an explanation while blocked.
- Disconnecting from the AP server does not invalidate already received items.
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

## Source scaffold

### Entry flow and feature profile

- `Models/MultiplayerFeature.cs` names each capability independently.
- `Utils/MultiplayerSupport.cs` owns the initial allowlist, selected play
  destination, deferred unsupported items, run-local safety state, and peer
  disconnect handling.
- `ModSettingsRegistration.cs` exposes the opt-in warning and setting.
- `Patches_MainMenuBehavior.cs` opens AP login before the native Host/Join flow
  and hides Load/Abandon for this disposable-run mode.

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

The reward UI recalculates the offer at click time, invokes the command, and
only then consumes the AP gold source. If local application fails, the offer
remains unconsumed. If synchronization unexpectedly fails after local
application, the offer is consumed once and all later claims fail closed; a
retry would duplicate Alice's already-applied gold.

### Gold-only safety gates

The initial profile bypasses AP combat reward replacement, card/relic/potion
claims, floorsanity, shops, rest sites, Ancients, progressive starters,
ascension effects, universal combat buffs, Death Link, victory checks, and
save/rejoin behavior. These features remain unchanged in singleplayer.

This is scaffolding, not a claim that every future AP feature is multiplayer
safe. Each enum entry must receive its own ownership and synchronization review
before moving into the allowlist.

## Two-client runtime matrix

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
11. Repeat with Bob as the recipient.
12. Repeat an equivalent claim in singleplayer as a regression test.

Additional safety cases:

- During combat, verify the gold row is visible but disabled, then becomes
  claimable after combat ends.
- Disconnect AP after receiving gold and verify the banked reward remains
  claimable.
- Disconnect a MegaCrit peer and verify all later AP claims stay disabled even
  if that peer reconnects.
- Deliver an unsupported item and verify it is disabled, counted, and neither
  mutates state nor consumes RNG/pools.
- Back out to singleplayer in the same AP session and verify deferred items are
  processed once.

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
- saves, continue, rejoin, late join, or rollback
- mixed mod/RitsuLib versions or players without the mod

## What this proves

This milestone tests one architectural rule:

> The local AP client decides and consumes an AP-owned reward. MegaCrit's
> existing multiplayer infrastructure replicates the resulting standard game
> mutation.

Runtime success in both host/client directions is required before enabling the
next capability. Decompiled-source and static-diff evidence alone do not prove
the multiplayer behavior works.
