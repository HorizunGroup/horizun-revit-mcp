#!/usr/bin/env python3
"""Does each command actually READ what its contract offers?

A STATIC CHECK, and it says so: it reads the sources and proves nothing about
behaviour. What it catches is the cheap half and the half that bites - a schema
property nobody implemented, which a client will send, this server will accept,
and the far end will silently ignore. That failure produces a plausible answer to
a question nobody asked, and it is invisible from both ends.

WHY IT EXISTS AT ALL. The contract is shared and hashed, so the two HALVES cannot
drift; nothing until now compared the contract to the code that answers it. A
parameter added to the schema and never read compiles, ships, and passes every
test that does not happen to use it.

THE ONE SUBTLETY, and the reason this file is longer than a grep: argument names
are often COMPOSED. `side + "_element_id"` and `plane + "_offset"` are read
correctly and appear nowhere as literals. Reporting those as missing trains
everybody to ignore the output, so they are detected separately and reported as
"composed" - visible, but not an alarm.

Run:  python scripts/check-contract-coverage.py
Exit: 0 when nothing is unread, 1 otherwise.

It executes nothing but itself: no build, no Revit, no server.
"""
from __future__ import annotations

import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: tool name -> the source file(s) that answer it.
#:
#: A tool absent from this map is not checked, and that is reported too: an
#: unchecked tool is a gap in this check, not a clean result.
ANSWERS = {
    "horizun_connect_mep": ["src/Horizun.Revit/Commands/ConnectMepCommand.cs"],
    "horizun_structural_connections": ["src/Horizun.Revit/Commands/StructuralConnectionsCommand.cs"],
    "horizun_manage_materials": ["src/Horizun.Revit/Commands/ManageMaterialsCommand.cs"],
    "horizun_manage_styles": ["src/Horizun.Revit/Commands/ManageStylesCommand.cs", "src/Horizun.Revit/Commands/VerifiedModelEdit.cs"],
    "horizun_manage_units": ["src/Horizun.Revit/Commands/ManageUnitsCommand.cs", "src/Horizun.Revit/Commands/VerifiedModelEdit.cs"],
    "horizun_electrical": ["src/Horizun.Revit/Commands/ElectricalCommand.cs", "src/Horizun.Revit/Commands/VerifiedModelEdit.cs"],
    "horizun_validate_ids": [
        "src/Horizun.Revit/Commands/ValidateIdsCommand.cs",
        "src/Horizun.Revit/Core/IdsDocument.cs",
        "src/Horizun.Revit/Core/IdsRestriction.cs",
        "src/Horizun.Revit/Core/IdsRun.cs",
        "src/Horizun.Revit/Core/IdsRevitPrecheck.cs",
        "src/Horizun.Revit/Core/IdsIfcEvaluator.cs",
        # The `cooperative` block is parsed in one shared place for every reader that
        # accepts it. Without this line its three keys read as unread on every one of them.
        "src/Horizun.Revit/Core/CooperativeReadOptions.cs",
    ],
    "horizun_list_elements": [
        "src/Horizun.Revit/Commands/ListElementsCommand.cs",
        "src/Horizun.Revit/Core/CooperativeReadOptions.cs",
    ],
    "horizun_navigate": [
        "src/Horizun.Revit/Commands/NavigateCommand.cs",
        "src/Horizun.Revit/Commands/NavigateLinked.cs",
    ],
    "horizun_audit_access": [
        "src/Horizun.Revit/Commands/AuditAccessCommand.cs",
        "src/Horizun.Revit/Core/AccessRules.cs",
        "src/Horizun.Revit/Core/AccessGeometry.cs",
    ],
    "horizun_copy_between_documents": ["src/Horizun.Revit/Commands/CopyBetweenDocumentsCommand.cs"],
    "horizun_plan_from_ifc": [
        "src/Horizun.Revit/Commands/PlanFromIfcCommand.cs",
        "src/Horizun.Revit/Core/IfcSubset.cs",
        "src/Horizun.Revit/Core/IfcProfile.cs",
        "src/Horizun.Revit/Core/IfcPlacement.cs",
    ],
    "horizun_apply_ifc_plan": [
        "src/Horizun.Revit/Commands/ApplyIfcPlanCommand.cs",
        "src/Horizun.Revit/Core/IfcProvenanceStore.cs",
    ],
    "horizun_repair_memory": ["src/Horizun.Server/RepairMemoryTool.cs"],
    "horizun_promote_script": [
        "src/Horizun.Server/ScriptPromotionTool.cs",
        "src/Horizun.Revit/Core/ScriptPromotion.cs",
        "src/Horizun.Revit/Core/ScriptInvocation.cs",
    ],
    "horizun_selection_exchange": ["src/Horizun.Server/PowerBiSelection.cs"],
    "horizun_manage_views": [
        "src/Horizun.Revit/Commands/ManageViewsCommand.cs",
        "src/Horizun.Revit/Commands/ManageViewsGraphics.cs",
        "src/Horizun.Revit/Commands/ManageViewsLegends.cs",
    ],
    "horizun_manage_schedules": [
        "src/Horizun.Revit/Commands/ManageSchedulesCommand.cs",
        "src/Horizun.Revit/Core/ScheduleEditRules.cs",
    ],
    "horizun_coordination": [
        "src/Horizun.Revit/Commands/CoordinationCommand.cs",
        "src/Horizun.Revit/Commands/CoordinationImport.cs",
    ],
}

