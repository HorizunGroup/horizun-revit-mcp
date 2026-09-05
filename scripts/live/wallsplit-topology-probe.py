# -*- coding: utf-8 -*-
"""
READ-ONLY. Measure how much solid material a wall has where a door passes.

This is the instrument for the topology experiment, and it is deliberately
INDEPENDENT of the product's own verifier. The verifier is one of the things
under test: if it and this disagree, that disagreement is itself the finding.
The method is the same five-point ray cast so the numbers are comparable -
centre of the door plus four points inset a quarter - but implemented here from
the raw solids.

For each (door, target wall) pair it casts a segment along the wall NORMAL
through each probe point, intersects it with the target wall's solid, and sums
the lengths. A wall with a real hole returns ~0 mm. A wall with no hole returns
its own thickness.

Reads its work list from C:\\hz-live\\topo-probe-config.json:

    {"cases":[{"label":"...","door":123,"targets":[456,789]}]}

Opens no transaction and writes nothing.
"""
import io
import json

from Autodesk.Revit.DB import (
    ElementId, XYZ, Line, Options, ViewDetailLevel, Solid,
    SolidCurveIntersectionOptions, FamilyInstance, Wall
)

MM = 1.0 / 304.8
doc = __revit__.ActiveUIDocument.Document

CONFIG = r'C:\hz-live\topo-probe-config.json'
TOLERANCE_MM = 0.5

notes, problems = [], []


def to_mm(feet):
    return feet / MM


def biggest_solid(el):
    """The element's largest solid at Fine detail - the one that carries its
    substance. Openings and joins are already applied to it by Revit."""
    try:
        opt = Options()
        opt.ComputeReferences = False
        opt.DetailLevel = ViewDetailLevel.Fine
        best = None
        stack = [el.get_Geometry(opt)]
        while stack:
            g = stack.pop()
            if g is None:
                continue
            for item in g:
                if isinstance(item, Solid):
                    try:
                        if item.Volume > 0 and (best is None or item.Volume > best.Volume):
                            best = item
                    except Exception:
                        pass
                else:
                    try:
                        stack.append(item.GetInstanceGeometry())
                    except Exception:
                        pass
        return best
    except Exception as ex:
        problems.append('geometry of ' + str(el.Id.Value) + ': ' + str(ex))
        return None


def probe_points(door, normal):
    """Five points in the door's plane: centre, and four inset a quarter."""
    bb = door.get_BoundingBox(None)
    if bb is None:
        return None, 'the door has no bounding box, so no probe point can be placed'
    lo, hi = bb.Min, bb.Max
    centre = XYZ((lo.X + hi.X) / 2.0, (lo.Y + hi.Y) / 2.0, (lo.Z + hi.Z) / 2.0)

    along = XYZ(-normal.Y, normal.X, 0)
    along = XYZ.BasisX if along.GetLength() < 1e-9 else along.Normalize()

    # Half-extent measured ALONG the wall, from the box corners projected on it.
    span_along = abs((hi - lo).DotProduct(along)) / 2.0
    span_z = (hi.Z - lo.Z) / 2.0

    pts = [
        ('centre', centre),
        ('quarter_left', centre - along.Multiply(span_along * 0.5)),
        ('quarter_right', centre + along.Multiply(span_along * 0.5)),
        ('quarter_low', XYZ(centre.X, centre.Y, centre.Z - span_z * 0.5)),
        ('quarter_high', XYZ(centre.X, centre.Y, centre.Z + span_z * 0.5)),
    ]
    return pts, None


def material_along_ray(solid, point, normal, span_feet):
    """Millimetres of solid the ray crosses. None when it cannot be measured -
    which is NOT the same as zero and must never be reported as a clear hole."""
    if solid is None:
        return None
    try:
        ray = Line.CreateBound(point - normal.Multiply(span_feet),
                               point + normal.Multiply(span_feet))
        res = solid.IntersectWithCurve(ray, SolidCurveIntersectionOptions())
        if res is None:
            return 0.0
        total = 0.0
        for i in range(res.SegmentCount):
            seg = res.GetCurveSegment(i)
            total += seg.Length
        return to_mm(total)
    except Exception as ex:
        problems.append('ray at ' + str(point) + ': ' + str(ex))
        return None


try:
    cfg = json.loads(io.open(CONFIG, encoding='utf-8').read())
except Exception as ex:
    cfg = {"cases": []}
    problems.append('config unreadable: ' + str(ex))

results = []
for case in cfg.get('cases', []):
    label = case.get('label')
    door = doc.GetElement(ElementId(int(case['door'])))
    if door is None or not isinstance(door, FamilyInstance):
        results.append({"label": label, "error": "door not found"})
        continue

    host = None
    try:
        host = door.Host
    except Exception:
        host = None
    normal = None
    try:
        normal = host.Orientation if isinstance(host, Wall) else None
    except Exception:
        normal = None
    if normal is None:
        # The host may be gone or may not be a wall; fall back to the door's own
        # facing, which is normal to the wall it was placed in.
        try:
            normal = door.FacingOrientation
            notes.append(label + ': normal taken from the door facing, not the host wall')
        except Exception:
            results.append({"label": label, "error": "no usable normal"})
            continue

    pts, why = probe_points(door, normal)
    if pts is None:
        results.append({"label": label, "error": why})
        continue

    span = 2.0  # feet each way: comfortably more than any of these walls is thick

    targets = []
    for tid in case.get('targets', []):
        w = doc.GetElement(ElementId(int(tid)))
        if w is None:
            targets.append({"wall": tid, "error": "wall not found"})
            continue
        solid = biggest_solid(w)
        try:
            tname = doc.GetElement(w.GetTypeId()).Name
        except Exception:
            tname = None
        rows, clear_points, measured_points = [], 0, 0
        for name, p in pts:
            mmv = material_along_ray(solid, p, normal, span)
            measured = mmv is not None
            clear = measured and mmv <= TOLERANCE_MM
            if measured:
                measured_points += 1
            if clear:
                clear_points += 1
            rows.append({"point": name, "measured": measured,
                         "material_mm": (round(mmv, 3) if measured else None),
                         "clear": clear})
        # Only three verdicts, and "not measured" is one of them.
        if measured_points < len(pts):
            status = 'unmeasurable'
        elif clear_points == len(pts):
            status = 'cut'
        elif clear_points == 0:
            status = 'solid'
        else:
            status = 'partial'
        targets.append({
            "wall": tid, "type": tname,
            "points_checked": len(pts), "measured_points": measured_points,
            "clear_points": clear_points, "cut_status": status, "probes": rows,
        })

    results.append({"label": label, "door": case['door'],
                    "normal": [round(normal.X, 6), round(normal.Y, 6), round(normal.Z, 6)],
                    "targets": targets})

__output__ = {
    "status": "self_reported_verified",
    "summary": "five-point material measurement, independent of the product verifier",
    "tolerance_mm": TOLERANCE_MM,
    "cases": results,
    "notes": notes,
    "problems": problems,
}
