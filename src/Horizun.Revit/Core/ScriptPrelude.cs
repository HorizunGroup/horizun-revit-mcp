// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Python every execute_python script starts with: stdout capture, and the
// three names a script can call without importing anything.
//
// IT LIVES IN ITS OWN FILE BECAUSE IT IS PYTHON. Concatenated into the command it
// was invisible to every compiler in the build: a missing colon here breaks EVERY
// execute_python call on the machine, and the first person to find out is a user
// inside Revit. Here it is one verbatim string that a real IronPython engine
// parses in the test suite - the same guard recipes already have.
//
// It is deliberately small. Everything it defines is either impossible from
// inside a script (the job record, the watcher, the dialog policy) or is the
// house contract for using them.
// -----------------------------------------------------------------------------
using System;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Process-wide Python streams captured in C# before untrusted script code runs.
    /// They cannot be rebound or deleted from the script scope because the authoritative
    /// references live here, outside that scope.
    /// </summary>
    public sealed class PythonStreamRestorer
    {
        private readonly ScriptScope _sys;
        private readonly object _stdout;
        private readonly object _stderr;
        private bool _restored;

        private PythonStreamRestorer(ScriptScope sys, object stdout, object stderr)
        {
            _sys = sys;
            _stdout = stdout;
            _stderr = stderr;
        }

        public static PythonStreamRestorer Capture(ScriptEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            ScriptScope sys = engine.GetSysModule();
            return new PythonStreamRestorer(sys, sys.GetVariable<object>("stdout"),
                                             sys.GetVariable<object>("stderr"));
        }

        public bool TryRestore(out string error)
        {
            error = null;
            if (_restored) return true;
            _restored = true;
            string stdoutError = null, stderrError = null;
            try { _sys.SetVariable("stdout", _stdout); } catch (Exception ex) { stdoutError = ex.Message; }
            try { _sys.SetVariable("stderr", _stderr); } catch (Exception ex) { stderrError = ex.Message; }
            if (stdoutError == null && stderrError == null) return true;
            error = "stdout=" + (stdoutError ?? "restored") + "; stderr=" + (stderrError ?? "restored");
            return false;
        }
    }

    public static class ScriptPrelude
    {
        /// <summary>
        /// Runs before the caller's script, in the same scope. Needs __hz_job,
        /// __hz_raised and __hz_dialog_scope to have been injected first.
        /// </summary>
        public const string Prologue = @"
import sys as __hz_sys, io as __hz_io
__hz_buf = __hz_io.StringIO()
__hz_stdout, __hz_stderr = __hz_sys.stdout, __hz_sys.stderr
__hz_sys.stdout = __hz_buf
__hz_sys.stderr = __hz_buf

def checkpoint(label, done=None, total=None):
    '''Record one step: checkpoint('model 40 of 300', 40, 300).

    Reaches the disk immediately, so progress is readable from OUTSIDE while this
    thread is busy being your call - and it survives a crash, which is when
    knowing how far it got matters most. The label is also what 'while' reports
    on anything Revit raises, so a batch that checkpoints per item gets every
    dialog attributed to the right one.'''
    __hz_job.Write(str(label), done, total)

def revit_raised(since=0):
    '''What Revit has raised SO FAR during this script: dialogs, warnings, errors.

    The bridge cancels modal dialogs, because nobody is at the keyboard - so an
    open that hit one comes back to you as nothing but 'Opening was canceled'.
    This is where the dialog itself is named, and it can be read DURING the run:

        before = len(revit_raised())
        doc = app.OpenDocumentFile(path, opts)
        raised = revit_raised(before)   # exactly what THIS open raised

    Returns a list of dicts: kind, description, answered, elements, while.'''
    __hz_r = __hz_raised(int(since))
    if __hz_r is None:
        raise RuntimeError(
            'The bridge did not fully observe both Revit dialog and failure channels for this run '
            '(a subscription was unavailable or an observer failed while processing), so the '
            'partial record is unavailable here - which is NOT the same as Revit raising nothing.')
    import json as __hz_json
    return __hz_json.loads(__hz_r)

class __HzDialogAnswer(object):
    # The factory is passed IN rather than read from the enclosing scope. Python
    # MANGLES a name beginning with two underscores when it is referenced inside a
    # class body: __hz_dialog_scope there compiles to _HzDialogAnswer__hz_dialog_scope,
    # which does not exist, and every `with dialog_answer(...)` fails with an
    # UnboundNameException naming a variable nobody wrote. (The prelude tests caught
    # exactly this; the other injected names are only ever touched from functions,
    # where the rule does not apply.)
    def __init__(self, make_scope, answer):
        self._make_scope = make_scope
        self._answer = answer
    def __enter__(self):
        self._scope = self._make_scope(self._answer)
        return self
    def __exit__(self, *ignored):
        self._scope.Dispose()
        return False

def dialog_answer(answer):
    '''Answer modal dialogs with 'dismiss' (OK/continue) around ONE call:

        with dialog_answer('dismiss'):
            doc = app.OpenDocumentFile(path, opts)

    Everything outside the block still cancels, and that scoping is the point:
    'dismiss' answers OK to EVERYTHING, and Revit reads OK on a close-with-changes
    dialog as SAVE - a read-only audit that left this on could write to every
    model it touched. A model that will not open unattended is still a finding;
    this only lets you measure one that would otherwise not be measurable at all.'''
    return __HzDialogAnswer(__hz_dialog_scope, answer)
";

        /// <summary>Runs after the caller's script, whatever happened to it.</summary>
        public const string Epilogue = @"
# Capturing text is best effort. Process-wide stdout/stderr restoration is done
# afterwards by PythonStreamRestorer from C# references outside this scope, so a
# script cannot defeat cleanup by rebinding or deleting these aliases.
try:
    __hz_printed = __hz_buf.getvalue()
except Exception as __hz_capture_error:
    __hz_printed = None
    __hz_capture_error_text = str(__hz_capture_error)
";
    }
}
