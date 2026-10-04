#!/usr/bin/env python3
"""Read-only analysis of progression_stats SQLite data; no regeneration required."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import csv
import html
import hashlib
import json
import os
from pathlib import Path
import random
import sqlite3
import statistics as stats

if __package__:
    from .progression_stats import CHECKPOINTS, FAMILIES
else:
    from progression_stats import CHECKPOINTS, FAMILIES

VARIANTS = ('release-1.1.2', 'current')
CHECKPOINT_ORDER = tuple(checkpoint for act in (1, 2, 3) for checkpoint in
                        (f'Early Act {act}', f'Mid Act {act}', f'Late Act {act}', f'Act {act} Boss Arena'))
TIER_FIELDS = (('Progressive Ancient', 'ancients', 3),
               ('Progressive Starter Card', 'starter_cards', 2),
               ('Progressive Starter Relic', 'starter_relics', 2),
               ('Progressive Smith', 'smiths', 3), ('Progressive Rest', 'rests', 3))


def checkpoint_availability(checkpoints, acquisitions):
    """Compare acquisition with first checkpoint access, retaining same-sphere ties."""
    arrivals = {(r['variant'], r['seed'], r['item_character'], r['family'], r['tier']): r['sphere']
                for r in acquisitions}
    groups = defaultdict(Counter)
    for row in checkpoints:
        for family, field, tiers in TIER_FIELDS:
            for tier in range(1, tiers + 1):
                arrival = arrivals[(row['variant'], row['seed'], row['character'], family, tier)]
                held = row[field] >= tier
                assert held == (arrival < row['sphere']), (row, family, tier, arrival)
                status = 'held' if held else 'same_sphere' if arrival == row['sphere'] else 'later'
                for character, role in (('all', 'all'), ('all', row['role']),
                                        (row['character'], 'all'), (row['character'], row['role'])):
                    groups[(row['variant'], row['checkpoint'], family, tier, character, role)][status] += 1
    results = []
    for key, counts in sorted(groups.items()):
        n = sum(counts.values())
        results.append(dict(zip(('variant', 'checkpoint', 'family', 'tier', 'character', 'role'), key),
            observations=n, **{status + '_count': counts[status] for status in ('held', 'same_sphere', 'later')},
            **{status + '_percent': 100 * counts[status] / n for status in ('held', 'same_sphere', 'later')}))
    return results


def checkpoint_matrix(availability, family, role='all'):
    rows = [r for r in availability if r['family'] == family and r['character'] == 'all' and r['role'] == role]
    lookup = {(r['variant'], r['checkpoint'], r['tier']): r for r in rows}
    tiers = next(tiers for f, _, tiers in TIER_FIELDS if f == family)
    result = []
    for checkpoint in CHECKPOINT_ORDER:
        row = {'Checkpoint': checkpoint}
        for tier in range(1, tiers + 1):
            old, new = (lookup[(v, checkpoint, tier)]['held_percent'] for v in VARIANTS)
            label = 'Neow / tier 1' if family == 'Progressive Ancient' and tier == 1 else f'Tier {tier}'
            row[label + ' — already held'] = f'{old:.1f}% → {new:.1f}%'
        result.append(row)
    return table(result)


def percentile(values, fraction):
    values = sorted(values)
    index = (len(values) - 1) * fraction
    low = int(index)
    return values[low] + (values[min(low + 1, len(values) - 1)] - values[low]) * (index - low)


def describe(values):
    return dict(n=len(values), mean=stats.mean(values), sd=stats.pstdev(values),
                p10=percentile(values, .1), median=stats.median(values), p90=percentile(values, .9))


def csv_file(path, rows):
    if rows:
        with path.open('w', newline='') as f:
            writer = csv.DictWriter(f, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)


def paired_interval(old, new):
    """Bootstrap whole seed pairs: five characters in one seed are not independent."""
    seeds = sorted(old.keys() & new.keys())
    changes = [new[seed] - old[seed] for seed in seeds]
    if not changes:
        return dict(seed_pairs=0, change=None, low=None, high=None)
    rng = random.Random(42)
    means = [stats.mean(rng.choices(changes, k=len(changes))) for _ in range(2000)]
    return dict(seed_pairs=len(seeds), change=stats.mean(changes),
                low=percentile(means, .025), high=percentile(means, .975))


def table(rows, columns=None):
    if not rows:
        return '<p>No observations.</p>'
    columns = columns or list(rows[0])
    def cell(value):
        return html.escape(f'{value:.2f}' if isinstance(value, float) else str(value))
    return ('<div class="scroll"><table><thead><tr>' + ''.join(f'<th>{html.escape(c)}</th>' for c in columns)
            + '</tr></thead><tbody>' + ''.join('<tr>' + ''.join(f'<td>{cell(r[c])}</td>' for c in columns)
            + '</tr>' for r in rows) + '</tbody></table></div>')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('database', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    output = args.output or args.database.parent / 'report'
    output.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(args.database.resolve().as_uri() + '?mode=ro', uri=True)
    db.row_factory = sqlite3.Row
    metadata = {r['key']: json.loads(r['value']) for r in db.execute('SELECT key,value FROM metadata')}
    runs = [dict(r) for r in db.execute('SELECT * FROM runs')]
    successful = [r for r in runs if r['status'] == 'success']
    checkpoints = [dict(r) for r in db.execute('SELECT * FROM first_checkpoints')]
    acquisitions = [dict(r) for r in db.execute('''
        SELECT t.*, r.starting_character, r.sphere_count,
         s.checks_before * 1.0 / r.location_count AS check_fraction_before,
         u.sphere AS unlock_sphere
        FROM tier_acquisition t JOIN runs r USING(variant,seed)
        JOIN spheres s USING(variant,seed,sphere)
        JOIN first_checkpoints u ON u.variant=t.variant AND u.seed=t.seed
         AND u.character=t.item_character AND u.checkpoint='Early Act 1'
    ''')]
    for row in acquisitions:
        row['family'] = row['item'][len(row['item_character']) + 1:]
        row['role'] = 'starting' if row['item_character'] == row['starting_character'] else 'locked'
        row['sphere_fraction'] = row['sphere'] / max(1, row['sphere_count'] - 1)
        row['relative_to_unlock'] = row['sphere'] + 1 - row['unlock_sphere']
    for row in checkpoints:
        row['role'] = 'starting' if row['character'] == row['starting_character'] else 'locked'
    availability = checkpoint_availability(checkpoints, acquisitions)
    availability_lookup = {(r['variant'], r['checkpoint'], r['family'], r['tier'], r['character'], r['role']): r
                           for r in availability}
    starter_states = []
    for row in availability:
        if row['tier'] != 2 or row['family'] not in ('Progressive Starter Card', 'Progressive Starter Relic'):
            continue
        first = availability_lookup[(row['variant'], row['checkpoint'], row['family'], 1, row['character'], row['role'])]
        counts = (row['observations'] - first['held_count'], first['held_count'] - row['held_count'], row['held_count'])
        starter_states.append({**{k: row[k] for k in ('variant','checkpoint','family','character','role','observations')},
            **{label + '_percent': 100 * count / row['observations']
               for label, count in zip(('no_starter', 'base_only', 'upgraded'), counts)}})
    pair_groups = defaultdict(Counter)
    for row in checkpoints:
        card, relic = row['starter_cards'] >= 2, row['starter_relics'] >= 2
        status = 'both' if card and relic else 'card_only' if card else 'relic_only' if relic else 'neither'
        for character, role in (('all','all'), ('all',row['role']), (row['character'],'all'), (row['character'],row['role'])):
            pair_groups[(row['variant'],row['checkpoint'],character,role)][status] += 1
    starter_pairs = [dict(zip(('variant','checkpoint','character','role'), key), observations=sum(counts.values()),
        **{status + '_percent': 100 * counts[status] / sum(counts.values())
           for status in ('neither','card_only','relic_only','both')}) for key,counts in sorted(pair_groups.items())]
    run_summary = []
    for variant in VARIANTS:
        rows = [r for r in successful if r['variant'] == variant]
        if not rows:
            raise SystemExit(f'No successful data for {variant}')
        run_summary.append(dict(variant=variant, successful=len(rows), attempted=sum(r['variant'] == variant for r in runs),
            mean_spheres=stats.mean(r['sphere_count'] for r in rows), sd_spheres=stats.pstdev(r['sphere_count'] for r in rows),
            min_spheres=min(r['sphere_count'] for r in rows), max_spheres=max(r['sphere_count'] for r in rows),
            mean_generation_seconds=stats.mean(r['generation_seconds'] for r in rows),
            mean_analysis_seconds=stats.mean(r['analysis_seconds'] for r in rows)))
    timing_groups = defaultdict(list)
    for row in acquisitions:
        # Pooled and fully stratified data are both exported. Pooled SD includes
        # character and unlock-position differences; do not call it pure randomness.
        for character, role in (('all', 'all'), (row['item_character'], row['role'])):
            for metric in ('sphere', 'sphere_fraction', 'check_fraction_before', 'relative_to_unlock'):
                timing_groups[(row['variant'], row['family'], row['tier'], character, role, metric)].append(row[metric])
    timing = [dict(zip(('variant', 'family', 'tier', 'character', 'role', 'metric'), key), **describe(values))
              for key, values in sorted(timing_groups.items())]
    role_groups = defaultdict(list)
    for row in acquisitions:
        for metric in ('sphere', 'relative_to_unlock', 'check_fraction_before'):
            role_groups[(row['variant'], row['family'], row['tier'], row['role'], metric)].append(row[metric])
    role_timing = [dict(zip(('variant', 'family', 'tier', 'role', 'metric'), key), **describe(values))
                   for key, values in sorted(role_groups.items())]
    gates = []
    definitions = [('Mid Act 1', 'Neow Ancient', 'ancients', 1),
        ('Early Act 2', 'second Ancient', 'ancients', 2), ('Early Act 3', 'third Ancient', 'ancients', 3),
        ('Late Act 1', 'first starter card', 'starter_cards', 1),
        ('Late Act 1', 'first starter relic', 'starter_relics', 1),
        *[(f'Mid Act {act}', f'Rest tier {act}', 'rests', act) for act in (1, 2, 3)],
        *[(f'Act {act} Boss Arena', f'Smith tier {act}', 'smiths', act) for act in (1, 2, 3)]]
    for variant in VARIANTS:
        for checkpoint, label, field, threshold in definitions:
            for character, role in [('all', 'all')] + sorted({(r['character'], r['role']) for r in checkpoints}):
                rows = [r for r in checkpoints if r['variant'] == variant and r['checkpoint'] == checkpoint
                        and (character == 'all' or (r['character'], r['role']) == (character, role))]
                if rows:
                    missing = [r for r in rows if r[field] < threshold]
                    gates.append(dict(variant=variant, checkpoint=checkpoint, missing_item=label, character=character,
                        role=role, observations=len(rows), missing_count=len(missing),
                        missing_percent=100 * len(missing) / len(rows)))
    support = []
    diversity = []
    for checkpoint in CHECKPOINTS:
        for variant in VARIANTS:
            rows = [r for r in checkpoints if r['variant'] == variant and r['checkpoint'] == checkpoint]
            combinations = Counter((r['ancients'], r['rests'], r['smiths'], r['starter_cards'], r['starter_relics']) for r in rows)
            if rows:
                combination, frequency = combinations.most_common(1)[0]
                diversity.append(dict(variant=variant, checkpoint=checkpoint, observations=len(rows),
                    distinct_support_combinations=len(combinations),
                    most_common_combination=json.dumps(combination), most_common_percent=100 * frequency / len(rows)))
            for missing in (False, True):
                subset = [r for r in rows if (r['ancients'] < int(checkpoint.split('Act ')[1][0])) == missing]
                if subset:
                    signatures = {(r['ancients'], r['rests'], r['smiths'], r['starter_cards'], r['starter_relics']) for r in subset}
                    support.append(dict(variant=variant, checkpoint=checkpoint, fewer_ancients_than_act=missing,
                        n=len(subset), mean_card_rewards=stats.mean(r['card_rewards'] for r in subset),
                        mean_rare_cards=stats.mean(r['rare_cards'] for r in subset),
                        mean_relics=stats.mean(r['relics'] for r in subset),
                        mean_starter_cards=stats.mean(r['starter_cards'] for r in subset),
                        mean_starter_relics=stats.mean(r['starter_relics'] for r in subset),
                        support_combinations=len(signatures)))
    counterfactual_rows = []
    for r in checkpoints:
        adjustments = json.loads(r['adjustments'])
        penalty = max(0, next((v for k, v in adjustments.items() if k.startswith('Ancient support')), 0))
        if penalty > 0:
            cf = json.loads(r['counterfactuals'])
            counterfactual_rows.append(dict(variant=r['variant'], seed=r['seed'], character=r['character'],
                role=r['role'], checkpoint=r['checkpoint'], sphere=r['sphere'], ancients=r['ancients'],
                penalty=penalty, cards=r['cards'], power=r['power'], required=r['required'],
                spare_power=r['power']-r['required'],
                **{name + '_necessary': not values['passes'] for name, values in cf.items()},
                **{name + '_ancient_specific': values['ancient_specific'] for name, values in cf.items()}))
    effects = []
    old, new = [{r['seed']: r['sphere_count'] for r in successful if r['variant'] == v} for v in VARIANTS]
    effects.append(dict(metric='total sphere count', **paired_interval(old, new)))
    for family in FAMILIES:
        for tier in range(1, 4 if family in ('Progressive Ancient', 'Progressive Smith', 'Progressive Rest') else 3):
            values = defaultdict(list)
            for r in acquisitions:
                if r['family'] == family and r['tier'] == tier:
                    values[(r['variant'], r['seed'])].append(r['sphere'])
            old, new = [{seed: stats.mean(x) for (v, seed), x in values.items() if v == variant} for variant in VARIANTS]
            effects.append(dict(metric=f'{family} {tier}: mean collection sphere', **paired_interval(old, new)))
    datasets = dict(checkpoint_availability=availability, starter_states=starter_states, starter_pairs=starter_pairs,
                    runs=run_summary, acquisitions=acquisitions, timing=timing, role_timing=role_timing, gates=gates, diversity=diversity,
                    support=support, counterfactuals=counterfactual_rows, effects=effects, checkpoints=checkpoints)
    for name, rows in datasets.items():
        csv_file(output / f'{name}.csv', rows)
    summary = {k: v for k, v in datasets.items() if k not in ('acquisitions', 'checkpoints', 'counterfactuals')}
    summary['counterfactual_observations'] = len(counterfactual_rows)
    summary['any_ancient_specific_ablation'] = sum(any(value for key, value in row.items() if key.endswith('_ancient_specific'))
                                                for row in counterfactual_rows)
    (output / 'summary.json').write_text(json.dumps(summary, indent=2))

    os.environ.setdefault('MPLCONFIGDIR', str(output.resolve() / '.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    colors = ('#7a8699', '#147d92')
    labels = ('Release 1.1.2', 'Current branch')
    fig, axes = plt.subplots(2, 2, figsize=(14, 10), layout='constrained')
    for column, family in enumerate(('Progressive Starter Card', 'Progressive Starter Relic')):
        for index, (variant, label) in enumerate(zip(VARIANTS, labels)):
            ax = axes[index, column]
            rows = [availability_lookup[(variant, cp, family, 2, 'all', 'all')] for cp in CHECKPOINT_ORDER]
            left = [0.] * len(rows)
            for status, color, description in (
                ('held', '#147d92', 'Already held'), ('same_sphere', '#dfac43', 'Found in checkpoint sphere'),
                ('later', '#dfe5eb', 'Arrives later')):
                values = [r[status + '_percent'] for r in rows]
                ax.barh(range(len(rows)), values, left=left, color=color, label=description)
                left = [a + b for a,b in zip(left,values)]
            for i,r in enumerate(rows):
                ax.text(r['held_percent'] + .8, i, f"{r['held_percent']:.1f}%", va='center', fontsize=8)
            ax.set_yticks(range(len(rows)), [cp.replace('Boss Arena','boss') for cp in CHECKPOINT_ORDER])
            ax.invert_yaxis()
            ax.set_xlim(0, 108)
            ax.set_xticks((0,25,50,75,100))
            ax.set_xlabel('Percent of character observations')
            ax.set_title(f'{family.removeprefix("Progressive ")} tier 2 · {label}')
    fig.legend(*axes[0,0].get_legend_handles_labels(), loc='outside upper center', ncols=3,
               title='Labels show the percentage already held at logical first access')
    fig.savefig(output / 'second-starter-checkpoints.png', dpi=160)
    plt.close(fig)
    fig, axes = plt.subplots(2, 3, figsize=(14, 8), layout='constrained')
    for tier in (1, 2, 3):
        for variant, color, label in zip(VARIANTS, colors, labels):
            rows = [r for r in acquisitions if r['family'] == 'Progressive Ancient' and r['tier'] == tier and r['variant'] == variant]
            for ax, metric in ((axes[0, tier-1], 'sphere'), (axes[1, tier-1], 'check_fraction_before')):
                values = sorted(r[metric] for r in rows)
                ax.step(values, [(i+1)/len(values) for i in range(len(values))], where='post', color=color, label=label, linewidth=2)
                ax.grid(alpha=.2)
                ax.set_ylim(0, 1.02)
            axes[0, tier-1].set_title(f'Ancient tier {tier}')
            axes[0, tier-1].set_xlabel('Collection sphere (zero-based)')
            axes[1, tier-1].set_xlabel('Fraction of checks collected before sphere')
    axes[0, 0].set_ylabel('Fraction of character observations')
    axes[1, 0].set_ylabel('Fraction of character observations')
    axes[0, 0].legend()
    fig.suptitle('When do Ancients arrive?\nPooled across all five characters; each curve is a cumulative distribution')
    fig.savefig(output / 'ancient-timing.png', dpi=160)
    plt.close(fig)

    fig, axes = plt.subplots(1, 2, figsize=(14, 6), layout='constrained')
    selections = [('Progressive Ancient', 1), ('Progressive Ancient', 2), ('Progressive Ancient', 3),
                  ('Progressive Starter Card', 1), ('Progressive Starter Card', 2),
                  ('Progressive Starter Relic', 1), ('Progressive Starter Relic', 2),
                  ('Progressive Smith', 1), ('Progressive Smith', 2), ('Progressive Smith', 3),
                  ('Progressive Rest', 1), ('Progressive Rest', 2), ('Progressive Rest', 3)]
    for ax, metric, title in zip(axes, ('sphere', 'relative_to_unlock'),
                               ('Absolute collection sphere', 'Available relative to character unlock')):
        for i, (variant, color, label) in enumerate(zip(VARIANTS, colors, labels)):
            values = [[r[metric] for r in acquisitions if (r['family'], r['tier'], r['variant']) == (f, t, variant)] for f, t in selections]
            positions = [j + (i-.5)*.3 for j in range(len(selections))]
            ax.boxplot(values, positions=positions, widths=.25, orientation='horizontal', patch_artist=True,
                       boxprops=dict(facecolor=color, alpha=.65), medianprops=dict(color='black'),
                       showfliers=False, whis=(10,90))
            ax.plot([], [], color=color, linewidth=6, label=label)
        ax.set_yticks(range(len(selections)), [f'{f.removeprefix("Progressive ")} {t}' for f,t in selections])
        ax.invert_yaxis()
        ax.set_title(title)
        ax.set_xlabel('Sphere; boxes = middle 50%, whiskers = 10th–90th percentile')
        ax.grid(axis='x', alpha=.2)
    fig.legend(*axes[0].get_legend_handles_labels(), loc='outside upper center', ncols=2)
    fig.savefig(output / 'all-item-timing.png', dpi=160)
    plt.close(fig)

    pooled_gates = [r for r in gates if r['character'] == 'all']
    pooled_timing = [r for r in timing if r['character'] == 'all' and r['metric'] == 'sphere']
    body = '''<!doctype html><html lang="en"><meta charset="utf-8"><title>Spire II progression report</title>
    <style>body{font:16px/1.5 system-ui;max-width:1250px;margin:40px auto;padding:0 24px;color:#192a38}
    h1,h2{line-height:1.2}table{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}
    th,td{padding:7px 10px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}th{background:#eef3f6}
    .scroll{overflow:auto;margin:20px 0}img{width:100%;height:auto}a{color:#147d92}details{margin:24px 0}
    </style><h1>Slay the Spire II: progression report</h1>
    <p>One AP slot, all five characters, original TeraSpire2.yaml. The baseline is the exact asset from release 1.1.2;
    its embedded manifest reports 1.1.1. The current APWorld is frozen at the commit in manifest.json.</p>
    <p>This measures logical availability, not play time or combat success. All available checks are collected each
    sphere; character selection, client release-on-victory and in-game reward choices are not simulated.</p>
    <details><summary>Sphere depth, runtime and Ancient timing (supporting data)</summary><h2>Depth and runtime</h2>'''
    old_run, new_run = run_summary
    body += f'<p>Current snapshot: <code>{html.escape(metadata["current_commit"][:12])}</code>.</p>'
    body += (f'<p>The current branch averages <strong>{new_run["mean_spheres"]:.2f} spheres</strong>, versus '
             f'<strong>{old_run["mean_spheres"]:.2f}</strong> in the release. '
             'This is dependency depth under collecting all available checks, not gameplay duration.</p>')
    body += table(run_summary)
    body += '<h2>Ancient timing</h2><img src="ancient-timing.png" alt="Cumulative Ancient timing distributions">'
    body += '</details><h2>Progress without former gate items</h2>'
    body += '''<p><strong>When has each tier arrived?</strong> The tables below show the percentage already holding
    each tier when that character's checkpoint checks first become logically reachable.
    Each cell reads <strong>release 1.1.2 → current branch</strong>. These are availability percentages:
    higher means more characters have the item by that checkpoint. Missing percentage is 100 minus this value.</p>
    <p>All five characters are pooled; the sample sizes below are per checkpoint. A checkpoint is an
    AP logic grouping, not a physical gate or an observed combat result. Each version uses its own first-access
    inventory; this is not a comparison at equal elapsed time or equal inventory.</p>
    <p>Items found in the newly reachable sphere are <strong>not already held</strong> at the checkpoint.
    Multiple checkpoints may open together. Initially locked characters can receive items before they unlock;
    expand the starting/locked views to separate that effect.</p>'''
    sample_sizes = []
    for variant in VARIANTS:
        sample = [r for r in checkpoints if r['variant']==variant and r['checkpoint']=='Early Act 1']
        sample_sizes.append(dict(variant=variant,seeds=len({r['seed'] for r in sample}),
            all_characters=len(sample),starting=sum(r['role']=='starting' for r in sample),
            initially_locked=sum(r['role']=='locked' for r in sample)))
    body += table(sample_sizes)
    for family, _, _ in TIER_FIELDS:
        body += f'<h3>{html.escape(family.removeprefix("Progressive "))}</h3>'
        if family in ('Progressive Starter Card','Progressive Starter Relic'):
            body += '<p>Tier 1 restores the normal starter; tier 2 upgrades it. Tier 1 percentages include characters who already have both copies.</p>'
        body += checkpoint_matrix(availability, family)
        body += '<details><summary>Starting character only</summary>' + checkpoint_matrix(availability, family, 'starting') + '</details>'
        body += '<details><summary>Initially locked characters</summary>' + checkpoint_matrix(availability, family, 'locked') + '</details>'
    body += '<h2>Second starter cards and relics</h2>'
    body += '''<p>This separates an upgrade already held at first access (teal), an upgrade found among the
    checks in that same sphere (gold), and an upgrade that arrives later (gray). Gold can be on any character's
    checks in that sphere; it does not imply placement inside the named region. Only teal can contribute power
    toward first access. This distinction is especially useful near the final boss.</p>
    <img src="second-starter-checkpoints.png" alt="Second starter card and relic availability at each checkpoint, comparing both versions">'''
    for family in ('Progressive Starter Card','Progressive Starter Relic'):
        body += f'<h3>{html.escape(family.removeprefix("Progressive "))}: none, restored, or upgraded?</h3>'
        rows = [r for r in starter_states if r['family']==family and r['character']=='all' and r['role']=='all']
        rows.sort(key=lambda r: (CHECKPOINT_ORDER.index(r['checkpoint']), VARIANTS.index(r['variant'])))
        body += table(rows, ['checkpoint','variant','no_starter_percent','base_only_percent','upgraded_percent'])
    body += '<details><summary>Second starter card and relic together: neither, one, or both</summary>'
    rows = [r for r in starter_pairs if r['character']=='all' and r['role']=='all']
    rows.sort(key=lambda r: (CHECKPOINT_ORDER.index(r['checkpoint']), VARIANTS.index(r['variant'])))
    body += table(rows, ['checkpoint','variant','neither_percent','card_only_percent','relic_only_percent','both_percent']) + '</details>'
    body += '<details><summary>Original former-gate summary (percent missing)</summary>'
    body += table(pooled_gates, ['variant','checkpoint','missing_item','observations','missing_count','missing_percent']) + '</details>'
    body += '<details><summary>Character and starting/locked breakdown</summary>'
    body += table([r for r in gates if r['character'] != 'all'], ['variant','checkpoint','missing_item','character','role','observations','missing_percent']) + '</details>'
    body += '<details><summary>Sphere timing and variability (supporting data)</summary><h2>Timing and variability</h2><img src="all-item-timing.png" alt="Item timing distributions">'
    body += '<p>Fewer spheres means shorter dependency depth. A later mean is not greater variability. Compare SD and percentile ranges as well as timing relative to character unlock. Negative relative timing means an item was available before its character unlocked.</p>'
    body += table(pooled_timing, ['variant','family','tier','n','mean','sd','p10','median','p90'])
    body += '<details><summary>Ancient timing: starting versus initially locked characters</summary>'
    body += table([r for r in role_timing if r['family'] == 'Progressive Ancient' and r['metric'] != 'check_fraction_before']) + '</details>'
    body += '</details>'
    body += '<h2>Evidence of replacement power</h2>'
    body += (f'<p>{len(counterfactual_rows)} first-checkpoint observations in current logic have a positive missing-Ancient penalty. '
             f'{summary["any_ancient_specific_ablation"]} have at least one tested power-source ablation that passes the power rule without that penalty but fails with it. '
             'These are repeated checkpoints within characters and seeds, not independent samples.</p>')
    body += '''<p>The native power calculation must meet the extra requirement despite the absent Ancient.
    Counterfactuals remove a category of card/relic receipts from this calculation only. This does not regenerate the
    seed or establish which item the fill algorithm intended as a replacement. Failing an ablation by itself is weaker
    evidence: those items may be required even without the missing-Ancient penalty. Baseline hard gates are recorded
    through reachability; its older power scale is not directly comparable to the current scale.</p>'''
    examples = [r for r in checkpoints if r['variant'] == 'current' and r['checkpoint'] == 'Early Act 2'
                and json.loads(r['counterfactuals']).get('ordinary_relics', {}).get('ancient_specific')]
    if examples:
        r = sorted(examples, key=lambda r: (r['seed'], r['character']))[0]
        cf = json.loads(r['counterfactuals'])['ordinary_relics']
        penalty = next(v for k,v in json.loads(r['adjustments']).items() if k.startswith('Ancient support'))
        body += (f'<p><strong>Worked example:</strong> seed {r["seed"]}, {html.escape(r["character"])}, '
                 f'Early Act 2, sphere {r["sphere"]}: {r["ancients"]} Ancient(s), '
                 f'{r["card_rewards"]} ordinary card rewards, {r["rare_cards"]} rare card rewards and '
                 f'{r["relics"]} ordinary relics. Total power is {r["power"]:.2f}, meeting a '
                 f'{r["required"]:.2f} requirement including {penalty:.2f} for missing Ancients. '
                 f'Removing ordinary relic contributions leaves {cf["power"]:.2f}: enough for '
                 f'the {r["required"]-penalty:.2f} requirement without that Ancient penalty, '
                 'but insufficient with it. The relics therefore cover the missing Ancient within the power rule.</p>')
    body += '<details><summary>Cards and relics at checkpoints, with and without expected Ancient tiers</summary>' + table(support) + '</details>'
    body += '<h2>How similar are the support inventories?</h2><p>Each combination lists Ancient, Rest, Smith, starter-card and starter-relic counts at first checkpoint access. This measures variety in support inventories, not uniform randomness or card/relic identities. Pooled across characters; equal sample sizes matter when comparing distinct counts.</p>' + table(diversity)
    body += '<h2>Differences and uncertainty</h2><p>Current minus release. 95% percentile bootstrap intervals resample entire seed pairs (2,000 replicates). Character observations within a seed stay together. Identical seeds do not guarantee identical RNG paths or starting characters across versions.</p>'
    body += table(effects)
    body += '''<h2>Query the same dataset</h2><p>CSV exports contain character and starting/locked splits, every tier acquisition,
    checkpoint inventories, and counterfactuals. SQLite additionally retains every placement and every sphere inventory.
    Collection spheres start at zero; items from sphere S enter inventory before sphere S+1. Simultaneous progressive
    copies share a sphere; their location ordering is only a stable display tie-break. Final empty inventory snapshots
    are excluded from total sphere counts. Check fraction uses all sendable locations, not event nodes.</p>
    <p>The original YAML is preserved byte-for-byte. Its logic_difficulty: normal entry is unsupported and ignored by
    both tested APWorlds; the generator warning and each version's resolved options are retained.</p>
    <p>These results describe this fixed YAML. They are not an estimate of failure rates under randomized options.</p><ul>'''
    body += ''.join(f'<li><a href="{name}.csv">{name}.csv</a></li>' for name in datasets)
    body += '<li><a href="summary.json">summary.json</a></li></ul></html>'
    (output / 'report.html').write_text(body)
    (output / 'report-provenance.json').write_text(json.dumps(dict(database=str(args.database.resolve()),
        metadata=metadata,analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()),indent=2))
    print(json.dumps(run_summary, indent=2))
    print(f'Report: {output / "report.html"}')


if __name__ == '__main__':
    main()
