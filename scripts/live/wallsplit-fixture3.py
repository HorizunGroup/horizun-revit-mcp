# -*- coding: utf-8 -*-
"""
The THIRD fixture pass: the structural cases, plus the two geometric ones the
first two passes did not reach.

Walls that host reinforcement must be structural walls and valid rebar hosts, so
they are created that way and RebarHostData.IsValidHost is asked rather than
assumed. Anything Revit will not build is reported unbuildable with its reason.
"""
import math
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallKind, Level, XYZ, Line,
    Transaction, ElementId, BuiltInParameter, FamilySymbol, BuiltInCategory,
    CompoundStructure, CompoundStructureLayer, MaterialFunctionAssignment,
    ShellLayerType, Material, WallSweep, WallSweepInfo, WallSweepType,
    FamilyInstance, Options
)
from Autodesk.Revit.DB.Structure import (
    StructuralType, RebarBarType, RebarStyle, RebarHookOrientation,
    RebarHookType, Rebar, AreaReinforcement, PathReinforcement,
    RebarHostData, AreaReinforcementType, PathReinforcementType,
    RebarCoverType, StructuralWallUsage
)
from System.Collections.Generic import List

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document

made = {}
unbuildable = {}
notes = []


def mm(v):
    return v * MM


def note(t):
    notes.append(t)


def levels():
    ls = sorted([l for l in FilteredElementCollector(doc).OfClass(Level)], key=lambda l: l.Elevation)
    return ls[0], (ls[1] if len(ls) > 1 else ls[0])


level, level2 = levels()


def find_type(name):
    for wt in FilteredElementCollector(doc).OfClass(WallType):
        if wt.Name == name:
            return wt
    return None


MULTI = find_type('M_Exterior - Brick on Mtl. Stud') or find_type('Exterior - Brick on Mtl. Stud')


def any_material():
    for m in FilteredElementCollector(doc).OfClass(Material):
        return m.Id
    return ElementId.InvalidElementId


def make_type(base, name, layers, ext_shell, int_shell):
    existing = find_type(name)
    if existing is not None:
        return existing
    new = base.Duplicate(name)
    cs_layers = []
    for fn, w, mat in layers:
        cs_layers.append(CompoundStructureLayer(mm(w), fn, mat))
    cs = CompoundStructure.CreateSimpleCompoundStructure(cs_layers)
    cs.SetNumberOfShellLayers(ShellLayerType.Exterior, ext_shell)
    cs.SetNumberOfShellLayers(ShellLayerType.Interior, int_shell)
    new.SetCompoundStructure(cs)
    return new


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
        if best is None:
            best = t
        try:
            if t.BarNominalDiameter < best.BarNominalDiameter:
                best = t
        except Exception:
            pass
    return best


def hook_none():
    return ElementId.InvalidElementId


