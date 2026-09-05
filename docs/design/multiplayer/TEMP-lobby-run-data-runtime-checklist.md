# Temporary multiplayer lobby and run-data runtime checklist

- **Purpose:** Manual verification of the existing pre-correction connection
  menu, generic-Guest/AP lobby records, host identity, history Ready gate,
  launch guard, and committed run-data scaffold.
- **Status:** Temporary working document. Delete or fold into the permanent runtime
  matrix after the checks have been executed.
- **Build under test:** Record the mod commit/SHA and RitsuLib version below.

This checklist describes the currently implemented scaffold, not the accepted
target design. Every unqualified `Guest` below means the legacy generic guest
and should be treated as a Vanilla Guest for interpretation. It does not prove
AP Guest receipt relay, host-owned `APProgressUnified`, shared receipt fan-out,
`SharedSlotCheckScope`, removal of local journals, or floor-checkpoint recovery.
Add those cases before replacing this temporary checklist with the permanent
runtime matrix.

## Test record

```text
Date:
Tester:
Mod commit/SHA:
Game version:
RitsuLib version (expected 0.5.14):
AP server/room:
Host STS client ID:
Client STS client ID:
Host Own AP Slot or legacy/Vanilla Guest:
Client Own AP Slot or legacy/Vanilla Guest:
Host log:
Client log:
```

Do not put AP passwords in this file or the logs.

## Commands used by this checklist

Run commands in the normal STS2 developer console on the process named in each
step.

```text
ap state summary
ap state lobby
ap state multiplayer
ap state run
ap state ledger
ap state grants
ap state assignments
```

Command scope and expected limitations:

| Command | When to run | What confirms success |
|---|---|---|
| `ap state summary` | Lobby and active run | Correct AP connection, slot, initial history state, and reward counts |
| `ap state lobby` | Character-select/start lobby only | Host identity, staged player records, history flags, blockers, and host validation |
| `ap state multiplayer` | Active run | Correct local role, frozen player list, currently connected Net IDs, participant connection status, permanent-invalidation flag, bounded Sidecar queue/drop counters, and full receipt-snapshot payload size |
| `ap state run` | Active run only | Same committed `RunId`, host Net ID, and participant mapping on both processes |
| `ap state ledger` | Active run only | Legacy scaffold diagnostic only; not target persistence evidence |
| `ap state grants` | Active run | Current local supported AP receipts and their claim state |
| `ap state assignments` | Active run | Stable prepared card/relic/potion/Ancient assignments when any exist |

`ap state lobby` is intentionally asymmetric:

- Host output is authoritative and must contain every active player's record.
- Client output may contain only its own AP record. Other players showing
  `contribution=missing` on the client is not a failure.
- The client's lobby `runId` may be `missing`; the host sends the committed
  `RunId` in the launch snapshot.

## Environment setup

1. Build/install the current mod and RitsuLib 0.5.14 in both game processes.
2. Use two distinct STS client/account IDs.
3. Enable **Experimental Multiplayer** for both IDs.
4. For AP/AP cases, create two distinct slots in the same AP room, for example
   Alice and Bob.
5. Keep the host and client logs separate.

For the repository's one-machine AP/AP beta harness:

```powershell
.\scripts\test_multiplayer_local.ps1 -SettingsOnly
```

Enable Experimental Multiplayer in both windows, close them, then run:

```powershell
.\scripts\test_multiplayer_local.ps1 `
    -ApServer localhost:38281 `
    -HostSlot Alice `
    -ClientSlot Bob
