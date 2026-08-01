# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# COPY A SLAB'S SHAPE ONTO OTHER SLABS.
#
# Ported from the "Adquirir Elevaciones Losa" pyRevit button. One floor has been
# warped with Modify Sub Elements - points raised, split lines drawn - and other
# floors need to follow the same surface. This reads the source's triangulated top
# face and, for each destination, creates shape points at: the destination's own
# boundary vertices, wherever the source's split lines cross that boundary, and
# every source vertex that falls inside it. All three are sampled off the source
# surface, so the destination lands ON it rather than near it.
#
# WHAT WAS FIXED IN THE PORT:
#
#   1. IT REFUSED A LEGITIMATELY WARPED SOURCE. The button accepted a source only
#      if it exposed more than four vertices ("no parece tener una forma
#      editada"). A rectangular slab with one corner raised has exactly four
#      vertices and IS edited - the commonest warped slab there is, refused with a
#      message saying it was not warped. The source is now judged by whether its
#      shape actually varies: more vertices than its boundary, OR non-boundary
#      creases, OR vertices at differing Z. Any one of the three is a real edit.
#
#   2. CURVED EDGES WERE REDUCED TO A POINT. get_face_loops_2d took only
#      GetEndPoint(0) of every edge curve, so an arc contributed its start point
#      and nothing else: the destination's boundary polygon cut straight across
#      the bulge, and points were placed and rejected against a shape that is not
#      the slab. Curved edges are now TESSELLATED, and a destination whose
#      boundary could not be read faithfully is reported rather than approximated.
#
#   3. THE RESET WAS SILENT AND DESTRUCTIVE. A destination that already carries
#      shape edits has them WIPED (ResetSlabShape) before the new points go on.
#      That is the right operation - you cannot merge two warps - but the button
#      did it without a word. plan() now names every destination that will lose
#      existing edits, before the transaction opens.
#
#   4. ONE DESTINATION CAN NO LONGER TAKE THE BATCH DOWN: each runs in its own
#      SubTransaction, so a failure rolls back alone and is reported by id.
#
#   5. Dead code removed (clip_segment_to_polygon was never called), and element
#      ids no longer go through the deprecated IntegerValue on Revit 2024+.
#
# No Transaction in this file. The host owns the commit - see Recipe.cs.
# -----------------------------------------------------------------------------
from __future__ import division

import math

from Autodesk.Revit.DB import (
    Floor, GeometryInstance, Options, Solid, XYZ, Line, SubTransaction
)

import hz

EPS = 1e-6
POINT_TOL = 1e-4

# How finely a curved edge is walked when turning a boundary into a polygon.
# Revit gives Tessellate() for exactly this; the fallback subdivision is only for
# a curve type that refuses it.
CURVE_STEPS = 16


# ---- 2D primitives (verbatim from the button) ------------------------------

def xyz(x, y, z=0.0):
    return XYZ(float(x), float(y), float(z))


def pt2(p):
    return (float(p.X), float(p.Y))


def dist2(a, b):
    dx = a[0] - b[0]
    dy = a[1] - b[1]
    return dx * dx + dy * dy


def almost_same_2d(a, b, tol=POINT_TOL):
    return dist2(a, b) <= tol * tol


def polygon_area(loop):
    area = 0.0
    count = len(loop)
    for i in range(count):
        x1, y1 = loop[i]
        x2, y2 = loop[(i + 1) % count]
        area += x1 * y2 - x2 * y1
    return 0.5 * area


def clean_loop(points, tol=POINT_TOL):
    clean = []
    for p in points:
        if not clean or not almost_same_2d(p, clean[-1], tol):
            clean.append(p)
    if len(clean) > 1 and almost_same_2d(clean[0], clean[-1], tol):
        clean.pop()
    return clean


def point_on_segment_2d(p, a, b, tol=POINT_TOL):
    ax, ay = a
    bx, by = b
    px, py = p
    abx = bx - ax
    aby = by - ay
    apx = px - ax
    apy = py - ay
    cross = abx * apy - aby * apx
    if abs(cross) > tol:
        return False
    dot = apx * abx + apy * aby
    if dot < -tol:
        return False
    sq_len = abx * abx + aby * aby
    if dot - sq_len > tol:
        return False
    return True


