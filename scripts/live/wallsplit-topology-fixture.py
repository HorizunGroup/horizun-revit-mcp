# -*- coding: utf-8 -*-
"""
The topology experiment's fixture: four IDENTICAL walls, each with a real door.

Why four identical walls rather than one reused four times: each variant has to
start from the same geometry, and a wall that has already been converted and
re-wired is not that. Reusing one would make variant B a measurement of "a wall
that survived variant A", which answers a different question.

Why a NEW zone far from everything: the saved fixture already contains a
converted canary and its two standing "joined but do not intersect" warnings.
Counting warnings is part of this experiment, so the walls under test must
contribute the only new ones. They are placed 20 m apart from each other and far
beyond every existing wall, so nothing can join anything.

Each wall is deliberately bare: no tag, no dimension, no rebar, no foundation,
no group, no pin, and both ends have wall joins DISALLOWED so Revit cannot
attach them to anything. The only thing hosted is one door.

Nothing here is inferred. Every wall is re-read after creation and the door is
confirmed to be hosted by it.
"""
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallUtils, Level, XYZ, Line,
    Transaction, ElementId, BuiltInParameter, FamilySymbol, BuiltInCategory,
    CompoundStructure, MaterialFunctionAssignment
)
from Autodesk.Revit.DB.Structure import StructuralType

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document

made, notes, problems = {}, [], []


def mm(v):
    return v * MM


def find_wall_type(name):
    for wt in FilteredElementCollector(doc).OfClass(WallType):
        if wt.Name == name:
            return wt
    return None


TYPE_NAME = 'M_Exterior - Brick on Mtl. Stud'
MULTI = find_wall_type(TYPE_NAME)
levels = sorted([l for l in FilteredElementCollector(doc).OfClass(Level)], key=lambda l: l.Elevation)
level = levels[0]


def door_symbol():
    """The same family and type for every wall - a different door would make the
    hole a different size and the variants incomparable."""
    best = None
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsElementType():
        if isinstance(s, FamilySymbol):
            if best is None:
                best = s
    return best


def describe_layers(wt):
    """The layer order, widths and functions, so the carrier's position is a
    recorded fact rather than an expectation."""
    cs = wt.GetCompoundStructure()
    if cs is None:
        return None, None, None
    rows = []
    for i in range(cs.LayerCount):
        rows.append({
            "index": i,
            "number": i + 1,
            "width_mm": round(cs.GetLayerWidth(i) / MM, 3),
            "function": str(cs.GetLayerFunction(i)),
            "has_volume": cs.GetLayerWidth(i) > 1e-9,
        })
    try:
        first, last = cs.GetFirstCoreLayerIndex(), cs.GetLastCoreLayerIndex()
    except Exception:
        first, last = None, None
    return rows, first, last


tx = Transaction(doc, 'HZ topology fixture')
tx.Start()
try:
    if MULTI is None:
        raise Exception('wall type ' + TYPE_NAME + ' is absent from this document')
    sym = door_symbol()
    if sym is None:
        raise Exception('this document has no door symbol')
    if not sym.IsActive:
        sym.Activate()
        doc.Regenerate()

    layers, core_first, core_last = describe_layers(MULTI)

    # Far from every existing wall (the last of them sits below x = 900 000 mm)
    # and 20 m apart from one another, so nothing can touch anything.
    X = 1200000.0
    STEP = 20000.0
    HEIGHT = 3000.0
    Y0, Y1 = 0.0, 6000.0
    DOOR_Y = 3000.0

    for key in ('A_star', 'B_chain', 'C_nojoin', 'D_openings'):
        line = Line.CreateBound(XYZ(mm(X), mm(Y0), 0), XYZ(mm(X), mm(Y1), 0))
        w = Wall.Create(doc, line, MULTI.Id, level.Id, mm(HEIGHT), 0.0, False, False)
        doc.Regenerate()

        # No end joins: nothing may attach to these walls from outside.
        try:
            WallUtils.DisallowWallJoinAtEnd(w, 0)
            WallUtils.DisallowWallJoinAtEnd(w, 1)
        except Exception as ex:
            notes.append(key + ': could not disallow end joins: ' + str(ex))

        d = doc.Create.NewFamilyInstance(XYZ(mm(X), mm(DOOR_Y), 0), sym, w, level,
                                         StructuralType.NonStructural)
        doc.Regenerate()

        # RE-READ, never assumed: the door must really be hosted by this wall.
        host_id = None
        try:
            host_id = d.Host.Id.Value
        except Exception:
            host_id = None
        if host_id != w.Id.Value:
            problems.append(key + ': the door reports host ' + str(host_id) +
                            ' rather than the wall ' + str(w.Id.Value))

        made[key] = {
            "variant": key,
            "wall": w.Id.Value,
            "wall_unique_id": w.UniqueId,
            "wall_type": TYPE_NAME,
            "door": d.Id.Value,
            "door_unique_id": d.UniqueId,
            "door_symbol": sym.Family.Name + ' : ' + sym.Name,
            "door_host": host_id,
            "x_mm": X,
            "height_mm": HEIGHT,
            "flipped": w.Flipped,
        }
        X += STEP

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    problems.append('TOPOLOGY FIXTURE FAILED: ' + str(ex))
    layers, core_first, core_last = None, None, None

# ---- self-verification, after the commit ------------------------------------
verified = {}
for key, entry in made.items():
    try:
        w = doc.GetElement(ElementId(entry['wall']))
        d = doc.GetElement(ElementId(entry['door']))
        verified[key] = {
            "wall_exists": w is not None,
            "door_exists": d is not None,
            "door_host_is_wall": (d is not None and d.Host is not None and
                                  d.Host.Id.Value == entry['wall']),
            "wall_type": (doc.GetElement(w.GetTypeId()).Name if w is not None else None),
        }
    except Exception as ex:
        verified[key] = {"error": str(ex)}

# Standing warnings AFTER building the fixture: these four walls must add none.
warnings_now = []
for wmsg in doc.GetWarnings():
    try:
        warnings_now.append({"text": wmsg.GetDescriptionText(),
                             "elements": [i.Value for i in wmsg.GetFailingElements()]})
    except Exception:
        pass

__output__ = {
    "status": status,
    "summary": "four identical 7-layer walls, one door each, isolated",
    "wall_type": TYPE_NAME,
    "layers": layers,
    "core_first_layer_index": core_first,
    "core_last_layer_index": core_last,
    "made": made,
    "verified": verified,
    "standing_warnings_after_fixture": len(warnings_now),
    "standing_warnings": warnings_now,
    "notes": notes,
    "problems": problems,
}
