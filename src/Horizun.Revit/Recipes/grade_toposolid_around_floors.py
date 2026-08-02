# -*- coding: ascii -*-
# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# GRADE A TOPOSOLID AROUND A PATH: OFFSET, BREAKLINES AND SIDE SLOPE.
#
# Ported from the "Grading TopoSolido" pyRevit button. It emulates a simple
# Civil 3D grading - a constant side slope run out from the path until it meets
# the existing terrain - and writes the whole thing into the toposolid as shape
# points and split lines:
#
#   * points along the slab edge, at the slab's own top elevation
#   * an inner ring just inside the edge, at the slab underside
#   * an outer offset ring, and split lines along it
#   * a DAYLIGHT line where the side slope finally meets the existing ground
#   * intermediate slope points and split lines between the two, so the terrain
#     is actually modelled between the path and daylight rather than interpolated
#
# The daylight search is per point: it walks outward along the slope until the
# slope's elevation crosses the sampled terrain, and gives up at max_search. A
# point where the two never cross has NO daylight, and that is reported rather
# than faked - "daylight_missing" is the number worth reading in the result.
#
# WHAT CHANGED IN THE PORT:
#
#   1. The five prompted numbers are arguments (offset, edge spacing, slope,
#      max search, slope point spacing). The slope still accepts the forms the
#      button accepted: "2:1" (H:V), "50%", or a bare ratio.
#   2. The toposolid was picked by clicking. Here toposolid_id is explicit;
#      omitted, it is auto-resolved ONLY when the document holds exactly one,
#      and that choice is reported. Ambiguity is refused with the candidates
#      listed - grading the wrong terrain is not a thing to resolve by guessing.
#   3. The transaction moved to the host, so the commit goes through Guard.
#   4. Element ids no longer go through the deprecated IntegerValue.
#
# THE `doc` GLOBAL. The functions below use a module-level `doc`, exactly as the
# button did, and the entry points bind it from the document the HOST resolved.
# That keeps ~1000 lines of proven geometry byte-for-byte identical instead of
# threading an argument through every one of them - the port's whole point is not
# to restart this code's bug history. It is safe because the bridge runs ONE
# command at a time on Revit's UI thread and refuses the second rather than
# queueing it, so two recipes can never be mid-flight together.
#
# No Transaction in this file. See Recipe.cs.
# -----------------------------------------------------------------------------

from __future__ import division, print_function

import math

from Autodesk.Revit.DB import (
    ElementId,
    FilteredElementCollector,
    Floor,
    GeometryInstance,
    HostObjectUtils,
    Options,
    Solid,
    Toposolid,
    UnitTypeId,
    UnitUtils,
    XYZ,
)

import hz


# Bound by plan()/apply()/verify() before any of the geometry below runs.
doc = None


class _Logger(object):
    """The button logged per-point failures - "this point could not be created",
    "this split line was refused" - to a pyRevit console somebody was watching.
    They are the difference between "1,200 points added" and "1,200 added, and
    here are the 14 Revit would not take", so they are kept verbatim: print goes
    into the reply's recipe_reported field. See Recipe.cs."""

    def warning(self, message):
        print(message)

    def error(self, message):
        print(message)


logger = _Logger()


EPS = 1e-9
DEFAULT_OFFSET_CM = 5.0
DEFAULT_INNER_OFFSET_CM = 1.0
DEFAULT_EDGE_SPACING_CM = 100.0
DEFAULT_SLOPE_TEXT = "2:1"
DEFAULT_MAX_SEARCH_CM = 1000.0
DEFAULT_SLOPE_POINT_SPACING_CM = 100.0
DEFAULT_DUPLICATE_TOL_MM = 5.0


def cm_to_internal(value_cm):
    return UnitUtils.ConvertToInternalUnits(value_cm, UnitTypeId.Centimeters)


def mm_to_internal(value_mm):
    return UnitUtils.ConvertToInternalUnits(value_mm, UnitTypeId.Millimeters)


def parse_number(value, fallback):
    if value is None:
        return fallback
    cleaned = str(value).strip().replace(",", ".")
    if not cleaned:
        return fallback
    return float(cleaned)


def parse_slope_ratio(value):
    if value is None:
        raise ValueError("Debes indicar un talud.")

    cleaned = str(value).strip().replace(",", ".").replace(" ", "")
    if not cleaned:
        raise ValueError("Debes indicar un talud.")

    if ":" in cleaned:
        parts = cleaned.split(":")
        if len(parts) != 2:
            raise ValueError("Talud invalido. Usa por ejemplo 2:1.")
        horizontal = float(parts[0])
        vertical = float(parts[1])
        if horizontal <= 0 or vertical <= 0:
            raise ValueError("El talud debe ser mayor que cero.")
        return horizontal / vertical

    if cleaned.endswith("%"):
        percent = float(cleaned[:-1])
        if percent <= 0:
            raise ValueError("El porcentaje debe ser mayor que cero.")
        return 100.0 / percent

    ratio = float(cleaned)
    if ratio <= 0:
        raise ValueError("El talud debe ser mayor que cero.")
    return ratio


