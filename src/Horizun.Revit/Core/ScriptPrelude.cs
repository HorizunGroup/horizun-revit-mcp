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
if globals().get('__hz_import_revit', False):
    import clr
    import Autodesk
    from Autodesk.Revit.DB import *
__hz_buf = __hz_io.StringIO()
__hz_stdout, __hz_stderr = __hz_sys.stdout, __hz_sys.stderr
__hz_sys.stdout = __hz_buf
__hz_sys.stderr = __hz_buf

# The run's time limit and cancellation, injected as __hz_control. Absent (a prelude
# exercised on its own) means there is nothing to stop the script.
try:
    __hz_control
except NameError:
    __hz_control = None

class HorizunInterrupted(BaseException):
    '''Raised at a safe point when the run hit its timeout_s or was cancelled.
    A BaseException on purpose: `except Exception` does not swallow it. A bare
    `except:` does - do not wrap checkpoint() in one. try/finally and `with
    horizun.transaction(...)` run as usual, so the work in an open transaction is
    rolled back as the interruption unwinds.'''
    pass

class HorizunCancelled(HorizunInterrupted):
    '''Cooperative cancellation observed at a checkpoint.'''
    pass

def should_stop():
    '''True once the run was cancelled or its timeout_s passed. Does not raise.'''
    return __hz_control is not None and __hz_control.Poll() is not None

def safe_point():
    '''A place where it is safe to stop: raises HorizunInterrupted when the run
    must stop, and does nothing otherwise. checkpoint() calls this too.'''
    if __hz_control is not None:
        __hz_why = __hz_control.Poll()
        if __hz_why is not None:
            raise HorizunInterrupted(str(__hz_why) + ': ' + str(__hz_control.Reason))

def checkpoint(label=None, done=None, total=None):
    '''Record one step: checkpoint('model 40 of 300', 40, 300).

    Reaches the disk immediately, so progress is readable from OUTSIDE while this
    thread is busy being your call - and it survives a crash, which is when
    knowing how far it got matters most. The label is also what 'while' reports
    on anything Revit raises, so a batch that checkpoints per item gets every
    dialog attributed to the right one.'''
    if label is not None:
        __hz_job.Write(str(label), done, total)
    if __hz_control is not None:
        __hz_why = __hz_control.Poll()
        if __hz_why is not None:
            raise HorizunCancelled(str(__hz_why) + ': ' + str(__hz_control.Reason))

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

# Version 1 helpers are scoped to this execution, not installed in sys.modules.
import types as _hz_types
from contextlib import contextmanager as _hz_contextmanager
horizun = _hz_types.ModuleType('horizun')
horizun.version = 1

@_hz_contextmanager
def _hz_transaction(name='Horizun script'):
    from Autodesk.Revit.DB import Transaction, SubTransaction, TransactionStatus
    if doc is None:
        raise RuntimeError('A document is required for a transaction')
    tx = SubTransaction(doc) if doc.IsModifiable else Transaction(doc, name)
    try:
        if tx.Start() != TransactionStatus.Started:
            raise RuntimeError('Transaction did not start')
        try:
            yield tx
            status = tx.Commit()
            if status != TransactionStatus.Committed:
                raise RuntimeError('Transaction did not commit: ' + str(status))
        except:
            if tx.GetStatus() == TransactionStatus.Started:
                status = tx.RollBack()
                if status != TransactionStatus.RolledBack:
                    raise RuntimeError('Rollback is unconfirmed: ' + str(status))
            raise
    finally:
        tx.Dispose()

def _hz_report(status='completed_unverified', created_ids=None, modified_ids=None,
               deleted_ids=None, evidence=None, warnings=None, summary=None):
    global __output__
    __output__ = dict(status=status, summary=summary, created_ids=list(created_ids or []),
                     modified_ids=list(modified_ids or []), deleted_ids=list(deleted_ids or []),
                     verification=dict(checked=bool(evidence), evidence=list(evidence or [])),
                     warnings=list(warnings or []))
    return __output__

def _hz_param_data_type(definition):
    try:
        getter = definition.GetDataType
    except AttributeError:
        return definition.ParameterType
    return getter()

