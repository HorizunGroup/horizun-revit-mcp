"""Measure the real stdio surface without reaching Revit or Ollama (UTF-16 chars/4)."""
import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile


def measure(exe, mode, profile):
    with tempfile.TemporaryDirectory(prefix="hz-schema-") as root:
        Path(root, "settings.json").write_text(json.dumps({"permission_profile": profile}), encoding="utf-8")
        env = dict(os.environ, HORIZUN_DATA_ROOT=root, HORIZUN_SCHEMA_MODE=mode)
        for key in ("HORIZUN_PROFILE", "HORIZUN_TOOLSETS", "HORIZUN_TOOL_PACKS", "HORIZUN_RESPONSE"):
            env.pop(key, None)
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "schema-measurement", "version": "1"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
        ]
        proc = subprocess.run([exe], input="".join(json.dumps(r) + "\n" for r in requests),
                              capture_output=True, text=True, encoding="utf-8", env=env, timeout=120)
        proc.check_returncode()
        reply = next(json.loads(line) for line in proc.stdout.splitlines() if json.loads(line).get("id") == 2)
        result = reply["result"]
        names = {t["name"] for t in result["tools"]}
        if mode == "local" and ("horizun_list_tools" not in names or len(names) > 11):
            raise RuntimeError("This executable did not expose the local profile; build the current source first.")
        wire = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
        chars = len(wire.encode("utf-16-le")) // 2
        return {"mode": mode, "permission_profile": profile, "tools": len(result["tools"]),
                "characters": chars, "utf8_bytes": len(wire.encode("utf-8")),
                "approx_tokens": math.ceil(chars / 4), "approximation": "ceil(UTF-16 characters/4)"}, result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True)
    parser.add_argument("--modes", nargs="+", default=["full", "facade", "local"])
    parser.add_argument("--profile", default="safe_write")
    parser.add_argument("--output", required=True)
    parser.add_argument("--dump-dir")
    args = parser.parse_args()
    rows = []
    for mode in args.modes:
        row, result = measure(args.exe, mode, args.profile)
        rows.append(row)
        if args.dump_dir:
            Path(args.dump_dir).mkdir(parents=True, exist_ok=True)
            Path(args.dump_dir, mode + ".json").write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    Path(args.output).write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(rows, indent=2))
