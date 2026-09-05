# Domain typing after the upstream merge

Audit date: 2026-09-05. Branch: `experiment/fsharp`, created from
`multiplayer-squashed` at `d8b8c7e`. Reassesses the proposal at
`origin/audit/domain-typing` (`c4eee84`, audited base `b630cc9`).

Status: fresh source audit plus a small implemented F# integration trial. The remaining
migrations below are proposals, not implemented fixes or confirmed runtime defects.

## Decision

Use F# as the intended home for domain variants, validated values, and state transitions.
C# remains the default owner of integration, orchestration, side effects, and JSON codecs.
F# may interoperate directly with MegaCrit, RitsuLib, or JSON libraries only when necessary
for a specific domain feature; permission to interoperate is not a migration objective.
Prefer an existing C# adapter when it serves the feature cleanly. For an exception, explain
the concrete need and keep the interop surface narrow. Direct Godot calls and the existing
Harmony entry points stay in C#. Do not translate the whole client or wrap every primitive;
most existing code should remain C#.

The actual project targets `net9.0` with C# 14, and the installed SDK is `10.0.302`.
The trial also targets `net9.0`, uses F# 10, and pins `FSharp.Core` to `10.1.302`.
No runtime or C# language upgrade is part of this change. This decision does not depend
on predictions about future C# union support.

```text
APWorld Python -> C# JSON codecs/adapters -> F# domain values and decisions
MegaCrit / RitsuLib <-> C# integration    <-> F# domain values and decisions
Godot / Harmony    <-> C# entry points and main-loop dispatch
F# -> MegaCrit / RitsuLib / JSON only for a necessary, documented exception
```

The current trial's F# project has no game or JSON references; this is its present shape,
not a permanent restriction on F#. Keep pure decisions separately testable from integration
effects using modules or, where useful, a separate project. An F# integration project is an
option, not a required extra layer. Avoid circular references to the C# mod.

MegaCrit model access, reward factories, commands, serialized-model restoration, RitsuLib
messages/managed actions, and JSON codecs stay in C# by default. Their availability to F#
does not justify moving them. If a domain feature requires direct interop, calls into those
libraries may internally use Godot; the boundary forbids direct Godot calls from F#, not
all operations whose implementation eventually touches the engine. C# performs scene/UI
operations and schedules work on the main thread when required. F# must preserve that
thread affinity when continuing asynchronous work.