def _hz_family_type_value(family_type, param):
    kind = str(param.StorageType).split('.')[-1]
    readers = {'Double': 'AsDouble', 'Integer': 'AsInteger',
               'String': 'AsString', 'ElementId': 'AsElementId'}
    if kind not in readers:
        raise ValueError('Unsupported family parameter StorageType: ' + kind)
    return getattr(family_type, readers[kind])(param)

def _hz_iter_safe(value):
    return () if value is None else value

def _hz_of_class(elements, cls):
    return [element for element in elements if isinstance(element, cls)]

def _hz_safe_json_path(p):
    import json
    return json.dumps(str(p))

def _hz_out_reference(dotnet_type):
    import clr
    return clr.Reference[dotnet_type]()

def _hz_id_value(element_id):
    '''The number inside an ElementId, on every supported Revit: .Value (64-bit)
    from 2024, .IntegerValue before. Revit 2026 removed IntegerValue, so a script
    written against either property alone fails on half the supported years.'''
    if element_id is None:
        return None
    try:
        # int(): .Value is a System.Int64, which json.dumps refuses as 'not JSON
        # serializable' - the helper that exists to make ids portable must hand
        # back a Python int a script can serialise.
        return int(element_id.Value)
    except AttributeError:
        return int(element_id.IntegerValue)

def _hz_install_json_net_values():
    '''json.dumps refuses the .NET values the Revit API hands back - ElementId.Value
    (Int64), Color.Red (Byte), a Guid, an enum - with TypeError: ... is not JSON
    serializable. Field signals (2026-10): read-only scripts that had done all their
    work died at the last line, writing their own JSON. This teaches json's default
    hook the lossless conversions only; anything else still raises exactly as
    before, and a script's own JSONEncoder subclass still wins. Once per engine.'''
    try:
        import json as _j
    except Exception:
        return
    if getattr(_j.JSONEncoder.default, '_hz_net_values', False):
        return
    _plain = _j.JSONEncoder.default
    def _hz_default(self, o):
        try:
            import System
            if isinstance(o, (System.Int64, System.UInt64, System.UInt32, System.Int16,
                              System.UInt16, System.Byte, System.SByte)):
                return int(o)
            if isinstance(o, (System.Single, System.Decimal)):
                return float(o)
            if isinstance(o, System.Guid):
                return str(o)
            if isinstance(o, System.Enum):
                return str(o)
        except ImportError:
            pass
        t = type(o)
        if t.__name__ == 'ElementId' and str(getattr(t, '__module__', '')).startswith('Autodesk'):
            try:
                return int(o.Value)
            except AttributeError:
                return int(o.IntegerValue)
        return _plain(self, o)
    _hz_default._hz_net_values = True
    _j.JSONEncoder.default = _hz_default

_hz_install_json_net_values()

# TRANSACTIONS THE SCRIPT OPENED, so none is left behind. PythonCompat routes every
# Transaction( / SubTransaction( / TransactionGroup( through _hz_new_tx and every
# name.Close( through _hz_closing(name). Revit refuses to close a document with an
# open transaction, and a transaction left open at the end used to poison the
# document; the bridge now rolls them back, innermost first, and reports each one.
__hz_tx_open = []
__hz_tx_closed = []

def _hz_new_tx(cls, *args, **kwargs):
    tx = cls(*args, **kwargs)
    __hz_tx_open.append((tx, args[0] if args else None))
    return tx

def __hz_tx_name(tx):
    try:
        return str(tx.GetName())
    except Exception:
        return ''

def __hz_rollback_open(only_doc=None, why='end of script'):
    closed = []
    for tx, owner in reversed(list(__hz_tx_open)):
        if only_doc is not None and owner is not None:
            try:
                if not owner.Equals(only_doc):
                    continue
            except Exception:
                continue
        try:
            if str(tx.GetStatus()) != 'Started':
                continue
            tx.RollBack()
            closed.append(dict(kind=type(tx).__name__, name=__hz_tx_name(tx), why=why))
        except Exception:
            pass
    __hz_tx_closed.extend(closed)
    return closed

