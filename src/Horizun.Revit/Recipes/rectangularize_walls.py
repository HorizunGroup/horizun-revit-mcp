# -*- coding: ascii -*-
# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# TURN AN IRREGULAR ORTHOGONAL WALL INTO RECTANGULAR FRAGMENTS.
#
# Ported from the "Rectangularizar Muros" pyRevit button. A wall whose elevation
# profile has been edited into steps is one element with a complicated face;
# this reads its REAL solid geometry, partitions the profile into a grid around
# the openings, and rebuilds it as simple rectangular walls - re-hosting the
# doors and windows it carried.
#
# This was the most defensively written of the buttons in the set, and the port
# keeps every one of its refusals: it works ONLY on straight Basic Walls and
# gives up - by name, with a reason - on curved edges, non-rectangular openings,
# or any profile it cannot rebuild stably. It already ran each wall in its own
# SubTransaction, so one failure never took the batch down, and it already
# suppressed the overlap/join warnings that rebuilding a wall raises by
# construction. Those are kept as they were.
#
# WHAT CHANGED IN THE PORT - all of it the envelope, none of it the geometry:
#
#   1. Selection became arguments; the confirmation dialog became dry_run plus a
#      single-use token. analyze_wall() was ALREADY a plan - it returns "ready",
#      "regular" or a refusal with a reason - so plan() reports exactly what the
#      button used to compute and throw away behind a Yes/No box.
#   2. The outer Transaction moved to the host, so the commit goes through Guard
#      and a silent rollback becomes an error instead of a cheerful count. The
#      per-wall SubTransactions stay exactly where they were.
#   3. Element ids no longer prefer the deprecated IntegerValue.
#
# THE `doc` GLOBAL. The ~1800 lines below use a module-level `doc`, as the button
# did, and the entry points bind it from the document the HOST resolved. That
# keeps proven geometry byte-for-byte identical rather than threading an argument
# through a hundred functions - the point of the port is not to restart this
# code's bug history. Safe because the bridge runs ONE command at a time on
# Revit's UI thread and refuses the second rather than queueing it.
#
# No Transaction in this file. See Recipe.cs.
# -----------------------------------------------------------------------------

from Autodesk.Revit.DB import (
    BuiltInCategory, BuiltInParameter, Element, ElementId,
    ElementTransformUtils, FailureProcessingResult, FailureSeverity,
    FamilyInstance, FilteredElementCollector, GeometryInstance,
    IFailuresPreprocessor,
    JoinGeometryUtils, Line,
    LocationCurve, LocationPoint, Options, Opening, PlanarFace, Solid,
    StorageType, SubTransaction, Wall, WallKind, XYZ
)
from Autodesk.Revit.DB.Structure import StructuralType, StructuralWallUsage
from System.Collections.Generic import List

import hz


# Bound by plan()/apply()/verify() before any of the geometry below runs.
doc = None

TOL = 1e-6
SNAP_TOL = 1e-4
MIN_FRAGMENT_DIM = 0.02       # feet, approx. 6 mm
MIN_FRAGMENT_AREA = 0.0004    # square feet
MAX_GRID_CELLS = 2500


def get_id_value(element_id):
    # .Value first: Revit 2024+ made ElementId 64-bit and IntegerValue is the
    # deprecated one there. The original asked for IntegerValue first and got a
    # deprecation path on every modern Revit.
    try:
        return element_id.Value
    except Exception:
        pass
    try:
        return element_id.IntegerValue
    except Exception:
        pass
    try:
        return int(str(element_id))
    except Exception:
        return None


def is_valid_id(element_id):
    return (
        element_id is not None
        and get_id_value(element_id) != get_id_value(ElementId.InvalidElementId)
    )


def int_bip(name):
    try:
        return int(getattr(BuiltInParameter, name))
    except Exception:
        return None


def int_bic(name):
    try:
        return int(getattr(BuiltInCategory, name))
    except Exception:
        return None


def get_bip(name):
    try:
        return getattr(BuiltInParameter, name)
    except Exception:
        return None


WALL_CATEGORY_ID = int_bic("OST_Walls")
DOOR_CATEGORY_ID = int_bic("OST_Doors")
WINDOW_CATEGORY_ID = int_bic("OST_Windows")

SKIP_COPY_PARAM_IDS = set()
for _bip_name in [
    "ELEM_TYPE_PARAM",
    "SYMBOL_ID_PARAM",
    "WALL_BASE_CONSTRAINT",
    "WALL_BASE_OFFSET",
    "WALL_HEIGHT_TYPE",
    "WALL_USER_HEIGHT_PARAM",
    "WALL_TOP_OFFSET",
    "WALL_TOP_IS_ATTACHED",
    "WALL_BOTTOM_IS_ATTACHED",
    "WALL_TOP_EXTENSION_DIST_PARAM",
    "WALL_BASE_EXTENSION_DIST_PARAM",
    "WALL_BOTTOM_EXTENSION_DIST_PARAM",
    "WALL_ATTR_WIDTH_PARAM",
    "WALL_LOCATION_LINE",
    "CURVE_ELEM_LENGTH",
    "HOST_ID_PARAM",
]:
    _bip_id = int_bip(_bip_name)
    if _bip_id is not None:
        SKIP_COPY_PARAM_IDS.add(_bip_id)


class RectangularWallFailuresPreprocessor(IFailuresPreprocessor):
    def PreprocessFailures(self, failuresAccessor):
        for failure in failuresAccessor.GetFailureMessages():
            if failure.GetSeverity() == FailureSeverity.Warning:
                desc = failure.GetDescriptionText().lower()
                if (
                    "overlap" in desc
                    or "solap" in desc
                    or "join" in desc
                    or "union" in desc
                    or "identical" in desc
                ):
                    failuresAccessor.DeleteWarning(failure)
        return FailureProcessingResult.Continue


def failure_preprocessor():
    """Handed to the host, which installs it on ITS transaction. Rebuilding a wall
    as fragments raises overlap/join/identical warnings BY CONSTRUCTION; every
    other warning Revit raises is left alone and reaches the caller."""
    return RectangularWallFailuresPreprocessor()


def get_element_name(element):
    try:
        return Element.Name.GetValue(element)
    except Exception:
        pass
    try:
        return element.Name
    except Exception:
        return ""


def element_label(element):
    try:
        return "{0} (Id {1})".format(
            get_element_name(element) or "Muro",
            get_id_value(element.Id)
        )
    except Exception:
        return "Muro"


def is_wall_element(element):
    if not isinstance(element, Wall):
        return False
    try:
        if element.Category is not None and WALL_CATEGORY_ID is not None:
            return get_id_value(element.Category.Id) == WALL_CATEGORY_ID
    except Exception:
        pass
    return True


def dot(a, b):
    return a.X * b.X + a.Y * b.Y + a.Z * b.Z


def normalize(vector):
    try:
        if vector.GetLength() < TOL:
            return None
    except Exception:
        pass
    try:
        return vector.Normalize()
    except Exception:
        return None


def almost_equal(a, b, tol=SNAP_TOL):
    return abs(a - b) <= tol


def point_close_2d(a, b, tol=SNAP_TOL):
    return almost_equal(a[0], b[0], tol) and almost_equal(a[1], b[1], tol)


def rect_width(rect):
    return rect["u1"] - rect["u0"]


