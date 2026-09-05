# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# THE MARKDOWN MATRIX IS A PROJECTION OF THE JSON, NEVER A SECOND SOURCE.
#
# Every number a reader sees - the cells, the denominators, the three tables of
# what is not passing - is rendered from release-1.2.0-acceptance.json between
# markers in the document. `--check` re-renders and refuses when the document has
# drifted, so a hand-edited total cannot survive a gate.
#
#   python scripts/render-acceptance-matrix.py            # write
#   python scripts/render-acceptance-matrix.py --check    # fail if it differs
# -----------------------------------------------------------------------------
import argparse
import io
import json
import os
import sys

BEGIN = "<!-- BEGIN GENERATED: acceptance-matrix (scripts/render-acceptance-matrix.py) -->"
END = "<!-- END GENERATED: acceptance-matrix -->"

LABEL = {
    "verify-registry-contract.ps1": "registry <-> contract",
    "verify-doctor-corrections.ps1": "Model Doctor corrections",
    "verify-prevention-gate.ps1": "pre-delivery gate",
    "verify-dwg-incremental.ps1": "DWG incremental",
    "verify-quantities-budget.ps1": "quantities / budget",
    "verify-worksharing-fixtures.ps1": "worksharing fixtures",
}
ORDER = ["verify-registry-contract.ps1", "verify-doctor-corrections.ps1", "verify-prevention-gate.ps1",
         "verify-dwg-incremental.ps1", "verify-quantities-budget.ps1", "verify-worksharing-fixtures.ps1"]
YEARS = ["2023", "2024", "2025", "2026", "2027"]

BLOCKER_TITLE = {
    "external_resource": "External blockers - a resource this machine does not have",
    "api_not_observable": "Not observable through the API - no fixture would change it",
    "not_observed": "Structurally verified, not reproduced live",
    "locally_constructible": "Buildable here and not yet built",
}


def cell(cases, harness, year, scenario):
    rows = [c for c in cases if c["harness"] == harness and c["revit_year"] == year and c["scenario"] == scenario]
    if not rows:
        return None
    counts = {}
    for r in rows:
        counts[r["result_status"]] = counts.get(r["result_status"], 0) + 1
    bits = []
    for name, label in (("passed", "pass"), ("failed", "**FAIL**"), ("unverified", "unverified"),
                        ("fixture_missing", "fixture"), ("not_applicable", "n/a")):
        if counts.get(name):
            bits.append("%d %s" % (counts[name], label))
    for other, n in sorted(counts.items()):
        if other not in ("passed", "failed", "unverified", "fixture_missing", "not_applicable"):
            bits.append("%d %s" % (n, other))
    return ", ".join(bits)


def render(doc):
    cov = doc["coverage"]
    cases = cov["cases"]
    scenarios = {}
    for c in cases:
        scenarios.setdefault((c["harness"], c["revit_year"]), set()).add(c["scenario"])

    main = {y: "HZ_M%s" % y for y in YEARS}
    out = [BEGIN, ""]
    out.append("| capability | " + " | ".join(YEARS) + " |")
    out.append("|---|" + "---|" * len(YEARS))
    for harness in ORDER:
        row = []
        for y in YEARS:
            row.append(cell(cases, harness, y, main[y]) or "not run")
        out.append("| %s | %s |" % (LABEL.get(harness, harness), " | ".join(row)))
    out.append("")

    extra = []
    for (harness, year), names in sorted(scenarios.items()):
        for scenario in sorted(names):
            if scenario != main.get(year):
                extra.append((harness, year, scenario))
    if extra:
        out.append("Other scenarios measured:")
        out.append("")
        out.append("| capability | year | scenario | result |")
        out.append("|---|---|---|---|")
        for harness, year, scenario in extra:
            out.append("| %s | %s | `%s` | %s |" % (LABEL.get(harness, harness), year, scenario,
                                                    cell(cases, harness, year, scenario)))
        out.append("")

    totals = cov["case_totals"]
    out.append("### Denominators")
    out.append("")
    order = ["passed", "failed", "unverified", "fixture_missing", "not_applicable"]
    parts = ["**%d %s**" % (totals[k], k) for k in order if totals.get(k)]
    parts += ["**%d %s**" % (v, k) for k, v in sorted(totals.items()) if k not in order]
    out.append("- **%d unique cases** — %s (%s = %d)." % (
        cov["unique_cases"], ", ".join(parts),
        " + ".join(str(totals[k]) for k in order if totals.get(k)), sum(totals.values())))
    out.append("- **%d results recorded** over **%d runs**; %d cases were measured more than once and only the "
               "qualifying latest one counts." % (cov["results_recorded"], doc["run_count"],
                                                  len(cov["repeated_cases"])))
    out.append("- **%d runs are history only** - measured on a dirty tree, or at a candidate this acceptance does "
               "not name. `cases_without_accepted_run` is %s." % (
                   len(cov["history_only_runs"]),
                   "empty" if not cov["cases_without_accepted_run"] else
                   "NOT empty: %d case(s)" % len(cov["cases_without_accepted_run"])))
    out.append("- **%d binary groups**, each `(commit, server SHA-256, add-in SHA-256)`." % len(doc["binary_groups"]))
    out.append("")

    not_passed = [c for c in cases if c["result_status"] != "passed"]
    by_kind = {}
    for c in not_passed:
        by_kind.setdefault(c["blocker_kind"] or "unclassified", []).append(c)
    for kind in ("external_resource", "api_not_observable", "not_observed", "locally_constructible", "unclassified"):
        rows = by_kind.get(kind)
        if not rows:
            continue
        out.append("### %s" % BLOCKER_TITLE.get(kind, kind))
        out.append("")
        out.append("| probe | capability | cases | status | evidence | why |")
        out.append("|---|---|---|---|---|---|")
        grouped = {}
        for r in rows:
            grouped.setdefault((r["probe"], r["harness"]), []).append(r)
        for (probe, harness), rs in sorted(grouped.items()):
            r = rs[0]
            out.append("| `%s` | %s | %d | %s | %s | %s |" % (
                probe, LABEL.get(harness, harness), len(rs), r["result_status"], r["evidence_level"],
                (r["why_not_passed"] or "").replace("\n", " ")))
        out.append("")

    out.append(END)
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default="docs/evidence/release-1.2.0-acceptance.json")
    ap.add_argument("--doc", default="docs/evidence/release-1.2.0-acceptance.md")
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args()

    doc = json.load(io.open(a.json, encoding="utf-8"))
    doc.setdefault("run_count", len(doc.get("harness_runs") or []))
    block = render(doc)

    text = io.open(a.doc, encoding="utf-8").read()
    if BEGIN not in text or END not in text:
        sys.exit("%s carries no generated-matrix markers; add %s ... %s where the matrix belongs"
                 % (a.doc, BEGIN, END))
    head, rest = text.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    rebuilt = head + block + tail

    if a.check:
        if rebuilt != text:
            sys.exit("%s has drifted from %s: re-run scripts/render-acceptance-matrix.py"
                     % (os.path.basename(a.doc), os.path.basename(a.json)))
        print("[PASS] the published matrix is the one the record produces")
        return
    io.open(a.doc, "w", encoding="utf-8", newline="\n").write(rebuilt)
    print("rendered", a.doc, "from", a.json)


if __name__ == "__main__":
    main()
