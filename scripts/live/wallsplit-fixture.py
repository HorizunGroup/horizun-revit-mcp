# -*- coding: utf-8 -*-
"""
The fixture for the wall-split live campaign, built in one transaction.

Every wall is placed on its own X band so that nothing joins by accident; the
cases that WANT a join make it deliberately. Each wall is returned with the case
numbers it serves, so the runner never has to guess which id is which.

Nothing here is saved. The document is disposable and created by
wallsplit-newmodel.ps1 from Revit's own multi-discipline metric template.
"""
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallKind, Level, XYZ, Line, Arc,
    Transaction, ElementId, BuiltInParameter, WallLocationLine, FamilySymbol,
    BuiltInCategory, Opening, CurveArray, JoinGeometryUtils, WallUtils,
    CompoundStructure, CompoundStructureLayer, MaterialFunctionAssignment,
    Material, Element, ShellLayerType
)
from Autodesk.Revit.DB.Structure import StructuralType

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document

made = {}          # case key -> {"wall": id, ...}
notes = []


def mm(v):
    return v * MM


def first_level():
    levels = sorted(
        [l for l in FilteredElementCollector(doc).OfClass(Level)],
        key=lambda l: l.Elevation)
    return levels[0]


def levels_two():
    levels = sorted(
        [l for l in FilteredElementCollector(doc).OfClass(Level)],
        key=lambda l: l.Elevation)
    return levels[0], (levels[1] if len(levels) > 1 else levels[0])


def wall_types():
    out = {}
    for wt in FilteredElementCollector(doc).OfClass(WallType):
        try:
            if wt.Kind != WallKind.Basic:
                continue
            cs = wt.GetCompoundStructure()
            if cs is None:
                continue
            out[wt.Name] = (wt, cs, len(cs.GetLayers()),
                            cs.GetFirstCoreLayerIndex(), cs.GetLastCoreLayerIndex())
        except Exception:
            pass
    return out


types = wall_types()
level, level2 = levels_two()

# The workhorse: seven layers, a single-layer core at index 4.
MULTI = None
for name in ('M_Exterior - Brick on Mtl. Stud', 'Exterior - Brick on Mtl. Stud',
             'M_Exterior - CMU on Mtl. Stud', 'Exterior - Block on Mtl. Stud'):
    if name in types:
        MULTI = types[name][0]
        break

# A core made of SEVERAL layers - a different code path for the carrier choice.
MULTI_WIDE_CORE = types.get('M_Exterior - CMU Insulated', (None,))[0]
if MULTI_WIDE_CORE is None:
    MULTI_WIDE_CORE = types.get('Exterior - CMU Insulated', (None,))[0]
if MULTI_WIDE_CORE is None:
    for _name, _facts in types.items():
        if _facts[3] >= 0 and _facts[4] > _facts[3]:
            MULTI_WIDE_CORE = _facts[0]
            break

# Something with one layer, for the single_layer refusal.
SINGLE = None
for wt in FilteredElementCollector(doc).OfClass(WallType):
    try:
        if wt.Kind != WallKind.Basic:
            continue
        cs = wt.GetCompoundStructure()
        if cs is not None and len(cs.GetLayers()) == 1:
            SINGLE = wt
            break
    except Exception:
        pass

STACKED = None
for wt in FilteredElementCollector(doc).OfClass(WallType):
    try:
        if wt.Kind == WallKind.Stacked:
            STACKED = wt
            break
    except Exception:
        pass

CURTAIN = None
for wt in FilteredElementCollector(doc).OfClass(WallType):
    try:
        if wt.Kind == WallKind.Curtain:
            CURTAIN = wt
            break
    except Exception:
        pass


def door_symbol():
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(FamilySymbol):
        return s
    return None


def window_symbol():
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).OfClass(FamilySymbol):
        return s
    return None


def make_wall(x, y0, y1, wtype, height=3000.0, structural=False):
    line = Line.CreateBound(XYZ(mm(x), mm(y0), 0), XYZ(mm(x), mm(y1), 0))
    w = Wall.Create(doc, line, wtype.Id, level.Id, mm(height), 0.0, False, structural)
    return w