def rect_height(rect):
    return rect["z1"] - rect["z0"]


def rect_area(rect):
    return max(0.0, rect_width(rect)) * max(0.0, rect_height(rect))


def normalize_rect(u0, u1, z0, z1):
    rect = {
        "u0": min(u0, u1),
        "u1": max(u0, u1),
        "z0": min(z0, z1),
        "z1": max(z0, z1),
    }
    if rect_width(rect) < MIN_FRAGMENT_DIM or rect_height(rect) < MIN_FRAGMENT_DIM:
        return None
    if rect_area(rect) < MIN_FRAGMENT_AREA:
        return None
    return rect


def rect_contains_rect(container, inner, tol=SNAP_TOL):
    return (
        inner["u0"] >= container["u0"] - tol
        and inner["u1"] <= container["u1"] + tol
        and inner["z0"] >= container["z0"] - tol
        and inner["z1"] <= container["z1"] + tol
    )


def rect_center(rect):
    return (
        (rect["u0"] + rect["u1"]) * 0.5,
        (rect["z0"] + rect["z1"]) * 0.5
    )


def rect_intersection_area(a, b):
    u0 = max(a["u0"], b["u0"])
    u1 = min(a["u1"], b["u1"])
    z0 = max(a["z0"], b["z0"])
    z1 = min(a["z1"], b["z1"])
    if u1 <= u0 or z1 <= z0:
        return 0.0
    return (u1 - u0) * (z1 - z0)


def rects_similar(a, b):
    min_area = min(rect_area(a), rect_area(b))
    if min_area <= TOL:
        return False
    overlap = rect_intersection_area(a, b)
    if overlap / min_area > 0.50:
        return True
    return (
        almost_equal(a["u0"], b["u0"], 0.15)
        and almost_equal(a["u1"], b["u1"], 0.15)
        and almost_equal(a["z0"], b["z0"], 0.15)
        and almost_equal(a["z1"], b["z1"], 0.15)
    )


def clamp_rect(rect, bounds):
    return normalize_rect(
        max(bounds["u0"], rect["u0"]),
        min(bounds["u1"], rect["u1"]),
        max(bounds["z0"], rect["z0"]),
        min(bounds["z1"], rect["z1"])
    )


def get_wall_axis(wall):
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        raise Exception("no tiene LocationCurve")

    curve = loc.Curve
    if not isinstance(curve, Line):
        raise Exception("no es un muro lineal")

    p0 = curve.GetEndPoint(0)
    p1 = curve.GetEndPoint(1)
    if abs(p1.Z - p0.Z) > SNAP_TOL:
        raise Exception("la linea base del muro no es horizontal")

    direction = normalize(p1.Subtract(p0))
    if direction is None:
        raise Exception("linea base invalida")

    try:
        normal = wall.Orientation.Normalize()
    except Exception:
        normal = XYZ(-direction.Y, direction.X, 0.0)

    if abs(normal.Z) > 0.20:
        normal = XYZ(-direction.Y, direction.X, 0.0)
    normal = normalize(normal)
    if normal is None:
        normal = XYZ(-direction.Y, direction.X, 0.0)

    return {
        "origin": p0,
        "direction": direction,
        "normal": normal,
        "line_z": p0.Z,
    }


def project_point(axis, point):
    vector = point.Subtract(axis["origin"])
    return (dot(vector, axis["direction"]), point.Z)


def point_from_uz(axis, u, z):
    base = axis["origin"].Add(axis["direction"].Multiply(u))
    return XYZ(base.X, base.Y, z)


def point_on_axis_at_line_z(axis, u):
    base = axis["origin"].Add(axis["direction"].Multiply(u))
    return XYZ(base.X, base.Y, axis["line_z"])


def point_on_axis_at_z(axis, u, z):
    base = axis["origin"].Add(axis["direction"].Multiply(u))
    return XYZ(base.X, base.Y, z)


def get_level_elevation(wall):
    level = doc.GetElement(wall.LevelId)
    if level is not None:
        try:
            return level.Elevation
        except Exception:
            pass

    base_offset = 0.0
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)
        if param is not None:
            base_offset = param.AsDouble()
    except Exception:
        pass
    return get_wall_axis(wall)["line_z"] - base_offset


def get_wall_base_offset(wall):
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)
        if param is not None:
            return param.AsDouble()
    except Exception:
        pass
    return 0.0


def get_wall_base_elevation(wall):
    return get_level_elevation(wall) + get_wall_base_offset(wall)


def get_wall_top_offset(wall):
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)
        if param is not None:
            return param.AsDouble()
    except Exception:
        pass
    return 0.0


def get_wall_unconnected_height(wall):
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)
        if param is not None:
            return param.AsDouble()
    except Exception:
        pass
    return 0.0


def get_wall_top_elevation(wall):
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)
        if param is not None:
            top_level_id = param.AsElementId()
            if is_valid_id(top_level_id):
                top_level = doc.GetElement(top_level_id)
                if top_level is not None:
                    return top_level.Elevation + get_wall_top_offset(wall)
    except Exception:
        pass

    return get_wall_base_elevation(wall) + get_wall_unconnected_height(wall)


def clamp_loop_bottom_to_base(loop, base_z):
    clamped = []
    for u, z in loop:
        if z < base_z:
            z = base_z
        clamped.append((u, z))
    return simplify_loop(clamped)


def shift_loop_z(loop, delta_z):
    if abs(delta_z) <= SNAP_TOL:
        return loop
    shifted = []
    for u, z in loop:
        shifted.append((u, z + delta_z))
    return simplify_loop(shifted)


def get_profile_z_delta(raw_bounds, source_base_z, source_top_z):
    if raw_bounds is None:
        return 0.0

    bottom_delta = source_base_z - raw_bounds["z0"]
    top_delta = source_top_z - raw_bounds["z1"]

    if bottom_delta <= SNAP_TOL or top_delta <= SNAP_TOL:
        return 0.0

    tolerance = max(0.05, abs(bottom_delta) * 0.25)
    if abs(bottom_delta - top_delta) <= tolerance:
        return (bottom_delta + top_delta) * 0.5

    return 0.0


def iter_solids(geometry):
    if geometry is None:
        return

    for obj in geometry:
        if isinstance(obj, Solid):
            try:
                if obj.Volume > TOL and obj.Faces.Size > 0:
                    yield obj
            except Exception:
                pass
        elif isinstance(obj, GeometryInstance):
            try:
                inst_geometry = obj.GetInstanceGeometry()
            except Exception:
                inst_geometry = None
            for solid in iter_solids(inst_geometry):
                yield solid


def get_primary_side_face(wall, axis):
    opt = Options()
    opt.ComputeReferences = True
    opt.IncludeNonVisibleObjects = True

    best_face = None
    best_area = 0.0

    geometry = wall.get_Geometry(opt)
    for solid in iter_solids(geometry):
        for face in solid.Faces:
            if not isinstance(face, PlanarFace):
                continue
            try:
                face_normal = face.FaceNormal.Normalize()
            except Exception:
                continue

            # The elevation profile is read from the largest vertical side face.
            if abs(face_normal.Z) > 0.20:
                continue
            if abs(dot(face_normal, axis["normal"])) < 0.70:
                continue
            if abs(dot(face_normal, axis["direction"])) > 0.25:
                continue

            try:
                area = face.Area
            except Exception:
                area = 0.0
            if area > best_area:
                best_area = area
                best_face = face

    if best_face is None:
        raise Exception("no se pudo leer una cara lateral plana del solido")
    return best_face


