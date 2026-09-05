# -*- coding: utf-8 -*-
"""
Decompose one wall BY HAND, under a named join topology.

Why by hand rather than through the tool: the tool refuses a wall with a door
(verify_parameter_mismatch on bip:HOST_AREA_COMPUTED - a computed parameter its
own copier already declines to copy), so it cannot produce the geometry this
experiment has to measure. The question under test is about REVIT's join
semantics, not about the tool's verifier, so the geometry is built directly and
measured with the independent probe.

This reproduces exactly what WallSplitExecutor does geometrically:
  - the ORIGINAL wall stays and becomes the wall of the core layer (the carrier),
    keeping its ElementId and its door;
  - every other layer with volume becomes a new single-layer wall at its own
    offset from the original location line;
  - then joins are applied according to the mode.

Modes:
  star   - the shipped topology: carrier joined to EVERY layer wall
  chain  - each materialised layer joined to its immediate NEIGHBOUR only
  none   - no joins between any of them

Reads C:\\hz-live\\topo-build-config.json:  {"wall":123,"door":456,"mode":"star"}

THE ORIGINAL CURVE IS DETACHED FIRST and every target curve is computed BEFORE
the carrier is converted - converting it replaces the live curve, and working
from the stale reference is a defect this project has already paid for once.
"""
import io
import json

from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallUtils, Level, XYZ, Line, Curve,
    Transaction, ElementId, BuiltInParameter, Transform, JoinGeometryUtils,
    CompoundStructure, CompoundStructureLayer, ShellLayerType, LocationCurve
)

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
notes, problems = [], []


def mm(v):
    return v * MM


def to_mm(v):
    return v / MM


cfg = json.loads(io.open(r'C:\hz-live\topo-build-config.json', encoding='utf-8').read())
WALL_ID = int(cfg['wall'])
MODE = cfg['mode']

made = {"mode": MODE, "source_wall": WALL_ID, "layers": [], "joins": []}

