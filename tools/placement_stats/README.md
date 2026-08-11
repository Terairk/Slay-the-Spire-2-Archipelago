# Placement statistics with Archipelago-fuzzer

This tool records raw placements directly from the generated `MultiWorld`. It
does not parse spoiler logs and the hook itself uses only Python's standard
library.

## Layout

- `placement_stats_hook.py`: fuzzer hook; calculates Archipelago logical
  spheres and writes one CSV shard per successful generation.
- `sample-yamls/slay-the-spire-2.yaml`: one fixed, explicit settings profile.
- `analyze.py`: DuckDB summary/custom-query CLI and optional Parquet compactor.
- `requirements-analysis.txt`: analysis-only dependency.
- `tests/`: standard-library unit tests for shard and sphere handling.

## Verified lifecycle and APIs

This implementation was checked against:

- Archipelago-fuzzer commit `ebe01d5523f04a2a0a1de5eb7229d10ef12b8fc2`.
  Its worker calls `before_generate(generator_args)`, calls Archipelago, and
  then calls `after_generate(multiworld, temporary_output_directory)` from a
  `finally` block. `multiworld` is the `MultiWorld` returned by `Main.main` on
  success and is `None` on failure. The output directory is temporary and is
  deleted after the callback, so this hook writes to its own configured path.
- Archipelago commit `fe5b49e1899b32bcb9f65e91cb7f74d6aa6d0ff8`
  (core version `0.6.8`). `MultiWorld.get_filled_locations()` returns filled
  `Location` objects. `Location.item`, `.player`, `.parent_region`, and
  `.address` provide placement data. `MultiWorld.get_spheres()` yields sets of
  reachable filled locations in logical-sphere order. It emits an empty-set
  sentinel followed by the unreachable locations when any remain.
- With `--skip-output`, current `Main.main` still completes `post_fill`,
  progression balancing, `finalize_multiworld`, and `pre_output`, then returns
  the fully generated `MultiWorld` before multidata/spoiler output.
- Current Archipelago `Generate.main` expects an `allow_quantity` namespace
  field that the pinned fuzzer does not yet provide. The hook supplies the
  normal disabled value when it is absent, without overriding future fuzzer
  versions that provide it themselves.

The hook numbers the first yielded reachable set as sphere 1. Unreachable
placements have an empty `sphere` and `reachable=false`.

## Generate fixed-config seeds

From this repository, run:

```console
python tools/placement_stats/run_experiment.py
```

That one command uses the sibling `../Archipelago` checkout, synchronizes and
builds the current APWorld, copies `spire2.apworld` to this repository's
`dist/`, installs the pinned fuzzer in the Archipelago checkout if it is
missing, and runs 10,000 generations. It configures the hook internally; no
`export`, manual `PYTHONPATH`, file copy, or directory change is needed.

Results go to a timestamped directory under
`artifacts/placement_stats/`, which is ignored by Git. The directory and the
same timestamped dataset label are printed before the run starts.

The runner uses only Python's standard library and handles POSIX and Windows
virtualenv layouts. For a quick smoke run or a custom checkout:

```console
python tools/placement_stats/run_experiment.py --runs 10 --jobs 2

python tools/placement_stats/run_experiment.py \
  --archipelago-root /path/to/Archipelago \
  --runs 10000 \
  --jobs 16 \
  --label sts2-v053-fixed-ironclad \
  --output /absolute/path/to/stats/sts2-v053
```

On Windows, `py tools/placement_stats/run_experiment.py` works as well. Run
`python tools/placement_stats/run_experiment.py --help` for all overrides. The
runner never replaces an existing `fuzz.py` unless `--fuzzer PATH` is supplied
explicitly. `--skip-prepare` is available for repeated runs after the APWorld
and fuzzer are already synchronized.

The runner uses `--sample-from` instead of `-g`: the current fuzzer makes those
flags mutually exclusive. With one YAML and `-n 1`, every generation uses that
YAML. Point `--yaml-dir` at a directory containing only the intended YAML.

