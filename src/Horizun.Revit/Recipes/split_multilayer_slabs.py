# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# SPLIT A COMPOUND FLOOR OR CEILING INTO ONE ELEMENT PER MATERIAL LAYER.
#
# Ported from the "Separar Losas" pyRevit button. The geometry is that button's.
# Unlike its wall sibling it is safe on curved edges: the profile is CLONED from
# the slab's sketch (or read off its face when Revit will not hand over a sketch)
# and each layer is then moved in Z, so nothing is rebuilt from endpoints and no
# arc is flattened.
#
# WHAT CHANGED IN THE PORT:
#
#   1. THE ORIGIN PARAMETER IS SUPPLIED, NOT COMPILED IN. PARAM_NAME =
#      "_GrupoOrigen" is one organisation's convention; Horizun compiles none in.
#      It arrives as origin_group_param, and when omitted nothing is copied and
#      the count is null - not tracked - rather than 0.
#
#   2. doc IS THE ARGUMENT THE HOST PASSES, not a module global read from
#      __revit__, so the tool acts on the document the caller NAMED.
#
#   3. ONE SLAB CAN NO LONGER TAKE THE BATCH DOWN. The original raised out of
#      restore_hosted_elements when a hosted family could not be put back, which
#      rolled the whole transaction back - correct (new slabs without their hosted
#      families is data loss) but expensive on a batch of fifty. Each slab now runs
#      in its own SubTransaction inside the host's transaction: a slab that fails
#      rolls back ALONE, leaving no orphan layer slabs behind, and is reported by
#      id. The host still owns the commit; a SubTransaction is not a Transaction.
#
#   4. The unpin-before-delete this button already did correctly is kept - it is
#      the thing its wall sibling was missing.
#
# No Transaction in this file. See Recipe.cs.
# -----------------------------------------------------------------------------
from Autodesk.Revit.DB import (
    FilteredElementCollector, Floor, FloorType, Ceiling, CeilingType,
    ElementId, XYZ, Line,
    BuiltInParameter, CompoundStructure, CompoundStructureLayer,
    MaterialFunctionAssignment, FamilyInstance, LocationPoint,
    CurveLoop, Transform, HostObjectUtils, EndCapCondition,
    FailureSeverity, FailureProcessingResult, IFailuresPreprocessor,
    Element, ElementTransformUtils, SubTransaction
)
from Autodesk.Revit.DB.Structure import StructuralType
from System.Collections.Generic import List

import hz

FEET_TO_CM = 30.48
TOL = 1e-7


class SlabOverlapPreprocessor(IFailuresPreprocessor):
    """Layer slabs occupy the same footprint by construction, so Revit's overlap
    warning is expected rather than informative. Everything else it raises is left
    alone and reaches the caller."""

    def PreprocessFailures(self, failuresAccessor):
        for failure in failuresAccessor.GetFailureMessages():
            if failure.GetSeverity() == FailureSeverity.Warning:
                desc = failure.GetDescriptionText().lower()
                if "overlap" in desc or "solap" in desc:
                    failuresAccessor.DeleteWarning(failure)
        return FailureProcessingResult.Continue


def failure_preprocessor():
    return SlabOverlapPreprocessor()


# ---- naming and parameters ------------------------------------------------

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


def is_valid_id(element_id):
    return element_id is not None and element_id != ElementId.InvalidElementId


def get_builtin_parameter(name):
    try:
        return getattr(BuiltInParameter, name)
    except Exception:
        return None


def get_parameter_by_names(element, names):
    for name in names:
        bip = get_builtin_parameter(name)
        if bip is None:
            continue
        try:
            param = element.get_Parameter(bip)
        except Exception:
            param = None
        if param is not None:
            return param
    return None


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


# ---- slab kinds and layers ------------------------------------------------

def is_slab(element):
    return isinstance(element, Floor) or isinstance(element, Ceiling)


def get_slab_type(doc, slab):
    return doc.GetElement(slab.GetTypeId())


def get_slab_type_class(slab):
    return FloorType if isinstance(slab, Floor) else CeilingType


def get_slab_kind_name(slab):
    return "piso" if isinstance(slab, Floor) else "cielo"


def get_slab_layers(doc, slab_type):
    cs = slab_type.GetCompoundStructure()
    if cs is None:
        return []

    result = []
    for layer in cs.GetLayers():
        t = layer.Width
        if t < TOL:
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


