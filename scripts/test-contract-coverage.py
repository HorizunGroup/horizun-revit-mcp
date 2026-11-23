import contextlib
import importlib.util
import io
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('coverage_check', pathlib.Path(__file__).with_name('check-contract-coverage.py'))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class CoverageLimitTests(unittest.TestCase):
    def test_false_positive_and_unmapped_tool_are_disclosed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / 'src/Horizun.Contracts').mkdir(parents=True)
            (root / 'src/Horizun.Contracts/Contract.cs').write_text(
                'Name = "mapped" InputSchema = JObject.Parse(@"{ ""properties"": { ""status"": {} } }")\n'
                'Name = "unmapped" InputSchema = JObject.Parse(@"{}")', encoding='utf-8')
            (root / 'answer.cs').write_text('// output key only: "status"', encoding='utf-8')
            old_root, old_answers = check.ROOT, check.ANSWERS
            try:
                check.ROOT, check.ANSWERS = str(root), {'mapped': ['answer.cs']}
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    self.assertEqual(0, check.main())
                self.assertIn('unmapped', out.getvalue())
                self.assertIn('PARTIAL SCREEN', out.getvalue())
                self.assertIn('comments', out.getvalue())
            finally:
                check.ROOT, check.ANSWERS = old_root, old_answers


if __name__ == '__main__':
    unittest.main()
