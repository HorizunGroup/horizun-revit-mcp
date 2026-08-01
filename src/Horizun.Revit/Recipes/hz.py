# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# The little that every recipe needs, in one place.
#
# These recipes are ports of tools that used to be pyRevit buttons. A button gets
# its input by asking a person to click things and answer dialogs; a recipe gets
# it as typed arguments, because there is nobody on the other end of an MCP call.
# That substitution is the whole of the port, and it lives here so each recipe
# does not reinvent it:
#
#   * ids in, elements out - and an id that does not resolve is REPORTED, not
#     silently dropped, because "I processed 8 of your 10" is the kind of quiet
#     arithmetic this project exists to refuse;
#   * one selection story (explicit ids, or a view, or the whole model), so every
#     tool takes the same three arguments and means the same thing by them;
#   * ElementId across Revit versions, the Python half of what Rid.cs does in C#.
#
# NO TRANSACTIONS IN HERE, and none in any recipe. The host owns the commit.
# -----------------------------------------------------------------------------
from Autodesk.Revit.DB import (
    ElementId, FilteredElementCollector, BuiltInCategory, BuiltInParameter
)


def bip(*names):
    """The first BuiltInParameter of these names that EXISTS in the running Revit,
    or None.

    BuiltInParameter is not stable across versions: FLOOR_HEIGHTOFFSET is real in
    older Revit and simply absent in 2026, where the name is
    FLOOR_HEIGHTABOVELEVEL_PARAM. Written as an attribute access, that absence is
    an AttributeError raised from inside the geometry - a tool that dies on its
    first element with a message about a 'type' object.

    This was not a hypothetical: split_floor_loops shipped with the wrong name and
    it took running it against a real Revit to find out. CI cannot catch this -
    a hosted runner has no RevitAPI.dll to check the enum against - so the guard
    has to live here, at the point of use, and say what it could not find."""
    for name in names:
        found = getattr(BuiltInParameter, name, None)
        if found is not None:
            return found
    return None


def param(element, *names):
    """An element's parameter, found by the first BuiltInParameter name that both
    exists in this Revit and resolves on this element. None when none does -
    which the caller must treat as 'not available here', never as zero."""
    for name in names:
        which = getattr(BuiltInParameter, name, None)
        if which is None:
            continue
        try:
            found = element.get_Parameter(which)
        except Exception:
            found = None
        if found is not None:
            return found
    return None


def eid(element_id):
    """The numeric value of an ElementId. Revit 2024+ has .Value (64-bit); older
    Revit only has .IntegerValue. Try the modern one first so the 2024+ build does
    not go through a deprecated property."""
    if element_id is None:
        return None
    try:
        return element_id.Value
    except AttributeError:
        return element_id.IntegerValue


def to_eid(value):
    """An ElementId from a number, on any supported Revit."""
    return ElementId(value)


def arg(args, key, default=None):
    """One argument, tolerating absence. Args arrive as a plain dict from the host."""
    if args is None:
        return default
    value = args.get(key, None)
    return default if value is None else value


def shape_editor(element, enable=False):
    """The SlabShapeEditor of a floor or toposolid, however this Revit exposes it.

    It is a METHOD - GetSlabShapeEditor() - on Revit 2026, and was a PROPERTY on
    older versions. The button this came from used the property, which means it
    could not have run on 2026 at all: the failure is an AttributeError from
    inside the geometry, nowhere near the line a reader would suspect.

    `enable=True` also turns editing on, which is required before any point can be
    added. Returns None when the element has no shape editor at all - a real
    answer, and not the same as one that failed."""
    editor = None
    getter = getattr(element, "GetSlabShapeEditor", None)
    if getter is not None:
        try:
            editor = getter()
        except Exception:
            editor = None
    if editor is None:
        editor = getattr(element, "SlabShapeEditor", None)
    if editor is None:
        return None

    if enable:
        try:
            if not editor.IsEnabled:
                editor.Enable()
        except Exception:
            try:
                editor.Enable()
            except Exception:
                pass
    return editor


