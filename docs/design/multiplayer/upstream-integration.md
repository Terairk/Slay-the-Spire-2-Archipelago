# Integrating singleplayer upstream changes

`upstream/main` supports singleplayer. Multiplayer is maintained separately and
its synchronization, ownership, save, and authoritative-item contracts take
priority when upstream changes overlap them. A clean Git merge or successful
compile does not establish that an upstream feature is safe in multiplayer.

## Branch baseline

`multiplayer-squashed` starts at upstream commit
`d3c4d4d` and consolidates the reviewed multiplayer implementation from
`multiplayer-main-checks` at `a2eb9b9`, including the experimental compatibility
build work. The original branches preserve the development history.

Continue new multiplayer development from `multiplayer-squashed`. Treat the old
multiplayer and experimental branches as historical references; new work based
on them should be reviewed and transferred deliberately to the new baseline.

## Review every upstream update

For each incoming feature or fix, choose and record one outcome:

- Adopt it when it is presentation-only or already obeys multiplayer contracts.
- Adapt it to player ownership, replicated construction, synchronized actions,
  stable received-item assignments, and save/load behavior as applicable.
- Keep its singleplayer behavior behind a mode guard and leave the feature
  disabled in multiplayer until multiplayer support is explicitly approved.
- Retain the existing multiplayer implementation where an upstream backport is
  an older or singleplayer-specific implementation of the same behavior.

The planned upstream bonus-items change is an explicit example: its new behavior
must remain disabled in multiplayer until supported deliberately. Disabling a
feature must not silently discard authoritative AP receipts or change item IDs;
review the APWorld/slot-data and client contract together when that change arrives.

Inspect automatically merged files as well as conflict hunks. Do not resolve all
conflicts by taking one side wholesale, or assume that similar commit titles
identify duplicate implementations. Backports may combine, split, or alter the
original multiplayer changes.

## Future updates

Fetch and merge upstream regularly after reviewing the incoming changes:

```powershell
git fetch upstream main
git switch multiplayer-squashed
git merge --no-commit --no-ff upstream/main
```

Resolve conflicts and review automatic changes using the policy above, validate,
and create a normal merge commit. Preserve upstream ancestry in these updates;
the initial squash is a baseline reset, not the routine upstream update method.
Even if an upstream behavior stays disabled in multiplayer, a reviewed merge
records that its upstream commit has been considered.

Features intended for upstream should use focused branches based on current
`upstream/main`. After acceptance, integrate the upstream result and adapt it to
multiplayer rather than repeatedly carrying independent copies of a backport.

## Validation

Compile against beta `0.111.0` and its matching RitsuLib variant, run the relevant
existing regressions, and check the actual diff. Compilation and source tracing
are static evidence. In-game checks must cover affected AP participants, vanilla
guests, and new-run/continue-run behavior before declaring runtime support.

For this baseline reconciliation, check character-selection ascension counts
after character switches, local-player-only shop hints and page navigation,
synchronized reward claims, and save/rejoin behavior. The older singleplayer
reward-menu implementation is superseded by the multiplayer dispatcher, and
Lasting Candy remains disabled as on the original multiplayer branch.

## Reconciliation through upstream `7a1535c` (2026-09-06)

Reviewed the five incoming commits after `d3c4d4d`. The checkout already contained
staged adaptations for session identity, collected campfires, and Anytime Neow.
These edits were preserved during the merge; no additional runtime changes were
needed after comparing the upstream behavior with those adaptations.

