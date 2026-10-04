#!/usr/bin/env python3
"""Standalone support-inventory analysis; reads existing runs without changing them."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import csv
import hashlib
import html
import json
import math
import os
from pathlib import Path
import sqlite3
import statistics

FIELDS = ('ancients', 'rests', 'smiths', 'starter_cards', 'starter_relics')
EXTRA_FIELDS = ('card_rewards', 'rare_cards', 'relics')
SUPPORT_FIELDS = EXTRA_FIELDS + FIELDS[1:]
VARIANTS = ('release-1.1.2', 'current')
CHECKPOINTS = tuple(cp for act in (1, 2, 3) for cp in
                    (f'Early Act {act}', f'Mid Act {act}', f'Late Act {act}', f'Act {act} Boss Arena'))


def read_data(path):
    with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True) as db:
        db.row_factory = sqlite3.Row
        rows = [dict(row) for row in db.execute('''SELECT variant,seed,character,checkpoint,
            starting_character,ancients,rests,smiths,starter_cards,starter_relics,
            card_rewards,rare_cards,relics,adjustments,counterfactuals FROM first_checkpoints''')]
        outcomes = [dict(row) for row in db.execute('SELECT variant,status,count(*) AS runs FROM runs GROUP BY variant,status')]
        metadata = {r['key']: json.loads(r['value']) for r in db.execute('SELECT key,value FROM metadata')}
    for row in rows:
        row['role'] = 'starting' if row['character'] == row['starting_character'] else 'locked'
        row['combination'] = tuple(row[field] for field in FIELDS)
        for field in ('adjustments', 'counterfactuals'):
            row[field] = json.loads(row[field])
    return rows, outcomes, metadata


def diversity_metrics(counts):
    n = sum(counts.values())
    entropy = -sum((count / n) * math.log(count / n) for count in counts.values())
    return dict(observations=n, distinct=len(counts), effective_combinations=math.exp(entropy),
                most_common_percent=100 * max(counts.values()) / n,
                top_five_percent=100 * sum(sorted(counts.values(), reverse=True)[:5]) / n,
                singleton_combinations=sum(count == 1 for count in counts.values()))


def expected_richness(seed_count, incidence, sample_size):
    """Expected distinct tuples in a uniform subset of whole seeds, without replacement.

    A tuple present in m seeds is absent with probability C(N-m,k)/C(N,k).
    Incidence counts seeds, not characters, preserving clustering within each seed.
    This interpolates the observed sample; it does not predict unseen combinations.
    """
    if not 0 <= sample_size <= seed_count:
        raise ValueError('Sample size must lie within the observed number of seeds')
    if sample_size == 0:
        return 0.0
    log_denominator = math.lgamma(seed_count + 1) - math.lgamma(sample_size + 1) - math.lgamma(seed_count - sample_size + 1)
    total = 0.0
    for present_in in incidence.values():
        if not 1 <= present_in <= seed_count:
            raise ValueError('Invalid seed incidence')
        absent_in = seed_count - present_in
        if sample_size > absent_in:
            total += 1
        else:
            log_numerator = math.lgamma(absent_in + 1) - math.lgamma(sample_size + 1) - math.lgamma(absent_in - sample_size + 1)
            total += -math.expm1(min(0, log_numerator - log_denominator))
    return total


def group_rows(rows):
    groups = defaultdict(list)
    for row in rows:
        for character, role in (('all','all'), ('all',row['role']), (row['character'],'all'), (row['character'],row['role'])):
            groups[(row['variant'],row['checkpoint'],character,role)].append(row)
    return groups


def analyze(rows):
    groups = group_rows(rows)
    summaries, combinations, discovery = [], [], []
    for key, group in sorted(groups.items()):
        identity = dict(zip(('variant','checkpoint','character','role'), key))
        counts = Counter(r['combination'] for r in group)
        seeds_per_combo = defaultdict(set)
        examples = {}
        for r in sorted(group, key=lambda r: (r['seed'],r['character'])):
            seeds_per_combo[r['combination']].add(r['seed'])
            examples.setdefault(r['combination'], (r['seed'],r['character']))
        n_seeds = len({r['seed'] for r in group})
        summaries.append(dict(**identity, seeds=n_seeds, **diversity_metrics(counts)))
        for rank, (combo,count) in enumerate(sorted(counts.items(), key=lambda x: (-x[1],x[0])), 1):
            seed, character = examples[combo]
            combinations.append(dict(**identity, rank=rank, **dict(zip(FIELDS,combo)),
                count=count, percent=100*count/len(group), seeds=len(seeds_per_combo[combo]),
                example_seed=seed, example_character=character))
        if identity['character'] == 'all':
            incidence = {combo: len(seeds) for combo,seeds in seeds_per_combo.items()}
            for size in sorted({s for s in (10,25,50,100,200,300,500,750,900,1000,n_seeds) if s <= n_seeds}):
                discovery.append(dict(**identity, sampled_seeds=size,
                    expected_distinct=expected_richness(n_seeds,incidence,size)))
    overlaps = []
    for checkpoint in CHECKPOINTS:
        for role in ('all','starting','locked'):
            sets = [{r['combination'] for r in rows if r['variant']==v and r['checkpoint']==checkpoint
                     and (role=='all' or r['role']==role)} for v in VARIANTS]
            old,new = sets
            overlaps.append(dict(checkpoint=checkpoint,role=role,shared=len(old & new),
                                 only_release=len(old-new),only_current=len(new-old)))
    return summaries, combinations, discovery, overlaps


def distribution(values):
    ordered = sorted(values)
    def quantile(fraction):
        position = (len(ordered) - 1) * fraction
        lower = int(position)
        upper = min(lower + 1, len(ordered) - 1)
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)
    return dict(mean=statistics.mean(ordered), p10=quantile(.1), median=quantile(.5),
                p90=quantile(.9), minimum=ordered[0], maximum=ordered[-1])


def broaden_combinations(rows, combinations):
    """Keep the original five-count signature; describe variation inside it."""
    lookup = {(r['variant'],r['checkpoint'],r['character'],r['role'],
               tuple(r[f] for f in FIELDS)): r for r in combinations}
    for identity, group in group_rows(rows).items():
        by_combo = defaultdict(list)
        for row in group:
            by_combo[row['combination']].append(row)
        for combo, members in by_combo.items():
            result = lookup[(*identity, combo)]
            for field in EXTRA_FIELDS:
                result.update({f'{field}_{stat}': value
                               for stat,value in distribution(r[field] for r in members).items()})
            counts = Counter(tuple(r[f] for f in EXTRA_FIELDS) for r in members)
            result['card_relic_combinations'] = len(counts)
            result['effective_card_relic_combinations'] = diversity_metrics(counts)['effective_combinations']


def ancient_analysis(rows):
    """Descriptive contrasts standardized to the missing group's character/role mix.

    Only strata containing both cohorts enter the contrast. Each present-stratum
    mean receives its missing-stratum observation weight, avoiding changes in the
    character or starting-role mix masquerading as support differences.
    """
    contrasts, cohorts, evidence = [], [], []
    for key, group in sorted(group_rows(rows).items()):
        identity = dict(zip(('variant','checkpoint','character','role'), key))
        for tier in (1,2,3):
            missing = [r for r in group if r['ancients'] < tier]
            present = [r for r in group if r['ancients'] >= tier]
            for label, members in (('missing',missing),('present',present)):
                if members:
                    cohorts.append(dict(**identity, tier=tier, status=label, observations=len(members),
                        seeds=len({r['seed'] for r in members}),
                        **{f'{f}_mean': statistics.mean(r[f] for r in members) for f in SUPPORT_FIELDS}))
            strata = defaultdict(lambda: ([],[]))
            for row in group:
                strata[(row['character'],row['role'])][int(row['ancients'] >= tier)].append(row)
            common = [(m,p) for m,p in strata.values() if m and p]
            matched_missing = [r for m,_ in common for r in m]
            matched_present = [r for _,p in common for r in p]
            n = len(matched_missing)
            base = dict(**identity,tier=tier,missing_observations=len(missing),present_observations=len(present),
                comparable_strata=len(common),matched_missing=n,matched_present=len(matched_present),
                missing_seeds=len({r['seed'] for r in matched_missing}),
                present_seeds=len({r['seed'] for r in matched_present}),
                missing_coverage_percent=100*n/len(missing) if missing else None)
            for field in SUPPORT_FIELDS:
                missing_mean = statistics.mean(r[field] for r in matched_missing) if n else None
                present_mean = sum(len(m)*statistics.mean(r[field] for r in p) for m,p in common)/n if n else None
                contrasts.append(dict(**base,resource=field,missing_mean=missing_mean,
                    standardized_present_mean=present_mean,
                    difference=missing_mean-present_mean if n else None))
            penalized = [r for r in missing if any(k.startswith('Ancient support') and v > 0
                                                  for k,v in r['adjustments'].items())]
            if identity['variant']=='current' and penalized:
                sources = sorted({source for r in penalized for source in r['counterfactuals']})
                for source in (*sources,'any_tested_category'):
                    specific = sum(any(c['ancient_specific'] for c in r['counterfactuals'].values())
                        if source=='any_tested_category' else r['counterfactuals'][source]['ancient_specific']
                        for r in penalized)
                    evidence.append(dict(**identity,tier=tier,source=source,
                        missing_observations=len(missing),positive_penalty_observations=len(penalized),
                        seeds=len({r['seed'] for r in penalized}),ancient_specific_observations=specific,
                        ancient_specific_percent=100*specific/len(penalized)))
    return contrasts, cohorts, evidence


def write_csv(path, rows):
    if rows:
        with path.open('w',newline='') as f:
            writer=csv.DictWriter(f,fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)


def table(rows, columns):
    def cell(value):
        return html.escape(f'{value:.1f}' if isinstance(value,float) else str(value))
    return '<div class="scroll"><table><thead><tr>' + ''.join(f'<th>{html.escape(c.replace("_"," "))}</th>' for c in columns) + '</tr></thead><tbody>' + ''.join(
        '<tr>'+''.join(f'<td>{cell(r[c])}</td>' for c in columns)+'</tr>' for r in rows)+'</tbody></table></div>'


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('database',type=Path)
    parser.add_argument('--pilot',type=Path,help='Optional preserved 100-seed database for observed growth comparison')
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    output=args.output or args.database.parent/'support-report'
    output.mkdir(parents=True,exist_ok=True)
    rows,outcomes,metadata=read_data(args.database)
    summaries,combinations,discovery,overlaps=analyze(rows)
    broaden_combinations(rows,combinations)
    contrasts,cohorts,evidence=ancient_analysis(rows)
    lookup={(r['variant'],r['checkpoint'],r['character'],r['role']):r for r in summaries}
    comparison=[]
    for cp in CHECKPOINTS:
        old,new=(lookup[(v,cp,'all','all')] for v in VARIANTS)
        comparison.append(dict(checkpoint=cp,release_distinct=old['distinct'],current_distinct=new['distinct'],
            release_effective=old['effective_combinations'],current_effective=new['effective_combinations'],
            release_top_five_percent=old['top_five_percent'],current_top_five_percent=new['top_five_percent']))
    pilot_growth=[]
    if args.pilot:
        pilot,_,pilot_meta=read_data(args.pilot)
        for key in ('current_commit','yaml_sha256','baseline_sha256','archipelago_commit','collector_sha256'):
            assert pilot_meta[key]==metadata[key], f'Pilot differs: {key}'
        assert {(r['variant'],r['seed']) for r in pilot} <= {(r['variant'],r['seed']) for r in rows}
        for cp in CHECKPOINTS:
            for variant in VARIANTS:
                sample=[r for r in pilot if r['variant']==variant and r['checkpoint']==cp]
                counts=Counter(r['combination'] for r in sample)
                full=lookup[(variant,cp,'all','all')]
                pilot_growth.append(dict(variant=variant,checkpoint=cp,pilot_seeds=len({r['seed'] for r in sample}),
                    expanded_seeds=full['seeds'],pilot_distinct=len(counts),expanded_distinct=full['distinct'],
                    additional_combinations=full['distinct']-len(counts)))
    datasets=dict(summary=summaries,combination_counts=combinations,discovery=discovery,
                  overlap=overlaps,comparison=comparison,pilot_growth=pilot_growth,
                  ancient_support_contrasts=contrasts,ancient_support_cohorts=cohorts,
                  ancient_support_evidence=evidence)
    for name,data in datasets.items():
        write_csv(output/f'{name}.csv',data)
    (output/'summary.json').write_text(json.dumps({k:v for k,v in datasets.items() if k!='combination_counts'},indent=2))

    os.environ.setdefault('MPLCONFIGDIR',str(output.resolve()/'.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    colors=('#7a8699','#147d92')
    labels=('Release 1.1.2','Current branch')
    fig,axes=plt.subplots(1,2,figsize=(15,6),layout='constrained')
    for ax,metric,title in zip(axes,('distinct','effective_combinations'),
                               ('All observed combinations','Effective number of common combinations')):
        for variant,color,label in zip(VARIANTS,colors,labels):
            ax.plot(range(12),[lookup[(variant,cp,'all','all')][metric] for cp in CHECKPOINTS],
                    marker='o',color=color,label=label)
        ax.set_xticks(range(12),[cp.replace('Boss Arena','boss') for cp in CHECKPOINTS],rotation=50,ha='right')
        ax.set_title(title)
        ax.set_ylim(bottom=0)
        ax.grid(alpha=.2)
    axes[0].legend()
    fig.savefig(output/'diversity-overview.png',dpi=160)
    plt.close(fig)
    selected=('Mid Act 1','Late Act 1','Early Act 2','Act 2 Boss Arena','Early Act 3','Act 3 Boss Arena')
    fig,axes=plt.subplots(2,3,figsize=(14,8),layout='constrained')
    for ax,cp in zip(axes.flat,selected):
        for variant,color,label in zip(VARIANTS,colors,labels):
            sample=[r for r in discovery if r['variant']==variant and r['checkpoint']==cp and r['role']=='all']
            ax.plot([r['sampled_seeds'] for r in sample],[r['expected_distinct'] for r in sample],marker='.',color=color,label=label)
        ax.set_title(cp)
        ax.set_xlabel('Seeds sampled (all five characters kept together)')
        ax.set_ylabel('Expected distinct combinations')
        ax.set_ylim(bottom=0)
        ax.grid(alpha=.2)
    axes[0,0].legend()
    fig.savefig(output/'discovery-curves.png',dpi=160)
    plt.close(fig)
    totals=[dict(variant=v,seeds=len({r['seed'] for r in rows if r['variant']==v}),
                 characters=lookup[(v,'Mid Act 1','all','all')]['observations']) for v in VARIANTS]
    title='Support inventories: an expanded comparison'
    body=f'''<!doctype html><html lang="en"><meta charset="utf-8"><title>{title}</title>
    <style>body{{font:16px/1.5 system-ui;max-width:1300px;margin:36px auto;padding:0 24px;color:#192a38}}
    h1,h2{{line-height:1.2}}table{{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}}
    th,td{{padding:7px 9px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}}th{{background:#eef3f6}}
    .scroll{{overflow:auto;margin:18px 0}}img{{width:100%}}a{{color:#147d92}}details{{margin:24px 0}}
    select{{font:inherit;padding:6px;margin:5px 14px 5px 0}}label{{display:inline-block}}.note{{background:#eef6f7;padding:16px}}
    </style><h1>{title}</h1><p>Same frozen APWorlds and original five-character YAML.
    Each inventory is observed when that character's checkpoint checks first become logically reachable,
    before collecting the newly accessible sphere. No gameplay or combat is simulated.</p>'''
    body+=table(totals,['variant','seeds','characters'])
    body+='''<p>A combination means five counts: <strong>Ancients, Rests, Smiths, starter cards, starter relics</strong>.
    The signature keeps its original definition; the new analysis below opens up the card/relic variation hidden
    inside it. Each character contributes one observation per checkpoint; checkpoints and characters
    within a seed are correlated.</p><p><a href="#broader">New: broader support inventories and missing Ancients</a></p>
    <h2>Distinct combinations and how common they are</h2>
    <p>Distinct counts include rare one-off inventories. The <strong>effective number</strong> weights combinations by
    frequency: it is the number of equally common combinations with the same Shannon entropy. It falls well below
    the distinct count when most observations use a smaller set. Top-five share measures concentration directly.</p>
    <img src="diversity-overview.png" alt="Observed and frequency-weighted support diversity across checkpoints">'''
    body+=table(comparison,list(comparison[0]))
    body+='''<h2>Does the sample still uncover new combinations?</h2>
    <p>Each curve gives the exact expected distinct count when choosing a random subset of the recorded seeds,
    without replacement. All five characters from a seed stay together. These are interpolations within this dataset,
    not forecasts of unseen combinations or confidence intervals. Continuing growth suggests that the observed
    catalogue is not exhausted; flattening does not prove that every feasible combination was found.</p>
    <img src="discovery-curves.png" alt="Expected support combination discovery as more seeds are sampled">'''
    if pilot_growth:
        body+='<details><summary>Actual original pilot versus expanded sample</summary>'+table(pilot_growth,list(pilot_growth[0]))+'</details>'
    body+='''<h2 id="broader">Broader support inventories and missing Ancients</h2>
    <p>Card rewards, rare card rewards and ordinary relic receipts now accompany the five progressive counts.
    These are AP receipts, not the number or identities of cards kept in an actual deck. Shop access, removals,
    gold, potions and filler can also matter in play; these eight counts still do not describe an entire run.</p>
    <p><strong>Missing tier 1</strong> means zero Ancients; <strong>missing tier 2</strong> means zero or one;
    <strong>missing tier 3</strong> means zero, one or two. Present means at least that tier.
    These cohorts overlap across tiers and must not be added together. A future tier can be missing before
    its act without creating a positive Ancient penalty.</p>'''
    early_two={r['resource']:r for r in contrasts if r['variant']=='current' and r['checkpoint']=='Early Act 2'
               and r['character']=='all' and r['role']=='all' and r['tier']==2}
    early_two_test=next((r for r in evidence if r['checkpoint']=='Early Act 2' and r['character']=='all'
                        and r['role']=='all' and r['tier']==2 and r['source']=='any_tested_category'),None)
    top_old=next(r for r in combinations if r['variant']=='release-1.1.2' and r['checkpoint']=='Early Act 2'
                 and r['character']=='all' and r['role']=='all' and r['rank']==1)
    body+=f'''<div class="note"><strong>What the broader view changes</strong>
    <p>The release's most common Early Act 2 five-upgrade combination alone contains
    <strong>{top_old['card_relic_combinations']} different card/relic triples</strong> across
    {top_old['count']} observations. Its card-reward 10th–90th percentile range is
    {top_old['card_rewards_p10']:g}–{top_old['card_rewards_p90']:g}, and its ordinary relic range is
    {top_old['relics_p10']:g}–{top_old['relics_p90']:g}. The old five-count measure concealed variety in
    <em>both</em> worlds; it should not be read as a complete comparison of power progression.</p>'''
    if early_two['card_rewards']['difference'] is not None:
        cards,relics=early_two['card_rewards'],early_two['relics']
        body+=f'''<p>In the current branch at Early Act 2, cases missing tier 2 average
        <strong>{cards['missing_mean']:.2f} ordinary card rewards versus {cards['standardized_present_mean']:.2f}</strong>
        for present cases after standardization; ordinary relics average
        <strong>{relics['missing_mean']:.2f} versus {relics['standardized_present_mean']:.2f}</strong>.
        These are descriptive associations at first access; they do not establish that generation deliberately
        supplied or withheld replacement support.</p>'''
    if early_two_test:
        body+=f'''<p>Nevertheless, <strong>{early_two_test['ancient_specific_percent']:.1f}%</strong>
        ({early_two_test['ancient_specific_observations']:,}/{early_two_test['positive_penalty_observations']:,})
        of the positive-penalty missing-tier-2 cases have at least one strict local substitution hit.
        Available support can cover an Ancient penalty without being more plentiful than in Ancient-present cases.
        The test definition and per-category breakdown are below.</p>'''
    body+='</div>'
    highlights=[]
    for cp,tier in (('Mid Act 1',1),('Early Act 2',2),('Early Act 3',3),('Act 3 Boss Arena',3)):
        selected=[r for r in contrasts if r['variant']=='current' and r['checkpoint']==cp
                  and r['character']=='all' and r['role']=='all' and r['tier']==tier]
        highlights.append(dict(checkpoint=cp,missing_tier=tier,missing_observations=selected[0]['missing_observations'],
            **{r['resource']+'_difference':r['difference'] for r in selected}))
    body+='<h3>Current branch: support difference when an Ancient is missing</h3>'
    body+='''<p>Positive values mean more receipts in missing-Ancient cases. For each checkpoint, comparisons
    are made within character × starting-status groups. Present-group means are then weighted to the
    missing group's mix. Other upgrades are outcomes here, not matching variables. Timing, shop support,
    other Ancient tiers and first-access selection can still explain differences; this is not a causal effect.</p>'''
    body+=table(highlights,list(highlights[0]))
    body+='''<h3>Explore a checkpoint</h3><p>These filters apply to the inventory tables, support comparison and
    local power-rule evidence below. Compare sample sizes; sparse subgroups are descriptive only.</p>
    <p>Each card/relic cell in the combination tables shows <strong>median [10th–90th percentile]</strong>.
    Percentiles interpolate the observed counts; they are not confidence intervals. The extra-combination
    count is the number of distinct (card rewards, rare card rewards, relics) triples inside that same five-upgrade
    combination. It is sample-size dependent, not a new balance score.</p><p>Filter by character and starting status to separate genuine within-group
    variation from differences between characters or unlock timing. Small starting-character subgroups have fewer
    seeds; compare the displayed sample sizes. The tables show the 15 most common combinations; the CSV retains all.</p>
    <label>Checkpoint <select id="checkpoint">'''
    body+=''.join(f'<option>{html.escape(cp)}</option>' for cp in CHECKPOINTS)+'</select></label>'
    body+='<label>Character <select id="character"><option value="all">All characters</option>'
    body+=''.join(f'<option>{html.escape(c)}</option>' for c in sorted({r['character'] for r in rows}))+'</select></label>'
    body+='''<label>Starting status <select id="role"><option value="all">All</option>
    <option value="starting">Starting character</option><option value="locked">Initially locked</option></select></label>
    <div id="explorer"></div>
    <h3>What support accompanies a missing Ancient?</h3>
    <label>Ancient tier <select id="tier"><option value="1">1 — Neow</option><option value="2" selected>2</option>
    <option value="3">3</option></select></label><div id="ancient-explorer"></div>
    <p>The raw cohort table shows observed means. The comparison table uses only character × starting-status
    groups with both missing and present cases, and reweights present cases to the missing group's composition.
    Coverage reports the fraction of missing cases retained. A dash means no comparable group, not zero difference.
    Neither table treats the five characters of a seed as independent trials; no significance tests are reported.</p>
    <h3>Does that support cover the Ancient penalty in the logic?</h3>
    <p>Current-world only, among missing-tier cases with a <strong>positive Ancient support penalty</strong>.
    Each test removes one whole receipt category from the saved state and evaluates the native power formula.
    A strict hit means remaining power still meets the minimum-card rule and would pass without the Ancient
    penalty, but fails with it. That ties the removed contribution to the extra requirement within this formula.</p>
    <div id="evidence-explorer"></div>
    <p>Tests overlap and percentages must not be added. A failed strict test does not rule out compensation:
    removing a whole category may remove too much power to isolate the Ancient penalty. These are local formula
    probes, not regenerated routes, actual combat outcomes or evidence of intent by the fill algorithm.
    A penalty may cover multiple absent Ancients; it is not necessarily the selected tier's marginal cost.</p>
    <h2>Which combinations are shared?</h2>
    <p>These compare the sets observed in each version, not all theoretically legal states. A combination absent from
    one sample may be rare rather than impossible. Different item classifications and fill behavior can also affect
    inventories; this experiment isolates APWorld versions, not individual rule changes.</p>'''
    body+=table([r for r in overlaps if r['role']=='all'],['checkpoint','shared','only_release','only_current'])
    body+='''<h2>Interpretation and saved data</h2><p>There are 576 possible count tuples before considering logic
    restrictions (4 × 4 × 4 × 3 × 3). Many are not feasible at a particular checkpoint, so this is not a coverage target.
    More combinations does not by itself mean better balance, independent randomness, or fewer real-world out-of-logic
    checks. Generation reliability under randomized options is outside this fixed-YAML experiment.</p>
    <p>The original progression report remains unchanged. The database contains every placement, sphere inventory,
    checkpoint and native power breakdown, so other definitions of support can be queried later.</p><ul>'''
    body+=''.join(f'<li><a href="{name}.csv">{name}.csv</a></li>' for name,data in datasets.items() if data)
    body+='</ul><details><summary>Generation outcomes</summary>'+table(outcomes,['variant','status','runs'])+'</details>'
    # Only the top combinations enter the browser; complete frequencies remain in CSV.
    payload=json.dumps(dict(summary=summaries,combinations=[r for r in combinations if r['rank']<=15],
                           contrasts=contrasts,cohorts=cohorts,evidence=evidence)).replace('<','\\u003c')
    body+='<script type="application/json" id="data">'+payload+'</script>'
    body+='''<script>
    const data=JSON.parse(document.getElementById('data').textContent);
    const fields=['ancients','rests','smiths','starter_cards','starter_relics','count','percent','card_rewards','rare_cards','relics','card_relic_combinations'];
    const support=['card_rewards','rare_cards','relics','rests','smiths','starter_cards','starter_relics'];
    const number=v=>v===null||v===undefined?'—':typeof v==='number'&&!Number.isInteger(v)?v.toFixed(2):v;
    function makeTable(rows,cols){
      const wrap=document.createElement('div');wrap.className='scroll';
      const table=document.createElement('table');const head=table.createTHead().insertRow();
      for(const c of cols){const th=document.createElement('th');th.textContent=c.replaceAll('_',' ');head.append(th);}
      const body=table.createTBody();for(const r of rows){const tr=body.insertRow();for(const c of cols){const td=tr.insertCell();
        td.textContent=number(r[c]);}}
      wrap.append(table);return wrap;
    }
    function render(){
      const cp=document.getElementById('checkpoint').value,character=document.getElementById('character').value,role=document.getElementById('role').value;
      const target=document.getElementById('explorer');target.replaceChildren();
      const match=r=>r.checkpoint===cp&&r.character===character&&r.role===role;
      target.append(makeTable(data.summary.filter(match),['variant','seeds','observations','distinct','effective_combinations','most_common_percent','top_five_percent','singleton_combinations']));
      for(const variant of ['release-1.1.2','current']){const heading=document.createElement('h3');heading.textContent=variant;target.append(heading);
        const combos=data.combinations.filter(r=>match(r)&&r.variant===variant).map(r=>({...r,
          ...Object.fromEntries(['card_rewards','rare_cards','relics'].map(f=>[f,`${number(r[f+'_median'])} [${number(r[f+'_p10'])}–${number(r[f+'_p90'])}]`]))}));
        target.append(makeTable(combos,fields));}
      const tier=Number(document.getElementById('tier').value);
      const comparison=document.getElementById('ancient-explorer');comparison.replaceChildren();
      const probe=document.getElementById('evidence-explorer');probe.replaceChildren();
      for(const variant of ['release-1.1.2','current']){
        const selected=r=>match(r)&&r.variant===variant&&r.tier===tier;
        const heading=document.createElement('h4');heading.textContent=variant;comparison.append(heading);
        comparison.append(makeTable(data.cohorts.filter(selected),['status','observations','seeds',...support.map(f=>f+'_mean')]));
        const contrasts=data.contrasts.filter(selected),first=contrasts[0];
        if(first){const note=document.createElement('p');
          note.textContent=`Comparable groups: ${first.comparable_strata}; retained missing: ${first.matched_missing} observations / ${first.missing_seeds} seeds; present: ${first.matched_present} observations / ${first.present_seeds} seeds; missing coverage: ${number(first.missing_coverage_percent)}%.`;
          comparison.append(note);}
        comparison.append(makeTable(contrasts,['resource','missing_mean','standardized_present_mean','difference']));
      }
      const tests=data.evidence.filter(r=>match(r)&&r.tier===tier);
      if(tests.length)probe.append(makeTable(tests,['source','missing_observations','positive_penalty_observations','seeds','ancient_specific_observations','ancient_specific_percent']));
      else probe.textContent='No positive-Ancient-penalty cases in the selected current-world group.';
    }
    for(const id of ['checkpoint','character','role','tier'])document.getElementById(id).addEventListener('change',render);
    document.getElementById('checkpoint').value='Early Act 2';render();
    </script></html>'''
    (output/'report.html').write_text(body)
    provenance=dict(database=str(args.database.resolve()),metadata=metadata,
        analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),matplotlib_version=matplotlib.__version__)
    (output/'provenance.json').write_text(json.dumps(provenance,indent=2))
    print(json.dumps(totals))
    print(json.dumps(comparison,indent=2))
    print(f'Report: {output / "report.html"}')


if __name__=='__main__':
    main()
