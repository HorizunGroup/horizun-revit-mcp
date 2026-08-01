# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# SPLIT A COMPOUND WALL INTO ONE WALL PER MATERIAL LAYER.
#
# Ported from the "Partir Muro Multicapa" pyRevit button. The geometry is that
# button's, which is the point: it has been run against real models and its edge
# cases were found there, not reasoned about here. Each layer becomes its own wall
# at its own offset, doors and windows are re-hosted on the structural layer, and
# the finish walls are joined to it so Revit cuts their openings for them.
#
# WHAT WAS FIXED IN THE PORT, and why each was a real defect rather than taste:
#
#   1. CURVED WALLS WERE SILENTLY STRAIGHTENED. offset_curve() built every layer
#      with Line.CreateBound(p0, p1) from the original curve's ENDPOINTS. On a
#      straight wall that is exact. On an arc wall it is the chord - the wall
#      moves, and nothing said so. Interactively you see it; from an agent you get
#      a committed, verified, wrong building. Curved walls are now REFUSED in
#      plan() with the reason, and the arithmetic that assumed a line is never
#      reached. Restoring them properly means offsetting arcs by radius, which is
#      a different algorithm and not one this port is entitled to invent.
#
#   2. THE ORIGINALS WERE DELETED WHILE PINNED. run() called doc.Delete on walls
#      that could be pinned; Revit answers with "you are trying to delete pinned
#      elements", and a warning nobody dismisses is a modal that holds the UI
#      thread until the caller times out. Its own sibling button ("Separar losas")
#      already unpinned first. Now this one does too.
#
#   3. THE ORIGIN PARAMETER WAS COMPILED IN. PARAM_NAME = "_GrupoOrigen" is one
#      organisation's convention. Horizun compiles none in: it arrives as
#      origin_group_param, and when it is omitted nothing is copied and the count
#      is reported as null - not tracked - rather than as 0.
#
#   4. doc WAS A MODULE GLOBAL taken from __revit__. Here it is the argument the
#      host passes, so the tool acts on the document the caller NAMED.
#
# No transaction in this file. The host owns the commit - see Recipe.cs.
# -----------------------------------------------------------------------------
from Autodesk.Revit.DB import (
    FilteredElementCollector, Wall, WallType, WallKind,
    ElementId, XYZ, Line,
    BuiltInParameter, CompoundStructure, CompoundStructureLayer,
    MaterialFunctionAssignment, FamilyInstance, LocationCurve,
    Opening, CurveArray, Transform,
    FailureSeverity, FailureProcessingResult, IFailuresPreprocessor,
    Element, JoinGeometryUtils
)
from Autodesk.Revit.DB.Structure import StructuralWallUsage, StructuralType
from System.Collections.Generic import List

import hz

FEET_TO_CM = 30.48


class WallOverlapPreprocessor(IFailuresPreprocessor):
    """Splitting a wall into layers produces overlapping walls BY CONSTRUCTION, so
    Revit's overlap warning is expected rather than informative. It is dismissed;
    everything else Revit raises is left alone and reaches the caller through the
    dispatcher's RevitSaid block."""

    def PreprocessFailures(self, failuresAccessor):
        for failure in failuresAccessor.GetFailureMessages():
            if failure.GetSeverity() == FailureSeverity.Warning:
                desc = failure.GetDescriptionText().lower()
                if "overlap" in desc and "wall" in desc:
                    failuresAccessor.DeleteWarning(failure)
        return FailureProcessingResult.Continue


def failure_preprocessor():
    """Handed to the host, which installs it on ITS transaction."""
    return WallOverlapPreprocessor()


# ---- naming ---------------------------------------------------------------

def get_element_name(element):
    try:
        return Element.Name.GetValue(element)
    except Exception:
        pass
    try:
        return element.Name
    except Exception:
        return ""


def sanitize_name(name):
    for ch in r'\/:*?"<>|{}':
        name = name.replace(ch, "_")
    return name.strip()


def build_layer_type_name(material_name, thickness_feet):
    cm = thickness_feet * FEET_TO_CM
    return u"{0}_{1:.1f}cm".format(sanitize_name(material_name), cm)


def is_valid_element_id(element_id):
    return element_id is not None and hz.eid(element_id) != hz.eid(ElementId.InvalidElementId)