tx = Transaction(doc, 'HZ wallsplit fixture 3')
tx.Start()
try:
    mat = any_material()
    X = 400000.0
    STEP = 8000.0

    # ---- 4  a wall whose CORE has no volume at all --------------------------
    t_nocore = make_type(MULTI, 'HZ_NoValidCore',
                         [(MaterialFunctionAssignment.Finish1, 20.0, mat),
                          (MaterialFunctionAssignment.Membrane, 0.0, mat),
                          (MaterialFunctionAssignment.Finish2, 20.0, mat)],
                         ext_shell=1, int_shell=1)
    w = make_wall(X, 0, 5000, t_nocore); record('c04_no_valid_core', w); X += STEP

    # ---- a structural wall type thick enough to hold bars -------------------
    t_struct = make_type(MULTI, 'HZ_StructuralSandwich',
                         [(MaterialFunctionAssignment.Finish1, 20.0, mat),
                          (MaterialFunctionAssignment.Structure, 300.0, mat),
                          (MaterialFunctionAssignment.Finish2, 20.0, mat)],
                         ext_shell=1, int_shell=1)
    # A valid compound rebar host whose future carrier is intentionally thinner
    # than the smallest loaded bar. The bar can be hosted by the original 44 mm
    # wall, but no relocation can contain its solid radius inside the 4 mm core.
    t_narrow_core = make_type(MULTI, 'HZ_StructuralNarrowCore',
                              [(MaterialFunctionAssignment.Finish1, 20.0, mat),
                               (MaterialFunctionAssignment.Structure, 4.0, mat),
                               (MaterialFunctionAssignment.Finish2, 20.0, mat)],
                              ext_shell=1, int_shell=1)

    # ---- 36  wall with a continuous footing ---------------------------------
    foundation_type = None
    for ft in FilteredElementCollector(doc).OfClass(WallType):
        pass
    from Autodesk.Revit.DB import WallFoundationType
    for ft in FilteredElementCollector(doc).OfClass(WallFoundationType):
        foundation_type = ft
        break

    w = make_wall(X, 0, 6000, t_struct, structural=True)
    doc.Regenerate()
    if foundation_type is not None:
        try:
            from Autodesk.Revit.DB import WallFoundation
            f = WallFoundation.Create(doc, foundation_type.Id, w.Id)
            doc.Regenerate()
            record('c36_foundation', w, foundation=f.Id.Value, foundation_unique=f.UniqueId)
        except Exception as ex:
            unbuildable['c36_foundation'] = 'WallFoundation.Create: ' + str(ex)
            record('c36_foundation', w, foundation=None)
    else:
        unbuildable['c36_foundation'] = 'this document has no WallFoundationType loaded'
        record('c36_foundation', w, foundation=None)
    X += STEP

    bt = bar_type()
    if bt is None:
        unbuildable['rebar'] = 'no RebarBarType in the document'
    else:
        # ---- 37  a single bar ----------------------------------------------
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        host_ok = False
        try:
            host_ok = RebarHostData.GetRebarHostData(w) is not None
        except Exception:
            host_ok = False
        if not host_ok:
            unbuildable['c37_single_bar'] = 'the wall is not a valid rebar host'
            record('c37_single_bar', w, rebar=None)
        else:
            try:
                # a vertical bar inside the 300 mm core, well clear of both faces
                curves = List[object]()
                from Autodesk.Revit.DB import Curve
                curves = List[Curve]()
                curves.Add(Line.CreateBound(XYZ(mm(X), mm(1000), mm(200)),
                                            XYZ(mm(X), mm(1000), mm(2600))))
                r = Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                           XYZ(0, 1, 0), curves,
                                           RebarHookOrientation.Right, RebarHookOrientation.Right,
                                           True, True)
                doc.Regenerate()
                record('c37_single_bar', w, rebar=r.Id.Value, rebar_unique=r.UniqueId)
            except Exception as ex:
                unbuildable['c37_single_bar'] = 'Rebar.CreateFromCurves: ' + str(ex)
                record('c37_single_bar', w, rebar=None)
        X += STEP

        # ---- 38  a distributed set -----------------------------------------
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        try:
            from Autodesk.Revit.DB import Curve
            curves = List[Curve]()
            curves.Add(Line.CreateBound(XYZ(mm(X), mm(500), mm(200)), XYZ(mm(X), mm(500), mm(2600))))
            r = Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                       XYZ(0, 1, 0), curves,
                                       RebarHookOrientation.Right, RebarHookOrientation.Right,
                                       True, True)
            r.GetShapeDrivenAccessor().SetLayoutAsNumberWithSpacing(6, mm(800), True, True, True)
            doc.Regenerate()
            record('c38_distributed', w, rebar=r.Id.Value, rebar_unique=r.UniqueId)
        except Exception as ex:
            unbuildable['c38_distributed'] = 'distributed set: ' + str(ex)
            record('c38_distributed', w, rebar=None)
        X += STEP

        # ---- 44  foundation AND rebar on one wall ---------------------------
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        extra = {}
        if foundation_type is not None:
            try:
                from Autodesk.Revit.DB import WallFoundation
                f = WallFoundation.Create(doc, foundation_type.Id, w.Id)
                extra['foundation'] = f.Id.Value
                doc.Regenerate()
            except Exception as ex:
                note('c44 foundation: ' + str(ex))
        try:
            from Autodesk.Revit.DB import Curve
            curves = List[Curve]()
            curves.Add(Line.CreateBound(XYZ(mm(X), mm(1200), mm(200)), XYZ(mm(X), mm(1200), mm(2600))))
            r = Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                       XYZ(0, 1, 0), curves,
                                       RebarHookOrientation.Right, RebarHookOrientation.Right,
                                       True, True)
            extra['rebar'] = r.Id.Value
            doc.Regenerate()
        except Exception as ex:
            note('c44 rebar: ' + str(ex))
        record('c44_foundation_rebar', w, **extra)
        X += STEP

        # ---- 45  a door AND rebar -------------------------------------------
        ds = None
        for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(FamilySymbol):
            ds = s
            break
        w = make_wall(X, 0, 8000, t_struct, structural=True)
        doc.Regenerate()
        extra = {}
        if ds is not None:
            if not ds.IsActive:
                ds.Activate(); doc.Regenerate()
            try:
                d = doc.Create.NewFamilyInstance(XYZ(mm(X), mm(2000), 0), ds, w, level,
                                                 StructuralType.NonStructural)
                extra['door'] = d.Id.Value
                doc.Regenerate()
            except Exception as ex:
                note('c45 door: ' + str(ex))
        try:
            from Autodesk.Revit.DB import Curve
            curves = List[Curve]()
            curves.Add(Line.CreateBound(XYZ(mm(X), mm(6000), mm(200)), XYZ(mm(X), mm(6000), mm(2600))))
            r = Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                       XYZ(0, 1, 0), curves,
                                       RebarHookOrientation.Right, RebarHookOrientation.Right,
                                       True, True)
            extra['rebar'] = r.Id.Value
            doc.Regenerate()
        except Exception as ex:
            note('c45 rebar: ' + str(ex))
        record('c45_door_rebar', w, **extra)
        X += STEP

        # ---- 47  a bar that cannot fit inside the future core ----------------
        # Keep it centred and validly hosted. The negative condition is physical,
        # not an invalid host relationship: even the smallest loaded bar cannot
        # fit within the deliberately 4 mm structural carrier.
        w = make_wall(X, 0, 6000, t_narrow_core, structural=True)
        doc.Regenerate()
        try:
            from Autodesk.Revit.DB import Curve
            off = 0.0
            p0 = XYZ(mm(X), mm(2000), mm(300))
            p1 = XYZ(mm(X), mm(2000), mm(2500))
            curves = List[Curve]()
            curves.Add(Line.CreateBound(p0, p1))
            r = Rebar.CreateFromCurves(doc, RebarStyle.Standard, bt, None, None, w,
                                       XYZ(0, 1, 0), curves,
                                       RebarHookOrientation.Right, RebarHookOrientation.Right,
                                       True, True)
            doc.Regenerate()
            record('c47_rebar_outside', w, rebar=r.Id.Value, offset_mm=off)
        except Exception as ex:
            unbuildable['c47_rebar_outside'] = 'narrow-core bar: ' + str(ex)
            record('c47_rebar_outside', w, rebar=None)
        X += STEP

        # ---- 40  area reinforcement ------------------------------------------
        art = None
        for t in FilteredElementCollector(doc).OfClass(AreaReinforcementType):
            art = t
            break
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        if art is None:
            unbuildable['c40_area'] = 'no AreaReinforcementType in the document'
            record('c40_area', w, area=None)
        else:
            try:
                from Autodesk.Revit.DB import Curve
                curves = List[Curve]()
                pts = [XYZ(mm(X), mm(500), mm(300)), XYZ(mm(X), mm(5500), mm(300)),
                       XYZ(mm(X), mm(5500), mm(2500)), XYZ(mm(X), mm(500), mm(2500))]
                for i in range(4):
                    curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
                a = AreaReinforcement.Create(doc, w, curves, XYZ(0, 0, 1), art.Id, bt.Id, hook_none())
                doc.Regenerate()
                record('c40_area', w, area=a.Id.Value, area_unique=a.UniqueId)
            except Exception as ex:
                unbuildable['c40_area'] = 'AreaReinforcement.Create: ' + str(ex)
                record('c40_area', w, area=None)
        X += STEP

        # ---- 41  path reinforcement ------------------------------------------
        prt = None
        for t in FilteredElementCollector(doc).OfClass(PathReinforcementType):
            prt = t
            break
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        if prt is None:
            unbuildable['c41_path'] = 'no PathReinforcementType in the document'
            record('c41_path', w, path=None)
        else:
            try:
                from Autodesk.Revit.DB import Curve
                curves = List[Curve]()
                curves.Add(Line.CreateBound(XYZ(mm(X), mm(800), mm(400)), XYZ(mm(X), mm(5200), mm(400))))
                p = PathReinforcement.Create(doc, w, curves, False, prt.Id, bt.Id, hook_none(), hook_none())
                doc.Regenerate()
                record('c41_path', w, path=p.Id.Value, path_unique=p.UniqueId)
            except Exception as ex:
                unbuildable['c41_path'] = 'PathReinforcement.Create: ' + str(ex)
                record('c41_path', w, path=None)
        X += STEP

        # ---- 43  a wall with a non-default cover -----------------------------
        w = make_wall(X, 0, 6000, t_struct, structural=True)
        doc.Regenerate()
        try:
            host = RebarHostData.GetRebarHostData(w)
            covers = [c for c in FilteredElementCollector(doc).OfClass(RebarCoverType)]
            if host is not None and covers:
                host.SetCommonCoverType(covers[-1])
                doc.Regenerate()
                record('c43_cover', w, cover_type=covers[-1].Id.Value, cover_name=covers[-1].Name)
            else:
                unbuildable['c43_cover'] = 'no RebarCoverType or not a rebar host'
                record('c43_cover', w)
        except Exception as ex:
            unbuildable['c43_cover'] = 'cover: ' + str(ex)
            record('c43_cover', w)
        X += STEP

    # ---- 20  a wall sweep ----------------------------------------------------
    sweep_type = None
    from Autodesk.Revit.DB import WallSweepType as WST
    for t in FilteredElementCollector(doc).OfClass(WallType):
        pass
    from Autodesk.Revit.DB import ElementType
    sweep_types = [t for t in FilteredElementCollector(doc).OfClass(ElementType)
                   if t.GetType().Name == 'WallSweepType']
    w = make_wall(X, 0, 6000, MULTI)
    doc.Regenerate()
    if not sweep_types:
        unbuildable['c20_sweep'] = 'this document has no WallSweepType loaded'
        record('c20_sweep', w, sweep=None)
    else:
        try:
            info = WallSweepInfo(WST.Sweep, True)
            info.Distance = mm(900.0)
            s = WallSweep.Create(w, sweep_types[0].Id, info)
            doc.Regenerate()
            record('c20_sweep', w, sweep=s.Id.Value, sweep_unique=s.UniqueId)
        except Exception as ex:
            unbuildable['c20_sweep'] = 'WallSweep.Create: ' + str(ex)
            record('c20_sweep', w, sweep=None)
    X += STEP

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    note('FIXTURE 3 FAILED: ' + str(ex))

__output__ = {
    "status": status,
    "summary": "wall-split fixture, structural pass",
    "created_ids": sorted([v["wall"] for v in made.values()]),
    "made": made,
    "unbuildable": unbuildable,
    "notes": notes,
}
