#!/usr/bin/env python3
"""Reproducible, fixed-YAML APWorld comparison using the 0.6.2 fuzzer's call_generate.

See progression_stats.md for semantics, reproduction and SQL examples.
Only the parent writes SQLite; isolated workers retain compressed raw observations.
"""
from __future__ import annotations

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import random
import shutil
import sqlite3
import subprocess
import sys
import time
import traceback
import zipfile

REPO = Path(__file__).resolve().parents[1]
FAMILIES = ('Progressive Ancient', 'Progressive Starter Card', 'Progressive Starter Relic',
            'Progressive Smith', 'Progressive Rest')
CHECKPOINTS = tuple(f'{part} Act {act}' for act in (1, 2, 3) for part in ('Early', 'Mid', 'Late')) + tuple(
    f'Act {act} Boss Arena' for act in (1, 2, 3))
SCHEMA = """
CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS runs(
 variant TEXT, seed INTEGER, status TEXT, wall_seconds REAL, generation_seconds REAL,
 analysis_seconds REAL, starting_character TEXT, sphere_count INTEGER, location_count INTEGER,
 error TEXT, details TEXT, PRIMARY KEY(variant,seed));
CREATE TABLE IF NOT EXISTS placements(
 variant TEXT, seed INTEGER, location TEXT, address INTEGER, region TEXT,
 location_character TEXT, item TEXT, item_code INTEGER, item_character TEXT,
 classification INTEGER, sphere INTEGER, event INTEGER, precollected INTEGER);
CREATE TABLE IF NOT EXISTS spheres(
 variant TEXT, seed INTEGER, sphere INTEGER, checks INTEGER, checks_before INTEGER,
 goal_reached INTEGER, PRIMARY KEY(variant,seed,sphere));
CREATE TABLE IF NOT EXISTS inventory(
 variant TEXT, seed INTEGER, sphere INTEGER, item TEXT, count INTEGER,
 PRIMARY KEY(variant,seed,sphere,item));
CREATE TABLE IF NOT EXISTS checkpoints(
 variant TEXT, seed INTEGER, sphere INTEGER, character TEXT, checkpoint TEXT,
 reachable INTEGER, first_reached INTEGER, ancients INTEGER, starter_cards INTEGER,
 starter_relics INTEGER, smiths INTEGER, rests INTEGER, card_rewards INTEGER,
 rare_cards INTEGER, relics INTEGER, cards REAL, power REAL, required REAL,
 minimum_cards REAL, adjustments TEXT, counterfactuals TEXT,
 PRIMARY KEY(variant,seed,sphere,character,checkpoint));
CREATE INDEX IF NOT EXISTS placements_item ON placements(item,variant,sphere);
CREATE INDEX IF NOT EXISTS checkpoint_first ON checkpoints(first_reached,checkpoint,variant);
CREATE VIEW IF NOT EXISTS first_checkpoints AS SELECT c.*, r.starting_character,
 s.checks_before * 1.0 / r.location_count AS check_fraction_before
 FROM checkpoints c JOIN runs r USING(variant,seed)
 JOIN spheres s USING(variant,seed,sphere) WHERE first_reached=1;
CREATE VIEW IF NOT EXISTS tier_acquisition AS
 SELECT variant,seed,item,item_character,
 row_number() OVER (PARTITION BY variant,seed,item ORDER BY sphere,location) AS tier,
 sphere,region,location,location_character
 FROM placements WHERE (item LIKE '% Progressive Ancient' OR item LIKE '% Progressive Starter Card'
 OR item LIKE '% Progressive Starter Relic' OR item LIKE '% Progressive Smith'
 OR item LIKE '% Progressive Rest') AND event=0 AND sphere IS NOT NULL;
"""


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def packed(value):
    return json.dumps(value, sort_keys=True, default=lambda v: sorted(v) if isinstance(v, set) else str(v))


class CountOverride:
    """Read-only counterfactual for current strength(), never used to mutate reachability."""
    def __init__(self, state, replacements):
        self.state, self.replacements = state, replacements

    def count(self, item, player):
        return self.replacements.get((item, player), self.state.count(item, player))

    def has(self, item, player, count=1):
        return self.count(item, player) >= count


def power_rules(rule, power_type):
    if isinstance(rule, power_type):
        yield rule
    for child in getattr(rule, 'children', ()):
        yield from power_rules(child, power_type)


