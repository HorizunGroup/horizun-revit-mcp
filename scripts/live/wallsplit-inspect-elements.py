# coding: utf-8
"""Read-only live probe for wall-split dependency classification fixtures."""
from Autodesk.Revit.DB import ElementId


ids = [425059, 425060, 425061, 425062, 425063, 425064, 425091, 425092, 425093]
rows = []
for raw in ids:
    element = doc.GetElement(ElementId(raw))
    if element is None:
        rows.append({"id": raw, "missing": True})
        continue
    category = None
    try:
        category = element.Category.Name if element.Category else None
    except Exception:
        pass
    rows.append({
        "id": raw,
        "class": element.GetType().FullName,
        "category": category,
        "name": getattr(element, "Name", None),
        "type_id": element.GetTypeId().Value,
        "dependents": [x.Value for x in element.GetDependentElements(None)],
    })

__output__ = {
    "status": "completed_unverified",
    "summary": "read-only wall split dependency probe",
    "rows": rows,
}
