# -*- coding: utf-8 -*-
"""
One more attempt at the fabric fixture, because the types are NOT missing.

The first attempt reported 'FabricArea.Create: Create() takes at most 7
arguments (6 given)' and the document holds 10 FabricSheetType and 1
FabricAreaType. Blaming the document for that would have been wrong: the count
says the fixture is buildable and the exception says the CALL was wrong. Every
plausible overload is tried and the one that works is recorded, along with what
each of the others said.
"""
from Autodesk.Revit.DB import (FilteredElementCollector, Wall, WallType, Level, XYZ,
                               Line, Curve, CurveLoop, Transaction, ElementId, BuiltInParameter)
from Autodesk.Revit.DB.Structure import (FabricArea, FabricSheetType, FabricAreaType,
                                         StructuralWallUsage)
from System.Collections.Generic import List

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document
attempts, made, notes = [], {}, []


def mm(v):
    return v * MM


sheet_types = [t for t in FilteredElementCollector(doc).OfClass(FabricSheetType)]
area_types = [t for t in FilteredElementCollector(doc).OfClass(FabricAreaType)]
notes.append('FabricSheetType=%d FabricAreaType=%d' % (len(sheet_types), len(area_types)))

wall = None
for w in FilteredElementCollector(doc).OfClass(Wall).WhereElementIsNotElementType():
    try:
        if doc.GetElement(w.GetTypeId()).Name == 'HZ_StructuralSandwich':
            wall = w
    except Exception:
        pass

tx = Transaction(doc, 'HZ fabric retry')
tx.Start()
try:
    if wall is None:
        raise Exception('no HZ_StructuralSandwich wall to host fabric')
    if not sheet_types or not area_types:
        raise Exception('fabric types absent after all')

    loc = wall.Location.Curve
    p0, p1 = loc.GetEndPoint(0), loc.GetEndPoint(1)
    x = p0.X
    y0, y1 = min(p0.Y, p1.Y), max(p0.Y, p1.Y)
    ya, yb = y0 + mm(1000), y0 + mm(4000)

    def loop():
        pts = [XYZ(x, ya, mm(400)), XYZ(x, yb, mm(400)),
               XYZ(x, yb, mm(2400)), XYZ(x, ya, mm(2400))]
        c = List[Curve]()
        for i in range(4):
            c.Add(Line.CreateBound(pts[i], pts[(i + 1) % 4]))
        return c

    def loops():
        # IList<CurveLoop>, which is what the 7-argument overload asked for by
        # name: 'expected IList[CurveLoop], got List[Curve]'.
        cl = CurveLoop()
        for cu in loop():
            cl.Append(cu)
        lst = List[CurveLoop]()
        lst.Add(cl)
        return lst

    major = XYZ(0, 0, 1)
    at, st = area_types[0].Id, sheet_types[0].Id

    # Host as an ElementId rather than the element is the likeliest difference;
    # the remaining forms are tried so the record says what each one answered.
    # MEASURED, not guessed. The 5-argument form answered 'expected Element, got
    # ElementId', which names the overload that exists: it wants the host ELEMENT
    # and no major direction. The 6-argument forms were rejected outright.
    # THE OVERLOAD NAMED ITSELF. 'expected IList[CurveLoop], got List[Curve]' is
    # the whole diagnosis: the boundary is a list of LOOPS, not a flat list of
    # curves. Every earlier form failed on the argument it names, and none of
    # them was evidence that this document cannot host fabric.
    # THE SIGNATURE, read off the exceptions one argument at a time:
    #   'expected IList[CurveLoop], got List[Curve]'  -> the boundary is LOOPS
    #   'expected XYZ, got ElementId'   at position 5 -> there is a MINOR
    #                                                    direction between the
    #                                                    major one and the types
    # so the call is (doc, host, loops, major, minor, areaTypeId, sheetTypeId).
    # The wall lies in a plane of constant x, so up is the major direction and
    # along-the-wall is the minor one.
    minor = XYZ(0, 1, 0)
    candidates = [
        ('loops, major, minor, areaType, sheetType', lambda: FabricArea.Create(doc, wall, loops(), major, minor, at, st)),
        ('loops, minor, major, areaType, sheetType', lambda: FabricArea.Create(doc, wall, loops(), minor, major, at, st)),
    ]
    got = None
    for label, call in candidates:
        try:
            got = call()
            doc.Regenerate()
            attempts.append({"form": label, "result": "created id " + str(got.Id.Value)})
            break
        except Exception as ex:
            attempts.append({"form": label, "result": str(ex)[:220]})
            got = None

    if got is not None:
        made['c42_fabric'] = {"wall": wall.Id.Value, "unique_id": wall.UniqueId,
                              "type": doc.GetElement(wall.GetTypeId()).Name,
                              "fabric": got.Id.Value, "fabric_unique": got.UniqueId}
        doc.Regenerate()
        tx.Commit()
        status = 'self_reported_verified'
    else:
        tx.RollBack()
        status = 'completed_unverified'
        notes.append('no FabricArea.Create form succeeded; every attempt is recorded')
except Exception as ex:
    tx.RollBack()
    status = 'failed'
    notes.append('FABRIC RETRY FAILED: ' + str(ex))

__output__ = {"status": status, "summary": "fabric fixture retry",
              "made": made, "attempts": attempts, "notes": notes,
              "unbuildable": ({} if made else {"c42_fabric": "no FabricArea.Create overload accepted; see attempts"})}