| Upstream change | Resolution |
| --- | --- |
| `1963c26`: separate APWorld compatibility from release versions | Retain the existing `CompatFlag` validation, embedded manifests, and compatibility loader/build setup. Preserve the checkout's existing client `2.1.0` and APWorld `1.1.0` values; this merge does not perform a release or change versions. |
| `78c0b59`: pending checks and connection handling | Retain `SessionCallbacks`, main-thread/session guards, asynchronous login, reconnect handling, character validation, and identity-bound outboxes. Preserve the staged use of the common `ApSessionIdentity` in `MultiplayerSupport`, including server authority in reconnect identity. |
| `6e41713`: option descriptions and Anytime Neow | Preserve the staged APWorld descriptions and client adaptation. `Patches_ItemProcessor` and `MultiplayerSupport.PrepareApSession` retain all Anytime Ancient receipts; `ArchipelagoProgress.GetOrAssignAncientRelicChoices` maps them to Neow/Act 2/Act 3. `AncientRelicPool` keeps Neow out of shared Ancient pools, including True Chaos. `ApMirroredRewardDispatcher` retains owner-authored assignments and synchronized claims. Keep descriptions consistent with this client's rejection of missing character mods. |
| `5af215a`: `!collect` support | Retain the existing `SessionCallbacks.LocationsUpdated` subscription and disposal. Preserve the staged publication through `MultiplayerLocationChecks.PublishEffectiveCheckProgress` so newly collected campfires reach replicas through `ApRunData.PublishLocalProgress`. SDK notifications do not acknowledge durable outbox entries. |
| `7a1535c`: assorted fixes | Retain `ArchipelagoIdCodec` location composition, `ApRestSiteModel`'s enabled-action fallback, the Lasting Candy blacklist, the Act 2 encounter-only Golden Compass filter, and progressive-count rebuilding on singleplayer continue and multiplayer preparation. The deleted `Patches_CampfireSanity` and `ApNativeRewardMenu` remain replaced by the multiplayer implementations. |

Validation performed on the resolved tree:

- Beta `0.111.0` compile-only client build passed using `C:/Users/terai/sts2dll/v01110`.
  Godot emitted CS8785 (`GodotProjectDir` absent in compile-only mode) and CS0436
  (generated `Main` conflicts with the imported game type).
- C# regression tests: 10 passed. The compiled beta assembly's manifest test also
  passed separately. The complete compatibility-bundle test was not run.
- F# domain tests: 18 passed.
- Archipelago option, logic, and group tests: 96 passed in an isolated framework
  copy. The framework APWorld builder succeeded there, and the archive's integrity,
  manifest, and top-level Python sources were checked. The sibling world and
  installed game were not replaced. Python reported its pure-Python LocationStore
  fallback and the framework's existing `get_all_state` deprecation warning.
- The separate admission harness could not compile: its project does not link
  `ApRewardEffectSpec`, which is referenced by `ApCardAssignmentState`. Both files
  are unchanged by this merge, so this is an existing validation limitation.
- No unresolved index entries or whitespace errors remain. In-game runtime
  behavior is **NOT RUN**; compilation and framework tests do not establish it.

Runtime follow-up on two beta peers with matching RitsuLib:

| Scenario | Expected result |
| --- | --- |
| Use `!collect` for an AP owner, then enter the next campfire | Both replicas omit that owner's collected campfire checks. Expect `Published ... campfire check(s) from an AP location update for player ...`; another slot and a vanilla guest retain their own state. |
| Anytime + Neow Sanity, each pool mode, new run and continue | Neow offers Proceed; the first AP Ancient receipt offers only Neow relics. Later receipts map to Acts 2/3. Reopening and continuing retain assignments and consume each selected reward once. |
| Lose AP connection, earn checks, then reconnect | Gameplay continues and checks replay only to the same server/seed/team/slot. Expect `Automatic Archipelago reconnect completed`; a different identity is refused. |
| Campfire with Rest locked and no upgradeable cards | An enabled fallback permits leaving without purchasing an AP check. Expect `Applied AP rest-site options ... fallback=True`. |
| Immediate Act 2 Ancient versus Anytime/other acts | Golden Compass is eligible only at the immediate Act 2 encounter; Lasting Candy remains excluded. |
| Continue repeatedly with progressive Rest/Smith/Ancient items | Unlock counts rebuild from AP history without increasing on each continue. |
