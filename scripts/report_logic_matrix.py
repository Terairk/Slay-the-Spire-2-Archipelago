#!/usr/bin/env python3
"""Combine four old/new logic experiments into a shareable report and queryable database.

Run the three existing per-configuration report generators first. See progression_stats.md.
"""
from __future__ import annotations

import argparse
from collections import Counter
import csv
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import sys

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scripts.report_support_diversity import CHECKPOINTS, FIELDS, EXTRA_FIELDS, diversity_metrics, group_rows, read_data, table, write_csv
from scripts.report_progression_stats import paired_interval


def read_csv(path):
    with path.open(newline='') as file:
        return list(csv.DictReader(file))


def combine_databases(root, cases):
    """Keep original observations intact, with configuration added to every identity."""
    destination = root / 'results.sqlite'
    temporary = root / 'results.building.sqlite'
    temporary.unlink(missing_ok=True)
    with sqlite3.connect(temporary) as db:
        db.execute('CREATE TABLE configurations(configuration TEXT PRIMARY KEY, starters INTEGER, campfires INTEGER)')
        for index, case in enumerate(cases):
            name = case['name']
            db.execute('INSERT INTO configurations VALUES (?,?,?)', (name, case['starters'], case['campfires']))
            db.commit()
            db.execute('ATTACH DATABASE ? AS source', (str(root / name / 'results.sqlite'),))
            for target in ('metadata', 'runs', 'placements', 'spheres', 'inventory', 'checkpoints'):
                if index == 0:
                    db.execute(f'CREATE TABLE {target} AS SELECT CAST(NULL AS TEXT) AS configuration,* FROM source.{target} WHERE 0')
                db.execute(f'INSERT INTO {target} SELECT ?,* FROM source.{target}', (name,))
            db.commit()
            db.execute('DETACH DATABASE source')
            db.execute('ATTACH DATABASE ? AS source', (str(root / name / 'power-report/power_comparison.sqlite'),))
            if index == 0:
                db.execute('CREATE TABLE converted_power AS SELECT CAST(NULL AS TEXT) AS configuration,* FROM source.checkpoint_power WHERE 0')
            db.execute('INSERT INTO converted_power SELECT ?,* FROM source.checkpoint_power', (name,))
            db.commit()
            db.execute('DETACH DATABASE source')
        for target, keys in (
            ('metadata', 'configuration,key'), ('runs', 'configuration,variant,seed'),
            ('spheres', 'configuration,variant,seed,sphere'),
            ('inventory', 'configuration,variant,seed,sphere,item'),
            ('checkpoints', 'configuration,variant,seed,sphere,character,checkpoint'),
            ('converted_power', 'configuration,variant,seed,character,checkpoint'),
        ):
            db.execute(f'CREATE UNIQUE INDEX {target}_identity ON {target}({keys})')
        db.execute('CREATE INDEX placements_item ON placements(configuration,item,variant,sphere)')
        db.execute('CREATE INDEX checkpoint_first ON checkpoints(first_reached,checkpoint,configuration,variant)')
        db.executescript('''
        CREATE VIEW first_checkpoints AS SELECT c.*,r.starting_character,
          s.checks_before*1.0/r.location_count AS check_fraction_before
          FROM checkpoints c JOIN runs r USING(configuration,variant,seed)
          JOIN spheres s USING(configuration,variant,seed,sphere) WHERE first_reached=1;
        CREATE VIEW tier_acquisition AS SELECT configuration,variant,seed,item,item_character,
          row_number() OVER (PARTITION BY configuration,variant,seed,item ORDER BY sphere,location) AS tier,
          sphere,region,location,location_character FROM placements
          WHERE (item LIKE '% Progressive Ancient' OR item LIKE '% Progressive Starter Card'
          OR item LIKE '% Progressive Starter Relic' OR item LIKE '% Progressive Smith'
          OR item LIKE '% Progressive Rest') AND event=0 AND sphere IS NOT NULL;
        ''')
        assert db.execute('PRAGMA quick_check').fetchone()[0] == 'ok'
    temporary.replace(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--combine-database', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    cases = json.loads((root / 'cases.json').read_text())
    overview, checkpoints, contrasts, availability, signatures, outcomes, pairing = [], [], [], [], [], [], []
    seed_lists, manifests = [], []
    for case in cases:
        folder = root / case['name']
        identity = dict(configuration=case['name'], starters=case['starters'], campfires=case['campfires'])
        manifest = json.loads((folder / 'manifest.json').read_text())
        assert manifest['compare_logic']
        manifests.append(manifest)
        seed_lists.append(json.loads((folder / 'seeds.json').read_text()))
        with sqlite3.connect(folder / 'results.sqlite') as db:
            attempted = set(db.execute('SELECT variant,seed FROM runs'))
            expected = {(variant,seed) for variant in ('old','current') for seed in seed_lists[-1]}
            assert attempted == expected, f'{case["name"]}: generation batch is incomplete'
            successful = dict(db.execute("SELECT variant,count(*) FROM runs WHERE status='success' GROUP BY variant"))
        progression = json.loads((folder / 'report/summary.json').read_text())
        assert {r['variant']:r['successful'] for r in progression['runs']} == successful, 'Progression report is stale'
        support = json.loads((folder / 'support-report/summary.json').read_text())
        for row in progression['runs']:
            overview.append({**identity, **row})
        for row in progression['effects']:
            pairing.append({**identity, **row})
        for row in progression['checkpoint_availability']:
            availability.append({**identity, **row})
        for row in support['ancient_support_contrasts']:
            contrasts.append({**identity, **row})
        lookup = {(r['variant'],r['checkpoint'],r['character'],r['role']): r for r in support['summary']}
        rates = {(r['variant'],r['checkpoint'],r['character'],r['role'],r['tier']): r['held_percent']
                 for r in progression['checkpoint_availability'] if r['family'] == 'Progressive Ancient'}
        rows, case_outcomes, _ = read_data(folder / 'results.sqlite')
        outcomes.extend({**identity, **r} for r in case_outcomes)
        groups = group_rows(rows)
        broader = {key: diversity_metrics(Counter(tuple(r[f] for f in FIELDS + EXTRA_FIELDS) for r in group))
                   for key, group in groups.items()}
        for key, metric in broader.items():
            signatures.append({**identity, **dict(zip(('variant','checkpoint','character','role'),key)), **metric})
        for row in read_csv(folder / 'power-report/summary.csv'):
            key = tuple(row[k] for k in ('variant','checkpoint','character','role'))
            mean = float(row['adjusted_power_mean']) if row['adjusted_power_mean'] else None
            # Paired uncertainty keeps all five characters in each seed together.
            # Compute CIs once per pooled checkpoint; detailed descriptive distributions remain in CSV.
            interval = {}
            if key[2:] == ('all','all') and row['variant']=='current':
                with sqlite3.connect(folder / 'power-report/power_comparison.sqlite') as db:
                    samples = {variant: dict(db.execute('''SELECT seed,avg(adjusted_power)
                        FROM checkpoint_power WHERE variant=? AND checkpoint=? AND has_power_gate=1
                        GROUP BY seed''', (variant,row['checkpoint']))) for variant in ('old','current')}
                interval = paired_interval(samples['old'], samples['current'])
            checkpoints.append({**identity, **row,
                'adjusted_power_mean': mean,
                'missing_ancient_1_percent': 100-rates[(*key,1)],
                'missing_ancient_2_percent': 100-rates[(*key,2)],
                'missing_ancient_3_percent': 100-rates[(*key,3)],
                'effective_progressive_combinations': lookup[key]['effective_combinations'],
                'effective_eight_count_combinations': broader[key]['effective_combinations']})
            if interval:
                pairing.append({**identity, 'metric': row['checkpoint'] + ': mean adjusted power', **interval})
    assert all(seeds == seed_lists[0] for seeds in seed_lists), 'Seed schedules differ'
    for field in ('current_source_sha256','archipelago_commit','fuzzer_sha256','collector_sha256'):
        assert len({m[field] for m in manifests}) == 1, field
    datasets = dict(overview=overview, checkpoint_comparison=checkpoints, ancient_support_contrasts=contrasts,
                    checkpoint_availability=availability, eight_count_diversity=signatures,
                    paired_effects=pairing, outcomes=outcomes)
    for name, rows in datasets.items():
        write_csv(root / (name+'.csv'), rows)
    # A paired within-configuration difference is the main comparison; cross-configuration changes
    # also change the item pool and, for Campfire Sanity, the set of locations.
    pooled = [r for r in checkpoints if r['character']=='all' and r['role']=='all']
    comparison = []
    for case in cases:
        for cp in CHECKPOINTS:
            old,new = [next(r for r in pooled if r['configuration']==case['name'] and r['checkpoint']==cp and r['variant']==v)
                       for v in ('old','current')]
            baseline,candidate = old['adjusted_power_mean'],new['adjusted_power_mean']
            comparison.append(dict(configuration=case['name'],checkpoint=cp,
                old_adjusted_power=baseline,new_adjusted_power=candidate,
                difference_percent=100*(candidate-baseline)/baseline if baseline else None,
                old_diversity=old['effective_progressive_combinations'],new_diversity=new['effective_progressive_combinations'],
                new_missing_ancient_1_percent=new['missing_ancient_1_percent'],
                new_missing_ancient_2_percent=new['missing_ancient_2_percent'],
                new_missing_ancient_3_percent=new['missing_ancient_3_percent']))
    write_csv(root/'comparison.csv',comparison)
    (root/'summary.json').write_text(json.dumps(dict(overview=overview,comparison=comparison,outcomes=outcomes,paired_effects=pairing),indent=2))
    os.environ.setdefault('MPLCONFIGDIR',str(root/'.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    figure,axes=plt.subplots(2,2,figsize=(15,10),layout='constrained')
    for ax,case in zip(axes.flat,cases):
        for variant,label,color in (('old','Old logic','#7a8699'),('current','New logic','#147d92')):
            points=[next(r for r in pooled if r['configuration']==case['name'] and r['checkpoint']==cp and r['variant']==variant)
                    for cp in CHECKPOINTS[1:]]
            ax.plot(range(len(points)),[r['adjusted_power_mean'] for r in points],marker='o',label=label,color=color)
            ax.fill_between(range(len(points)),[float(r['adjusted_power_p10']) for r in points],
                            [float(r['adjusted_power_p90']) for r in points],alpha=.1,color=color)
        ax.set_title(f"Starters {'on' if case['starters'] else 'off'} / Campfire Sanity {'on' if case['campfires'] else 'off'}")
        ax.set_xticks(range(len(CHECKPOINTS)-1),[cp.replace('Boss Arena','boss') for cp in CHECKPOINTS[1:]],rotation=50,ha='right')
        ax.set_ylabel('Mean support-adjusted power');ax.grid(alpha=.2);ax.legend()
    figure.savefig(root/'power-by-configuration.png',dpi=160)
    plt.close(figure)
    total = sum(r['runs'] for r in outcomes)
    failures = sum(r['runs'] for r in outcomes if r['status']!='success')
    body = f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Slay the Spire II — logic and support options</title>
    <style>body{{font:16px/1.5 system-ui;max-width:1400px;margin:32px auto;padding:0 24px;color:#192a38}}
    h1,h2{{line-height:1.2}}table{{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}}
    th,td{{padding:8px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}}th{{background:#eef3f6}}
    .scroll{{overflow:auto;margin:20px 0}}select{{font:inherit;padding:6px;margin:8px}}a{{color:#147d92}}details{{margin:20px 0}}</style>
    <h1>Old and new logic, with four support configurations</h1>
    <p><strong>{len(seed_lists[0]):,} matched seeds × four configurations × two logic modes = {total:,} generations.</strong>
    {failures:,} failed attempts. One AP slot contains all five characters from TeraSpire2.yaml.
    Both modes use the same APWorld source; this is an old/new mode comparison, not a rerun of release 1.1.2.</p>
    <p>Frozen statistics-branch snapshot: <code>{manifests[0]['current_commit'][:12]}</code>.
    First starters restore normal strength; missing starters require replacement power and second tiers add upgrade power.
    Early Act 2 uses {next(r['base_required'] for r in pooled if r['variant']=='current' and r['checkpoint']=='Early Act 2')} base power
    and Early Act 3 uses {next(r['base_required'] for r in pooled if r['variant']=='current' and r['checkpoint']=='Early Act 3')},
    with no Rest/Smith penalties in those entry checks.</p>
    <p><strong>Read disabled options correctly:</strong> starters off means normal starters are retained;
    Campfire Sanity off means normal rest and smith access remains. Their zero AP receipt counts do not mean missing abilities.
    Comparisons measure logical first access and modelled power, not combat outcomes, gameplay time, or randomized-option fuzz reliability.</p>
    <h2>Full reports for each configuration</h2><ul>'''
    for case in cases:
        name=case['name']; label=f"Starters {'on' if case['starters'] else 'off'} / Campfire Sanity {'on' if case['campfires'] else 'off'}"
        body+=f'<li><strong>{label}</strong>: <a href="{name}/report/report.html">Progression and tier availability</a> · <a href="{name}/support-report/report.html">Support combinations and Ancient compensation</a> · <a href="{name}/power-report/report.html">Power comparison</a></li>'
    body+='</ul><h2>Generation and dependency depth</h2>'
    body+=table(overview,['configuration','variant','successful','attempted','mean_spheres','sd_spheres','min_spheres','max_spheres'])
    body+='<h2>Power throughout the acts</h2><p>Lines show pooled-character means, with observed 10th–90th percentile bands. Bands describe inventory variation, not confidence intervals.</p><img style="width:100%" src="power-by-configuration.png" alt="Old and new logic support-adjusted power for all four configurations">'
    body+='<details><summary>Act 3 boss comparison across configurations</summary>'
    body+=table([r for r in comparison if r['checkpoint']=='Act 3 Boss Arena'],list(comparison[0]))+'</details>'
    body+='''<h2>Compare power, Ancient availability, and support variety</h2>
    <p>Both modes' first-access inventories are rescored using the new formula for that configuration.
    Adjusted power is raw power minus support penalties (including missing starters); higher means more modelled support.
    Early Act 1 has no power gate and no adjusted-power value. A lower mean is not a measured difficulty percentage.</p>
    <label>Checkpoint <select id="checkpoint">'''+''.join(f'<option>{cp}</option>' for cp in CHECKPOINTS)+'''</select></label>
    <label>Character <select id="character"><option value="all">All characters</option>'''+''.join(f'<option>{c}</option>' for c in ('Ironclad','Silent','Defect','Necrobinder','Regent'))+'''</select></label>
    <label>Role <select id="role"><option value="all">All</option><option value="starting">Starting character</option><option value="locked">Initially locked</option></select></label><div id="explorer"></div>
    <p>The five-count signature covers Ancients, Rests, Smiths and both starters. The eight-count signature also includes
    ordinary card rewards, rare card rewards and ordinary relics. Effective diversity weights combinations by frequency.
    Disabling options removes dimensions from these signatures, so compare old/new within each configuration first.</p>
    <details><summary>Paired differences and uncertainty</summary><p>95% percentile bootstrap intervals resample whole seed pairs.
    Each seed's five characters stay together; they are not five independent trials. Matching seed numbers does not force
    identical placements or starting characters. Character-specific tables are descriptive.</p>'''
    body+=table(pairing,['configuration','metric','seed_pairs','change','low','high'])+'</details>'
    body+='''<h2>How to interpret the comparisons</h2><ul>
    <li>Items found in a checkpoint's newly accessible sphere are not yet held at first access. Separate held/same-sphere/later breakdowns are in the progression reports.</li>
    <li>Support inventories include items from every character's accessible checks, so locked characters may already hold substantial support when unlocked.</li>
    <li>Ancient-compensation probes test the actual local power formula. They do not establish that the generator deliberately supplied a replacement or that a whole alternate route is reachable.</li>
    <li>Disabling Campfire Sanity changes locations and the item pool. Disabling starters replaces their items with filler. Cross-configuration differences include these effects.</li>
    <li>All eight cells reuse the same seed schedule. There are 1,000 independent seed clusters, not 8,000 independent seeds or 40,000 independent characters.</li>
    </ul><h2>Query and share</h2><p>Share the reports ZIP for offline reading. Share the data ZIP for the complete combined SQLite database,
    frozen source, original and effective YAMLs, seed schedule, and analysis scripts. No server or external scripts are needed.</p>
    <p>The combined database adds <code>configuration</code> to every observation. Use <code>variant='old'</code> or
    <code>variant='current'</code> (new logic), and join on configuration as well as variant and seed.</p><pre>
SELECT configuration, variant, checkpoint,
       avg(adjusted_power) AS mean_adjusted_power
FROM converted_power WHERE role='starting'
GROUP BY configuration, variant, checkpoint;

SELECT configuration, variant, character,
       100.0 * avg(ancients &lt; 2) AS percent_without_second_ancient
FROM first_checkpoints WHERE checkpoint='Act 3 Boss Arena'
GROUP BY configuration, variant, character;
    </pre><ul>'''
    body+=''.join(f'<li><a href="{name}.csv">{name.replace("_"," ")}</a></li>' for name in (*datasets,'comparison'))+'</ul>'
    payload=json.dumps(checkpoints).replace('<','\\u003c')
    body+='<script type="application/json" id="data">'+payload+'</script>'
    body+='''<script>const rows=JSON.parse(document.getElementById('data').textContent);
    function render(){const selected=rows.filter(r=>['checkpoint','character','role'].every(k=>r[k]===document.getElementById(k).value));
    const target=document.getElementById('explorer');target.replaceChildren();const wrap=document.createElement('div');wrap.className='scroll';
    const t=document.createElement('table'),h=t.createTHead().insertRow();
    const cols=['configuration','variant','seeds','observations','power_mean','adjusted_power_mean','adjusted_power_p10','adjusted_power_p90','missing_ancient_1_percent','missing_ancient_2_percent','missing_ancient_3_percent','effective_progressive_combinations','effective_eight_count_combinations'];
    for(const k of cols){const th=document.createElement('th');th.textContent=k.replaceAll('_',' ');h.append(th);}
    for(const r of selected){const tr=t.insertRow();for(const k of cols){const td=tr.insertCell(),v=r[k];td.textContent=v===null||v===''?'—':isNaN(Number(v))?v:Number(Number(v).toFixed(2));}}
    wrap.append(t);target.append(wrap);}
    for(const k of ['checkpoint','character','role'])document.getElementById(k).addEventListener('change',render);
    document.getElementById('checkpoint').value='Early Act 2';render();</script></html>'''
    (root/'index.html').write_text(body)
    (root/'report-provenance.json').write_text(json.dumps(dict(manifests=manifests,
        analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()),indent=2))
    if args.combine_database:
        combine_databases(root,cases)
    print(f'Report: {root / "index.html"}; {total} attempts, {failures} failures')


if __name__ == '__main__':
    main()