def get_or_create_slab_type(doc, base_type, type_class, type_name,
                            thickness_feet, mat_id, func):
    for slab_type in FilteredElementCollector(doc).OfClass(type_class):
        if get_element_name(slab_type) == type_name:
            return slab_type

    new_type = base_type.Duplicate(type_name)
    new_layer = CompoundStructureLayer(thickness_feet, func, mat_id)
    layer_list = List[CompoundStructureLayer]()
    layer_list.Add(new_layer)

    cs = base_type.GetCompoundStructure()
    if cs is None:
        cs = new_type.GetCompoundStructure()
    if cs is None:
        cs = CompoundStructure.CreateSimpleCompoundStructure(layer_list)
    else:
        cs.SetLayers(layer_list)

    try:
        cs.EndCap = EndCapCondition.NoEndCap
    except Exception:
        pass

    new_type.SetCompoundStructure(cs)
    return new_type


# ---- profiles -------------------------------------------------------------

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


def clone_curve_loop(curve_loop):
    new_loop = CurveLoop()
    for curve in iter_revit_collection(curve_loop):
        new_loop.Append(clone_curve(curve))
    return new_loop


def get_profile_from_sketch(doc, slab):
    try:
        sketch_id = slab.SketchId
    except Exception:
        sketch_id = ElementId.InvalidElementId

    if not is_valid_id(sketch_id):
        return None

    sketch = doc.GetElement(sketch_id)
    if sketch is None:
        return None

    loops = List[CurveLoop]()
    for curve_array in iter_revit_collection(sketch.Profile):
        loop = CurveLoop()
        count = 0
        for curve in iter_revit_collection(curve_array):
            loop.Append(clone_curve(curve))
            count += 1
        if count:
            loops.Add(loop)

    return loops if loops.Count else None


def get_profile_from_face(slab):
    try:
        if isinstance(slab, Floor):
            refs = HostObjectUtils.GetTopFaces(slab)
        else:
            refs = HostObjectUtils.GetBottomFaces(slab)
    except Exception:
        return None

    for ref in refs:
        try:
            face = slab.GetGeometryObjectFromReference(ref)
            raw_loops = face.GetEdgesAsCurveLoops()
        except Exception:
            continue

        loops = List[CurveLoop]()
        for raw_loop in iter_revit_collection(raw_loops):
            loops.Add(clone_curve_loop(raw_loop))
        if loops.Count:
            return loops

    return None


def get_profile_loops(doc, slab):
    loops = get_profile_from_sketch(doc, slab)
    if loops is not None:
        return loops

    loops = get_profile_from_face(slab)
    if loops is not None:
        return loops

    raise Exception("No se pudo leer el perfil del {0} con Id {1}.".format(
        get_slab_kind_name(slab), hz.eid(slab.Id)))


def can_read_profile(doc, slab):
    """Cheap enough to ask during plan(): a slab whose profile Revit will not hand
    over cannot be split, and saying so before the transaction is better than
    discovering it half-way through one."""
    try:
        return get_profile_loops(doc, slab) is not None
    except Exception:
        return False


# ---- level, offset, structure ---------------------------------------------

def get_level_id(slab):
    try:
        if is_valid_id(slab.LevelId):
            return slab.LevelId
    except Exception:
        pass

    param = get_parameter_by_names(
        slab, ["LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM"])
    if param is not None:
        try:
            level_id = param.AsElementId()
            if is_valid_id(level_id):
                return level_id
        except Exception:
            pass

    raise Exception("No se pudo obtener el nivel del {0} con Id {1}.".format(
        get_slab_kind_name(slab), hz.eid(slab.Id)))


def _offset_param_names(slab):
    if isinstance(slab, Floor):
        return ["FLOOR_HEIGHTABOVELEVEL_PARAM"]
    return ["CEILING_HEIGHTABOVELEVEL_PARAM", "CEILING_HEIGHT_OFFSET_PARAM"]


def get_height_offset(slab):
    param = get_parameter_by_names(slab, _offset_param_names(slab))
    if param is None:
        return 0.0
    try:
        return param.AsDouble()
    except Exception:
        return 0.0


def set_height_offset(slab, value):
    param = get_parameter_by_names(slab, _offset_param_names(slab))
    if param is None or param.IsReadOnly:
        return False
    try:
        param.Set(value)
        return True
    except Exception:
        return False


def get_floor_structural_value(floor):
    param = get_parameter_by_names(floor, ["FLOOR_PARAM_IS_STRUCTURAL"])
    if param is None:
        return None
    try:
        return param.AsInteger()
    except Exception:
        return None


def set_floor_structural_value(floor, value):
    if value is None:
        return
    param = get_parameter_by_names(floor, ["FLOOR_PARAM_IS_STRUCTURAL"])
    if param is None or param.IsReadOnly:
        return
    try:
        param.Set(value)
    except Exception:
        pass


def get_bbox_z(slab):
    bbox = slab.get_BoundingBox(None)
    if bbox is None:
        raise Exception("No se pudo leer el bounding box del elemento {}".format(hz.eid(slab.Id)))
    return bbox.Min.Z, bbox.Max.Z