# ---- the origin parameter, supplied rather than assumed --------------------

def get_text_parameter(element, param_name):
    if not param_name:
        return None
    param = element.LookupParameter(param_name)
    if param is None:
        return None
    try:
        value = param.AsString()
    except Exception:
        return None
    if value is None:
        return None
    value = value.strip()
    return value if value else None


def set_text_parameter(element, param_name, value):
    param = element.LookupParameter(param_name)
    if param is None:
        return False, "no existe"
    if param.IsReadOnly:
        return False, "solo lectura"
    if param.StorageType.ToString() != "String":
        return False, "no es texto"
    param.Set(value or "")
    return True, None


# ---- layers ---------------------------------------------------------------

def get_or_create_wall_type(doc, base_wt, type_name, thickness_feet, mat_id, func):
    for wt in FilteredElementCollector(doc).OfClass(WallType):
        try:
            wt_name = Element.Name.GetValue(wt)
        except Exception:
            try:
                wt_name = wt.Name
            except Exception:
                continue
        if wt_name == type_name:
            return wt
    new_type = base_wt.Duplicate(type_name)
    new_layer = CompoundStructureLayer(thickness_feet, func, mat_id)
    layer_list = List[CompoundStructureLayer]()
    layer_list.Add(new_layer)
    try:
        cs = CompoundStructure.CreateSimpleCompoundStructure(layer_list)
        new_type.SetCompoundStructure(cs)
    except Exception:
        pass
    return new_type


def get_wall_layers(doc, wall_type):
    cs = wall_type.GetCompoundStructure()
    if cs is None:
        return []

    result = []
    for layer in cs.GetLayers():
        t = layer.Width
        if t < 1e-6:
            continue
        mid = layer.MaterialId
        mat_name = "NoMaterial"
        if mid != ElementId.InvalidElementId:
            mat = doc.GetElement(mid)
            if mat is not None:
                n = get_element_name(mat)
                mat_name = n if n else "NoMaterial"
        result.append({
            "material_name": mat_name,
            "material_id": mid,
            "thickness_feet": t,
            "function": layer.Function,
        })
    return result


def get_wall_normal(wall):
    try:
        return wall.Orientation.Normalize()
    except Exception:
        pass
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return XYZ.BasisY
    d = loc.Curve.Direction.Normalize()
    normal = XYZ(-d.Y, d.X, 0)
    try:
        if wall.Flipped:
            normal = normal.Negate()
    except Exception:
        pass
    return normal


# ---- stacked walls --------------------------------------------------------

def is_stacked_wall(wall):
    try:
        if wall.IsStackedWall:
            return True
    except Exception:
        pass
    try:
        return wall.WallType.Kind == WallKind.Stacked
    except Exception:
        return False


def is_stacked_wall_member(wall):
    try:
        return bool(wall.IsStackedWallMember)
    except Exception:
        return False


def get_stacked_wall_owner(doc, wall):
    try:
        owner_id = wall.StackedWallOwnerId
    except Exception:
        return None
    if not is_valid_element_id(owner_id):
        return None
    owner = doc.GetElement(owner_id)
    return owner if isinstance(owner, Wall) else None


def get_stack_root_wall(doc, wall):
    if is_stacked_wall(wall):
        return wall
    if is_stacked_wall_member(wall):
        owner = get_stacked_wall_owner(doc, wall)
        if owner is not None:
            return owner
    return None


def get_stacked_member_walls(doc, stacked_wall):
    member_walls = []
    try:
        member_ids = stacked_wall.GetStackedWallMemberIds()
    except Exception as ex:
        print("Could not get stacked wall members {}: {}".format(stacked_wall.Id, ex))
        return member_walls
    for member_id in member_ids:
        member = doc.GetElement(member_id)
        if isinstance(member, Wall):
            member_walls.append(member)
    return member_walls


def wall_has_multiple_layers(doc, wall):
    try:
        if wall.WallType.Kind != WallKind.Basic:
            return False
    except Exception:
        return False
    return len(get_wall_layers(doc, wall.WallType)) > 1


def stacked_wall_has_multiple_layer_member(doc, stacked_wall):
    for member in get_stacked_member_walls(doc, stacked_wall):
        if wall_has_multiple_layers(doc, member):
            return True
    return False


# ---- the straight-line assumption, made explicit --------------------------

