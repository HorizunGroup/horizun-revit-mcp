# -*- coding: utf-8 -*-
"""
THE DEFECTS THE CORRECTION CYCLE IS SUPPOSED TO CORRECT, BUILT ON PURPOSE.

A harness that waits for a model to happen to contain an orphan group type is a
harness that reports "fixture_missing" on most days and "passed" on the rest
without anybody knowing which. So this builds the defects, in one transaction,
in a document the integrator has already declared disposable.

EVERYTHING HERE IS SOMETHING NO TYPED COMMAND DOES. A group type with no
instance, a room with no location, a view template minted from a view: there is
no horizun_* command for any of them, which is exactly the case the Python
fallback exists for. The link unpin/unload and the view duplication the campaign
needs ARE typed, and the harness does those through their typed commands - not
here.

WHAT IT RETURNS, and why it verifies its own work: __output__ on this path is
self-reported, never host-verified, so every id below is RE-READ from the model
after the commit and reported only if the re-read agrees. An id this script
claims and the model does not carry would make every probe downstream measure
the harness instead of the product.

THE INCOMPATIBLE TEMPLATE IS DELIBERATE. One of the two templates is minted from
a 3D view and is offered later to a floor plan: Revit refuses that assignment
when it is made, and refusing it is the only way to induce a child that
rehearses cleanly and then fails - which is what `rollback_scope: per_action`
claims to survive. Naming it here keeps it from reading like an accident.

Nothing is saved. The document is disposable by the integrator's declaration.
"""
from System.Collections.Generic import List
from Autodesk.Revit.DB import (
    ElementId, FilteredElementCollector, Group, GroupType, Level, Line, Plane,
    SketchPlane, Transaction, View3D, ViewDuplicateOption, ViewFamily,
    ViewFamilyType, ViewPlan, XYZ
)

MM = 1.0 / 304.8
# A corner of the world 900 m from anything the base model contains, so the
# scaffolding cannot be mistaken for - or join - somebody's building.
FAR = 900000.0 * MM
ORPHANS_WANTED = 4
PREFIX = "HZ_DOCTOR_"

out = {
    "status": "failed",
    "group_type_ids": [],
    "room_id": None,
    "plan_view_id": None,
    "spare_view_id": None,
    "template_id": None,
    "incompatible_template_id": None,
    "notes": [],
}


def note(text):
    out["notes"].append(text)


def rid(element_id):
    """Element ids are 64-bit longs from 2024 and ints before it."""
    try:
        return int(element_id.Value)
    except AttributeError:
        return int(element_id.IntegerValue)


def ids_of(python_list):
    collection = List[ElementId]()
    for one in python_list:
        collection.Add(one)
    return collection


def first_level():
    levels = [l for l in FilteredElementCollector(doc).OfClass(Level)]
    levels.sort(key=lambda l: l.Elevation)
    return levels[0] if levels else None


def first_plan():
    plans = [v for v in FilteredElementCollector(doc).OfClass(ViewPlan)
             if not v.IsTemplate]
    plans.sort(key=lambda v: rid(v.Id))
    return plans[0] if plans else None


def orphan_group_type_ids():
    """A group type nothing places - read exactly as the audit reads it."""
    placed = set()
    for group in FilteredElementCollector(doc).OfClass(Group):
        try:
            placed.add(rid(group.GetTypeId()))
        except Exception:
            pass
    found = []
    for group_type in FilteredElementCollector(doc).OfClass(GroupType):
        if rid(group_type.Id) not in placed:
            found.append(rid(group_type.Id))
    return found


def three_d_type():
    for vft in FilteredElementCollector(doc).OfClass(ViewFamilyType):
        try:
            if vft.ViewFamily == ViewFamily.ThreeDimensional:
                return vft
        except Exception:
            pass
    return None


