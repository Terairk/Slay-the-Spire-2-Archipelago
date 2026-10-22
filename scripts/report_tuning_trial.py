#!/usr/bin/env python3
"""Compare a tuning trial with its predecessor and release on identical seed sets."""
from __future__ import annotations

import argparse
import ast
from collections import Counter, defaultdict
import hashlib
import json
import math
import os
from pathlib import Path
import sqlite3
import sys

if __package__ in (None,''):
    sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from scripts.report_support_diversity import (CHECKPOINTS, FIELDS, EXTRA_FIELDS, distribution,
    diversity_metrics, group_rows, table, write_csv)

LABELS=('release-1.1.2','before-tuning','candidate')


def read_observations(root,variant,label,seeds):
    with sqlite3.connect((root/'results.sqlite').resolve().as_uri()+'?mode=ro',uri=True) as db:
        db.row_factory=sqlite3.Row
        db.execute('attach database ? as power',((root/'power-report/power_comparison.sqlite').resolve().as_uri()+'?mode=ro',))
        rows=[dict(r) for r in db.execute('''select c.*,p.power as converted_power,p.adjusted_power,p.base_required
            from first_checkpoints c join power.checkpoint_power p using(variant,seed,character,checkpoint)
            where c.variant=? and c.seed in ('''+','.join('?' for _ in seeds)+')',(variant,*seeds))]
        outcomes=[dict(r) for r in db.execute('select variant,status,count(*) runs from runs group by variant,status')]
    assert len(rows)==len(seeds)*5*len(CHECKPOINTS), (label,len(rows))
    assert len({(r['seed'],r['character'],r['checkpoint']) for r in rows})==len(rows)
    for r in rows:
        r['variant']=label
        r['role']='starting' if r['character']==r['starting_character'] else 'locked'
    return rows,outcomes


def summaries(rows):
    result=[]
    for key,group in sorted(group_rows(rows).items()):
        record=dict(zip(('variant','checkpoint','character','role'),key))
        record.update(observations=len(group),seeds=len({r['seed'] for r in group}),
                      base_required=group[0]['base_required'])
        for field in ('converted_power','adjusted_power'):
            values=[r[field] for r in group if r[field] is not None]
            record.update({field+'_'+stat:value for stat,value in (distribution(values) if values else
                dict.fromkeys(('mean','p10','median','p90','minimum','maximum'))).items()})
        for family,field,tiers in (('ancient','ancients',3),('smith','smiths',3),('rest','rests',3),
                                    ('starter_card','starter_cards',2),('starter_relic','starter_relics',2)):
            for tier in range(1,tiers+1):
                record[f'missing_{family}_{tier}_percent']=100*sum(r[field]<tier for r in group)/len(group)
        for label,fields in (('progressive',FIELDS),('broader',FIELDS+EXTRA_FIELDS)):
            metrics=diversity_metrics(Counter(tuple(r[f] for f in fields) for r in group))
            record.update({f'{label}_{k}':metrics[k] for k in ('distinct','effective_combinations','top_five_percent')})
        result.append(record)
    return result


def gap_scorecard(summary):
    """Weight the 11 power-gated checkpoints equally; do not cancel opposite gaps."""
    lookup={(r['variant'],r['checkpoint'],r['character'],r['role']):r for r in summary}
    result=[]
    for role in ('all','starting','locked'):
        for label in LABELS[1:]:
            gaps={cp:100*(lookup[label,cp,'all',role]['adjusted_power_mean'] /
                          lookup['release-1.1.2',cp,'all',role]['adjusted_power_mean']-1)
                  for cp in CHECKPOINTS[1:]}
            worst=max(gaps,key=lambda cp:abs(gaps[cp]))
            result.append(dict(variant=label,role=role,
                mean_signed_gap_percent=sum(gaps.values())/len(gaps),
                mean_absolute_gap_percent=sum(abs(v) for v in gaps.values())/len(gaps),
                maximum_absolute_gap_percent=abs(gaps[worst]),worst_checkpoint=worst,
                checkpoints_outside_15_percent=sum(abs(v)>15 for v in gaps.values())))
    return result


