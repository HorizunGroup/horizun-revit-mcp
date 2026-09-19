# -*- coding: utf-8 -*-
"""The DWG -> BIM route from ONE input spec, through the bridge's public tools only.

    python run_spec.py <spec.json> <document_name> [--config <machine.json>]

The spec is versionable (no machine path): the drawing by name, the link units, the level, the
requirement sets (walls, devices - which carry the zone, the catalogue, the mounting heights and the
wall policy), an optional decisions file and the acceptance criteria. The MACHINE config is not
versioned: where models live, which Revit template to start from, where each drawing name is on
this machine and where records go. Default: %USERPROFILE%\\.horizun\\dwg-bim-run.json.

What it does, in order - each step a recorded call, nothing edited by hand:
  1. a new disposable model from the template, the drawing linked (rehearsed, confirmed)
  2. PREFLIGHT: horizun_plan_from_cad catalog_check_only for each set - stops on a refused rule
  3. horizun_run_procedure dwg-to-bim-unit: advanced step by step; a step waiting for a person is
     decided from the spec's decisions file (versioned) or the run stops and says what is owed
  4. results by dimension: every drawn symbol of the spec's truth file against what was built
     (built, host, face kind, elevation, position), the bridge's own audit, and the pending items
     with a stable identity (candidate id and drawn point)
  5. real save, close and reopen; the same reading again, compared
Writes <records>/<utc>-<document>/result.json and prints its path. Nothing chooses "the newest file".
"""
import datetime
import hashlib
import io
import json
import math
import os
import shutil
import subprocess
import sys
import uuid

import session_hooks

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))


def load(path):
    return json.load(io.open(path, encoding='utf-8-sig'))


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


# Reads, and the procedure runner (which keys its own dispatches), carry no idempotency key; every
# call that changes the model or the session carries a new one.
UNKEYED = {'horizun_health', 'horizun_query_model', 'horizun_model_scan', 'horizun_plan_from_cad',
           'horizun_audit_cad_model', 'horizun_run_procedure'}


class Calls:
    """Every call recorded: arguments, reply, seconds. The reply file is never overwritten."""

    def __init__(self, folder, server_exe):
        self.dir, self.exe, self.n = folder, server_exe, 0
        os.makedirs(os.path.join(folder, 'calls'), exist_ok=False)

    def __call__(self, tool, args, name, key=None, timeout=1900):
        self.n += 1
        stem = '%04d-%s' % (self.n, name)
        args = dict(args)
        if key is None:
            key = tool not in UNKEYED and args.get('dry_run') is not True
        if key:
            args.setdefault('idempotency_key', str(uuid.uuid4()))
        a = os.path.join(self.dir, 'calls', stem + '.args.json')
        out = os.path.join(self.dir, 'calls', stem + '.json')
        io.open(a, 'x', encoding='utf-8').write(json.dumps(args, indent=1))
        env = session_hooks.env_for(dict(os.environ, HORIZUN_SERVER_EXE=self.exe))
        subprocess.run(['pwsh', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                        os.path.join(REPO, 'scripts', 'hz-call.ps1'), '-Tool', tool, '-ArgumentsPath', a,
                        '-TimeoutSec', str(timeout), '-Json', out, '-Quiet'],
                       cwd=REPO, env=env, capture_output=True, text=True, timeout=timeout + 120)
        if not os.path.exists(out):
            raise SystemExit('no reply for %s (%s)' % (tool, stem))
        reply = load(out)
        r = reply.get('result', reply)
        sc = (r.get('structuredContent', r) if isinstance(r, dict) else {}) or {}
        registered = session_hooks.after_call(tool, args, reply.get('is_error'), sc)
        if registered is not None:
            io.open(os.path.join(self.dir, 'calls', stem + '.registered.json'), 'x', encoding='utf-8').write(json.dumps(registered))
            if not registered['ok']:
                print('WARNING: %s was not registered in the isolated session: %s' % (tool, registered['detail']))
        return reply, sc

    def confirmed(self, tool, args, name):
        _, dry = self(tool, dict(args, dry_run=True), name + '-dry')
        token = dry.get('confirmation_token')
        if not token:
            raise SystemExit('%s rehearsal gave no token: %s' % (name, json.dumps(dry)[:400]))
        return self(tool, dict(args, dry_run=False, confirmation_token=token), name, key=True)