def make_arc_wall(cx, cy, radius_mm, a0, a1, wtype, height=3000.0):
    import math
    c = XYZ(mm(cx), mm(cy), 0)
    arc = Arc.Create(c, mm(radius_mm), math.radians(a0), math.radians(a1), XYZ.BasisX, XYZ.BasisY)
    return Wall.Create(doc, arc, wtype.Id, level.Id, mm(height), 0.0, False, False)


def set_location_line(w, value):
    p = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)
    if p is not None and not p.IsReadOnly:
        p.Set(int(value))


def record(key, wall, **extra):
    d = {"wall": wall.Id.Value, "unique_id": wall.UniqueId,
         "type": doc.GetElement(wall.GetTypeId()).Name}
    d.update(extra)
    made[key] = d


tx = Transaction(doc, 'HZ wallsplit fixture')
tx.Start()
try:
    x = 0.0
    STEP = 6000.0     # far enough apart that nothing joins by accident

    # Revit 2026's current English metric template no longer ships the old
    # `M_Exterior - CMU Insulated` type. Build the topology this case needs
    # explicitly: one exterior shell, two core layers, one interior shell.
    if MULTI_WIDE_CORE is None and MULTI is not None:
        source_layers = list(MULTI.GetCompoundStructure().GetLayers())
        material = source_layers[0].MaterialId if source_layers else ElementId.InvalidElementId
        MULTI_WIDE_CORE = MULTI.Duplicate('HZ_WideCore')
        wide = CompoundStructure.CreateSimpleCompoundStructure([
            CompoundStructureLayer(mm(15), MaterialFunctionAssignment.Finish1, material),
            CompoundStructureLayer(mm(90), MaterialFunctionAssignment.Structure, material),
            CompoundStructureLayer(mm(70), MaterialFunctionAssignment.Structure, material),
            CompoundStructureLayer(mm(15), MaterialFunctionAssignment.Finish2, material),
        ])
        wide.SetNumberOfShellLayers(ShellLayerType.Exterior, 1)
        wide.SetNumberOfShellLayers(ShellLayerType.Interior, 1)
        MULTI_WIDE_CORE.SetCompoundStructure(wide)

    # ---- 1  five-plus-layer wall, single-layer core -------------------------
    if MULTI:
        w = make_wall(x, 0, 5000, MULTI); record('c01', w); x += STEP

        # ---- 5  flipped --------------------------------------------------------
        w = make_wall(x, 0, 5000, MULTI); w.Flip(); record('c05', w, flipped=True); x += STEP

        # ---- 6  one wall per location line ------------------------------------
        for name, value in (('WallCenterline', WallLocationLine.WallCenterline),
                            ('CoreCenterline', WallLocationLine.CoreCenterline),
                            ('FinishFaceExterior', WallLocationLine.FinishFaceExterior),
                            ('FinishFaceInterior', WallLocationLine.FinishFaceInterior),
                            ('CoreExterior', WallLocationLine.CoreExterior),
                            ('CoreInterior', WallLocationLine.CoreInterior)):
            w = make_wall(x, 0, 5000, MULTI)
            set_location_line(w, value)
            record('c06_' + name, w, location_line=name)
            x += STEP

        # ---- 7/8/9  arcs -------------------------------------------------------
        w = make_arc_wall(x + 4000, 0, 4000, 200, 250, MULTI); record('c07_arc', w); x += STEP * 2
        w = make_arc_wall(x + 4000, 0, 4000, 200, 250, MULTI); w.Flip(); record('c09_arc_flipped', w); x += STEP * 2

        # ---- 11  top constrained ----------------------------------------------
        w = make_wall(x, 0, 5000, MULTI)
        p = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)
        if p is not None and not p.IsReadOnly:
            p.Set(level2.Id)
        record('c11_top_constrained', w, top_level=level2.Id.Value); x += STEP

        # ---- 12  pinned --------------------------------------------------------
        w = make_wall(x, 0, 5000, MULTI); w.Pinned = True; record('c12_pinned', w); x += STEP

        # ---- 13/15/16  a door, a window, and several ---------------------------
        ds = door_symbol()
        ws = window_symbol()
        if ds is not None:
            if not ds.IsActive:
                ds.Activate(); doc.Regenerate()
            w = make_wall(x, 0, 6000, MULTI)
            doc.Regenerate()
            d = doc.Create.NewFamilyInstance(XYZ(mm(x), mm(2000), 0), ds, w, level,
                                             StructuralType.NonStructural)
            record('c13_door', w, door=d.Id.Value, door_unique=d.UniqueId)
            x += STEP

        if ws is not None:
            if not ws.IsActive:
                ws.Activate(); doc.Regenerate()
            w = make_wall(x, 0, 6000, MULTI)
            doc.Regenerate()
            win = doc.Create.NewFamilyInstance(XYZ(mm(x), mm(2000), mm(1000)), ws, w, level,
                                               StructuralType.NonStructural)
            record('c15_window', w, window=win.Id.Value, window_unique=win.UniqueId)
            x += STEP

        if ds is not None and ws is not None:
            w = make_wall(x, 0, 12000, MULTI)
            doc.Regenerate()
            ids = []
            for yy in (2000.0, 6000.0):
                ids.append(doc.Create.NewFamilyInstance(XYZ(mm(x), mm(yy), 0), ds, w, level,
                                                        StructuralType.NonStructural).Id.Value)
            for yy in (4000.0, 9000.0):
                ids.append(doc.Create.NewFamilyInstance(XYZ(mm(x), mm(yy), mm(1000)), ws, w, level,
                                                        StructuralType.NonStructural).Id.Value)
            record('c16_many', w, inserts=ids)
            x += STEP

        # ---- 17  a rectangular opening ----------------------------------------
        w = make_wall(x, 0, 6000, MULTI)
        doc.Regenerate()
        try:
            op = doc.Create.NewOpening(w, XYZ(mm(x), mm(1500), mm(500)),
                                       XYZ(mm(x), mm(3000), mm(2200)))
            record('c17_opening', w, opening=op.Id.Value, opening_unique=op.UniqueId)
        except Exception as ex:
            notes.append('c17 opening failed: ' + str(ex))
            record('c17_opening', w, opening=None)
        x += STEP

        # ---- 19  joined at both ends ------------------------------------------
        centre = make_wall(x, 0, 5000, MULTI)
        left = Wall.Create(doc, Line.CreateBound(XYZ(mm(x - 3000), mm(0), 0), XYZ(mm(x), mm(0), 0)),
                           MULTI.Id, level.Id, mm(3000.0), 0.0, False, False)
        right = Wall.Create(doc, Line.CreateBound(XYZ(mm(x), mm(5000), 0), XYZ(mm(x + 3000), mm(5000), 0)),
                            MULTI.Id, level.Id, mm(3000.0), 0.0, False, False)
        doc.Regenerate()
        record('c19_joined', centre, neighbours=[left.Id.Value, right.Id.Value])
        x += STEP * 2

        # ---- 32  a second eligible wall, for the mixed batch --------------------
        w = make_wall(x, 0, 5000, MULTI); record('c32_valid', w); x += STEP

    # ---- 2  a core of several layers ------------------------------------------
    if MULTI_WIDE_CORE:
        w = make_wall(x, 0, 5000, MULTI_WIDE_CORE); record('c02_wide_core', w); x += STEP

    # ---- 4 / single_layer refusal ----------------------------------------------
    if SINGLE:
        w = make_wall(x, 0, 5000, SINGLE); record('c04_single_layer', w); x += STEP

    # ---- 10  stacked wall -------------------------------------------------------
    if STACKED:
        try:
            w = make_wall(x, 0, 5000, STACKED); record('c10_stacked', w); x += STEP
        except Exception as ex:
            notes.append('stacked wall could not be created: ' + str(ex))

    # ---- curtain wall, for not_basic_wall ---------------------------------------
    if CURTAIN:
        try:
            w = make_wall(x, 0, 5000, CURTAIN); record('c_curtain', w); x += STEP
        except Exception as ex:
            notes.append('curtain wall could not be created: ' + str(ex))

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    notes.append('FIXTURE FAILED: ' + str(ex))

__output__ = {
    "status": status,
    "summary": "wall-split fixture",
    "created_ids": sorted([v["wall"] for v in made.values()]),
    "made": made,
    "notes": notes,
    "types_seen": {
        "multi": MULTI.Name if MULTI else None,
        "multi_wide_core": MULTI_WIDE_CORE.Name if MULTI_WIDE_CORE else None,
        "single": SINGLE.Name if SINGLE else None,
        "stacked": STACKED.Name if STACKED else None,
        "curtain": CURTAIN.Name if CURTAIN else None,
    },
    "level": level.Id.Value,
    "level2": level2.Id.Value,
}