def is_line_curve(curve):
    return isinstance(curve, Line)


def order_segments_as_loop(segments):
    if not segments:
        return None

    for start_index in range(len(segments)):
        for reversed_start in [False, True]:
            unused = list(segments)
            segment = unused.pop(start_index)
            if reversed_start:
                points = [segment[1], segment[0]]
            else:
                points = [segment[0], segment[1]]

            while unused:
                found_index = None
                found_point = None

                for i, candidate in enumerate(unused):
                    if point_close_2d(points[-1], candidate[0]):
                        found_index = i
                        found_point = candidate[1]
                        break
                    if point_close_2d(points[-1], candidate[1]):
                        found_index = i
                        found_point = candidate[0]
                        break

                if found_index is None:
                    break

                points.append(found_point)
                unused.pop(found_index)

            if not unused and point_close_2d(points[-1], points[0]):
                points.pop()
                return points

    return None


def make_snap_values(values):
    sorted_values = sorted(values)
    clusters = []

    for value in sorted_values:
        if not clusters:
            clusters.append([value])
            continue
        cluster = clusters[-1]
        avg = sum(cluster) / float(len(cluster))
        if abs(value - avg) <= SNAP_TOL:
            cluster.append(value)
        else:
            clusters.append([value])

    snap_values = []
    for cluster in clusters:
        snap_values.append(round(sum(cluster) / float(len(cluster)), 7))
    return snap_values


def snap_value(value, snap_values):
    best = None
    best_dist = None
    for snap in snap_values:
        dist = abs(value - snap)
        if best_dist is None or dist < best_dist:
            best = snap
            best_dist = dist
    if best_dist is not None and best_dist <= SNAP_TOL * 2.0:
        return best
    return round(value, 7)


def snap_loops(raw_loops):
    us = []
    zs = []
    for loop in raw_loops:
        for u, z in loop:
            us.append(u)
            zs.append(z)

    snap_us = make_snap_values(us)
    snap_zs = make_snap_values(zs)

    snapped = []
    for loop in raw_loops:
        snapped_loop = []
        for u, z in loop:
            snapped_loop.append((snap_value(u, snap_us), snap_value(z, snap_zs)))
        snapped.append(snapped_loop)
    return snapped


def simplify_loop(points):
    if not points:
        return []

    cleaned = []
    for point in points:
        if not cleaned or not point_close_2d(point, cleaned[-1]):
            cleaned.append(point)

    if len(cleaned) > 1 and point_close_2d(cleaned[0], cleaned[-1]):
        cleaned.pop()

    changed = True
    while changed and len(cleaned) > 2:
        changed = False
        result = []
        count = len(cleaned)
        for i, point in enumerate(cleaned):
            prev_point = cleaned[(i - 1) % count]
            next_point = cleaned[(i + 1) % count]
            same_u = almost_equal(prev_point[0], point[0]) and almost_equal(point[0], next_point[0])
            same_z = almost_equal(prev_point[1], point[1]) and almost_equal(point[1], next_point[1])
            if same_u or same_z:
                changed = True
                continue
            result.append(point)
        cleaned = result

    return cleaned


def polygon_area(points):
    area = 0.0
    count = len(points)
    for i, point in enumerate(points):
        next_point = points[(i + 1) % count]
        area += point[0] * next_point[1] - next_point[0] * point[1]
    return area * 0.5


def loop_is_orthogonal(points):
    if len(points) < 4:
        return False
    count = len(points)
    for i, point in enumerate(points):
        next_point = points[(i + 1) % count]
        du = abs(next_point[0] - point[0])
        dz = abs(next_point[1] - point[1])
        if du <= SNAP_TOL and dz <= SNAP_TOL:
            return False
        if du > SNAP_TOL and dz > SNAP_TOL:
            return False
    return True


def unique_sorted(values):
    result = []
    for value in sorted(values):
        if not result or not almost_equal(value, result[-1]):
            result.append(value)
    return result


def loop_to_rect(points):
    points = simplify_loop(points)
    if len(points) != 4:
        return None
    if not loop_is_orthogonal(points):
        return None

    us = unique_sorted([p[0] for p in points])
    zs = unique_sorted([p[1] for p in points])
    if len(us) != 2 or len(zs) != 2:
        return None
    return normalize_rect(us[0], us[1], zs[0], zs[1])


def loop_bounds(points):
    return normalize_rect(
        min([p[0] for p in points]),
        max([p[0] for p in points]),
        min([p[1] for p in points]),
        max([p[1] for p in points])
    )


def extract_face_loops(face, axis):
    raw_loops = []

    for edge_loop in face.EdgeLoops:
        segments = []
        has_non_linear_edge = False
        for edge in edge_loop:
            curve = edge.AsCurve()
            if not is_line_curve(curve):
                has_non_linear_edge = True
                break
            p0 = curve.GetEndPoint(0)
            p1 = curve.GetEndPoint(1)
            q0 = project_point(axis, p0)
            q1 = project_point(axis, p1)
            if point_close_2d(q0, q1):
                continue
            segments.append((q0, q1))

        if has_non_linear_edge:
            # Circular or complex secondary holes must not cancel the wall.
            # They are ignored unless they are represented by a valid door or
            # window insert, which is handled separately.
            continue

        ordered = order_segments_as_loop(segments)
        if ordered is None:
            continue
        raw_loops.append(ordered)

    if not raw_loops:
        raise Exception("la cara lateral no contiene contornos")

    loops = []
    for loop in snap_loops(raw_loops):
        simplified = simplify_loop(loop)
        if len(simplified) >= 4:
            loops.append(simplified)

    if not loops:
        raise Exception("no se encontraron contornos validos")

    return loops


def point_in_polygon(point, polygon):
    x = point[0]
    y = point[1]
    inside = False
    count = len(polygon)
    j = count - 1

    for i in range(count):
        xi, yi = polygon[i]
        xj, yj = polygon[j]
        if (yi > y) != (yj > y):
            denom = yj - yi
            if abs(denom) > TOL:
                x_at_y = (xj - xi) * (y - yi) / denom + xi
                if x < x_at_y:
                    inside = not inside
        j = i

    return inside


def point_in_rect(point, rect):
    return (
        point[0] > rect["u0"] + SNAP_TOL
        and point[0] < rect["u1"] - SNAP_TOL
        and point[1] > rect["z0"] + SNAP_TOL
        and point[1] < rect["z1"] - SNAP_TOL
    )


def collect_grid_values(outer, opening_rects):
    us = [p[0] for p in outer]
    zs = [p[1] for p in outer]
    for opening in opening_rects:
        us.extend([opening["u0"], opening["u1"]])
        zs.extend([opening["z0"], opening["z1"]])
    return unique_sorted(us), unique_sorted(zs)


def rectangle_from_indices(xs, zs, i0, i1, j0, j1):
    return normalize_rect(xs[i0], xs[i1], zs[j0], zs[j1])


