# -*- coding: utf-8 -*-
"""Derive a campaign summary from an EXPLICIT selection of run records - never by hand.

    python scripts/dwg-bim/identity_chain.py <selection.json> [--out <summary.json>]

For every case the summary carries the chain  case -> candidate -> DLL -> Revit -> config -> result,
each link read from the run's own record. The identity a run REPORTED is kept as reported: a
later candidate never overwrites it. Whether that historical build is the candidate's product is
decided by comparing product sources in git (`git diff --quiet <observed> <candidate> -- <paths>`),
not by assuming it.

A case fails the summary when:
  - no explicit selection: the result is not a named file (a glob, 'latest', a folder) or carries
    no selection_reason;
  - missing identity: commit, Revit version, Revit build, staged DLL hash or config is absent - a
    DLL that was not recorded at run time may be DECLARED unrecorded, which is reported as
    incomplete, never as complete;
  - incompatible commit: the observed build is not the product of the candidate the case claims;
  - omitted later run: a record of the same family, newer than the selected one, that the
    selection neither selects nor explains in `considered`.

The selection shape (all paths explicit):
  {"schema": "horizun.identity-selection/1", "repo": "<git checkout>",
   "candidate": {"name": "I", "commit": "<sha>"},
   "product_paths": ["src", "global.json", "Directory.Build.props"],
   "cases": [{"case": "...", "year": "2026", "result": "<.../result.json>",
              "selection_reason": "...", "family_dir": "<dir>", "family_pattern": "<regex on folder name>",
              "family_document": "<optional regex on the record's document; a folder without a record always counts>",
              "considered": {"<other result.json>": "why it is not the one"},
              "candidate": "<optional: the candidate this case was run as, if not the top one>",
              "candidate_commit": "<its sha>",
              "config": {...optional explicit config identity...},
              "dll_sha256": "<optional, with dll_source>", "dll_unrecorded_reason": "<optional>",
              "verdict": "all_true | status_ok | key:<name>"}]}
"""
import hashlib
import io
import json
import os
import re
import subprocess
import sys

SCHEMA = 'horizun.identity-selection/1'
SUMMARY_SCHEMA = 'horizun.identity-summary/1'
WILDCARDS = re.compile(r'[*?\[]|(^|[\\/])latest$', re.I)


def load(path):
    with io.open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def git_product_equal(repo, a, b, paths):
    """True/False when git can answer; None when a commit is unknown to this checkout."""
    for c in (a, b):
        if subprocess.run(['git', '-C', repo, 'cat-file', '-e', c + '^{commit}'],
                          capture_output=True).returncode != 0:
            return None
    r = subprocess.run(['git', '-C', repo, 'diff', '--quiet', a, b, '--'] + list(paths), capture_output=True)
    return r.returncode == 0


def observed_commit(build):
    raw = str((build or {}).get('commit') or '')
    m = re.match(r'^([0-9a-f]{7,40})(-dirty)?$', raw.split('...')[0].strip())
    if not m:
        return None, None, raw
    return m.group(1), bool(m.group(2)), raw


def verdict_of(result, rule):
    rule = rule or 'all_true'
    if rule == 'all_true':
        v = result.get('verdict')
        if not isinstance(v, dict) or not v:
            return None, 'the record has no verdict block'
        failed = sorted(k for k, x in v.items() if x is not True)
        return (not failed), ('false: ' + ', '.join(failed)) if failed else 'every verdict true'
    if rule == 'status_ok':
        s = result.get('status')
        return s == 'ok', 'status %s' % s
    if rule.startswith('status:'):
        s = result.get('status')
        return s == rule[7:], 'status %s' % s
    if rule.startswith('key:'):
        k = rule[4:]
        return result.get(k) is True, '%s = %s' % (k, result.get(k))
    return None, 'unknown verdict rule %s' % rule


def stamp_of(folder_name):
    m = re.match(r'^(\d{8}T\d{6}Z|\d{8}-\d{6})', folder_name)
    return m.group(1).replace('-', 'T') if m else None


