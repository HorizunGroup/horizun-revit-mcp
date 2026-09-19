# -*- coding: utf-8 -*-
"""What every driver call does for the isolated session (scripts/dwg-bim/session.ps1):

- aim at the recorded Revit YEAR (HORIZUN_REVIT_YEAR from %TEMP%\\hz_year.txt), so a person's
  Revit of another year is never the one the bridge answers for;
- after an open or a save-as that REPORTED SUCCESS, register the active document in the
  session's register by the path the bridge publishes. A document that is not registered is
  somebody else's, and `session.ps1 stop` then leaves Revit running instead of closing it.

Registration failures are returned, never swallowed: the caller records them."""
import os
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))


def session_year():
    p = os.path.join(os.environ.get('TEMP', ''), 'hz_year.txt')
    try:
        with open(p, encoding='utf-8-sig') as f:
            return f.read().strip() or None
    except OSError:
        return None


def env_for(env):
    env = dict(env)
    year = session_year()
    if year and not env.get('HORIZUN_REVIT_YEAR'):
        env['HORIZUN_REVIT_YEAR'] = year
    return env


def _title(value):
    if isinstance(value, dict):
        return value.get('title')
    return value


def expected_title(tool, args, sc):
    """The document an open or save-as should have made active, or None when the call is not one."""
    if args.get('dry_run') is True:
        return None
    if tool == 'horizun_open_document':
        return _title((sc or {}).get('active_document')) or os.path.splitext(os.path.basename(args.get('path') or ''))[0] or None
    if tool == 'horizun_document_session' and args.get('operation') == 'save_as':
        return os.path.splitext(os.path.basename(args.get('save_as_path') or ''))[0] or None
    return None


def after_call(tool, args, is_error, sc):
    """Register what an open or save-as produced. Returns None when nothing had to be registered,
    else {'ok': bool, 'detail': str}."""
    title = None if is_error else expected_title(tool, args, sc)
    if not title:
        return None
    year = session_year()
    if not year:
        return {'ok': False, 'detail': 'no isolated session year recorded (%TEMP%\\hz_year.txt); nothing registered'}
    cmd = ['pwsh', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(HERE, 'session.ps1'),
           'register', '-Year', year, '-ExpectedTitle', title]
    src = args.get('path') or args.get('save_as_path')
    if src:
        cmd += ['-SourceFile', src]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    return {'ok': r.returncode == 0, 'detail': (r.stdout.strip() or r.stderr.strip())[-600:]}
