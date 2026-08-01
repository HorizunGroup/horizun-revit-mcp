# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# SPLIT A MULTI-LOOP FLOOR INTO ONE FLOOR PER LOOP.
#
# A floor sketched with several closed loops is one element. Everything that
# counts area, assigns a keynote, or schedules by room then treats those separate
# slabs as a single row, and no amount of care downstream fixes it. This makes
# each loop its own floor.
#
# Ported from the "Partir Losa" pyRevit button. Two things changed, and both are
# the port rather than the algorithm:
#
#   * the button called PickObjects and then asked the user to confirm in a
#     dialog. There is no user here, so the selection is an argument and the
#     confirmation is dry_run - which the HOST implements by running plan() and
#     never opening a transaction at all.
#
#   * the button did NOT carry the height offset onto the new floors, which moves
#     every split slab to its level. Interactively you see that immediately and
#     undo. A tool called by an agent must not have that failure mode, so the
#     offset is copied and REPORTED per floor.
#
# The inner loops of a floor are its openings. Splitting a slab whose second loop
# is a hole would turn the hole into a slab, so plan() reports the loop count and
# the caller can look before applying - but the original behaviour (every loop
# becomes a floor) is preserved, because that is what the button did and what the
# people using it expect.
# -----------------------------------------------------------------------------
from Autodesk.Revit.DB import (
    Floor, CurveLoop, ElementId, BuiltInParameter
)
from System.Collections.Generic import List

import hz


def _is_floor(element):
    return isinstance(element, Floor)


def _loops(doc, floor):
    """The closed loops of a floor's sketch, as lists of curves. Empty when Revit
    will not give us the sketch - which is a reason to skip the floor, not to
    guess at its geometry."""
    sketch_id = floor.SketchId
    if sketch_id == ElementId.InvalidElementId:
        return []
    sketch = doc.GetElement(sketch_id)
    if sketch is None:
        return []
    loops = []
    for curve_array in sketch.Profile:
        curves = [c for c in curve_array]
        if curves:
            loops.append(curves)
    return loops


def _is_structural(floor):
    p = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)
    return p is not None and p.AsInteger() == 1


# The offset parameter, by every name Revit has given it. FLOOR_HEIGHTOFFSET is
# real on older Revit and ABSENT on 2026, where it is FLOOR_HEIGHTABOVELEVEL_PARAM
# - and asking for a name that is not there raises from inside the geometry rather
# than reporting anything useful. Found by running this against a real 2026.
OFFSET_PARAM_NAMES = ("FLOOR_HEIGHTABOVELEVEL_PARAM", "FLOOR_HEIGHTOFFSET")


def _height_offset(floor):
    """Offset from the level, in feet. None when the floor has no such parameter -
    which is 'not available', not zero."""
    p = hz.param(floor, *OFFSET_PARAM_NAMES)
    if p is None:
        return None
    try:
        return p.AsDouble()
    except Exception:
        return None


def _describe(doc, floor):
    loops = _loops(doc, floor)
    name = None
    try:
        name = floor.Name
    except Exception:
        pass
    return {
        "id": hz.eid(floor.Id),
        "name": name,
        "loops": len(loops),
        "structural": _is_structural(floor),
        "height_offset_ft": _height_offset(floor),
    }


def plan(doc, args):
    scope = hz.resolve(doc, args, _is_floor, of_class=Floor)

    eligible = []
    skipped = []
    for floor in scope.elements:
        info = _describe(doc, floor)
        if info["loops"] >= 2:
            eligible.append(info)
        else:
            info["reason"] = ("no sketch Revit will hand over" if info["loops"] == 0
                              else "a single loop - nothing to split")
            skipped.append(info)

    return {
        "scope": scope.report(),
        "eligible": eligible,
        "skipped": skipped,
        "would_delete": len(eligible),
        "would_create": sum(f["loops"] for f in eligible),
        "note": ("Every loop becomes a floor, including inner loops, which in a slab with "
                 "openings are the holes. Check the loop count per floor before applying."),
    }


def _create(doc, curves, floor_type_id, level_id, structural):
    loop = CurveLoop()
    for curve in curves:
        loop.Append(curve)
    loops = List[CurveLoop]()
    loops.Add(loop)
    try:
        return Floor.Create(doc, loops, floor_type_id, level_id, structural, None, 0.0)
    except TypeError:
        # Older API: no slope-arrow overload.
        return Floor.Create(doc, loops, floor_type_id, level_id)


def apply(doc, args, plan):
    created = []
    deleted = []
    errors = []

    for entry in plan["eligible"]:
        floor = doc.GetElement(hz.to_eid(entry["id"]))
        if floor is None:
            errors.append({"id": entry["id"], "error": "vanished between plan and apply"})
            continue

        floor_type_id = floor.FloorType.Id
        level_id = floor.LevelId
        structural = entry["structural"]
        offset = entry["height_offset_ft"]

        made_here = []
        for index, curves in enumerate(_loops(doc, floor)):
            try:
                new_floor = _create(doc, curves, floor_type_id, level_id, structural)
                if offset is not None:
                    p = hz.param(new_floor, *OFFSET_PARAM_NAMES)
                    if p is not None and not p.IsReadOnly:
                        p.Set(offset)
                made_here.append(hz.eid(new_floor.Id))
            except Exception as exc:
                errors.append({"id": entry["id"], "loop": index, "error": hz.brief(exc)})

        # Only remove the original once something replaced it. A loop that failed
        # to become a floor must not take its geometry with it.
        if made_here:
            try:
                # Unpin first. Deleting a pinned element raises Revit's "you are trying
                # to delete pinned elements" warning, and a warning nobody answers is a
                # modal that holds the UI thread until the caller times out. The button
                # this came from had a person to click it; here there is nobody.
                if floor.Pinned:
                    floor.Pinned = False
                doc.Delete(floor.Id)
                deleted.append(entry["id"])
            except Exception as exc:
                errors.append({"id": entry["id"], "error": "could not delete the original: " + hz.brief(exc)})
        created.extend(made_here)

    return {
        "created_ids": created,
        "deleted_ids": deleted,
        "created": len(created),
        "deleted": len(deleted),
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    """After the commit, ask the MODEL. apply() counted its own calls; this counts
    what survived, and the host fails the call when the two disagree."""
    present = 0
    for value in applied["created_ids"]:
        element = doc.GetElement(hz.to_eid(value))
        if isinstance(element, Floor):
            present += 1

    gone = 0
    for value in applied["deleted_ids"]:
        if not hz.still_exists(doc, value):
            gone += 1

    return {
        "created_present": present,
        "deleted_gone": gone,
        "intended_created": applied["created"],
        "intended_deleted": applied["deleted"],
    }