def create_layer_slab(doc, source_slab, profile_loops, slab_type, level_id):
    if isinstance(source_slab, Floor):
        new_slab = Floor.Create(doc, profile_loops, slab_type.Id, level_id)
        set_floor_structural_value(new_slab, get_floor_structural_value(source_slab))
        return new_slab
    return Ceiling.Create(doc, profile_loops, slab_type.Id, level_id)


# ---- hosted elements ------------------------------------------------------

def get_hosted_elements(doc, slab):
    hosted = []
    for eid_ in slab.GetDependentElements(None):
        elem = doc.GetElement(eid_)
        if isinstance(elem, FamilyInstance):
            host = elem.Host
            if host is not None and host.Id == slab.Id:
                hosted.append(elem)
    return hosted


def capture_hosted_info(hosted_elements):
    out = []
    for fi in hosted_elements:
        loc = fi.Location
        if loc is None or not hasattr(loc, "Point"):
            continue

        rotation = 0.0
        try:
            if isinstance(loc, LocationPoint):
                rotation = loc.Rotation
        except Exception:
            pass

        out.append({
            "symbol": fi.Symbol,
            "point": loc.Point,
            "level_id": fi.LevelId,
            "hand_flipped": fi.HandFlipped,
            "facing_flipped": fi.FacingFlipped,
            "rotation": rotation,
            "name": get_element_name(fi.Symbol) or get_element_name(fi),
        })
    return out


def restore_hosted_element(doc, slab, info):
    sym = info["symbol"]
    if not sym.IsActive:
        sym.Activate()
        doc.Regenerate()

    level = None
    if is_valid_id(info["level_id"]):
        level = doc.GetElement(info["level_id"])

    try:
        new_fi = doc.Create.NewFamilyInstance(
            info["point"], sym, slab, level, StructuralType.NonStructural)
    except Exception:
        new_fi = doc.Create.NewFamilyInstance(
            info["point"], sym, slab, StructuralType.NonStructural)

    try:
        if info["hand_flipped"] != new_fi.HandFlipped:
            new_fi.flipHand()
    except Exception:
        pass

    try:
        if info["facing_flipped"] != new_fi.FacingFlipped:
            new_fi.flipFacing()
    except Exception:
        pass

    try:
        loc = new_fi.Location
        if isinstance(loc, LocationPoint):
            delta = info["rotation"] - loc.Rotation
            if abs(delta) > TOL:
                axis = Line.CreateBound(info["point"], info["point"].Add(XYZ.BasisZ))
                ElementTransformUtils.RotateElement(doc, new_fi.Id, axis, delta)
    except Exception:
        pass

    return new_fi


def get_structural_layer_index(layers):
    for i, layer in enumerate(layers):
        if layer["function"] == MaterialFunctionAssignment.Structure:
            return i
    return 0


def get_hosted_candidate_indices(source_slab, layers):
    struct_idx = get_structural_layer_index(layers)
    last_idx = len(layers) - 1

    if isinstance(source_slab, Floor):
        ordered = [0, struct_idx, last_idx]
    else:
        ordered = [last_idx, struct_idx, 0]

    result = []
    for idx in ordered:
        if idx not in result and 0 <= idx < len(layers):
            result.append(idx)
    return result


def restore_hosted_elements(doc, source_slab, layers, new_slabs_by_index, hosted_info):
    """Raises when a hosted family cannot be put back anywhere. That is deliberate:
    layer slabs without the families they hosted is silent data loss, and the caller
    would have no way to know. The SubTransaction in apply() means only THIS slab is
    rolled back."""
    if not hosted_info:
        return

    failures = []
    candidate_indices = get_hosted_candidate_indices(source_slab, layers)

    for h_info in hosted_info:
        restored = False
        last_error = None

        for idx in candidate_indices:
            slab = new_slabs_by_index.get(idx)
            if slab is None:
                continue
            try:
                restore_hosted_element(doc, slab, h_info)
                restored = True
                break
            except Exception as ex:
                last_error = ex

        if not restored:
            failures.append("{0}: {1}".format(h_info.get("name") or "familia hospedada", last_error))

    if failures:
        raise Exception("No se pudieron reubicar familias hospedadas:\n{0}".format(
            "\n".join(failures[:10])))


# ---- the split ------------------------------------------------------------

