# -*- coding: utf-8 -*-
"""Compatibility helpers for Revit 2023-2027 and IronPython 3 scripts.

Import this module from execute_python scripts and shipped recipes instead of
guessing which API member is present in the running Revit version.
"""

# Python 2 names still appear in older Dynamo/pyRevit snippets. IronPython 3
# runs Python 3, where these names are gone; keep their intent explicit.
text_type = str
string_types = (str, bytes)
integer_types = (int,)
unicode = str
basestring = string_types
long = int


def eid_int(element_id):
    """Return an ElementId number across the 32/64-bit API transition."""
    if element_id is None:
        return None
    try:
        return int(element_id.Value)
    except AttributeError:
        return int(element_id.IntegerValue)


def element_id(value, element_id_type=None):
    """Construct an ElementId from a Python int (or a Python 2-style long)."""
    if value is None:
        return None
    if isinstance(value, bool):
        raise TypeError("ElementId value must be an integer, not bool")
    number = int(value)
    if element_id_type is None:
        from Autodesk.Revit.DB import ElementId
        element_id_type = ElementId
    if isinstance(value, element_id_type):
        return value
    return element_id_type(number)


def _db():
    from Autodesk.Revit import DB
    return DB


def unit_type_id(name, legacy_name=None):
    """Resolve UnitTypeId on newer APIs or an explicit DisplayUnitType alias.

    Supply the legacy enum name when it differs, for example
    ``unit_type_id('Millimeters', 'DUT_MILLIMETERS')``. No unit is guessed.
    """
    db = _db()
    modern = getattr(getattr(db, "UnitTypeId", None), name, None)
    if modern is not None:
        return modern
    legacy_type = getattr(db, "DisplayUnitType", None)
    legacy = getattr(legacy_type, legacy_name or name, None) if legacy_type is not None else None
    if legacy is not None:
        return legacy
    raise AttributeError(
        "Neither UnitTypeId.%s nor DisplayUnitType.%s exists in this Revit API"
        % (name, legacy_name or name)
    )


def parameter_data_type(definition):
    """Return a definition's ForgeTypeId, falling back to legacy ParameterType."""
    getter = getattr(definition, "GetDataType", None)
    if getter is not None:
        return getter()
    return definition.ParameterType


def parameter_unit_type(parameter):
    """Return a parameter's unit identifier across Revit API generations."""
    getter = getattr(parameter, "GetUnitTypeId", None)
    if getter is not None:
        return getter()
    return parameter.DisplayUnitType


def parameter_group(name):
    """Resolve a GroupTypeId name or its legacy BuiltInParameterGroup value."""
    db = _db()
    supplied = str(name)
    if supplied.startswith("PG_"):
        legacy_name = supplied
        modern_name = "".join(part[:1] + part[1:].lower() for part in supplied[3:].split("_"))
    else:
        modern_name = supplied
        words = []
        word = ""
        for char in supplied:
            if char.isupper() and word:
                words.append(word)
                word = char
            else:
                word += char
        if word:
            words.append(word)
        legacy_name = "PG_" + "_".join(words).upper()

    modern = getattr(getattr(db, "GroupTypeId", None), modern_name, None)
    if modern is not None:
        return modern
    legacy = getattr(getattr(db, "BuiltInParameterGroup", None), legacy_name, None)
    if legacy is not None:
        return legacy
    raise AttributeError(
        "Neither GroupTypeId.%s nor BuiltInParameterGroup.%s exists in this Revit API"
        % (modern_name, legacy_name)
    )


def convert_from_internal(value, unit):
    """Convert an internal value with a UnitTypeId or DisplayUnitType argument."""
    return _db().UnitUtils.ConvertFromInternalUnits(float(value), unit)


def convert_to_internal(value, unit):
    """Convert an external value with a UnitTypeId or DisplayUnitType argument."""
    return _db().UnitUtils.ConvertToInternalUnits(float(value), unit)