def point_in_ring(pt, ring):
    x, y = pt
    inside = False
    count = len(ring)
    for i in range(count):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % count]
        if point_on_segment_2d(pt, (x1, y1), (x2, y2)):
            return True
        intersect = ((y1 > y) != (y2 > y))
        if intersect:
            x_on_edge = x1 + (y - y1) * (x2 - x1) / ((y2 - y1) if abs(y2 - y1) > EPS else EPS)
            if x_on_edge >= x - POINT_TOL:
                inside = not inside
    return inside


def point_in_polygon_with_holes(pt, outer_ring, hole_rings):
    if not point_in_ring(pt, outer_ring):
        return False
    for hole in hole_rings:
        if point_in_ring(pt, hole):
            return False
    return True


def segment_intersection_params(a1, a2, b1, b2, tol=POINT_TOL):
    x1, y1 = a1
    x2, y2 = a2
    x3, y3 = b1
    x4, y4 = b2
    den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
    if abs(den) <= tol:
        return None
    pre = x1 * y2 - y1 * x2
    post = x3 * y4 - y3 * x4
    px = (pre * (x3 - x4) - (x1 - x2) * post) / den
    py = (pre * (y3 - y4) - (y1 - y2) * post) / den

    def seg_t(p1, p2, p):
        dx = p2[0] - p1[0]
        dy = p2[1] - p1[1]
        if abs(dx) >= abs(dy):
            if abs(dx) <= tol:
                return 0.0
            return (p[0] - p1[0]) / dx
        if abs(dy) <= tol:
            return 0.0
        return (p[1] - p1[1]) / dy

    p = (px, py)
    ta = seg_t(a1, a2, p)
    tb = seg_t(b1, b2, p)
    if -tol <= ta <= 1.0 + tol and -tol <= tb <= 1.0 + tol:
        return max(0.0, min(1.0, ta)), max(0.0, min(1.0, tb)), p
    return None


def barycentric_z(pt, tri):
    ax, ay, az = tri[0]
    bx, by, bz = tri[1]
    cx, cy, cz = tri[2]
    px, py = pt

    v0x = bx - ax
    v0y = by - ay
    v1x = cx - ax
    v1y = cy - ay
    v2x = px - ax
    v2y = py - ay

    den = v0x * v1y - v1x * v0y
    if abs(den) <= EPS:
        return None

    v = (v2x * v1y - v1x * v2y) / den
    w = (v0x * v2y - v2x * v0y) / den
    u = 1.0 - v - w

    tol = -1e-5
    if u >= tol and v >= tol and w >= tol:
        return u * az + v * bz + w * cz
    return None


# ---- geometry off the model ------------------------------------------------

def iterate_geometry(geom):
    for obj in geom:
        if isinstance(obj, Solid):
            if obj.Volume > 0:
                yield obj
        elif isinstance(obj, GeometryInstance):
            for nested in iterate_geometry(obj.GetInstanceGeometry()):
                yield nested


def triangle_projected_area_xy(a, b, c):
    return abs(0.5 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y)))


def triangle_normal_z(a, b, c):
    ux, uy, uz = b.X - a.X, b.Y - a.Y, b.Z - a.Z
    vx, vy, vz = c.X - a.X, c.Y - a.Y, c.Z - a.Z
    nz = ux * vy - uy * vx
    length = math.sqrt(
        (uy * vz - uz * vy) ** 2 + (uz * vx - ux * vz) ** 2 + nz * nz)
    if length <= EPS:
        return 0.0
    return nz / length


def get_top_faces(element):
    opts = Options()
    opts.ComputeReferences = True
    opts.IncludeNonVisibleObjects = True
    geom = element.get_Geometry(opts)
    top_faces = []
    for solid in iterate_geometry(geom):
        for face in solid.Faces:
            mesh = face.Triangulate()
            if mesh.NumTriangles == 0:
                continue
            upward_area = 0.0
            total_area = 0.0
            max_z = None
            for i in range(mesh.NumTriangles):
                tri = mesh.get_Triangle(i)
                a, b, c = tri.get_Vertex(0), tri.get_Vertex(1), tri.get_Vertex(2)
                area_xy = triangle_projected_area_xy(a, b, c)
                if area_xy <= EPS:
                    continue
                total_area += area_xy
                if triangle_normal_z(a, b, c) > 0.05:
                    upward_area += area_xy
                tri_max_z = max(a.Z, b.Z, c.Z)
                if max_z is None or tri_max_z > max_z:
                    max_z = tri_max_z
            if total_area > EPS and upward_area / total_area > 0.6:
                top_faces.append((face, max_z, upward_area))
    top_faces.sort(key=lambda item: (item[1], item[2]), reverse=True)
    return [item[0] for item in top_faces]