def later_runs(case, selected):
    """Newer runs of the same family the selection does not explain - INCLUDING attempts that
    died before writing a result.json (their folder is the run; its absence of a result is
    exactly what a summary must not hide)."""
    fam_dir, pattern = case.get('family_dir'), case.get('family_pattern')
    if not fam_dir or not pattern:
        return None
    sel_folder = os.path.basename(os.path.dirname(os.path.abspath(selected)))
    sel_stamp = stamp_of(sel_folder)
    norm = lambda x: os.path.normcase(os.path.abspath(x))  # noqa: E731
    considered = {norm(k) for k in (case.get('considered') or {})}
    found = []
    for name in sorted(os.listdir(fam_dir)):
        folder = os.path.join(fam_dir, name)
        if not os.path.isdir(folder) or not re.search(pattern, name) or name == sel_folder:
            continue
        res = os.path.join(folder, 'result.json')
        doc_re = case.get('family_document')
        if doc_re and os.path.isfile(res):
            r = load(res)
            doc = (r.get('summary') or {}).get('document') if isinstance(r.get('summary'), dict) else None
            if not re.search(doc_re, str(doc or r.get('document') or '')):
                continue
        st = stamp_of(name)
        if not (st and sel_stamp and st > sel_stamp):
            continue
        if norm(folder) in considered or norm(res) in considered:
            continue
        found.append(res if os.path.isfile(res) else folder + ' (no result.json)')
    return found


def health_reply(case, result_path):
    """The run's OWN first health reply: named explicitly (build_from) or the first
    calls/*-health.json of the run folder. Returns (path, call_record, health_dict)."""
    path = case.get('build_from')
    if not path:
        calls = os.path.join(os.path.dirname(result_path), 'calls')
        names = sorted(n for n in os.listdir(calls) if re.search(r'health\.json$', n) and not n.endswith('.args.json')) \
            if os.path.isdir(calls) else []
        path = os.path.join(calls, names[0]) if names else None
    if not path or not os.path.isfile(path):
        return None, None, {}
    d = load(path)
    h = d.get('result') if isinstance(d.get('result'), dict) else (d.get('structuredContent') or {})
    if isinstance(h.get('structuredContent'), dict):
        h = h['structuredContent']
    return path, d, h