def settings_from_args(args):
    """The five numbers the button prompted for, as arguments. Same defaults, same
    accepted slope forms ("2:1", "50%", or a bare ratio)."""
    offset_cm = parse_number(hz.arg(args, "offset_cm"), DEFAULT_OFFSET_CM)
    edge_spacing_cm = parse_number(hz.arg(args, "edge_spacing_cm"), DEFAULT_EDGE_SPACING_CM)
    slope_ratio = parse_slope_ratio(hz.arg(args, "slope", DEFAULT_SLOPE_TEXT))
    max_search_cm = parse_number(hz.arg(args, "max_search_cm"), DEFAULT_MAX_SEARCH_CM)
    slope_spacing_cm = parse_number(hz.arg(args, "slope_spacing_cm"), DEFAULT_SLOPE_POINT_SPACING_CM)

    if offset_cm <= 0 or edge_spacing_cm <= 0 or max_search_cm <= 0 or slope_spacing_cm <= 0:
        raise ValueError("offset_cm, edge_spacing_cm, max_search_cm and slope_spacing_cm "
                         "must all be greater than zero.")

    return {
        "offset": cm_to_internal(offset_cm),
        "edge_spacing": cm_to_internal(edge_spacing_cm),
        "slope_ratio": slope_ratio,
        "max_search": cm_to_internal(max_search_cm),
        "slope_spacing": cm_to_internal(slope_spacing_cm),
        "_cm": {
            "offset_cm": offset_cm,
            "edge_spacing_cm": edge_spacing_cm,
            "slope_ratio": slope_ratio,
            "max_search_cm": max_search_cm,
            "slope_spacing_cm": slope_spacing_cm,
        },
    }


def resolve_toposolid(args):
    """Explicit id, or the only one in the document. Ambiguity is REFUSED with the
    candidates named - the button clicked its way out of this, and grading the
    wrong terrain is not a decision to make on the caller's behalf."""
    topo_id = hz.arg(args, "toposolid_id")
    if topo_id:
        topo = doc.GetElement(hz.to_eid(topo_id))
        if not isinstance(topo, Toposolid):
            raise ValueError("toposolid_id {0} is not a Toposolid.".format(topo_id))
        return topo, "named by the caller"

    found = list(FilteredElementCollector(doc).OfClass(Toposolid)
                 .WhereElementIsNotElementType().ToElements())
    if not found:
        raise ValueError("This document contains no Toposolid, so there is no terrain to grade.")
    if len(found) > 1:
        raise ValueError(
            "This document contains {0} Toposolids and toposolid_id was not given: {1}. Name the "
            "one you mean.".format(len(found), ", ".join(str(hz.eid(t.Id)) for t in found)))
    return found[0], "the only Toposolid in the document"


def get_floor_shape_editor(floor):
    try:
        if hasattr(floor, "GetSlabShapeEditor"):
            return floor.GetSlabShapeEditor()
    except Exception:
        pass

    try:
        return floor.SlabShapeEditor
    except Exception:
        return None


def get_floor_sketch(floor):
    try:
        sketch_id = floor.SketchId
        if sketch_id is None:
            return None
        if hz.eid(sketch_id) < 0:
            return None
        return doc.GetElement(sketch_id)
    except Exception:
        return None


def iterate_geometry(geom):
    if geom is None:
        return
    for obj in geom:
        if isinstance(obj, Solid):
            if obj.Volume > 0:
                yield obj
        elif isinstance(obj, GeometryInstance):
            inst_geom = obj.GetInstanceGeometry()
            if inst_geom is None:
                continue
            for nested in iterate_geometry(inst_geom):
                yield nested


def triangle_projected_area_xy(a, b, c):
    return abs(
        0.5
        * (
            a.X * (b.Y - c.Y)
            + b.X * (c.Y - a.Y)
            + c.X * (a.Y - b.Y)
        )
    )


def triangle_normal_z(a, b, c):
    ux = b.X - a.X
    uy = b.Y - a.Y
    uz = b.Z - a.Z
    vx = c.X - a.X
    vy = c.Y - a.Y
    vz = c.Z - a.Z
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    length = math.sqrt((nx * nx) + (ny * ny) + (nz * nz))
    if length <= EPS:
        return 0.0
    return nz / length


def get_surface_triangles(element):
    opts = Options()
    opts.ComputeReferences = False
    opts.IncludeNonVisibleObjects = True
    geom = element.get_Geometry(opts)
    triangles = []

    if geom is None:
        return triangles

    for solid in iterate_geometry(geom):
        for face in solid.Faces:
            if face is None:
                continue

            mesh = face.Triangulate()
            if mesh is None:
                continue

            for index in range(mesh.NumTriangles):
                tri = mesh.get_Triangle(index)
                if tri is None:
                    continue
                a = tri.get_Vertex(0)
                b = tri.get_Vertex(1)
                c = tri.get_Vertex(2)
                if triangle_projected_area_xy(a, b, c) <= EPS:
                    continue
                if triangle_normal_z(a, b, c) <= 0.05:
                    continue
                triangles.append(
                    (
                        (a.X, a.Y, a.Z),
                        (b.X, b.Y, b.Z),
                        (c.X, c.Y, c.Z),
                    )
                )

    return triangles


