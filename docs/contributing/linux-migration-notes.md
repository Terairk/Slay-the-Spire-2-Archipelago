# Moving this development setup to Linux

Personal recovery notes, inventoried on 2026-10-07 from `apworld-logic-overhaul`
at `32ee27d`. These are approximate setup instructions, not a freshly tested
Linux installation recipe. Start here for the otherwise invisible local files;
the [existing WSL setup](wsl-setup.md), [API compatibility guide](sts2-api-compat.md),
and [contributing guide](../../CONTRIBUTING.md) contain the fuller build details.

## Before leaving the old machine

A clone only restores committed files on the branches you fetch. This branch
tracks the previously excluded root [AGENTS.md](../../AGENTS.md) and these notes;
it does **not** contain the ignored tools, DLLs, reports, or personal settings.
Push this branch to your fork or transfer a Git bundle before retiring the old
checkout. Preserve any other local branches and uncommitted work too.

Prioritize a private backup of:

| Path | Why keep it / how to replace it |
| --- | --- |
| `.local-tools/sts2-references/` | Actual game assemblies for both historical versions. Keep these if possible: a later Steam install may no longer supply these exact builds. |
| `.local-tools/sts2-decompiled/` | Decompiled source for those assemblies; regenerable if the original DLLs survive. |
| `artifacts/` | Experiment databases, reports, failure cases, patch checks, and mod-install backups. These are research history, not just disposable build output. |
| `.local-tools/waimea-failures/`, `campfire-ab/`, `hook-ab/`, `buff-validation/`, `task-id-investigation/`, `multiplayer-launch-diagnosis/` | Local investigations: scripts, patches, reproduction inputs, and logs that a clone will not restore. |
| `.local-tools/check_starter_logic.py`, `.local-tools/starter-logic-results.json` | Local-only starter logic check and results. |
| `.local-tools/ut-fuzz/`, `.local-tools/ut-fuzz-smoke/` | Fuzz outputs and reproductions. |
| `.local-tools/previous-apworld/`, `client-build-84fde26/`, `release-2.5.5-source/` | Old comparison/build snapshots. Some can be reconstructed from Git; copy them if unsure about local differences. |
| `.local-tools/data/Archipelago/`, sibling `../Archipelago/host.yaml` and `custom_worlds/` | Local AP settings, installed worlds, and any player YAMLs or generation data. Also check wherever you actually stored `Players/` and output files. |
| `docs/misc/telemetry-integration.md` | Locally excluded design notes. This is not preserved merely by committing `AGENTS.md`. |
| `client/StS2AP/local.props`, `global.json`, `.git/info/exclude` | Machine-specific build paths, SDK selection, and local exclude rules. Examples below let you recreate them. |
| `.codex/config.toml`, `.idea/`, `StS2AP.sln.DotSettings.user` | Optional agent/IDE configuration; recreate paths and integrations on Linux. |
| `.local-tools/telemetry/`, `logs/`, game saves/settings | Private diagnostics and runtime state. Keep only what you need; do not add credentials or player data to this branch. |

In particular, `artifacts/logic-matrix-7196038/` contains `results.sqlite`, CSVs,
reports, validation/provenance JSON, and the input YAML for the old/new logic
comparison. Preserve the whole directory if you want to keep querying that work.
The statistics tooling lives on the separate `logic-statistics` branch; it is
not present on this branch's base. Other local branches at inventory time were
`client-checkpoints-and-rewards`, `multiplayer-fixes`, `opt-in-telemetry`,
`release-2.5.5`, `v2`, and `main`.

For an inventory before copying, run from the old repository:

```bash
git status --short
git branch -vv
git status --short --ignored
cat .git/info/exclude
git config --get core.excludesfile  # no output is fine
```

Use a private archive or file transfer for the selected directories. Preserve
symlinks, but recreate links containing the old absolute checkout path. Rebuild
`.venv/`, `bin/`, `obj/`, and Godot caches instead of relying on copied environments.
This inventory is not a backup of files outside the checkout, such as game saves
or global Codex/IDE settings.

## 1. Keep Archipelago beside this repository

The layout expected by `scripts/build_apworld_local.py` is:

```text
~/project/
├── Archipelago/                       # upstream tag 0.6.7
│   └── worlds/
│       ├── apquest/                   # ordinary APWorld example/reference
│       └── spire2 -> <repo>/world/spire2
└── Slay-the-Spire-2-Archipelago/
    ├── world/spire2/
    ├── client/
    ├── .venv/
    └── .local-tools/
```

The symlink shown conceptually above should be created with the absolute-path
command below. Install Git, `uv`, and normal compiler/build tools through your
Linux distribution or their installers. From this repository's root, for a
**new** sibling checkout and environment:

