# -*- coding: utf-8 -*-
"""A wall SPLIT with doors and windows on it, end to end through the bridge's public tools only.

    python split_openings.py build   <document>    new model, SPLIT-A linked, the wall built from it, TEST
                                                    door and window families, four openings placed
    python split_openings.py run     <document>    rollback probe, repoint to SPLIT-B, plan, an impossible
                                                    "stay" refused, the decided deletes, apply, read back,
                                                    replan, save / close / reopen
    python split_openings.py recover <document>    after a run whose apply was cut by a fault (Revit started
                                                    with HORIZUN_TEST_FAIL_ACTION): plan again, apply, the
                                                    same checks as a clean run

The drawings and their truth come from synthetic/make_split_dwgs.py; the machine config is the same
as run_spec.py's (%USERPROFILE%\\.horizun\\dwg-bim-run.json) with the drawing names "split-A" and
"split-B", and optionally "revit_templates": {"<year>": "<.rte>"} for more than one Revit year.
Every decision here is a TEST RULE written below, never a project decision:
    D-OFF  first decided "stay" (must be REFUSED: a door does not stand on the kept piece), then "delete"
    W-GAP  "delete"
Writes <records>/<utc>-<document>-<mode>/result.json and prints its path.
"""
import datetime
import io
import json
import math
import os
import sys

import run_spec as RS
import session_hooks

MM = 25.4
FAMILIES = {'door': ('Door.rft', 'HZ-TEST Door', 'OST_Doors'), 'window': ('Window.rft', 'HZ-TEST Window', 'OST_Windows')}
NOTICE = 'HZ-TEST generic opening: not a project family; its size is the template default.'
DECISIONS_VERSION = 'split-openings-test-1.0.0'
TOL_MM = 5.0
# the Mark by its BuiltInParameter: a parameter's NAME is translated with the interface ("Marca")
MARK = 'ALL_MODEL_MARK'


def walls_set():
    return {
        'schema': 'horizun.cad-requirements/1',
        'requirement_set': {'id': 'synthetic-split-walls', 'version': '1.0.0',
                            'title': 'synthetic split fixture - one 8 in wall, one listed type'},
        'source': {'units': 'millimeter', 'role': 'floor_plan',
                   'extent_mm': {'min_x': -300.0, 'min_y': -300.0, 'max_x': 6500.0, 'max_y': 600.0, 'crossing': 'whole'}},
        'tolerances': {'point_mm': 25.0, 'gap_mm': 150.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0,
                       'wall_overlap_mm': 1.0, 'revision_compare_mm': 25.0, 'thickness_mm': 3.2},
        'rules': [{'id': 'r-wall', 'precedence': 10, 'discipline': 'architecture', 'layers': ['A-WALL'],
                   'produces': 'wall', 'family_type': 'Basic Wall: Generic - 8"', 'level': 'Level 1',
                   'height_mm': 2700.0, 'join_rule': 'none',
                   'geometry': {'from': 'double_lines', 'min_thickness_mm': 60.0, 'max_thickness_mm': 400.0,
                                'min_overlap_mm': 200.0},
                   'wall_types': {'types': ['Basic Wall: Generic - 8"'], 'tolerance_mm': 3.2, 'otherwise': 'withdraw'}}]}


class Session:
    def __init__(self, doc, mode, config=None):
        self.cfg = RS.load(config or os.path.join(os.environ['USERPROFILE'], '.horizun', 'dwg-bim-run.json'))
        server = io.open(os.path.expandvars(self.cfg['server_exe_file'].replace('%TEMP%', os.environ['TEMP'])),
                         encoding='utf-8-sig').read().strip()
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        self.folder = os.path.join(self.cfg['records'], '%s-%s-%s' % (stamp, doc, mode))
        self.call = RS.Calls(self.folder, server)
        self.doc = doc
        _, h = self.call('horizun_health', {}, 'health')
        self.year = str(h.get('revit_version'))
        self.build = {'version': h.get('horizun_version'), 'commit': h.get('horizun_commit'),
                      'contract_hash': h.get('contract_hash'), 'revit_version': self.year,
                      'revit_build': h.get('revit_build'), 'open_documents': h.get('open_documents'),
                      'process_id': h.get('process_id'), 'staged': session_hooks.staged_build(h)}
        self.truth = RS.load(os.path.join(os.path.dirname(self.cfg['drawings']['split-A']), 'SPLIT.truth.json'))

    def save(self, out):
        out['build'] = self.build
        path = os.path.join(self.folder, 'result.json')
        io.open(path, 'w', encoding='utf-8').write(json.dumps(out, indent=1, default=str))
        print(path)
        print(json.dumps(out.get('verdict'), default=str))


