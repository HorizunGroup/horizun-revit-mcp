# -*- coding: utf-8 -*-
"""
The FIFTH fixture pass: the cases the previous campaign could not reach.

Two different reasons brought them here, and they are not the same reason:

  * 46, 50, 53 were never BUILT. The previous session ran out of fixture passes
    before it got to a wall carrying a door AND a footing AND a bar, or a wall
    whose bar can be moved between the dry run and the apply. Those are ordinary
    walls; they just did not exist yet.

  * 39, 40, 41, 42 were reported as "this template carries no type". That claim
    is re-MEASURED here rather than repeated: every attempt records the number of
    candidate types actually found and the exception Revit actually raised. A
    case that stays uncovered stays uncovered with its reason in the artifact,
    and a count of zero is evidence - "I could not find one" is not.

Nothing here is inferred. Anything Revit refuses is recorded in `unbuildable`
with the refusal text, and the case that depends on it is reported blocked.
"""
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, Level, XYZ, Line, Curve,
    Transaction, ElementId, BuiltInParameter, FamilySymbol, BuiltInCategory,
    Material, Structure
)
from Autodesk.Revit.DB.Structure import (
    StructuralType, RebarBarType, RebarStyle, RebarHookOrientation,
    Rebar, AreaReinforcement, PathReinforcement, RebarHostData,
    AreaReinforcementType, PathReinforcementType, StructuralWallUsage
)
from System.Collections.Generic import List

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document

made, unbuildable, notes, counts = {}, {}, [], {}


def mm(v):
    return v * MM


def find_type(name):
    for wt in FilteredElementCollector(doc).OfClass(WallType):
        if wt.Name == name:
            return wt
    return None


levels = sorted([l for l in FilteredElementCollector(doc).OfClass(Level)], key=lambda l: l.Elevation)
level = levels[0]
STRUCT = find_type('HZ_StructuralSandwich')
MULTI = find_type('M_Exterior - Brick on Mtl. Stud') or find_type('Exterior - Brick on Mtl. Stud')


def make_wall(x, y0, y1, wtype, height=3000.0, structural=False):
    line = Line.CreateBound(XYZ(mm(x), mm(y0), 0), XYZ(mm(x), mm(y1), 0))
    w = Wall.Create(doc, line, wtype.Id, level.Id, mm(height), 0.0, False, structural)
    if structural:
        p = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM)
        if p is not None and not p.IsReadOnly:
            p.Set(int(StructuralWallUsage.Bearing))
    return w


def record(key, wall, **extra):
    d = {"wall": wall.Id.Value, "unique_id": wall.UniqueId}
    try:
        d["type"] = doc.GetElement(wall.GetTypeId()).Name
    except Exception:
        pass
    d.update(extra)
    made[key] = d


def bar_type():
    best = None
    for t in FilteredElementCollector(doc).OfClass(RebarBarType):
        if best is None or t.BarNominalDiameter < best.BarNominalDiameter:
            best = t
    return best


def vertical_bar(w, x, y, bt):
    """A vertical bar inside the 300 mm core, clear of both faces."""
    curves = List[Curve]()
    curves.Add(Line.CreateBound(XYZ(mm(x), mm(y), mm(200)), XYZ(mm(x), mm(y), mm(2600))))
    return Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                  XYZ(0, 1, 0), curves,
                                  RebarHookOrientation.Right, RebarHookOrientation.Right,
                                  True, True)


def a_door():
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsElementType():
        if isinstance(s, FamilySymbol):
            return s
    return None