def model(call, cfg, spec, doc):
    rvt = os.path.join(cfg['model_dir'], doc + '.rvt')
    rte = os.path.join(cfg['model_dir'], doc + '.rte')
    if os.path.exists(rvt):
        raise SystemExit('refusing: %s exists (a run builds a new model)' % rvt)
    shutil.copyfile(cfg['revit_template'], rte)
    _, opened = call('horizun_open_document', {'path': rte}, 'open-template')
    if opened.get('active_document') != doc:
        raise SystemExit('the template did not open as %s' % doc)
    call('horizun_document_session', {'operation': 'save_as', 'target_document': doc, 'save_as_path': rvt},
         'save-as')
    _, scan = call('horizun_model_scan', {'target_document_title': doc, 'sections': ['views']}, 'views')
    plans = [v['view_id'] for v in scan['sections']['views']['views']['items']
             if not v['is_template'] and v['view_type'] == 'FloorPlan' and v['level'] == spec['level']]
    if not plans:
        raise SystemExit('the template has no floor plan on %s' % spec['level'])
    dwg = cfg['drawings'][spec['drawing']['name']]
    _, link = call.confirmed('horizun_manage_cad_links', {
        'operation': 'add', 'target_document': doc, 'file_path': dwg, 'view_id': plans[0],
        'units': spec['drawing']['units'], 'placement': 'origin', 'current_view_only': False}, 'link')
    if not link.get('element_id'):
        raise SystemExit('the link was not created: %s' % json.dumps(link)[:400])
    return rvt, dwg, link['element_id']


def preflight(call, doc, spec):
    out = {}
    for what in ('walls_set', 'devices_set'):
        _, check = call('horizun_plan_from_cad', {'target_document': doc, 'requirement_set': spec[what],
                                                  'catalog_check_only': True}, 'preflight-' + what)
        out[what] = {'counts': check.get('counts'),
                     'refused': [{k: r.get(k) for k in ('rule', 'problems')} for r in check.get('rules') or []
                                 if r.get('verdict') == 'refused'],
                     'warnings': [{k: r.get(k) for k in ('rule', 'warnings')} for r in check.get('rules') or []
                                  if r.get('warnings')]}
    return out


def procedure(call, doc, spec, link, dwg, decisions):
    _, started = call('horizun_run_procedure', {
        'operation': 'start', 'procedure': 'dwg-to-bim-unit', 'target_document': doc,
        'inputs': {'document': doc, 'instance_id': link, 'level_name': spec['level'], 'dwg_path': dwg,
                   'walls_set': spec['walls_set'], 'devices_set': spec['devices_set']}}, 'procedure-start')
    run_id = started.get('run_id')
    if not run_id:
        raise SystemExit('the procedure did not start: %s' % json.dumps(started)[:600])
    steps, owed = [], None
    for n in range(1, 40):
        reply, res = call('horizun_run_procedure', {'operation': 'advance', 'run_id': run_id}, 'advance-%02d' % n)
        if reply.get('is_error'):
            steps.append({'error': (reply.get('raw') or '')[:400]})
            break
        state = res.get('state')
        steps.append({'step': res.get('step'), 'tool': res.get('tool'), 'state': state})
        if state == 'waiting_for_a_decision':
            d = (decisions or {}).get(str(res.get('step')))
            if not d:
                owed = {'step': res.get('step'), 'needed': res.get('decision_needed') or res.get('means')}
                break
            call('horizun_run_procedure', {'operation': 'decide', 'run_id': run_id, 'step': res.get('step'),
                                           'values': d['values'], 'decision_version': decisions['version'],
                                           'decided_by': 'decisions file'}, 'decide-%s' % res.get('step'))
            continue
        if state in ('dispatched_outcome_unknown', 'blocked', 'failed') or res.get('run_state') not in (None, 'running') \
                or not res.get('next'):
            break
    _, status = call('horizun_run_procedure', {'operation': 'status', 'run_id': run_id}, 'procedure-status')
    return run_id, steps, owed, status