def value(p):
    return p.get('display') or p.get('raw') or p.get('value') if isinstance(p, dict) else p


def openings(s, name):
    _, q = s.call('horizun_query_model', {'target_document': s.doc, 'response_mode': 'full', 'max_rows': 50,
                                          'return_parameters': [MARK], 'include_orientation': True,
                                          'categories': ['OST_Doors', 'OST_Windows']}, name)
    out = {}
    for r in q.get('rows', []):
        if r.get('is_element_type'):
            continue
        p = r.get('placement') or {}
        out[value((r.get('parameters') or {}).get(MARK)) or str(r['element_id'])] = {
            'id': r['element_id'], 'host': r.get('host_id') or p.get('host_id'), 'point': p.get('point'),
            'category': r.get('category')}
    return out


def walls(s, name):
    _, q = s.call('horizun_query_model', {'target_document': s.doc, 'response_mode': 'full', 'max_rows': 50,
                                          'include_orientation': True, 'categories': ['OST_Walls']}, name)
    out = {}
    for r in q.get('rows', []):
        if r.get('is_element_type'):
            continue
        p = r.get('placement') or {}
        xs = sorted([round(p['start'][0], 1), round(p['end'][0], 1)]) if p.get('start') else None
        out[r['element_id']] = {'x_mm': xs, 'level_id': r.get('level_id') or p.get('level_id')}
    return out


def refused(reply):
    return (reply.get('raw') or '')[:1200] if reply.get('is_error') else None


# --------------------------------------------------------------------------------------------- build
def build(doc):
    s = Session(doc, 'build')
    tmpl = (s.cfg.get('revit_templates') or {}).get(s.year)
    if tmpl:
        s.cfg['revit_template'] = tmpl
    spec = {'level': 'Level 1', 'drawing': {'name': 'split-A', 'units': 'inch'}}
    out = {'mode': 'build', 'document': doc, 'template': s.cfg['revit_template']}
    rvt, dwg, link = RS.model(s.call, s.cfg, spec, doc)
    out.update(link=link, dwg=dwg)
    rules = walls_set()
    _, plan = s.call('horizun_plan_from_cad', {'instance_id': link, 'target_document': doc, 'requirement_set': rules,
                                               'level_name': 'Level 1', 'dwg_path': dwg}, 'walls-plan')
    args = {'target_document': doc, 'instance_id': link, 'requirement_set': rules,
            'apply_binding': plan['apply_binding'], 'actions': plan['execute_plan_request']['actions'],
            'candidate_index': plan['candidate_index'], 'dry_run': True}
    _, dry = s.call('horizun_apply_cad_plan', args, 'walls-apply-dry')
    tokens = (dry.get('rehearsal') or {}).get('tokens_by_key') or {}
    for a in args['actions']:
        if tokens.get(a['key']):
            a['arguments']['confirmation_token'] = tokens[a['key']]
    args['dry_run'] = False
    reply, built = s.call('horizun_apply_cad_plan', args, 'walls-apply', key=True)
    out['walls_built'] = {'error': refused(reply), 'created_verified': built.get('created_verified')}
    w = walls(s, 'walls')
    if len(w) != 1:
        raise SystemExit('expected ONE wall, read %s' % w)
    wall_id = next(iter(w))
    _, lv = s.call('horizun_query_model', {'target_document': doc, 'response_mode': 'full', 'max_rows': 20,
                                           'categories': ['OST_Levels']}, 'levels')
    level = next((r['element_id'] for r in lv.get('rows', []) if (r.get('name') or r.get('type')) == 'Level 1'), None)
    if level is None:
        raise SystemExit('no Level 1: %s' % [r.get('name') for r in lv.get('rows', [])])
    fam_dir = os.path.join(s.cfg['model_dir'], 'families-' + s.year)
    os.makedirs(fam_dir, exist_ok=True)
    tpl_dir = 'C:/ProgramData/Autodesk/RVT %s/Family Templates/English-Imperial/' % s.year
    types = {}
    for kind, (tpl, family, cat) in FAMILIES.items():
        rfa = os.path.join(fam_dir, family + '.rfa').replace('\\', '/')
        fargs = ({'target_document': doc, 'source_path': rfa} if os.path.exists(rfa) else
                 {'target_document': doc, 'template_path': tpl_dir + tpl, 'output_path': rfa, 'units': 'mm',
                  'parameters': [{'name': 'HZ_TEST_NOTICE', 'data_type': 'text', 'group': 'identity_data'}],
                  'types': [{'name': 'Test', 'values': {'HZ_TEST_NOTICE': NOTICE}}],
                  'forms': [{'kind': 'extrusion', 'plane': 'xy', 'key': 'marker', 'depth': 1500,
                             'profile': [[[-300, -30, 0], [300, -30, 0], [300, 30, 0], [-300, 30, 0]]]}],
                  'overwrite': False, 'load_into_project': True})
        reply, res = s.call.confirmed('horizun_create_family', fargs, 'family-' + kind)
        if reply.get('is_error'):
            raise SystemExit('%s family refused: %s' % (kind, refused(reply)))
        _, q = s.call('horizun_query_model', {'target_document': doc, 'include_types': True, 'response_mode': 'full',
                                              'max_rows': 100, 'categories': [cat]}, 'types-' + kind)
        t = [r for r in q.get('rows', []) if r.get('is_element_type') and r.get('family') == family]
        if not t:
            raise SystemExit('no %s type after loading' % family)
        types[kind] = t[0]['element_id']
    rows = [{'kind': 'family_instance', 'coordinate_mode': 'absolute', 'type_id': types[o['kind']], 'host_id': wall_id,
             'level_id': level, 'point': [o['x_in'] * MM, s.truth['wall']['centre_y_in'] * MM, 0.0],
             'parameters': {MARK: o['id']}} for o in s.truth['openings']]
    reply, made = s.call.confirmed('horizun_create_elements', {'target_document': doc, 'units': 'mm', 'elements': rows},
                                   'openings')
    out['openings_created'] = {'error': refused(reply), 'rows': len(made.get('rows') or [])}
    out['openings'] = openings(s, 'openings-read')
    out['walls'] = walls(s, 'walls-read')
    s.call('horizun_save_document', {'target_document': doc}, 'save')
    out['verdict'] = {'one_wall': len(out['walls']) == 1,
                      'four_openings_on_it': len(out['openings']) == 4 and
                      all(o['host'] == wall_id for o in out['openings'].values())}
    s.save(out)