The fuzzer deletes its own `./fuzz_output` at startup. An absolute
`AP_PLACEMENT_STATS_DIR` outside that directory makes retention explicit. The
hook appends unique shards, so use a fresh directory or label for each
experiment.

## Analyze

Use the output directory printed by the runner as the data argument below:

```bash
# Most common locations for an item
uv run --with duckdb python tools/placement_stats/analyze.py \
  item-locations /absolute/path/to/stats/sts2-v053 \
  --item "Ironclad Relic"

# Most common items at a location
uv run --with duckdb python tools/placement_stats/analyze.py \
  location-items /absolute/path/to/stats/sts2-v053 \
  --location "Ironclad Card Reward 1"

# Sphere distribution of every placed copy of an item
uv run --with duckdb python tools/placement_stats/analyze.py \
  item-spheres /absolute/path/to/stats/sts2-v053 \
  --item "Ironclad Relic"

# Sphere where the first reachable copy appears in each seed
uv run --with duckdb python tools/placement_stats/analyze.py \
  item-nth-sphere /absolute/path/to/stats/sts2-v053 \
  --item "Ironclad Relic" --count 1

# Sphere by which three reachable copies are available in each seed
uv run --with duckdb python tools/placement_stats/analyze.py \
  item-nth-sphere /absolute/path/to/stats/sts2-v053 \
  --item "Ironclad Relic" --count 3
```

`item-nth-sphere` ranks reachable copies separately inside every generation.
Its `not_reached` row includes seeds with fewer than the requested number of
reachable copies. `cumulative_percent_by_sphere` is the percentage of all
generated seeds that have reached that many copies by the given sphere.

For faster repeated analysis after generation finishes, compact the shards to
Parquet without loading the dataset into Python memory:

```bash
uv run --with duckdb python tools/placement_stats/analyze.py \
  compact /absolute/path/to/stats/sts2-v053 \
  /absolute/path/to/stats/sts2-v053.parquet
```

Every command also accepts that Parquet file as its data argument. For custom
metrics, the CLI exposes a `placements` view:

```bash
uv run --with duckdb python tools/placement_stats/analyze.py \
  sql /absolute/path/to/stats/sts2-v053.parquet \
  'SELECT dataset_label, count(DISTINCT generation_id) AS seeds FROM placements GROUP BY 1'
```

## Extending metrics

The raw schema already includes item classification flags, region, sphere,
reachability, player ownership, game, event/locked status, and dataset label.
Most Slay the Spire 2 metrics should be separate SQL over these rows. For
example, the first relic/progressive-relic sphere per seed is `min(sphere)`
grouped by `generation_id`; Act 1 can be selected from `region`.

If a world needs extra placement metadata (for example, an explicit `act`
column), add an entry to `CUSTOM_COLUMNS` in `placement_stats_hook.py`. Keep
expensive aggregate metrics out of the hook.

## Concurrency and determinism

- Workers never share an open data file. Each successful seed gets a UUID-named
  CSV temporary file followed by an atomic rename. Interrupted writes remain
  invisible to analysis because only `*.csv` is read.
- Sphere calculation and CSV writing happen before the fuzzer cancels its
  per-generation timeout. Allow headroom in `-t`; use `-t 0` only if disabling
  hung-generation protection is acceptable.
- The current fuzzer chooses each generator seed with worker-local
  `random.randint(0, 1_000_000_000)`. The hook records the actual
  `MultiWorld.seed` and `seed_name`, but the fuzzer does not expose a stable run
  index or a built-in replay seed list. Two large experiments can be compared
  as independent samples; exact paired-seed comparisons need a separate seed
  scheduling extension.
- A 0-to-1,000,000,000 seed range can collide at 10,000 runs. Do not use `seed`
  alone as a row/run key; use `generation_id`.
- `get_spheres()` iterates sets, so location order within a sphere is not
  meaningful. The hook sorts output rows only for stable files; it does not
  invent an order among checks in the same sphere.
- Record a distinct `AP_PLACEMENT_STATS_LABEL` for every settings/generator
  version. `archipelago_version` is recorded automatically; an external
  APWorld Git revision is not available through `MultiWorld`, so put it in the
  label or experiment metadata.
