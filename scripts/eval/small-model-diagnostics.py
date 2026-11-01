#!/usr/bin/env python3
"""
Horizun Revit MCP - what tools/list costs a small model, measured without Revit.

Each configuration runs the real server over stdio with an EMPTY temporary data
root (no owner settings, client configuration or authentication files are read),
asks for tools/list, and records:

  * how many tools are advertised, and the UTF-8 bytes and characters of the
    {"tools":[...]} payload; tokens use the repository convention ceil(chars/4);
  * per tool: description length and schema characters;
  * the pairs of tools whose names and descriptions overlap most (cosine over
    word counts, accents folded), which is where a small model picks the wrong one.

    python scripts/eval/small-model-diagnostics.py --server <horizun-mcp.exe> --out diag.json

Read-only: it never connects to a Revit and never writes outside its temp roots.
"""
import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unicodedata
from collections import Counter

CONFIGS = [
    # (label, permission profile, HORIZUN_SCHEMA_MODE, HORIZUN_TOOLSETS)
    ("full", "safe_write", "full", None),
    ("local", "safe_write", "full", "local"),
    ("facade", "safe_write", "facade", None),
    ("local+facade", "safe_write", "facade", "local"),
    ("local@full_write", "full_write", "full", "local"),
    # compact exists from the small-model work on; an older server refuses the mode and is skipped.
    ("local+compact", "safe_write", "compact", "local"),
    ("local+compact@full_write", "full_write", "compact", "local"),
    # tool profiles (settings "tool_profile"); a new install writes esencial.
    ("esencial", "safe_write", "full", None, "esencial"),
    ("esencial+compact", "safe_write", "compact", None, "esencial"),
    ("consulta", "safe_write", "full", None, "consulta"),
    ("modelado", "safe_write", "full", None, "modelado"),
    ("documentacion", "safe_write", "full", None, "documentacion"),
    ("auditoria", "safe_write", "full", None, "auditoria"),
]

STOP = set("""
a an and are as at be by can for from in into is it its of on one or the this to
with without when which that than not no only per each every via any all your you
el la los las de del un una y o en por para con sin que se su sus es al lo
""".split())


def fold(text):
    text = unicodedata.normalize("NFKD", text or "")
    return "".join(c for c in text if not unicodedata.combining(c)).lower()


def words(text):
    return [w for w in re.findall(r"[a-z0-9]+", fold(text)) if len(w) > 2 and w not in STOP]


def cosine(a, b):
    common = set(a) & set(b)
    num = sum(a[w] * b[w] for w in common)
    den = math.sqrt(sum(v * v for v in a.values())) * math.sqrt(sum(v * v for v in b.values()))
    return num / den if den else 0.0


def tools_list(server, profile, mode, toolsets, timeout, tool_profile=None):
    root = tempfile.mkdtemp(prefix="hz-small-diag-")
    try:
        with open(os.path.join(root, "settings.json"), "w", encoding="utf-8") as f:
            settings = {"permission_profile": profile, "enable_execute_python": False}
            if tool_profile:
                settings["tool_profile"] = tool_profile
            json.dump(settings, f)
        env = {k: v for k, v in os.environ.items()
               if k not in ("HORIZUN_TOOLSETS", "HORIZUN_TOOL_PACKS", "HORIZUN_REVIT_YEAR", "HORIZUN_SCHEMA_MODE",
                            "HORIZUN_PROFILE")}
        env["HORIZUN_DATA_ROOT"] = root
        env["HORIZUN_SCHEMA_MODE"] = mode
        if toolsets:
            env["HORIZUN_TOOLSETS"] = toolsets
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize",
             "params": {"protocolVersion": "2025-11-25", "capabilities": {},
                        "clientInfo": {"name": "small-model-diagnostics", "version": "1"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
        ]
        stdin = "".join(json.dumps(r) + "\n" for r in requests).encode("utf-8")
        proc = subprocess.run([server], input=stdin, capture_output=True, env=env, timeout=timeout)
        instructions = None
        for line in proc.stdout.decode("utf-8").splitlines():
            if not line.strip():
                continue
            msg = json.loads(line)
            if msg.get("id") == 1:
                instructions = (msg.get("result") or {}).get("instructions")
            if msg.get("id") == 2:
                return msg["result"]["tools"], instructions
        raise RuntimeError("no tools/list reply; stderr: " + proc.stderr.decode("utf-8", "replace")[-2000:])
    finally:
        shutil.rmtree(root, ignore_errors=True)


def measure(tools, instructions):
    payload = json.dumps({"tools": tools}, ensure_ascii=False, separators=(",", ":"))
    per_tool = []
    for t in tools:
        schema = json.dumps(t.get("inputSchema", {}), ensure_ascii=False, separators=(",", ":"))
        props = (t.get("inputSchema") or {}).get("properties") or {}
        per_tool.append({
            "name": t["name"],
            "description_chars": len(t.get("description") or ""),
            "schema_chars": len(schema),
            "arguments": len(props),
            "required": len((t.get("inputSchema") or {}).get("required") or []),
            "tool_chars": len(json.dumps(t, ensure_ascii=False, separators=(",", ":"))),
        })
    per_tool.sort(key=lambda r: -r["tool_chars"])
    # The pointer every compact description ends with is shared boilerplate, not meaning.
    def meaning(d):
        return re.sub(r"Full schema: horizun://contract/tools/\S+", "", d or "")
    vectors = {t["name"]: Counter(words(t["name"].replace("_", " ") + " " + meaning(t.get("description"))[:600]))
               for t in tools}
    names = sorted(vectors)
    pairs = []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            s = cosine(vectors[a], vectors[b])
            if s >= 0.35:
                pairs.append({"a": a, "b": b, "similarity": round(s, 3)})
    pairs.sort(key=lambda p: -p["similarity"])
    return {
        "tools": len(tools),
        "bytes": len(payload.encode("utf-8")),
        "chars": len(payload),
        "tokens_est": math.ceil(len(payload) / 4),
        "instructions_chars": len(instructions or ""),
        "descriptions_over_200": sum(1 for r in per_tool if r["description_chars"] > 200),
        "description_chars_median": sorted(r["description_chars"] for r in per_tool)[len(per_tool) // 2] if per_tool else 0,
        "per_tool": per_tool,
        "similar_pairs": pairs[:40],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--timeout", type=int, default=90)
    args = ap.parse_args()
    report = {"schema": "horizun.small-model-diagnostics/1", "token_rule": "ceil(chars/4)", "configs": {}}
    for label, profile, mode, toolsets, *rest in CONFIGS:
        tool_profile = rest[0] if rest else None
        try:
            tools, instructions = tools_list(args.server, profile, mode, toolsets, args.timeout, tool_profile)
        except Exception as e:
            print(f"{label:18} skipped: {str(e)[:160]}")
            continue
        m = measure(tools, instructions)
        m.update({"permission_profile": profile, "schema_mode": mode, "toolsets": toolsets,
                  "tool_profile": tool_profile})
        report["configs"][label] = m
        print(f"{label:18} tools={m['tools']:4} bytes={m['bytes']:8} tokens~{m['tokens_est']:7} "
              f"desc>200={m['descriptions_over_200']:3} similar_pairs={len(m['similar_pairs'])}")
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