# ------------------------------------------------------------------------------------------- update
def activate(s):
    _, opened = s.call('horizun_open_document', {'path': os.path.join(s.cfg['model_dir'], s.doc + '.rvt')}, 'open')
    active = opened.get('active_document')
    if (active.get('title') if isinstance(active, dict) else active) != s.doc:
        raise SystemExit('%s is not the active document' % s.doc)


def link_id(s):
    _, q = s.call('horizun_manage_cad_links', {'operation': 'list', 'target_document': s.doc}, 'links', key=False)
    links = q.get('cad_instances') or []
    ids = [l.get('element_id') or l.get('instance_id') for l in links]
    if len(ids) != 1:
        raise SystemExit('expected one CAD link: %s' % json.dumps(q)[:400])
    return ids[0], links[0]


def plan(s, link, name, accept=None, decisions=None):
    args = {'instance_id': link, 'target_document': s.doc, 'requirement_set': walls_set(), 'level_name': 'Level 1',
            'dwg_path': s.cfg['drawings']['split-B'], 'supersedes_sha256': [s.truth['drawings']['SPLIT-A']['sha256']]}
    if accept:
        args['accept_pairings'] = accept
    if decisions is not None:
        args['dependent_decisions'] = decisions
    return s.call('horizun_plan_cad_update', args, name)


def held_openings(p):
    out = {}
    for a in p.get('plan', []):
        for d in (a.get('evidence') or {}).get('split_dependents') or []:
            out[d.get('element_id')] = {k: d.get(k) for k in ('class', 'alternatives', 'decision_key', 'category')}
    return out


def apply(s, p, name):
    args = {'target_document': s.doc, 'actions': p.get('actions') or [], 'candidate_index': p['candidate_index'],
            'provenance': p['provenance'], 'dry_run': True}
    _, dry = s.call('horizun_apply_cad_update', args, name + '-dry')
    args['dry_run'] = False
    reply, done = s.call('horizun_apply_cad_update', args, name, key=True)
    return {'error': refused(reply), 'state': done.get('state'),
            'actions': [(a.get('key'), a.get('ok'), (a.get('error') or '')[:300] or None) for a in done.get('actions') or []]}


