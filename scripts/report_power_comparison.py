#!/usr/bin/env python3
"""Rescore both worlds' first-access inventories with the frozen current power rules."""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import html
import json
import math
import os
from pathlib import Path
import sqlite3
import statistics
import sys
import zipfile

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scripts.progression_stats import power_rules
from scripts.report_support_diversity import CHECKPOINTS, VARIANTS, distribution, group_rows, table, write_csv


class ReceiptState:
    """Read-only received-item counts; no reachability caches or world mutation."""
    def __init__(self, inventory):
        self.inventory = inventory

    def count(self, item, player):
        assert player == 1
        return self.inventory.get(item, 0)

    def has(self, item, player, count=1):
        return self.count(item, player) >= count


def reference_rules(root, options, metadata, output):
    runtime = (root/'runtime/current').resolve()
    archive = root/'inputs/current-source.zip'
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == metadata['current_source_sha256']
    with zipfile.ZipFile(archive) as source:
        for name in source.namelist():
            if name.startswith('world/spire2/') and name.endswith('.py'):
                assert source.read(name) == (runtime/'worlds'/name.removeprefix('world/')).read_bytes(), name
    sys.path.insert(0, str(runtime))
    os.environ['SKIP_REQUIREMENTS_UPDATE'] = 'true'
    import Utils
    userdata = (output/'userdata').resolve()
    userdata.mkdir(exist_ok=True)
    Utils.user_path.cached_path = str(userdata)
    from BaseClasses import MultiWorld
    from worlds.spire2.world import SlayTheSpire2World
    from worlds.spire2.rules import SpireHasPower
    import worlds.spire2.rules as rules_module
    assert Path(rules_module.__file__).resolve() == runtime/'worlds/spire2/rules.py'
    mw = MultiWorld(1)
    mw.game[1] = SlayTheSpire2World.game
    mw.player_name = {1: 'Power comparison'}
    mw.set_seed(0)
    args = argparse.Namespace(**{name: {1: cls.from_any(options.get(name, cls.default))}
        for name,cls in SlayTheSpire2World.options_dataclass.type_hints.items()})
    mw.set_options(args)
    world = mw.worlds[1]
    world.generate_early()
    world.create_regions()
    world.set_rules()
    rules = {}
    for character in world.characters:
        for checkpoint in CHECKPOINTS:
            matches = list(power_rules(world.get_entrance(f'{character.name} {checkpoint}').access_rule,
                                       SpireHasPower.Resolved))
            assert len(matches) == (0 if checkpoint == 'Early Act 1' else 1)
            # Early Act 1 has only an unlock rule. Use native card/relic scoring
            # with zero expected vanilla cards, but do not invent a power gate.
            rule = matches[0] if matches else SpireHasPower(character.char_offset,0).resolve(world)
            rules[(character.name,checkpoint)] = (rule,bool(matches))
    return rules


def score_inventory(rule, has_gate, inventory):
    state = ReceiptState(inventory)
    cards,power,required = rule.strength(state)
    adjustments = rule.power_adjustments(state) if has_gate else {}
    adjustment = sum(adjustments.values()) if has_gate else None
    return dict(cards=cards,power=power,has_power_gate=int(has_gate),
        base_required=rule.power_level if has_gate else None,
        required=required if has_gate else None,minimum_cards=rule.minimum_cards if has_gate else None,
        adjustment=adjustment,adjusted_power=power-adjustment if has_gate else None,
        margin=power-required if has_gate else None,
        passes_power_rule=int(cards>=rule.minimum_cards and power>=required) if has_gate else None,
        adjustments=json.dumps(adjustments,sort_keys=True))