def get_boundary_curves_from_shape_editor(floor):
    editor = get_floor_shape_editor(floor)
    if editor is None:
        return []

    try:
        creases = editor.SlabShapeCreases
    except Exception:
        return []

    boundary_curves = []
    for crease in creases:
        try:
            crease_type = getattr(crease, "CreaseType", None)
            if crease_type is None or "Boundary" not in str(crease_type):
                continue
            curve = getattr(crease, "Curve", None)
            if curve is None:
                continue
            boundary_curves.append(curve)
        except Exception:
            continue

    return boundary_curves


def points_match_xy(point_a, point_b, tolerance):
    return distance_xy(point_a, point_b) <= tolerance


def build_curve_loop_from_boundary_curves(curves, tolerance):
    if not curves:
        return []

    unused = []
    for curve in curves:
        try:
            start_point = curve.GetEndPoint(0)
            end_point = curve.GetEndPoint(1)
            unused.append(
                {
                    "curve": curve,
                    "start": start_point,
                    "end": end_point,
                }
            )
        except Exception:
            continue

    if not unused:
        return []

    ordered = [unused.pop(0)["curve"]]
    current_end = ordered[0].GetEndPoint(1)

    guard = 0
    while unused and guard < 10000:
        guard += 1
        found_index = None
        reverse_curve = False

        for index, item in enumerate(unused):
            if points_match_xy(item["start"], current_end, tolerance):
                found_index = index
                reverse_curve = False
                break
            if points_match_xy(item["end"], current_end, tolerance):
                found_index = index
                reverse_curve = True
                break

        if found_index is None:
            break

        next_item = unused.pop(found_index)
        next_curve = next_item["curve"].CreateReversed() if reverse_curve else next_item["curve"]
        ordered.append(next_curve)
        current_end = next_curve.GetEndPoint(1)

    return ordered


def get_outer_loop_from_shape_editor(floor, tolerance):
    boundary_curves = get_boundary_curves_from_shape_editor(floor)
    if not boundary_curves:
        return None

    ordered_curves = build_curve_loop_from_boundary_curves(boundary_curves, tolerance)
    if len(ordered_curves) < 2:
        return None

    return ordered_curves


def get_outer_loop_from_sketch(floor):
    sketch = get_floor_sketch(floor)
    if sketch is None:
        return None

    try:
        profile = sketch.Profile
    except Exception:
        return None

    if profile is None:
        return None

    best_loop = None
    best_area = -1.0

    for curve_array in profile:
        curve_loop = [curve for curve in curve_array]
        if len(curve_loop) < 2:
            continue

        pts = []
        for curve in curve_loop:
            try:
                pts.append(curve.GetEndPoint(0))
            except Exception:
                continue

        area = abs(signed_area_xy(pts))
        if area > best_area:
            best_area = area
            best_loop = curve_loop

    return best_loop


def barycentric_z(point_xy, triangle):
    ax, ay, az = triangle[0]
    bx, by, bz = triangle[1]
    cx, cy, cz = triangle[2]
    px, py = point_xy

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

    tol = -1e-6
    if u >= tol and v >= tol and w >= tol:
        return (u * az) + (v * bz) + (w * cz)

    return None


def sample_surface_z(point_xy, triangles):
    for triangle in triangles:
        z_value = barycentric_z(point_xy, triangle)
        if z_value is not None:
            return z_value
    return None


def get_top_face(floor):
    return get_host_face(floor, HostObjectUtils.GetTopFaces)


def get_bottom_face(floor):
    return get_host_face(floor, HostObjectUtils.GetBottomFaces)


def get_host_face(floor, face_getter):
    face_refs = face_getter(floor)
    if not face_refs or face_refs.Count == 0:
        return None

    max_area = -1.0
    best_face = None
    for face_ref in face_refs:
        face = floor.GetGeometryObjectFromReference(face_ref)
        if face and face.Area > max_area:
            max_area = face.Area
            best_face = face
    return best_face


def get_floor_thickness(floor):
    try:
        floor_type = doc.GetElement(floor.GetTypeId())
        if floor_type is None:
            return 0.0
        compound = floor_type.GetCompoundStructure()
        if compound is None:
            return 0.0
        width = compound.GetWidth()
        if width and width > 0.0:
            return width
    except Exception:
        pass
    return 0.0


def project_face_z(face, x_value, y_value, fallback_z):
    if face is None:
        return fallback_z

    try:
        result = face.Project(XYZ(x_value, y_value, fallback_z))
        if result is None:
            return fallback_z
        projected = getattr(result, "XYZPoint", None)
        if projected is None:
            return fallback_z
        return projected.Z
    except Exception:
        return fallback_z


def loop_to_points(curve_loop):
    points = []
    for curve in curve_loop:
        tess = list(curve.Tessellate())
        if not tess:
            continue
        if not points:
            points.extend(tess)
        else:
            points.extend(tess[1:])
    return points


def signed_area_xy(points):
    if len(points) < 3:
        return 0.0
    area = 0.0
    total = len(points)
    for index in range(total):
        point_a = points[index]
        point_b = points[(index + 1) % total]
        area += (point_a.X * point_b.Y) - (point_b.X * point_a.Y)
    return area * 0.5


def get_outer_loop(face):
    loops = face.GetEdgesAsCurveLoops()
    if not loops or loops.Count == 0:
        return None

    best_loop = None
    best_area = -1.0
    for curve_loop in loops:
        pts = loop_to_points(curve_loop)
        area = abs(signed_area_xy(pts))
        if area > best_area:
            best_area = area
            best_loop = curve_loop
    return best_loop