def _walk_curve(curve):
    """Every point of an edge, not just where it starts.

    The button took GetEndPoint(0) alone, which turns an arc into one point and
    the boundary polygon into something that cuts across the bulge. Tessellate is
    Revit's own answer to this; the manual subdivision is the fallback for a curve
    type that will not tessellate."""
    try:
        pts = [pt2(p) for p in curve.Tessellate()]
        if len(pts) >= 2:
            return pts[:-1]      # the next curve contributes this loop's next start
    except Exception:
        pass

    try:
        if isinstance(curve, Line):
            return [pt2(curve.GetEndPoint(0))]
        return [pt2(curve.Evaluate(i / float(CURVE_STEPS), True))
                for i in range(CURVE_STEPS)]
    except Exception:
        return [pt2(curve.GetEndPoint(0))]


def _is_straight(curve):
    return isinstance(curve, Line)


def get_face_loops_2d(face):
    """The face's outer ring and holes, in 2D. Also reports whether any edge was
    curved, so the caller can say the boundary is a tessellation rather than an
    exact polygon."""
    loops = []
    curved = False
    for curve_loop in face.GetEdgesAsCurveLoops():
        pts = []
        for curve in curve_loop:
            if not _is_straight(curve):
                curved = True
            pts.extend(_walk_curve(curve))
        pts = clean_loop(pts)
        if len(pts) >= 3:
            loops.append(pts)

    if not loops:
        return None, [], curved

    loops = sorted(loops, key=lambda lp: abs(polygon_area(lp)), reverse=True)
    return loops[0], loops[1:], curved


def get_faces_triangles(faces):
    triangles = []
    for face in faces:
        mesh = face.Triangulate()
        for i in range(mesh.NumTriangles):
            tri = mesh.get_Triangle(i)
            a, b, c = tri.get_Vertex(0), tri.get_Vertex(1), tri.get_Vertex(2)
            if triangle_projected_area_xy(a, b, c) <= EPS:
                continue
            if triangle_normal_z(a, b, c) <= 0.0:
                continue
            triangles.append(((a.X, a.Y, a.Z), (b.X, b.Y, b.Z), (c.X, c.Y, c.Z)))
    return triangles


def sample_surface_z(pt, triangles):
    for tri in triangles:
        z = barycentric_z(pt, tri)
        if z is not None:
            return z
    return None


# ---- the slab shape editor -------------------------------------------------

def get_vertex_position(vertex):
    return getattr(vertex, "Position", None)


def get_crease_endpoints(crease):
    curve = getattr(crease, "Curve", None)
    if curve is None:
        curve = getattr(crease, "FullCurve", None)
    if curve is not None:
        try:
            return curve.GetEndPoint(0), curve.GetEndPoint(1)
        except Exception:
            pass

    verts = getattr(crease, "Vertices", None)
    if verts:
        pts = [get_vertex_position(v) for v in verts]
        pts = [p for p in pts if p]
        if len(pts) >= 2:
            return pts[0], pts[-1]
    return None, None


def ensure_shape_editor(floor):
    """Via hz.shape_editor: on Revit 2026 the editor is a METHOD, and the button
    this was ported from read it as a PROPERTY - so it could not have run on 2026
    at all. Found by running this against a real one."""
    return hz.shape_editor(floor, enable=True)


def get_source_vertices(shape_editor):
    result = []
    for v in shape_editor.SlabShapeVertices:
        pos = get_vertex_position(v)
        if pos:
            result.append(pos)
    return result


