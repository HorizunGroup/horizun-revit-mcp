# -*- coding: utf-8 -*-
"""
READ-ONLY. For each variant's wall set: which pairs are joined, do they actually
intersect, and does the model carry a standing warning about them.

This is what separates the star from the chain. Both transmit the cut - that was
measured - so the only thing left to choose between them is whether the joins
they create are geometrically meaningful, and whether Revit records a permanent
complaint about the ones that are not.

The intersection volume is COMPUTED, not inferred from the layer numbering: two
walls either share volume or they do not, and a boolean intersect says which.

Reads C:\\hz-live\\topo-joins-config.json:
    {"sets":[{"label":"star","walls":[1,2,3]}, ...]}
"""
import io
import json

from Autodesk.Revit.DB import (
    ElementId, Options, ViewDetailLevel, Solid, JoinGeometryUtils,
    BooleanOperationsUtils, BooleanOperationsType, Wall
)

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
problems = []


def biggest_solid(el):
    try:
        opt = Options()
        opt.ComputeReferences = False
        opt.DetailLevel = ViewDetailLevel.Fine
        best = None
        for g in el.get_Geometry(opt):
            if isinstance(g, Solid):
                try:
                    if g.Volume > 0 and (best is None or g.Volume > best.Volume):
                        best = g
                except Exception:
                    pass
        return best
    except Exception as ex:
        problems.append('geometry ' + str(el.Id.Value) + ': ' + str(ex))
        return None


cfg = json.loads(io.open(r'C:\hz-live\topo-joins-config.json', encoding='utf-8').read())

# Every standing warning in the document, with the elements it names.
standing = []
for w in doc.GetWarnings():
    try:
        standing.append({"text": w.GetDescriptionText(),
                         "elements": sorted([i.Value for i in w.GetFailingElements()])})
    except Exception:
        pass

sets_out = []
for s in cfg.get('sets', []):
    ids = [int(x) for x in s['walls']]
    id_set = set(ids)
    pairs, seen = [], set()

    for wid in ids:
        el = doc.GetElement(ElementId(wid))
        if el is None:
            continue
        try:
            joined = [j.Value for j in JoinGeometryUtils.GetJoinedElements(doc, el)]
        except Exception as ex:
            problems.append('joins of ' + str(wid) + ': ' + str(ex))
            continue
        for oid in joined:
            if oid not in id_set:
                # a join to something outside this variant's set: worth naming
                pairs.append({"a": wid, "b": oid, "in_set": False})
                continue
            key = tuple(sorted((wid, oid)))
            if key in seen:
                continue
            seen.add(key)

            a = doc.GetElement(ElementId(key[0]))
            b = doc.GetElement(ElementId(key[1]))
            sa, sb = biggest_solid(a), biggest_solid(b)
            vol = None
            if sa is not None and sb is not None:
                try:
                    inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                        sa, sb, BooleanOperationsType.Intersect)
                    vol = 0.0 if inter is None else round(inter.Volume, 9)
                except Exception as ex:
                    problems.append('intersect %s/%s: %s' % (key[0], key[1], str(ex)[:120]))
            pairs.append({
                "a": key[0], "b": key[1], "in_set": True,
                "intersection_volume": vol,
                # A join between solids that share no volume is the invalid one.
                "disjoint": (vol is not None and vol <= 0.0),
                "measurable": vol is not None,
            })

    mine = [w for w in standing
            if any(e in id_set for e in w['elements'])]

    disjoint_pairs = [p for p in pairs if p.get('in_set') and p.get('disjoint')]
    sets_out.append({
        "label": s['label'],
        "walls": ids,
        "joined_pairs": len(seen),
        "joined_pairs_disjoint": len(disjoint_pairs),
        "disjoint_detail": disjoint_pairs,
        "standing_warnings_naming_these_walls": len(mine),
        "warnings": mine,
        "pairs": pairs,
    })

__output__ = {
    "status": "self_reported_verified",
    "summary": "join graph, real intersection volume, and standing warnings per variant",
    "standing_warnings_document_total": len(standing),
    "sets": sets_out,
    "problems": problems,
}