def normalized_xy(vector):
    length = math.sqrt((vector.X * vector.X) + (vector.Y * vector.Y))
    if length <= EPS:
        return None
    return XYZ(vector.X / length, vector.Y / length, 0.0)


def build_outward_normal(tangent_xy, is_ccw):
    if is_ccw:
        candidate = XYZ(tangent_xy.Y, -tangent_xy.X, 0.0)
    else:
        candidate = XYZ(-tangent_xy.Y, tangent_xy.X, 0.0)
    return normalized_xy(candidate)


def build_inward_normal(outward_normal):
    return XYZ(-outward_normal.X, -outward_normal.Y, 0.0)


def distance_xy(point_a, point_b):
    dx = point_a.X - point_b.X
    dy = point_a.Y - point_b.Y
    return math.sqrt((dx * dx) + (dy * dy))


def point_on_segment_2d(point_xy, seg_start_xy, seg_end_xy, tolerance):
    ax, ay = seg_start_xy
    bx, by = seg_end_xy
    px, py = point_xy

    abx = bx - ax
    aby = by - ay
    apx = px - ax
    apy = py - ay

    cross = (abx * apy) - (aby * apx)
    if abs(cross) > tolerance:
        return False

    dot = (apx * abx) + (apy * aby)
    if dot < -tolerance:
        return False

    sq_len = (abx * abx) + (aby * aby)
    if dot - sq_len > tolerance:
        return False

    return True


def point_in_ring_xy(point_xy, ring_points, tolerance):
    if ring_points is None or len(ring_points) < 3:
        return False

    inside = False
    count = len(ring_points)
    x_value, y_value = point_xy

    for index in range(count):
        point_a = ring_points[index]
        point_b = ring_points[(index + 1) % count]
        a_xy = (point_a.X, point_a.Y)
        b_xy = (point_b.X, point_b.Y)

        if point_on_segment_2d(point_xy, a_xy, b_xy, tolerance):
            return True

        x1, y1 = a_xy
        x2, y2 = b_xy
        intersects = ((y1 > y_value) != (y2 > y_value))
        if intersects:
            denom = (y2 - y1)
            if abs(denom) <= EPS:
                denom = EPS
            x_on_edge = x1 + ((y_value - y1) * (x2 - x1) / denom)
            if x_on_edge >= x_value - tolerance:
                inside = not inside

    return inside


def unique_key(point, tolerance):
    return (
        int(round(point.X / tolerance)),
        int(round(point.Y / tolerance)),
        int(round(point.Z / tolerance)),
    )


def sample_curve(curve, spacing):
    curve_length = curve.Length
    segments = max(1, int(math.ceil(curve_length / spacing)))
    samples = []
    for index in range(segments):
        parameter = float(index) / float(segments)
        point = curve.Evaluate(parameter, True)
        tangent = curve.ComputeDerivatives(parameter, True).BasisX
        samples.append((point, tangent))
    return samples


def trim_closed_ring(points, normals, tolerance):
    if len(points) < 3:
        return points, normals
    if distance_xy(points[0], points[-1]) <= tolerance:
        return points[:-1], normals[:-1]
    return points, normals


def add_ring_sample(point, normal, ring_points, ring_normals, tolerance):
    if not ring_points or distance_xy(point, ring_points[-1]) > tolerance:
        ring_points.append(point)
        ring_normals.append(normal)


def compute_grade_z(start_z, horizontal_distance, slope_ratio, slope_direction):
    return start_z + (slope_direction * (horizontal_distance / slope_ratio))


def evaluate_grade_difference(start_point, outward_normal, distance, slope_ratio, slope_direction, terrain_triangles):
    sample_xy = (
        start_point.X + (outward_normal.X * distance),
        start_point.Y + (outward_normal.Y * distance),
    )
    terrain_z = sample_surface_z(sample_xy, terrain_triangles)
    if terrain_z is None:
        return None
    grade_z = compute_grade_z(start_point.Z, distance, slope_ratio, slope_direction)
    return grade_z - terrain_z, terrain_z