def get_source_breaklines(shape_editor):
    lines = []
    for crease in shape_editor.SlabShapeCreases:
        crease_type = getattr(crease, "CreaseType", None)
        if crease_type and "Boundary" in str(crease_type):
            continue
        p0, p1 = get_crease_endpoints(crease)
        if p0 is None or p1 is None:
            continue
        if p0.DistanceTo(p1) <= POINT_TOL:
            continue
        lines.append((p0, p1))
    return lines


def has_non_boundary_breaklines(shape_editor):
    return len(get_source_breaklines(shape_editor)) > 0


def vertices_vary_in_z(vertices):
    """A rectangular slab with one corner raised has FOUR vertices and is warped.
    The button's 'more than four vertices' test called that unedited and refused
    it - the commonest warped slab there is."""
    if len(vertices) < 2:
        return False
    zs = [v.Z for v in vertices]
    return (max(zs) - min(zs)) > POINT_TOL


def shape_is_edited(shape_editor, outer_ring, hole_rings):
    boundary_vertex_count = len(outer_ring)
    for hole in hole_rings:
        boundary_vertex_count += len(hole)

    vertices = get_source_vertices(shape_editor)
    if len(vertices) > boundary_vertex_count:
        return True
    if vertices_vary_in_z(vertices):
        return True
    return has_non_boundary_breaklines(shape_editor)


def reset_destination_shape(doc, floor):
    editor = hz.shape_editor(floor)
    if editor is None:
        return None
    try:
        editor.ResetSlabShape()
    except Exception:
        return None
    doc.Regenerate()
    return ensure_shape_editor(floor)


# ---- building the point set ------------------------------------------------

def add_unique_point(store, pt):
    store[(round(pt[0] / POINT_TOL), round(pt[1] / POINT_TOL))] = pt


def build_boundary_vertex_points(dest_outer, dest_holes, triangles):
    points = {}
    for ring in [dest_outer] + list(dest_holes):
        for p in ring:
            z = sample_surface_z(p, triangles)
            if z is not None:
                add_unique_point(points, (p[0], p[1], z))
    return list(points.values())


def build_source_interior_points(source_vertices, dest_outer, dest_holes):
    points = {}
    for v in source_vertices:
        if point_in_polygon_with_holes((v.X, v.Y), dest_outer, dest_holes):
            add_unique_point(points, (v.X, v.Y, v.Z))
    return list(points.values())


def build_breakline_intersection_points(source_breaklines, dest_outer, dest_holes, triangles):
    points = {}
    rings = [dest_outer] + list(dest_holes)
    for p0, p1 in source_breaklines:
        a = (p0.X, p0.Y)
        b = (p1.X, p1.Y)
        for ring in rings:
            for i in range(len(ring)):
                hit = segment_intersection_params(a, b, ring[i], ring[(i + 1) % len(ring)])
                if not hit:
                    continue
                inter = hit[2]
                z = sample_surface_z(inter, triangles)
                if z is not None:
                    add_unique_point(points, (inter[0], inter[1], z))
    return list(points.values())


def build_points_for_destination(source_vertices, source_breaklines, dest_outer, dest_holes, triangles):
    candidates = build_boundary_vertex_points(dest_outer, dest_holes, triangles)
    candidates.extend(build_breakline_intersection_points(
        source_breaklines, dest_outer, dest_holes, triangles))
    candidates.extend(build_source_interior_points(source_vertices, dest_outer, dest_holes))

    unique = {}
    for pt in candidates:
        add_unique_point(unique, pt)
    return list(unique.values())


