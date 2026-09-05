# -*- coding: utf-8 -*-
"""
The SECOND fixture pass: everything the first one did not build.

It runs on top of the walls the first pass left in HZ_WALLSPLIT, and every wall
it adds sits well clear of them. Where Revit's API cannot build a case at all,
this reports it as unbuildable with the reason rather than approximating it -
the campaign then records not_covered, which is a different answer from passed.
"""
import math
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallKind, Level, XYZ, Line, Arc,
    Transaction, ElementId, BuiltInParameter, WallLocationLine, FamilySymbol,
    BuiltInCategory, CurveArray, JoinGeometryUtils, WallUtils, Material,
    CompoundStructure, CompoundStructureLayer, MaterialFunctionAssignment,
    ShellLayerType, WallCrossSection, Group, IndependentTag, Reference,
    TagMode, TagOrientation, ReferenceArray, DesignOption, ElementTransformUtils
)
from Autodesk.Revit.DB.Structure import (
    StructuralType, RebarBarType, RebarStyle, RebarHookOrientation,
    RebarHookType, RebarShape, Rebar, AreaReinforcement, PathReinforcement,
    RebarHostData, BarTerminationsData
)

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
uidoc = __revit__.ActiveUIDocument

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


def make_type(base, name, layers, exterior_shell, interior_shell):
    """layers: list of (function, width_mm, materialId)."""
    existing = find_type(name)
    if existing is not None:
        return existing
    new = base.Duplicate(name)
    cs_layers = []
    for fn, w, mat in layers:
        cs_layers.append(CompoundStructureLayer(mm(w), fn, mat))
    cs = CompoundStructure.CreateSimpleCompoundStructure(cs_layers)
    cs.SetNumberOfShellLayers(ShellLayerType.Exterior, exterior_shell)
    cs.SetNumberOfShellLayers(ShellLayerType.Interior, interior_shell)
    new.SetCompoundStructure(cs)
    return new


def make_wall(x, y0, y1, wtype, height=3000.0, structural=False):
    line = Line.CreateBound(XYZ(mm(x), mm(y0), 0), XYZ(mm(x), mm(y1), 0))
    return Wall.Create(doc, line, wtype.Id, level.Id, mm(height), 0.0, False, structural)


def record(key, wall, **extra):
    d = {"wall": wall.Id.Value, "unique_id": wall.UniqueId}
    try:
        d["type"] = doc.GetElement(wall.GetTypeId()).Name
    except Exception:
        pass
    d.update(extra)
    made[key] = d


def door_symbol():
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(FamilySymbol):
        return s
    return None