def derive_case(sel, case, equal=git_product_equal):
    out = {'case': case.get('case'), 'year': case.get('year'), 'failures': [], 'incomplete': []}
    path = case.get('result')
    if not path or WILDCARDS.search(str(path)) or not str(path).lower().endswith('.json') or not os.path.isfile(path):
        out['failures'].append('no explicit selection: result must name one existing record file, got %r' % path)
        out['state'] = 'failed_check'
        return out
    if not str(case.get('selection_reason') or '').strip():
        out['failures'].append('no explicit selection: selection_reason is empty')
    rec = load(path)
    hpath, hcall, health = health_reply(case, path)
    build = dict(rec.get('build') or {})
    for k_rec, k_h in (('commit', 'horizun_commit'), ('version', 'horizun_version'), ('contract_hash', 'contract_hash'),
                       ('revit_version', 'revit_version'), ('revit_build', 'revit_build'), ('process_id', 'process_id')):
        if not build.get(k_rec) and health.get(k_h) is not None:
            build[k_rec] = health.get(k_h)
    staged = build.get('staged') if isinstance(build.get('staged'), dict) else {}
    commit, dirty, raw = observed_commit(build)
    if health.get('horizun_commit') and str(health['horizun_commit']) != str(rec.get('build', {}).get('commit') or health['horizun_commit']):
        out['failures'].append('the record and its own health reply name different builds (%s vs %s)'
                               % (rec['build'].get('commit'), health['horizun_commit']))
    cand_name = case.get('candidate') or sel['candidate']['name']
    cand_commit = case.get('candidate_commit') or sel['candidate']['commit']
    loaded = health.get('addin_assembly') if isinstance(health.get('addin_assembly'), dict) else {}
    dll, dll_source = None, None
    if loaded.get('sha256'):
        dll, dll_source = loaded['sha256'].lower(), 'health reply of the run: the add-in assembly hashed inside the Revit process'
        if staged.get('staged_dll_sha256') and staged['staged_dll_sha256'].lower() != dll:
            out['failures'].append('the session staged DLL %s but Revit loaded %s' % (staged['staged_dll_sha256'][:12], dll[:12]))
    elif staged.get('staged_dll_sha256'):
        dll, dll_source = staged['staged_dll_sha256'].lower(), 'session build stamp (staged, not confirmed loaded)'
    elif case.get('dll_sha256'):
        dll, dll_source = case['dll_sha256'], case.get('dll_source') or 'declared in the selection'
    if staged.get('revit_pid') and health.get('process_id') and int(staged['revit_pid']) != int(health['process_id']):
        out['failures'].append('the build stamp is for pid %s, the run talked to pid %s' % (staged['revit_pid'], health['process_id']))
    config = case.get('config') or {k: rec[k] for k in ('spec', 'spec_sha256', 'spec_version', 'config', 'decisions_version', 'mode', 'fault') if k in rec}
    out['chain'] = {
        'candidate': {'name': cand_name, 'commit': cand_commit},
        'observed_build': {'commit_as_reported': raw, 'commit': commit, 'dirty': dirty,
                           'version': build.get('version'), 'contract_hash': build.get('contract_hash')},
        'dll': {'sha256': dll, 'source': dll_source, 'path': loaded.get('path'), 'written_utc': loaded.get('written_utc'),
                'unrecorded_reason': case.get('dll_unrecorded_reason')},
        'server': {'path': (hcall or {}).get('server'), 'sha256': (hcall or {}).get('server_sha256')},
        'health_reply': {'path': hpath, 'sha256': sha256(hpath) if hpath else None},
        'revit': {'version': build.get('revit_version'), 'build': build.get('revit_build'), 'process_id': build.get('process_id')},
        'config': config,
        'result': {'path': path, 'sha256': sha256(path), 'selection_reason': case.get('selection_reason')},
    }
    missing = [n for n, v in (('commit', commit), ('revit_version', build.get('revit_version')),
                              ('revit_build', build.get('revit_build')), ('config', config)) if not v]
    if missing:
        out['failures'].append('missing identity: ' + ', '.join(missing))
    if not dll:
        if case.get('dll_unrecorded_reason'):
            out['incomplete'].append('DLL hash not recorded at run time: ' + case['dll_unrecorded_reason'])
        else:
            out['failures'].append('missing identity: staged DLL sha256 (neither in the record nor declared unrecorded)')
    if case.get('year') and str(build.get('revit_version')) != str(case['year']):
        out['failures'].append('the record ran in Revit %s, the case says %s' % (build.get('revit_version'), case['year']))
    if commit:
        same = equal(sel['repo'], commit, cand_commit, sel.get('product_paths') or ['src'])
        out['chain']['observed_build']['product_equals_candidate'] = same
        if same is None:
            out['failures'].append('incompatible commit: %s or %s is unknown to this checkout' % (commit, cand_commit))
        elif not same:
            out['failures'].append('incompatible commit: %s is not the product of candidate %s (%s)' % (commit, cand_name, cand_commit))
        if dirty and not (staged and staged.get('product_sources_dirty') is False):
            out['incomplete'].append('built from a dirty tree: which files were dirty is not in the record')
    later = later_runs(case, path)
    if later is None:
        out['incomplete'].append('no run family declared: later runs could not be checked')
    elif later:
        out['failures'].append('omitted later run(s): ' + ', '.join(later))
    ok, why = verdict_of(rec, case.get('verdict'))
    out['result'] = {'passed': ok, 'detail': why}
    out['state'] = 'failed_check' if out['failures'] else ('incomplete_identity' if out['incomplete'] else 'complete')
    return out


def derive(selection_path, equal=git_product_equal):
    sel = load(selection_path)
    if sel.get('schema') != SCHEMA:
        raise SystemExit('not an identity selection (%s)' % sel.get('schema'))
    cases = [derive_case(sel, c, equal) for c in sel['cases']]
    counts = {}
    for c in cases:
        counts[c['state']] = counts.get(c['state'], 0) + 1
    return {'schema': SUMMARY_SCHEMA, 'derived_from': {'selection': selection_path, 'sha256': sha256(selection_path)},
            'candidate': sel['candidate'], 'product_paths': sel.get('product_paths') or ['src'],
            'counts': counts, 'results_passed': sum(1 for c in cases if (c.get('result') or {}).get('passed') is True),
            'checks_ok': all(c['state'] != 'failed_check' for c in cases), 'cases': cases}


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    summary = derive(argv[1])
    out = argv[argv.index('--out') + 1] if '--out' in argv else os.path.splitext(argv[1])[0] + '.summary.json'
    with io.open(out, 'w', encoding='utf-8') as f:
        f.write(json.dumps(summary, indent=1, ensure_ascii=False))
    print(out)
    print(json.dumps({'counts': summary['counts'], 'checks_ok': summary['checks_ok'],
                      'results_passed': summary['results_passed']}))
    for c in summary['cases']:
        for f_ in c['failures']:
            print('FAIL %s: %s' % (c['case'], f_))
    return 0 if summary['checks_ok'] else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
