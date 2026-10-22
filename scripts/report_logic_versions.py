#!/usr/bin/env python3
"""Compare the new logic of two old/new logic matrices that share YAMLs and seeds.

Both roots come from progression_stats.py --compare-logic run on different APWorld commits,
with their per-configuration reports and report_logic_matrix.py already generated. Old logic
runs serve as a control: identical old-logic placements show that the seeds, YAMLs and
generator match, so differences in new logic come from the logic itself.

Power uses the candidate's formula for both versions: the base inventories are rescored with
report_power_comparison.py --rules-from, which this script runs when its output is missing.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
import csv
import hashlib
import html
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scripts.report_logic_matrix import RELIC_CHECKS, opening_rows
from scripts.report_progression_stats import paired_interval
from scripts.report_support_diversity import CHECKPOINTS, table, write_csv


def read_csv(path):
    with path.open(newline='') as file:
        return list(csv.DictReader(file))


def number(value):
    return None if value in (None, '', 'None') else float(value)


def connect(path):
    return sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)


def old_logic_fingerprints(path):
    """Per seed, a hash of every old-logic placement and the sphere it was collected in."""
    fingerprints = {}
    with connect(path) as db:
        rows = db.execute("""SELECT seed,location,item,sphere FROM placements WHERE variant='old'
                             ORDER BY seed,location,item,sphere""")
        current, digest = None, None
        for seed, location, item, sphere in rows:
            if seed != current:
                if current is not None:
                    fingerprints[current] = digest.hexdigest()
                current, digest = seed, hashlib.sha256()
            digest.update(f'{location}|{item}|{sphere}\n'.encode())
        if current is not None:
            fingerprints[current] = digest.hexdigest()
    return fingerprints


def sphere_counts(path, variant):
    with connect(path) as db:
        return dict(db.execute("SELECT seed,sphere_count FROM runs WHERE variant=? AND status='success'", (variant,)))


def seed_adjusted_power(path, checkpoint):
    """Per seed, the mean adjusted power of its characters at a checkpoint's first access."""
    with connect(path) as db:
        return dict(db.execute("""SELECT seed,avg(adjusted_power) FROM checkpoint_power
            WHERE variant='current' AND checkpoint=? AND has_power_gate=1 GROUP BY seed""", (checkpoint,)))