def results(call, doc, spec, truth, dwg, link, name):
    _, dev = call('horizun_query_model', {
        'target_document': doc, 'response_mode': 'full', 'include_orientation': True, 'include_cad_provenance': True,
        'max_rows': 2000, 'categories': spec['device_categories']}, name + '-devices')
    _, walls = call('horizun_query_model', {'target_document': doc, 'response_mode': 'summary',
                                            'categories': ['OST_Walls']}, name + '-walls')
    rows = [r for r in dev.get('rows', []) if not r.get('is_element_type')]
    inch = 25.4
    acc = spec['acceptance']
    per = {}
    for s in truth['symbols']:
        at = [s['at_in'][0] * inch, s['at_in'][1] * inch]
        near = sorted(rows, key=lambda r: math.hypot(r['placement']['point'][0] - at[0], r['placement']['point'][1] - at[1]))
        r = near[0] if near and math.hypot(near[0]['placement']['point'][0] - at[0],
                                           near[0]['placement']['point'][1] - at[1]) <= acc['match_radius_mm'] else None
        want = acc['symbols'][s['id']]
        got = {'built': r is not None}
        if r:
            p = r['placement']
            got.update({'element_id': r['element_id'], 'host_id': r.get('host_id'),
                        'face_kind': p.get('host_face_kind'), 'on_face': p.get('on_host_face'),
                        'z_mm': p['point'][2], 'plan_offset_mm': round(math.hypot(p['point'][0] - at[0], p['point'][1] - at[1]), 1),
                        'candidate_id': (r.get('cad_provenance') or {}).get('candidate_id')})
        dims = {'recognition_and_plan': 'pass' if got['built'] == want['built'] else 'fail'}
        if want['built'] and r:
            dims['host_surface'] = 'pass' if got['face_kind'] == want['face'] and got['on_face'] else 'fail'
            dims['elevation'] = 'pass' if abs(got['z_mm'] - acc['elevation_mm']) <= acc['tolerance_mm'] else 'fail'
            dims['plan_position'] = 'pass' if got['plan_offset_mm'] <= acc['plan_offset_max_mm'] else 'fail'
        per[s['id']] = {'expected': want, 'got': got, 'dimensions': dims}
    _, audit = call('horizun_audit_cad_model', {'instance_id': link, 'target_document': doc,
                                               'requirement_set': spec['devices_set'], 'dwg_path': dwg},
                    name + '-audit')
    pending = [{'code': f.get('code'), 'candidate_id': f.get('candidate_id'),
                'at_mm': ((f.get('evidence') or {}).get('geometry_mm') or [None])[0]}
               for f in audit.get('findings') or [] if f.get('code') in ('drawing_not_built',)]
    tally = {}
    for v in per.values():
        for k, d in v['dimensions'].items():
            tally.setdefault(k, {}).setdefault(d, 0)
            tally[k][d] += 1
    return {'symbols': per, 'dimensions': tally, 'devices_in_model': len(rows),
            'walls_in_model': walls.get('matched_total'),
            'audit': {'matched': (audit.get('matched') or {}).get('total'),
                      'states': {k: v for k, v in (audit.get('match_states') or {}).items() if k != 'means'}},
            'pending_with_identity': pending}


