# Local agent guidance

## Project map

- `world/spire2/` is the Python Archipelago world (APWorld). It defines the game's Archipelago items, locations/checks, options, access rules, and data used when Archipelago generates a multiworld. An APWorld describes what can be randomized and checked; it does not run as the game mod.
- Use the sibling `../Archipelago/worlds/apquest/` as the worked guide for ordinary APWorld structure and conventions. For features APQuest does not cover, such as custom rule builder classes or `LogicMixin`, inspect the corresponding Archipelago implementation and documentation.
- `client/` is the game mod/client. Its C# code runs inside Slay the Spire 2, applies Harmony patches, communicates with Archipelago, grants received items, and reports completed locations. `client/StS2AP.Domain/` contains shared F# domain logic.
- `web/` is a separate web application. Avoid changing it unless the user asks for web work or the requested change genuinely requires it; explain any such scope expansion.

## Learn from the game

- Decompiled game source is available locally for both supported versions: main `0.107.1` at `.local-tools/sts2-decompiled/Spire2-Main-0.107.1-Decompiled/` and beta `0.111.0` at `.local-tools/sts2-decompiled/Spire2-Beta-Decompiled/`. These directories are ignored by Git. Consult the matching version routinely, especially before writing or changing Harmony patches: find the target method, callers, state ownership, and nearby game behavior instead of guessing from names. Compare both versions when a patch or client behavior must work on both.
- The local `ilspycmd` executable is `.local-tools/ilspycmd/ilspycmd`; the main source was decompiled from `.local-tools/sts2-references/0.107.1/sts2.dll`. Decompiled code is evidence about its particular build, not a guarantee of runtime behavior. Check important behavior in the game when practical.
- Rider and PyCharm MCP servers are available for IDE-indexed search, symbol navigation, call hierarchy, inspections, and builds. Use them when they help; use `rg`, `fd`, and other fast search tools for ordinary file and text searches. If an IDE server is unavailable, continue with filesystem and command-line tools.

## Local library references

- RitsuLib source is cloned at `.local-tools/STS2-RitsuLib/` from https://github.com/BAKAOLC/STS2-RitsuLib and is ignored by Git. For future telemetry work, start with `src/Telemetry/` and `docs/pages/guide/telemetry-backend.md`; for Harmony diagnostics, inspect `src/Patching/` and `src/Diagnostics/HarmonyPatchDumpWriter.cs`. Exception telemetry includes framework patcher status/counts in `src/Telemetry/Diagnostics/DiagnosticsTelemetryCollector.cs`; the full Harmony dump is a separate local report.
- Additional telemetry references: IntoTheSpireverse's `IntoTheSpireverseCode/Metrics/` at https://github.com/Shadowfall-Team/IntoTheSpireverse/tree/main/IntoTheSpireverseCode/Metrics and https://tutorials.sts2modding.com/en/docs/04-ritsulib/04-29-telemetry/. These are references for future integration, not installed client dependencies; check the selected RitsuLib version against both supported game APIs before integrating.

## Versions and compatibility

- "main" and "beta" usually mean Slay the Spire 2 game branches, not this repository's Git branches. The currently supported game APIs are main `0.107.1` and beta `0.111.0`. Support both when changing shared client code; select and check each `Sts2ApiCompat` version as appropriate.
- Prefer compile-time version branches (`STS_PUBLIC` / `STS_BETA`, including `client/StS2AP/Utils/BetaMainCompatibility.cs`) over reflection for game API differences. The client is compiled separately against each game API; `client/StS2AP.Loader/` selects and loads the appropriate precompiled `lib/<version>/Archipelago.dll` at runtime. Reflection inside that loader is part of assembly loading, not a reason to replace compile-time checks in client features.
- Prefer direct, strongly typed access to game members exposed by `Krafs.Publicizer` (or the publicized reference libraries) over reflection such as `AccessTools.Field`/`GetValue` whenever possible, including private fields and methods. This lets builds against both supported game APIs catch renamed, removed, or type-changed members at compile time after game updates. Use reflection only when direct access is not viable, and explain the constraint in a comment.
- The GitHub `build-sts2-compat` workflow compiles the client against both game APIs, and client build/package workflows also build the loader and variants. Use these compile checks locally when changing compatibility code; compilation catches API mismatches that reflective access cannot.
- Multiplayer development is active. Do not spend effort preserving old multiplayer behavior, old saves, or older mod/game versions unless the task calls for it; players can use the corresponding older mod and game versions. This does not remove support for the two current game APIs above.
- Preserve APWorld-facing contracts unless a change explicitly updates them. Treat item and location IDs, slot data, and other data exchanged between the APWorld and client as compatibility-sensitive; investigate their consumers before changing them.

## Local shell execution

- This checkout runs natively on Arch Linux at `/home/terp/RealProjects/Slay-the-Spire-2-Archipelago` with zsh; the sibling Archipelago 0.6.8 checkout is `/home/terp/RealProjects/Archipelago`. Use the repository's `.venv/bin/python` for APWorld work.
- Slay the Spire 2 is a native Linux Steam install at `/home/terp/.local/share/Steam/steamapps/common/Slay the Spire 2`; its assemblies are in `data_sts2_linuxbsd_x86_64/`, and `release_info.json` records the installed game version. Keep `.local-tools/sts2-references/` as the versioned build references rather than reading DLLs from the install.
- The previous WSL2/Codex sandbox-launcher workaround is historical; see `docs/contributing/linux-migration-notes.md` if it matters again.

## Code and working style

- Write code for human readers: clear names, straightforward control flow, and comments that explain intent, constraints, or non-obvious game behavior. Avoid comments that merely restate what the next line does.
- Choose builds and tests that fit the change. There is no preferred IDE or command, and routine builds, multiplayer tests, and local game launches do not require extra approval.
- Focus on APWorld, client, and multiplayer work. Releases are handled mainly by GitHub workflows and need less local guidance.