def find_daylight_point(start_point, outward_normal, slope_ratio, max_search, step_distance, terrain_triangles):
    start_xy = (start_point.X, start_point.Y)
    terrain_at_start = sample_surface_z(start_xy, terrain_triangles)
    if terrain_at_start is None:
        return None

    slope_direction = -1.0 if terrain_at_start <= start_point.Z else 1.0
    initial_diff = start_point.Z - terrain_at_start

    if abs(initial_diff) <= 1e-6:
        return XYZ(start_point.X, start_point.Y, terrain_at_start)

    previous_distance = 0.0
    previous_diff = initial_diff

    steps = max(1, int(math.ceil(max_search / step_distance)))
    for step_index in range(1, steps + 1):
        current_distance = min(max_search, step_index * step_distance)
        result = evaluate_grade_difference(
            start_point,
            outward_normal,
            current_distance,
            slope_ratio,
            slope_direction,
            terrain_triangles,
        )
        if result is None:
            continue

        current_diff, _terrain_z = result
        if previous_diff == 0.0 or current_diff == 0.0 or (previous_diff * current_diff) < 0.0:
            low = previous_distance
            high = current_distance
            low_diff = previous_diff
            high_diff = current_diff

            for _ in range(12):
                mid = 0.5 * (low + high)
                mid_result = evaluate_grade_difference(
                    start_point,
                    outward_normal,
                    mid,
                    slope_ratio,
                    slope_direction,
                    terrain_triangles,
                )
                if mid_result is None:
                    break

                mid_diff, mid_terrain_z = mid_result
                if abs(mid_diff) <= 1e-6:
                    return XYZ(
                        start_point.X + (outward_normal.X * mid),
                        start_point.Y + (outward_normal.Y * mid),
                        mid_terrain_z,
                    )

                if low_diff * mid_diff <= 0.0:
                    high = mid
                    high_diff = mid_diff
                else:
                    low = mid
                    low_diff = mid_diff

            final_distance = high
            final_xy = (
                start_point.X + (outward_normal.X * final_distance),
                start_point.Y + (outward_normal.Y * final_distance),
            )
            final_terrain_z = sample_surface_z(final_xy, terrain_triangles)
            if final_terrain_z is None:
                return None
            return XYZ(final_xy[0], final_xy[1], final_terrain_z)

        previous_distance = current_distance
        previous_diff = current_diff

    return None


def build_slope_path(start_point, end_point, spacing, tolerance):
    if end_point is None:
        return []

    path = [start_point]
    horizontal_distance = distance_xy(start_point, end_point)
    if horizontal_distance <= tolerance:
        return path

    segments = max(1, int(math.ceil(horizontal_distance / spacing)))
    for index in range(1, segments):
        factor = float(index) / float(segments)
        path.append(
            XYZ(
                start_point.X + ((end_point.X - start_point.X) * factor),
                start_point.Y + ((end_point.Y - start_point.Y) * factor),
                start_point.Z + ((end_point.Z - start_point.Z) * factor),
            )
        )
    path.append(end_point)
    return path


def build_floor_data(floor, settings, terrain_triangles, tolerance):
    face = get_top_face(floor)
    if face is None:
        raise RuntimeError("No fue posible obtener la cara superior de la losa.")

    bottom_face = get_bottom_face(floor)
    floor_thickness = get_floor_thickness(floor)
    inner_offset = cm_to_internal(DEFAULT_INNER_OFFSET_CM)
    slab_surface_triangles = get_surface_triangles(floor)
    if not slab_surface_triangles and face is None:
        raise RuntimeError("No fue posible leer la superficie superior de la losa.")

    outer_loop = get_outer_loop_from_sketch(floor)
    if outer_loop is None:
        outer_loop = get_outer_loop_from_shape_editor(floor, tolerance)
    if outer_loop is None:
        perimeter_face = bottom_face if bottom_face is not None else face
        outer_loop = get_outer_loop(perimeter_face)
    if outer_loop is None:
        raise RuntimeError("No fue posible obtener el perimetro exterior de la losa.")

    loop_points = loop_to_points(outer_loop)
    is_ccw = signed_area_xy(loop_points) > 0.0

    edge_ring = []
    inner_ring = []
    offset_ring = []
    outward_normals = []
    previous_inner = None

    for curve in outer_loop:
        for base_point, tangent in sample_curve(curve, settings["edge_spacing"]):
            tangent_xy = normalized_xy(tangent)
            if tangent_xy is None:
                continue

            outward = build_outward_normal(tangent_xy, is_ccw)
            if outward is None:
                continue

            point_xy = (base_point.X, base_point.Y)
            top_z = sample_surface_z(point_xy, slab_surface_triangles)
            if top_z is None:
                top_z = project_face_z(face, base_point.X, base_point.Y, base_point.Z + floor_thickness)

            edge_point = XYZ(base_point.X, base_point.Y, top_z)
            inward = build_inward_normal(outward)
            inner_x = base_point.X + (inward.X * inner_offset)
            inner_y = base_point.Y + (inward.Y * inner_offset)
            fallback_z = top_z - floor_thickness
            inner_z = project_face_z(bottom_face, inner_x, inner_y, fallback_z)
            if inner_z >= top_z and floor_thickness > 0.0:
                inner_z = fallback_z
            inner_point = XYZ(inner_x, inner_y, inner_z)
            offset_point = XYZ(
                base_point.X + (outward.X * settings["offset"]),
                base_point.Y + (outward.Y * settings["offset"]),
                top_z,
            )

            add_ring_sample(edge_point, outward, edge_ring, outward_normals, tolerance)
            if previous_inner is None or distance_xy(inner_point, previous_inner) > tolerance:
                inner_ring.append(inner_point)
                previous_inner = inner_point
            if not offset_ring or distance_xy(offset_point, offset_ring[-1]) > tolerance:
                offset_ring.append(offset_point)

    edge_ring, outward_normals = trim_closed_ring(edge_ring, outward_normals, tolerance)
    inner_ring, _unused_inner_normals = trim_closed_ring(inner_ring, list(outward_normals), tolerance)
    offset_ring, _unused_normals = trim_closed_ring(offset_ring, list(outward_normals), tolerance)
    outward_normals = outward_normals[: len(offset_ring)]

    daylight_ring = []
    slope_paths = []
    all_points = list(edge_ring) + list(inner_ring) + list(offset_ring)

    for offset_point, outward_normal in zip(offset_ring, outward_normals):
        daylight_point = find_daylight_point(
            offset_point,
            outward_normal,
            settings["slope_ratio"],
            settings["max_search"],
            settings["slope_spacing"],
            terrain_triangles,
        )
        daylight_ring.append(daylight_point)

        if daylight_point is None:
            slope_paths.append([])
            continue

        slope_path = build_slope_path(
            offset_point,
            daylight_point,
            settings["slope_spacing"],
            tolerance,
        )
        slope_paths.append(slope_path)
        for point in slope_path[1:]:
            all_points.append(point)

    cleanup_ring = []
    for offset_point, daylight_point in zip(offset_ring, daylight_ring):
        cleanup_ring.append(daylight_point if daylight_point is not None else offset_point)

    return {
        "floor_id": hz.eid(floor.Id),
        "edge_ring": edge_ring,
        "inner_ring": inner_ring,
        "offset_ring": offset_ring,
        "daylight_ring": daylight_ring,
        "cleanup_ring": cleanup_ring,
        "slope_paths": slope_paths,
        "all_points": all_points,
    }