def is_straight(wall):
    """Every offset in this recipe is built with Line.CreateBound from the original
    curve's endpoints. That is exact for a line and WRONG for an arc - it returns
    the chord. So a wall whose location is not a Line is refused rather than
    quietly straightened."""
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return False
    return isinstance(loc.Curve, Line)


def curved_reason(wall):
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return "its location is not a curve, so there is no centreline to offset"
    return ("its centreline is a " + type(loc.Curve).__name__ + ", not a straight Line. Offsetting it "
            "the way this tool offsets a line would replace the arc with its chord and MOVE the wall. "
            "Refused rather than committed wrong.")


def get_curve_direction(curve):
    try:
        return curve.Direction.Normalize()
    except Exception:
        pass
    try:
        return curve.GetEndPoint(1).Subtract(curve.GetEndPoint(0)).Normalize()
    except Exception:
        return XYZ.BasisX


def dot_xyz(a, b):
    try:
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z
    except Exception:
        return 0.0


def reverse_curve(curve):
    return Line.CreateBound(curve.GetEndPoint(1), curve.GetEndPoint(0))


def ensure_wall_orientation(doc, wall, target_direction, target_normal):
    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return

    actual_curve = loc.Curve
    actual_direction = get_curve_direction(actual_curve)
    if dot_xyz(actual_direction, target_direction) < 0.0:
        try:
            loc.Curve = reverse_curve(actual_curve)
            doc.Regenerate()
        except Exception as ex:
            print("Could not reverse wall {}: {}".format(wall.Id, ex))

    actual_normal = get_wall_normal(wall)
    if dot_xyz(actual_normal, target_normal) < 0.0:
        try:
            wall.Flip()
            doc.Regenerate()
        except Exception as ex:
            print("Could not flip wall {}: {}".format(wall.Id, ex))


def offset_curve(curve, distance, direction):
    v = direction.Multiply(distance)
    p0 = curve.GetEndPoint(0).Add(v)
    p1 = curve.GetEndPoint(1).Add(v)
    return Line.CreateBound(p0, p1)


def offset_point(point, distance, direction):
    return point.Add(direction.Multiply(distance))


# ---- hosted elements and openings -----------------------------------------

def iter_revit_collection(collection):
    try:
        iterator = collection.ForwardIterator()
        iterator.Reset()
        while iterator.MoveNext():
            yield iterator.Current
        return
    except Exception:
        pass
    for item in collection:
        yield item


def clone_curve(curve):
    try:
        return curve.Clone()
    except Exception:
        pass
    try:
        return curve.CreateTransformed(Transform.Identity)
    except Exception:
        return curve


def curve_array_from_curves(curves, distance, direction):
    profile = CurveArray()
    transform = Transform.CreateTranslation(direction.Multiply(distance))
    for curve in curves:
        try:
            profile.Append(curve.CreateTransformed(transform))
        except Exception:
            profile.Append(clone_curve(curve))
    return profile


def get_hosted_elements(doc, wall):
    hosted = []
    for eid_ in wall.GetDependentElements(None):
        elem = doc.GetElement(eid_)
        if isinstance(elem, FamilyInstance):
            host = elem.Host
            if host is not None and host.Id == wall.Id:
                hosted.append(elem)
    return hosted


def add_opening_if_hosted(openings, seen_ids, wall, elem):
    if not isinstance(elem, Opening):
        return
    try:
        host = elem.Host
    except Exception:
        host = None
    if host is None or host.Id != wall.Id:
        return
    key = hz.eid(elem.Id)
    if key is None:
        key = str(elem.Id)
    if key in seen_ids:
        return
    openings.append(elem)
    seen_ids.add(key)


def get_wall_openings(doc, wall):
    openings = []
    seen_ids = set()
    for eid_ in wall.GetDependentElements(None):
        elem = doc.GetElement(eid_)
        add_opening_if_hosted(openings, seen_ids, wall, elem)
    for elem in FilteredElementCollector(doc).OfClass(Opening):
        add_opening_if_hosted(openings, seen_ids, wall, elem)
    return openings


def capture_hosted_info(hosted_elements):
    out = []
    for fi in hosted_elements:
        loc = fi.Location
        if loc is None:
            continue
        out.append({
            "symbol": fi.Symbol,
            "point": loc.Point,
            "level_id": fi.LevelId,
            "hand_flipped": fi.HandFlipped,
            "facing_flipped": fi.FacingFlipped,
        })
    return out