```bash
git clone --depth 1 --branch 0.6.7 https://github.com/ArchipelagoMW/Archipelago.git ../Archipelago
uv python install 3.13
uv venv --python 3.13 --seed .venv
.venv/bin/python ../Archipelago/ModuleUpdate.py -y
ln -s "$PWD/world/spire2" ../Archipelago/worlds/spire2
site_packages=$(.venv/bin/python -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')
realpath ../Archipelago > "$site_packages/archipelago.pth"
```

Skip clone/link steps if they already exist; inspect an existing `worlds/spire2`
before replacing anything. The original sibling was upstream
`ArchipelagoMW/Archipelago` at `debe4cf035c7c15efe6fb95f72343af0d420c68c`
(tag `0.6.7`). `worlds/apquest/` is included there; no separate APQuest clone is
needed. For custom rule builders or `LogicMixin`, read the framework itself.

The old Python was CPython 3.13.15 installed by uv under `.local-tools/python/`.
That exact storage location is optional. To retain it, set
`UV_PYTHON_INSTALL_DIR="$PWD/.local-tools/python"` before installing Python and
creating the venv. Point your IDE at `.venv/bin/python`; the `.pth` file above
makes the sibling's `worlds.*` imports available.

## 2. Restore local game references and decompiled source

Supported APIs in this snapshot are main/public `0.107.1` and beta `0.111.0`.
Check `client/StS2AP/SupportedGameVersions.props` when returning to this later.
Keep this layout:

```text
.local-tools/
├── sts2-references/
│   ├── 0.107.1/     # sts2.dll, 0Harmony.dll, GodotSharp.dll
│   └── 0.111.0/     # sts2.dll, 0Harmony.dll, GodotSharp.dll
├── ilspycmd/ilspycmd
└── sts2-decompiled/
    ├── Spire2-Main-0.107.1-Decompiled/
    └── Spire2-Beta-Decompiled/
```

Prefer copying the existing versioned DLL directories. Otherwise take the three
DLLs from your own matching game installation. On the old Windows install they
were in `data_sts2_windows_x86_64/`; locate the equivalent managed assemblies in
your actual Linux/Proton installation instead of assuming the Windows path.
Do not label newer DLLs with an older version directory name.

After installing .NET, recreate ILSpy (the old tool version was `11.1.0.9782`):

```bash
dotnet tool install ilspycmd --tool-path .local-tools/ilspycmd --version 11.1.0.9782
.local-tools/ilspycmd/ilspycmd -p \
  -o .local-tools/sts2-decompiled/Spire2-Main-0.107.1-Decompiled \
  .local-tools/sts2-references/0.107.1/sts2.dll
.local-tools/ilspycmd/ilspycmd -p \
  -o .local-tools/sts2-decompiled/Spire2-Beta-Decompiled \
  .local-tools/sts2-references/0.111.0/sts2.dll
```

Generate into fresh output directories, or just restore the existing source
trees. The stripped NuGet reference assemblies can replace local DLLs for
**compilation**, but do not replace real method bodies for game-behavior research.
Consult both versions before changing shared Harmony patches.

## 3. .NET, Godot, and local build properties

Install the .NET 10 SDK. The projects target `net9.0`; the domain test README
documents its runtime roll-forward behavior. The old excluded root `global.json`
contained this optional SDK selection:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