def find_largest_cell_rectangle(active_cells, xs, zs):
    nx = len(xs) - 1
    ny = len(zs) - 1
    best = None
    best_area = -1.0
    best_cell_count = -1

    for cell in list(active_cells):
        i0, j0 = cell
        max_j = ny

        for i1 in range(i0 + 1, nx + 1):
            column = i1 - 1
            if (column, j0) not in active_cells:
                break

            j = j0
            while j < max_j and (column, j) in active_cells:
                j += 1
            max_j = j

            for j1 in range(j0 + 1, max_j + 1):
                width = xs[i1] - xs[i0]
                height = zs[j1] - zs[j0]
                area = width * height
                cell_count = (i1 - i0) * (j1 - j0)
                if (
                    area > best_area + TOL
                    or (abs(area - best_area) <= TOL and cell_count > best_cell_count)
                ):
                    best = (i0, i1, j0, j1)
                    best_area = area
                    best_cell_count = cell_count

    return best


def remove_cell_rectangle(active_cells, rect_index):
    i0, i1, j0, j1 = rect_index
    for i in range(i0, i1):
        for j in range(j0, j1):
            if (i, j) in active_cells:
                active_cells.remove((i, j))


def merge_rectangles(rectangles):
    rects = list(rectangles)
    changed = True

    while changed:
        changed = False
        merged = []
        used = set()

        for i, a in enumerate(rects):
            if i in used:
                continue
            current = dict(a)

            for j in range(i + 1, len(rects)):
                if j in used:
                    continue
                b = rects[j]

                same_z = almost_equal(current["z0"], b["z0"]) and almost_equal(current["z1"], b["z1"])
                touches_u = almost_equal(current["u1"], b["u0"]) or almost_equal(b["u1"], current["u0"])
                if same_z and touches_u:
                    current = normalize_rect(
                        min(current["u0"], b["u0"]),
                        max(current["u1"], b["u1"]),
                        current["z0"],
                        current["z1"]
                    )
                    used.add(j)
                    changed = True
                    continue

                same_u = almost_equal(current["u0"], b["u0"]) and almost_equal(current["u1"], b["u1"])
                touches_z = almost_equal(current["z1"], b["z0"]) or almost_equal(b["z1"], current["z0"])
                if same_u and touches_z:
                    current = normalize_rect(
                        current["u0"],
                        current["u1"],
                        min(current["z0"], b["z0"]),
                        max(current["z1"], b["z1"])
                    )
                    used.add(j)
                    changed = True

            used.add(i)
            if current is not None:
                merged.append(current)

        rects = merged

    return rects


def partition_outer_profile(outer, opening_rects):
    xs, zs = collect_grid_values(outer, opening_rects)
    nx = len(xs) - 1
    ny = len(zs) - 1

    if nx <= 0 or ny <= 0:
        raise Exception("contorno sin area util")
    if nx * ny > MAX_GRID_CELLS:
        raise Exception("perfil demasiado fragmentado para procesar de forma segura")

    active_cells = set()
    for i in range(nx):
        if xs[i + 1] - xs[i] < MIN_FRAGMENT_DIM:
            continue
        for j in range(ny):
            if zs[j + 1] - zs[j] < MIN_FRAGMENT_DIM:
                continue
            center = ((xs[i] + xs[i + 1]) * 0.5, (zs[j] + zs[j + 1]) * 0.5)
            if point_in_polygon(center, outer):
                active_cells.add((i, j))
                continue
            for opening in opening_rects:
                if point_in_rect(center, opening):
                    active_cells.add((i, j))
                    break

    rectangles = []
    while active_cells:
        rect_index = find_largest_cell_rectangle(active_cells, xs, zs)
        if rect_index is None:
            break
        rect = rectangle_from_indices(xs, zs, rect_index[0], rect_index[1], rect_index[2], rect_index[3])
        remove_cell_rectangle(active_cells, rect_index)
        if rect is not None:
            rectangles.append(rect)

    if not rectangles:
        raise Exception("no se pudo convertir el perfil en rectangulos")

    rectangles = merge_rectangles(rectangles)
    rectangles.sort(key=lambda r: (r["u0"], r["z0"], -rect_area(r)))
    return rectangles


def get_family_category_id(family_instance):
    try:
        if family_instance.Category is not None:
            return get_id_value(family_instance.Category.Id)
    except Exception:
        pass
    return None


def get_hosted_family_instances(wall):
    hosted = []
    try:
        dep_ids = wall.GetDependentElements(None)
    except Exception:
        dep_ids = []

    for element_id in dep_ids:
        elem = doc.GetElement(element_id)
        if not isinstance(elem, FamilyInstance):
            continue
        try:
            host = elem.Host
        except Exception:
            host = None
        if host is not None and host.Id == wall.Id:
            hosted.append(elem)

    return hosted


def family_bbox_to_rect(axis, family_instance):
    bbox = family_instance.get_BoundingBox(None)
    if bbox is None:
        return None

    points = []
    for x in [bbox.Min.X, bbox.Max.X]:
        for y in [bbox.Min.Y, bbox.Max.Y]:
            for z in [bbox.Min.Z, bbox.Max.Z]:
                points.append(project_point(axis, XYZ(x, y, z)))

    return normalize_rect(
        min([p[0] for p in points]),
        max([p[0] for p in points]),
        min([p[1] for p in points]),
        max([p[1] for p in points])
    )


def capture_family_info(axis, family_instance):
    loc = family_instance.Location
    point = None
    rotation = 0.0
    if isinstance(loc, LocationPoint):
        point = loc.Point
        try:
            rotation = loc.Rotation
        except Exception:
            rotation = 0.0

    if point is None:
        return None

    info = {
        "element_id": family_instance.Id,
        "symbol": family_instance.Symbol,
        "point": point,
        "projected_point": project_point(axis, point),
        "level_id": family_instance.LevelId,
        "hand_flipped": False,
        "facing_flipped": False,
        "rotation": rotation,
        "name": get_element_name(family_instance.Symbol) or get_element_name(family_instance),
    }

    try:
        info["hand_flipped"] = family_instance.HandFlipped
    except Exception:
        pass
    try:
        info["facing_flipped"] = family_instance.FacingFlipped
    except Exception:
        pass

    return info


def add_opening_info(openings, info):
    priority = {"family": 1, "opening_element": 2, "geometry_hole": 3}
    for i, existing in enumerate(openings):
        if rects_similar(existing, info):
            existing_kind = existing.get("kind")
            info_kind = info.get("kind")

            if info_kind == "family" and existing_kind != "family":
                # Keep the solid/opening cut rectangle, but remember that a
                # hosted family should be restored instead of a plain opening.
                merged = dict(existing)
                merged["kind"] = "family"
                merged["family_info"] = info.get("family_info")
                if "source_id" in info:
                    merged["source_id"] = info["source_id"]
                openings[i] = merged
            elif existing_kind == "family" and info_kind != "family":
                # Use the real cut rectangle and keep the hosted family data.
                merged = dict(info)
                if "family_info" in existing:
                    merged["family_info"] = existing["family_info"]
                    merged["kind"] = "family"
                if "source_id" in existing:
                    merged["source_id"] = existing["source_id"]
                openings[i] = merged
            elif priority.get(info_kind, 0) > priority.get(existing_kind, 0):
                openings[i] = dict(info)
            return
    openings.append(info)