def add_point_to_editor(editor, point):
    if hasattr(editor, "AddPoint"):
        return editor.AddPoint(point)
    return editor.DrawPoint(point)


def add_split_line(editor, start_vertex, end_vertex):
    if hasattr(editor, "AddSplitLine"):
        return editor.AddSplitLine(start_vertex, end_vertex)
    return editor.DrawSplitLine(start_vertex, end_vertex)


def collect_vertices(editor, tolerance):
    vertices_by_key = {}
    try:
        vertices = editor.SlabShapeVertices
    except Exception:
        return vertices_by_key

    for vertex in vertices:
        position = getattr(vertex, "Position", None)
        if position is None:
            continue
        vertices_by_key[unique_key(position, tolerance)] = vertex

    return vertices_by_key


def delete_vertices_inside_ring(editor, ring_points, tolerance):
    deleted_count = 0
    skipped_count = 0

    if ring_points is None or len(ring_points) < 3:
        return deleted_count, skipped_count

    try:
        vertices = list(editor.SlabShapeVertices)
    except Exception:
        return deleted_count, skipped_count

    for vertex in vertices:
        position = getattr(vertex, "Position", None)
        if position is None:
            skipped_count += 1
            continue

        point_xy = (position.X, position.Y)
        if not point_in_ring_xy(point_xy, ring_points, tolerance):
            continue

        try:
            if editor.DeletePoint(vertex):
                deleted_count += 1
            else:
                skipped_count += 1
        except Exception:
            skipped_count += 1

    return deleted_count, skipped_count


def add_points(editor, floors_data, tolerance):
    vertices_by_key = collect_vertices(editor, tolerance)
    added_count = 0
    skipped_count = 0

    for floor_data in floors_data:
        for point in floor_data["all_points"]:
            key = unique_key(point, tolerance)
            if key in vertices_by_key:
                skipped_count += 1
                continue

            try:
                vertex = add_point_to_editor(editor, point)
                if vertex:
                    vertices_by_key[key] = vertex
                    added_count += 1
                else:
                    skipped_count += 1
            except Exception as ex:
                logger.warning("No se pudo crear un punto en {0}: {1}".format(point, ex))
                skipped_count += 1

    return added_count, skipped_count


def split_key(vertex_a, vertex_b, tolerance):
    key_a = unique_key(vertex_a.Position, tolerance)
    key_b = unique_key(vertex_b.Position, tolerance)
    return tuple(sorted((key_a, key_b)))


def add_ring_splitlines(editor, ring_points, vertices_by_key, tolerance, created_pairs):
    added_count = 0
    skipped_count = 0

    valid_indices = [index for index, point in enumerate(ring_points) if point is not None]
    if len(valid_indices) < 2:
        return added_count, skipped_count

    total = len(ring_points)
    for index in range(total):
        start_point = ring_points[index]
        end_point = ring_points[(index + 1) % total]

        if start_point is None or end_point is None:
            skipped_count += 1
            continue

        if distance_xy(start_point, end_point) <= tolerance:
            skipped_count += 1
            continue

        start_vertex = vertices_by_key.get(unique_key(start_point, tolerance))
        end_vertex = vertices_by_key.get(unique_key(end_point, tolerance))
        if start_vertex is None or end_vertex is None:
            skipped_count += 1
            continue

        pair_key = split_key(start_vertex, end_vertex, tolerance)
        if pair_key in created_pairs:
            skipped_count += 1
            continue

        try:
            result = add_split_line(editor, start_vertex, end_vertex)
            if result:
                created_pairs.add(pair_key)
                added_count += 1
            else:
                skipped_count += 1
        except Exception as ex:
            logger.warning("No se pudo crear split line de anillo: {0}".format(ex))
            skipped_count += 1

    return added_count, skipped_count