def power_observation(rule, state, char):
    if rule is None:
        return None, None, None, None, {}, {}
    if not hasattr(rule, 'strength'):
        # Release 1.1.2 tracks power through its LogicMixin; do not retrofit new weights.
        return None, state.power_level[1][rule.char_offset], rule.power_level, None, {}, {}
    cards, power, required = rule.strength(state)
    adjustments = rule.power_adjustments(state)
    counterfactuals = {}
    for name, items in {
        'ordinary_and_rare_cards': ('Card Reward', 'Rare Card Reward'),
        'ordinary_relics': ('Relic',),
        'starter_cards': ('Progressive Starter Card',),
        'starter_relics': ('Progressive Starter Relic',),
        'cards_and_relics': ('Card Reward', 'Rare Card Reward', 'Relic'),
    }.items():
        proxy = CountOverride(state, {(f'{char} {item}', 1): 0 for item in items})
        c, p, r = rule.strength(proxy)
        # Isolate the positive missing-Ancient penalty. A failing ablation alone does
        # not show Ancient substitution: it may also fail the baseline requirement.
        ancient_penalty = max(0, next((v for k, v in adjustments.items() if k.startswith('Ancient support')), 0))
        counterfactuals[name] = dict(cards=c, power=p, required=r,
            passes=c >= rule.minimum_cards and p >= r,
            ancient_specific=c >= rule.minimum_cards and r - ancient_penalty <= p < r)
    return cards, power, required, rule.minimum_cards, adjustments, counterfactuals


def collect(mw):
    from BaseClasses import CollectionState
    from worlds.spire2.rules import SpireHasPower
    world = mw.worlds[1]
    if getattr(world, 'effective_logic', None) == 'old':
        from worlds.spire2.rules_old import LegacySpireHasPower as SpireHasPower
    assert mw.players == 1
    chars = [c.name for c in world.characters]
    state = CollectionState(mw)
    remaining = set(mw.get_filled_locations())
    events = {loc for loc in remaining if loc.address is None or loc.item.code is None}
    remaining -= events
    actual_inventory = Counter(item.name for item in mw.precollected_items[1])
    out = dict(placements=[], spheres=[], inventory=[], checkpoints=[])
    reached = set()
    seen_spheres = []
    starting = [c.name for c in world.characters if not c.locked]
    assert len(starting) == 1, starting

    def character(name):
        return next((c for c in chars if name.startswith(c + ' ')), None)

    def placement(loc, sphere, precollected=False, item=None):
        item = item or loc.item
        out['placements'].append([loc.name if loc else None, loc.address if loc else None,
            loc.parent_region.name if loc else None, character(loc.name) if loc else None,
            item.name, item.code, character(item.name), int(item.classification), sphere,
            int(loc in all_events) if loc else 0, int(precollected)])

    all_events = events.copy()
    for item in mw.precollected_items[1]:
        placement(None, -1, True, item)
    probes = []
    for char in chars:
        for checkpoint in CHECKPOINTS:
            entrance = world.get_entrance(f'{char} {checkpoint}')
            rules = list(power_rules(entrance.access_rule, SpireHasPower.Resolved))
            assert len(rules) <= 1, entrance.name
            probes.append((char, checkpoint, entrance, rules[0] if rules else None))
    checks_before = 0
    sphere = 0
    while True:
        # Same event closure as MultiWorld.get_sendable_spheres(). No AP item in
        # this sphere is collected until all inventory/checkpoint observations finish.
        while True:
            available = sorted((loc for loc in events if loc.can_reach(state)), key=lambda loc: loc.name)
            if not available:
                break
            for loc in available:
                state.collect(loc.item, True, loc)
                actual_inventory[loc.item.name] += 1
                placement(loc, sphere)
            events.difference_update(available)
        batch = sorted((loc for loc in remaining if loc.can_reach(state)), key=lambda loc: loc.name)
        out['spheres'].append([sphere, len(batch), checks_before, int(mw.has_beaten_game(state))])
        out['inventory'].extend([sphere, item, n] for item, n in sorted(actual_inventory.items()) if n)
        for char, checkpoint, entrance, rule in probes:
            accessible = entrance.can_reach(state)
            key = (char, checkpoint)
            first = accessible and key not in reached
            if accessible:
                reached.add(key)
            counts = [state.count(f'{char} {item}', 1) for item in
                      (*FAMILIES[:3], 'Progressive Smith', 'Progressive Rest',
                       'Card Reward', 'Rare Card Reward', 'Relic')]
            cards, power, required, minimum, adjustments, cf = power_observation(rule, state, char)
            out['checkpoints'].append([sphere, char, checkpoint, int(accessible), int(first),
                *counts, cards, power, required, minimum, packed(adjustments), packed(cf)])
        if not batch:
            break
        seen_spheres.append({loc.name for loc in batch})
        for loc in batch:
            placement(loc, sphere)
            state.collect(loc.item, True, loc)
            actual_inventory[loc.item.name] += 1
        remaining.difference_update(batch)
        checks_before += len(batch)
        sphere += 1
    for loc in sorted(remaining | events, key=lambda loc: loc.name):
        placement(loc, None)
    # Independent upstream traversal guards event handling and off-by-one errors.
    expected = [{loc.name for loc in group} for group in mw.get_sendable_spheres()]
    assert not remaining, f'{len(remaining)} unreachable checks with accessibility=full'
    assert expected == seen_spheres, 'Collector spheres disagree with Archipelago'
    assert mw.has_beaten_game(state), 'Goal unreachable'
    assert len(reached) == len(probes), 'Missing checkpoint observations'
    for char in chars:
        for family, expected_count in zip(FAMILIES, (
                2 + int(bool(world.options.neow_sanity)),
                2 * bool(world.options.progressive_starter_card),
                2 * bool(world.options.progressive_starter_relic),
                3 * bool(world.options.campfire_sanity), 3 * bool(world.options.campfire_sanity))):
            assert actual_inventory[f'{char} {family}'] == expected_count, (char, family)
    out.update(starting_character=starting[0], sphere_count=sphere, location_count=checks_before,
               resolved_options={name: getattr(world.options, name).value
                                 for name in world.options_dataclass.type_hints},
               slot_data=world.fill_slot_data())
    return out


