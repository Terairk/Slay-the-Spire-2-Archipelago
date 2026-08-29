# AP rewards during map travel

## Evidence and scope

RitsuLib checksum 496 in the 2026-08-28 report differed by the remote player's
Petrified Toad. That player opened AP rewards after voting and claimed the relic
after move action 532 started. The host generated the rest-site exit checksum at
17:37:49.668 and applied the relic at 17:37:50.294, 626 ms later. The client applied
the relic before its checkpoint. Compare action/set IDs across peers; their wall
clocks differ. The empty-chest completion exception is a separate issue.

The agreed fix prevents new AP claims during actual travel. It does not restrict
map votes, wait for peer acknowledgments, change receipt consumption, or replay
grants. Claims sent immediately before travel can still arrive late at another
peer; this is not a general synchronization barrier.

## Native beta control flow

Static inspection of the installed beta 0.111.0 assembly established:

- `MoveToMapCoordAction.ExecuteAction` awaits `NMapScreen.TravelToMapCoord` in the
  normal game. Travel sets `IsTraveling`, plays animations, and enters the room.
- `RunManager.ExitCurrentRoom` calls `RewardsSetSynchronizer.BeforeLeavingRoom`,
  which skips local sets and signals `RewardsSkippedDuringRoomExit`.
- `NRewardsScreen.BeforeRoomExit` handles that signal. This occurs after travel
  begins; `NRewardButton.GetReward` itself has no travel guard. The map normally
  hides overlays. These observations do not prove a vanilla multiplayer repro.
- `NCardRewardSelectionScreen._ExitTree` faults an unresolved choice with
  `TaskCanceledException`. Removing that picker is not a normal Skip.
- Selecting the existing `Skip` alternative resolves `OptionSelected`; native
  `CardReward.OnSelect` sends `SyncLocalChoice` and returns false. It owns both
  choice synchronization and picker removal.

## AP implementation

- `Patches_APRewardTravel.CloseAtTravelStart` brackets the native map-travel task.
  It calls `ArchipelagoRewardUI.BeginTravel` before animations, and clears the
  guard in `finally` when travel ends. Only real multiplayer runs enable it.
- `ArchipelagoRewardUI` gates menu opening and AP reward-button selection. A
  local `ApLifecycleVersion` token rejects deferred/awaiting openings from before travel, even
  if preparation resumes after entry into the next room. Cleanup invalidates
  tokens; old completions cannot clear a later transition's guard.
- Ordinary AP browsing (including unselected linked relic choices) closes using
  the existing native skip path. A completed set is not skipped again.
- `BetaMainCompatibility.TrySkipCardRewardSelection` matches the exact picker
  owned by an AP card reward and invokes its existing Skip handler. It checks the
  actual alternative ID and non-consuming result by name; it does not assume a
  button index or choose another alternative. Already-resolved choices and
  choices without Skip are not force-canceled. Unrelated/nested reward sets are
  left to native cleanup rather than skipping the wrong stack entry.

The guard is transient UI state, not saved or replicated AP progress. Item IDs,
assignments, used-item boundaries, APWorld data, and version files are untouched.

## Validation

The local `StS2AP.AdmissionTests` harness includes six travel-guard cases: idle
availability, animation before loading, stale preparation, loading, cleanup,
and stale transition completion. This harness is intentionally locally excluded
from Git. These checks simulate guard lifetimes; they do not exercise Godot,
Harmony, native Skip callbacks, or network replication.

Compilation uses beta assemblies on `multiplayer-main-checks`. In-game validation
is still required with two beta 0.111.0 peers and matching RitsuLib variants.

| Scenario | Expected result |
| --- | --- |
| Vote, then open AP while waiting for the other player | Menu opens normally; no vote cancellation required. |
| Other player triggers travel with AP browsing open | AP closes at travel start; clicks during animation do not send an AP reward selection. |
| Travel with an AP card picker open | Existing Skip is selected; no card or AP receipt is consumed; reopening offers the same assignment. |
| Travel with linked Ancient relic choices unselected | Menu closes; no relic is selected or receipt consumed. |
| Travel during menu-preparation approval | Old opening never appears in the next room; a fresh opening works. |
| Claim immediately before travel, with latency and reversed host/client roles | Check for checksum agreement. A late-message mismatch here is outside this guard's guarantee. |
| Picker with no Skip, or selection already accepted | No invented cancellation or rollback; inspect native cleanup and effects. |
| Quit/continue and start a new run | No stale travel lock or reopening from the old run. |

Expected AP logs:

```text
Closing AP rewards at map travel start.
Skipped AP card selection through native Skip for player <netId>.
```

For a canceled card picker, also check the native `Sending player choice id ...`
and matching remote choice completion. There should be no cancellation exception
from deleting the picker, duplicate reward-set skip, or unexpected AP consumption.
