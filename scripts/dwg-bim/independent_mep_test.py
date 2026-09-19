# -*- coding: utf-8 -*-
"""independent_mep.py without Revit: every way the check could look at nothing must fail, with its code.

    python scripts/dwg-bim/independent_mep_test.py
"""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import independent_mep as IM  # noqa: E402

TSV = ('E\t*Model_Space\tA1\tLWPOLYLINE\tM-Main-Duct\t0\t0\t0,0;200,0\n'
       'E\t*Model_Space\tT1\tTEXT\tM-TEXT\t100\t10\t\t8X6\n')


def duct(eid, x0, x1, w=203.2, h=152.4, cons=2):
    c = [{'origin': [x0, 0.0, 0.0]}, {'origin': [x1, 0.0, 0.0]}][:cons]
    ft = 304.8
    return {'element_id': eid, 'mep': {'connectors': c},
            'parameters': {'RBS_CURVE_WIDTH_PARAM': {'raw': w / ft}, 'RBS_CURVE_HEIGHT_PARAM': {'raw': h / ft}}}


class Independent(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='hz-indep-test-')
        self.tsv = os.path.join(self.tmp, 'd.tsv')
        open(self.tsv, 'w', encoding='utf-8').write(TSV)
        self.out = os.path.join(self.tmp, 'out')

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_main(self, caller, *extra):
        return IM.main(['--tsv', self.tsv, '--document', 'HZ_X', '--out', self.out] + list(extra), caller=caller)

    def sel(self):
        return json.load(open(os.path.join(self.out, 'selection.json'), encoding='utf-8'))

    def test_missing_server_is_not_accepted_and_exits_2(self):
        os.environ.pop('HORIZUN_SERVER_EXE', None)
        self.assertEqual(self.run_main(IM.call), 2)
        self.assertEqual(self.sel()['not_accepted'][0]['reason'], 'no_server')
        self.assertNotIn('selected', self.sel())

    def test_a_failed_call_or_wrong_document_exits_3(self):
        def refuse(tool, args, server):
            raise IM.Refused('call_failed', "the ACTIVE document is 'HZ_OTHER'")
        self.assertEqual(self.run_main(refuse), 3)

    def test_an_empty_population_nobody_declared_exits_4(self):
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': []}), 4)

    def test_an_empty_population_that_is_declared_is_accepted(self):
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': []}, '--expect-empty'), 0)
        self.assertEqual(self.sel()['selected'], 'attempt-1.json')

    def test_incomplete_evidence_exits_5(self):
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': [duct(1, 0, 5080, cons=1)]}), 5)
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': [], 'truncated': True}), 5)

    def test_a_count_that_does_not_match_exits_6(self):
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': [duct(1, 0, 5080)]}, '--expect-ducts', '2'), 6)

    def test_ducts_off_the_drawing_exit_7(self):
        def off(t, a, s):
            d = duct(1, 0, 5080)
            for c in d['mep']['connectors']:
                c['origin'][1] = 900.0
            return {'rows': [d]}
        self.assertEqual(self.run_main(off), 7)

    def test_a_later_good_run_is_selected_and_the_failed_one_stays(self):
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': []}), 4)
        self.assertEqual(self.run_main(lambda t, a, s: {'rows': [duct(1, 0, 5080)]}, '--expect-ducts', '1'), 0)
        s = self.sel()
        self.assertEqual(s['selected'], 'attempt-2.json')
        self.assertEqual([x['attempt'] for x in s['not_accepted']], ['attempt-1.json'])
        self.assertTrue(os.path.exists(os.path.join(self.out, 'attempt-1.json')))
        good = json.load(open(os.path.join(self.out, 'attempt-2.json'), encoding='utf-8'))
        self.assertEqual(good['summary']['label_beside_and_agrees'], 1)


if __name__ == '__main__':
    unittest.main()
