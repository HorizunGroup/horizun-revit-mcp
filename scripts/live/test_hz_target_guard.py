import io
import types
import unittest
from hz_target_guard import validate_target, install_guard


class TargetGuardTests(unittest.TestCase):
    def test_matching_receipt(self):
        self.assertEqual(validate_target({'result': {'structuredContent': {'selected_pid': 42}}}, 42)['selected_pid'], 42)

    def test_refusals_and_missing_receipt(self):
        for reply in [None, {'error': {}}, {'result': {'isError': True}}, {'result': {}},
                      {'result': {'structuredContent': {'selected_pid': 43}}}]:
            with self.subTest(reply=reply), self.assertRaises(RuntimeError): validate_target(reply, 42)

    def test_helper_never_sends_requested_tool_after_failed_target(self):
        sent = io.StringIO()
        fake_process = types.SimpleNamespace(stdout=iter(['{"id":9,"result":{"isError":true}}\n']), stdin=sent)
        hz = types.SimpleNamespace(PID=42, subprocess=types.SimpleNamespace(Popen=lambda: fake_process, PIPE=-1))
        install_guard(hz)
        process = hz.subprocess.Popen()
        with self.assertRaises(RuntimeError):
            for line in process.stdout:
                sent.write('requested_write')
        self.assertEqual(sent.getvalue(), '')

    def test_missing_pid_refuses_install(self):
        for pid in [0, -1, None, True]:
            with self.subTest(pid=pid), self.assertRaises(RuntimeError):
                install_guard(types.SimpleNamespace(PID=pid))

    def test_eof_without_target_receipt_refuses_send(self):
        sent = io.StringIO()
        fake = types.SimpleNamespace(stdout=iter([]), stdin=sent)
        hz = types.SimpleNamespace(PID=42, subprocess=types.SimpleNamespace(Popen=lambda: fake, PIPE=-1))
        install_guard(hz)
        process = hz.subprocess.Popen()
        list(process.stdout)
        with self.assertRaises(RuntimeError):
            process.stdin.write('{"id":2,"method":"tools/call","params":{"name":"horizun_create_elements"}}\n')
        self.assertEqual(sent.getvalue(), '')


if __name__ == '__main__': unittest.main()
