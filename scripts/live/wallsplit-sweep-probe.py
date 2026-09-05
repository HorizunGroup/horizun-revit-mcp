# -*- coding: utf-8 -*-
"""Read-only probe for the two disposable sweep fixtures."""
from System import Enum
from Autodesk.Revit.DB import BuiltInParameter, ElementId

doc = __revit__.ActiveUIDocument.Document


def xyz(p):
    if p is None:
        return None
    return {"x_mm": p.X * 304.8, "y_mm": p.Y * 304.8, "z_mm": p.Z * 304.8}


def parameter_value(p):
    try:
        storage = str(p.StorageType)
        if storage == "Double":
            return {"internal": p.AsDouble(), "display": p.AsValueString()}
        if storage == "Integer":
            return {"internal": p.AsInteger(), "display": p.AsValueString()}
        if storage == "ElementId":
            eid = p.AsElementId()
            return {"internal": eid.Value, "display": p.AsValueString()}
        return {"internal": p.AsString(), "display": p.AsValueString()}
    except Exception as ex:
        return {"error": str(ex)}


rows = []
for eid in (425082, 425084):
    element = doc.GetElement(ElementId(eid))
    if element is None:
        rows.append({"id": eid, "missing": True})
        continue
    box = element.get_BoundingBox(None)
    info = element.GetWallSweepInfo()
    params = []
    for p in element.Parameters:
        pid = p.Id.Value
        bip = None
        if pid < 0:
            try:
                bip = Enum.GetName(BuiltInParameter, int(pid))
            except Exception:
                pass
        try:
            name = p.Definition.Name
        except Exception:
            name = None
        params.append({
            "name": name,
            "id": pid,
            "built_in": bip,
            "read_only": p.IsReadOnly,
            "storage": str(p.StorageType),
            "has_value": p.HasValue,
            "value": parameter_value(p),
        })
    rows.append({
        "id": eid,
        "unique_id": element.UniqueId,
        "host_ids": [x.Value for x in element.GetHostIds()],
        "bounds": None if box is None else {"min": xyz(box.Min), "max": xyz(box.Max)},
        "info": {
            "kind": str(info.WallSweepType),
            "wall_side": str(info.WallSide),
            "distance_mm": info.Distance * 304.8,
            "wall_offset_mm": info.WallOffset * 304.8,
            "is_vertical": info.IsVertical,
            "profile_id": info.ProfileId.Value,
        },
        "parameters": params,
    })

__output__ = {
    "status": "self_reported_verified",
    "summary": "read-only sweep parameter probe",
    "rows": rows,
    "verification": {"checked": True, "evidence": ["read directly from active document"]},
}