def pooled(rows, **keys):
    return {r['checkpoint']: r for r in rows
            if r['character'] == 'all' and r['role'] == 'all' and all(r[k] == v for k, v in keys.items())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('base', type=Path, help='Matrix root of the earlier logic version')
    parser.add_argument('candidate', type=Path, help='Matrix root of the later logic version')
    parser.add_argument('--base-label', default='v2')
    parser.add_argument('--candidate-label', default='v3')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    base_root, candidate_root = args.base.resolve(), args.candidate.resolve()
    base_label, candidate_label = args.base_label, args.candidate_label
    cases = json.loads((candidate_root / 'cases.json').read_text())
    assert cases == json.loads((base_root / 'cases.json').read_text()), 'The matrices have different configurations'

    controls, depth, openings, gates, power, diversity, effects, identities = [], [], [], [], [], [], [], []
    for case in cases:
        name = case['name']
        base, candidate = base_root / name, candidate_root / name
        identity = dict(configuration=name, starters=case['starters'], campfires=case['campfires'])
        manifests = [json.loads((folder / 'manifest.json').read_text()) for folder in (base, candidate)]
        for field in ('yaml_sha256', 'archipelago_commit', 'seed_schedule'):
            assert manifests[0][field] == manifests[1][field], (name, field)
        assert (base / 'seeds.json').read_text() == (candidate / 'seeds.json').read_text(), (name, 'seeds')
        identities.append(dict(configuration=name, base_commit=manifests[0]['current_commit'],
                               candidate_commit=manifests[1]['current_commit'],
                               same_collector=manifests[0]['collector_sha256'] == manifests[1]['collector_sha256']))

        # Control: old logic should not have changed between the versions.
        base_old, candidate_old = (old_logic_fingerprints(folder / 'results.sqlite') for folder in (base, candidate))
        shared = base_old.keys() & candidate_old.keys()
        controls.append(dict(identity, seeds=len(shared), identical_old_logic_seeds=sum(
            base_old[seed] == candidate_old[seed] for seed in shared)))

        old_spheres = sphere_counts(candidate / 'results.sqlite', 'old')
        base_spheres = sphere_counts(base / 'results.sqlite', 'current')
        candidate_spheres = sphere_counts(candidate / 'results.sqlite', 'current')
        interval = paired_interval(base_spheres, candidate_spheres)
        depth.append(dict(identity, old_mean_spheres=sum(old_spheres.values()) / len(old_spheres),
                          base_mean_spheres=sum(base_spheres.values()) / len(base_spheres),
                          candidate_mean_spheres=sum(candidate_spheres.values()) / len(candidate_spheres),
                          change=interval['change'], low=interval['low'], high=interval['high']))

        old_open = {r['target']: r for r in opening_rows(candidate / 'results.sqlite') if r['variant'] == 'old'}
        base_open = {r['target']: r for r in opening_rows(base / 'results.sqlite') if r['variant'] == 'current'}
        candidate_open = {r['target']: r for r in opening_rows(candidate / 'results.sqlite') if r['variant'] == 'current'}
        for kind, targets in (('region', CHECKPOINTS), ('relic check', RELIC_CHECKS)):
            for target in targets:
                o, b, c = old_open[target], base_open[target], candidate_open[target]
                openings.append(dict(identity, kind=kind, target=target,
                    base_region=b.get('regions') or target, candidate_region=c.get('regions') or target,
                    old_spheres_after_unlock=o['spheres_after_unlock'],
                    base_spheres_after_unlock=b['spheres_after_unlock'],
                    candidate_spheres_after_unlock=c['spheres_after_unlock'],
                    change=c['spheres_after_unlock'] - b['spheres_after_unlock'],
                    old_checks_before_percent=o['checks_before_percent'],
                    base_checks_before_percent=b['checks_before_percent'],
                    candidate_checks_before_percent=c['checks_before_percent']))

        summaries = [json.loads((folder / 'report/summary.json').read_text()) for folder in (base, candidate)]
        gate_rows = [{(r['checkpoint'], r['missing_item']): r for r in summary['gates']
                      if r['character'] == 'all' and r['role'] == 'all' and r['variant'] == variant}
                     for summary, variant in ((summaries[1], 'old'), (summaries[0], 'current'), (summaries[1], 'current'))]
        for key in gate_rows[2]:
            gates.append(dict(identity, checkpoint=key[0], missing_item=key[1],
                              old_missing_percent=gate_rows[0][key]['missing_percent'],
                              base_missing_percent=gate_rows[1][key]['missing_percent'],
                              candidate_missing_percent=gate_rows[2][key]['missing_percent']))

        # Diversity of support inventories, from each matrix's combined comparison table.
        base_matrix = [r for r in read_csv(base_root / 'checkpoint_comparison.csv') if r['configuration'] == name]
        candidate_matrix = [r for r in read_csv(candidate_root / 'checkpoint_comparison.csv') if r['configuration'] == name]
        old_div, base_div, candidate_div = (pooled(candidate_matrix, variant='old'), pooled(base_matrix, variant='current'),
                                            pooled(candidate_matrix, variant='current'))
        for checkpoint in CHECKPOINTS:
            diversity.append(dict(identity, checkpoint=checkpoint, **{
                f'{label}_{field}': number(rows[checkpoint][f'effective_{field}'])
                for label, rows in (('old', old_div), ('base', base_div), ('candidate', candidate_div))
                for field in ('progressive_combinations', 'eight_count_combinations')}))

        # Power on the candidate's scale.
        rescored = base / f"power-report-rules-{manifests[1]['current_commit'][:7]}"
        if not (rescored / 'summary.csv').exists():
            subprocess.run([sys.executable, str(Path(__file__).with_name('report_power_comparison.py')),
                            str(base / 'results.sqlite'), '--rules-from', str(candidate), '--output', str(rescored)],
                           check=True, stdout=subprocess.DEVNULL)
        candidate_power = read_csv(candidate / 'power-report/summary.csv')
        old_power, candidate_new_power = pooled(candidate_power, variant='old'), pooled(candidate_power, variant='current')
        base_power = pooled(read_csv(rescored / 'summary.csv'), variant='current')
        for checkpoint in CHECKPOINTS[1:]:
            o, b, c = old_power[checkpoint], base_power[checkpoint], candidate_new_power[checkpoint]
            interval = paired_interval(seed_adjusted_power(rescored / 'power_comparison.sqlite', checkpoint),
                                       seed_adjusted_power(candidate / 'power-report/power_comparison.sqlite', checkpoint))
            power.append(dict(identity, checkpoint=checkpoint, requirement=number(c['base_required']),
                old_adjusted_power=number(o['adjusted_power_mean']), base_adjusted_power=number(b['adjusted_power_mean']),
                candidate_adjusted_power=number(c['adjusted_power_mean']),
                base_p10=number(b['adjusted_power_p10']), base_p90=number(b['adjusted_power_p90']),
                candidate_p10=number(c['adjusted_power_p10']), candidate_p90=number(c['adjusted_power_p90']),
                change=interval['change'], low=interval['low'], high=interval['high'],
                old_passes_percent=number(o['passes_power_rule_percent']),
                base_passes_percent=number(b['passes_power_rule_percent']),
                base_passes_path_percent=number(b['path_passes_power_rule_percent']),
                candidate_passes_path_percent=number(c['path_passes_power_rule_percent'])))

    output = (args.output or candidate_root.parent / f'logic-versions-{base_root.name}-vs-{candidate_root.name}').resolve()
    output.mkdir(parents=True, exist_ok=True)
    datasets = dict(controls=controls, depth=depth, region_opening=openings, former_gates=gates,
                    power_on_candidate_scale=power, support_diversity=diversity)
    for name, rows in datasets.items():
        write_csv(output / f'{name}.csv', rows)

    os.environ.setdefault('MPLCONFIGDIR', str(output / '.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    series = (('old', 'Old logic', '#7a8699'), ('base', f'{base_label} new logic', '#c27c2c'),
              ('candidate', f'{candidate_label} new logic', '#147d92'))
    for filename, rows, field, ylabel, labels in (
            ('opening.png', [r for r in openings if r['kind'] == 'region'], 'spheres_after_unlock',
             'Spheres after the character unlocks', CHECKPOINTS),
            ('power.png', power, 'adjusted_power', f'Support-adjusted power ({candidate_label} formula)', CHECKPOINTS[1:])):
        figure, axes = plt.subplots(2, 2, figsize=(15, 10), layout='constrained')
        for ax, case in zip(axes.flat, cases):
            case_rows = {r['target'] if 'target' in r else r['checkpoint']: r for r in rows if r['configuration'] == case['name']}
            for prefix, label, color in series:
                ax.plot(range(len(labels)), [case_rows[cp][f'{prefix}_{field}'] for cp in labels],
                        marker='o', label=label, color=color)
            if field == 'adjusted_power':
                ax.plot(range(len(labels)), [case_rows[cp]['requirement'] for cp in labels], linestyle='--',
                        color='#a65031', label=f'{candidate_label} base requirement')
            ax.set_title(f"Starters {'on' if case['starters'] else 'off'} / Campfire Sanity {'on' if case['campfires'] else 'off'}")
            ax.set_xticks(range(len(labels)), [cp.replace('Boss Arena', 'boss') for cp in labels], rotation=50, ha='right')
            ax.set_ylabel(ylabel); ax.grid(alpha=.2); ax.legend(fontsize=8)
        figure.savefig(output / filename, dpi=160)
        plt.close(figure)

    def rename(column):
        for prefix, label in (('base_', base_label), ('candidate_', candidate_label)):
            if column.startswith(prefix):
                return f'{label}_{column.removeprefix(prefix)}'
        return column

    def label_columns(rows, columns):
        return [{rename(c): r[c] for c in columns} for r in rows], [rename(c) for c in columns]

    def case_label(case):
        return f"Starters {'on' if case['starters'] else 'off'} / Campfire Sanity {'on' if case['campfires'] else 'off'}"

    def per_case(rows, columns, kind=None):
        body = ''
        for index, case in enumerate(cases):
            selected = [r for r in rows if r['configuration'] == case['name'] and (kind is None or r['kind'] == kind)]
            body += f'<details{" open" if index == 0 else ""}><summary>{case_label(case)}</summary>'
            body += table(*label_columns(selected, columns)) + '</details>'
        return body

    seeds = controls[0]['seeds']
    identical = sum(r['identical_old_logic_seeds'] for r in controls)
    total = sum(r['seeds'] for r in controls)
    title = f'Logic {base_label} and {candidate_label} against old logic'
    body = f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{html.escape(title)}</title>
    <style>body{{font:16px/1.5 system-ui;max-width:1400px;margin:32px auto;padding:0 24px;color:#192a38}}
    table{{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}}
    th,td{{padding:8px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}}th{{background:#eef3f6}}
    .scroll{{overflow:auto;margin:12px 0}}a{{color:#147d92}}details{{margin:12px 0}}summary{{cursor:pointer;font-weight:600}}img{{width:100%}}</style>
    <h1>{html.escape(title)}</h1>
    <p>{html.escape(base_label)} is new logic at <code>{identities[0]['base_commit'][:12]}</code>; {html.escape(candidate_label)} is new
    logic at <code>{identities[0]['candidate_commit'][:12]}</code>. Both matrices use the same four configurations of
    TeraSpire2.yaml (five characters in one slot), the same {seeds:,} seeds and the same generator.
    Every comparison of {html.escape(base_label)} with {html.escape(candidate_label)} below pairs runs of the same seed.</p>
    <p><strong>Control:</strong> {identical:,} of {total:,} old-logic runs are identical between the two matrices
    (same placements and spheres), so differences in new logic come from the logic itself.
    Collector source identical: {'yes' if all(r['same_collector'] for r in identities) else 'no'}.</p>
    <p>Matrix reports: <a href="{html.escape(os.path.relpath(base_root / 'index.html', output))}">old logic and {html.escape(base_label)}</a> ·
    <a href="{html.escape(os.path.relpath(candidate_root / 'index.html', output))}">old logic and {html.escape(candidate_label)}</a></p>
    <h2>Dependency depth</h2><p>Mean spheres per seed. The change is {html.escape(candidate_label)} minus {html.escape(base_label)},
    with a 95% bootstrap interval over seed pairs.</p>'''
    body += table(*label_columns(depth, ['configuration', 'old_mean_spheres', 'base_mean_spheres', 'candidate_mean_spheres',
                                         'change', 'low', 'high']))
    body += f'''<h2>Where regions and relic checks open</h2>
    <p>Spheres after the character's unlock (its Early Act 1 sphere). Lower opens earlier. Relic checks moved from path
    regions in {html.escape(base_label)} to one elite track region per relic in {html.escape(candidate_label)}.</p>
    <img src="opening.png" alt="Spheres after unlock at which each region opens, for old logic and both versions">'''
    opening_columns = ['target', 'base_region', 'candidate_region', 'old_spheres_after_unlock', 'base_spheres_after_unlock',
                       'candidate_spheres_after_unlock', 'change', 'old_checks_before_percent',
                       'base_checks_before_percent', 'candidate_checks_before_percent']
    body += '<h3>Regions</h3>' + per_case(openings, opening_columns, 'region')
    body += '<h3>Relic checks</h3>' + per_case(openings, opening_columns, 'relic check')
    body += f'''<h2>Former hard gates</h2><p>Percentage of characters missing each item when the region first opens.
    Old logic required these items; new logic lets other power make up for them, except for its own item gates.</p>'''
    body += per_case(gates, ['checkpoint', 'missing_item', 'old_missing_percent', 'base_missing_percent',
                             'candidate_missing_percent'])
    body += f'''<h2>Power at first access on the {html.escape(candidate_label)} scale</h2>
    <p>All three inventories are scored with {html.escape(candidate_label)}'s formula, counting the most relics each region
    allows. Adjusted power is power minus the value of missing support; the dashed line is {html.escape(candidate_label)}'s
    base requirement. The change is {html.escape(candidate_label)} minus {html.escape(base_label)} with a 95% interval over seed
    pairs. <em>Passes</em> columns give how often an inventory would meet {html.escape(candidate_label)}'s check;
    <em>path</em> counts only relics claimable while walking around elites.</p>
    <img src="power.png" alt="Support-adjusted power at first access for old logic and both versions">'''
    body += per_case(power, ['checkpoint', 'requirement', 'old_adjusted_power', 'base_adjusted_power',
                             'candidate_adjusted_power', 'change', 'low', 'high', 'old_passes_percent', 'base_passes_percent',
                             'base_passes_path_percent', 'candidate_passes_path_percent'])
    body += '''<h2>Variety of support inventories</h2><p>Effective number of equally common combinations at first access:
    the five progressive counts (Ancients, Rests, Smiths, starter cards and relics), and the eight counts that add card
    rewards, rare card rewards and relics. Higher means more varied item orders.</p>'''
    body += per_case(diversity, ['checkpoint', 'old_progressive_combinations', 'base_progressive_combinations',
                                 'candidate_progressive_combinations', 'old_eight_count_combinations',
                                 'base_eight_count_combinations', 'candidate_eight_count_combinations'])
    body += '''<h2>Limits</h2><ul><li>One fixed five-character YAML; other option sets can behave differently.</li>
    <li>Spheres measure logical depth with every reachable check collected, not play time or difficulty.</li>
    <li>Each seed's five characters are correlated; intervals resample whole seeds.</li></ul><h2>Data</h2><ul>'''
    body += ''.join(f'<li><a href="{name}.csv">{name.replace("_", " ")}</a></li>' for name in datasets) + '</ul></html>'
    (output / 'index.html').write_text(body)
    (output / 'provenance.json').write_text(json.dumps(dict(
        base=str(base_root), candidate=str(candidate_root), identities=identities, controls=controls,
        analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()), indent=2))
    print(f'Report: {output / "index.html"}; old-logic control {identical}/{total} identical')


if __name__ == '__main__':
    main()