tx = Transaction(doc, 'HZ wallsplit fixture 5')
tx.Start()
try:
    if STRUCT is None:
        raise Exception('HZ_StructuralSandwich is absent - fixture pass three did not run on this document')

    bt = bar_type()
    X = 800000.0
    STEP = 8000.0

    # ---- the CANARY: the simplest thing that must work ---------------------
    # Straight, multilayer, vertical, and carrying nothing at all: no door, no
    # window, no bar, no footing, no tag, no dimension, no join, no group, no
    # design option, no edited profile, no attachment, no pin. If this one does
    # not convert, nothing downstream is worth running, and every failure after
    # it would be the same failure counted many times.
    w = make_wall(X, 0, 6000, MULTI)
    doc.Regenerate()
    record('canary', w)
    X += STEP

    # ---- 46  door AND footing AND bar, all three on ONE wall ---------------
    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    entry = {}
    sym = a_door()
    if sym is None:
        unbuildable['c46_all_three'] = 'no door symbol in this document'
    else:
        if not sym.IsActive:
            sym.Activate(); doc.Regenerate()
        try:
            d = doc.Create.NewFamilyInstance(XYZ(mm(X), mm(2000), 0), sym, w, level,
                                             StructuralType.NonStructural)
            doc.Regenerate()
            entry['door'] = d.Id.Value
            entry['door_unique'] = d.UniqueId
        except Exception as ex:
            unbuildable['c46_door'] = 'NewFamilyInstance: ' + str(ex)
    try:
        from Autodesk.Revit.DB import WallFoundation, WallFoundationType
        ft = None
        for cand in FilteredElementCollector(doc).OfClass(WallFoundationType):
            ft = cand
            break
        if ft is None:
            unbuildable['c46_foundation'] = 'no WallFoundationType in this document'
        else:
            f = WallFoundation.Create(doc, ft.Id, w.Id)
            doc.Regenerate()
            entry['foundation'] = f.Id.Value
            entry['foundation_unique'] = f.UniqueId
    except Exception as ex:
        unbuildable['c46_foundation'] = 'WallFoundation.Create: ' + str(ex)
    if bt is not None:
        try:
            r = vertical_bar(w, X, 4500, bt)
            doc.Regenerate()
            entry['rebar'] = r.Id.Value
            entry['rebar_unique'] = r.UniqueId
        except Exception as ex:
            unbuildable['c46_rebar'] = 'Rebar.CreateFromCurves: ' + str(ex)
    record('c46_all_three', w, **entry)
    X += STEP

    # ---- 50  a wall whose bar can be MOVED between the dry run and the apply
    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    if bt is None:
        unbuildable['c50_stale_bar'] = 'no RebarBarType in the document'
        record('c50_stale_bar', w, rebar=None)
    else:
        try:
            r = vertical_bar(w, X, 3000, bt)
            doc.Regenerate()
            record('c50_stale_bar', w, rebar=r.Id.Value, rebar_unique=r.UniqueId)
        except Exception as ex:
            unbuildable['c50_stale_bar'] = 'Rebar.CreateFromCurves: ' + str(ex)
            record('c50_stale_bar', w, rebar=None)
    X += STEP

    # ---- 53  a structural wall for the mixed architectural/structural batch -
    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    if bt is not None:
        try:
            r = vertical_bar(w, X, 3000, bt)
            doc.Regenerate()
            record('c53_structural', w, rebar=r.Id.Value, rebar_unique=r.UniqueId)
        except Exception as ex:
            unbuildable['c53_structural'] = 'Rebar.CreateFromCurves: ' + str(ex)
            record('c53_structural', w, rebar=None)
    else:
        record('c53_structural', w, rebar=None)
    X += STEP

    # ---- 51 / 52  two more ordinary structural walls -----------------------
    # 51 needs a structural wall to convert TWICE; 52 needs one to convert and
    # then lose a sibling. Reusing a wall another case converts would make the
    # two cases share an outcome, and a shared outcome is one measurement
    # reported as two.
    for key in ('c51_second_apply', 'c52_structural_partial'):
        w = make_wall(X, 0, 6000, STRUCT, structural=True)
        doc.Regenerate()
        extra = {}
        if bt is not None:
            try:
                r = vertical_bar(w, X, 3000, bt)
                doc.Regenerate()
                extra = {'rebar': r.Id.Value, 'rebar_unique': r.UniqueId}
            except Exception as ex:
                unbuildable[key] = 'Rebar.CreateFromCurves: ' + str(ex)
        record(key, w, **extra)
        X += STEP

    # ---- 39  a stirrup: a CLOSED loop, plane normal along the wall ---------
    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    if bt is None:
        unbuildable['c39_stirrup'] = 'no RebarBarType in the document'
        record('c39_stirrup', w, rebar=None)
    else:
        try:
            # The loop lives in the XZ plane at one y; its normal is the wall
            # direction. x stays within the 300 mm core, z well inside the wall.
            y = 3000.0
            x0, x1 = X - 100.0, X + 100.0
            z0, z1 = 400.0, 1400.0
            pts = [XYZ(mm(x0), mm(y), mm(z0)), XYZ(mm(x1), mm(y), mm(z0)),
                   XYZ(mm(x1), mm(y), mm(z1)), XYZ(mm(x0), mm(y), mm(z1))]
            curves = List[Curve]()
            for i in range(4):
                curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
            r = Rebar.CreateFromCurves(doc, RebarStyle.StirrupTie, bt, None, None, w,
                                       XYZ(0, 1, 0), curves,
                                       RebarHookOrientation.Right, RebarHookOrientation.Right,
                                       True, True)
            doc.Regenerate()
            record('c39_stirrup', w, rebar=r.Id.Value, rebar_unique=r.UniqueId,
                   style='StirrupTie')
        except Exception as ex:
            unbuildable['c39_stirrup'] = 'stirrup CreateFromCurves: ' + str(ex)
            record('c39_stirrup', w, rebar=None)
    X += STEP

    # ---- 40 / 41  Area and Path reinforcement, re-measured -----------------
    area_types = [t for t in FilteredElementCollector(doc).OfClass(AreaReinforcementType)]
    path_types = [t for t in FilteredElementCollector(doc).OfClass(PathReinforcementType)]
    counts['area_reinforcement_types'] = len(area_types)
    counts['path_reinforcement_types'] = len(path_types)
    counts['rebar_bar_types'] = len([t for t in FilteredElementCollector(doc).OfClass(RebarBarType)])

    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    if not area_types or bt is None:
        unbuildable['c40_area'] = ('AreaReinforcementType count=%d, RebarBarType count=%d'
                                   % (len(area_types), counts['rebar_bar_types']))
        record('c40_area', w, area=None)
    else:
        try:
            curves = List[Curve]()
            pts = [XYZ(mm(X), mm(1000), mm(400)), XYZ(mm(X), mm(5000), mm(400)),
                   XYZ(mm(X), mm(5000), mm(2400)), XYZ(mm(X), mm(1000), mm(2400))]
            for i in range(4):
                curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
            a = AreaReinforcement.Create(doc, w, curves, XYZ(0, 0, 1),
                                         area_types[0].Id, bt.Id, ElementId.InvalidElementId)
            doc.Regenerate()
            record('c40_area', w, area=a.Id.Value, area_unique=a.UniqueId)
        except Exception as ex:
            unbuildable['c40_area'] = 'AreaReinforcement.Create: ' + str(ex)
            record('c40_area', w, area=None)
    X += STEP

    w = make_wall(X, 0, 6000, STRUCT, structural=True)
    doc.Regenerate()
    if not path_types or bt is None:
        unbuildable['c41_path'] = ('PathReinforcementType count=%d, RebarBarType count=%d'
                                   % (len(path_types), counts['rebar_bar_types']))
        record('c41_path', w, path=None)
    else:
        try:
            curves = List[Curve]()
            curves.Add(Line.CreateBound(XYZ(mm(X), mm(1000), mm(1500)),
                                        XYZ(mm(X), mm(5000), mm(1500))))
            pth = PathReinforcement.Create(doc, w, curves, True, path_types[0].Id, bt.Id,
                                           ElementId.InvalidElementId, ElementId.InvalidElementId)
            doc.Regenerate()
            record('c41_path', w, path=pth.Id.Value, path_unique=pth.UniqueId)
        except Exception as ex:
            unbuildable['c41_path'] = 'PathReinforcement.Create: ' + str(ex)
            record('c41_path', w, path=None)
    X += STEP

    # ---- 42  Fabric, re-measured -------------------------------------------
    try:
        from Autodesk.Revit.DB.Structure import FabricSheetType, FabricAreaType
        fabric_sheet_types = [t for t in FilteredElementCollector(doc).OfClass(FabricSheetType)]
        fabric_area_types = [t for t in FilteredElementCollector(doc).OfClass(FabricAreaType)]
    except Exception as ex:
        fabric_sheet_types, fabric_area_types = [], []
        notes.append('fabric types not enumerable: ' + str(ex))
    counts['fabric_sheet_types'] = len(fabric_sheet_types)
    counts['fabric_area_types'] = len(fabric_area_types)
    if not fabric_sheet_types or not fabric_area_types:
        unbuildable['c42_fabric'] = ('FabricSheetType count=%d, FabricAreaType count=%d'
                                     % (len(fabric_sheet_types), len(fabric_area_types)))
    else:
        w = make_wall(X, 0, 6000, STRUCT, structural=True)
        doc.Regenerate()
        try:
            from Autodesk.Revit.DB.Structure import FabricArea
            curves = List[Curve]()
            pts = [XYZ(mm(X), mm(1000), mm(400)), XYZ(mm(X), mm(5000), mm(400)),
                   XYZ(mm(X), mm(5000), mm(2400)), XYZ(mm(X), mm(1000), mm(2400))]
            for i in range(4):
                curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
            fa = FabricArea.Create(doc, w, curves, XYZ(0, 0, 1), fabric_area_types[0].Id,
                                   fabric_sheet_types[0].Id)
            doc.Regenerate()
            record('c42_fabric', w, fabric=fa.Id.Value, fabric_unique=fa.UniqueId)
        except Exception as ex:
            unbuildable['c42_fabric'] = 'FabricArea.Create: ' + str(ex)
            record('c42_fabric', w, fabric=None)
        X += STEP

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    notes.append('FIXTURE 5 FAILED: ' + str(ex))

# Self-verification: every wall recorded is re-read from the model, so what comes
# back is what the document holds rather than what the script believes it made.
verified = {}
for key, entry in made.items():
    try:
        el = doc.GetElement(ElementId(entry['wall']))
        verified[key] = {
            'exists': el is not None,
            'is_wall': isinstance(el, Wall),
            'type': (doc.GetElement(el.GetTypeId()).Name if el is not None else None),
        }
    except Exception as ex:
        verified[key] = {'exists': False, 'error': str(ex)}

__output__ = {
    "status": status,
    "summary": "fixture pass five: 39, 40, 41, 42, 46, 50, 51, 52, 53",
    "made": made,
    "unbuildable": unbuildable,
    "type_counts": counts,
    "verified": verified,
    "notes": notes,
}