def summarize(rows):
    summaries=[]
    for key,group in sorted(group_rows(rows).items()):
        result=dict(zip(('variant','checkpoint','character','role'),key))
        result.update(observations=len(group),seeds=len({r['seed'] for r in group}),
                      has_power_gate=group[0]['has_power_gate'])
        for field in ('cards','power','required','adjustment','adjusted_power','margin'):
            values=[r[field] for r in group if r[field] is not None]
            result.update({f'{field}_{s}':v for s,v in (distribution(values).items() if values else
                dict.fromkeys(('mean','p10','median','p90','minimum','maximum')).items())})
        result['base_required']=group[0]['base_required']
        result['passes_power_rule_percent']=100*statistics.mean(r['passes_power_rule'] for r in group) if result['has_power_gate'] else None
        summaries.append(result)
    return summaries


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('database',type=Path)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    root=args.database.resolve().parent
    output=(args.output or root/'power-report').resolve()
    output.mkdir(parents=True,exist_ok=True)
    with sqlite3.connect(args.database.resolve().as_uri()+'?mode=ro',uri=True) as db:
        db.row_factory=sqlite3.Row
        metadata={r['key']:json.loads(r['value']) for r in db.execute('select * from metadata')}
        options={json.dumps(json.loads(r['details'])['resolved_options'],sort_keys=True)
                 for r in db.execute("select details from runs where variant='current' and status='success'")}
        assert len(options)==1, 'This report requires a fixed current-world option set'
        rules=reference_rules(root,json.loads(next(iter(options))),metadata,output)
        checkpoints=defaultdict(list)
        for r in db.execute('select * from first_checkpoints'):
            checkpoints[(r['variant'],r['seed'])].append(dict(r))
        converted=[]
        validated=0
        for (variant,seed),group in sorted(checkpoints.items()):
            inventories=defaultdict(dict)
            for row in db.execute('select sphere,item,count from inventory where variant=? and seed=?',(variant,seed)):
                inventories[row['sphere']][row['item']]=row['count']
            for row in group:
                rule,has_gate=rules[(row['character'],row['checkpoint'])]
                score=score_inventory(rule,has_gate,inventories[row['sphere']])
                if variant=='current' and has_gate:
                    for field in ('cards','power','required','minimum_cards'):
                        assert math.isclose(score[field],row[field],abs_tol=1e-9), (seed,row['character'],row['checkpoint'],field)
                    assert json.loads(score['adjustments'])==json.loads(row['adjustments'])
                    assert score['passes_power_rule']==1
                    validated+=1
                converted.append(dict(variant=variant,seed=seed,character=row['character'],
                    checkpoint=row['checkpoint'],sphere=row['sphere'],
                    role='starting' if row['character']==row['starting_character'] else 'locked',**score))
    summaries=summarize(converted)
    write_csv(output/'checkpoint_power.csv',converted)
    write_csv(output/'summary.csv',summaries)
    with sqlite3.connect(output/'power_comparison.sqlite') as db:
        db.execute('drop table if exists checkpoint_power')
        types={key:('TEXT' if isinstance(value,str) else 'INTEGER' if isinstance(value,int) else 'REAL')
               for key,value in converted[0].items()}
        db.execute('create table checkpoint_power ('+','.join(f'{key} {kind}' for key,kind in types.items())+', primary key(variant,seed,character,checkpoint))')
        db.executemany('insert into checkpoint_power values ('+','.join('?' for _ in types)+')',
                       [tuple(r.values()) for r in converted])
        db.execute('create table if not exists metadata(key TEXT PRIMARY KEY,value TEXT)')
        db.execute('delete from metadata')
        db.executemany('insert into metadata values (?,?)',[(k,json.dumps(v)) for k,v in metadata.items()])
    lookup={(r['variant'],r['checkpoint'],r['character'],r['role']):r for r in summaries}
    comparison=[]
    for cp in CHECKPOINTS:
        old,new=(lookup[v,cp,'all','all'] for v in VARIANTS)
        comparison.append(dict(checkpoint=cp,release_power=old['power_mean'],current_power=new['power_mean'],
            release_adjusted_power=old['adjusted_power_mean'],current_adjusted_power=new['adjusted_power_mean'],
            current_base_requirement=new['base_required'],
            release_passes_current_power_rule_percent=old['passes_power_rule_percent']))
    write_csv(output/'comparison.csv',comparison)
    os.environ.setdefault('MPLCONFIGDIR',str(output/'.matplotlib'))
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig,axes=plt.subplots(1,2,figsize=(15,6),layout='constrained')
    for ax,metric,title in zip(axes,('power','adjusted_power'),('Card/relic power on the current scale','Power after subtracting support adjustments')):
        cps=CHECKPOINTS if metric=='power' else CHECKPOINTS[1:]
        for variant,color,label in zip(VARIANTS,('#7a8699','#147d92'),('Release 1.1.2 inventories','Current inventories')):
            sample=[lookup[variant,cp,'all','all'] for cp in cps]
            ax.plot(range(len(cps)),[r[f'{metric}_mean'] for r in sample],marker='o',color=color,label=label)
            ax.fill_between(range(len(cps)),[r[f'{metric}_p10'] for r in sample],
                            [r[f'{metric}_p90'] for r in sample],color=color,alpha=.12)
        if metric=='adjusted_power':
            ax.plot(range(len(cps)),[lookup['current',cp,'all','all']['base_required'] for cp in cps],
                    color='#a65031',linestyle='--',label='Current base requirement')
        ax.set_xticks(range(len(cps)),[cp.replace('Boss Arena','boss') for cp in cps],rotation=50,ha='right')
        ax.set_title(title);ax.set_ylabel('Current-formula power');ax.grid(alpha=.2);ax.legend(fontsize=8)
    fig.savefig(output/'power-comparison.png',dpi=160)
    plt.close(fig)
    title='Power at first region access: both worlds on the current scale'
    body=f'''<!doctype html><html lang="en"><meta charset="utf-8"><title>{title}</title>
    <style>body{{font:16px/1.5 system-ui;max-width:1350px;margin:36px auto;padding:0 24px;color:#192a38}}
    table{{border-collapse:collapse;font-size:13px;width:100%;font-variant-numeric:tabular-nums}}th,td{{padding:8px;border-bottom:1px solid #dce3e8;text-align:left;white-space:nowrap}}
    th{{background:#eef3f6}}.scroll{{overflow:auto;margin:20px 0}}img{{width:100%}}select{{font:inherit;padding:6px;margin:8px 14px}}a{{color:#147d92}}</style>
    <h1>{title}</h1><p>Both versions' recorded inventories are evaluated by the exact frozen current-world rules
    from commit <code>{html.escape(metadata['current_commit'][:12])}</code>. Each observation is taken before collecting
    the sphere in which that character's checkpoint first becomes logically reachable in its <em>original</em> world.
    “Expected” here means the observed mean across seeds, not a fixed minimum or a combat prediction.</p>
    <p>Each version contributes {lookup['current','Mid Act 1','all','all']['seeds']:,} seeds and
    {lookup['current','Mid Act 1','all','all']['observations']:,} character observations per checkpoint.
    This rescoring uses existing runs; no new placements or routes were generated.</p>
    <ul><li><strong>Power</strong>: current weights for ordinary/rare cards, relics, starter tiers and Necrobinder's starter synergy.</li>
    <li><strong>Required power</strong>: current base requirement plus that inventory's adjustments for Ancients, Rests, Smiths,
    shop removals and shop choices. Early later-tier Ancients can make an adjustment negative in Anytime mode.</li>
    <li><strong>Adjusted power</strong> = power − total support adjustments. This puts support on the supply side;
    compare it with the current base requirement. It is an algebraic presentation of the rule, not a separate game stat.</li>
    <li><strong>Margin</strong> = power − required power. Passing also requires the current minimum card strength.</li></ul>
    <p>Early Act 1 has only a character-unlock rule: card/relic power can be scored, but a requirement, margin or pass rate
    would invent a gate. Those entries are unavailable. Shops are separate gold gates, outside the 12 progression checkpoints here.</p>
    <img src="power-comparison.png" alt="Mean power and adjusted power for both versions, with observed 10th to 90th percentile bands">
    <p>Lines show means; shading is the observed 10th–90th percentile range, not a confidence interval.
    Both versions include all five characters. Starting versus initially locked characters can arrive with very different inventories.</p>'''
    old,new=(lookup[v,'Early Act 3','all','all'] for v in VARIANTS)
    mid_old=lookup['release-1.1.2','Mid Act 2','all','all']
    body+=f'''<h2>Main observations</h2><p>At Early Act 3, release inventories score
    <strong>{old['power_mean']:.2f}</strong> card/relic power versus <strong>{new['power_mean']:.2f}</strong>
    for current inventories. After support adjustments, that becomes
    <strong>{old['adjusted_power_mean']:.2f} versus {new['adjusted_power_mean']:.2f}</strong> against a
    base requirement of {new['base_required']:g}. Average surplus above the full requirement is therefore
    <strong>{old['margin_mean']:.2f} versus {new['margin_mean']:.2f}</strong>.
    The old first-access inventories carry more surplus under the current model at this checkpoint.</p>
    <p>The current formula is not uniformly easier for every old inventory: at Mid Act 2,
    <strong>{100-mid_old['passes_power_rule_percent']:.2f}%</strong> of the old first-access inventories
    fail its local power/card-strength test. These are formula comparisons, not proof of actual game difficulty
    or complete-route reachability.</p>'''
    body+=table([{k:('—' if v is None else v) for k,v in row.items()} for row in comparison],list(comparison[0]))
    body+='''<h2>Compare a character or starting status</h2><label>Character <select id="character"><option value="all">All characters</option>'''
    body+=''.join(f'<option>{html.escape(c)}</option>' for c in sorted({r['character'] for r in converted}))
    body+='''</select></label><label>Starting status <select id="role"><option value="all">All</option>
    <option value="starting">Starting character</option><option value="locked">Initially locked</option></select></label>
    <div id="explorer"></div><h2>What this comparison establishes</h2>
    <p>A converted release inventory can fail the current local power rule even though its old checkpoint was reachable.
    Conversely, passing this one rule does not prove the whole current route is reachable: earlier regions, unlocks
    and separate hard gates are not replayed. The versions' original first-access times are intentionally preserved.</p>
    <p>Higher average power at first access can reflect later access or extra inventory acquired while waiting on another
    gate. It does not by itself imply stronger requirements, better combat balance or more deliberate support allocation.
    Starting-character identities may differ between versions for the same seed; use the character/status filters to
    inspect the groups and their sizes. No significance tests assume independent characters within a seed.</p>
    <p>The old native power score is never multiplied by a conversion factor. Each old receipt is revalued directly,
    including shop and removal counts from the full saved inventory. Only the current formula's modeled resources
    contribute; actual deck choices, relic identities, potions, filler and player skill remain outside this model.</p>'''
    body+=f'''<p>Validation: rescoring reproduced cards, power, requirement, minimum-card threshold and every support adjustment
    for all <strong>{validated:,}</strong> recorded current-world checkpoints with a power gate.</p>
    <p><a href="checkpoint_power.csv">Every converted observation (CSV)</a> · <a href="summary.csv">All grouped distributions (CSV)</a> ·
    <a href="comparison.csv">Overview (CSV)</a> · <a href="power_comparison.sqlite">Queryable converted data (SQLite)</a> ·
    <a href="provenance.json">Provenance</a></p>'''
    payload=json.dumps(dict(summary=summaries,checkpoints=CHECKPOINTS)).replace('<','\\u003c')
    body+='<script type="application/json" id="data">'+payload+'</script>'
    body+='''<script>
    const data=JSON.parse(document.getElementById('data').textContent);
    function render(){
      const target=document.getElementById('explorer');target.replaceChildren();
      const character=document.getElementById('character').value,role=document.getElementById('role').value;
      for(const variant of ['release-1.1.2','current']){
        const h=document.createElement('h3');h.textContent=variant;target.append(h);
        const wrap=document.createElement('div');wrap.className='scroll';const table=document.createElement('table');
        const cols=['checkpoint','seeds','observations','power_mean','power_p10','power_p90','required_mean','adjusted_power_mean','margin_mean','passes_power_rule_percent'];
        const head=table.createTHead().insertRow();for(const c of cols){const th=document.createElement('th');th.textContent=c.replaceAll('_',' ');head.append(th);}
        const body=table.createTBody();for(const cp of data.checkpoints){
          const row=data.summary.find(r=>r.variant===variant&&r.character===character&&r.role===role&&r.checkpoint===cp);
          if(!row)continue;const tr=body.insertRow();for(const c of cols){const td=tr.insertCell();td.textContent=row[c]===null?'—':typeof row[c]==='number'?Number(row[c].toFixed(2)):row[c];}}
        wrap.append(table);target.append(wrap);
      }
    }
    for(const id of ['character','role'])document.getElementById(id).addEventListener('change',render);render();
    </script></html>'''
    (output/'report.html').write_text(body)
    provenance=dict(source_database=str(args.database.resolve()),metadata=metadata,
        analysis_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        helper_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in ('progression_stats.py','report_support_diversity.py')},
        validated_current_power_checkpoints=validated,observations=len(converted))
    (output/'provenance.json').write_text(json.dumps(provenance,indent=2))
    print(json.dumps(comparison,indent=2))
    print(f'Report: {output / "report.html"}')


if __name__=='__main__':
    main()