def collect_dependent_opening_infos(wall, axis, bounds, openings):
    try:
        dep_ids = wall.GetDependentElements(None)
    except Exception:
        dep_ids = []

    for element_id in dep_ids:
        elem = doc.GetElement(element_id)
        if isinstance(elem, Opening):
            try:
                if not elem.IsRectBoundary:
                    raise Exception("opening no rectangular")
                rect_points = elem.BoundaryRect
                if rect_points is None or len(rect_points) < 2:
                    continue
                p0 = project_point(axis, rect_points[0])
                p1 = project_point(axis, rect_points[1])
                rect = normalize_rect(p0[0], p1[0], p0[1], p1[1])
                if rect is None:
                    continue
                rect = clamp_rect(rect, bounds)
                if rect is None:
                    continue
                rect["kind"] = "opening_element"
                rect["source_id"] = elem.Id
                add_opening_info(openings, rect)
            except Exception:
                raise Exception("contiene un opening no rectangular")

    for family_instance in get_hosted_family_instances(wall):
        cat_id = get_family_category_id(family_instance)
        if cat_id not in [DOOR_CATEGORY_ID, WINDOW_CATEGORY_ID]:
            continue
        family_rect = family_bbox_to_rect(axis, family_instance)
        if family_rect is None:
            continue
        family_rect = clamp_rect(family_rect, bounds)
        if family_rect is None:
            continue
        family_info = capture_family_info(axis, family_instance)
        if family_info is None:
            continue
        family_rect["kind"] = "family"
        family_rect["family_info"] = family_info
        family_rect["source_id"] = family_instance.Id
        add_opening_info(openings, family_rect)


def add_unique_element(elements, seen_ids, element):
    if element is None:
        return
    key = get_id_value(element.Id)
    if key is None:
        key = str(element.Id)
    if key in seen_ids:
        return
    elements.append(element)
    seen_ids.add(key)


def is_door_or_window(element):
    if not isinstance(element, FamilyInstance):
        return False
    cat_id = get_family_category_id(element)
    return cat_id in [DOOR_CATEGORY_ID, WINDOW_CATEGORY_ID]


def is_hosted_by_wall(element, wall):
    wall_id_value = get_id_value(wall.Id)
    try:
        host = element.Host
        if host is not None and get_id_value(host.Id) == wall_id_value:
            return True
    except Exception:
        pass

    try:
        param = element.get_Parameter(BuiltInParameter.HOST_ID_PARAM)
        if param is not None and get_id_value(param.AsElementId()) == wall_id_value:
            return True
    except Exception:
        pass

    return False


def get_wall_door_window_inserts(wall):
    inserts = []
    seen_ids = set()

    try:
        insert_ids = wall.FindInserts(True, True, True, True)
    except Exception:
        insert_ids = []

    for element_id in insert_ids:
        element = doc.GetElement(element_id)
        if is_door_or_window(element):
            add_unique_element(inserts, seen_ids, element)

    for element in get_hosted_family_instances(wall):
        if is_door_or_window(element):
            add_unique_element(inserts, seen_ids, element)

    for category in [BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows]:
        for element in FilteredElementCollector(doc).OfCategory(category).OfClass(FamilyInstance):
            if is_hosted_by_wall(element, wall):
                add_unique_element(inserts, seen_ids, element)

    return inserts


def get_location_curve_u_bounds(wall, axis):
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return None

    curve = loc.Curve
    p0 = project_point(axis, curve.GetEndPoint(0))
    p1 = project_point(axis, curve.GetEndPoint(1))
    return min(p0[0], p1[0]), max(p0[0], p1[0])


def build_wall_processing_rect(wall, axis, outer_bounds):
    source_base_z = get_wall_base_elevation(wall)
    source_top_z = get_wall_top_elevation(wall)
    if source_top_z <= source_base_z + MIN_FRAGMENT_DIM:
        source_top_z = source_base_z + max(get_wall_unconnected_height(wall), MIN_FRAGMENT_DIM)

    u_bounds = get_location_curve_u_bounds(wall, axis)
    if u_bounds is None:
        if outer_bounds is None:
            return None
        u0 = outer_bounds["u0"]
        u1 = outer_bounds["u1"]
    else:
        u0, u1 = u_bounds
        if outer_bounds is not None:
            # Use real solid ends when they are reasonably close to the
            # location curve, but ignore large join artifacts.
            loc_width = max(MIN_FRAGMENT_DIM, u1 - u0)
            if abs(outer_bounds["u0"] - u0) < loc_width * 0.15:
                u0 = outer_bounds["u0"]
            if abs(outer_bounds["u1"] - u1) < loc_width * 0.15:
                u1 = outer_bounds["u1"]

    return normalize_rect(u0, u1, source_base_z, source_top_z)


def get_rects_from_inner_loops(loops, outer_index, wall_rect):
    rects = []
    for i, loop in enumerate(loops):
        if i == outer_index:
            continue
        rect = loop_to_rect(loop)
        if rect is None:
            continue
        rect = clamp_rect(rect, wall_rect)
        if rect is not None:
            rects.append(rect)
    return rects


def get_insert_location_uz(axis, insert):
    loc = insert.Location
    if isinstance(loc, LocationPoint):
        return project_point(axis, loc.Point)
    bbox = insert.get_BoundingBox(None)
    if bbox is None:
        return None
    center = XYZ(
        (bbox.Min.X + bbox.Max.X) * 0.5,
        (bbox.Min.Y + bbox.Max.Y) * 0.5,
        (bbox.Min.Z + bbox.Max.Z) * 0.5
    )
    return project_point(axis, center)


def get_hole_match_score(hole_rect, insert_rect, insert_point):
    score = 0.0
    if insert_rect is not None:
        overlap = rect_intersection_area(hole_rect, insert_rect)
        if overlap > MIN_FRAGMENT_AREA:
            score += overlap * 100.0

    if insert_point is not None and point_in_rect(insert_point, hole_rect):
        score += rect_area(hole_rect) * 10.0

    if insert_rect is not None:
        hc = rect_center(hole_rect)
        ic = rect_center(insert_rect)
        du = abs(hc[0] - ic[0])
        dz = abs(hc[1] - ic[1])
        score += 1.0 / (1.0 + du + dz)

    return score


def find_matching_hole_for_insert(hole_rects, insert_rect, insert_point):
    best_rect = None
    best_score = 0.0

    for hole_rect in hole_rects:
        score = get_hole_match_score(hole_rect, insert_rect, insert_point)
        if score > best_score:
            best_score = score
            best_rect = hole_rect

    if best_score <= 0.0:
        return None
    return best_rect


def normalize_insert_rect_for_category(insert, rect, wall_rect):
    rect = clamp_rect(rect, wall_rect)
    if rect is None:
        return None

    cat_id = get_family_category_id(insert)
    if cat_id == DOOR_CATEGORY_ID:
        # Door cuts normally start at wall base. Family bounding boxes can miss
        # the threshold, so force the split to the wall base for doors.
        rect = normalize_rect(rect["u0"], rect["u1"], wall_rect["z0"], rect["z1"])
        if rect is None:
            return None

    if rect_width(rect) >= rect_width(wall_rect) - MIN_FRAGMENT_DIM:
        return None
    if rect_height(rect) >= rect_height(wall_rect) - MIN_FRAGMENT_DIM:
        return None
    return rect