Game-dependent F# assemblies must follow the public/beta compatibility build matrix rather
than being assumed safe to share between variants. The current game-independent domain
assembly can remain shared. JSON formats must be explicit and tested in either language.
Use immutable copies when entering pure decisions; a read-only interface over a mutable
dictionary is not an immutable snapshot. Keep default F# union serialization out of the protocol.
Microsoft documents the underlying [union modeling](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions)
and [cross-language project references](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

## What changes from the earlier audit

| Earlier proposal or assumption | Finding in the merged checkout | Revised decision |
| --- | --- | --- |
| No tracked solution | `StS2AP.sln` is tracked | Add the F# project to that solution. |
| Validate one client assembly and dependency copy | `StS2AP.csproj`, `Sts2Compatibility`, `StS2AP.Loader/Bootstrap.cs`, and `assemble_client_variants.ps1` build/select public `0.107.1` and beta `0.111.0` variants | Validate both variants and the shared domain/Core DLLs in the bundle root. The domain assembly must be independent of game API variants. |
| Collapse progress and construction into a richer state model | `ApPlayerRunState` explicitly separates `Progress` from `Construction`; `ApRunData.EnsureConstructionInitialized` seeds only once | Preserve canonical owner progress, replica-local cursors, and host checkpoint ownership as separate types and transitions. Never apply a live owner delta to another replica's construction cursor. |
| Start with a complete mirrored-reward decoder | `ApMirroredRewardSpec` now has strategy, new-generation flag, reveal state, fingerprints, and persistent effects; restored native rewards retain their strategy but must not reroll | Start with strategy/generation policy, then model the full assignment and effect contract. |
| A single receipt ledger keyed by slot/index is sufficient | `ApRelicReceiptState.Claims` intentionally keys by player Net ID and received index; players can share a slot and still receive separate in-game rewards | Distinguish AP receipt identity from per-player grant identity and from run/menu identity. Scope every ledger explicitly. |
| Assignment plus consumption is always contradictory | `MarkConsumed` removes live card/relic assignments, while host claim data preserves decisions for reconciliation | Make live claimability exclusive; preserve consumed assignment provenance when needed for reconciliation, diagnostics, or deterministic reservation. Do not indiscriminately erase historical payloads. |
| Delete old saves and replace the multiplayer protocol immediately | The new trial does not need either change | Keep schemas unchanged here. Design a codec/versioning decision as part of each later migration; old branch design text is not permission to destroy current saves. |
| An operation-list protocol saves about 76% | That was an illustrative calculation for an older DTO | Do not carry the percentage forward. Measure current messages and framing before making a bandwidth claim. |
| Character offsets are loosely named | `ArchipelagoIdCodec` now documents one-based character item blocks and zero-based location blocks | Build on the codec, use `ApCharacterNumber` in domain code, and preserve the wire name `char_offset`. |
| Version surfaces are synchronized at 0.5.3 | This merged base already has client manifest `2.1.0`, world manifest/compatibility `1.1.0`, and no `ModVersion` property | Leave the baseline versions untouched. This audit does not reconcile or downgrade upstream versioning. |

## C# typing work and integration boundaries

These are concrete improvements to existing C# owners. Keep them in C# by default.
Moving integration code requires a specific necessity for the domain feature, rather than
language preference or the fact that F# can call the library.

| Priority | Source and current shape | Proposed C# change | Boundary |
| --- | --- | --- | --- |
| P1 | `Models/ApGrantModels.cs` mixes a receipt record, enums, wire DTOs, and diagnostic snapshots | Split public types into their own files; retain explicit JSON DTOs. Mark transport versus domain intent clearly. | No DTO/property/enum renaming on the wire as incidental cleanup. |
| P1 | `Utils/NonCombatActionAdmissionState.BlockedReason` derives prose from a ten-boolean engine snapshot | Introduce an engine-local `NonCombatBlocker` enum and render its reason separately, preserving priority. | The raw flags are observations that can overlap during transitions; ten flags do not imply a 1,024-case F# lifecycle model. Keep capture and scheduling in C#. |
| P1 | `Utils/ApSessionIdentity` and nested `MultiplayerSupport.ApSessionIdentity` share a name but have different scopes | Name the durable server-qualified identity and lobby slot identity distinctly; retain their intentional relationship. Separate deserialized data from validated identity if construction must be enforced. | `required init` plus a public record is not factory-only validation. URI normalization, hashing, and file paths can move with the owning feature. |
| P1 | `Utils/ManagedActionRequestScheduler`, `ApReconnectController`, `ApFastMpLaunchController` manage callbacks and lifecycle states | Retain named status enums; group coherent callback/request data in sealed records and replace unnamed tuples where roles are easy to swap. | Delegates, cancellation, timers, Godot frame callbacks, and cleanup stay local to the C# owner. |
| P1 | `Utils/Sts2Compatibility`, `AscensionManager.GetLevel` and `CharacterConfig.fromJObject` bridge game enums and names | Validate parsed game enum values; map game-specific identities to semantic domain keys explicitly per compiled API target. | Do not copy MegaCrit enum ordinals into the shared F# assembly. Existing version interpretation is not changed by this trial. |
| P2 | `Patches_ShopSanity.ApSlotCounts`, `UniversalBuffGold`, `DeathLinkEventLedger` already name compact computations/state | Preserve these structures; use named event/delivery keys and bounded inputs where needed. | A short set operation or arithmetic helper does not require an F# migration merely because it is pure. |
| P2 | `Models/IndexedItemInfo.Index` is mutable and described as the only unique identity | Make receipt-envelope mutation deliberate; correct documentation to specify index scope. | External `ItemInfo` remains an adapter input. It must not enter an F# core as an opaque mutable engine object. |

Keep Harmony signatures, Godot nodes/signals and UI mutations, main-loop dispatch, and
the existing compatibility loader in C#. Keep RitsuLib registration/message handling,
MegaCrit model serialization/commands, JSON, and file/network I/O in C# by default.
Any necessary F# interop exception needs a narrow scope and concrete validation. Feature-owned helpers are
appropriate; avoid a shared utility type that accumulates unrelated domain decisions.

## F# domain typing work, in recommended order

### 1. Mirrored reward specifications and materialization (P1, first production slice)

Sources: `Models/ApGrantModels.cs`, `Persistence/ApCardAssignmentState.cs`, and
`Utils/ApMirroredRewardDispatcher` (`BuildSpec`, `ValidateMenuOnHost`,
`PrepareReplicaMaterializations`, `RestoreCardReward`, `MarkConsumed`).

Replace `Kind` plus unrelated fields in the *internal* model with `Card`, `Potion`,
`Relic`, `AncientChoice`, and `Unavailable` cases carrying only appropriate data.
Card construction needs distinct rare/act-based recipes and revealed/unrevealed state;
materialization needs owner-final, restored native, and new native cases. A later
new-native case should require before/after fingerprints and the relevant recipe.
Persistent effects should become typed `SilkenTressUsed` and `SilverCrucibleAdvanced`
values with validated transitions. Validate duplicate effects and counter overflow.

The first trial implements only the existing strategy/flag sub-contract. It intentionally
does not tighten fingerprint/model/cardinality validation or redesign the DTO.
Next, decode the complete reward once and pass that validated value through the
dispatcher. Revalidating and discarding the domain value at every stage would leave
mutable DTOs as the true source of domain truth.

### 2. Progressive starter states (P1)

Sources: `ApProgressiveStarterKindState`, `ApProgressiveStarterState`,
`ProgressiveStarterMultiplayer` (construction, validation, application), and
`ProgressiveStarterUtility`.

Use `Uninitialized | Unsupported | Supported(recipe, tier)`. The supported tier type
must contain only `None | Basic | Upgraded`; reusing the current `ProgressiveStarterTier`
unchanged would still allow `Supported(..., Unsupported)`. Keep card and relic recipes
separate. Validate identifiers and required serialized models before constructing recipes.

F# owns tier decisions; C# retains MegaCrit deck/relic command orchestration by default.
Keep pure transitions distinct from effects. Introduce direct F# interop only if necessary
for this feature; Godot dispatch and Harmony entry points remain C#.
Maintain receipt idempotency, initialization, reset, and save/load mappings together.
Do not add retry/rollback around an authoritative item as part of this typing migration.

### 3. Participant contributions and readiness (P1)

Sources: `ApPlayerRunState`, `ApPlayerContextResolver`,
`ApRunData.TryValidateHostLobbyContributions`, and `MultiplayerSupport`.

Model a guest separately from an own-slot contribution. Within own-slot state,
distinguish awaiting identity/settings/receipt history from ready-to-launch. Preserve
offline continuation from a valid checkpoint: "not connected" does not mean "no valid
participant". A ready contribution owns validated room/team/slot identity, frozen settings,
and a receipt source. An AP receipt uses room/team/slot/index scope; its per-player
realization also needs run/player scope. `ApGrantId` alone is not a globally unique key.

Keep Net ID lookup, host/sender authentication, connection state, and RitsuLib callbacks
in C# by default. Pass authenticated facts to pure validators; a domain record cannot establish
that a network sender really owns a player.

### 4. Reward claim state and host destinations (P1, high correctness value)

Sources: `ArchipelagoProgress`, `ApRunProgressState`, `ApRelicReceiptState`,
`RelicReceiptMultiplayer`, `RelicRewardUtility`, and `ApMirroredRewardDispatcher.MarkConsumed`.

Use an exclusive live state such as unassigned, assigned, or consumed. Model destination
as menu versus a validated chest key, with only the appropriate bank/assignment data.
Retain host-approved consumed provenance and per-player ownership. Enforce stable choices
by received index; a reopened or restored assignment cannot be regenerated.

This needs a written transition table before implementation. Cards skipped and potions
without space remain claimable. Gold/relic grant wrappers consume at the existing game
command boundary. A secondary combat-card failure must not reopen a permanent deck grant.
Do not import the earlier audit's simplistic erase-all-assignment transition without
tracing every reconciliation consumer.

### 5. Progress operations and revisions (P1, after the state models settle)

Sources: `ApProgressDelta.Between/HasChanges/ApplyToCopy`, `ApRunProgressState`,
`ApRunData.PublishLocalProgress/OnProgressDeltaReceived`, and progress message DTOs.

Use typed operations and a validated nonempty transition with `next = base + 1`, bounded
revisions, conflict rules, and atomic application. Keep initial/repair snapshots distinct.
The current receiver already checks sender, initialization, revision ordering, and nonempty
delta; the benefit is making those invariants harder to omit when fields are added.

Create separate `CanonicalProgress`, `ReplicaConstruction`, and `HostCheckpoint` concepts.
The existing replica regression is a required migration gate: a fast owner's live update
must not advance the slow replica, while a newly reconstructed process can seed from the
checkpoint. Do not make all three facets one synchronized immutable record.

Define the explicit JSON codec, unknown case behavior, and save/message version policy in
the same change as any eventual wire replacement. Domain-only typing need not break saves.
Property target: applying the validated diff from A to B yields B for canonical fields,
without affecting replica-local fields.

### 6. Configuration, IDs, and bounded planning (P2, introduce alongside consumers)

- Decode `ShopSanity = Disabled | Enabled configuration`, DeathLink configuration,
  Ancient mode/pool, and validated character settings into immutable domain values.
  `ArchipelagoSettings` is currently documented as read-only but remains mutable.
- `DeathLinkDamagePercent` permits **0 through 100**, per `world/spire2/options.py`.
  The C# property's 1-through-100 comment is stale. Preserve zero's meaning.
- Introduce `ApCharacterNumber`, `CharacterItemTypeId`, `ApLocationId`, `ReceivedItemIndex`,
  `ActIndex`, `ActNumber`, and `ProgressRevision` where a migrated function benefits.
  Require a positive character number, nonnegative receipt/revision, and checked arithmetic.
  Universal IDs below 10,000 must never go through the character-item modulo path.
- `ArchipelagoIdCodec.TryComposeLocationId` checks block bounds and positive character
  number but currently does not check multiplication overflow. Address that at validated
  construction when the ID slice is taken on; it is not fixed by this trial.
- Gold planning should return unavailable/empty/offer with nonnegative amounts and a
  monotonic raw cursor. Preserve cumulative Poverty rounding, withheld refund semantics,
  and `source = granted + withheld`. `GoldRemaining = -1` must remain outside normal
  domain arithmetic. Keep gold command/consumption orchestration in C# by default and
  preserve the exact authoritative consumption boundary.
- Shop capacities, relic selection, and pending-check set reconciliation can move when
  they consume these models. Preserve candidate ordering, seeds, fallbacks, outbox server
  identity, and replay semantics; changing a deterministic algorithm is not a typing cleanup.

### 7. Larger state machines (P2, last)

`MultiplayerSupport` pending/active/prepared/invalidated flags, ascension projection
(`ApRunSharedState`, `AscensionMultiplayer`), and campaign load results remain good F#
candidates. Start with explicit events and transition tables, expose existing C# properties
as projections, then migrate writers. Keep lifecycle subscriptions and save locks in C#
by default; direct Godot calls always remain there. Separate `CampaignLoaded metadata`
from `CampaignInvalid(id, error)` rather than
independently nullable metadata/error fields. Avoid forcing connection, lobby, run,
and reward capability into one enormous state machine.

## APWorld contract

The APWorld stays Python. Future Python typing work: validated character dataclasses,
typed slot-data dictionaries, normalized character lookup keys, and precise
`fill_slot_data`/`interpret_slot_data` signatures. This trial changes no Python files.

Trace a future configuration/ID migration through `characters.py`/`items.py`/`locations.py`
and `world.py.fill_slot_data` -> generated IDs/JSON keys -> `ArchipelagoClient` parsing and
`CharacterConfig.fromJObject` -> the domain adapter -> the runtime consumer.
Use shared JSON fixtures on both sides, not a Python dependency on F#.
Keep Ancient pool/location controls in Game Options. Do not introduce legacy YAML aliases.

## Implemented trial and its limits

`StS2AP.Domain/RewardMaterialization.fs` has private union construction, typed decode
errors (including the unknown wire value), and exhaustive matches with F# warnings treated
as errors. C# consumes `Result` through `IsOk/ResultValue/ErrorValue`; `Func`-based `Match`
members avoid `FSharpFunc` conversion and keep error handling exhaustive at the facade.
Values are not immune to C# nulls/reflection; the decoder is the normal construction boundary.

The production call chain is `CompleteRemoteMenu` on the host -> `ValidateMenuOnHost`
after owner/schema/receipt checks -> `RewardMaterializationAdapter.Decode` -> F# `Decode`.
Errors become the same `InvalidOperationException` and log text as before. Native factories,
fingerprint/effect checks, reward completion, game-thread dispatch, saves, and wire schema
remain as they were. The trial validates and discards the policy at this existing guard;
it is not yet the complete typed execution path. Singleplayer does not use this host guard.
For a reliable targeted test, the non-host AP participant must open the menu: its
`OpenMenu` sends to the host, which enters `CompleteRemoteMenu` and calls F# for each
card/potion entry. A vanilla guest's empty menu, gold-only menu, or normal combat reward
does not exercise this guard. Do not rely on the host opening only its own menu.

The three successful decisions are:

| Wire strategy | Native materialization flag | Domain decision |
| --- | --- | --- |
| `ap_rng_owner_final_v1` | false | OwnerFinal |
| `replica_native_v1` | false | RestoredReplicaNative |
| `replica_native_v1` | true | NewReplicaNative |

Owner-final with true is inconsistent; all other strings, including null, are unknown.
No strategy string or accepted combination changed.

## Validation and reproduction

Commands run from the repository root:

```powershell
dotnet build client/StS2AP/StS2AP.csproj -c Debug -t:Compile --no-restore
dotnet test client/StS2AP.Domain.Tests/StS2AP.Domain.Tests.fsproj -c Release
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release
# Use a workspace-only fake game root, preserving the installed mod.
dotnet build client/StS2AP/StS2AP.csproj -c Debug -p:STS2GamePath=C:\Users\terai\Projects\Slay-the-Spire-2-Archipelago\artifacts\fsharp-trial-game
$env:STS2AP_TEST_BUNDLE = (Resolve-Path 'artifacts/fsharp-trial-game/mods/Archipelago').Path
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release --filter 'Category=Bundle'
Remove-Item Env:STS2AP_TEST_BUNDLE
```

| Layer | Result |
| --- | --- |
| Baseline C# compilation | PASS, beta references; preexisting CS8785 missing `GodotProjectDir` and CS0436 generated `Main` conflict warnings. |
| F# library / C# interop | Initial trial passed 1,014 strategy/flag checks. Domain coverage now lives in the F# xUnit/FsCheck suite; C# xUnit retains three adapter cases: success and both exception conversions. |
| F# xUnit/FsCheck suite | PASS, 18 cases including four properties with 500 generated examples each. |
| C# xUnit conversion | PASS, all six cases with artifact paths supplied: three interop cases, the existing construction regression, beta intermediate manifests, and both installed bundle variants. Without paths, four pass and the two packaging cases are explicitly skipped. No new in-game run was performed for the test conversion. |
| Existing replica construction regression | PASS. |
| Final local checks | PASS, Debug and Release regressions, final beta C# compilation, tracked diff review and `git diff --check`, new-file review/whitespace check. |
| CI | The compatibility workflow checks manifests through C# xUnit. The standalone test workflow runs F# domain tests and C# interop/construction tests. Remote results for this conversion are not yet confirmed. |
| Full client/package path | PASS, Godot 4.5.1 PCK export, both 0.107.1 and 0.111.0 DLL variants, compatibility loader, domain and FSharp.Core DLLs copied to bundle root. Existing generator warnings persist. |
| Packaged assembly loading | PASS for both variants: invoke their real C# adapter through the shipped loader's dependency resolver in an isolated AssemblyLoadContext. Verify domain/Core load from the bundle root, not the test runner's copies. |
| Embedded manifests after reported startup failure | Initially FAIL in the installed beta DLL and its intermediate output (no resources). After the resource-preparation fix, PASS for compile-only outputs and installed public/beta variants; mod manifest matches the external manifest, both embedded version fields parse, and deployed hashes match the loader manifest. |
| Runtime used by regression executable | .NET 10.0.10 via existing `RollForward=Major`; system-wide .NET 9 runtime is not installed. Targeting net9.0 is not proof of execution in the game's embedded runtime. |
| Decompiled game source | NOT INSPECTED: `Spire2-Decompiled/` is absent here. Trial adds no game API assumptions; both reference-assembly compilations ran. |
| In-game smoke test after manifest repair | USER-REPORTED PASS: the user reported that the in-game test "seemed to have worked well enough." Individual test-matrix cases and logs were not supplied; full multiplayer replay/reconnect coverage remains unconfirmed. Managed loader tests themselves do not establish Godot/Harmony/RitsuLib startup. |
| APWorld framework tests / fresh package build | NOT RUN; no Python changes. Full client build may copy the preexisting `dist/spire2.apworld`; that is not a newly validated APWorld. |

Initial trial output: `artifacts/fsharp-trial-game/mods/Archipelago`; build transcript:
`artifacts/fsharp-full-build.log`. Initial generated Godot configuration-only churn was
removed after inspection. The later manifest repair was deployed to the installed game,
as described below. The trial made no release or version change.

### Startup manifest repair

The reported `StS2AP.Archipelago.json` error was reproduced against the installed beta
variant: it contained no embedded resources, while the public variant contained both
manifests. The beta intermediate assembly also lacked resources. The earlier compile-only
validation skipped MSBuild's resource preparation, leaving a resource-free intermediate
DLL that a subsequent incremental build could copy into the bundle. This was a build
validation gap; the F# domain decoder was not involved in the failing startup path.

`StS2AP.csproj.PrepareClientResourcesForCompile` now runs `PrepareResources` before
`_GenerateCompileInputs`, including for `-t:Compile`. Each variant embeds its own copies
of both manifests. No mod-root JSON fallback was introduced, and a fresh APWorld build
was unnecessary. C# xUnit `PackagingTests` checks actual embedded streams;
`STS2AP_TEST_ASSEMBLY` selects an intermediate/output DLL. CI exercises compile-only validation followed
by an incremental build and verifies resources at both stages.

Both Debug compile-only variants passed the new checks. A full incremental build exported
the PCK and redeployed the installed bundle; that build reported zero warnings/errors.
Checks against the installed bundle passed for both manifest pairs, both C# -> F# calls,
and both variant hashes. The user subsequently reported a successful in-game smoke test;
the exact reward/reconnect scenarios exercised were not specified.
Repair transcript: `artifacts/manifest-repair-deploy.log`. Godot configuration files already
dirty at the start of the repair were preserved.

### Required in-game checks before expanding the migration

The repaired trial bundle is now installed locally. For subsequent changes, with the game
closed, a normal full build from this branch deploys the current client
using `local.props` (and replaces the installed Archipelago mod directory):

```powershell
dotnet build client/StS2AP/StS2AP.csproj -c Debug
```

Use that complete resulting `mods/Archipelago` bundle on both peers, including root
`StS2AP.Domain.dll`, `FSharp.Core.dll`, the loader, and both `lib/<version>` variants.
Use beta 0.111.0 and its matching RitsuLib installation for multiplayer. No new APWorld
generation or schema migration is required for this trial.

The primary test is a two-player run with the joining player connected to an AP slot,
not participating as a vanilla guest. Give that player unclaimed AP card and potion
rewards, then have that player open the AP reward menu outside combat. Record card
choices, close/reopen without claiming, skip a card, test a potion with full slots,
and finally claim after making space. Repeat an unclaimed reward after save/rejoin.
Expect stable choices, no synchronization hang, and exactly one successful grant.

There is no new F# success log in this trial. The non-host menu scenario selects the
actual F# call path; the existing materialization messages are supporting diagnostics,
not a standalone proof that the validator ran. Unknown-strategy rejection is covered
by the automated tests; normal play does not need a deliberately malformed message.

| Scenario | Expected behavior / evidence |
| --- | --- |
| Launch the packaged client on beta with matching RitsuLib | Existing `[Archipelago.Loader] Loaded Archipelago ... target 0.111.0` line; no missing `FSharp.Core` or domain assembly exception. |
| Non-host AP participant opens a fresh card/potion menu | Host executes the F# validator and both replicas agree; existing `Materialized AP card reward ... with ...` / `Materialized AP potion reward ...` diagnostic retains its receipt identity and strategy. |
| Close and reopen a previously assigned native reward; repeat after save/rejoin | Identical choices; no new native generation caused by the restored strategy. |
| Skip a card / attempt a potion with full slots | Reward remains claimable; subsequent valid claim consumes once. |
| Valid owner-final reward and an unsupported strategy in a development fixture | Valid reward remains usable; invalid fixture yields `AP reward <slot>:<index> used an unknown materialization strategy.` without granting/consuming it. |
| Public variant singleplayer smoke test | Loader selects 0.107.1; ordinary rewards remain unchanged. This is distinct from the beta multiplayer check. |

Following the reported in-game smoke-test pass, the recommended next implementation should expand the mirrored-reward
decoder and have execution consume its typed result. Progressive starter modeling is
the next independent slice; ledger/progress-schema replacement comes later.