#: Arguments every command receives through shared plumbing - DocumentGate,
#: the confirmation ladder, the unit scale - rather than by reading the name.
SHARED = {
    "target_document",
    "expected_document",
    "target_document_title",
    "dry_run",
    "confirmation_token",
    "transaction_name",
    "units",
}


def schema_properties(node, out, depth=0):
    """Every property name anywhere in a JSON Schema, nested objects included."""
    if depth > 10 or not isinstance(node, dict):
        return
    properties = node.get("properties")
    if isinstance(properties, dict):
        for name, child in properties.items():
            out.add(name)
            schema_properties(child, out, depth + 1)
    items = node.get("items")
    if isinstance(items, dict):
        schema_properties(items, out, depth + 1)
    additional = node.get("additionalProperties")
    if isinstance(additional, dict):
        schema_properties(additional, out, depth + 1)


def composed_suffixes(body):
    """Suffixes the code builds argument names from, e.g. `+ "_offset"`."""
    return set(re.findall(r'\+\s*"(_[a-z0-9_]+)"', body))


def input_schema(contract, tool):
    marker = 'Name = "%s"' % tool
    if marker not in contract:
        return None, "not declared in the contract"
    start = contract.index(marker)
    opener = 'InputSchema = JObject.Parse(@"'
    if opener not in contract[start:start + 20000]:
        return None, "declares no InputSchema"
    head = contract.index(opener, start) + len(opener)
    tail = contract.index('")', head)
    try:
        return json.loads(contract[head:tail].replace('""', '"')), None
    except ValueError as exc:
        return None, "schema does not parse: %s" % exc


def main():
    contract_path = os.path.join(ROOT, "src/Horizun.Contracts/Contract.cs")
    contract = io.open(contract_path, encoding="utf-8-sig").read()

    declared_tools = set(re.findall(r'Name = "([a-z0-9_]+)"', contract))
    unchecked = sorted(declared_tools - set(ANSWERS))

    unread = 0
    for tool in sorted(ANSWERS):
        schema, problem = input_schema(contract, tool)
        if problem:
            print("!! %-34s %s" % (tool, problem))
            unread += 1
            continue

        declared = set()
        schema_properties(schema, declared)
        declared -= SHARED

        body = ""
        for relative in ANSWERS[tool]:
            path = os.path.join(ROOT, relative)
            if not os.path.isfile(path):
                print("!! %-34s file missing: %s" % (tool, relative))
                unread += 1
                continue
            body += io.open(path, encoding="utf-8-sig").read()

        suffixes = composed_suffixes(body)
        missing, composed = [], []
        for name in sorted(declared):
            if ('"%s"' % name) in body:
                continue
            if any(name.endswith(suffix) for suffix in suffixes):
                composed.append(name)
            else:
                missing.append(name)

        if missing:
            print("%-34s DECLARED BUT NEVER READ: %s" % (tool, ", ".join(missing)))
            unread += 1
        if composed:
            print("%-34s (composed at run time, not an alarm: %s)" % (tool, ", ".join(composed)))

    if unchecked:
        print("\nNOT CHECKED - no answering file is mapped for these, which is a gap in")
        print("this check rather than a clean result:")
        for tool in unchecked:
            print("  - " + tool)

    print("\n%d tool(s) declare an argument nothing reads." % unread)
    return 1 if unread else 0


if __name__ == "__main__":
    sys.exit(main())