made_group_types = []
transaction = Transaction(doc, "Horizun: model doctor correction fixture")
transaction.Start()
try:
    level = first_level()
    if level is None:
        raise Exception("this document has no level, so nothing can be drawn in it")

    # ---- ORPHAN GROUP TYPES ------------------------------------------------
    # Two model lines, grouped, and the INSTANCE deleted. The type survives with
    # its geometry in the file and no instance anywhere, which is the defect the
    # audit's orphan_group_types check exists to find.
    plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ(FAR, 0, level.Elevation))
    sketch = SketchPlane.Create(doc, plane)
    for index in range(ORPHANS_WANTED):
        base_x = FAR + (index * 2.0)
        members = []
        for offset in (0.0, 0.5):
            start = XYZ(base_x + offset, 0.0, level.Elevation)
            end = XYZ(base_x + offset, 1.0, level.Elevation)
            members.append(doc.Create.NewModelCurve(Line.CreateBound(start, end), sketch).Id)
        group = doc.Create.NewGroup(ids_of(members))
        group_type = group.GroupType
        try:
            group_type.Name = PREFIX + "ORPHAN_" + str(index + 1)
        except Exception:
            pass
        made_group_types.append(rid(group_type.Id))
        # Deleting the instance takes its members with it and leaves the type.
        doc.Delete(group.Id)

    # ---- AN UNPLACED ROOM --------------------------------------------------
    # A room with no location: it is in every schedule, bounds nothing, and
    # measures zero. Only this half of the rooms finding is deletable, and the
    # registry filters on the typed problem_code to tell it from a room that is
    # placed and merely not enclosed.
    room = None
    try:
        phases = doc.Phases
        if phases.Size > 0:
            room = doc.Create.NewRoom(phases.get_Item(phases.Size - 1))
    except Exception as ex:
        note("no unplaced room could be created: " + str(ex))

    # ---- A VIEW WITH NO TEMPLATE, AND TWO TEMPLATES ------------------------
    plan = first_plan()
    spare = None
    template = None
    wrong_template = None
    if plan is None:
        note("this document has no floor plan, so no view fixture was built")
    else:
        try:
            spare = doc.GetElement(plan.Duplicate(ViewDuplicateOption.Duplicate))
            spare.Name = PREFIX + "VIEW_NO_TEMPLATE"
        except Exception as ex:
            note("the plan could not be duplicated: " + str(ex))
            spare = None
        if spare is not None:
            # A duplicate INHERITS the source's template. Clearing it is what
            # makes this view a defect rather than a copy of a compliant one,
            # and no typed command clears a view template.
            try:
                spare.ViewTemplateId = ElementId.InvalidElementId
            except Exception as ex:
                note("the duplicate's template could not be cleared: " + str(ex))
        try:
            # CreateViewTemplate returns the VIEW, not an ElementId. Measured
            # 2026-09-03 on Revit 2026: "expected ElementId, got ViewPlan".
            made = plan.CreateViewTemplate()
            template = made if not hasattr(made, "IntegerValue") else doc.GetElement(made)
            template.Name = PREFIX + "TEMPLATE_PLAN"
        except Exception as ex:
            note("no plan view template could be minted: " + str(ex))
            template = None
        vft = three_d_type()
        if vft is None:
            note("this document has no 3D view family type, so no incompatible template was built")
        else:
            try:
                view3d = View3D.CreateIsometric(doc, vft.Id)
                view3d.Name = PREFIX + "VIEW_3D"
                made3d = view3d.CreateViewTemplate()
                wrong_template = made3d if not hasattr(made3d, "IntegerValue") else doc.GetElement(made3d)
                wrong_template.Name = PREFIX + "TEMPLATE_3D"
            except Exception as ex:
                note("no 3D view template could be minted: " + str(ex))
                wrong_template = None

    transaction.Commit()

    # ---- RE-READ. Nothing above is reported until the model agrees. --------
    surviving = orphan_group_type_ids()
    out["group_type_ids"] = [one for one in made_group_types if one in surviving]
    if len(out["group_type_ids"]) != len(made_group_types):
        note("only %d of the %d group types survived as orphans; Revit removed the rest with their instance"
             % (len(out["group_type_ids"]), len(made_group_types)))

    if room is not None:
        reread = doc.GetElement(room.Id)
        if reread is not None and reread.Location is None:
            out["room_id"] = rid(room.Id)
        else:
            note("the room was created but is not unplaced, so it is not the defect this fixture needs")

    if plan is not None:
        out["plan_view_id"] = rid(plan.Id)
    if spare is not None:
        reread = doc.GetElement(spare.Id)
        if reread is not None and reread.ViewTemplateId == ElementId.InvalidElementId:
            out["spare_view_id"] = rid(spare.Id)
        else:
            note("the spare view still carries a template, so it is not a views_without_template finding")
    for key, element in (("template_id", template), ("incompatible_template_id", wrong_template)):
        if element is None:
            continue
        reread = doc.GetElement(element.Id)
        if reread is not None and reread.IsTemplate:
            out[key] = rid(element.Id)
        else:
            note(key + " was created but does not read back as a view template")

    built = [k for k in ("room_id", "spare_view_id", "template_id") if out[k] is not None]
    if out["group_type_ids"] and len(built) == 3:
        out["status"] = "self_reported_verified"
    elif out["group_type_ids"] or built:
        out["status"] = "partial"
    out["verification"] = ("every id above was re-read from the model after the commit and is reported only "
                           "where the re-read agreed. This is the SCRIPT's testimony: horizun_execute_python "
                           "host-verifies nothing, and the probes that matter re-measure through the audit.")
except Exception as ex:
    if transaction.HasStarted() and not transaction.HasEnded():
        transaction.RollBack()
    out["status"] = "failed"
    note("the fixture transaction was rolled back: " + str(ex))

__output__ = out