def _hz_closing(target):
    if type(target).__name__ == 'Document':
        __hz_rollback_open(target, 'rolled back before Close() - Revit refuses to close a document with an open transaction')
    return target

horizun.transaction = _hz_transaction
horizun.report = _hz_report
horizun.out_reference = _hz_out_reference
horizun.id_value = _hz_id_value
horizun.param_data_type = _hz_param_data_type
horizun.family_type_value = _hz_family_type_value
horizun.iter_safe = _hz_iter_safe
horizun.of_class = _hz_of_class
horizun.safe_json_path = _hz_safe_json_path

# ---- View <-> sheet <-> model coordinates, a ray from a view, leader capture/restore ----
# (4.2-N5, CT-P1-5e). Lengths are Revit internal units (feet) everywhere. Pure
# arithmetic on (x, y, z) tuples, so the same math is proven without Revit. The
# Revit objects are only READ.

def _hz_t(p):
    return (float(p.X), float(p.Y), float(p.Z))

def _hz_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def _hz_dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def _hz_as_t(point):
    if hasattr(point, 'X'):
        return _hz_t(point)
    return (float(point[0]), float(point[1]), float(point[2]))

def _hz_view_frame(view):
    return dict(origin=_hz_t(view.Origin), right=_hz_t(view.RightDirection),
                up=_hz_t(view.UpDirection), toward_eye=_hz_t(view.ViewDirection),
                scale=float(view.Scale))

def _hz_model_to_view(view, point):
    '''Model point -> (u, v, w) in the view axes, feet from view.Origin: u right,
    v up, w toward the eye (negative = into the view).'''
    f = _hz_view_frame(view)
    d = _hz_sub(_hz_as_t(point), f['origin'])
    return (_hz_dot(d, f['right']), _hz_dot(d, f['up']), _hz_dot(d, f['toward_eye']))

def _hz_view_to_model(view, u, v, w=0.0):
    f = _hz_view_frame(view)
    o, r, up, e = f['origin'], f['right'], f['up'], f['toward_eye']
    return tuple(o[i] + u * r[i] + v * up[i] + w * e[i] for i in range(3))

def _hz_new_xyz(t):
    from Autodesk.Revit.DB import XYZ
    return XYZ(t[0], t[1], t[2])

def _hz_crop_center_uv(view, xyz=None):
    '''Centre of the crop box in (u, v) of the view: the anchor a viewport box is
    placed by, hence of every sheet conversion.'''
    mk = xyz or _hz_new_xyz
    cb = view.CropBox
    mid = ((cb.Min.X + cb.Max.X) / 2.0, (cb.Min.Y + cb.Max.Y) / 2.0, (cb.Min.Z + cb.Max.Z) / 2.0)
    model = cb.Transform.OfPoint(mk(mid))
    u, v, w = _hz_model_to_view(view, model)
    return (u, v)

_HZ_ROT = {'None': 0, 'Clockwise': 1, 'Counterclockwise': -1}

def _hz_rot_steps(viewport):
    name = str(viewport.Rotation)
    if name not in _HZ_ROT:
        raise ValueError('Unknown viewport rotation: ' + name)
    return _HZ_ROT[name]

def _hz_rotate(du, dv, steps):
    if steps == 0:
        return (du, dv)
    if steps == 1:
        return (dv, -du)
    return (-dv, du)

def _hz_viewport_view(viewport, view=None):
    if view is not None:
        return view
    return doc.GetElement(viewport.ViewId)

def _hz_view_to_sheet(viewport, u, v, view=None, offset=(0.0, 0.0), xyz=None):
    '''(u, v) in the view -> (x, y) on the sheet, feet. offset comes from
    horizun.calibrate_viewport (zero when the box is centred on the crop).'''
    view = _hz_viewport_view(viewport, view)
    cu, cv = _hz_crop_center_uv(view, xyz)
    s = float(view.Scale)
    du, dv = _hz_rotate((u - cu) / s, (v - cv) / s, _hz_rot_steps(viewport))
    c = viewport.GetBoxCenter()
    return (float(c.X) + du + offset[0], float(c.Y) + dv + offset[1])