def verify_scoring_on_saved_inventories(before, candidate, metadata, seeds, output):
    """Permit refactoring only when rescoring reproduces every compared support score."""
    from scripts.report_power_comparison import reference_rules, score_inventory
    with sqlite3.connect((candidate/'results.sqlite').resolve().as_uri()+'?mode=ro', uri=True) as db:
        options = {json.dumps(json.loads(row[0])['resolved_options'], sort_keys=True)
                   for row in db.execute("select details from runs where variant='current' and status='success'")}
    assert len(options) == 1, 'Expected fixed candidate options'
    rules = reference_rules(candidate.resolve(), json.loads(options.pop()), metadata, output.resolve())
    selected = set(seeds)
    validated = 0
    with sqlite3.connect((before/'results.sqlite').resolve().as_uri()+'?mode=ro', uri=True) as db:
        db.row_factory = sqlite3.Row
        db.execute('attach database ? as power', ((before/'power-report/power_comparison.sqlite').resolve().as_uri()+'?mode=ro',))
        rows = defaultdict(list)
        for row in db.execute('select * from power.checkpoint_power'):
            if row['seed'] in selected:
                rows[row['variant'], row['seed']].append(dict(row))
        for (variant, seed), observations in rows.items():
            inventories = defaultdict(dict)
            for row in db.execute('select sphere,item,count from inventory where variant=? and seed=?', (variant, seed)):
                inventories[row['sphere']][row['item']] = row['count']
            for saved in observations:
                rule, has_gate = rules[saved['character'], saved['checkpoint']]
                score = score_inventory(rule, has_gate, inventories[saved['sphere']])
                # Base thresholds may change in a tuning trial. The common power scale may not.
                for field in ('cards', 'power', 'adjustment', 'adjusted_power', 'minimum_cards', 'has_power_gate'):
                    old, new = saved[field], score[field]
                    assert ((old is None and new is None) or
                            (old is not None and new is not None and math.isclose(old, new, abs_tol=1e-9))), (
                                'Power scoring changed; rescore on one common formula', variant, seed,
                                saved['character'], saved['checkpoint'], field, old, new)
                assert json.loads(saved['adjustments']) == json.loads(score['adjustments'])
                validated += 1
    assert validated == len(seeds)*2*5*len(CHECKPOINTS), validated
    return validated


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before',type=Path)
    parser.add_argument('candidate',type=Path)
    args=parser.parse_args()
    output=args.candidate/'tuning-report'
    output.mkdir(parents=True,exist_ok=True)
    before_meta=json.loads((args.before/'manifest.json').read_text())
    candidate_meta=json.loads((args.candidate/'manifest.json').read_text())
    for key in ('yaml_sha256','fuzzer_sha256','collector_sha256','baseline_sha256','archipelago_commit','seed_schedule'):
        assert before_meta[key]==candidate_meta[key],key
    candidate_seeds=json.loads((args.candidate/'seeds.json').read_text())
    before_seeds=set(json.loads((args.before/'seeds.json').read_text()))
    assert len(candidate_seeds)==len(set(candidate_seeds))
    seeds=[seed for seed in candidate_seeds if seed in before_seeds]
    assert seeds, 'No common seeds to compare'
    # Requirements changed; the scoring/adjustment methods must stay identical
    # before comparing adjusted power from two differently frozen report runs.
    def power_class(root):
        world_root=root/'runtime/current/worlds/spire2'
        source=world_root/('rules_new.py' if (world_root/'rules_new.py').exists() else 'rules.py')
        tree=ast.parse(source.read_text())
        return ast.dump(next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='SpireHasPower'))
    scoring_validation = {'method': 'identical power class', 'rescored_observations': 0}
    if power_class(args.before) != power_class(args.candidate):
        scoring_validation = {'method': 'candidate scoring reproduces saved inventories',
            'rescored_observations': verify_scoring_on_saved_inventories(
                args.before, args.candidate, candidate_meta, seeds, output)}
    rows=[]
    for root,variant,label in ((args.candidate,'release-1.1.2','release-1.1.2'),
                               (args.before,'current','before-tuning'),(args.candidate,'current','candidate')):
        observations,outcomes=read_observations(root,variant,label,seeds)
        rows.extend(observations)
    # Repeated release controls are a reproducibility check, never extra samples.
    old_control,_=read_observations(args.before,'release-1.1.2','release-1.1.2',seeds)
    def control_signature(group):
        return {(r['seed'],r['character'],r['checkpoint']):tuple(r[k] for k in
            ('sphere','role','converted_power','adjusted_power',*FIELDS,*EXTRA_FIELDS)) for r in group}
    assert control_signature(old_control)==control_signature([r for r in rows if r['variant']=='release-1.1.2'])
    summary=summaries(rows)
    scorecard=gap_scorecard(summary)
    lookup={(r['variant'],r['checkpoint'],r['character'],r['role']):r for r in summary}
    comparison=[]
    for cp in CHECKPOINTS[1:]:
        values={label:lookup[label,cp,'all','all']['adjusted_power_mean'] for label in LABELS}
        comparison.append(dict(checkpoint=cp,**values,
            before_gap_percent=100*(values['before-tuning']/values['release-1.1.2']-1),
            candidate_gap_percent=100*(values['candidate']/values['release-1.1.2']-1)))
    flexibility=[]
    for cp in CHECKPOINTS[1:]:
        tier=next(act for act in (1,2,3) if f'Act {act}' in cp)
        for label in LABELS:
            r=lookup[label,cp,'all','all']
            flexibility.append(dict(checkpoint=cp,variant=label,ancient_tier=tier,
                missing_ancient_percent=r[f'missing_ancient_{tier}_percent'],
                missing_starter_card_2_percent=r['missing_starter_card_2_percent'],
                missing_smith_for_act_percent=r[f'missing_smith_{tier}_percent'],
                progressive_effective_combinations=r['progressive_effective_combinations'],
                broader_effective_combinations=r['broader_effective_combinations']))
    for name,data in (('summary',summary),('power_comparison',comparison),('flexibility',flexibility),('gap_scorecard',scorecard)):
        write_csv(output/f'{name}.csv',data)
    os.environ.setdefault('MPLCONFIGDIR',str(output.resolve()/'.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig,axes=plt.subplots(1,2,figsize=(15,6),layout='constrained')
    for ax,role,title in zip(axes,('all','starting'),('All five characters','Starting character only')):
        for label,color in zip(LABELS,('#7a8699','#3978bd','#16846e')):
            ax.plot(range(11),[lookup[label,cp,'all',role]['adjusted_power_mean'] for cp in CHECKPOINTS[1:]],
                    color=color,marker='o',label=label)
        ax.set_title(title);ax.set_ylabel('Mean support-adjusted power')
        ax.set_xticks(range(11),[cp.replace('Boss Arena','boss') for cp in CHECKPOINTS[1:]],rotation=50,ha='right')
        ax.grid(alpha=.2);ax.legend()
    fig.savefig(output/'tuning-power.png',dpi=160);plt.close(fig)
    requirements=[dict(checkpoint=cp,before=lookup['before-tuning',cp,'all','all']['base_required'],
                       candidate=lookup['candidate',cp,'all','all']['base_required']) for cp in CHECKPOINTS[1:]]
    changed=sum(r['before']!=r['candidate'] for r in requirements)
    body='''<!doctype html><html lang="en"><meta charset="utf-8"><title>Power tuning comparison</title>
    <style>body{font:16px/1.5 system-ui;max-width:1400px;margin:36px auto;padding:0 24px;color:#192a38}
    table{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}th,td{padding:8px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}
    th{background:#eef3f6}.scroll{overflow:auto;margin:20px 0}img{width:100%}select{font:inherit;padding:6px;margin:8px}a{color:#147d92}</style>
    <h1>Power tuning comparison</h1>'''
    body+=f'''<p><strong>{len(seeds)} identical numeric seeds per version; {len(seeds)*5} character observations per checkpoint.</strong>
    Comparison: release 1.1.2, previous snapshot ({before_meta['current_commit'][:7]}) and approved candidate
    ({candidate_meta['current_commit'][:7]}). Each dataset is restricted to this same seed subset.
    Release controls reproduce the saved first-access inventories and spheres exactly.</p>
    <p>{changed} base requirements changed. For this fixed YAML, the compared inventories use the same verified
    item weights, support adjustments and minimum-card requirements. Adjusted power is card/relic power minus
    support adjustments. Original first-access times and rules are retained for each version.
    Settings outside this YAML, including sparse-generation fallbacks, are not measured here.</p>
    <img src="tuning-power.png" alt="Mean adjusted power across regions before tuning, after tuning, and in the release">'''
    old,before,candidate=(lookup[label,'Act 3 Boss Arena','all','all'] for label in LABELS)
    body+=f'''<h2>What changed</h2><p>At the final boss, mean adjusted power moved from
    <strong>{before['adjusted_power_mean']:.2f} to {candidate['adjusted_power_mean']:.2f}</strong>;
    the release benchmark is {old['adjusted_power_mean']:.2f}. Missing-third-Ancient access changed from
    <strong>{before['missing_ancient_3_percent']:.1f}% to {candidate['missing_ancient_3_percent']:.1f}%</strong>.
    The effective number of progressive support combinations changed from
    <strong>{before['progressive_effective_combinations']:.1f} to {candidate['progressive_effective_combinations']:.1f}</strong>
    (release: {old['progressive_effective_combinations']:.1f}). Including card/relic counts, effective combinations
    changed from {before['broader_effective_combinations']:.1f} to {candidate['broader_effective_combinations']:.1f}
    (release: {old['broader_effective_combinations']:.1f}). This is one strength-versus-variety comparison
    to review before another tuning pass.</p>'''
    body+='''<h2>Distance from the release</h2><p>Each of the 11 power-gated checkpoints has equal weight;
    Early Act 1 has no power gate and is excluded. Absolute gaps prevent above-release values from cancelling
    below-release values. The 15% count is a diagnostic reference, not an automatic pass/fail rule.
    Starting/locked rows expose differences hidden by pooling. Desired opening and late-game targets may differ.</p>'''
    body+=table(scorecard,list(scorecard[0]))
    body+='<details><summary>Approved requirement changes</summary>'+table(requirements,list(requirements[0]))+'</details>'
    body+='<details><summary>Candidate and release-control generation outcomes</summary>'+table(outcomes,['variant','status','runs'])+'</details>'
    body+='<h2>Average support-adjusted power at first access</h2>'
    body+=table(comparison,list(comparison[0]))
    body+='''<h2>Alternative support remains available</h2><p>The Ancient column means missing Neow in Act 1,
    missing tier 2 in Act 2, and missing tier 3 in Act 3. Smiths use the act's tier for inventory comparison,
    whether or not that tier is expected by that particular checkpoint's rule.</p>
    <p>Effective combinations weight inventory signatures by frequency. Progressive signatures use the five upgrade
    counts; broader signatures also include ordinary card rewards, rare cards and relics. Neither is a measure of
    gameplay balance. Both are shown on equal sample sizes; compare within character and starting status below.</p>'''
    body+=table(flexibility,list(flexibility[0]))
    body+='''<h2>Character and starting-status detail</h2><label>Character <select id="character"><option value="all">All</option>'''
    body+=''.join(f'<option>{c}</option>' for c in sorted({r['character'] for r in rows}))
    body+='''</select></label><label>Starting status <select id="role"><option value="all">All</option>
    <option value="starting">Starting character</option><option value="locked">Initially locked</option></select></label>
    <div id="explorer"></div><h2>Interpretation</h2><p>The pilot checks whether the agreed requirements move the
    observed strength curve toward the release while preserving alternative support. Differences remain descriptive:
    characters within a seed are correlated, starting characters may differ between versions, and changing logic
    changes placement as well as reachability. These runs do not simulate combat or show that a particular
    numeric curve is objectively correct. Broader signatures approach the sample-size ceiling at several checkpoints,
    limiting what this 100-seed pilot says about their diversity. Further tuning should follow review of this trial.</p>
    <p>Original reports and datasets remain intact. Full candidate receipts, placements and checkpoints are saved in
    the adjacent results.sqlite. No repeated control runs are counted as extra evidence.</p>
    <p><a href="summary.csv">All grouped statistics</a> · <a href="power_comparison.csv">Power comparison</a> ·
    <a href="flexibility.csv">Support alternatives</a> · <a href="gap_scorecard.csv">Percentage-gap scorecard</a> ·
    <a href="../power-report/report.html">Candidate power detail</a> ·
    <a href="provenance.json">Provenance</a></p>'''
    body+='<script type="application/json" id="data">'+json.dumps(dict(summary=summary,checkpoints=CHECKPOINTS)).replace('<','\\u003c')+'</script>'
    body+='''<script>
    const data=JSON.parse(document.getElementById('data').textContent);
    function render(){const target=document.getElementById('explorer');target.replaceChildren();
      for(const variant of ['release-1.1.2','before-tuning','candidate']){
        const heading=document.createElement('h3');heading.textContent=variant;target.append(heading);
        const wrap=document.createElement('div');wrap.className='scroll';const table=document.createElement('table');
        const cols=['checkpoint','observations','converted_power_mean','adjusted_power_mean','adjusted_power_p10','adjusted_power_p90',
          'missing_ancient_1_percent','missing_ancient_2_percent','missing_ancient_3_percent','missing_starter_card_2_percent','missing_starter_relic_2_percent',
          'progressive_effective_combinations','broader_effective_combinations'];
        const head=table.createTHead().insertRow();for(const c of cols){const th=document.createElement('th');th.textContent=c.replaceAll('_',' ');head.append(th);}
        const body=table.createTBody();for(const cp of data.checkpoints){const r=data.summary.find(r=>r.variant===variant&&r.checkpoint===cp&&r.character===document.getElementById('character').value&&r.role===document.getElementById('role').value);
          if(!r)continue;const tr=body.insertRow();for(const c of cols){const td=tr.insertCell();td.textContent=r[c]===null?'—':typeof r[c]==='number'?Number(r[c].toFixed(2)):r[c];}}
        wrap.append(table);target.append(wrap);
      }}
    for(const id of ['character','role'])document.getElementById(id).addEventListener('change',render);render();
    </script></html>'''
    (output/'report.html').write_text(body)
    (output/'provenance.json').write_text(json.dumps(dict(before=before_meta,candidate=candidate_meta,seeds=seeds,
        analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        identical_release_controls=True,scoring_validation=scoring_validation,observations=len(rows)),indent=2))
    print(json.dumps(comparison,indent=2))
    print(f'Report: {output / "report.html"}')


if __name__=='__main__':
    main()