Install the Linux **.NET/Mono** build of Godot 4.5.1, not the standard edition.
The old x86_64 layout was
`.local-tools/godot/Godot_v4.5.1-stable_mono_linux_x86_64/`;
[the WSL guide](wsl-setup.md#client-and-godot) has the download/extraction commands.
Use the appropriate architecture for the new machine. Minimal Linux installs
may also need fontconfig, freetype, and libpng.

Create ignored `client/StS2AP/local.props` from the tracked template. A useful
Linux starting point, with compilation as the default, is:

```xml
<Project>
  <PropertyGroup>
    <Sts2ApiCompat>$(Sts2BetaApiCompat)</Sts2ApiCompat>
    <UseSts2RefLib>true</UseSts2RefLib>
    <GodotExePath>$(MSBuildThisFileDirectory)../../.local-tools/godot/Godot_v4.5.1-stable_mono_linux_x86_64/Godot_v4.5.1-stable_mono_linux.x86_64</GodotExePath>
    <PythonExe>$(MSBuildThisFileDirectory)../../.venv/bin/python</PythonExe>
    <BuildMode>CompileOnly</BuildMode>
    <ModsOutputDir>$(MSBuildThisFileDirectory)../../dist/Archipelago</ModsOutputDir>
  </PropertyGroup>
</Project>
```

For the old local-DLL setup, set `UseSts2RefLib` to `false` and add
`<Sts2ApiSignatureRoot>$(MSBuildThisFileDirectory)../../.local-tools/sts2-references</Sts2ApiSignatureRoot>`.
Set `STS2GamePath` to the actual Steam installation if you want local deployment.
The old `D:/SteamLibrary/...`, `/mnt/d/...`, Windows Python, and Windows Godot
paths are machine-specific; do not copy them into the native Linux setup.

`BuildMode=Package` stages the loader and both API variants under the configured
`ModsOutputDir`. To enable normal game deployment later, remove that staging
override, use `BuildMode=Local`, and configure `STS2GamePath`.

## 4. Reference clones and optional research tools

Run these only if the destination directories do not already exist:

```bash
mkdir -p .local-tools
git clone https://github.com/BAKAOLC/STS2-RitsuLib.git .local-tools/STS2-RitsuLib
git clone https://github.com/Eijebong/Archipelago-fuzzer.git .local-tools/Archipelago-fuzzer
```

Recorded clean source revisions (fuzzer only had an untracked `__pycache__/`):

- RitsuLib: `1bebdb0365c34f8dc9066ded81ac4587df145449`.
- Archipelago-fuzzer: `17227d793cc1088ae9be5fe97421e73bb683efdf`.

Use `git -C <clone> checkout <revision>` to reproduce those reference snapshots.
The RitsuLib clone is for reading source; the client project controls its actual
build dependency. Start at `src/Telemetry/`,
`docs/pages/guide/telemetry-backend.md`, `src/Patching/`, and
`src/Diagnostics/HarmonyPatchDumpWriter.cs`.

The fuzzer README explains its flags, hooks, and outputs. The repository's
`scripts/fuzz_world.ps1` also documents the existing suites, but has Windows
Python/branch defaults that need adjusting on Linux. Keep failing YAMLs, seeds,
meta files, and hook scripts with their reports. Give parallel experiment workers
separate AP user-data directories to avoid `host.yaml` creation races.

`.local-tools/celeste-v1.1-ap/` is an optional comparison checkout from
`https://github.com/PoryGoneDev/Pory_Archipelago.git`, not the required sibling
Archipelago checkout. Preserve it if you need its local build/custom-world data;
it is unnecessary for the normal Spire build.

Additional telemetry reading locations are recorded in `AGENTS.md`; they are not
dependencies that need installing for a client build.

## 5. Agent settings, excludes, and Linux caveats

`AGENTS.md` is preserved as it existed on the old machine. On the new machine,
update its **Local shell execution** section for the real checkout path and
available Linux shell. Its WSL2/Codex sandbox-launcher workaround is historical,
not a native Linux prerequisite. Rider/PyCharm MCP integrations mentioned there
also need configuring again; normal CLI work does not depend on them.

The old `.git/info/exclude` added:

```gitignore
/global.json
/AGENTS.md
/docs/misc/telemetry-integration.md
```

This file is local to a checkout. `.gitignore` already excludes `.local-tools/`,
`.venv/`, `local.props`, `.codex/config.toml`, and `artifacts/`. Since `AGENTS.md`
is now tracked on this branch, an exclude entry does not hide edits to it.
Back up the telemetry note separately, or explicitly track it later if wanted.

The existing multiplayer shell launcher calls Windows PowerShell and the Windows
game. It is not a native Linux two-process launcher. Getting compilation and
packaging working does not establish native Linux gameplay support; revisit
launch paths and test the game separately after the development setup works.

## 6. Quick checks after restoring

From the repository root:

```bash
readlink -f ../Archipelago/worlds/spire2
.venv/bin/python -c 'import worlds.spire2; print(worlds.spire2.__file__)'
.venv/bin/python scripts/build_apworld_local.py
dotnet build client/StS2AP/StS2AP.csproj -c Release -p:Sts2ApiCompat=0.107.1 -p:UseSts2RefLib=true -p:BuildMode=CompileOnly
dotnet build client/StS2AP/StS2AP.csproj -c Release -p:Sts2ApiCompat=0.111.0 -p:UseSts2RefLib=true -p:BuildMode=CompileOnly
dotnet test client/StS2AP.Domain.Tests/StS2AP.Domain.Tests.fsproj -c Release
dotnet test client/StS2AP.RegressionTests/StS2AP.RegressionTests.csproj -c Release
dotnet build client/StS2AP/StS2AP.csproj -c Release -p:BuildMode=Package
```

Expect the world link/import to resolve into this checkout,
`dist/spire2.apworld` to exist, and the staged client bundle in `dist/Archipelago/`.
Artifact-dependent regression tests can skip until their documented environment
variables are supplied; see the [regression test README](../../client/StS2AP.RegressionTests/README.md).
Check APWorld logic tests using the sibling framework too:

```bash
repo_root="$PWD"
mkdir -p "$repo_root/.local-tools/data/Archipelago"
(
  cd ../Archipelago
  "$repo_root/.venv/bin/python" - "$repo_root" <<'PY'
import sys
import unittest
from pathlib import Path
import Utils

Utils.user_path.cached_path = str(Path(sys.argv[1]) / '.local-tools/data/Archipelago')
unittest.main(module=None, argv=['unittest', 'discover', '-s', 'worlds/spire2/test', '-t', '.', '-p', '*_tests.py'])
PY
)
```

If these pass, the development environment has a useful baseline. Live gameplay,
multiplayer, saves, and the actual Linux launch method still need their own checks.
