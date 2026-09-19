# -*- coding: utf-8 -*-
"""identity_chain.py without git history or Revit: synthetic records in a temp folder.

    python scripts/dwg-bim/identity_chain_test.py
"""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import identity_chain as IC  # noqa: E402

PRODUCT = {'aaaaaaa': 'P1', 'bbbbbbb': 'P1', 'ccccccc': 'P2'}


def equal(repo, a, b, paths):
    if a not in PRODUCT or b not in PRODUCT:
        return None
    return PRODUCT[a] == PRODUCT[b]


class IdentityChain(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='hz-idchain-')
        self.fam = os.path.join(self.tmp, 'records')
        os.makedirs(self.fam)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def record(self, folder, commit='aaaaaaa', year='2026', dll='d' * 64, verdict=None, build_extra=None):
        d = os.path.join(self.fam, folder)
        os.makedirs(d)
        build = {'version': '1.3.3', 'commit': commit, 'revit_version': year, 'revit_build': '26.4', 'process_id': 7}
        if dll:
            build['staged'] = {'staged_dll_sha256': dll, 'product_sources_dirty': False, 'year': year, 'revit_pid': 7}
        build.update(build_extra or {})
        rec = {'spec': 's.json', 'spec_sha256': 'e' * 64, 'build': build, 'verdict': verdict or {'a': True, 'b': True}}
        p = os.path.join(d, 'result.json')
        with open(p, 'w', encoding='utf-8') as f:
            json.dump(rec, f)
        return p

    def selection(self, cases):
        sel = {'schema': IC.SCHEMA, 'repo': self.tmp, 'candidate': {'name': 'I', 'commit': 'bbbbbbb'},
               'product_paths': ['src'], 'cases': cases}
        p = os.path.join(self.tmp, 'selection.json')
        with open(p, 'w', encoding='utf-8') as f:
            json.dump(sel, f)
        return IC.derive(p, equal)

    def case(self, result, **kw):
        c = {'case': 'x', 'year': '2026', 'result': result, 'selection_reason': 'the run of candidate I',
             'family_dir': self.fam, 'family_pattern': r'-HZ_X-run$'}
        c.update(kw)
        return c

    def test_complete_chain_keeps_the_reported_commit(self):
        p = self.record('20260919T010000Z-HZ_X-run')
        s = self.selection([self.case(p)])
        c = s['cases'][0]
        self.assertEqual(c['state'], 'complete', c)
        self.assertEqual(c['chain']['observed_build']['commit'], 'aaaaaaa')  # not replaced by the candidate's
        self.assertTrue(c['chain']['observed_build']['product_equals_candidate'])
        self.assertTrue(s['checks_ok'])

    def test_incompatible_commit_fails(self):
        p = self.record('20260919T010000Z-HZ_X-run', commit='ccccccc')
        c = self.selection([self.case(p)])['cases'][0]
        self.assertEqual(c['state'], 'failed_check')
        self.assertTrue(any('incompatible commit' in f for f in c['failures']))

    def test_unknown_commit_fails(self):
        p = self.record('20260919T010000Z-HZ_X-run', commit='fffffff')
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('unknown to this checkout' in f for f in c['failures']))

    def test_missing_identity_fails(self):
        p = self.record('20260919T010000Z-HZ_X-run', dll=None, build_extra={'revit_build': None})
        c = self.selection([self.case(p)])['cases'][0]
        self.assertEqual(c['state'], 'failed_check')
        self.assertTrue(any('revit_build' in f for f in c['failures']))
        self.assertTrue(any('staged DLL' in f for f in c['failures']))

    def test_declared_unrecorded_dll_is_incomplete_not_complete(self):
        p = self.record('20260919T010000Z-HZ_X-run', dll=None)
        c = self.selection([self.case(p, dll_unrecorded_reason='historical run; the stage folder name was a GUID')])['cases'][0]
        self.assertEqual(c['state'], 'incomplete_identity')

    def test_summary_that_omits_a_later_run_fails(self):
        p = self.record('20260919T010000Z-HZ_X-run')
        later = self.record('20260919T020000Z-HZ_X-run', verdict={'a': False})
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('omitted later run' in f for f in c['failures']), c)
        c = self.selection([self.case(p, considered={later: 'aborted by a harness error, rerun at 01:00'})])['cases'][0]
        self.assertEqual(c['state'], 'complete', c)

    def test_newest_file_without_explicit_selection_fails(self):
        self.record('20260919T010000Z-HZ_X-run')
        for bad in (os.path.join(self.fam, '*', 'result.json'), 'latest', self.fam):
            c = self.selection([self.case(bad)])['cases'][0]
            self.assertTrue(any('no explicit selection' in f for f in c['failures']), bad)
        p = self.record('20260919T030000Z-HZ_X-run')
        c = self.selection([self.case(p, selection_reason='')])['cases'][0]
        self.assertTrue(any('selection_reason' in f for f in c['failures']))

    def test_wrong_year_and_false_verdict_are_reported(self):
        p = self.record('20260919T010000Z-HZ_X-run', year='2025', verdict={'a': True, 'b': False})
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('ran in Revit 2025' in f for f in c['failures']))
        self.assertFalse(c['result']['passed'])
        self.assertIn('b', c['result']['detail'])

    def test_dirty_build_without_stamp_is_incomplete(self):
        p = self.record('20260919T010000Z-HZ_X-run', commit='aaaaaaa-dirty', dll=None)
        c = self.selection([self.case(p, dll_unrecorded_reason='historical')])['cases'][0]
        self.assertTrue(any('dirty tree' in i for i in c['incomplete']))
        self.assertEqual(c['chain']['observed_build']['commit_as_reported'], 'aaaaaaa-dirty')


    def health(self, result_path, sha, pid=7, commit='aaaaaaa'):
        calls = os.path.join(os.path.dirname(result_path), 'calls')
        os.makedirs(calls, exist_ok=True)
        with open(os.path.join(calls, '0001-health.json'), 'w', encoding='utf-8') as f:
            json.dump({'server': 'C:/x/horizun-mcp.exe', 'server_sha256': 's' * 64,
                       'result': {'horizun_commit': commit, 'process_id': pid, 'revit_version': '2026', 'revit_build': '26.4',
                                  'addin_assembly': {'path': 'C:/x/Horizun.Revit.dll', 'sha256': sha}}}, f)

    def test_the_loaded_dll_comes_from_the_runs_own_health(self):
        p = self.record('20260919T010000Z-HZ_X-run', dll=None)
        self.health(p, 'A' * 64)
        c = self.selection([self.case(p)])['cases'][0]
        self.assertEqual(c['chain']['dll']['sha256'], 'a' * 64)
        self.assertIn('inside the Revit process', c['chain']['dll']['source'])
        self.assertEqual(c['chain']['server']['sha256'], 's' * 64)

    def test_staged_and_loaded_dll_disagree(self):
        p = self.record('20260919T010000Z-HZ_X-run', dll='d' * 64)
        self.health(p, 'e' * 64)
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('Revit loaded' in f for f in c['failures']), c)

    def test_record_and_health_name_different_builds(self):
        p = self.record('20260919T010000Z-HZ_X-run')
        self.health(p, 'd' * 64, commit='bbbbbbb')
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('different builds' in f for f in c['failures']), c)

    def test_a_later_attempt_without_result_is_not_hidden(self):
        p = self.record('20260919T010000Z-HZ_X-run')
        dead = os.path.join(self.fam, '20260919T020000Z-HZ_X-run')
        os.makedirs(os.path.join(dead, 'calls'))
        c = self.selection([self.case(p)])['cases'][0]
        self.assertTrue(any('no result.json' in f for f in c['failures']), c)
        c = self.selection([self.case(p, considered={dead: 'died at open: modal dialog'})])['cases'][0]
        self.assertEqual(c['state'], 'complete', c)


if __name__ == '__main__':
    unittest.main(verbosity=1)