def worker(args):
    stage = args.result.parent / 'stage.txt'
    stage.write_text('setup')
    runtime = args.output / 'runtime' / args.variant
    sys.path.insert(0, str(runtime))
    sys.path.insert(0, str(args.output / 'inputs'))
    os.chdir(runtime)
    os.environ['SKIP_REQUIREMENTS_UPDATE'] = 'true'
    import Utils
    user_data = args.result.parent / 'userdata'
    user_data.mkdir(exist_ok=True)
    # get_settings() writes its initial host.yaml even with skip_autosave. Each
    # process needs its own directory to avoid racing on host.yaml.tmp.
    Utils.user_path.cached_path = str(user_data)
    import fuzz
    class SeedHook:
        def before_generate(self, genargs):
            genargs.seed = args.seed
            genargs.weights_file_path = ''
    fuzz.MP_HOOKS = [SeedHook()]
    fuzz.patched_init_logging('ProgressionStats')
    start = time.perf_counter()
    # Keep normal generation assertions and output enabled. The fuzzer ordinarily
    # discards successful output; here each run keeps its generated zip and logs.
    stage.write_text('generation')
    players = args.output / 'players'
    if (players / args.variant).is_dir():
        players /= args.variant
    mw = fuzz.call_generate(str(players), argparse.Namespace(skip_output=False),
                            str(args.output / 'runs' / args.variant / str(args.seed) / 'generated'))
    generation = time.perf_counter() - start
    start = time.perf_counter()
    stage.write_text('analysis')
    result = collect(mw)
    result.update(generation_seconds=generation, analysis_seconds=time.perf_counter() - start)
    with gzip.open(args.result, 'wt', encoding='utf-8') as f:
        json.dump(result, f, default=lambda v: sorted(v) if isinstance(v, set) else str(v))


