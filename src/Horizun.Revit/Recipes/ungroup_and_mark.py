# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# UNGROUP MODEL GROUPS AND REMEMBER WHERE EACH ELEMENT CAME FROM.
#
# Ported from the "Desagrupar y Marcar" pyRevit button. Ungrouping loses the one
# fact you need to ever put the group back: which elements belonged to it. So
# before the members scatter, each one is stamped with the group's name in a text
# parameter, and the matching tool (horizun_regroup_by_param) reads that stamp to
# rebuild the group later.
#
# WHAT CHANGED IN THE PORT:
#
#   1. THE PARAMETER IS SUPPLIED, NOT COMPILED IN. The button hard-coded one
#      organisation's "_GrupoOrigen". It is now origin_group_param and it is
#      REQUIRED - the entire purpose of this tool is to write it, so defaulting it
#      would be guessing at the caller's standard.
#
#   2. THE PARAMETER IS CHECKED BEFORE ANYTHING IS UNGROUPED. The button
#      ungrouped first and discovered per element that the parameter did not
#      exist, leaving the model ungrouped and UNMARKED - the worst of both, and
#      unrecoverable because the group membership was already gone. plan() now
#      samples the members first and refuses a group whose members cannot carry
#      the stamp, before a single group is taken apart.
#
#   3. THE DETAIL MARKER IS OPTIONAL AND NAMES ITS VIEW. The button drew a circle
#      and X/Y axes at each group's origin in whatever view happened to be active.
#      Drawing into "whatever is in front" is not something a tool called by an
#      agent should do, so markers are drawn only when marker_view_id is given,
#      and only into that view.
#
# No Transaction in this file. The host owns the commit - see Recipe.cs.
# -----------------------------------------------------------------------------
import math

from Autodesk.Revit.DB import (
    Group, ElementId, BuiltInCategory, XYZ, Arc, Line, TextNote, TextNoteType,
    ElementTypeGroup, FilteredElementCollector, StorageType, Element,
    BuiltInParameter, View
)

import hz

CM_POR_FOOT = 30.48
RADIO_CIRCULO = 25.0 / CM_POR_FOOT
LONGITUD_EJE = 60.0 / CM_POR_FOOT
OFFSET_TEXTO = 10.0 / CM_POR_FOOT

MODEL_GROUP_CATEGORY_ID = hz.eid(ElementId(BuiltInCategory.OST_IOSModelGroups))


# ---- naming ---------------------------------------------------------------

def element_name(element):
    try:
        return Element.Name.GetValue(element)
    except Exception:
        pass
    try:
        return element.Name
    except Exception:
        pass
    try:
        return element.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME).AsString()
    except Exception:
        pass
    return ""


def element_category(element):
    try:
        if element.Category:
            return element.Category.Name
    except Exception:
        pass
    return "<Sin categoria>"


def is_model_group(element):
    if not isinstance(element, Group):
        return False
    try:
        return element.Category and hz.eid(element.Category.Id) == MODEL_GROUP_CATEGORY_ID
    except Exception:
        return False


def group_name(group):
    if group.GroupType:
        return element_name(group.GroupType)
    return element_name(group)


# ---- the stamp ------------------------------------------------------------

def can_write_text(element, param_name):
    """Why this element could not carry the stamp, or None when it can. Asked
    BEFORE anything is ungrouped, which is the fix this port exists to make."""
    param = element.LookupParameter(param_name)
    if param is None:
        return "no existe"
    if param.IsReadOnly:
        return "solo lectura"
    if param.StorageType != StorageType.String:
        return "no es texto"
    return None


def write_text(element, param_name, value):
    reason = can_write_text(element, param_name)
    if reason is not None:
        return False, reason
    element.LookupParameter(param_name).Set(value or "")
    return True, None


# ---- the origin marker ----------------------------------------------------

def group_point(group):
    try:
        loc = group.Location
        if loc and hasattr(loc, "Point"):
            return loc.Point
    except Exception:
        pass
    return None


def group_rotation(group):
    try:
        loc = group.Location
        if loc and hasattr(loc, "Rotation"):
            return float(loc.Rotation)
    except Exception:
        pass
    return 0.0