def get_valid_insert_opening_rect(axis, insert, hole_rects, wall_rect):
    insert_rect = family_bbox_to_rect(axis, insert)
    if insert_rect is not None:
        insert_rect = clamp_rect(insert_rect, wall_rect)

    insert_point = get_insert_location_uz(axis, insert)
    matched_hole = find_matching_hole_for_insert(hole_rects, insert_rect, insert_point)
    if matched_hole is not None:
        return normalize_insert_rect_for_category(insert, matched_hole, wall_rect)

    if insert_rect is not None:
        return normalize_insert_rect_for_category(insert, insert_rect, wall_rect)

    return None


def collect_valid_insert_openings(wall, axis, wall_rect, hole_rects):
    openings = []
    inserts = get_wall_door_window_inserts(wall)

    for insert in inserts:
        rect = get_valid_insert_opening_rect(axis, insert, hole_rects, wall_rect)
        if rect is None:
            continue
        rect["kind"] = "insert"
        rect["source_id"] = insert.Id
        rect["insert_name"] = get_element_name(insert.Symbol) or get_element_name(insert)
        add_opening_info(openings, rect)

    openings.sort(key=lambda r: (r["u0"], r["z0"], r["u1"], r["z1"]))
    return openings


def rect_center_in_any_opening(rect, openings):
    center = rect_center(rect)
    for opening in openings:
        if point_in_rect(center, opening):
            return True
    return False


def partition_wall_rect_by_openings(wall_rect, opening_rects):
    xs = [wall_rect["u0"], wall_rect["u1"]]
    zs = [wall_rect["z0"], wall_rect["z1"]]

    for opening in opening_rects:
        xs.extend([opening["u0"], opening["u1"]])
        zs.extend([opening["z0"], opening["z1"]])

    xs = unique_sorted(xs)
    zs = unique_sorted(zs)
    nx = len(xs) - 1
    ny = len(zs) - 1

    if nx <= 0 or ny <= 0:
        raise Exception("contorno sin area util")
    if nx * ny > MAX_GRID_CELLS:
        raise Exception("demasiadas divisiones alrededor de puertas/ventanas")

    active_cells = set()
    for i in range(nx):
        if xs[i + 1] - xs[i] < MIN_FRAGMENT_DIM:
            continue
        for j in range(ny):
            if zs[j + 1] - zs[j] < MIN_FRAGMENT_DIM:
                continue
            cell_rect = rectangle_from_indices(xs, zs, i, i + 1, j, j + 1)
            if cell_rect is None:
                continue
            if rect_center_in_any_opening(cell_rect, opening_rects):
                continue
            active_cells.add((i, j))

    rectangles = []
    while active_cells:
        rect_index = find_largest_cell_rectangle(active_cells, xs, zs)
        if rect_index is None:
            break
        rect = rectangle_from_indices(xs, zs, rect_index[0], rect_index[1], rect_index[2], rect_index[3])
        remove_cell_rectangle(active_cells, rect_index)
        if rect is not None:
            rectangles.append(rect)

    if not rectangles:
        raise Exception("no se pudo generar ningun rectangulo alrededor de inserts")

    rectangles = merge_rectangles(rectangles)
    rectangles.sort(key=lambda r: (r["u0"], r["z0"], -rect_area(r)))
    return rectangles


def analyze_wall(wall):
    if not is_wall_element(wall):
        return {"status": "omitted", "reason": "no es un muro"}

    try:
        if is_valid_id(wall.GroupId):
            return {"status": "omitted", "reason": "pertenece a un grupo"}
    except Exception:
        pass

    try:
        if wall.WallType.Kind != WallKind.Basic:
            return {"status": "omitted", "reason": "no es Basic Wall"}
    except Exception:
        return {"status": "omitted", "reason": "tipo de muro no compatible"}

    try:
        if wall.IsStackedWall:
            return {"status": "omitted", "reason": "es un muro apilado"}
    except Exception:
        pass

    try:
        axis = get_wall_axis(wall)
        face = get_primary_side_face(wall, axis)
        loops = extract_face_loops(face, axis)

        areas = [abs(polygon_area(loop)) for loop in loops]
        outer_index = areas.index(max(areas))
        outer_bounds = loop_bounds(loops[outer_index])
        wall_rect = build_wall_processing_rect(wall, axis, outer_bounds)
        if wall_rect is None:
            raise Exception("no se pudo determinar el rectangulo base del muro")

        hole_rects = get_rects_from_inner_loops(loops, outer_index, wall_rect)
        openings = collect_valid_insert_openings(wall, axis, wall_rect, hole_rects)

        if not openings:
            return {
                "status": "regular",
                "wall": wall,
                "reason": "no tiene puertas o ventanas validas para dividir"
            }

        rectangles = partition_wall_rect_by_openings(wall_rect, openings)

        if len(rectangles) <= 1:
            return {
                "status": "regular",
                "wall": wall,
                "reason": "no requiere subdivision rectangular"
            }

        return {
            "status": "ready",
            "wall": wall,
            "axis": axis,
            "rectangles": rectangles,
            "openings": openings,
            "bounds": wall_rect,
        }
    except Exception as ex:
        return {"status": "omitted", "reason": str(ex)}


def get_param_id(param):
    try:
        return get_id_value(param.Id)
    except Exception:
        pass
    try:
        return int(param.Definition.BuiltInParameter)
    except Exception:
        return None


def get_matching_parameter(target, source_param):
    try:
        return target.get_Parameter(source_param.Definition)
    except Exception:
        pass
    try:
        return target.LookupParameter(source_param.Definition.Name)
    except Exception:
        return None


def copy_parameter_value(source_param, target_param):
    if target_param is None or target_param.IsReadOnly:
        return False

    if source_param.StorageType != target_param.StorageType:
        return False

    try:
        if source_param.StorageType == StorageType.Double:
            target_param.Set(source_param.AsDouble())
        elif source_param.StorageType == StorageType.Integer:
            target_param.Set(source_param.AsInteger())
        elif source_param.StorageType == StorageType.String:
            value = source_param.AsString()
            target_param.Set(value if value is not None else "")
        elif source_param.StorageType == StorageType.ElementId:
            target_param.Set(source_param.AsElementId())
        else:
            return False
        return True
    except Exception:
        return False


def copy_instance_parameters(source, target):
    for source_param in source.Parameters:
        param_id = get_param_id(source_param)
        if param_id in SKIP_COPY_PARAM_IDS:
            continue
        try:
            if source_param.IsReadOnly:
                continue
        except Exception:
            pass
        target_param = get_matching_parameter(target, source_param)
        copy_parameter_value(source_param, target_param)


def set_double_parameter(element, builtin_parameter, value):
    try:
        param = element.get_Parameter(builtin_parameter)
    except Exception:
        param = None
    if param is None or param.IsReadOnly:
        return False
    try:
        param.Set(value)
        return True
    except Exception:
        return False


def set_integer_parameter(element, builtin_parameter, value):
    try:
        param = element.get_Parameter(builtin_parameter)
    except Exception:
        param = None
    if param is None or param.IsReadOnly:
        return False
    try:
        param.Set(value)
        return True
    except Exception:
        return False


def set_element_id_parameter(element, builtin_parameter, value):
    try:
        param = element.get_Parameter(builtin_parameter)
    except Exception:
        param = None
    if param is None or param.IsReadOnly:
        return False
    try:
        param.Set(value)
        return True
    except Exception:
        return False