def prepare(args):
    root = args.output
    root.mkdir(parents=True, exist_ok=True)
    assert not subprocess.check_output(['git', '-C', str(args.archipelago), 'diff', 'HEAD', '--name-only']), 'AP source has tracked edits'
    manifest_path = root / 'manifest.json'
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text())
        assert manifest['collector_sha256'] == digest(__file__), 'Collector changed; use a new output directory'
        assert manifest['yaml_sha256'] == digest(args.yaml), 'YAML changed; use a new output directory'
        assert manifest['fuzzer_sha256'] == digest(args.fuzzer), 'Fuzzer changed'
        assert manifest.get('compare_logic', False) == args.compare_logic, 'Comparison changed'
        assert manifest['baseline_sha256'] == (None if args.compare_logic else digest(args.baseline)), 'Baseline changed'
        assert manifest['archipelago_commit'] == subprocess.check_output(
            ['git', '-C', str(args.archipelago), 'rev-parse', 'HEAD'], text=True).strip(), 'AP revision changed'
        return manifest
    commit = subprocess.check_output(['git', '-C', str(REPO), 'rev-parse', 'HEAD'], text=True).strip()
    assert not subprocess.check_output(['git', '-C', str(REPO), 'status', '--porcelain', '--', 'world/spire2']), 'Commit APWorld first'
    inputs = root / 'inputs'
    inputs.mkdir(exist_ok=True)
    sources = [(args.yaml, 'TeraSpire2.yaml'), (args.fuzzer, 'fuzz.py')]
    if not args.compare_logic:
        sources.append((args.baseline, 'spire2-1.1.2.apworld'))
    for source, name in sources:
        if source.resolve() != (inputs / name).resolve():
            shutil.copy2(source, inputs / name)
    shutil.copy2(__file__, inputs / 'collector.py')
    archive = subprocess.check_output(['git', '-C', str(REPO), 'archive', '--format=zip', commit, 'world/spire2'])
    (inputs / 'current-source.zip').write_bytes(archive)
    players = root / 'players'
    players.mkdir(exist_ok=True)
    variants = ('old', 'current') if args.compare_logic else ('release-1.1.2', 'current')
    if args.compare_logic:
        import yaml
        for variant in variants:
            config = yaml.safe_load(args.yaml.read_text())
            config['Slay the Spire II']['use_new_logic'] = variant == 'current'
            folder = players / variant
            folder.mkdir(exist_ok=True)
            (folder / 'TeraSpire2.yaml').write_text(yaml.safe_dump(config, sort_keys=False))
    else:
        shutil.copy2(args.yaml, players / 'TeraSpire2.yaml')
    for variant in variants:
        runtime = root / 'runtime' / variant
        runtime.mkdir(parents=True)
        for source in args.archipelago.iterdir():
            if source.name.startswith('.') or source.name in ('worlds', 'host.yaml', 'Players', 'output'):
                continue
            (runtime / source.name).symlink_to(source, target_is_directory=source.is_dir())
        worlds = runtime / 'worlds'
        worlds.mkdir()
        for source in (args.archipelago / 'worlds').iterdir():
            if source.name in ('spire2', '__pycache__') or source.name.startswith('.'):
                continue
            if source.is_dir():
                (worlds / source.name).symlink_to(source, target_is_directory=True)
            else:
                shutil.copy2(source, worlds / source.name)
        current_source = variant == 'current' or args.compare_logic
        with zipfile.ZipFile(io.BytesIO(archive) if current_source else args.baseline) as z:
            prefix = 'world/spire2/' if current_source else 'spire2/'
            for name in z.namelist():
                if name.startswith(prefix) and not name.endswith('/'):
                    relative = Path(name[len(prefix):])
                    assert '..' not in relative.parts
                    dest = worlds / 'spire2' / relative
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(z.read(name))
        (root / 'userdata' / variant).mkdir(parents=True)
    manifest = dict(schema_version=1, current_commit=commit, current_source_sha256=hashlib.sha256(archive).hexdigest(),
        yaml_sha256=digest(args.yaml), baseline_sha256=None if args.compare_logic else digest(args.baseline),
        baseline_url=None if args.compare_logic else 'https://github.com/dlueben1/Slay-the-Spire-2-Archipelago/releases/download/1.1.2/spire2.apworld',
        compare_logic=args.compare_logic, variants=variants,
        fuzzer_sha256=digest(args.fuzzer), fuzzer_version='0.6.2', collector_sha256=digest(__file__),
        archipelago_commit=subprocess.check_output(['git', '-C', str(args.archipelago), 'rev-parse', 'HEAD'], text=True).strip(),
        python=sys.version, seed_schedule=20261003,
        sphere_semantics='zero-based sendable spheres; event closure before observations; final empty snapshot retained')
    manifest_path.write_text(packed(manifest) + '\n')
    return manifest