def capture_opening_info(openings):
    out = []
    for opening in openings:
        try:
            if opening.IsRectBoundary:
                rect = opening.BoundaryRect
                if rect is not None and len(rect) >= 2:
                    out.append({"is_rect": True, "p0": rect[0], "p1": rect[1]})
                    continue
        except Exception as ex:
            print("Could not read rectangular opening {}: {}".format(opening.Id, ex))

        try:
            curves = []
            for curve in iter_revit_collection(opening.BoundaryCurves):
                curves.append(clone_curve(curve))
            if curves:
                out.append({"is_rect": False, "curves": curves})
        except Exception as ex:
            print("Could not read non-rectangular opening {}: {}".format(opening.Id, ex))
    return out


def restore_hosted_element(doc, wall, info):
    sym = info["symbol"]
    if not sym.IsActive:
        sym.Activate()
        doc.Regenerate()
    level = doc.GetElement(info["level_id"])
    new_fi = doc.Create.NewFamilyInstance(
        info["point"], sym, wall, level, StructuralType.NonStructural
    )
    if info["hand_flipped"] != new_fi.HandFlipped:
        new_fi.flipHand()
    if info["facing_flipped"] != new_fi.FacingFlipped:
        new_fi.flipFacing()
    return new_fi


def restore_wall_opening(doc, wall, info, normal, offset_distance):
    if info["is_rect"]:
        p0 = offset_point(info["p0"], offset_distance, normal)
        p1 = offset_point(info["p1"], offset_distance, normal)
        return doc.Create.NewOpening(wall, p0, p1)
    profile = curve_array_from_curves(info["curves"], offset_distance, normal)
    try:
        return doc.Create.NewOpening(wall, profile)
    except Exception:
        return doc.Create.NewOpening(wall, profile, True)


def restore_wall_openings(doc, walls_with_offsets, opening_info, normal):
    if not opening_info:
        return
    for wall, offset_distance in walls_with_offsets:
        for info in opening_info:
            try:
                restore_wall_opening(doc, wall, info, normal, offset_distance)
            except Exception as ex:
                print("Could not re-create wall opening on wall {}: {}".format(wall.Id, ex))


def try_join(doc, wall_a, wall_b):
    try:
        if not JoinGeometryUtils.AreElementsJoined(doc, wall_a, wall_b):
            JoinGeometryUtils.JoinGeometry(doc, wall_a, wall_b)
    except Exception as ex:
        print("Join failed: {}".format(ex))


# ---- the split ------------------------------------------------------------

def copy_wall_as_independent(doc, wall, param_name, grupo_origen_override=None):
    wall_type = wall.WallType
    if wall_type.Kind != WallKind.Basic:
        return []

    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return []

    original_curve = loc.Curve
    original_direction = get_curve_direction(original_curve)
    target_normal = get_wall_normal(wall)

    level_id = wall.LevelId
    base_offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()
    wall_height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()
    flip = wall.Flipped
    is_structural = (wall.StructuralUsage != StructuralWallUsage.NonBearing)
    grupo_origen = get_text_parameter(wall, param_name)
    if not grupo_origen and grupo_origen_override:
        grupo_origen = grupo_origen_override

    hosted_info = capture_hosted_info(get_hosted_elements(doc, wall))
    opening_info = capture_opening_info(get_wall_openings(doc, wall))

    new_wall = Wall.Create(
        doc, original_curve, wall_type.Id, level_id,
        wall_height, base_offset, flip, is_structural
    )
    ensure_wall_orientation(doc, new_wall, original_direction, target_normal)

    if grupo_origen and param_name:
        ok, reason = set_text_parameter(new_wall, param_name, grupo_origen)
        if not ok:
            print("Could not copy {} to wall {}: {}".format(param_name, new_wall.Id, reason))

    doc.Regenerate()
    restore_wall_openings(doc, [(new_wall, 0.0)], opening_info, target_normal)
    doc.Regenerate()

    for h_info in hosted_info:
        try:
            restore_hosted_element(doc, new_wall, h_info)
        except Exception as ex:
            print("Could not re-place hosted element: {}".format(ex))

    try:
        new_wall.Pinned = wall.Pinned
    except Exception:
        pass

    return [new_wall.Id]