def set_named_double_parameter(element, builtin_parameter_name, value):
    builtin_parameter = get_bip(builtin_parameter_name)
    if builtin_parameter is None:
        return False
    return set_double_parameter(element, builtin_parameter, value)


def set_named_integer_parameter(element, builtin_parameter_name, value):
    builtin_parameter = get_bip(builtin_parameter_name)
    if builtin_parameter is None:
        return False
    return set_integer_parameter(element, builtin_parameter, value)


def reapply_wall_vertical_extent(wall, base_offset, height):
    # Revit can inherit/copy constraints after creation. Reapply these last so
    # every fragment keeps the Z values measured from the source solid.
    try:
        set_element_id_parameter(
            wall,
            BuiltInParameter.WALL_HEIGHT_TYPE,
            ElementId.InvalidElementId
        )
    except Exception:
        pass
    set_double_parameter(wall, BuiltInParameter.WALL_BASE_OFFSET, base_offset)
    set_double_parameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM, height)
    for param_name in [
        "WALL_TOP_IS_ATTACHED",
        "WALL_BOTTOM_IS_ATTACHED",
    ]:
        set_named_integer_parameter(wall, param_name, 0)
    for param_name in [
        "WALL_TOP_EXTENSION_DIST_PARAM",
        "WALL_BASE_EXTENSION_DIST_PARAM",
        "WALL_BOTTOM_EXTENSION_DIST_PARAM",
    ]:
        set_named_double_parameter(wall, param_name, 0.0)


def get_element_bbox_z(element):
    bbox = element.get_BoundingBox(None)
    if bbox is None:
        return None
    return bbox.Min.Z, bbox.Max.Z


def align_fragments_to_source_profile(fragments, level_elevation):
    # First force the intended unconnected constraints, then verify the real
    # geometry Revit produced and correct any residual Z drift.
    for fragment in fragments:
        rect = fragment["rect"]
        reapply_wall_vertical_extent(
            fragment["wall"],
            rect["z0"] - level_elevation,
            rect_height(rect)
        )

    doc.Regenerate()

    for fragment in fragments:
        wall = fragment["wall"]
        rect = fragment["rect"]
        bbox_z = get_element_bbox_z(wall)
        if bbox_z is None:
            continue

        current_min_z, current_max_z = bbox_z
        target_min_z = rect["z0"]
        target_height = rect_height(rect)
        current_height = current_max_z - current_min_z

        if abs(current_height - target_height) > SNAP_TOL:
            reapply_wall_vertical_extent(
                wall,
                target_min_z - level_elevation,
                target_height
            )

    doc.Regenerate()

    for fragment in fragments:
        wall = fragment["wall"]
        rect = fragment["rect"]
        bbox_z = get_element_bbox_z(wall)
        if bbox_z is None:
            continue

        current_min_z = bbox_z[0]
        target_min_z = rect["z0"]
        dz = target_min_z - current_min_z
        if abs(dz) > SNAP_TOL:
            ElementTransformUtils.MoveElement(doc, wall.Id, XYZ(0.0, 0.0, dz))

    doc.Regenerate()


def get_wall_structural_flag(wall):
    try:
        return wall.StructuralUsage != StructuralWallUsage.NonBearing
    except Exception:
        pass
    try:
        param = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)
        return param is not None and param.AsInteger() == 1
    except Exception:
        return False


def ensure_wall_orientation(new_wall, source_wall, axis):
    loc = new_wall.Location
    if isinstance(loc, LocationCurve):
        try:
            new_curve = loc.Curve
            direction = normalize(new_curve.GetEndPoint(1).Subtract(new_curve.GetEndPoint(0)))
            if direction is not None and dot(direction, axis["direction"]) < 0.0:
                loc.Curve = Line.CreateBound(new_curve.GetEndPoint(1), new_curve.GetEndPoint(0))
        except Exception:
            pass

    try:
        new_normal = new_wall.Orientation.Normalize()
        source_normal = source_wall.Orientation.Normalize()
        if dot(new_normal, source_normal) < 0.0:
            new_wall.Flip()
    except Exception:
        pass


def create_wall_fragment(source_wall, rect, axis, level_elevation):
    start = point_on_axis_at_z(axis, rect["u0"], level_elevation)
    end = point_on_axis_at_z(axis, rect["u1"], level_elevation)
    line = Line.CreateBound(start, end)
    height = rect_height(rect)
    base_offset = rect["z0"] - level_elevation
    flip = False
    try:
        flip = source_wall.Flipped
    except Exception:
        pass

    new_wall = Wall.Create(
        doc,
        line,
        source_wall.WallType.Id,
        source_wall.LevelId,
        height,
        base_offset,
        flip,
        get_wall_structural_flag(source_wall)
    )

    ensure_wall_orientation(new_wall, source_wall, axis)
    copy_instance_parameters(source_wall, new_wall)
    reapply_wall_vertical_extent(new_wall, base_offset, height)

    try:
        new_wall.Pinned = source_wall.Pinned
    except Exception:
        pass

    return new_wall


def build_id_list(ids):
    result = List[ElementId]()
    for element_id in ids:
        result.Add(element_id)
    return result


def find_fragment_for_opening(fragments, opening):
    candidates = []
    for fragment in fragments:
        if rect_contains_rect(fragment["rect"], opening):
            candidates.append(fragment)

    if not candidates:
        return None

    candidates.sort(key=lambda f: rect_area(f["rect"]))
    return candidates[0]


def opening_too_large_for_fragment(fragment_rect, opening):
    return (
        rect_width(opening) >= rect_width(fragment_rect) - MIN_FRAGMENT_DIM
        and rect_height(opening) >= rect_height(fragment_rect) - MIN_FRAGMENT_DIM
    )


def create_rect_opening(target_wall, opening, axis):
    p0 = point_from_uz(axis, opening["u0"], opening["z0"])
    p1 = point_from_uz(axis, opening["u1"], opening["z1"])
    return doc.Create.NewOpening(target_wall, p0, p1)


def restore_family_instance(target_wall, opening):
    info = opening.get("family_info")
    if info is None:
        raise Exception("sin informacion de familia")

    symbol = info["symbol"]
    if not symbol.IsActive:
        symbol.Activate()
        doc.Regenerate()

    level = None
    if is_valid_id(info["level_id"]):
        level = doc.GetElement(info["level_id"])

    try:
        new_instance = doc.Create.NewFamilyInstance(
            info["point"],
            symbol,
            target_wall,
            level,
            StructuralType.NonStructural
        )
    except Exception:
        new_instance = doc.Create.NewFamilyInstance(
            info["point"],
            symbol,
            target_wall,
            StructuralType.NonStructural
        )

    try:
        if info["hand_flipped"] != new_instance.HandFlipped:
            new_instance.flipHand()
    except Exception:
        pass

    try:
        if info["facing_flipped"] != new_instance.FacingFlipped:
            new_instance.flipFacing()
    except Exception:
        pass

    try:
        loc = new_instance.Location
        if isinstance(loc, LocationPoint):
            delta = info["rotation"] - loc.Rotation
            if abs(delta) > SNAP_TOL:
                axis_line = Line.CreateBound(
                    info["point"],
                    info["point"].Add(XYZ.BasisZ)
                )
                ElementTransformUtils.RotateElement(doc, new_instance.Id, axis_line, delta)
    except Exception:
        pass

    return new_instance


