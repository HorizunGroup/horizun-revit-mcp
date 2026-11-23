"""Fail-closed targeting wrapper for external hz.py helpers; no shared helper edits.

Use install_guard(hz) before any call. Each target reply and selected PID are
checked before the requested tool can run. This is transport-side Python,
never Revit's execute_python command.
"""
import json


def validate_target(reply, expected_pid):
    if not isinstance(reply, dict) or 'error' in reply:
        raise RuntimeError('Target selection failed; requested tool was not sent.')
    result = reply.get('result') or {}
    if result.get('isError'):
        raise RuntimeError('Target selection refused; requested tool was not sent.')
    data = result.get('structuredContent')
    if not isinstance(data, dict):
        for item in result.get('content', []):
            if item.get('type') == 'text':
                try:
                    data = json.loads(item['text'])
                    break
                except (ValueError, KeyError):
                    pass
    if not isinstance(data, dict):
        raise RuntimeError('Target selection has no readable receipt.')
    # Server's horizun_target receipt calls this process_id or selected_pid.
    pid = data.get('process_id', data.get('selected_pid', data.get('pid')))
    if pid != expected_pid:
        raise RuntimeError(f'Target PID mismatch: requested {expected_pid}, received {pid}.')
    return data


def install_guard(hz):
    """Intercept only this helper module's subprocess; process-wide Popen stays intact."""
    import types
    if not isinstance(hz.PID, int) or isinstance(hz.PID, bool) or hz.PID <= 0:
        raise RuntimeError('A positive owned Revit PID is required before installing the guard.')
    original = hz.subprocess.Popen

    class GuardedStdout:
        def __init__(self, wrapped, state): self.wrapped, self.state = wrapped, state
        def __iter__(self):
            for line in self.wrapped:
                try: reply = json.loads(line)
                except ValueError:
                    yield line
                    continue
                if reply.get('id') == 9:
                    validate_target(reply, hz.PID)
                    self.state['validated'] = True
                yield line
        def __getattr__(self, name): return getattr(self.wrapped, name)

    class GuardedStdin:
        def __init__(self, wrapped, state): self.wrapped, self.state, self.pending = wrapped, state, ''
        def write(self, chunk):
            self.pending += chunk
            while '\n' in self.pending:
                line, self.pending = self.pending.split('\n', 1)
                message = json.loads(line)
                if message.get('method') == 'tools/call':
                    name = message.get('params', {}).get('name')
                    if name == 'horizun_target':
                        if message.get('id') != 9 or message.get('params', {}).get('arguments', {}).get('pid') != hz.PID:
                            raise RuntimeError('Target request differs from the owned PID.')
                        self.state['validated'] = False
                    elif not self.state['validated']:
                        raise RuntimeError('Requested tool refused: no matching target receipt.')
                self.wrapped.write(line + '\n')
            return len(chunk)
        def __getattr__(self, name): return getattr(self.wrapped, name)

    def guarded_popen(*args, **kwargs):
        process = original(*args, **kwargs)
        state = {'validated': False}
        process.stdout = GuardedStdout(process.stdout, state)
        process.stdin = GuardedStdin(process.stdin, state)
        return process

    hz.subprocess = types.SimpleNamespace(Popen=guarded_popen, PIPE=hz.subprocess.PIPE)
