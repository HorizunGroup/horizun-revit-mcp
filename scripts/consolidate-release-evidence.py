#!/usr/bin/env python3
# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# Copy the two 1.2.0 campaign artifacts into docs/evidence as ONE sanitised,
# machine-readable record, with the identity of what was measured beside every
# number. Nothing is recomputed: counts, buckets and identities are copied from
# the runner's own artifact. Long per-case payloads are truncated to keep the
# record reviewable; the truncation is stated in the record.
#
#   python scripts/consolidate-release-evidence.py \
#       --wall  <run>/wallsplit-matrix.json \
#       --doctor <run>/doctor-campaign-*.json \
#       --out docs/evidence/release-1.2.0-live-evidence.json
#
# Refuses a source artifact that names a personal path or a fixture path outside
# the disposable fixtures root, so the record never carries a client's name.
# -----------------------------------------------------------------------------
import argparse
import json
import os
import re
import sys

PERSONAL = re.compile(r"[A-Za-z]:\\\\?Users\\\\?[A-Za-z0-9._-]+", re.IGNORECASE)

# A path also arrives FLATTENED into a directory name: an agent scratchpad names
# its session folder "C--Users-<account>-<repo>", which carries the same account
# name straight past a pattern written for separators. That is how a personal name
# reached an evidence file a separator-shaped redaction had already "cleaned".
# Redact the flattened shape too, and the bare account name of the machine that ran
# the sweep: a record needs to say WHERE a disposable file was, never who owns the
# profile.
FLATTENED = re.compile(r"[A-Za-z]--Users-[A-Za-z0-9._]+-", re.IGNORECASE)


def refuse_if_personal(obj, label):
    text = json.dumps(obj)
    hits = sorted(set(PERSONAL.findall(text) + FLATTENED.findall(text)))
    if hits:
        sys.exit(label + " names a personal path and is refused: " + ", ".join(hits))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wall", required=True)
    ap.add_argument("--doctor", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--release", default="1.2.0")
    ap.add_argument("--release-commit", default="1b3575c84718fcbafd4e6fb0a0a500948ff71418")
    ap.add_argument("--inventory-commit", default="419346697cec31fc99f7100a6d9980b7350e0f6b")
    ap.add_argument("--consolidated-utc", default="2026-09-03T00:00:00Z")
    a = ap.parse_args()

    w = json.load(open(a.wall, encoding="utf-8"))
    d = json.load(open(a.doctor, encoding="utf-8"))
    refuse_if_personal(w, a.wall)
    refuse_if_personal(d, a.doctor)

    cut = 400
    out = {
        "schema": "horizun.release-evidence/1",
        "release": a.release,
        "release_commit": a.release_commit,
        "inventory_commit": a.inventory_commit,
        "consolidated_utc": a.consolidated_utc,
        "consolidated_from": "artifacts/live in the integration worktree of branch codex/model-doctor-wall-integration (gitignored); copied by scripts/consolidate-release-evidence.py, nothing recomputed",
        "truncation": "observed/reason/evidence strings are cut at " + str(cut) + " characters; the runner's artifact holds the full text",
        "code_identity_note": "git diff --stat b08b7a2 1b3575c -- src and a792663 1b3575c -- src are both EMPTY: the release commit changed only the version stamp, CHANGELOG and docs. The binaries measured below therefore carry the same source as the release, but their product version stamp reads 1.1.6 because Directory.Build.props moved to 1.2.0 one commit later. No binary stamped 1.2.0 has been measured live.",
        "campaigns": {
            "wall_layer_decomposition": {
                "harness": "scripts/live/wallsplit-matrix.ps1",
                "run_id": w["run_id"],
                "recorded_utc": w["recorded_utc"],
                "identity": w["identity"],
                "tolerance_mm": w["tolerance_mm"],
                "totals": {k: w[k] for k in (
                    "total", "passed", "failed", "unverified", "not_run", "blocked_fixture",
                    "blocked_environment", "unsupported_api", "executed", "executed_pass_rate",
                    "coverage_rate", "verified_pass_rate")},
                "status_meaning": {
                    "passed": "executed and verified after commit against the expectation",
                    "blocked_fixture": "the fixture this case needs does not exist on the machine; NOT a pass",
                    "blocked_environment": "needs a second user / multiuser environment; NOT a pass",
                    "unsupported_api": "a public Revit API limit measured and recorded; NOT a pass",
                },
                "cases": [{
                    "case": c["case"], "name": c["name"], "status": c["status"],
                    "expected": c.get("expected"),
                    "observed": (c.get("observed") or "")[:cut],
                    "bucket": c.get("bucket"),
                    "reason": (c.get("because") or "")[:cut],
                    "recorded_utc": c.get("recorded_utc"),
                } for c in w["case_results"]],
                "notes": w.get("notes", []),
            },
            "model_doctor": {
                "harness": d["harness_file"],
                "run_id": d["run_id"],
                "generated_utc": d["generated_utc"],
                "identity": {
                    "horizun_commit": d["code_candidate_commit"], "repo_head": d["repo_head"],
                    "repo_tracked_clean": d["repo_tracked_clean"], "horizun_version": d["horizun_version"],
                    "built_from_clean_tree": d.get("built_from_clean_tree"),
                    "revit_year": d["revit_year"], "revit_build": d["revit_build"],
                    "server_sha256": d["server_sha256"], "addin_sha256": d["addin_sha256"],
                    "contract_hash": d["contract_hash"], "tool_count": d["tool_count"],
                    "document": d["document"], "open_document_count": d.get("open_document_count"),
                    "harness_sha256": d["harness_sha256"], "harness_git_blob": d["harness_git_blob"],
                },
                "totals": {k: d[k] for k in (
                    "passed", "failed", "unverified", "not_covered", "fixture_missing", "not_assessable",
                    "not_applicable", "available", "implemented_not_live_verified", "calls_made")},
                "counting_rule": d["counting_rule"],
                "probes": [{
                    "id": p.get("id"), "title": p.get("title") or p.get("name"), "status": p.get("status"),
                    "evidence": (json.dumps(p.get("evidence"))[:cut] if p.get("evidence") is not None else None),
                    "reason": (p.get("reason") or p.get("note") or "")[:cut],
                } for p in d["probes"]],
            },
        },
    }
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("wrote", a.out, os.path.getsize(a.out), "bytes")


if __name__ == "__main__":
    main()
