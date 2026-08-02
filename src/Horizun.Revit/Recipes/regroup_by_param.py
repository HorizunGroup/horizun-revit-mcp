# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# PUT BACK A GROUP THAT WAS UNGROUPED AND STAMPED.
#
# The other half of horizun_ungroup_and_mark. That tool scattered a group's
# members and wrote the group's name into a text parameter on each of them; this
# one collects everything still carrying a given value, groups it again, and
# clears the stamp so the pair is idempotent.
#
# Ported from the "Reagrupar MOD_" pyRevit button. WHAT WAS FIXED, and why each
# was a real defect:
#
#   1. IT SWEPT UP ELEMENTS THAT CANNOT BE IN A MODEL GROUP. The button collected
#      EVERY non-type element in the document carrying the parameter and handed
#      the lot to doc.Create.NewGroup. Annotation - anything view-specific -
#      cannot be a member of a model group, and Revit answers the whole call with
#      one ArgumentException. So a single stray tag anywhere in the model made the
#      button fail entirely, with a dialog that named nothing. View-specific
#      elements are now excluded up front and REPORTED, so the rest still groups.
#
#   2. IT CLEARED THE STAMP AFTER GROUPING. Writing a parameter on an element that
#      is already inside a Model Group is the operation that raises Revit's
#      "elements in group are different" modal - and a modal nobody answers holds
#      Revit's UI thread until the caller times out. The stamp is now cleared
#      BEFORE the group is created. Same end state, no modal, and if the grouping
#      fails the host rolls the clearing back with it.
#
#   3. THE PARAMETER AND THE PREFIX WERE COMPILED IN ("_GrupoOrigen", "MOD_").
#      Both are arguments now; the prefix defaults to empty rather than to one
#      organisation's convention.
#
# No Transaction in this file. See Recipe.cs.
# -----------------------------------------------------------------------------
from Autodesk.Revit.DB import (
    Group, GroupType, ElementId, BuiltInCategory, FilteredElementCollector,
    StorageType, Element, BuiltInParameter
)
from System.Collections.Generic import List

import hz

MODEL_GROUP_CATEGORY_ID = hz.eid(ElementId(BuiltInCategory.OST_IOSModelGroups))
INVALID_ID = hz.eid(ElementId.InvalidElementId)


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


def read_text(element, param_name):
    param = element.LookupParameter(param_name)
    if not param:
        return None
    if param.StorageType != StorageType.String:
        return None
    value = param.AsString()
    if not value:
        return None
    return value.strip()


def clear_text(element, param_name):
    param = element.LookupParameter(param_name)
    if not param:
        return False, "no existe"
    if param.IsReadOnly:
        return False, "solo lectura"
    if param.StorageType != StorageType.String:
        return False, "no es texto"
    param.Set("")
    return True, None


def is_in_a_group(element):
    try:
        group_id = element.GroupId
    except Exception:
        return False
    if group_id is None:
        return False
    return hz.eid(group_id) != INVALID_ID


def can_join_a_model_group(element):
    """Why this element cannot be a member of a model group, or None when it can.

    View-specific elements are the case the original button tripped over: Revit
    puts annotation into an ATTACHED DETAIL group, never into the model group
    itself, and refuses the whole NewGroup call rather than the one element."""
    try:
        if element.ViewSpecific:
            return "view-specific (annotation): a model group cannot contain it"
    except Exception:
        pass
    if element.Category is None:
        return "it has no category"
    if is_in_a_group(element):
        return "it already belongs to a group"
    return None


def available_group_name(doc, base_name):
    """A model-group type name nothing else is using. Detail group types are a
    different category and do not collide."""
    taken = set()
    for group_type in FilteredElementCollector(doc).OfClass(GroupType):
        try:
            if (group_type.Category
                    and hz.eid(group_type.Category.Id) == MODEL_GROUP_CATEGORY_ID):
                name = element_name(group_type)
                if name:
                    taken.add(name)
        except Exception:
            continue

    if base_name not in taken:
        return base_name

    index = 1
    while True:
        candidate = "{0}_{1:02d}".format(base_name, index)
        if candidate not in taken:
            return candidate
        index += 1


def id_list(element_ids):
    out = List[ElementId]()
    for element_id in element_ids:
        out.Add(element_id)
    return out


# ---- the host contract: plan / apply / verify ------------------------------

def _collect(doc, param_name, wanted_value):
    """Everything loose in the model carrying the stamp, split into what can be
    grouped and what cannot."""
    groupable = {}
    rejected = {}

    for element in FilteredElementCollector(doc).WhereElementIsNotElementType():
        if isinstance(element, Group):
            continue

        value = read_text(element, param_name)
        if not value:
            continue
        if wanted_value and value != wanted_value:
            continue

        reason = can_join_a_model_group(element)
        if reason is None:
            groupable.setdefault(value, []).append(element.Id)
        else:
            rejected.setdefault(value, []).append({
                "id": hz.eid(element.Id),
                "category": element_category(element),
                "name": element_name(element) or "<Sin nombre>",
                "reason": reason,
            })

    return groupable, rejected