def apply_points(shape_editor, points, dest_outer, dest_holes):
    """Put each sampled elevation onto the destination, choosing the operation by
    whether a vertex is already there.

    THE BUTTON ONLY EVER CALLED DrawPoint, and that is why it did nothing at all
    to the commonest destination there is. A rectangular slab's candidate points
    are its four corners; those corners ALREADY EXIST as shape vertices, and
    DrawPoint on an existing vertex is refused. The button caught the exception,
    continued, and reported 'Puntos aplicados: 0' - a run that changed nothing and
    did not say why.

    So: a candidate that lands on an existing vertex MOVES that vertex to the
    sampled elevation (ModifySubElement); one that lands on open surface is drawn
    as a new point. Both are counted, and the two are reported apart, because
    'raised a corner you already had' and 'added a point you did not' are
    different things to have done to somebody's slab."""
    tol = POINT_TOL * 10.0     # a vertex within this of the candidate IS that candidate
    drawn = 0
    modified = 0
    refused = 0

    existing = []
    try:
        for v in shape_editor.SlabShapeVertices:
            existing.append(v)
    except Exception:
        existing = []

    for x, y, z in points:
        if not point_in_polygon_with_holes((x, y), dest_outer, dest_holes):
            continue

        here = None
        for v in existing:
            try:
                p = v.Position
            except Exception:
                continue
            if almost_same_2d((p.X, p.Y), (x, y), tol):
                here = v
                break

        if here is not None:
            try:
                shape_editor.ModifySubElement(here, z)
                modified += 1
            except Exception:
                refused += 1
            continue

        try:
            shape_editor.DrawPoint(xyz(x, y, z))
            drawn += 1
        except Exception:
            refused += 1

    return {"drawn": drawn, "modified": modified, "refused": refused,
            "applied": drawn + modified}


def get_destination_loops(dest):
    faces = get_top_faces(dest)
    if not faces:
        return None, None, False
    for face in faces:
        outer, holes, curved = get_face_loops_2d(face)
        if outer:
            return outer, holes, curved
    return None, None, False


# ---- the host contract: plan / apply / verify ------------------------------

def _read_source(doc, args):
    source_id = hz.arg(args, "source_floor_id")
    if not source_id:
        raise Exception("source_floor_id is required: it names the floor whose shape is copied.")

    source = doc.GetElement(hz.to_eid(source_id))
    if not isinstance(source, Floor):
        raise Exception("source_floor_id {0} is not a floor.".format(source_id))

    editor = ensure_shape_editor(source)
    if editor is None:
        raise Exception(
            "Floor {0} has no SlabShapeEditor, so it carries no shape to copy.".format(source_id))

    return source, editor


def plan(doc, args):
    source, source_editor = _read_source(doc, args)
    source_id = hz.eid(source.Id)

    source_faces = get_top_faces(source)
    if not source_faces:
        raise Exception("The top face of the source floor could not be read; there is no surface to sample.")

    triangles = get_faces_triangles(source_faces)
    if not triangles:
        raise Exception("The source floor's top surface could not be triangulated.")

    source_outer, source_holes, _ = get_destination_loops(source)
    source_vertices = get_source_vertices(source_editor)
    if not shape_is_edited(source_editor, source_outer or [], source_holes or []):
        raise Exception(
            "The source floor carries no shape edit: {0} vertices, all at the same elevation, and no "
            "split lines. There is nothing to copy - warp it with Modify Sub Elements first.".format(
                len(source_vertices)))

    source_breaklines = get_source_breaklines(source_editor)

    scope = hz.resolve(doc, args, lambda e: isinstance(e, Floor), of_class=Floor)

    eligible = []
    skipped = []

    for dest in scope.elements:
        dest_id = hz.eid(dest.Id)
        if dest_id == source_id:
            skipped.append({"id": dest_id, "reason": "this is the source floor"})
            continue

        if ensure_shape_editor(dest) is None:
            skipped.append({"id": dest_id, "reason": "no SlabShapeEditor: it cannot carry a shape"})
            continue

        dest_outer, dest_holes, curved = get_destination_loops(dest)
        if not dest_outer:
            skipped.append({"id": dest_id, "reason": "its top face boundary could not be read"})
            continue

        points = build_points_for_destination(
            source_vertices, source_breaklines, dest_outer, dest_holes, triangles)
        if not points:
            skipped.append({"id": dest_id,
                            "reason": "no point of the source surface falls inside this floor - "
                                      "check that the two overlap in plan"})
            continue

        editor = ensure_shape_editor(dest)
        already = shape_is_edited(editor, dest_outer, dest_holes)

        eligible.append({
            "id": dest_id,
            "would_create_points": len(points),
            "boundary_vertices": len(dest_outer) + sum(len(h) for h in dest_holes),
            "curved_boundary": curved,
            "existing_shape_will_be_reset": already,
        })

    to_reset = [e["id"] for e in eligible if e["existing_shape_will_be_reset"]]
    curved_dests = [e["id"] for e in eligible if e["curved_boundary"]]

    return {
        "scope": scope.report(),
        "source_floor_id": source_id,
        "source_vertices": len(source_vertices),
        "source_breaklines": len(source_breaklines),
        "source_triangles": len(triangles),
        "eligible": eligible,
        "skipped": skipped,
        "would_process": len(eligible),
        "would_create_points": sum(e["would_create_points"] for e in eligible),
        "destinations_whose_shape_will_be_reset": to_reset,
        "destructive_note": (None if not to_reset else
            "THESE FLOORS ALREADY CARRY SHAPE EDITS AND WILL LOSE THEM: " +
            ", ".join(str(i) for i in to_reset) + ". Two warps cannot be merged, so the existing "
            "shape is reset before the new points go on. This is not undoable from here."),
        "curved_boundary_note": (None if not curved_dests else
            "These floors have CURVED edges and their boundary is a tessellation, not an exact "
            "polygon: " + ", ".join(str(i) for i in curved_dests) + ". Points land on the sampled "
            "surface, but boundary vertices follow the tessellation."),
    }