def _hz_sheet_to_view(viewport, x, y, view=None, offset=(0.0, 0.0), xyz=None):
    view = _hz_viewport_view(viewport, view)
    cu, cv = _hz_crop_center_uv(view, xyz)
    s = float(view.Scale)
    c = viewport.GetBoxCenter()
    du, dv = _hz_rotate(x - float(c.X) - offset[0], y - float(c.Y) - offset[1], -_hz_rot_steps(viewport))
    return (cu + du * s, cv + dv * s)

def _hz_model_to_sheet(viewport, point, view=None, offset=(0.0, 0.0), xyz=None):
    view = _hz_viewport_view(viewport, view)
    u, v, w = _hz_model_to_view(view, point)
    return _hz_view_to_sheet(viewport, u, v, view, offset, xyz)

def _hz_sheet_to_model(viewport, x, y, w=0.0, view=None, offset=(0.0, 0.0), xyz=None):
    view = _hz_viewport_view(viewport, view)
    u, v = _hz_sheet_to_view(viewport, x, y, view, offset, xyz)
    return _hz_view_to_model(view, u, v, w)

def _hz_calibrate_viewport(viewport, model_point, measured_sheet_xy, view=None, xyz=None):
    '''The (dx, dy) to pass as offset= so that model_point lands where you MEASURED
    it on the sheet (a note, a PDF mark). Run once per viewport on a point you trust.'''
    px, py = _hz_model_to_sheet(viewport, model_point, view, (0.0, 0.0), xyz)
    return (float(measured_sheet_xy[0]) - px, float(measured_sheet_xy[1]) - py)

def _hz_ray_vector(view, direction='into_view'):
    f = _hz_view_frame(view)
    if direction == 'into_view':
        e = f['toward_eye']
        return (-e[0], -e[1], -e[2])
    if direction == 'toward_eye':
        return f['toward_eye']
    if direction == 'down':
        return (0.0, 0.0, -1.0)
    if isinstance(direction, (list, tuple)) and len(direction) == 3:
        return tuple(float(c) for c in direction)
    raise ValueError('direction must be into_view, toward_eye, down or a 3-number vector')

def _hz_view_ray(view, points_uv, depth_ft=None, categories=None, direction='into_view',
                 max_hits=1, include_links=True, view3d=None):
    '''Rays that start on the view plane at (u, v) and run into the model. One dict per
    point: uv, origin, hits=[{distance_ft, point, element_id, category, link_instance_id,
    linked_element_id}]. READS only; the 3D view is an existing non-template one (or
    view3d=), else a temporary one that is rolled back. categories: names such as
    OST_Walls or BuiltInCategory values.'''
    from System.Collections.Generic import List
    from Autodesk.Revit.DB import (BuiltInCategory, ElementMulticategoryFilter, FilteredElementCollector,
                                   FindReferenceTarget, ReferenceIntersector, View3D, Transaction,
                                   ViewFamily, ViewFamilyType, XYZ)
    vec = _hz_ray_vector(view, direction)
    dirx = XYZ(vec[0], vec[1], vec[2]).Normalize()
    temp = None
    v3 = view3d
    if v3 is None:
        for cand in FilteredElementCollector(doc).OfClass(View3D):
            if not cand.IsTemplate:
                v3 = cand
                break
    if v3 is None:
        vft = None
        for t in FilteredElementCollector(doc).OfClass(ViewFamilyType):
            if t.ViewFamily == ViewFamily.ThreeDimensional:
                vft = t
                break
        if vft is None:
            raise RuntimeError('No 3D view type exists to cast rays from')
        temp = Transaction(doc, 'Horizun temporary 3D view for rays')
        temp.Start()
        v3 = View3D.CreateIsometric(doc, vft.Id)
    try:
        if categories:
            cats = List[BuiltInCategory]()
            for c in categories:
                cats.Add(getattr(BuiltInCategory, c) if isinstance(c, str) else c)
            ri = ReferenceIntersector(ElementMulticategoryFilter(cats), FindReferenceTarget.Element, v3)
        else:
            ri = ReferenceIntersector(v3)
        try:
            ri.FindReferencesInRevitLinks = bool(include_links)
        except Exception:
            pass
        out = []
        for uv in points_uv:
            o = _hz_view_to_model(view, uv[0], uv[1], 0.0)
            hits = []
            for rwc in ri.Find(XYZ(o[0], o[1], o[2]), dirx):
                dist = float(rwc.Proximity)
                if depth_ft is not None and dist > depth_ft:
                    continue
                ref = rwc.GetReference()
                linked = ref.LinkedElementId
                has_link = linked is not None and _hz_id_value(linked) not in (None, -1)
                eid = linked if has_link else ref.ElementId
                el = None if has_link else doc.GetElement(ref.ElementId)
                cat = None
                if el is not None and el.Category is not None:
                    cat = el.Category.Name
                hits.append(dict(distance_ft=dist, point=_hz_t(ref.GlobalPoint), element_id=_hz_id_value(eid),
                                 category=cat,
                                 link_instance_id=(_hz_id_value(ref.ElementId) if has_link else None),
                                 linked_element_id=(_hz_id_value(linked) if has_link else None)))
            hits.sort(key=lambda h: h['distance_ft'])
            out.append(dict(uv=(uv[0], uv[1]), origin=o, hits=hits[:max(1, int(max_hits))]))
        return out
    finally:
        if temp is not None:
            temp.RollBack()
            temp.Dispose()

