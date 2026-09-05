# -*- coding: utf-8 -*-
"""
The document's inventory, before and after the campaign.

Two runs of this bracket the whole session. What it counts is what a reader
needs to answer "did this campaign leave anything behind": how many walls exist,
how many are multilayer, how many wall types there are, and how many of those
types are ones this capability creates. A conversion that produced a wall nobody
asked for, or a type nobody cleaned up, shows as a difference here and nowhere
else.

It writes nothing. It only counts.
"""
from Autodesk.Revit.DB import FilteredElementCollector, Wall, WallType, ElementId

doc = __revit__.ActiveUIDocument.Document

walls = [w for w in FilteredElementCollector(doc).OfClass(Wall).WhereElementIsNotElementType()]
types = [t for t in FilteredElementCollector(doc).OfClass(WallType)]


def layer_count(wt):
    try:
        cs = wt.GetCompoundStructure()
        return 0 if cs is None else cs.LayerCount
    except Exception:
        return -1


multilayer = 0
single = 0
unknown = 0
for w in walls:
    try:
        n = layer_count(doc.GetElement(w.GetTypeId()))
    except Exception:
        n = -1
    if n < 0:
        unknown += 1
    elif n > 1:
        multilayer += 1
    else:
        single += 1

# Types this capability creates carry the ' - NN' suffix its naming rule
# specifies. Counting them separately is how a leftover type is noticed.
produced_types = []
for t in types:
    try:
        name = t.Name
    except Exception:
        continue
    parts = name.split(' - ')
    if len(parts) >= 3 and len(parts[-1]) == 2 and parts[-1].isdigit():
        produced_types.append({"id": t.Id.Value, "name": name})

__output__ = {
    "status": "self_reported_verified",
    "summary": "inventory of the open document",
    "document": doc.Title,
    "walls_total": len(walls),
    "walls_multilayer": multilayer,
    "walls_single_layer": single,
    "walls_unknown_structure": unknown,
    "wall_types_total": len(types),
    "wall_types_produced_by_split": len(produced_types),
    "produced_type_names": sorted([p["name"] for p in produced_types]),
    "wall_ids": sorted([w.Id.Value for w in walls]),
}