def attempt(args, variant, seed):
    folder = args.output / 'runs' / variant / str(seed)
    folder.mkdir(parents=True, exist_ok=True)
    result_path = folder / 'observations.json.gz'
    start = time.perf_counter()
    status, error, result = 'success', None, {}
    env = {**os.environ, 'PYTHONHASHSEED': '0', 'SKIP_REQUIREMENTS_UPDATE': 'true'}
    with (folder / 'run.log').open('w') as log:
        try:
            subprocess.run([sys.executable, str(Path(__file__).resolve()), 'worker', '--output', str(args.output),
                            '--variant', variant, '--seed', str(seed), '--result', str(result_path)],
                           env=env, stdout=log, stderr=subprocess.STDOUT, timeout=args.timeout, check=True)
            with gzip.open(result_path, 'rt') as f:
                result = json.load(f)
        except subprocess.TimeoutExpired:
            status, error = 'timeout', f'Exceeded {args.timeout}s (generation plus analysis and imports)'
        except Exception:
            stage = folder / 'stage.txt'
            status = (stage.read_text() if stage.exists() else 'setup') + '_failure'
            error = traceback.format_exc()
    return variant, seed, status, time.perf_counter() - start, error, result


def ingest(db, observation):
    variant, seed, status, duration, error, data = observation
    with db:
        db.execute('INSERT INTO runs VALUES (?,?,?,?,?,?,?,?,?,?,?)',
            (variant, seed, status, duration, data.get('generation_seconds'), data.get('analysis_seconds'),
             data.get('starting_character'), data.get('sphere_count'), data.get('location_count'), error,
             packed({k: v for k, v in data.items() if k not in ('placements', 'spheres', 'inventory', 'checkpoints')})))
        for table in ('placements', 'spheres', 'inventory', 'checkpoints'):
            rows = data.get(table, [])
            if rows:
                query = f'INSERT INTO {table} VALUES ({",".join("?" for _ in range(len(rows[0]) + 2))})'
                db.executemany(query, ([variant, seed, *row] for row in rows))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('run', 'worker'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--archipelago', type=Path, default=REPO.parent / 'Archipelago')
    parser.add_argument('--yaml', type=Path)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--compare-logic', action='store_true', help='Compare old/new modes of the same committed APWorld')
    parser.add_argument('--fuzzer', type=Path)
    parser.add_argument('--runs', type=int, default=100)
    parser.add_argument('--jobs', type=int, default=4)
    parser.add_argument('--timeout', type=float, default=300)
    parser.add_argument('--variant')
    parser.add_argument('--seed', type=int)
    parser.add_argument('--result', type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if args.command == 'worker':
        worker(args)
        return
    if not all((args.yaml, args.fuzzer)) or (not args.compare_logic and not args.baseline):
        parser.error('run requires --yaml, --fuzzer, and either --baseline or --compare-logic')
    if args.runs < 1 or args.jobs < 1 or args.timeout <= 0:
        parser.error('runs, jobs and timeout must be positive')
    args.archipelago = args.archipelago.resolve()
    manifest = prepare(args)
    db = sqlite3.connect(args.output / 'results.sqlite')
    db.executescript(SCHEMA)
    with db:
        db.executemany('INSERT OR REPLACE INTO metadata VALUES (?,?)', ((k, packed(v)) for k, v in manifest.items()))
    rng = random.Random(manifest['seed_schedule'])
    seeds = []
    while len(seeds) < args.runs:
        candidate = rng.randrange(1_000_000_000)
        if candidate not in seeds:
            seeds.append(candidate)
    (args.output / 'seeds.json').write_text(packed(seeds) + '\n')
    done = set(db.execute('SELECT variant,seed FROM runs'))
    tasks = [(variant, seed) for seed in seeds for variant in manifest.get('variants', ('release-1.1.2', 'current'))
             if (variant, seed) not in done]
    start = time.perf_counter()
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        futures = [pool.submit(attempt, args, variant, seed) for variant, seed in tasks]
        for index, future in enumerate(as_completed(futures), 1):
            result = future.result()
            ingest(db, result)
            variant, seed, status, seconds, _, _ = result
            print(f'{index}/{len(tasks)} {variant} seed={seed} {status} {seconds:.1f}s', flush=True)
    print(f'Batch wall time: {time.perf_counter() - start:.1f}s; database: {args.output / "results.sqlite"}')
    print(db.execute('SELECT variant,status,count(*) FROM runs GROUP BY variant,status').fetchall())
    db.close()


if __name__ == '__main__':
    main()
