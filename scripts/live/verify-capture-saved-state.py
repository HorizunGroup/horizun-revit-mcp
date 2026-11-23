"""Regression: capture must leave a saved disposable model clean.

Requires the course's stdio-only hz helper, an owned Revit, and a COPY.
No arbitrary code is executed in Revit. Failure preserves logs and stops.
"""
import argparse
import json
import os
import sys
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--helper-root', required=True)
p.add_argument('--pid', required=True, type=int)
p.add_argument('--model', required=True)
p.add_argument('--view', required=True)
p.add_argument('--out', required=True)
a = p.parse_args()
out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)
os.environ['HZ_LOG'] = str(out / 'mcp_logs')
os.environ['HZ_VER'] = '2023'
sys.path.insert(0, a.helper_root)
import hz
hz.PID = a.pid

def save(name, result):
    (out / (name + '.json')).write_text(json.dumps(result, indent=2), encoding='utf-8')
    return result

save('health', hz.call('horizun_health', {}))
save('open', hz.activate(a.model))
title = Path(a.model).stem
def state(name):
    return save(name, hz.call('horizun_document_session', dict(
        operation='close', target_document=title, discard_unsaved=True,
        activate_other=True, dry_run=True)))

assert state('before')['is_modified'] is False, 'Fixture must start saved'
for name, options in [('temporary', dict(hide_annotations=True, orientation='isometric')),
                      ('plain', {})]:
    captured = save(name, hz.call('horizun_capture_view', dict(
        view_name=a.view, pixel_size=400, target_document=title, **options)))
    assert captured.get('captured') is True
    if options:
        assert captured['view_restored'] is True
        assert captured['document_modified_preserved'] is True
    assert state('after_' + name)['is_modified'] is False, name + ' dirtied the model'
try:
    hz.call('horizun_capture_view', dict(view_name=a.view, pixel_size=400,
            target_document=title, display_style='InvalidStyle'))
except RuntimeError as error:
    assert 'Invalid display_style' in str(error)
else:
    raise AssertionError('Invalid style should fail')
assert state('after_failure')['is_modified'] is False
assert hz.cerrar(title) is True
print('PASS: temporary/plain captures preserve saved state; close without discard succeeds')
