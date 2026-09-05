# -*- coding: utf-8 -*-
"""
Two fresh canary walls, in their own zone, built for one campaign and no other.

  canary_bare - multilayer, nothing hosted. Proves the conversion: identity,
                cardinality, the skipped membranes, the naming, the offsets, the
                chain, the provenance. It CANNOT prove anything about holes and
                the harness says so.
  canary_door - the same wall with one real door. This is the one that used to
                be impossible: every wall with a door rolled back on
                verify_parameter_mismatch, because two tables disagreed about a
                parameter Revit computes.

They are FRESH every run. Reusing a converted wall would measure a wall that
survived an earlier campaign, which is a different thing, and the saved fixture
already holds one of those.
"""
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallUtils, Level, XYZ, Line,
    Transaction, ElementId, BuiltInParameter, FamilySymbol, BuiltInCategory
)
from Autodesk.Revit.DB.Structure import StructuralType

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
made, notes, problems = {}, [], []


def mm(v):
    return v * MM


TYPE_NAME = 'M_Exterior - Brick on Mtl. Stud'
MULTI = None
for wt in FilteredElementCollector(doc).OfClass(WallType):
    if wt.Name == TYPE_NAME:
        MULTI = wt
        break
level = sorted([l for l in FilteredElementCollector(doc).OfClass(Level)],
               key=lambda l: l.Elevation)[0]


def door_symbol():
    for s in FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsElementType():
        if isinstance(s, FamilySymbol):
            return s
    return None


# A zone of its own, well past the topology experiment's walls, so nothing this
# campaign measures can be touching anything an earlier one left behind.
X0 = 1500000.0
STEP = 20000.0
HEIGHT = 3000.0

tx = Transaction(doc, 'HZ canary fixture')
tx.Start()
try:
    if MULTI is None:
        raise Exception('wall type ' + TYPE_NAME + ' is absent')
    sym = door_symbol()
    if sym is None:
        raise Exception('no door symbol in this document')
    if not sym.IsActive:
        sym.Activate()
        doc.Regenerate()

    x = X0
    for key, with_door in (('canary_bare', False), ('canary_door', True)):
        line = Line.CreateBound(XYZ(mm(x), 0.0, 0.0), XYZ(mm(x), mm(6000), 0.0))
        w = Wall.Create(doc, line, MULTI.Id, level.Id, mm(HEIGHT), 0.0, False, False)
        doc.Regenerate()
        try:
            WallUtils.DisallowWallJoinAtEnd(w, 0)
            WallUtils.DisallowWallJoinAtEnd(w, 1)
        except Exception as ex:
            notes.append(key + ': end joins not disallowed: ' + str(ex))

        entry = {"wall": w.Id.Value, "wall_unique_id": w.UniqueId, "type": TYPE_NAME, "x_mm": x}

        if with_door:
            d = doc.Create.NewFamilyInstance(XYZ(mm(x), mm(3000), 0.0), sym, w, level,
                                             StructuralType.NonStructural)
            doc.Regenerate()
            host = None
            try:
                host = d.Host.Id.Value
            except Exception:
                pass
            if host != w.Id.Value:
                problems.append(key + ': the door reports host ' + str(host))
            entry.update({
                "door": d.Id.Value,
                "door_unique_id": d.UniqueId,
                "door_symbol": sym.Family.Name + ' : ' + sym.Name,
                "door_host": host,
                # The identity fields the campaign must find unchanged afterwards.
                "door_level": d.LevelId.Value,
                "door_facing_flipped": d.FacingFlipped,
                "door_hand_flipped": d.HandFlipped,
                "door_sill": (d.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).AsDouble()
                              if d.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) else None),
                "door_head": (d.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM).AsDouble()
                              if d.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM) else None),
                "door_phase_created": (d.get_Parameter(BuiltInParameter.PHASE_CREATED).AsElementId().Value
                                       if d.get_Parameter(BuiltInParameter.PHASE_CREATED) else None),
                "door_phase_demolished": (d.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED).AsElementId().Value
                                          if d.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED) else None),
                "door_subcomponents": sorted([i.Value for i in d.GetSubComponentIds()]),
            })

        made[key] = entry
        x += STEP

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    problems.append('CANARY FIXTURE FAILED: ' + str(ex))

verified = {}
for key, e in made.items():
    try:
        w = doc.GetElement(ElementId(e['wall']))
        verified[key] = {"wall_exists": w is not None,
                         "type": (doc.GetElement(w.GetTypeId()).Name if w else None)}
        if 'door' in e:
            d = doc.GetElement(ElementId(e['door']))
            verified[key]["door_exists"] = d is not None
            verified[key]["door_host_is_wall"] = (d is not None and d.Host is not None and
                                                  d.Host.Id.Value == e['wall'])
    except Exception as ex:
        verified[key] = {"error": str(ex)}

warnings_now = []
for wmsg in doc.GetWarnings():
    try:
        warnings_now.append({"text": wmsg.GetDescriptionText(),
                             "elements": sorted([i.Value for i in wmsg.GetFailingElements()])})
    except Exception:
        pass

__output__ = {
    "status": status, "summary": "two fresh canary walls: one bare, one with a door",
    "made": made, "verified": verified,
    "standing_warnings_total": len(warnings_now), "standing_warnings": warnings_now,
    "notes": notes, "problems": problems,
}
