# Build and release scripts

The Bash scripts are the macOS/Linux equivalents of the existing PowerShell
helpers. Run them from any working directory; each resolves the repository root
from its own location.

## Build the APWorld

```bash
./scripts/build_world.sh
```

The default Archipelago checkout is the sibling `../Archipelago` directory.
Override it when needed:

```bash
./scripts/build_world.sh --archipelago-root /path/to/Archipelago
```

By default the script uses `/path/to/Archipelago/.venv/bin/python`. If the
environment does not exist, it uses `uv venv --seed` to create it with a
supported Python and runs `ModuleUpdate.py --yes` inside it. This avoids
Archipelago trying to install packages into a PEP 668/uv-managed global
interpreter. Pass `--python /path/to/an/existing/venv/bin/python` to manage the
environment yourself.

This script replaces `Archipelago/worlds/spire2`, invokes the Archipelago
launcher, and copies `build/apworlds/spire2.apworld` to `dist/`.

## Preview the legacy item enum generator

```bash
./scripts/generate_item_enums.sh
```

The PowerShell generator is stale relative to the current client: it emits
standalone `RawItemID` and `RawCharacterID` enums and would replace the richer
`ItemTable.cs`. The Bash port therefore prints a diff without changing files by
default. Use `--write` only after reviewing that complete replacement:

```bash
./scripts/generate_item_enums.sh --write
```

Unlike the PowerShell version, the Bash generator can read the three relevant
world modules without an installed Archipelago framework. It backs up an
existing output to `.bak` before an explicit write.

## Release

```bash
./scripts/release.sh 0.5.4
./scripts/release.sh alpha-0.5.4 --skip-github
```

The release script intentionally refuses to run unless the tracked working tree
and index are clean and the current branch is `main`. It then:

1. synchronizes the four public version surfaces and commits them;
2. calls `build_world.sh`;
3. builds the C# client with `dotnet`;
4. creates `dist/sts2-client.zip` and verifies `dist/spire2.apworld`;
5. creates the Git tag;
6. unless `--skip-github` was supplied, pushes `main` and the tag and creates a
   GitHub release with every file in `dist/`.

The local `client/StS2AP/local.props` version is updated when the file exists,
but it is deliberately not staged.
