# -*- coding: utf-8 -*-
"""
READ-ONLY. Why did a clean, isolated wall leave 'joined but do not intersect'?

The canary converted correctly - identity kept, five walls for five layers with
volume, 0.0 mm deviation, post-commit verification passed - and still reported
all_verified false, because Revit raised a warning the operation does not list
as expected.

Before that is called a defect it has to be measured, and two readings have to
be told apart:

  A. the conversion CREATED the condition - the sibling layer walls end up
     joined to each other while only touching face to face, which is precisely
     'joined but do not intersect';

  B. the warning was already in the document and the conversion merely happened
     to be the transaction that surfaced it.

They call for opposite fixes, so this asks the model which one is true: it reads
every wall's join partners and reports, for each joined pair, whether their
solids actually intersect. No transaction is opened and nothing is written.
"""
from Autodesk.Revit.DB import (FilteredElementCollector, Wall, JoinGeometryUtils,
                               Options, SolidCurveIntersectionOptions, ElementId,
                               BooleanOperationsUtils, BooleanOperationsType)

doc = __revit__.ActiveUIDocument.Document

walls = [w for w in FilteredElementCollector(doc).OfClass(Wall).WhereElementIsNotElementType()]
by_id = dict((w.Id.Value, w) for w in walls)


def type_name(w):
    try:
        return doc.GetElement(w.GetTypeId()).Name
    except Exception:
        return '?'


def solid_of(w):
    try:
        opt = Options()
        opt.ComputeReferences = False
        opt.DetailLevel = 3
        best = None
        for g in w.get_Geometry(opt):
            try:
                if g.Volume > 0 and (best is None or g.Volume > best.Volume):
                    best = g
            except Exception:
                pass
        return best
    except Exception:
        return None


# Walls this capability produced carry the ' - NN' suffix of its naming rule.
def is_produced(w):
    n = type_name(w)
    parts = n.split(' - ')
    return len(parts) >= 3 and len(parts[-1]) == 2 and parts[-1].isdigit()


pairs = []
seen = set()
for w in walls:
    try:
        joined = JoinGeometryUtils.GetJoinedElements(doc, w)
    except Exception:
        continue
    for oid in joined:
        other = by_id.get(oid.Value)
        if other is None:
            continue
        key = tuple(sorted((w.Id.Value, oid.Value)))
        if key in seen:
            continue
        seen.add(key)

        a, b = solid_of(w), solid_of(other)
        overlap = None
        if a is not None and b is not None:
            try:
                inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                    a, b, BooleanOperationsType.Intersect)
                overlap = inter.Volume if inter is not None else 0.0
            except Exception as ex:
                overlap = 'error: ' + str(ex)[:80]

        pairs.append({
            "a": w.Id.Value, "b": oid.Value,
            "a_type": type_name(w), "b_type": type_name(other),
            "a_produced": is_produced(w), "b_produced": is_produced(other),
            "both_produced": bool(is_produced(w) and is_produced(other)),
            "intersection_volume_ft3": overlap,
            "touching_only": (overlap == 0.0),
        })

produced = [w for w in walls if is_produced(w)]
sibling_pairs = [p for p in pairs if p["both_produced"]]
touching_sibling_pairs = [p for p in sibling_pairs if p["touching_only"]]

__output__ = {
    "status": "self_reported_verified",
    "summary": "which joined pairs do not intersect, and were they made here",
    "walls_total": len(walls),
    "walls_produced_by_split": len(produced),
    "joined_pairs_total": len(pairs),
    "joined_pairs_between_produced_walls": len(sibling_pairs),
    "joined_pairs_that_only_touch": len([p for p in pairs if p["touching_only"]]),
    "sibling_pairs_that_only_touch": len(touching_sibling_pairs),
    "reading": ("A: the conversion created the condition - sibling layer walls are joined "
                "to one another while only touching"
                if touching_sibling_pairs else
                "B: no sibling pair only touches; the warning comes from somewhere else"),
    "pairs": pairs,
}