def _hz_leaders_capture(note):
    '''The leaders of a text note as plain data: [{end, elbow}] in model feet. Put
    them back with horizun.leaders_restore after an edit that moves or resets them.'''
    rows = []
    for ld in note.GetLeaders():
        row = dict(end=_hz_t(ld.End), elbow=None)
        try:
            row['elbow'] = _hz_t(ld.Elbow)
        except Exception:
            pass
        rows.append(row)
    return dict(note_id=_hz_id_value(note.Id), count=len(rows), leaders=rows)

def _hz_leaders_restore(note, captured, tolerance_ft=1e-4, xyz=None):
    '''Put captured leader ends and elbows back (inside YOUR transaction), then RE-READ
    them. Never guesses: with a different leader count nothing is touched. Trust
    `verified`, not `restored`.'''
    mk = xyz or _hz_new_xyz
    rows = captured['leaders']
    leaders = list(note.GetLeaders())
    if len(leaders) != len(rows):
        return dict(restored=False, verified=False, reason='leader count differs',
                    expected=len(rows), found=len(leaders), mismatches=[])
    for ld, row in zip(leaders, rows):
        ld.End = mk(row['end'])
        if row.get('elbow') is not None:
            try:
                ld.Elbow = mk(row['elbow'])
            except Exception:
                pass
    mism = []
    for i, (ld, row) in enumerate(zip(list(note.GetLeaders()), rows)):
        pairs = [('end', row['end'], _hz_t(ld.End))]
        if row.get('elbow') is not None:
            try:
                pairs.append(('elbow', row['elbow'], _hz_t(ld.Elbow)))
            except Exception:
                mism.append(dict(index=i, part='elbow', reason='unreadable after restore'))
        for part, want, got in pairs:
            gap = max(abs(want[k] - got[k]) for k in range(3))
            if gap > tolerance_ft:
                mism.append(dict(index=i, part=part, gap_ft=gap))
    return dict(restored=True, verified=not mism, mismatches=mism, count=len(rows))

horizun.model_to_view = _hz_model_to_view
horizun.view_to_model = _hz_view_to_model
horizun.view_to_sheet = _hz_view_to_sheet
horizun.sheet_to_view = _hz_sheet_to_view
horizun.model_to_sheet = _hz_model_to_sheet
horizun.sheet_to_model = _hz_sheet_to_model
horizun.calibrate_viewport = _hz_calibrate_viewport
horizun.view_ray = _hz_view_ray
horizun.leaders_capture = _hz_leaders_capture
horizun.leaders_restore = _hz_leaders_restore
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