def split_wall_into_layers(doc, wall, param_name, grupo_origen_override=None):
    wall_type = wall.WallType
    if wall_type.Kind != WallKind.Basic:
        return []

    layers_info = get_wall_layers(doc, wall_type)
    if len(layers_info) <= 1:
        return []

    loc = wall.Location
    if not isinstance(loc, LocationCurve):
        return []
    original_curve = loc.Curve
    original_direction = get_curve_direction(original_curve)

    level_id = wall.LevelId
    base_offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()
    wall_height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()
    flip = wall.Flipped
    is_structural = (wall.StructuralUsage != StructuralWallUsage.NonBearing)
    grupo_origen = get_text_parameter(wall, param_name)
    if not grupo_origen and grupo_origen_override:
        grupo_origen = grupo_origen_override

    normal = get_wall_normal(wall)
    target_normal = normal
    total_thickness = sum(l["thickness_feet"] for l in layers_info)

    hosted_info = capture_hosted_info(get_hosted_elements(doc, wall))
    opening_info = capture_opening_info(get_wall_openings(doc, wall))

    struct_idx = 0
    for i, lyr in enumerate(layers_info):
        if lyr["function"] == MaterialFunctionAssignment.Structure:
            struct_idx = i
            break

    new_walls = []
    acc_offset = total_thickness / 2.0

    for i, lyr in enumerate(layers_info):
        t = lyr["thickness_feet"]
        ctr = acc_offset - t / 2.0
        acc_offset -= t

        layer_curve = offset_curve(original_curve, ctr, normal)
        type_name = build_layer_type_name(lyr["material_name"], t)
        layer_wt = get_or_create_wall_type(
            doc, wall_type, type_name, t, lyr["material_id"], lyr["function"]
        )

        new_wall = Wall.Create(
            doc, layer_curve, layer_wt.Id, level_id,
            wall_height, base_offset, flip, is_structural
        )
        ensure_wall_orientation(doc, new_wall, original_direction, target_normal)
        if grupo_origen and param_name:
            ok, reason = set_text_parameter(new_wall, param_name, grupo_origen)
            if not ok:
                print("Could not copy {} to wall {}: {}".format(param_name, new_wall.Id, reason))
        new_walls.append((new_wall, i, ctr))

    doc.Regenerate()
    restore_wall_openings(doc, [(nw, ctr) for nw, idx, ctr in new_walls], opening_info, normal)
    doc.Regenerate()

    struct_wall = None
    for nw, idx, ctr in new_walls:
        if idx == struct_idx:
            struct_wall = nw
            break

    if struct_wall is not None and hosted_info:
        for h_info in hosted_info:
            try:
                restore_hosted_element(doc, struct_wall, h_info)
            except Exception as ex:
                print("Could not re-place hosted element: {}".format(ex))

    doc.Regenerate()
    if struct_wall is not None:
        for nw, idx, ctr in new_walls:
            if idx != struct_idx:
                try_join(doc, struct_wall, nw)

    doc.Regenerate()
    for nw, idx, ctr in new_walls:
        ensure_wall_orientation(doc, nw, original_direction, target_normal)

    return [nw.Id for nw, idx, ctr in new_walls]


def split_stacked_wall_into_layers(doc, stacked_wall, param_name):
    member_walls = get_stacked_member_walls(doc, stacked_wall)
    if not member_walls:
        return []

    grupo_origen = get_text_parameter(stacked_wall, param_name)
    new_ids = []

    for member_wall in member_walls:
        if wall_has_multiple_layers(doc, member_wall):
            member_ids = split_wall_into_layers(doc, member_wall, param_name, grupo_origen)
        else:
            member_ids = copy_wall_as_independent(doc, member_wall, param_name, grupo_origen)
        for member_id in member_ids:
            new_ids.append(member_id)

    return new_ids


# ---- the host contract: plan / apply / verify ------------------------------

def _describe(doc, wall, kind, layers):
    name = None
    try:
        name = get_element_name(wall.WallType)
    except Exception:
        pass
    return {
        "id": hz.eid(wall.Id),
        "type_name": name,
        "kind": kind,
        "layers": layers,
        "pinned": bool(wall.Pinned),
    }