def main(spec_path, doc, config=None):
    config = config or os.path.join(os.environ['USERPROFILE'], '.horizun', 'dwg-bim-run.json')
    cfg = load(config)
    spec = load(spec_path)
    truth = load(os.path.join(os.path.dirname(cfg['drawings'][spec['drawing']['name']]), spec['drawing']['truth']))
    decisions = load(os.path.join(os.path.dirname(spec_path), spec['decisions'])) if spec.get('decisions') else None
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    folder = os.path.join(cfg['records'], '%s-%s' % (stamp, doc))
    exe = io.open(os.path.expandvars(cfg['server_exe_file']), encoding='utf-8-sig').read().strip()
    call = Calls(folder, exe)
    out = {'spec': os.path.basename(spec_path), 'spec_sha256': sha(spec_path), 'spec_version': spec['spec_version'],
           'document': doc, 'config': os.path.basename(config)}
    _, health = call('horizun_health', {}, 'health')
    # health names them horizun_version / horizun_commit; the short names are read too for older builds
    out['build'] = {'version': health.get('horizun_version') or health.get('version'),
                    'commit': health.get('horizun_commit') or health.get('commit'),
                    'contract_hash': health.get('contract_hash'), 'revit_version': health.get('revit_version'),
                    'revit_build': health.get('revit_build'), 'built_from_clean_tree': health.get('built_from_clean_tree')}
    rvt, dwg, link = model(call, cfg, spec, doc)
    out['drawing'] = {'name': spec['drawing']['name'], 'sha256': sha(dwg), 'truth_sha256': truth.get('sha256')}
    if out['drawing']['sha256'] != truth.get('sha256'):
        raise SystemExit('the drawing on this machine is not the one the truth file describes')
    out['preflight'] = preflight(call, doc, spec)
    if any(v['refused'] for v in out['preflight'].values()):
        out['stopped'] = 'preflight refused a rule: fix the data set, nothing was written beyond the new model'
    else:
        run_id, steps, owed, status = procedure(call, doc, spec, link, dwg, decisions)
        out['procedure'] = {'run_id': run_id, 'steps': steps, 'owed': owed,
                            'run_state': (status.get('run') or status).get('state') if isinstance(status, dict) else None}
        out['results'] = results(call, doc, spec, truth, dwg, link, 'built')
        call('horizun_save_document', {'target_document': doc}, 'save')
        _, closed = call('horizun_document_session', {'operation': 'close', 'target_document': doc,
                                                      'activate_other': True, 'save_on_close': False,
                                                      'dry_run': False}, 'close')
        out['closed'] = closed.get('closed') if isinstance(closed, dict) else None
        call('horizun_open_document', {'path': rvt}, 'reopen')
        again = results(call, doc, spec, truth, dwg, link, 'reopened')
        # 6. THE SAME DRAWING PLANNED AGAIN: nothing to do, nothing held, nothing read as moved
        out['replan'] = {}
        for what in ('walls_set', 'devices_set'):
            reply, rp = call('horizun_plan_cad_update', {'instance_id': link, 'target_document': doc,
                                                         'requirement_set': spec[what], 'level_name': spec['level'],
                                                         'dwg_path': dwg}, 'replan-' + what)
            out['replan'][what] = {'error': (reply.get('raw') or '')[:300] if reply.get('is_error') else None,
                                   'actions': len(rp.get('actions') or []),
                                   'awaiting_a_decision': rp.get('awaiting_a_decision'),
                                   'classification': {k: v for k, v in (rp.get('counts_by_classification') or {}).items() if v}}
        out['after_reopen_identical'] = {k: again['symbols'][k]['got'] == out['results']['symbols'][k]['got']
                                         for k in again['symbols']}
        failed = [(k, d) for k, v in out['results']['symbols'].items() for d, r in v['dimensions'].items() if r != 'pass']
        replan_clean = all(not v['error'] and v['actions'] == 0 and not v['awaiting_a_decision']
                           for v in out['replan'].values())
        audit_ok = not (out['results']['audit']['states'] or {}).get('differs')
        out['acceptance'] = {'passed': not failed and all(out['after_reopen_identical'].values()) and not owed
                                       and replan_clean and audit_ok,
                             'failed_dimensions': failed, 'replan_clean': replan_clean, 'audit_without_differences': audit_ok}
    io.open(os.path.join(folder, 'result.json'), 'x', encoding='utf-8').write(json.dumps(out, indent=1, default=str))
    print(os.path.join(folder, 'result.json'))
    print(json.dumps(out.get('acceptance') or out.get('stopped'), default=str))
    return out


if __name__ == '__main__':
    a = sys.argv[1:]
    cfgp = None
    if '--config' in a:
        i = a.index('--config')
        cfgp = a[i + 1]
        a = a[:i] + a[i + 2:]
    main(a[0], a[1], cfgp)