def apply(doc, args, plan):
    source, source_editor = _read_source(doc, args)

    source_faces = get_top_faces(source)
    triangles = get_faces_triangles(source_faces)
    source_vertices = get_source_vertices(source_editor)
    source_breaklines = get_source_breaklines(source_editor)

    processed = []
    points_created = 0
    errors = []

    for entry in plan["eligible"]:
        dest = doc.GetElement(hz.to_eid(entry["id"]))
        if dest is None:
            errors.append({"id": entry["id"], "error": "vanished between plan and apply"})
            continue

        # One destination per SubTransaction: a failure rolls back THIS floor only,
        # so a half-applied shape never survives and the batch continues.
        sub = SubTransaction(doc)
        sub.Start()
        try:
            dest_outer, dest_holes, _ = get_destination_loops(dest)
            if not dest_outer:
                sub.RollBack()
                errors.append({"id": entry["id"], "error": "its boundary could not be read at apply time"})
                continue

            points = build_points_for_destination(
                source_vertices, source_breaklines, dest_outer, dest_holes, triangles)
            if not points:
                sub.RollBack()
                errors.append({"id": entry["id"], "error": "no transferable points at apply time"})
                continue

            editor = ensure_shape_editor(dest)
            if shape_is_edited(editor, dest_outer, dest_holes):
                editor = reset_destination_shape(doc, dest)
                if editor is None:
                    sub.RollBack()
                    errors.append({"id": entry["id"], "error": "its existing shape could not be reset"})
                    continue
            else:
                editor = ensure_shape_editor(dest)

            doc.Regenerate()
            tally = apply_points(editor, points, dest_outer, dest_holes)
            doc.Regenerate()

            if tally["applied"] == 0:
                sub.RollBack()
                errors.append({"id": entry["id"],
                               "error": "Revit accepted none of the {0} candidate points "
                                        "({1} refused)".format(len(points), tally["refused"])})
                continue

            sub.Commit()
            processed.append({"id": entry["id"], "points": tally["applied"],
                              "points_drawn": tally["drawn"],
                              "vertices_moved": tally["modified"],
                              "refused": tally["refused"]})
            points_created += tally["applied"]
        except Exception as exc:
            try:
                sub.RollBack()
            except Exception:
                pass
            errors.append({"id": entry["id"], "error": hz.brief(exc, 300)})

    return {
        "processed": processed,
        "processed_ids": [p["id"] for p in processed],
        "floors_shaped": len(processed),
        "points_created": points_created,
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    """After the commit, ask the MODEL: does each destination really carry a warped
    shape now. Counting DrawPoint calls that did not throw is not evidence - the
    shape can still fail to survive the commit."""
    warped = 0
    for entry in applied["processed"]:
        dest = doc.GetElement(hz.to_eid(entry["id"]))
        if not isinstance(dest, Floor):
            continue
        editor = hz.shape_editor(dest)
        if editor is None:
            continue
        try:
            vertices = get_source_vertices(editor)
            if vertices_vary_in_z(vertices) or len(get_source_breaklines(editor)) > 0:
                warped += 1
        except Exception:
            pass

    return {
        "floors_now_warped": warped,
        "intended_shaped": applied["floors_shaped"],
    }