def add_path_splitlines(editor, path_points, vertices_by_key, tolerance, created_pairs):
    added_count = 0
    skipped_count = 0

    if len(path_points) < 2:
        return added_count, skipped_count

    for index in range(len(path_points) - 1):
        start_point = path_points[index]
        end_point = path_points[index + 1]

        if distance_xy(start_point, end_point) <= tolerance:
            skipped_count += 1
            continue

        start_vertex = vertices_by_key.get(unique_key(start_point, tolerance))
        end_vertex = vertices_by_key.get(unique_key(end_point, tolerance))
        if start_vertex is None or end_vertex is None:
            skipped_count += 1
            continue

        pair_key = split_key(start_vertex, end_vertex, tolerance)
        if pair_key in created_pairs:
            skipped_count += 1
            continue

        try:
            result = add_split_line(editor, start_vertex, end_vertex)
            if result:
                created_pairs.add(pair_key)
                added_count += 1
            else:
                skipped_count += 1
        except Exception as ex:
            logger.warning("No se pudo crear split line de talud: {0}".format(ex))
            skipped_count += 1

    return added_count, skipped_count


def apply_geometry_to_toposolid(toposolid, floors_data):
    tolerance = mm_to_internal(DEFAULT_DUPLICATE_TOL_MM)
    deleted_points = 0
    skipped_deletes = 0
    added_points = 0
    skipped_points = 0
    added_splits = 0
    skipped_splits = 0

    # No transaction here: the host opened one and commits it through Guard, so a
    # silent rollback becomes an error instead of a cheerful count. Everything
    # below is what used to sit inside the button's own Transaction.
    editor = toposolid.GetSlabShapeEditor()
    if editor is None:
        raise RuntimeError("This Toposolid exposes no SlabShapeEditor; its shape cannot be edited.")
    if not editor.IsEnabled:
        editor.Enable()

    for floor_data in floors_data:
        delete_result = delete_vertices_inside_ring(
            editor,
            floor_data["cleanup_ring"],
            tolerance,
        )
        deleted_points += delete_result[0]
        skipped_deletes += delete_result[1]

    doc.Regenerate()
    point_result = add_points(editor, floors_data, tolerance)
    added_points += point_result[0]
    skipped_points += point_result[1]

    doc.Regenerate()
    vertices_by_key = collect_vertices(editor, tolerance)
    created_pairs = set()

    for floor_data in floors_data:
        ring_result = add_ring_splitlines(
            editor,
            floor_data["offset_ring"],
            vertices_by_key,
            tolerance,
            created_pairs,
        )
        added_splits += ring_result[0]
        skipped_splits += ring_result[1]

        inner_ring_result = add_ring_splitlines(
            editor,
            floor_data["inner_ring"],
            vertices_by_key,
            tolerance,
            created_pairs,
        )
        added_splits += inner_ring_result[0]
        skipped_splits += inner_ring_result[1]

        daylight_result = add_ring_splitlines(
            editor,
            floor_data["daylight_ring"],
            vertices_by_key,
            tolerance,
            created_pairs,
        )
        added_splits += daylight_result[0]
        skipped_splits += daylight_result[1]

        for slope_path in floor_data["slope_paths"]:
            path_result = add_path_splitlines(
                editor,
                slope_path,
                vertices_by_key,
                tolerance,
                created_pairs,
            )
            added_splits += path_result[0]
            skipped_splits += path_result[1]

    return (
        deleted_points,
        skipped_deletes,
        added_points,
        skipped_points,
        added_splits,
        skipped_splits,
    )


# ---- the host contract: plan / apply / verify ------------------------------

def _bind(document):
    """Point the module-level `doc` at the document the HOST resolved. See the
    header for why the geometry above keeps using a global."""
    global doc
    doc = document


def _build(document, args):
    """Everything plan(), apply() and verify() need, recomputed each time. Stale
    geometry carried from a plan into an apply is the one thing that must not
    happen: the model can move in between."""
    _bind(document)

    settings = settings_from_args(args)
    toposolid, how = resolve_toposolid(args)

    terrain_triangles = get_surface_triangles(toposolid)
    if not terrain_triangles:
        raise RuntimeError("The Toposolid's current surface could not be read, so there is no "
                           "terrain for the side slope to daylight against.")

    scope = hz.resolve(document, args, lambda e: isinstance(e, Floor), of_class=Floor)
    if not scope.elements:
        raise RuntimeError("No floor resolved, so there is nothing to grade around.")

    tolerance = mm_to_internal(DEFAULT_DUPLICATE_TOL_MM)

    floors_data = []
    failed = []
    for floor in scope.elements:
        try:
            floors_data.append(build_floor_data(floor, settings, terrain_triangles, tolerance))
        except Exception as exc:
            failed.append({"id": hz.eid(floor.Id), "error": hz.brief(exc, 300)})

    return toposolid, how, settings, scope, floors_data, failed, tolerance


def _daylight_tally(floors_data):
    found = 0
    missing = 0
    for fd in floors_data:
        for point in fd["daylight_ring"]:
            if point is None:
                missing += 1
            else:
                found += 1
    return found, missing