def view_directions(view):
    try:
        derecha = view.RightDirection
    except Exception:
        derecha = XYZ.BasisX
    try:
        arriba = view.UpDirection
    except Exception:
        arriba = XYZ.BasisY
    return derecha.Normalize(), arriba.Normalize()


def rotate_in_plane(base_x, base_y, angle):
    cos_a = math.cos(angle)
    sin_a = math.sin(angle)
    x_rot = base_x.Multiply(cos_a).Add(base_y.Multiply(sin_a))
    y_rot = base_y.Multiply(cos_a).Subtract(base_x.Multiply(sin_a))
    return x_rot.Normalize(), y_rot.Normalize()


def text_note_type_id(doc):
    try:
        type_id = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)
        if type_id and type_id != ElementId.InvalidElementId:
            return type_id
    except Exception:
        pass
    try:
        for text_type in FilteredElementCollector(doc).OfClass(TextNoteType):
            return text_type.Id
    except Exception:
        pass
    return None


def draw_origin_marker(doc, view, group):
    centro = group_point(group)
    if centro is None:
        return False, "sin punto de origen"

    try:
        derecha, arriba = view_directions(view)
        p_derecha = centro.Add(derecha.Multiply(RADIO_CIRCULO))
        p_izquierda = centro.Subtract(derecha.Multiply(RADIO_CIRCULO))
        p_arriba = centro.Add(arriba.Multiply(RADIO_CIRCULO))
        p_abajo = centro.Subtract(arriba.Multiply(RADIO_CIRCULO))

        doc.Create.NewDetailCurve(view, Arc.Create(p_derecha, p_izquierda, p_arriba))
        doc.Create.NewDetailCurve(view, Arc.Create(p_izquierda, p_derecha, p_abajo))

        eje_x, eje_y = rotate_in_plane(derecha, arriba, group_rotation(group))
        fin_x = centro.Add(eje_x.Multiply(LONGITUD_EJE))
        fin_y = centro.Add(eje_y.Multiply(LONGITUD_EJE))

        doc.Create.NewDetailCurve(view, Line.CreateBound(centro, fin_x))
        doc.Create.NewDetailCurve(view, Line.CreateBound(centro, fin_y))

        type_id = text_note_type_id(doc)
        if type_id is None:
            return False, "no hay tipo de texto disponible"
        TextNote.Create(doc, view.Id, fin_x.Add(eje_x.Multiply(OFFSET_TEXTO)), "X", type_id)
        TextNote.Create(doc, view.Id, fin_y.Add(eje_y.Multiply(OFFSET_TEXTO)), "Y", type_id)
        return True, None
    except Exception as exc:
        return False, hz.brief(exc)


# ---- the host contract: plan / apply / verify ------------------------------

def _resolve_marker_view(doc, args):
    view_id = hz.arg(args, "marker_view_id")
    if not view_id:
        return None, None
    view = doc.GetElement(hz.to_eid(view_id))
    if not isinstance(view, View):
        return None, "marker_view_id {0} is not a view".format(view_id)
    return view, None