def final_checks(s, out, before):
    """What a completed split must show, read back from the model; then replan, save, close, reopen."""
    kept_x = [v * MM for v in s.truth['pieces_in_B']['kept']]
    new_x = [v * MM for v in s.truth['pieces_in_B']['new']]
    o, w = openings(s, 'openings-after'), walls(s, 'walls-after')
    out['after'] = {'openings': o, 'walls': w}
    wall_id = before['D-KEEP']['host']
    out['verdict'].update({
        'kept_wall_same_id_on_kept_piece': w.get(wall_id, {}).get('x_mm') is not None and
        all(abs(a - b) <= TOL_MM for a, b in zip(w[wall_id]['x_mm'], kept_x)),
        'new_piece_built': any(v['x_mm'] and all(abs(a - b) <= TOL_MM for a, b in zip(v['x_mm'], new_x))
                               for k, v in w.items() if k != wall_id),
        'two_walls': len(w) == 2,
        'd_keep_and_w_keep_same_id_host_point': all(
            o.get(m, {}).get('id') == before[m]['id'] and o.get(m, {}).get('host') == wall_id and
            o[m].get('point') and before[m].get('point') and
            math.dist(o[m]['point'][:2], before[m]['point'][:2]) <= TOL_MM for m in ('D-KEEP', 'W-KEEP')),
        'd_off_and_w_gap_gone': 'D-OFF' not in o and 'W-GAP' not in o})
    link, _ = link_id(s)
    reply, again = plan(s, link, 'replan')
    held = [a for a in again.get('plan', []) if not a.get('automatic') and a.get('kind') not in ('unchanged', 'leave')]
    out['replan'] = {'error': refused(reply), 'actions': [a.get('key') for a in again.get('actions') or []],
                     'held': [{k: a.get(k) for k in ('element_id', 'kind', 'classification')} for a in held],
                     'pairings_offered': len(again.get('pairings_offered') or [])}
    out['verdict']['replan_clean'] = not reply.get('is_error') and not out['replan']['actions'] and not held
    s.call('horizun_save_document', {'target_document': s.doc}, 'save')
    s.call('horizun_document_session', {'operation': 'close', 'target_document': s.doc}, 'close')
    activate(s)
    o2, w2 = openings(s, 'openings-reopened'), walls(s, 'walls-reopened')
    out['reopened'] = {'openings': o2, 'walls': w2}
    out['verdict']['reopen_identical'] = o2 == o and w2 == w