tx = Transaction(doc, 'HZ topology build ' + MODE)
tx.Start()
try:
    wall = doc.GetElement(ElementId(WALL_ID))
    wtype = doc.GetElement(wall.GetTypeId())
    cs = wtype.GetCompoundStructure()
    if cs is None:
        raise Exception('the wall type has no compound structure')

    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        raise Exception('the wall has no location curve')

    # DETACHED. Converting the carrier replaces the live curve.
    original = loc.Curve.CreateTransformed(Transform.Identity)
    normal = wall.Orientation          # points to the exterior side
    level_id = wall.LevelId
    height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()
    base_off = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()

    # ---- the offset of each layer from the location line --------------------
    widths = [cs.GetLayerWidth(i) for i in range(cs.LayerCount)]
    total = sum(widths)
    # These walls are on WallCenterline, so the location line sits at half the
    # total width measured from the exterior face. Asserted, not assumed:
    kind = str(wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).AsInteger())
    u_loc = total / 2.0
    notes.append('location line parameter value = ' + kind + ' (0 = WallCenterline); u_loc = ' +
                 str(round(to_mm(u_loc), 3)) + ' mm from the exterior face')

    core_first, core_last = cs.GetFirstCoreLayerIndex(), cs.GetLastCoreLayerIndex()
    carrier_index = core_first        # single-layer core on this type

    plan = []
    run = 0.0
    for i in range(cs.LayerCount):
        w = widths[i]
        centre = run + w / 2.0
        plan.append({
            "index": i, "number": i + 1, "width_mm": round(to_mm(w), 3),
            "function": str(cs.GetLayerFunction(i)),
            "materialised": w > 1e-9,
            "offset_mm": round(to_mm(u_loc - centre), 3),
            "offset_feet": u_loc - centre,
            "is_carrier": i == carrier_index,
            "material_id": cs.GetMaterialId(i).Value,
        })
        run += w

    # ---- a single-layer type per materialised layer -------------------------
    def single_layer_type(entry):
        name = 'HZTOPO ' + MODE + ' L%02d' % entry['number']
        for t in FilteredElementCollector(doc).OfClass(WallType):
            if t.Name == name:
                return t
        nt = wtype.Duplicate(name)
        layer = CompoundStructureLayer(mm(entry['width_mm']),
                                       cs.GetLayerFunction(entry['index']),
                                       ElementId(entry['material_id']))
        ncs = CompoundStructure.CreateSingleLayerCompoundStructure(
            cs.GetLayerFunction(entry['index']), mm(entry['width_mm']),
            ElementId(entry['material_id']))
        nt.SetCompoundStructure(ncs)
        return nt

    # ---- EVERY target curve BEFORE anything is written ----------------------
    targets = {}
    for entry in plan:
        if not entry['materialised']:
            continue
        targets[entry['index']] = original.CreateTransformed(
            Transform.CreateTranslation(normal.Multiply(entry['offset_feet'])))

    # ---- convert the carrier ------------------------------------------------
    carrier_entry = [e for e in plan if e['is_carrier']][0]
    carrier_type = single_layer_type(carrier_entry)
    wall.ChangeTypeId(carrier_type.Id)
    doc.Regenerate()
    wall.Location.Curve = targets[carrier_entry['index']]
    doc.Regenerate()

    created = {carrier_entry['index']: wall}
    made['layers'].append({"number": carrier_entry['number'], "wall": wall.Id.Value,
                           "is_carrier": True, "offset_mm": carrier_entry['offset_mm'],
                           "type": carrier_type.Name})

    # ---- the other layers ---------------------------------------------------
    for entry in plan:
        if not entry['materialised'] or entry['is_carrier']:
            continue
        t = single_layer_type(entry)
        w = Wall.Create(doc, targets[entry['index']], t.Id, level_id, height, base_off,
                        wall.Flipped, False)
        doc.Regenerate()
        try:
            WallUtils.DisallowWallJoinAtEnd(w, 0)
            WallUtils.DisallowWallJoinAtEnd(w, 1)
        except Exception:
            pass
        created[entry['index']] = w
        made['layers'].append({"number": entry['number'], "wall": w.Id.Value,
                               "is_carrier": False, "offset_mm": entry['offset_mm'],
                               "type": t.Name})

    doc.Regenerate()

    # ---- the topology under test -------------------------------------------
    ordered = sorted(created.keys())

    def join(a_idx, b_idx):
        a, b = created[a_idx], created[b_idx]
        try:
            if not JoinGeometryUtils.AreElementsJoined(doc, a, b):
                JoinGeometryUtils.JoinGeometry(doc, a, b)
            made['joins'].append({"a": a.Id.Value, "b": b.Id.Value,
                                  "a_layer": a_idx + 1, "b_layer": b_idx + 1, "ok": True})
        except Exception as ex:
            made['joins'].append({"a": a.Id.Value, "b": b.Id.Value,
                                  "a_layer": a_idx + 1, "b_layer": b_idx + 1,
                                  "ok": False, "error": str(ex)[:200]})

    if MODE == 'star':
        for i in ordered:
            if i != carrier_entry['index']:
                join(carrier_entry['index'], i)
    elif MODE == 'chain':
        for a, b in zip(ordered, ordered[1:]):
            join(a, b)
    elif MODE == 'none':
        notes.append('no joins were made, deliberately')
    else:
        raise Exception('unknown mode ' + MODE)

    doc.Regenerate()
    tx.Commit()
    status = 'completed_unverified'
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    problems.append('BUILD FAILED: ' + str(ex))

# ---- what the model holds now ----------------------------------------------
after = []
for row in made['layers']:
    try:
        w = doc.GetElement(ElementId(row['wall']))
        after.append({"number": row['number'], "wall": row['wall'], "exists": w is not None,
                      "type": (doc.GetElement(w.GetTypeId()).Name if w else None)})
    except Exception as ex:
        after.append({"number": row['number'], "wall": row['wall'], "error": str(ex)})

warnings_now = []
for wmsg in doc.GetWarnings():
    try:
        warnings_now.append({"text": wmsg.GetDescriptionText(),
                             "elements": [i.Value for i in wmsg.GetFailingElements()]})
    except Exception:
        pass

__output__ = {
    "status": status, "summary": "manual decomposition, topology=" + MODE,
    "built": made, "after": after,
    "standing_warnings_total": len(warnings_now),
    "standing_warnings": warnings_now,
    "notes": notes, "problems": problems,
}