def split_slab_into_layers(doc, slab, param_name):
    slab_type = get_slab_type(doc, slab)
    layers_info = get_slab_layers(doc, slab_type)
    if len(layers_info) <= 1:
        return []

    profile_loops = get_profile_loops(doc, slab)
    level_id = get_level_id(slab)
    original_offset = get_height_offset(slab)
    original_pinned = slab.Pinned
    grupo_origen = get_text_parameter(slab, param_name)

    hosted_info = capture_hosted_info(get_hosted_elements(doc, slab))

    type_class = get_slab_type_class(slab)
    min_z, top_z = get_bbox_z(slab)

    new_slabs = []
    new_slabs_by_index = {}
    acc_offset = 0.0

    for i, lyr in enumerate(layers_info):
        t = lyr["thickness_feet"]
        target_center_z = top_z - acc_offset - (t / 2.0)
        acc_offset += t

        type_name = build_layer_type_name(lyr["material_name"], t)
        layer_type = get_or_create_slab_type(
            doc, slab_type, type_class, type_name, t, lyr["material_id"], lyr["function"])

        new_slab = create_layer_slab(doc, slab, profile_loops, layer_type, level_id)
        set_height_offset(new_slab, original_offset)

        if grupo_origen and param_name:
            ok, reason = set_text_parameter(new_slab, param_name, grupo_origen)
            if not ok:
                print("Could not copy {} to slab {}: {}".format(param_name, new_slab.Id, reason))

        new_slabs.append((new_slab, i, target_center_z))
        new_slabs_by_index[i] = new_slab

    doc.Regenerate()

    for new_slab, idx, target_center_z in new_slabs:
        new_min_z, new_max_z = get_bbox_z(new_slab)
        current_center_z = (new_min_z + new_max_z) / 2.0
        dz = target_center_z - current_center_z
        if abs(dz) > TOL:
            ElementTransformUtils.MoveElement(doc, new_slab.Id, XYZ(0, 0, dz))

    doc.Regenerate()
    restore_hosted_elements(doc, slab, layers_info, new_slabs_by_index, hosted_info)

    for new_slab, idx, target_center_z in new_slabs:
        try:
            new_slab.Pinned = original_pinned
        except Exception:
            pass

    return [new_slab.Id for new_slab, idx, target_center_z in new_slabs]


# ---- the host contract: plan / apply / verify ------------------------------

def _describe(doc, slab, layers):
    return {
        "id": hz.eid(slab.Id),
        "kind": get_slab_kind_name(slab),
        "type_name": get_element_name(get_slab_type(doc, slab)),
        "layers": layers,
        "pinned": bool(slab.Pinned),
    }


def plan(doc, args):
    param_name = hz.arg(args, "origin_group_param")
    scope = hz.resolve(doc, args, is_slab)

    eligible = []
    skipped = []

    for slab in scope.elements:
        try:
            layers = len(get_slab_layers(doc, get_slab_type(doc, slab)))
        except Exception as exc:
            skipped.append(dict(_describe(doc, slab, 0),
                                reason="its type's compound structure could not be read: " + hz.brief(exc)))
            continue

        if layers <= 1:
            skipped.append(dict(_describe(doc, slab, layers),
                                reason="a single material layer - nothing to split"))
            continue

        if not can_read_profile(doc, slab):
            skipped.append(dict(_describe(doc, slab, layers),
                                reason="Revit will not hand over a sketch or a usable face for this "
                                       "element, so there is no profile to build the layers from"))
            continue

        eligible.append(dict(_describe(doc, slab, layers), would_create=layers))

    return {
        "scope": scope.report(),
        "origin_group_param": param_name,
        "eligible": eligible,
        "skipped": skipped,
        "would_delete": len(eligible),
        "would_create": sum(e["would_create"] for e in eligible),
        "note": ("Each layer keeps the original profile - curves included - and is moved in Z, so "
                 "nothing is rebuilt from endpoints. A slab whose hosted families cannot be put "
                 "back rolls back ALONE and is reported; the rest of the batch still applies."),
    }


def apply(doc, args, plan):
    param_name = hz.arg(args, "origin_group_param")
    created = []
    deleted = []
    errors = []

    for entry in plan["eligible"]:
        slab = doc.GetElement(hz.to_eid(entry["id"]))
        if slab is None:
            errors.append({"id": entry["id"], "error": "vanished between plan and apply"})
            continue

        # One slab per SubTransaction: a failure here rolls back THIS slab only,
        # leaving no half-split geometry behind, and the batch continues. The host
        # still owns the outer transaction and the commit.
        sub = SubTransaction(doc)
        sub.Start()
        try:
            new_ids = split_slab_into_layers(doc, slab, param_name)
            if not new_ids:
                sub.RollBack()
                errors.append({"id": entry["id"], "error": "produced no layer elements; the original was kept"})
                continue

            if slab.Pinned:
                slab.Pinned = False
            doc.Delete(slab.Id)

            sub.Commit()
            created.extend([hz.eid(i) for i in new_ids])
            deleted.append(entry["id"])
        except Exception as exc:
            try:
                sub.RollBack()
            except Exception:
                pass
            errors.append({"id": entry["id"], "error": hz.brief(exc, 300)})

    return {
        "created_ids": created,
        "deleted_ids": deleted,
        "created": len(created),
        "deleted": len(deleted),
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    present = 0
    for value in applied["created_ids"]:
        if is_slab(doc.GetElement(hz.to_eid(value))):
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