def run(doc):
    s = Session(doc, 'run')
    out = {'mode': 'run', 'document': doc, 'decisions_version': DECISIONS_VERSION,
           'fault': os.environ.get('HORIZUN_TEST_FAIL_ACTION'), 'verdict': {}}
    activate(s)
    before = openings(s, 'openings-before')
    wall_before = walls(s, 'walls-before')
    out['before'] = {'openings': before, 'walls': wall_before}
    wall_id = before['D-KEEP']['host']
    # 1. the re-shape Revit cannot do with D-OFF standing on the stretch it removes: refused WITH Revit's reason
    kept = [v * MM for v in s.truth['pieces_in_B']['kept']]
    y = s.truth['wall']['centre_y_in'] * MM
    probe = {'target_document': doc, 'units': 'mm', 'operations': [
        {'operation': 'set_curve', 'element_ids': [wall_id], 'start': [kept[0], y, 0.0], 'end': [kept[1], y, 0.0]}]}
    _, pdry = s.call('horizun_transform_elements', dict(probe, dry_run=True), 'probe-dry')
    reply, _ = s.call('horizun_transform_elements', dict(probe, dry_run=False,
                                                         confirmation_token=pdry.get('confirmation_token')), 'probe', key=True)
    after_probe = openings(s, 'openings-after-probe')
    out['rollback_probe'] = {'refusal': refused(reply)}
    out['verdict']['rollback_refused_with_revits_reason'] = bool(reply.get('is_error')) and 'Revit said:' in (reply.get('raw') or '')
    out['verdict']['rollback_wrote_nothing'] = after_probe == before and walls(s, 'walls-after-probe') == wall_before
    # 2. the revision, planned
    link, _ = link_id(s)
    rep = {'operation': 'repoint', 'target_document': doc, 'instance_id': link,
           'file_path': s.cfg['drawings']['split-B'].replace('/', '\\')}
    reply, _ = s.call.confirmed('horizun_manage_cad_links', rep, 'repoint')
    if reply.get('is_error'):
        raise SystemExit('repoint refused: %s' % refused(reply))
    _, first = plan(s, link, 'plan')
    accept = [{'element_id': p['element_id'], 'candidate_id': p['candidate_id']} for p in first.get('pairings_offered') or []]
    _, proposal = plan(s, link, 'plan-accepted', accept=accept)
    held = held_openings(proposal)
    by_mark = {m: before[m]['id'] for m in before}
    out['proposal'] = {'accept': accept, 'held': held,
                       'splits': [{k: x.get(k) for k in ('element_id', 'automatic', 'held')} for x in proposal.get('splits') or []]}
    key = lambda m: (held.get(by_mark[m]) or {}).get('decision_key')
    # 3. an impossible "stay" is refused in the PLAN
    first_try = [{'element_id': by_mark['D-OFF'], 'decision': 'stay', 'decision_key': key('D-OFF')},
                 {'element_id': by_mark['W-GAP'], 'decision': 'delete', 'decision_key': key('W-GAP')}]
    reply, _ = plan(s, link, 'plan-stay-refused', accept=accept, decisions=first_try)
    out['stay_refusal'] = refused(reply)
    out['verdict']['impossible_stay_refused_in_plan'] = bool(reply.get('is_error')) and \
        ('cannot_move_an_opening: element %d ' % by_mark['D-OFF']) in (reply.get('raw') or '')
    # 4. decided deletes, emitted BEFORE the re-shape
    decisions = [dict(first_try[0], decision='delete'), first_try[1]]
    reply, decided = plan(s, link, 'plan-decided', accept=accept, decisions=decisions)
    keys = [a.get('key') for a in decided.get('actions') or []]
    out['decided'] = {'error': refused(reply), 'actions': keys, 'decisions': decisions}
    deletes = [i for i, k in enumerate(keys) if k.startswith('cad-update-dependent-delete-')]
    moves = [i for i, k in enumerate(keys) if k.startswith('cad-update-move-')]
    out['verdict']['deletes_before_reshape'] = bool(deletes) and bool(moves) and max(deletes) < min(moves)
    out['apply'] = apply(s, decided, 'apply')
    if out['fault']:
        # 5a. cut between the deletes and the re-shape: what landed, read back
        o, w = openings(s, 'openings-partial'), walls(s, 'walls-partial')
        out['partial'] = {'openings': o, 'walls': w}
        out['verdict'].update({
            'apply_partial': out['apply']['state'] == 'partial',
            'partial_deletes_landed': 'D-OFF' not in o and 'W-GAP' not in o,
            'partial_wall_untouched': w == wall_before,
            'partial_kept_openings_intact': all(o.get(m) == before[m] for m in ('D-KEEP', 'W-KEEP'))})
        s.call('horizun_save_document', {'target_document': doc}, 'save')
    else:
        out['verdict']['apply_whole'] = out['apply']['state'] == 'applied' and all(ok for _, ok, _ in out['apply']['actions'])
        final_checks(s, out, before)
    s.save(out)


def recover(doc):
    """Revit restarted WITHOUT the fault: the model holds what the cut apply left. Plan again from it."""
    s = Session(doc, 'recover')
    prior = sorted(d for d in os.listdir(s.cfg['records']) if d.endswith('-%s-run' % doc))
    before = RS.load(os.path.join(s.cfg['records'], prior[-1], 'result.json'))['before']['openings']
    out = {'mode': 'recover', 'document': doc, 'from_run': prior[-1], 'verdict': {}}
    activate(s)
    link, _ = link_id(s)
    _, first = plan(s, link, 'plan')
    accept = [{'element_id': p['element_id'], 'candidate_id': p['candidate_id']} for p in first.get('pairings_offered') or []]
    reply, p = plan(s, link, 'plan-accepted', accept=accept)
    out['plan'] = {'error': refused(reply), 'actions': [a.get('key') for a in p.get('actions') or []],
                   'held': held_openings(p),
                   'splits': [{k: x.get(k) for k in ('element_id', 'automatic', 'held')} for x in p.get('splits') or []]}
    out['verdict']['nothing_left_to_decide'] = all((x.get('automatic') and not x.get('held')) for x in p.get('splits') or [])
    out['apply'] = apply(s, p, 'apply')
    out['verdict']['apply_whole'] = out['apply']['state'] == 'applied' and all(ok for _, ok, _ in out['apply']['actions'])
    final_checks(s, out, before)
    s.save(out)


if __name__ == '__main__':
    {'build': build, 'run': run, 'recover': recover}[sys.argv[1]](sys.argv[2])
