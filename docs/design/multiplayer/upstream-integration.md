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
