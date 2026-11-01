#!/usr/bin/env python3
"""
Summarise small-model battery results into Markdown.

Metrics are recomputed from each run's recorded calls with the scoring in
small-model-battery.py, so a scoring fix applies to every stored run alike.

    python scripts/eval/small-model-report.py results/antes results/despues > summary.md
"""
import glob
import importlib.util
import json
import os
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("battery", os.path.join(HERE, "small-model-battery.py"))
battery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(battery)


def load(dirs):
    tasks = {t["id"]: t for t in json.load(open(os.path.join(HERE, "small-model-tasks.json"), encoding="utf-8"))["tasks"]}
    out = []
    for d in dirs:
        for path in sorted(glob.glob(os.path.join(d, "*.json"))):
            s = json.load(open(path, encoding="utf-8"))
            for r in s["runs"]:
                r["first_call_right"] = battery.first_call_right(tasks[r["task"]], r["calls"])
                r["first_action_right"] = battery.first_call_right(tasks[r["task"]], r["calls"], action=True)
            out.append(s)
    return out


def main():
    summaries = load(sys.argv[1:])
    print("| Etiqueta | Modelo | Config | Tareas | Aciertos | Herramienta correcta a la primera (estricto) | Primera acción correcta | Llamadas (mediana) | Tokens entrada (mediana) | Tokens salida (total) | Segundos (mediana) |")
    print("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for s in summaries:
        runs = s["runs"]
        n = len(runs)
        ok = sum(r["completed"] for r in runs)
        first = sum(r["first_call_right"] for r in runs)
        act = sum(r["first_action_right"] for r in runs)
        print(f"| {s.get('label') or ''} | {s['model']} | {s['config']} | {n} | {ok} ({ok / n:.0%}) | {first} ({first / n:.0%}) | {act} ({act / n:.0%}) | "
              f"{statistics.median(r['tool_calls'] for r in runs):g} | {statistics.median(r['tokens_in'] for r in runs):,.0f} | "
              f"{sum(r['tokens_out'] for r in runs):,} | {statistics.median(r['seconds'] for r in runs):g} |")
    print()
    by_task = {}
    for s in summaries:
        key = f"{s.get('label') or ''} {s['model']} {s['config']}".strip()
        for r in s["runs"]:
            by_task.setdefault(r["task"], {})[key] = ("✔" if r["completed"] else "✘") + ("¹" if r["first_call_right"] else "")
    keys = [f"{s.get('label') or ''} {s['model']} {s['config']}".strip() for s in summaries]
    print("| Tarea | " + " | ".join(keys) + " |")
    print("|---|" + "---|" * len(keys))
    for task, cells in by_task.items():
        print(f"| {task} | " + " | ".join(cells.get(k, "") for k in keys) + " |")
    print("\n✔ = tarea cumplida (comprobada contra el modelo o la respuesta) · ¹ = herramienta correcta a la primera")


if __name__ == "__main__":
    main()