```

Expected logs:

```text
logs\multiplayer\host_standard-1.log
logs\multiplayer\join-1000.log
```

The harness is AP/AP only. Use the ordinary UI on two processes or machines for
legacy guest cases. These exercise only Vanilla Guest-like behavior; AP Guest
requires the future host receipt relay and settings selection.

## Case 1: Disconnected and connected main-menu behavior

Start one clean process without connecting to AP.

1. Confirm the main menu displays `Archipelago: Not connected`.
2. Confirm **Connect to Archipelago** is enabled.
3. Confirm **AP Singleplayer** is disabled.
4. Confirm **AP Multiplayer** is available and opens the native Host/Join flow
   as a guest.
5. Return to the main menu without starting a run.
6. Press **Connect to Archipelago**, connect to a valid slot, and wait for the
   connection UI to close.
7. Confirm the UI returns to the main menu rather than automatically starting a
   run.
8. Confirm the status names the connected server and slot.
9. Confirm **AP Singleplayer** and **AP Multiplayer** are enabled.

Result:

```text
PASS / FAIL:
Evidence/notes:
```

## Case 2: AP host and AP client lobby

Use the harness with Alice as host and Bob as client.

1. Connect Alice first and wait for the native host lobby.
2. Connect Bob and wait for Bob to join Alice's lobby.
3. Before either player presses Ready, run on Alice:

   ```text
   ap state summary
   ap state lobby
   ```

4. In Alice's `ap state lobby` output, verify:

   ```text
   role=Host
   localNetId=<Alice STS Net ID>
   hostNetId=<same Alice STS Net ID>
   visibility=authoritative host staging; merged peer contributions
   contributionValidation=ready
   runId=<nonempty UUID>
   Alice: participation=Archipelago, complete room/team/slot, apHistoryComplete=yes
   Bob: participation=Archipelago, complete room/team/slot, apHistoryComplete=yes
   both players: readyBlocker=none
   ```

5. Run on Bob:

   ```text
   ap state summary
   ap state lobby
   ```

6. Verify Bob reports `role=Client`, a different `localNetId`, and Alice's host
   Net ID. `contributionValidation=not-authoritative-on-client` is expected.
7. Ready Bob, then Ready Alice. Confirm the run starts.

Result:

```text
PASS / FAIL:
Alice lobby output:
Bob lobby output:
Evidence/notes:
```

## Case 3: Committed run snapshot after AP/AP launch

Continue immediately from Case 2.

1. Run on Alice:

   ```text
   ap state multiplayer
   ap state run
   ap state ledger
   ```

2. Run the same three commands on Bob.
3. Verify:

   - Alice reports `role=Host`; Bob reports `role=Client`.
   - Both report the same `hostNetId`.
   - Both report the same nonempty `RunId`.
   - Both contain Alice and Bob with their correct AP room/team/slot mappings.
   - `appliedEffectCount` matches on both.
   - `ap state ledger` is empty before any ledger-integrated AP effect.
   - `participantConnection=complete` and `claimsInvalidated=no` before any failure.

Do not fail this case merely because an existing reward feature changes the
game while the ledger remains empty. The scaffold exposes the ledger, but each
reward still needs to be connected to `RecordAppliedEffectFromOrderedAction`
before its effect ID is expected here.

Result:

```text
PASS / FAIL:
Alice RunId:
Bob RunId:
Alice hostNetId:
Bob hostNetId:
Evidence/notes:
```

## Case 4: AP host and guest client

Use two ordinary UI processes.

1. Connect Alice to AP.
2. Leave Bob disconnected from AP.
3. Alice selects **AP Multiplayer** and hosts.
4. Bob selects **AP Multiplayer** and joins as a guest.
5. On Alice run:

   ```text
   ap state lobby
   ```

6. Verify Alice's authoritative output contains:

   ```text
   Alice: participation=Archipelago, apHistoryComplete=yes, readyBlocker=none
   Bob: participation=Guest, identity=guest, readyBlocker=none
   contributionValidation=ready
   ```

7. Confirm Bob can choose any locally available/unlocked-by-patch character.
8. Confirm Bob's AP reward menu is empty.
9. Ready both players and launch.
10. On both processes run `ap state run` and verify Bob is committed as Guest
    while Alice retains her AP identity.

Result:

```text
PASS / FAIL:
Host lobby output:
Evidence/notes:
```

## Case 5: Guest host waits for AP client

Use two ordinary UI processes.

1. Leave Alice disconnected from AP and host through **AP Multiplayer** as a
   guest.
2. Connect Bob to AP, select **AP Multiplayer**, and join Alice.
3. On Alice run `ap state lobby`.
4. Verify:

   ```text
   Alice: participation=Guest, identity=guest, readyBlocker=none
   Bob: participation=Archipelago, apHistoryComplete=yes, readyBlocker=none
   contributionValidation=ready
   hostNetId=<Alice localNetId>
   ```

5. Ready and launch both players.
6. On both processes run `ap state run`; verify the same `RunId`, Alice as
   Guest, Bob with his AP identity, and Alice as the derived host.

Result:

```text
PASS / FAIL:
Host lobby output:
Alice run output:
Bob run output:
Evidence/notes:
```

## Case 6: AP client becomes incomplete while guest host is Ready

This is the clearest remote-history test because stopping Bob's AP connection
does not also invalidate Alice: Alice is a guest and has no AP socket.

1. Recreate Case 5 and stop in the lobby with both records complete.
2. Press Ready on guest host Alice.
3. Break only Bob's AP connection while leaving the STS multiplayer connection
   alive. For a local AP server, stop or disconnect Bob's AP session; for a
   remote setup, interrupt only Bob's AP network path.
4. Wait for Bob's AP client to notice the disconnect.
5. Verify Bob automatically becomes unready and Bob's Ready button is disabled.
6. Verify Alice automatically becomes unready and Alice's Ready button is
   disabled.
7. On Alice run:

   ```text
   ap state lobby
   ```

8. Verify Bob's row contains:

   ```text
   participation=Archipelago
   apHistoryComplete=no
   readyBlocker=ap-history-incomplete
   ```

9. Verify the host output contains:

   ```text
   contributionValidation=blocked (Player <Bob Net ID>: ap-history-incomplete.)
   ```

10. Attempt to press Ready as Alice and confirm it remains disabled. If the UI
    permits an activation attempt through keyboard/controller focus, confirm it
    is refused with the same reason.
11. Restore Bob's AP connection and wait for automatic reconnect/history
    preparation.
12. Run `ap state lobby` on Alice again and verify Bob returns to
    `apHistoryComplete=yes`, `readyBlocker=none`, and
    `contributionValidation=ready`.
13. Confirm Alice's and Bob's Ready buttons become available again.
14. Ready both players and confirm the run starts.

Result:

```text
PASS / FAIL:
Blocked host output:
Recovered host output:
Observed notification:
Evidence/notes:
```

## Case 7: AP host loses AP while a guest client remains

1. Recreate Case 4 and stop in the lobby.
2. Ready Alice, the AP-bound host.
3. Break Alice's AP connection while leaving STS multiplayer alive.
4. Verify Alice automatically unreaddies and Ready becomes disabled.
5. On Alice run `ap state lobby` and verify Alice has
   `apHistoryComplete=no`, `readyBlocker=ap-history-incomplete`, and blocked
   contribution validation.
6. Verify Bob remains a Guest and does not acquire or initialize AP state.
7. Restore Alice's AP connection and wait for history preparation.
8. Verify Alice returns to `apHistoryComplete=yes`, host validation becomes
   ready, and the Ready button becomes available.

Result:

```text
PASS / FAIL:
Blocked host output:
Recovered host output:
Evidence/notes:
```

## Case 8: Final client-last Ready race guard

The ordinary UI intentionally prevents an AP client with incomplete history
from pressing Ready, so this defensive host branch may be difficult to hit
manually. Do not claim it passed unless the actual launch-block log is observed.

Stress attempt:

1. Use the guest-host/AP-client lobby from Case 6.
2. Ready Alice.
3. With Bob still unready, break Bob's AP connection and immediately attempt to
   Ready Bob before the local UI processes the disconnect.
4. Repeat if necessary with artificial AP latency while keeping STS networking
   healthy.
5. If Bob's invalid Ready reaches Alice, verify the run does not start, Alice is
   automatically unreadied, and Alice logs/displays:

   ```text
   AP multiplayer launch blocked: Player <Bob Net ID>: ap-history-incomplete.
   ```

6. Run `ap state lobby` on Alice and verify the blocked Bob record.

If the local Ready guard wins every attempt, record this case as **NOT
EXERCISED**, not PASS. A future narrowly scoped fault-injection command can make
this branch deterministic if repeated runtime testing warrants it.

Result:

```text
PASS / FAIL / NOT EXERCISED:
Number of attempts:
Observed launch-block message:
Host lobby output:
Evidence/notes:
```

## Case 9: Process restart/reset regression

1. Exit both game processes completely.
2. Relaunch a fresh AP/AP case with the same STS client IDs and AP slots.
3. Verify no old lobby `RunId`, player record, or Ready state leaks into the new
   lobby.
4. On the host run `ap state lobby` and verify a new nonempty `RunId` and the
   current two player records.
5. Launch and verify `ap state run` matches that new lobby `RunId` on both
   processes.

Result:

```text
PASS / FAIL:
Previous RunId:
New RunId:
Evidence/notes:
```

## Corrected architecture cases not covered by this scaffold

Do not mark the accepted multiplayer design runtime-verified until a later
checklist covers at least:

1. settings-selected AP Guest versus Vanilla Guest launch behavior;
2. rejection of AP Guest when the fixed host has no prepared AP slot;
3. full and incremental host receipt relay, including rejoin and revision gates;
4. independent per-player claims and stable assignments for one shared receipt;
5. `HostCharacterOnly` and `AllAPParticipants` check submission, including
   duplicate-character deduplication and no guest forwarding message;
6. cached reward behavior while the host AP socket is disconnected;
7. immediate live host consumption before a floor save;
8. floor-checkpoint save/load and pre-checkpoint host-crash rollback; and
9. own-slot and AP Guest rejoin with empty local storage and no AP DataStorage
   journal.

## Minimum evidence bundle

For each failed case, retain:

1. Host and client `ap state lobby` output immediately before the failure.
2. Host and client `ap state run` output if a run launched.
3. `ap state summary` and `ap state multiplayer` from the affected process.
4. Both process logs, with the approximate failure time.
5. Which player was host, guest/AP participation, AP slot names, and the two STS
   Net IDs.
6. A screenshot or short description of each Ready button's enabled/disabled
   and ready/unready state.

## Final result summary

| Case | Result | Notes/evidence |
|---|---|---|
| 1. Main-menu behavior | | |
| 2. AP/AP lobby | | |
| 3. Committed AP/AP run data | | |
| 4. AP host/guest client | | |
| 5. Guest host/AP client | | |
| 6. Remote AP history becomes incomplete | | |
| 7. Host AP history becomes incomplete | | |
| 8. Final launch race guard | | |
| 9. Fresh-process reset | | |
