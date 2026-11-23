"""Inventory needs/requires messages and their declared JSON Schema requirements.

This is a review aid, not a semantic proof: prose may describe model conditions,
permissions or nested parameters rather than an input field. All messages remain
in the output so a reviewer can inspect unresolved candidates without silently
treating them as covered. No Revit session or external package is required.
"""
import argparse
import json
import re
from pathlib import Path


def schema_requirements(node, path="$"):
    found = {}
    if isinstance(node, dict):
        for name in node.get("required", []):
            found.setdefault(name, []).append(path + ".required")
        for key, value in node.items():
            for name, locations in schema_requirements(value, path + "." + key).items():
                found.setdefault(name, []).extend(locations)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            for name, locations in schema_requirements(value, path + f"[{index}]").items():
                found.setdefault(name, []).extend(locations)
    return found


def inventory(root, schema_json=None):
    contract = (root / "src/Horizun.Contracts/Contract.cs").read_text(encoding="utf-8-sig")
    schemas = {}
    for row in re.split(r"new CommandContract\s*\{", contract)[1:]:
        name = re.search(r'Name\s*=\s*"([^"]+)"', row)
        literal = re.search(r'InputSchema\s*=\s*JObject\.Parse\(@"((?:[^"]|"")*)"\)', row)
        if name and literal:
            schemas[name[1]] = json.loads(literal[1].replace('""', '"'))
    if schema_json:
        for tool in json.loads(schema_json.read_text(encoding="utf-8-sig"))["tools"]:
            schemas[tool["name"]] = tool["inputSchema"]
    sources = {}
    for path in (root / "src").rglob("*.cs"):
        if any(part in {"obj", "bin"} for part in path.parts):
            continue
        sources[path] = path.read_text(encoding="utf-8-sig")
    owners = {}
    for path, source in sources.items():
        tools = re.findall(r'\bName\s*=>\s*"(horizun_[^"]+)"', source)
        classes = re.findall(r'\bclass\s+(\w+)', source)
        if tools:
            for cls in (name for name in classes if name.endswith("Command")):
                owners.setdefault(cls, set()).update(tools)
    callers_by_rule = {}
    for source in sources.values():
        caller_tools = set()
        for cls in re.findall(r'\bclass\s+(\w+)', source):
            caller_tools.update(owners.get(cls, []))
        for rule in set(re.findall(r"\b(\w+Rules)\.", source)):
            callers_by_rule.setdefault(rule, set()).update(caller_tools)
    messages = []
    for path, source in sources.items():
        tools = set()
        for cls in re.findall(r'\bclass\s+(\w+)', source):
            tools.update(owners.get(cls, []))
        # Revit-free rules often carry validation for one command. Resolve that
        # relationship from the actual referring command source, not its name.
        if not tools:
            for cls in re.findall(r'\bclass\s+(\w+Rules)', source):
                tools.update(callers_by_rule.get(cls, []))
        for number, line in enumerate(source.splitlines(), 1):
            if line.lstrip().startswith(("//", "*")):
                continue
            for literal in re.findall(r'"((?:\\.|[^"\\])*)"', line):
                if not re.search(r"\b(?:needs|requires)\b", literal, re.IGNORECASE):
                    continue
                matches = []
                for tool in sorted(tools):
                    schema = schemas.get(tool, {})
                    props = schema.get("properties", {})
                    required = schema_requirements(schema)
                    mentioned = [field for field in props if re.search(r"\b" + re.escape(field) + r"\b", literal)]
                    matches.append({"tool": tool, "mentioned_fields": mentioned,
                                    "required_paths": {field: required.get(field, []) for field in mentioned},
                                    "review_candidates": [field for field in mentioned if field not in required]})
                messages.append({"source": str(path.relative_to(root)), "line": number,
                                 "message_fragment": literal, "contracts": matches})
    return {"schemas_read": len(schemas), "message_count": len(messages),
            "scope": "All non-comment needs/requires string fragments in src/**/*.cs, excluding bin/obj.",
            "limitation": "Candidate matching is lexical; model/permission preconditions and split messages require human review. Use --schema-json with real tools/list output to include computed schemas.",
            "messages": messages}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--schema-json", type=Path, help="A real tools/list result containing tools[].inputSchema.")
    args = parser.parse_args()
    report = inventory(args.root, args.schema_json)
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
        print(json.dumps({"output": str(args.output), "message_count": report["message_count"],
                          "schemas_read": report["schemas_read"]}))
    else:
        print(text)
