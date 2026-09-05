# -*- coding: utf-8 -*-
"""Fixture pass four: the sweep and the reveal, whose types are plain ElementType
   under OST_Cornices and OST_Reveals - not a class called WallSweepType, which is
   what pass three looked for and did not find."""
from Autodesk.Revit.DB import (FilteredElementCollector, Wall, WallType, Level, XYZ, Line,
                               Transaction, BuiltInCategory, WallSweep, WallSweepInfo,
                               WallSweepType, ElementId)
MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
made, unbuildable, notes = {}, {}, []
def mm(v): return v * MM
levels = sorted([l for l in FilteredElementCollector(doc).OfClass(Level)], key=lambda l: l.Elevation)
level = levels[0]
MULTI = None
for wt in FilteredElementCollector(doc).OfClass(WallType):
    if wt.Name in ('M_Exterior - Brick on Mtl. Stud', 'Exterior - Brick on Mtl. Stud'):
        MULTI = wt; break

def type_in(cat):
    for t in FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType():
        return t
    return None

sweep_t = type_in(BuiltInCategory.OST_Cornices)
reveal_t = type_in(BuiltInCategory.OST_Reveals)

tx = Transaction(doc, 'HZ wallsplit fixture 4'); tx.Start()
try:
    X = 600000.0
    for key, etype, kind in (('c20_sweep', sweep_t, WallSweepType.Sweep),
                             ('c20_reveal', reveal_t, WallSweepType.Reveal)):
        line = Line.CreateBound(XYZ(mm(X), mm(0), 0), XYZ(mm(X), mm(6000), 0))
        w = Wall.Create(doc, line, MULTI.Id, level.Id, mm(3000), 0.0, False, False)
        doc.Regenerate()
        entry = {"wall": w.Id.Value, "unique_id": w.UniqueId}
        if etype is None:
            unbuildable[key] = 'no type for this sweep kind'
        else:
            try:
                info = WallSweepInfo(kind, True)
                info.Distance = mm(1200.0)
                s = WallSweep.Create(w, etype.Id, info)
                doc.Regenerate()
                entry["sweep"] = s.Id.Value
                entry["sweep_unique"] = s.UniqueId
                entry["kind"] = str(kind)
            except Exception as ex:
                unbuildable[key] = 'WallSweep.Create: ' + str(ex)
        made[key] = entry
        X += 8000.0
    doc.Regenerate(); tx.Commit(); status = 'completed_unverified'
except Exception as ex:
    tx.RollBack(); status = 'failed'; notes.append('FIXTURE 4 FAILED: ' + str(ex))
__output__ = {"status": status, "summary": "sweep and reveal", "made": made,
              "unbuildable": unbuildable, "notes": notes}