def plan(doc, args):
    param_name = hz.arg(args, "origin_group_param")
    if not param_name:
        raise Exception(
            "origin_group_param is required: this tool exists to stamp each element with the name of "
            "the group it belonged to, and the parameter to stamp is your standard, not one this "
            "bridge is entitled to assume.")

    marker_view, marker_error = _resolve_marker_view(doc, args)

    scope = hz.resolve(doc, args, is_model_group, of_class=Group)

    eligible = []
    skipped = []

    for group in scope.elements:
        try:
            member_ids = list(group.GetMemberIds())
        except Exception as exc:
            skipped.append({"id": hz.eid(group.Id), "name": group_name(group),
                            "reason": "its members could not be listed: " + hz.brief(exc)})
            continue

        # Sample the members BEFORE anything is ungrouped. A group whose members
        # cannot carry the stamp must not be taken apart: ungrouping is not
        # reversible from here, and an unmarked scattered group is unrecoverable.
        carriers = 0
        blockers = {}
        for member_id in member_ids:
            member = doc.GetElement(member_id)
            if member is None:
                continue
            reason = can_write_text(member, param_name)
            if reason is None:
                carriers += 1
            else:
                blockers[reason] = blockers.get(reason, 0) + 1

        entry = {
            "id": hz.eid(group.Id),
            "name": group_name(group),
            "members": len(member_ids),
            "members_that_can_be_marked": carriers,
            "blockers": blockers,
            "pinned": bool(group.Pinned),
        }

        if carriers == 0:
            entry["reason"] = ("not one member can carry '" + param_name + "'. Ungrouping would scatter "
                               "them with nothing recording where they came from, so this group is left "
                               "intact. Bind the parameter to these categories first "
                               "(horizun_bind_shared_param).")
            skipped.append(entry)
        else:
            eligible.append(entry)

    return {
        "scope": scope.report(),
        "origin_group_param": param_name,
        "marker_view_id": hz.eid(marker_view.Id) if marker_view is not None else None,
        "marker_view_error": marker_error,
        "eligible": eligible,
        "skipped": skipped,
        "would_ungroup": len(eligible),
        "would_mark": sum(e["members_that_can_be_marked"] for e in eligible),
        "note": ("Members that cannot carry the parameter are counted per group in 'blockers' and are "
                 "ungrouped WITHOUT a stamp - they will not come back with horizun_regroup_by_param. "
                 "A group where NO member can carry it is refused outright."),
    }


def apply(doc, args, plan):
    param_name = plan["origin_group_param"]
    marker_view, marker_error = _resolve_marker_view(doc, args)
    if marker_error:
        raise Exception(marker_error + ". Nothing was ungrouped.")

    ungrouped = []
    marked_ids = []
    markers_drawn = 0
    markers_failed = []
    errors = []
    skipped_members = []

    for entry in plan["eligible"]:
        group = doc.GetElement(hz.to_eid(entry["id"]))
        if group is None:
            errors.append({"id": entry["id"], "error": "vanished between plan and apply"})
            continue

        origin = group_name(group)

        # The marker goes in FIRST: it is drawn at the group's own origin point, and
        # once the group is gone that point is gone with it.
        if marker_view is not None:
            ok, reason = draw_origin_marker(doc, marker_view, group)
            if ok:
                markers_drawn += 1
            else:
                markers_failed.append({"group": origin, "reason": reason})

        try:
            if group.Pinned:
                group.Pinned = False
            member_ids = list(group.UngroupMembers())
        except Exception as exc:
            errors.append({"id": entry["id"], "name": origin, "error": hz.brief(exc)})
            continue

        ungrouped.append(entry["id"])

        for member_id in member_ids:
            member = doc.GetElement(member_id)
            if member is None:
                continue
            ok, reason = write_text(member, param_name, origin)
            if ok:
                marked_ids.append(hz.eid(member_id))
            else:
                skipped_members.append({
                    "group": origin,
                    "id": hz.eid(member_id),
                    "category": element_category(member),
                    "name": element_name(member) or "<Sin nombre>",
                    "reason": reason or "desconocido",
                })

    return {
        "ungrouped_ids": ungrouped,
        "marked_ids": marked_ids,
        "ungrouped": len(ungrouped),
        "marked": len(marked_ids),
        "markers_drawn": markers_drawn,
        "markers_failed": markers_failed,
        "members_not_marked": skipped_members,
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    """After the commit, ask the MODEL: are the groups really gone, and does each
    element that was stamped really carry the value now."""
    param_name = plan["origin_group_param"]

    gone = 0
    for value in applied["ungrouped_ids"]:
        if not hz.still_exists(doc, value):
            gone += 1

    carrying = 0
    for value in applied["marked_ids"]:
        element = doc.GetElement(hz.to_eid(value))
        if element is None:
            continue
        param = element.LookupParameter(param_name)
        if param is None:
            continue
        try:
            if param.AsString():
                carrying += 1
        except Exception:
            pass

    return {
        "groups_gone": gone,
        "elements_carrying_the_stamp": carrying,
        "intended_ungrouped": applied["ungrouped"],
        "intended_marked": applied["marked"],
    }