def apply_openings_to_fragments(fragments, openings, axis):
    restored = 0

    for opening in openings:
        fragment = find_fragment_for_opening(fragments, opening)
        if fragment is None:
            raise Exception("un hueco no cae dentro de ningun fragmento rectangular")

        if opening_too_large_for_fragment(fragment["rect"], opening):
            raise Exception("un hueco ocupa completamente su fragmento anfitrion")

        if opening["kind"] == "family":
            restore_family_instance(fragment["wall"], opening)
            restored += 1
        else:
            create_rect_opening(fragment["wall"], opening, axis)
            restored += 1

    return restored


def try_join_walls(walls):
    for i, wall_a in enumerate(walls):
        for wall_b in walls[i + 1:]:
            try:
                if not JoinGeometryUtils.AreElementsJoined(doc, wall_a, wall_b):
                    JoinGeometryUtils.JoinGeometry(doc, wall_a, wall_b)
            except Exception:
                pass


def process_wall_analysis(analysis):
    wall = analysis["wall"]
    axis = analysis["axis"]
    rectangles = analysis["rectangles"]
    openings = analysis["openings"]
    level_elevation = get_level_elevation(wall)

    sub = SubTransaction(doc)
    sub.Start()

    try:
        fragments = []
        skipped_tiny = 0
        failed_fragments = 0

        for rect in rectangles:
            if rect_width(rect) < MIN_FRAGMENT_DIM or rect_height(rect) < MIN_FRAGMENT_DIM:
                skipped_tiny += 1
                continue
            try:
                new_wall = create_wall_fragment(wall, rect, axis, level_elevation)
                fragments.append({"wall": new_wall, "rect": rect})
            except Exception as fragment_ex:
                failed_fragments += 1
                print("No se pudo crear fragmento de muro {}: {}".format(
                    wall.Id,
                    fragment_ex
                ))

        if not fragments:
            raise Exception("no se pudo crear ningun fragmento valido")

        doc.Regenerate()
        align_fragments_to_source_profile(fragments, level_elevation)
        doc.Regenerate()

        try_join_walls([f["wall"] for f in fragments])

        try:
            if wall.Pinned:
                wall.Pinned = False
        except Exception:
            pass

        doc.Delete(wall.Id)
        sub.Commit()

        new_ids = [f["wall"].Id for f in fragments]
        return {
            "ok": True,
            "new_ids": new_ids,
            "created": len(new_ids),
            "splits": max(0, len(new_ids) - 1),
            "openings": len(openings),
            "skipped_tiny": skipped_tiny,
            "failed_fragments": failed_fragments,
        }
    except Exception as ex:
        if sub.HasStarted():
            sub.RollBack()
        return {
            "ok": False,
            "reason": str(ex),
            "new_ids": [],
            "created": 0,
            "splits": 0,
            "openings": 0,
            "skipped_tiny": 0,
            "failed_fragments": 0,
        }


# ---- the host contract: plan / apply / verify ------------------------------

def _bind(document):
    """Point the module-level `doc` at the document the HOST resolved. See the
    header for why the geometry above keeps using a global."""
    global doc
    doc = document


def _analyze(document, args):
    """Run the button's own analysis over the resolved scope. analyze_wall() was
    already a plan: it answers "ready" with the rectangles it would build,
    "regular" for a wall that needs nothing, or a refusal with its reason. The
    button computed all three and threw them away behind a Yes/No box."""
    _bind(document)

    scope = hz.resolve(document, args, is_wall_element, of_class=Wall)

    ready = []
    regular = []
    refused = []

    for wall in scope.elements:
        try:
            result = analyze_wall(wall)
        except Exception as exc:
            refused.append({"id": get_id_value(wall.Id), "label": element_label(wall),
                            "reason": "the analysis itself failed: " + hz.brief(exc, 200)})
            continue

        status = result.get("status")
        if status == "ready":
            ready.append(result)
        elif status == "regular":
            regular.append({"id": get_id_value(wall.Id), "label": element_label(wall)})
        else:
            refused.append({"id": get_id_value(wall.Id), "label": element_label(wall),
                            "reason": result.get("reason", "no compatible")})

    return scope, ready, regular, refused


def plan(document, args):
    scope, ready, regular, refused = _analyze(document, args)

    eligible = []
    for analysis in ready:
        wall = analysis["wall"]
        eligible.append({
            "id": get_id_value(wall.Id),
            "label": element_label(wall),
            "would_create_fragments": len(analysis["rectangles"]),
        })

    return {
        "scope": scope.report(),
        "eligible": eligible,
        "already_rectangular": regular,
        "refused": refused,
        "would_replace": len(eligible),
        "would_create_fragments": sum(e["would_create_fragments"] for e in eligible),
        "note": ("Only straight Basic Walls are touched. A curved wall, a non-rectangular opening, or "
                 "any profile that cannot be rebuilt stably is REFUSED by name with its reason rather "
                 "than approximated - see 'refused'. Walls already rectangular are listed separately "
                 "in 'already_rectangular': nothing is done to them and that is not a failure. Each "
                 "wall is rebuilt in its own SubTransaction, so one that fails rolls back alone."),
    }


def apply(document, args, plan):
    scope, ready, regular, refused = _analyze(document, args)

    if not ready:
        raise Exception(
            "No wall is eligible at apply time. {0} were already rectangular and {1} were refused; "
            "nothing was written.".format(len(regular), len(refused)))

    processed = 0
    created = 0
    splits = 0
    openings = 0
    skipped_tiny = 0
    failed_fragments = 0
    new_ids = []
    errors = []

    for analysis in ready:
        wall = analysis["wall"]
        # process_wall_analysis already runs in its own SubTransaction and returns
        # ok/reason rather than raising, so a wall that defeats it rolls back alone
        # and the batch continues. That was the button's design; it is kept.
        result = process_wall_analysis(analysis)
        if result["ok"]:
            processed += 1
            created += result["created"]
            splits += result["splits"]
            openings += result["openings"]
            skipped_tiny += result["skipped_tiny"]
            failed_fragments += result.get("failed_fragments", 0)
            new_ids.extend(result["new_ids"])
        else:
            errors.append({"id": get_id_value(wall.Id), "label": element_label(wall),
                           "reason": result["reason"]})

    return {
        "walls_processed": processed,
        "fragments_created": created,
        "created_ids": [get_id_value(i) for i in new_ids],
        "splits": splits,
        "openings_used": openings,
        "fragments_skipped_tiny": skipped_tiny,
        "fragments_failed": failed_fragments,
        "already_rectangular": len(regular),
        "refused": refused,
        "errors": errors,
    }


def verify(document, args, plan, applied):
    """After the commit, ask the MODEL: are the fragments really there as walls.
    process_wall_analysis counted what it built inside a SubTransaction, and a
    SubTransaction that committed still dies with the outer one."""
    _bind(document)

    present = 0
    for value in applied["created_ids"]:
        if isinstance(document.GetElement(hz.to_eid(value)), Wall):
            present += 1

    return {
        "fragments_present": present,
        "intended_fragments": applied["fragments_created"],
    }