def match_positions(wanted, actual, ceiling):
    """How many of `wanted` (x,y,z tuples) the model really has at `actual`, split
    into EXACT and NEAR, with the near-tolerance derived from the data.

    Why this exists, measured rather than assumed: writing shape points onto a
    toposolid, Revit SNAPS a point that lands on existing geometry - a slab edge -
    to that geometry, up to about 14mm away. Comparing rounded keys then reports
    those points as missing when they are plainly there, at the same elevation,
    a centimetre away on a terrain model. That is a false alarm, and a check that
    cries wolf on every run teaches people to ignore it.

    Widening the tolerance blindly would be the other failure: these recipes write
    rings only 5cm apart, so a generous radius would let one ring's vertex answer
    for another's and PROVE NOTHING. So the tolerance is half the smallest gap
    between any two DISTINCT wanted positions - below which no two of them can be
    confused - and never more than `ceiling`.

    Returns (exact, near, tolerance_used). The caller reports both numbers: 'here
    to the millimetre' and 'here, within the tolerance' are different claims and
    the difference belongs in the reply, not in a widened count."""
    wanted = list(wanted)
    actual = list(actual)
    if not wanted:
        return 0, 0, 0.0

    # Smallest gap between two distinct wanted points, via a grid so this stays
    # linear-ish instead of comparing every pair with every other.
    def cells(p, size):
        cx, cy, cz = int(p[0] // size), int(p[1] // size), int(p[2] // size)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    yield (cx + dx, cy + dy, cz + dz)

    probe = max(ceiling * 4.0, 1e-6)
    grid = {}
    for p in wanted:
        grid.setdefault((int(p[0] // probe), int(p[1] // probe), int(p[2] // probe)), []).append(p)

    smallest = None
    for p in wanted:
        for c in cells(p, probe):
            for q in grid.get(c, ()):
                if q is p:
                    continue
                d = ((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 + (p[2] - q[2]) ** 2) ** 0.5
                if d > 1e-9 and (smallest is None or d < smallest):
                    smallest = d

    tol = ceiling if smallest is None else min(ceiling, smallest / 2.0)

    agrid = {}
    for p in actual:
        agrid.setdefault((int(p[0] // probe), int(p[1] // probe), int(p[2] // probe)), []).append(p)

    exact = 0
    near = 0
    for p in wanted:
        best = None
        for c in cells(p, probe):
            for q in agrid.get(c, ()):
                d = ((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 + (p[2] - q[2]) ** 2) ** 0.5
                if best is None or d < best:
                    best = d
        if best is None:
            continue
        if best <= 1e-9:
            exact += 1
        elif best <= tol:
            near += 1

    return exact, near, tol


class Scope(object):
    """What a tool was pointed at, and what it could not resolve.

    `elements`   - the ones that resolved, in the order given
    `missing`    - ids that resolve to nothing at all
    `wrong_type` - ids that resolve to something of the wrong kind

    A caller that passed ten ids and sees eight elements can tell WHICH two fell
    out and why. That distinction is the reason this returns an object instead of
    a list."""

    def __init__(self):
        self.elements = []
        self.missing = []
        self.wrong_type = []

    def report(self):
        return {
            "resolved": len(self.elements),
            "missing_ids": self.missing,
            "wrong_type_ids": self.wrong_type,
        }


def resolve(doc, args, predicate, of_class=None, of_category=None):
    """The one selection story, in the order a caller expects to be obeyed.

    1. `element_ids`: exactly these, nothing else. An id that does not pass
       `predicate` comes back in wrong_type rather than being ignored.
    2. `view_id`: everything visible in that view that passes.
    3. otherwise: the whole model.

    `predicate` is what makes an element eligible for THIS tool (a Floor, a
    compound wall, a toposolid). It is applied in every branch, so the three
    routes cannot disagree about what the tool operates on."""
    scope = Scope()

    ids = arg(args, "element_ids")
    if ids:
        for raw in ids:
            element = doc.GetElement(to_eid(raw))
            if element is None:
                scope.missing.append(raw)
            elif not predicate(element):
                scope.wrong_type.append(raw)
            else:
                scope.elements.append(element)
        return scope

    view_id = arg(args, "view_id")
    if view_id:
        collector = FilteredElementCollector(doc, to_eid(view_id))
    else:
        collector = FilteredElementCollector(doc).WhereElementIsNotElementType()

    if of_class is not None:
        collector = collector.OfClass(of_class)
    if of_category is not None:
        collector = collector.OfCategory(of_category)

    for element in collector:
        if predicate(element):
            scope.elements.append(element)
    return scope


def still_exists(doc, element_id_value):
    """Is this id a live element in the model RIGHT NOW. The verification half of
    every recipe is built out of this: after the commit, ask the model."""
    try:
        return doc.GetElement(to_eid(element_id_value)) is not None
    except Exception:
        return False


def brief(exc, limit=160):
    """An exception as one short line. Recipes collect these per element so one bad
    element is reported by name instead of taking the batch down."""
    try:
        text = str(exc)
    except Exception:
        text = "unprintable error"
    text = " ".join(text.split())
    return text[:limit]