def plan(doc, args):
    param_name = hz.arg(args, "origin_group_param")
    if not param_name:
        raise Exception(
            "origin_group_param is required: it names the text parameter holding the group each "
            "element came from. It is your standard, not one this bridge may assume.")

    wanted_value = hz.arg(args, "origin_value")
    prefix = hz.arg(args, "group_name_prefix", "")

    groupable, rejected = _collect(doc, param_name, wanted_value)

    candidates = []
    for value in sorted(groupable.keys()):
        ids = groupable[value]
        excluded = rejected.get(value, [])
        base = prefix + value
        candidates.append({
            "origin_value": value,
            "elements": len(ids),
            "element_ids": [hz.eid(i) for i in ids],
            "group_name": available_group_name(doc, base),
            "requested_group_name": base,
            "excluded": excluded,
            "excluded_count": len(excluded),
        })

    # A value whose every element was excluded still deserves to be reported: it is
    # the case where the answer is "nothing to group", and silence would look like
    # "nothing was there".
    for value in sorted(rejected.keys()):
        if value not in groupable:
            candidates.append({
                "origin_value": value,
                "elements": 0,
                "element_ids": [],
                "group_name": None,
                "requested_group_name": prefix + value,
                "excluded": rejected[value],
                "excluded_count": len(rejected[value]),
                "reason": "every element carrying this value is excluded; nothing can be grouped",
            })

    return {
        "origin_group_param": param_name,
        "origin_value_filter": wanted_value,
        "group_name_prefix": prefix,
        "candidates": candidates,
        "would_create_groups": len([c for c in candidates if c["elements"] > 0]),
        "would_group_elements": sum(c["elements"] for c in candidates),
        "note": ("View-specific elements (annotation) are excluded: a model group cannot contain them, "
                 "and Revit refuses the WHOLE grouping call rather than the one element. They are "
                 "listed per candidate in 'excluded' and keep their stamp."),
    }


def apply(doc, args, plan):
    param_name = plan["origin_group_param"]

    created = []
    cleared = 0
    not_cleared = []
    errors = []

    for candidate in plan["candidates"]:
        if candidate["elements"] <= 0:
            continue

        ids = [hz.to_eid(v) for v in candidate["element_ids"]]

        # Clear the stamp BEFORE grouping. Writing a parameter on an element that is
        # already a group member is what raises Revit's group modal, and a modal
        # nobody answers holds the UI thread.
        for element_id in ids:
            element = doc.GetElement(element_id)
            if element is None:
                continue
            ok, reason = clear_text(element, param_name)
            if ok:
                cleared += 1
            else:
                not_cleared.append({"id": hz.eid(element_id), "reason": reason})

        try:
            group = doc.Create.NewGroup(id_list(ids))
            doc.Regenerate()
            group.GroupType.Name = candidate["group_name"]
            created.append({
                "id": hz.eid(group.Id),
                "name": candidate["group_name"],
                "origin_value": candidate["origin_value"],
                "elements": len(ids),
                # Carried so verify() can prove CONTAINMENT of what we grouped,
                # rather than compare against a member count that also includes
                # the sketch internals Revit adds.
                "element_ids": list(candidate["element_ids"]),
            })
        except Exception as exc:
            # Nothing is swallowed: the host rolls the whole transaction back, so the
            # clearing above goes with it. Say which value failed and why.
            raise Exception(
                "Revit refused to group the {0} element(s) carrying '{1}': {2}. Nothing was written - "
                "the parameter clearing is rolled back with it.".format(
                    len(ids), candidate["origin_value"], hz.brief(exc, 300)))

    return {
        "created_groups": created,
        "created": len(created),
        "elements_grouped": sum(c["elements"] for c in created),
        "stamps_cleared": cleared,
        "stamps_not_cleared": not_cleared,
        "errors": errors,
    }


def verify(doc, args, plan, applied):
    """After the commit: does each group really exist, does it really hold the
    elements WE PUT IN IT, and is the stamp really gone.

    'The elements we grouped' and 'the members Revit says the group has' are two
    different quantities, and comparing them was this recipe's own bug: grouping
    two elements produces a group whose GetMemberIds() returns ten, because Revit
    counts the sketch internals - model lines, span direction edges, automatic
    dimensions - as members too. The raw count made a correct run report a
    mismatch. So the check is containment of the ids we asked for, which is the
    thing the caller actually wants proven."""
    present = 0
    members_confirmed = 0

    for entry in applied["created_groups"]:
        group = doc.GetElement(hz.to_eid(entry["id"]))
        if not isinstance(group, Group):
            continue
        present += 1
        try:
            inside = set(hz.eid(m) for m in group.GetMemberIds())
        except Exception:
            continue
        for value in entry.get("element_ids", []):
            if value in inside:
                members_confirmed += 1

    param_name = plan["origin_group_param"]
    still_stamped = 0
    for candidate in plan["candidates"]:
        for value in candidate["element_ids"]:
            element = doc.GetElement(hz.to_eid(value))
            if element is None:
                continue
            if read_text(element, param_name):
                still_stamped += 1

    return {
        "groups_present": present,
        "members_confirmed": members_confirmed,
        "elements_still_stamped": still_stamped,
        "intended_created": applied["created"],
        "intended_grouped": applied["elements_grouped"],
    }