tx = Transaction(doc, 'HZ wallsplit fixture 2')
tx.Start()
try:
    mat = any_material()
    X = 200000.0          # a fresh band, far from pass one
    STEP = 6000.0

    # ---- 3  a core with NO Structure-function layer -------------------------
    t_nostruct = make_type(MULTI, 'HZ_NoStructureCore',
                           [(MaterialFunctionAssignment.Finish1, 20.0, mat),
                            (MaterialFunctionAssignment.Substrate, 100.0, mat),
                            (MaterialFunctionAssignment.Insulation, 60.0, mat),
                            (MaterialFunctionAssignment.Finish2, 20.0, mat)],
                           exterior_shell=1, interior_shell=1)
    w = make_wall(X, 0, 5000, t_nostruct); record('c03_no_structure_core', w); X += STEP

    # ---- zero-width membrane inside the assembly ----------------------------
    t_membrane = make_type(MULTI, 'HZ_WithMembrane',
                           [(MaterialFunctionAssignment.Finish1, 20.0, mat),
                            (MaterialFunctionAssignment.Membrane, 0.0, mat),
                            (MaterialFunctionAssignment.Structure, 150.0, mat),
                            (MaterialFunctionAssignment.Finish2, 20.0, mat)],
                           exterior_shell=2, interior_shell=1)
    w = make_wall(X, 0, 5000, t_membrane); record('c_membrane', w); X += STEP

    # ---- 8  an arc curving the other way ------------------------------------
    c = XYZ(mm(X + 4000), mm(0), 0)
    arc = Arc.Create(c, mm(4000), math.radians(20), math.radians(70), XYZ.BasisX, XYZ.BasisY)
    w = Wall.Create(doc, arc, MULTI.Id, level.Id, mm(3000), 0.0, False, False)
    record('c08_arc_interior', w); X += STEP * 2

    # ---- 23  slanted and tapered: NEGATIVE cases ----------------------------
    for key, section, angle in (('c23_slanted', WallCrossSection.SingleSlanted, 0.15),
                                ('c23_tapered', WallCrossSection.Tapered, 0.10)):
        w = make_wall(X, 0, 5000, MULTI)
        try:
            w.CrossSection = section
            p = w.get_Parameter(BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL)
            if p is not None and not p.IsReadOnly:
                p.Set(angle)
            doc.Regenerate()
            record(key, w, cross_section=str(w.CrossSection))
        except Exception as ex:
            unbuildable[key] = 'the cross section could not be set: ' + str(ex)
            doc.Delete(w.Id)
        X += STEP

    # ---- 18  an opening cut from a profile ----------------------------------
    w = make_wall(X, 0, 6000, MULTI)
    doc.Regenerate()
    try:
        pts = [XYZ(mm(X), mm(1500), mm(600)), XYZ(mm(X), mm(3000), mm(600)),
               XYZ(mm(X), mm(3000), mm(2200)), XYZ(mm(X), mm(2200), mm(2600)),
               XYZ(mm(X), mm(1500), mm(2200))]
        ca = CurveArray()
        for i in range(len(pts)):
            ca.Append(Line.CreateBound(pts[i], pts[(i + 1) % len(pts)]))
        op = doc.Create.NewOpening(w, ca, True)
        record('c18_profile_opening', w, opening=op.Id.Value, opening_unique=op.UniqueId)
    except Exception as ex:
        unbuildable['c18_profile_opening'] = 'profiled opening: ' + str(ex)
        record('c18_profile_opening', w, opening=None)
    X += STEP

    # ---- 24  a wall inside a group ------------------------------------------
    w = make_wall(X, 0, 5000, MULTI)
    doc.Regenerate()
    try:
        from System.Collections.Generic import List
        ids = List[ElementId]()
        ids.Add(w.Id)
        g = doc.Create.NewGroup(ids)
        record('c24_group', w, group=g.Id.Value)
    except Exception as ex:
        unbuildable['c24_group'] = 'group: ' + str(ex)
    X += STEP

    # ---- 33  a wall with a tag and a dimension ------------------------------
    w = make_wall(X, 0, 5000, MULTI)
    doc.Regenerate()
    tagged = None
    dimensioned = None
    try:
        view = doc.ActiveView
        r = Reference(w)
        t = IndependentTag.Create(doc, view.Id, r, True, TagMode.TM_ADDBY_CATEGORY,
                                  TagOrientation.Horizontal, XYZ(mm(X + 1500), mm(2500), 0))
        tagged = t.Id.Value
    except Exception as ex:
        note('tag: ' + str(ex))
    try:
        view = doc.ActiveView
        loc = w.Location.Curve
        ra = ReferenceArray()
        opts = __import__('Autodesk.Revit.DB', fromlist=['Options']).Options()
        opts.ComputeReferences = True
        opts.IncludeNonVisibleObjects = False
        geo = w.get_Geometry(opts)
        faces = []
        for g in geo:
            if hasattr(g, 'Faces'):
                for f in g.Faces:
                    faces.append(f)
        if len(faces) >= 2:
            ra.Append(faces[0].Reference)
            ra.Append(faces[1].Reference)
            dim = doc.Create.NewDimension(view, Line.CreateBound(XYZ(mm(X - 500), mm(0), 0),
                                                                 XYZ(mm(X - 500), mm(5000), 0)), ra)
            dimensioned = dim.Id.Value
    except Exception as ex:
        note('dimension: ' + str(ex))
    record('c33_tag_dim', w, tag=tagged, dimension=dimensioned)
    X += STEP

    # ---- 31  a wall reserved for the stale-plan case ------------------------
    w = make_wall(X, 0, 5000, MULTI); record('c31_stale', w); X += STEP

    # ---- 30  a wall reserved for the idempotence case -----------------------
    w = make_wall(X, 0, 5000, MULTI); record('c30_idempotent', w); X += STEP

    # ---- 32  a second wall for the mixed batch ------------------------------
    w = make_wall(X, 0, 5000, MULTI); record('c32_second', w); X += STEP

    # ---- 52  a wall whose sibling will be deleted ---------------------------
    w = make_wall(X, 0, 5000, MULTI); record('c52_partial', w); X += STEP

    # ---- 14  a door that carries nested components --------------------------
    ds = door_symbol()
    if ds is not None:
        if not ds.IsActive:
            ds.Activate(); doc.Regenerate()
        w = make_wall(X, 0, 6000, MULTI)
        doc.Regenerate()
        d = doc.Create.NewFamilyInstance(XYZ(mm(X), mm(2500), 0), ds, w, level, StructuralType.NonStructural)
        doc.Regenerate()
        subs = []
        try:
            subs = [i.Value for i in d.GetSubComponentIds()]
        except Exception:
            pass
        record('c14_nested', w, door=d.Id.Value, subcomponents=subs)
        if not subs:
            note('c14: the loaded door family carries no nested shared components; the case measures a door with zero subcomponents')
        X += STEP

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    note('FIXTURE 2 FAILED: ' + str(ex))

__output__ = {
    "status": status,
    "summary": "wall-split fixture, second pass",
    "created_ids": sorted([v["wall"] for v in made.values()]),
    "made": made,
    "unbuildable": unbuildable,
    "notes": notes,
}
