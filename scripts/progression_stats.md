# Fixed-YAML progression comparison

`progression_stats.py` compares the committed APWorld with a supplied release artifact.
It calls the unmodified **Archipelago-fuzzer 0.6.2 `call_generate`** entry point with a
`before_generate` seed hook. It does not use randomized fuzzer options, the index's
extra 100-check world, or the fuzzer CLI's scheduler. The local runner supplies
isolation, timeouts, resumability and durable successful-run data.

## Reproduce

Use the project's Python environment with the sibling Archipelago **0.6.7** source
and its dependencies. Download and retain the exact inputs:

- [fuzz.py at 0.6.2](https://raw.githubusercontent.com/Eijebong/Archipelago-fuzzer/0.6.2/fuzz.py)
- [release 1.1.2 spire2.apworld](https://github.com/dlueben1/Slay-the-Spire-2-Archipelago/releases/download/1.1.2/spire2.apworld)
- The original `TeraSpire2.yaml`.

The release artifact's internal manifest calls itself **1.1.1**. Its URL and SHA-256
identify the baseline; do not substitute an artifact based on that internal label.

```sh
.venv/bin/python scripts/progression_stats.py run \
  --output artifacts/progression-pilot-final \
  --yaml /mnt/c/ProgramData/Archipelago/Players/sts2/TeraSpire2.yaml \
  --baseline artifacts/progression-pilot/inputs/spire2-1.1.2.apworld \
  --fuzzer artifacts/progression-pilot/inputs/fuzz.py \
  --runs 100 --jobs 4 --timeout 300

.venv/bin/python -m pip install matplotlib
.venv/bin/python scripts/report_progression_stats.py \
  artifacts/progression-pilot-final/results.sqlite

.venv/bin/python -m unittest scripts.tests.test_progression_stats
```

The collector itself needs no additional dependency beyond Archipelago. Matplotlib
is for report charts only. Reports use a read-only database connection. The HTML,
PNG charts, CSV tables and summary JSON are standalone local artifacts with no
external services or scripts.

The runner refuses uncommitted APWorld changes. It snapshots the committed world,
preserves the baseline and YAML bytes, and records hashes, generator commit,
collector source, Python version, resolved options and slot data. Workers use
separate processes, immutable world snapshots and per-run user-settings directories.
Core Archipelago modules are linked from the sibling checkout, which must remain
at the recorded revision without tracked edits. Do not modify it during a batch.

Repeating the command resumes missing attempts. A longer seed list has the same
prefix, so `--runs 200` would extend it, but the pilot is deliberately limited to
100 per version. Completed failures are retained and not silently replaced. A
collector, YAML, fuzzer or baseline change requires a new output directory. The
recorded current snapshot is reused when resuming even if the repository's HEAD
subsequently advances. Keep diagnostic/smoke directories separate from the final
pilot; do not pool their repeated seeds as additional samples.

## Scope and sphere semantics

- One AP slot using the fixed YAML. All five characters remain selected. The
  same 100 numeric seeds run against both versions. RNG consumption may differ,
  including the starting character; identical seeds are not identical playthroughs.
- Progressive-item count invariants follow Neow Sanity, both starter options and
  Campfire Sanity. Disabled categories correctly expect zero AP receipts.
- Normal generation assertions, accessibility verification, game output and
  spoiler generation remain enabled. Generated ZIP files and logs are retained.
- **Sphere 0** contains initially available AP checks. Before each sphere, events
  are swept to closure. Inventories and checkpoint access are observed before its
  AP items are collected. Items found in sphere S are available before sphere S+1.
- All available checks are collected each sphere, regardless of character. This
  models logical access, not a preferred character route, combat performance,
  client reward selection or client release-on-victory behavior.
- A final empty snapshot captures the inventory after the last checks. It is not
  included in `sphere_count`. Event-only locations are recorded separately and
  excluded from check fractions. Precollected items have sphere -1.
- Tier acquisition means the earliest sphere containing enough cumulative copies.
  Copies found together share a sphere; alphabetical location order only breaks
  ties in the SQL view, without inventing an acquisition order within that sphere.
- Relative timing is `collection sphere + 1 - character unlock sphere`. It can be
  negative when rewards are found before their character unlocks.
- Every successful run must agree with `MultiWorld.get_sendable_spheres()`, reach
  its goal and all checkpoints, reach all sendable checks, and contain the expected
  number of each requested progressive item.

## Power and counterfactuals

Checkpoint rows contain the native power rule's current strength and requirement.
Current logic records card strength, total card/relic power, minimum card strength,
and the exact support adjustments returned by `power_adjustments`. Baseline power
comes from its original `SpireLogic` state; its numeric scale is not interchangeable
with current power. The old hard item gates are reflected in actual reachability.

For current logic, read-only counterfactuals zero ordinary/rare card receipts,
ordinary relic receipts, starter card receipts, starter relic receipts, or both
ordinary cards and relics. They call the original `strength` calculation without
altering the live state, generating different placements, or using cached rule
results. These probe the local **power rule**, not the whole preceding route.

`passes=false` alone is not proof that a category replaces an Ancient: it could
also be needed to meet the baseline requirement. `ancient_specific=true` is a
stricter test: after removing that category, the minimum card constraint still
passes and remaining power would satisfy the requirement with the positive
missing-Ancient penalty removed, but fails with that penalty present. This is
evidence of substitution within the logic formula, not intent by the fill algorithm.
A false value does not prove no substitution: a whole-category ablation can remove
too much power to isolate the marginal Ancient penalty.

All power observations and ablations are preserved, including before checkpoints
become reachable. Reports highlight only first access. Inventory tables separately
retain actual received items, including items which do not contribute to logic.

## Tables and queries

The report leads with checkpoint availability. `checkpoint_availability.csv`
contains all three Ancient tiers, both starter-card and starter-relic tiers, and
all Smith/Rest tiers at every checkpoint, in act order. It includes pooled,
character, starting/locked, and character-by-role breakdowns. Its three exclusive
categories are already held before first access, found in that checkpoint's
newly reachable sphere, and found later. Same-sphere rewards may be on another
character's checks and cannot contribute to that checkpoint's first access.
Arrival timing is cross-checked against each recorded checkpoint inventory.

`starter_states.csv` splits inventories into no starter, normal starter only,
and upgraded starter. `starter_pairs.csv` shows whether the second starter card,
second starter relic, both, or neither are held. These are logical first-access
inventories, not observed gameplay or item placement regions. Sphere timing
remains available in collapsed supporting sections and the original CSV exports.

`results.sqlite` contains `metadata`, `runs`, `placements`, `spheres`, `inventory`,
`checkpoints`, and the `first_checkpoints` / `tier_acquisition` views. JSON fields
retain resolved options, slot data, native adjustments and ablation results.
`runs.status` distinguishes setup, generation, analysis failures and timeouts.
Logs and compressed raw observations live under `runs/<variant>/<seed>/`.

```sql
-- Was Mid Act 1 reachable before Neow's Ancient arrived?
SELECT variant, character, count(*) AS seeds,
       round(100.0 * sum(ancients=0) / count(*), 1) AS percent_without_neow
FROM first_checkpoints WHERE checkpoint='Mid Act 1'
GROUP BY variant, character;

-- Acquisition distributions; ties remain in the same sphere.
SELECT variant, tier, sphere, count(*) AS observations
FROM tier_acquisition WHERE item LIKE '% Progressive Ancient'
GROUP BY variant, tier, sphere ORDER BY variant, tier, sphere;

-- Actual cards, relics and penalty when entering Act 2 without two Ancients.
SELECT variant, seed, character, sphere, ancients, card_rewards, rare_cards,
       relics, starter_cards, starter_relics, power, required, adjustments
FROM first_checkpoints WHERE checkpoint='Early Act 2' AND ancients<2;

-- A stricter example of relic power specifically covering an Ancient penalty.
SELECT seed,character,checkpoint,sphere,power,required,adjustments,counterfactuals
FROM first_checkpoints WHERE variant='current'
AND json_extract(counterfactuals,'$.ordinary_relics.ancient_specific')=1;

-- Starting versus locked characters: unlocking can explain timing differences.
SELECT variant, item_character=starting_character AS starting, tier,
       avg(t.sphere) AS mean_collection_sphere
FROM tier_acquisition t JOIN runs USING(variant,seed)
WHERE item LIKE '% Progressive Ancient'
GROUP BY variant,starting,tier;

-- Every received item before a specific checkpoint, including filler.
SELECT i.* FROM inventory i JOIN first_checkpoints c USING(variant,seed,sphere)
WHERE c.variant='current' AND c.seed=30241394
AND c.character='Silent' AND c.checkpoint='Mid Act 1';
```

## Interpretation

### Separate support-inventory report

To examine inventory diversity while preserving the original progression report:

```sh
.venv/bin/python scripts/report_support_diversity.py \
  artifacts/progression-pilot-final/results.sqlite \
  --pilot artifacts/progression-pilot-final/pilot-100-seeds/results.sqlite
```

This writes `support-report/`, leaving `report/` intact. Before extending the pilot,
the original 100-seed database, report and metadata were archived under
`pilot-100-seeds/`. The 1,000-seed dataset includes those original seeds once; it
does not count repeated generations as additional observations.

The support signature remains the five counts `(Ancients, Rests, Smiths, starter
cards, starter relics)`. The report provides every tuple's frequency, seed incidence,
example seed/character, distinct counts, top-five concentration, singletons and
effective diversity (`exp(Shannon entropy)`). The latter is the equivalent number
of equally common combinations, a descriptive frequency-weighted statistic rather
than an estimate of every possible inventory or of game balance.

Discovery curves use exact expected richness in uniformly chosen subsets of the
observed seeds. For a tuple occurring in `m` of `N` seeds, its probability of being
seen in a subset of `k` seeds is `1 - C(N-m,k)/C(N,k)`. Summing these probabilities
gives the curve. Whole seeds, including their correlated character observations,
are sampled together. These curves interpolate the sample; they are not forecasts
or confidence intervals. Starting/locked and character breakdowns expose differences
that pooling can hide. All combination frequencies remain available in CSV, while
the interactive HTML explorer shows the top 15 for each selected group.

The broader inventory view keeps that signature, but adds ordinary card rewards,
rare card rewards and ordinary relics within each tuple. `combination_counts.csv`
now includes their means, medians, linearly interpolated 10th/90th percentiles,
minima/maxima and the number of distinct card/relic triples inside each tuple.
These are receipt counts, not actual deck composition or combat strength. Shop
slots/removals, gold, potions and filler are not included in the eight-count view.

`ancient_support_cohorts.csv` gives raw cohort means for missing/present Ancient
tiers. Missing tier N means fewer than N receipts; present means at least N.
`ancient_support_contrasts.csv` compares those cohorts within each version and
checkpoint, stratifying by character and starting status. Only strata containing
both cohorts are included; present-stratum means are weighted by the missing
stratum's observation count. Coverage and seed counts are retained, and absent
comparators are blank rather than zero. Other support items are outcomes, not
matching variables. These descriptive differences do not control for timing,
other Ancient tiers, shop support, or selection at first access, and do not
establish fill intent or causal effects. No independence or significance claim
is made for the correlated character observations.

`ancient_support_evidence.csv` separately counts the saved strict
`ancient_specific` ablations, only among current-world missing-tier cases with a
positive Ancient penalty. Tests overlap; the union is also exported. The penalty
can cover multiple missing tiers. This measures contribution to the native local
power formula, not success of an alternate route or actual combat. A whole-category
ablation can fail the strict test by removing too much power, so its hit rate is
not an estimate of all compensation. HTML checkpoint/character/status/tier filters
expose all three tables without expanding the overview into an eight-dimensional
signature. All of this reuses existing runs; it does not alter the collector.

### Power comparison on a common scale

```sh
.venv/bin/python scripts/report_power_comparison.py \
  artifacts/progression-pilot-final/results.sqlite
```

This produces a separate `power-report/` without changing either existing report
or the source database. It verifies the frozen current Python world against its
archived source, constructs its native regions/rules using the saved resolved
options (without fill/generation), then calls those native power methods on both
versions' full saved inventories at their original first-access checkpoints.
The script requires one fixed current option set. It validates every converted
current checkpoint against the saved cards, power, required power, minimum cards
and adjustment breakdown before publishing the conversion of release inventories.

Raw power includes ordinary/rare cards, relics, starter tiers and starter synergy.
Support-adjusted power is `power - sum(current support adjustments)`, so it can
be compared directly with the current base requirement. Negative adjustments,
such as early higher-tier Ancient rewards in Anytime mode, increase adjusted
power. The margin is `power - required`; the separate minimum-card constraint
still applies. Early Act 1 has no power gate and reports raw power only.

`power-report/power_comparison.sqlite` and `checkpoint_power.csv` retain every
converted observation and exact adjustment JSON, keyed by variant/seed/character/
checkpoint. `summary.csv` includes character and starting-status distributions.
Reported expectations are observed means at first access under each original
world's logic, not translations of a single old numeric threshold. The conversion
does not rerun reachability, change placements, simulate combat, or assert that an
old inventory passing one current power rule could reach it through the current
route. It also does not revalue resources outside the current power model.

```sql
-- Run against power-report/power_comparison.sqlite.
SELECT variant, checkpoint, count(*) AS observations,
       avg(power) AS mean_power, avg(adjusted_power) AS mean_adjusted_power,
       avg(margin) AS mean_margin, 100.0 * avg(passes_power_rule) AS pass_percent
FROM checkpoint_power
WHERE role='starting'
GROUP BY variant, checkpoint;
```

### Approved tuning trial

The first approved tuning pass is frozen at `f42e8e4`. It changes only nine base
requirements and increases an existing hard-gate test's deliberately oversized
synthetic inventory. The original overhaul remains frozen at `89913f7`.

```sh
.venv/bin/python scripts/progression_stats.py run \
  --output artifacts/progression-tuning-pass1 \
  --yaml artifacts/progression-pilot-final/inputs/TeraSpire2.yaml \
  --baseline artifacts/progression-pilot-final/inputs/spire2-1.1.2.apworld \
  --fuzzer artifacts/progression-pilot-final/inputs/fuzz.py \
  --runs 100 --jobs 8 --timeout 300
.venv/bin/python scripts/report_power_comparison.py \
  artifacts/progression-tuning-pass1/results.sqlite
.venv/bin/python scripts/report_tuning_trial.py \
  artifacts/progression-pilot-final artifacts/progression-tuning-pass1
```

`tuning-report/` compares the release, original overhaul and candidate using the
same 100 numeric seeds, restricting the original larger dataset to that subset.
The repeated release controls must exactly reproduce the saved first-access
inventories and spheres; they are never counted as new independent observations.
The report also checks that both frozen current-world power classes are identical,
so power weights and support adjustments remain comparable despite the changed
base requirements. It exports character/status power distributions, missing-tier
rates, and effective diversity for both the five-progressive and eight-count
signatures. Candidate and previous datasets/reports remain separate.

The second user-directed pass is frozen at `0042a84`: Late Act 1 8.5, Mid Act 2
14, Late Act 2 18, Act 2 boss 24, Early Act 3 24, and Mid Act 3 25.5. It uses
`artifacts/progression-tuning-pass2/` with the same 100 seeds and unchanged YAML.
Generate it with the same runner command as above, changing only the output path.
After producing its power report, compare it with the first candidate:

```sh
.venv/bin/python scripts/report_power_comparison.py \
  artifacts/progression-tuning-pass2/results.sqlite
.venv/bin/python scripts/report_tuning_trial.py \
  artifacts/progression-tuning-pass1 artifacts/progression-tuning-pass2
```

The tuning report also exports `gap_scorecard.csv`: equally weighted mean absolute
percentage deviation, signed mean deviation, worst checkpoint deviation, and
number of checkpoints outside 15%, all relative to the release. Early Act 1 is
excluded because it has no power gate. The rough goal is 10% mean absolute deviation
and no individual checkpoint beyond 15%, using pooled character observations;
starting/locked breakdowns are shown separately without silently becoming extra
acceptance criteria. Above-release gaps cannot cancel below-release gaps.

### Latest full report refresh

The third approved snapshot is `1d376e1`: Mid Act 1 4, Late Act 1 10, Act 2 boss
25, Early Act 3 23.5, Late Act 3 26, and Act 3 boss 30. Other requirements retain
the second-pass values. The sparse-opening guarantee shares the Mid Act 1 budget
so configurations without floor checks still receive enough opening support.

The latest direction is to protect the rough opening while aiming for roughly
12–13% less support than release 1.1.2 later in the run. Historical 10% average /
15% checkpoint figures remain useful diagnostics, not a universal acceptance rule.
Starting-character observations remain separate from pooled observations.

```sh
.venv/bin/python scripts/progression_stats.py run \
  --output artifacts/progression-tuning-pass3 \
  --yaml artifacts/progression-pilot-final/inputs/TeraSpire2.yaml \
  --baseline artifacts/progression-pilot-final/inputs/spire2-1.1.2.apworld \
  --fuzzer artifacts/progression-pilot-final/inputs/fuzz.py \
  --runs 1000 --jobs 8 --timeout 300
.venv/bin/python scripts/report_progression_stats.py \
  artifacts/progression-tuning-pass3/results.sqlite
.venv/bin/python scripts/report_support_diversity.py \
  artifacts/progression-tuning-pass3/results.sqlite
.venv/bin/python scripts/report_power_comparison.py \
  artifacts/progression-tuning-pass3/results.sqlite
.venv/bin/python scripts/report_tuning_trial.py \
  artifacts/progression-tuning-pass2 artifacts/progression-tuning-pass3
```

The first three reports use all 1,000 seeds per version. The tuning report uses
the intersection of recorded seed lists: the 100 seeds shared with pass 2.
The report generators never overwrite earlier experiment folders. Sample labels
in the progression report are derived from the database rather than a fixed
pilot size. Every regenerated report records its snapshot and analysis provenance.

### Limits of the measurements

Depth, timing and variation are separate measurements. A later average does not
mean a wider distribution, and fewer logical spheres does not establish shorter
gameplay. Report absolute spheres, check fractions, relative-to-unlock timing and
character/starting-role splits together. Check fractions measure cumulative checks
before a sphere, not elapsed time. Pooled variation includes differences between
characters and unlock positions.

The original progression report has 100 independent seeds per version, not 500
independent characters. Its bootstrap intervals resample complete seed pairs,
keeping their five characters together. The separate expanded support report has
1,000 seeds per version and uses descriptive estimates without confidence intervals.
Missing-item percentages are descriptive character observations;
checkpoint observations within a character are also correlated. The report does
not claim randomized-option fuzz reliability or a general measure of randomness.


### Accepted build refresh and sharing

The accepted APWorld snapshot is `09a04a7`. Its Python files match the tested
`dist/sparse-fallback-v2/spire2.apworld`. It includes the new/old selector, the
floor/gold/potion-disabled fallback regardless of shops, and the legacy
half-shuffle opening mix. It has no natural-resource power allowances or stage
fill sorting. All these sparse-setting changes are inactive for the preserved
five-character YAML: every current run resolves to new logic.

The refreshed dataset is `artifacts/progression-accepted-build/`, with 1,000 fresh
seeds per version using the same seed schedule and byte-identical YAML, fuzzer
and release baseline as pass 3. Generate its reports with:

```sh
.venv/bin/python scripts/report_progression_stats.py artifacts/progression-accepted-build/results.sqlite
.venv/bin/python scripts/report_support_diversity.py artifacts/progression-accepted-build/results.sqlite
.venv/bin/python scripts/report_power_comparison.py artifacts/progression-accepted-build/results.sqlite
.venv/bin/python scripts/report_tuning_trial.py artifacts/progression-tuning-pass3 artifacts/progression-accepted-build
```

The tuning comparison uses all 1,000 shared seeds. If the power class was
refactored, it rescales every previous first-access inventory with the candidate
formula and verifies identical card power, raw power, support adjustments,
minimum-card requirements and adjusted power before allowing the comparison.
A changed scoring scale still fails this check. Base threshold changes are
permitted because they are the subject of a tuning comparison.

Share `dist/spire2-statistics-09a04a7-reports.zip` for reading: extract it and open
`index.html`, keeping the adjacent report folders. HTML filters, charts and CSV
exports work locally without a server. For custom queries, also share
`dist/spire2-statistics-09a04a7-data.zip`, which contains the full SQLite dataset,
converted power database, seeds, frozen inputs, APWorld/source snapshots and
analysis scripts. The raw generation logs and generated multiworld ZIPs remain
in the local experiment directory; they are not required to read or query the
shared results.

The scope is one fixed YAML and 1,000 independent seeds per version, with five
correlated character observations per checkpoint per seed. Reported power gaps
are properties of the model, not measured changes in gameplay difficulty. These
reports do not measure randomized-option fuzz failure rates or the sparse-setting
fallback, which needs its separate fuzz evidence.

## Old/new logic × starter/campfire matrix (2026-10-05)

The latest experiment is `artifacts/logic-matrix-7196038/`. It compares **old and
new logic in the same committed APWorld**, using `use_new_logic`, rather than
comparing against the historical 1.1.2 binary. The logic change is `7196038`;
the frozen world snapshot is the statistics-branch merge `393382f`.

Four configurations cross both progressive starter options together (on/off)
with Campfire Sanity (on/off). Each uses the same 1,000 numeric seeds in both
modes: 8,000 generation attempts, with five correlated character observations
per checkpoint in each run. There are 1,000 shared seed clusters, not 8,000
independent seed draws. The original five-character YAML is retained at the
matrix root. Each case's `input.yaml` changes only those switches and removes
the ignored `logic_difficulty` key. `players/old/` and `players/current/` preserve
the exact effective YAMLs with `use_new_logic` false and true respectively.

`--compare-logic` uses the same immutable source for both modes, selects the
correct native power class for observations, and checks option-dependent item
counts. The release-baseline workflow remains supported without that flag.

Resume or reproduce each saved configuration with:

```sh
for case in starters-1-campfires-1 starters-1-campfires-0 starters-0-campfires-1 starters-0-campfires-0; do
  folder="artifacts/logic-matrix-7196038/$case"
  .venv/bin/python scripts/progression_stats.py run --compare-logic \
    --output "$folder" --yaml "$folder/input.yaml" \
    --fuzzer artifacts/progression-accepted-build/inputs/fuzz.py \
    --runs 1000 --jobs 4 --timeout 300
  .venv/bin/python scripts/report_progression_stats.py "$folder/results.sqlite"
  .venv/bin/python scripts/report_support_diversity.py "$folder/results.sqlite"
  .venv/bin/python scripts/report_power_comparison.py "$folder/results.sqlite"
done
.venv/bin/python scripts/report_logic_matrix.py artifacts/logic-matrix-7196038 --combine-database
```

The four collectors can run concurrently; the accepted batch used four workers
per case (16 total). Reports must run after generation finishes. The combined
report refuses incomplete batches or stale progression summaries. To start a
new experiment, use a new root, copy its input YAMLs and `cases.json`, and record
that root's new frozen commit. Do not resume a dataset with a modified collector.

`index.html` links the twelve detailed reports and provides side-by-side power,
Ancient-tier and diversity comparisons, including character and starting/locked
filters. The five-count support signature is retained and an eight-count
signature adds ordinary cards, rare cards and ordinary relics. Disabled families
are omitted from missing-item/timing tables. Their recorded zero receipts mean
natural starter/campfire access remains, not that the character lacks it.

Power comparisons rescore both modes' first-access inventories with the exact
new formula for the configuration. Missing starter penalties are included in
adjusted power. Paired power intervals bootstrap per-seed pooled-character means;
character-specific and starting/locked distributions are descriptive. Disabling
Campfire Sanity also changes locations and the item pool, so differences across
configurations include those effects. No runs simulate combat or player choices.

The root `results.sqlite` contains all raw observation tables, the
`first_checkpoints` and `tier_acquisition` views, and `converted_power`. Every
identity includes `configuration`, then the original variant/seed keys. Join on
all three to avoid mixing identical seed numbers from different configurations.
The original per-case databases and logs remain in their experiment folders.
The report CSVs now use `baseline_*` for comparison columns; this means old logic
in this matrix and release 1.1.2 when the release-baseline workflow is used.

```sql
SELECT configuration, variant, checkpoint,
       avg(adjusted_power) AS mean_adjusted_power
FROM converted_power WHERE role='starting'
GROUP BY configuration, variant, checkpoint;

SELECT configuration, variant, character,
       100.0 * avg(ancients < 2) AS percent_without_second_ancient
FROM first_checkpoints WHERE checkpoint='Act 3 Boss Arena'
GROUP BY configuration, variant, character;
```

Validation:

```sh
.venv/bin/python -m unittest scripts.tests.test_progression_stats \
  scripts.tests.test_report_support_diversity scripts.tests.test_report_power_comparison \
  scripts.tests.test_report_tuning_trial scripts.tests.test_report_logic_matrix
```