def plan(doc, args):
    param_name = hz.arg(args, "origin_group_param")
    scope = hz.resolve(doc, args, lambda e: isinstance(e, Wall), of_class=Wall)

    eligible = []
    skipped = []
    seen = set()

    for wall in scope.elements:
        stack_root = get_stack_root_wall(doc, wall)
        target = stack_root if stack_root is not None else wall

        key = hz.eid(target.Id)
        if key in seen:
            continue
        seen.add(key)

        if stack_root is not None:
            if not stacked_wall_has_multiple_layer_member(doc, target):
                skipped.append(dict(_describe(doc, target, "stacked", 0),
                                    reason="no member of this stacked wall has more than one layer"))
                continue
            members = get_stacked_member_walls(doc, target)
            curved = [m for m in members if not is_straight(m)]
            if curved:
                skipped.append(dict(_describe(doc, target, "stacked", len(members)),
                                    reason="a member is curved: " + curved_reason(curved[0])))
                continue
            eligible.append(dict(_describe(doc, target, "stacked", len(members)),
                                 would_create=None))
            continue

        if not wall_has_multiple_layers(doc, wall):
            skipped.append(dict(_describe(doc, wall, "basic", 1),
                                reason="a single material layer - nothing to split"))
            continue

        if not is_straight(wall):
            skipped.append(dict(_describe(doc, wall, "basic",
                                          len(get_wall_layers(doc, wall.WallType))),
                                reason=curved_reason(wall)))
            continue

        layers = len(get_wall_layers(doc, wall.WallType))
        eligible.append(dict(_describe(doc, wall, "basic", layers), would_create=layers))

    # A stacked wall's total is only knowable per member, so it is left null rather
    # than guessed; the caller sees which entries could not be counted in advance.
    countable = [e["would_create"] for e in eligible if e["would_create"] is not None]

    return {
        "scope": scope.report(),
        "origin_group_param": param_name,
        "eligible": eligible,
        "skipped": skipped,
        "would_delete": len(eligible),
        "would_create": sum(countable) if len(countable) == len(eligible) else None,
        "would_create_note": None if len(countable) == len(eligible) else
            "One or more targets are stacked walls, whose layer walls are only countable per "
            "member at apply time. The total is reported as null rather than guessed.",
        "note": ("Curved walls are refused, not straightened: every offset here is built as a "
                 "straight Line between endpoints, which on an arc is the chord."),
    }


def apply(doc, args, plan):
    param_name = hz.arg(args, "origin_group_param")
    created = []
    deleted = []
    errors = []

    for entry in plan["eligible"]:
        wall = doc.GetElement(hz.to_eid(entry["id"]))
        if wall is None:
            errors.append({"id": entry["id"], "error": "vanished between plan and apply"})
            continue

        try:
            if is_stacked_wall(wall):
                new_ids = split_stacked_wall_into_layers(doc, wall, param_name)
            else:
                new_ids = split_wall_into_layers(doc, wall, param_name)
        except Exception as exc:
            errors.append({"id": entry["id"], "error": hz.brief(exc)})
            continue

        if not new_ids:
            errors.append({"id": entry["id"], "error": "produced no layer walls; the original was kept"})
            continue

        created.extend([hz.eid(i) for i in new_ids])

        try:
            # Unpin first: deleting a pinned element raises a warning, and a warning
            # nobody answers is a modal that holds Revit's UI thread.
            if wall.Pinned:
                wall.Pinned = False
            doc.Delete(wall.Id)
            deleted.append(entry["id"])
        except Exception as exc:
            errors.append({"id": entry["id"],
                           "error": "layer walls were created but the original could not be deleted: " +
                                    hz.brief(exc)})

    return {
        "created_ids": created,
        "deleted_ids": deleted,
        "created": len(created),
        "deleted": len(deleted),
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    """After the commit, ask the MODEL."""
    present = 0
    for value in applied["created_ids"]:
        if isinstance(doc.GetElement(hz.to_eid(value)), Wall):
            present += 1

    gone = 0
    for value in applied["deleted_ids"]:
        if not hz.still_exists(doc, value):
            gone += 1

    return {
        "created_present": present,
        "deleted_gone": gone,
        "intended_created": applied["created"],
        "intended_deleted": applied["deleted"],
    }
