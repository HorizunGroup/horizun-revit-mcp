import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent


class Gates(unittest.TestCase):
    def test_native_modal_test_has_windows_gate_and_non_windows_skip(self):
        source = (ROOT / 'tests/Horizun.Core.Tests/ModalAutoCloseWin32Tests.cs').read_text(encoding='utf-8')
        self.assertIn('Skip =', source)
        self.assertNotIn('if (!OperatingSystem.IsWindows()) return;', source)
        for name in ('pr.yml', 'ci.yml'):
            with self.subTest(workflow=name):
                text = (ROOT / '.github' / 'workflows' / name).read_text(encoding='utf-8')
                self.assertIn('FullyQualifiedName~ModalAutoCloseWin32Tests', text)
                self.assertIn('LogFileName=win32-modal.trx', text)

    def test_sync_runs_and_has_separate_results_in_both_workflows(self):
        for name in ('pr.yml', 'ci.yml'):
            with self.subTest(workflow=name):
                text = (ROOT / '.github' / 'workflows' / name).read_text(encoding='utf-8')
                self.assertIn('dotnet restore tests/Horizun.Sync.Integration.Tests/Horizun.Sync.Integration.Tests.csproj --locked-mode', text)
                self.assertIn('dotnet test tests/Horizun.Sync.Integration.Tests/Horizun.Sync.Integration.Tests.csproj', text)
                self.assertIn('LogFileName=sync-integration.trx', text)


if __name__ == '__main__':
    unittest.main()