def plan(document, args):
    toposolid, how, settings, scope, floors_data, failed, tolerance = _build(document, args)

    found, missing = _daylight_tally(floors_data)

    per_floor = []
    for fd in floors_data:
        d_found = len([p for p in fd["daylight_ring"] if p is not None])
        d_missing = len(fd["daylight_ring"]) - d_found
        per_floor.append({
            "id": fd["floor_id"],
            "edge_points": len(fd["edge_ring"]),
            "inner_points": len(fd["inner_ring"]),
            "offset_points": len(fd["offset_ring"]),
            "candidate_points": len(fd["all_points"]),
            "daylight_found": d_found,
            "daylight_missing": d_missing,
        })

    total_candidates = sum(len(fd["all_points"]) for fd in floors_data)

    return {
        "scope": scope.report(),
        "toposolid_id": hz.eid(toposolid.Id),
        "toposolid_resolved_by": how,
        "settings": settings["_cm"],
        "floors": per_floor,
        "floors_failed": failed,
        "would_add_points": total_candidates,
        "daylight_found": found,
        "daylight_missing": missing,
        "daylight_note": (None if missing == 0 else
            "{0} of {1} slope rays never met the existing terrain within max_search_cm. Those "
            "stations get NO daylight point and NO slope path - the grading simply stops there. "
            "Raise max_search_cm, or flatten the slope, if that is not what you want.".format(
                missing, missing + found)),
        "note": ("Existing toposolid points inside the graded footprint are DELETED first. The side "
                 "slope is a constant ratio run outward from the offset ring until it crosses the "
                 "sampled terrain - a simple Civil 3D style grading, not a corridor model."),
    }


def apply(document, args, plan):
    toposolid, how, settings, scope, floors_data, failed, tolerance = _build(document, args)

    if not floors_data:
        raise RuntimeError("No slab geometry could be read at apply time; nothing was written.")

    total_candidates = sum(len(fd["all_points"]) for fd in floors_data)
    if total_candidates == 0:
        raise RuntimeError("No points were generated for the Toposolid; nothing was written.")

    (deleted_points, skipped_deletes, added_points,
     skipped_points, added_splits, skipped_splits) = apply_geometry_to_toposolid(toposolid, floors_data)

    found, missing = _daylight_tally(floors_data)

    # The DISTINCT positions this run should have left behind, keyed the same way
    # the adder keys them, and CARRIED to verify() rather than recomputed there.
    #
    # This is the one recipe where an independent recomputation is impossible, and
    # it was measured rather than reasoned: the side slope daylights against the
    # EXISTING TERRAIN, and this apply() has just reshaped that terrain. Recomputing
    # afterwards samples a different surface and produces a different set of points -
    # 72 where the run applied 74 - so the check would fail on a run that did
    # everything right. Verification still means re-reading the MODEL and asking
    # whether a vertex is at each position; it just cannot re-derive which positions
    # to ask about, because the tool changed the input to that derivation.
    expected = set()
    positions = []
    for fd in floors_data:
        for p in fd["all_points"]:
            key = (int(round(p.X / tolerance)),
                   int(round(p.Y / tolerance)),
                   int(round(p.Z / tolerance)))
            if key not in expected:
                expected.add(key)
                positions.append([p.X, p.Y, p.Z])

    return {
        "toposolid_id": hz.eid(toposolid.Id),
        "floors_processed": len(floors_data),
        "floors_failed": failed,
        "points_deleted": deleted_points,
        "points_delete_skipped": skipped_deletes,
        "points_added": added_points,
        "points_skipped": skipped_points,
        "split_lines_added": added_splits,
        "split_lines_skipped": skipped_splits,
        "daylight_found": found,
        "daylight_missing": missing,
        "points_expected": len(expected),
        "points_expected_positions": positions,
    }


def verify(document, args, plan, applied):
    """After the commit, ask the MODEL whether a vertex is really at each position
    this grading meant to produce - not a tally of AddPoint calls that did not throw.

    The positions come from apply(), not from a fresh derivation: the side slope
    daylights against the existing terrain and apply() just reshaped that terrain,
    so recomputing here samples a surface that no longer exists. See apply().

    Vertices only. Iterating SlabShapeCreases crashes Revit on large toposolids,
    and a verification that can take the host down is not one."""
    _bind(document)

    toposolid, how = resolve_toposolid(args)
    tolerance = mm_to_internal(DEFAULT_DUPLICATE_TOL_MM)

    editor = toposolid.GetSlabShapeEditor()
    if editor is None:
        return {"points_present": 0, "intended_points": applied["points_expected"],
                "note": "the Toposolid no longer exposes a shape editor"}

    actual = []
    for vertex in editor.SlabShapeVertices:
        p = vertex.Position
        actual.append((p.X, p.Y, p.Z))

    wanted = [tuple(p) for p in applied.get("points_expected_positions", [])]

    # Revit snaps a point that lands on existing geometry by up to ~14mm, measured.
    # Matching by rounded key alone reported those as missing. See hz.match_positions.
    exact, near, tol = hz.match_positions(wanted, actual, ceiling=mm_to_internal(25.0))

    return {
        "points_present": exact + near,
        "points_exact": exact,
        "points_within_tolerance": near,
        "match_tolerance_mm": round(tol / mm_to_internal(1.0), 2),
        "points_recomputed": len(wanted),
        "intended_points": applied["points_expected"],
        "toposolid_vertices_now": len(actual),
        "note": (None if near == 0 else
                 "{0} of the {1} points sit within {2:.1f}mm of the position asked for rather than "
                 "exactly on it - Revit snaps onto existing geometry. Counted, and kept "
                 "distinguishable from the exact ones.".format(near, exact + near,
                                                               tol / mm_to_internal(1.0))),
    }
