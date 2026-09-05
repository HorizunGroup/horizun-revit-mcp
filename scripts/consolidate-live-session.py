#!/usr/bin/env python3
# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# Consolidate one development live session into docs/evidence: the campaign and
# harness manifests (schema horizun.live-evidence/2) plus the ad-hoc smoke calls
# made through scripts/hz-call.ps1, each bound to the commit, server SHA and
# Revit build that answered. Nothing is recomputed: counts and statuses are
# copied from the manifests; smoke calls are summarised by the fields named
# here and truncated, and the truncation is stated.
#
#   python scripts/consolidate-live-session.py --session <dir> --out docs/evidence/<name>.json
#
# <dir> is the scratch directory the session wrote into: any *.json manifest
# under it whose "schema" is horizun.live-evidence/2 is a harness run; a
# smoke/ subfolder holds hz-call replies (*.out.json). Personal paths are
# refused, never rewritten.
# -----------------------------------------------------------------------------
import argparse
import glob
import json
import os
import re
import sys

PERSONAL = re.compile(r"[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9._-]+", re.IGNORECASE)

# A path also arrives FLATTENED into a directory name: an agent scratchpad names
# its session folder "C--Users-<account>-<repo>", which carries the same account
# name straight past a pattern written for separators. That is how a personal name
# reached an evidence file a separator-shaped redaction had already "cleaned".
# Redact the flattened shape too, and the bare account name of the machine that ran
# the sweep: a record needs to say WHERE a disposable file was, never who owns the
# profile.
FLATTENED = re.compile(r"[A-Za-z]--Users-[A-Za-z0-9._]+-", re.IGNORECASE)
_ACCOUNT = os.path.basename(os.environ.get("USERPROFILE", "")) or os.environ.get("USERNAME", "")
ACCOUNT = re.compile(re.escape(_ACCOUNT), re.IGNORECASE) if len(_ACCOUNT) >= 3 else None


def redact(text):
    text = PERSONAL.sub("<user-profile>", text)
    text = FLATTENED.sub("<user-profile-dir>-", text)
    if ACCOUNT is not None:
        text = ACCOUNT.sub("<account>", text)
    return text
CUT = 400


def scrub_check(obj, label):
    text = json.dumps(obj)
    hits = sorted(set(PERSONAL.findall(text) + FLATTENED.findall(text)))
    if hits:
        sys.exit(label + " names a personal path and is refused: " + ", ".join(hits[:5]))


def reply_of(path):
    d = json.load(open(path, encoding="utf-8"))
    raw = d.get("raw") or ""
    body = None
    if d.get("result") and isinstance(d["result"], dict) and d["result"].get("structuredContent"):
        body = d["result"]["structuredContent"]
    else:
        try:
            body = json.loads(raw)
        except Exception:
            # some replies carry a second document after the first; take the first
            try:
                body = json.JSONDecoder().raw_decode(raw)[0]
            except Exception:
                body = None
    return d, body, raw


def self_test():
    """Prove the redaction on the two shapes an account name really travels in.

    Run as: python scripts/consolidate-live-session.py --self-test
    A leak of the flattened shape is what put a personal name into a published
    evidence file once; this is the assertion that says it cannot happen quietly
    again.
    """
    account = "someone"
    cases = [
        r"C:\Users\someone\AppData\Local\Temp\x.xlsx",
        "C:/Users/someone/AppData/Local/Temp/x.xlsx",
        r"C:\Temp\claude\C--Users-someone-horizun-mcp\run\a.json",
        "/tmp/claude/D--Users-someone-repo/run/a.json",
    ]
    failed = []
    for c in cases:
        got = PERSONAL.sub("<user-profile>", c)
        got = FLATTENED.sub("<user-profile-dir>-", got)
        if account in got.lower():
            failed.append(c + " -> " + got)
    if failed:
        sys.exit("redaction leaked the account name: " + "; ".join(failed))
    # And the shape must not eat an ordinary evidence string.
    keep = "runs/HZ_M2026.rvt at commit 36dd4f8"
    out = FLATTENED.sub("<user-profile-dir>-", PERSONAL.sub("<user-profile>", keep))
    if out != keep:
        sys.exit("redaction rewrote an ordinary evidence string: " + out)
    print("[PASS] evidence redaction covers separator and flattened account paths")
    # --- the consolidator must REFUSE, not summarise, when the record is not
    # countable. Each case below is a way a total has been wrong before.
    def run(**kw):
        base = {
            "run_id": "r1", "harness": "scripts/live/verify-x.ps1", "code_candidate_commit": "abc123",
            "contract_hash": "c", "server_sha256": "s", "addin_sha256": "a",
            "revit_year": "2026", "revit_build": "26.0", "repo_tracked_clean": True, "document": "HZ_M2026",
            "totals": {"passed": 1}, "probes": [{"id": "X1", "status": "passed"}],
        }
        base.update(kw)
        return base

    def refuses(label, runs, repeats=()):
        problems = []
        check_vocabulary(runs, problems)
        check_completeness(runs, problems)
        check_totals(runs, problems)
        build_coverage(runs, set(repeats), problems)
        if not problems:
            sys.exit("the consolidator ACCEPTED " + label + ", which it must refuse")
        return problems

    refuses("an unknown probe status", [run(probes=[{"id": "X1", "status": "kind-of-passed"}],
                                            totals={"passed": 0})])
    refuses("a run with no add-in hash", [run(addin_sha256=None)])
    refuses("a run measured on a dirty tree", [run(repo_tracked_clean=False)])
    refuses("totals that disagree with the probes", [run(totals={"passed": 7})])
    refuses("an undeclared repetition", [run(run_id="r1"), run(run_id="r2")])
    ok0 = []
    check_vocabulary([run(run_id="r1"), run(run_id="r2", document="OTHER")], ok0)
    other = build_coverage([run(run_id="r1"), run(run_id="r2", document="OTHER")], set(), ok0)
    if ok0 or other["unique_cases"] != 2:
        sys.exit("the same probe against a DIFFERENT document was read as a repetition: %r" % other)

    ok = []
    check_vocabulary([run()], ok); check_completeness([run()], ok); check_totals([run()], ok)
    cov = build_coverage([run(run_id="r1"), run(run_id="r2")], {"verify-x.ps1/2026/HZ_M2026/X1"}, ok)
    if ok:
        sys.exit("the consolidator refused a sound record: " + "; ".join(ok))
    if cov["unique_cases"] != 1 or cov["results_recorded"] != 2:
        sys.exit("a declared repetition was counted as coverage: %r" % cov)
    # --- WHICH RUN CARRIES THE ACCEPTANCE. Being the latest is not a qualification.
    dirty_last = [run(run_id="clean1", generated_utc="2026-01-01T00:00:00Z"),
                  run(run_id="dirty2", generated_utc="2026-01-02T00:00:00Z", repo_tracked_clean=False)]
    problems2 = []
    check_vocabulary(dirty_last, problems2)
    cov2 = build_coverage(dirty_last, {"verify-x.ps1/2026/HZ_M2026/X1"}, problems2)
    chosen = cov2["matrix"]["verify-x.ps1"]["2026"]["HZ_M2026"]["artifact"]
    if chosen != "clean1":
        sys.exit("a run measured on a dirty tree was chosen over a clean one: %r" % chosen)

    other_candidate = [run(run_id="mine", code_candidate_commit="aaaa111", generated_utc="2026-01-01T00:00:00Z"),
                       run(run_id="theirs", code_candidate_commit="bbbb222", generated_utc="2026-01-02T00:00:00Z")]
    problems3 = []
    cov3 = build_coverage(other_candidate, {"verify-x.ps1/2026/HZ_M2026/X1"}, problems3, candidates=("aaaa",))
    if cov3["matrix"]["verify-x.ps1"]["2026"]["HZ_M2026"]["artifact"] != "mine":
        sys.exit("a run at a candidate this acceptance does not name was chosen: %r" % cov3)
    if problems3:
        sys.exit("a case WITH a qualifying run was reported as unacceptable: %s" % problems3)

    problems4 = []
    build_coverage([run(run_id="theirs", code_candidate_commit="bbbb222")], set(), problems4, candidates=("aaaa",))
    if not problems4:
        sys.exit("a case whose only run is at an unnamed candidate was accepted anyway")

    # --- A TITLE REVIT INVENTED IS NOT AN IDENTITY.
    if scenario_of("HZ_CLOSED_L_detached_1") != "HZ_CLOSED_L" or scenario_of("HZ_M2026") != "HZ_M2026":
        sys.exit("the scenario of a detached title is wrong: %r" % scenario_of("HZ_CLOSED_L_detached_1"))
    problems5 = []
    same = build_coverage([run(run_id="a", document="HZ_CLOSED_L_detached", generated_utc="2026-01-01T00:00:00Z"),
                           run(run_id="b", document="HZ_CLOSED_L_detached_1", generated_utc="2026-01-02T00:00:00Z")],
                          {"verify-x.ps1/2026/HZ_CLOSED_L/X1"}, problems5)
    if same["unique_cases"] != 1 or same["results_recorded"] != 2:
        sys.exit("two openings of one scenario were counted as two cases: %r" % same)
    print("[PASS] the accepted run is the latest that QUALIFIES, and a title Revit invented is not new coverage")

    print("[PASS] the consolidator refuses unknown states, incomplete records, "
          "disagreeing totals and undeclared repetitions; a declared repetition counts as ONE case")



# The nine buckets a probe may land in. A status outside this set is not a new
# kind of result, it is a harness writing something nobody agreed to count, and
# a consolidator that passes it through turns an unknown into a total.
KNOWN_STATUSES = {
    "passed", "failed", "unverified", "not_covered", "fixture_missing",
    "not_assessable", "not_applicable", "available", "implemented_not_live_verified",
}

# What a run must say about itself before its numbers may be added to anything.
# A run missing any of these is not a smaller run; it is a run whose results
# cannot be attributed to a binary - and those are the results that later get
# quoted as if they had been.
REQUIRED_RUN_FIELDS = (
    "run_id", "harness", "code_candidate_commit", "contract_hash",
    "server_sha256", "addin_sha256", "revit_year", "revit_build",
)


# HOW MUCH A RESULT IS WORTH, kept apart from what the result IS.
#
# A probe's status says what happened. It does not say how the evidence was
# obtained, and it does not say what would change it - and pooling those three
# into one word is how "fixture_missing" came to mean both "an ACC model nobody
# here can open" and "Revit did not happen to throw". The three fields:
#
#   result_status  - the probe's own answer, from the probe.
#   evidence_level - live (measured in a running Revit), structural (the real
#                    functions, offline, with the one thing Revit supplies
#                    substituted), offline (unit-level), documentary.
#   blocker_kind   - what stands between here and a pass: an external_resource,
#                    something locally_constructible, a condition merely
#                    not_observed, a property api_not_observable, or none.
EVIDENCE_LIVE = "live"
EVIDENCE_STRUCTURAL = "structural"
EVIDENCE_OFFLINE = "offline"
EVIDENCE_DOCUMENTARY = "documentary"

BLOCKER_EXTERNAL = "external_resource"
BLOCKER_LOCAL = "locally_constructible"
BLOCKER_NOT_OBSERVED = "not_observed"
BLOCKER_API = "api_not_observable"
BLOCKER_NONE = "none"

EVIDENCE_LEVELS = {EVIDENCE_LIVE, EVIDENCE_STRUCTURAL, EVIDENCE_OFFLINE, EVIDENCE_DOCUMENTARY}
BLOCKER_KINDS = {BLOCKER_EXTERNAL, BLOCKER_LOCAL, BLOCKER_NOT_OBSERVED, BLOCKER_API, BLOCKER_NONE}

# Every probe that does not pass must be classified HERE, by hand, with the
# reason a reader can check. An unclassified one fails the consolidation rather
# than defaulting to anything.
CLASSIFICATION = {
    ("verify-doctor-corrections.ps1", "D9.2"): (
        EVIDENCE_STRUCTURAL, BLOCKER_NOT_OBSERVED,
        "the six situations of rollback_scope: per_action are proved over the real apply loop with the step "
        "executor substituted (CorrectionApplyLoopTests). What is not reproduced is Revit failing one action "
        "INSIDE a confirmed plan: everything inducible from outside invalidates the token and the whole call is "
        "refused first, measured live in every year."),
    ("verify-doctor-corrections.ps1", "D11.3"): (
        EVIDENCE_LIVE, BLOCKER_EXTERNAL,
        "a correction on an element owned by SOMEBODY ELSE. One machine cannot borrow from itself; this needs a "
        "second Revit user on the central."),
    ("verify-quantities-budget.ps1", "Q4.1"): (
        EVIDENCE_STRUCTURAL, BLOCKER_NOT_OBSERVED,
        "the translation of a failed read into `unreadable` - never absent, never a zero, identity preserved - is "
        "proved over the real function with a substituted measurement (TakeoffUnreadableReadingTests). What is not "
        "reproduced is Revit's own geometry read throwing; nothing is missing from this machine."),
    ("verify-quantities-budget.ps1", "Q4.2"): (
        EVIDENCE_LIVE, BLOCKER_API,
        "MEASURED live: a link loaded with a workset closed reports every workset open AND hands over that "
        "workset's elements, identical to the same type reloaded with everything open. The API exposes no way to "
        "read back a link's load configuration, so the effective state is not observable - by anyone, with any "
        "fixture."),
    ("verify-quantities-budget.ps1", "Q3.8"): (
        EVIDENCE_LIVE, BLOCKER_EXTERNAL,
        "an authorised Power BI test workspace and dataset, plus credentials in the environment. A productive "
        "workspace is not a substitute and a dry run is not a send."),
    ("verify-worksharing-fixtures.ps1", "W6"): (
        EVIDENCE_LIVE, BLOCKER_EXTERNAL,
        "an element held by ANOTHER user. The census that would read it is proved on a real workshared model; "
        "what is missing is the second user."),
    ("verify-worksharing-fixtures.ps1", "W7"): (
        EVIDENCE_LIVE, BLOCKER_EXTERNAL,
        "an ACC model this machine is entitled to open and whose model is disposable. The only ACC projects this "
        "account reaches belong to a client."),
}


def classify(harness, probe_id, status):
    """The three fields for one case. A passing case needs no entry."""
    if status == "passed":
        return EVIDENCE_LIVE, BLOCKER_NONE, None
    return CLASSIFICATION.get((harness, probe_id), (None, None, None))


def check_classification(problems):
    for (harness, probe), (level, kind, why) in CLASSIFICATION.items():
        if level not in EVIDENCE_LEVELS:
            problems.append("%s/%s declares evidence_level %r, which is not one of %s"
                            % (harness, probe, level, sorted(EVIDENCE_LEVELS)))
        if kind not in BLOCKER_KINDS:
            problems.append("%s/%s declares blocker_kind %r, which is not one of %s"
                            % (harness, probe, kind, sorted(BLOCKER_KINDS)))
        if not why:
            problems.append("%s/%s is classified without a reason" % (harness, probe))


def check_vocabulary(runs, problems):
    for r in runs:
        for q in r["probes"]:
            if q.get("status") not in KNOWN_STATUSES:
                problems.append("run %s probe %s has status %r, which is not one of the nine buckets"
                                % (r.get("run_id"), q.get("id"), q.get("status")))
            if not q.get("id"):
                problems.append("run %s has a probe with no id" % r.get("run_id"))


def check_completeness(runs, problems):
    for r in runs:
        missing = [f for f in REQUIRED_RUN_FIELDS if not r.get(f)]
        if missing:
            problems.append("run %s is incomplete: %s"
                            % (r.get("run_id") or r.get("harness"), ", ".join(missing)))
        if not r["probes"]:
            problems.append("run %s recorded no probes at all" % r.get("run_id"))
        # A DIRTY TREE is not a missing field, it is a disqualification: the run
        # is kept as history and `eligible` refuses to let it carry an
        # acceptance. A session fails on it only when a case has nothing else,
        # which cases_without_accepted_run reports by name.


def check_totals(runs, problems):
    """A run's own totals must equal what its probes say. The two are written by
    different code paths, and a disagreement means one of them is wrong."""
    for r in runs:
        counted = {}
        for q in r["probes"]:
            counted[q["status"]] = counted.get(q["status"], 0) + 1
        for bucket, declared in (r.get("totals") or {}).items():
            if bucket == "calls_made" or declared is None:
                continue
            if int(declared) != counted.get(bucket, 0):
                problems.append("run %s declares %s=%s but its probes count %d"
                                % (r.get("run_id"), bucket, declared, counted.get(bucket, 0)))


# Revit renames a document it opens detached, and numbers the second copy of a
# name it already has: HZ_CLOSED_L becomes HZ_CLOSED_L_detached, and the next one
# in the same session becomes HZ_CLOSED_L_detached_1. Those are the SAME scenario
# measured twice, and keying cases on the title as it happened to read would have
# counted the repetition as new coverage.
_TEMPORARY_TITLE = re.compile(r"(_detached)?(_\d+)?$", re.IGNORECASE)


def scenario_of(title):
    """The stable name of the model a case ran against."""
    if not title:
        return "unknown"
    t = str(title)
    while True:
        stripped = _TEMPORARY_TITLE.sub("", t, count=1)
        if stripped == t or not stripped:
            return t
        t = stripped


def case_key(run, probe):
    """What makes a result a DISTINCT case rather than a repetition: the harness,
    the Revit year, the SCENARIO it ran against, and the probe id. Two runs of one
    harness in one year on one scenario measure the same cases twice - that is
    coverage of those cases, not of twice as many. The scenario is part of the key
    because the same probe against a workshared model and against a single-user
    one are two different measurements, and collapsing them would let the second
    silently replace the first; it is the scenario rather than the title because a
    title Revit invented for one session is not an identity."""
    harness = (run.get("harness") or "").replace(chr(92), "/").split("/")[-1]
    return (harness, str(run.get("revit_year")), scenario_of(run.get("document")), probe.get("id"))


def eligible(row, candidates, contract):
    """Why a run may or may not carry an acceptance. Being the latest is not a
    qualification: a run measured on a dirty tree, at a candidate this delivery
    does not accept, or against another contract, is history."""
    if row.get("clean") is False:
        return "measured on a dirty tree"
    if candidates and not any(str(row.get("candidate") or "").startswith(c) for c in candidates):
        return "candidate %s is not one this acceptance names" % str(row.get("candidate"))[:12]
    if contract and row.get("contract") != contract:
        return "contract %s is not %s" % (row.get("contract"), contract)
    return None


def build_coverage(runs, accepted_repeats, problems, candidates=(), contract=None):
    cases = {}
    for r in runs:
        for q in r["probes"]:
            cases.setdefault(case_key(r, q), []).append({
                "run_id": r.get("run_id"), "status": q.get("status"),
                "generated_utc": r.get("generated_utc"),
                "candidate": r.get("code_candidate_commit"), "addin_sha256": r.get("addin_sha256"),
                "contract": r.get("contract_hash"), "clean": r.get("repo_tracked_clean"),
                "document": r.get("document"), "server_sha256": r.get("server_sha256"),
                "addin_unsigned_sha256": r.get("addin_unsigned_sha256"),
                "artifact": r.get("artifact_path"),
            })
    # LAST means latest, not last read off the disk. Sorting by the file name put
    # an older run after a newer one and the older result was then the accepted
    # one - the exact way a superseded number gets published.
    for rows in cases.values():
        rows.sort(key=lambda x: x.get("generated_utc") or "")

    repeats = {}
    for k, rows in sorted(cases.items()):
        if len(rows) > 1:
            label = "%s/%s/%s/%s" % k
            repeats[label] = rows
            if label not in accepted_repeats and "*" not in accepted_repeats:
                statuses = sorted(set(x["status"] for x in rows))
                problems.append(
                    "case %s was measured %d times (%s) and the repetition was not declared: pass "
                    "--accept-repeat to name the accepted run" % (label, len(rows), ", ".join(statuses)))

    matrix = {}
    accepted_row = {}
    unacceptable = []
    for key, rows in cases.items():
        harness, year, scenario, probe_id = key
        # The ACCEPTED result of a case is the LATEST run that QUALIFIES - clean
        # tree, a candidate this acceptance names, the right contract. A run that
        # does not qualify stays as history and says why it was not chosen.
        chosen = None
        for row in reversed(rows):
            if eligible(row, candidates, contract) is None:
                chosen = row
                break
        if chosen is None:
            unacceptable.append({
                "case": "%s/%s/%s/%s" % key,
                "runs": [{"run_id": x.get("run_id"), "why_not": eligible(x, candidates, contract)} for x in rows],
            })
            continue
        accepted_row[key] = chosen
        level, kind, why = classify(harness, probe_id, chosen["status"])
        if level is None:
            problems.append("case %s/%s/%s/%s is %s and nothing classifies it: add it to CLASSIFICATION"
                            % (harness, year, scenario, probe_id, chosen["status"]))
            level, kind, why = None, None, None
        chosen["evidence_level"] = level
        chosen["blocker_kind"] = kind
        chosen["why_not_passed"] = why
        matrix.setdefault(harness, {}).setdefault(year, {}).setdefault(scenario, {})[probe_id] = chosen

    if unacceptable:
        problems.append("%d case(s) have no run this acceptance can stand on; the first is %s"
                        % (len(unacceptable), unacceptable[0]["case"]))

    per_cell = {}
    for harness, years in matrix.items():
        per_cell[harness] = {}
        for year, scenarios in years.items():
            per_cell[harness][year] = {}
            for scenario, probes in scenarios.items():
                counts = {}
                for row in probes.values():
                    counts[row["status"]] = counts.get(row["status"], 0) + 1
                # WHAT THE CELL DOES NOT COVER, named rather than summarised: the
                # probe ids that did not pass, and the artifact they came from.
                limitations = sorted(pid for pid, row in probes.items() if row["status"] != "passed")
                artifact = None
                for key, chosen in accepted_row.items():
                    if key[0] == harness and key[1] == year and key[2] == scenario:
                        artifact = chosen.get("run_id")
                        break
                per_cell[harness][year][scenario] = {
                    "counts": counts, "artifact": artifact,
                    "limitations": [{
                        "probe": pid, "status": probes[pid]["status"],
                        "evidence_level": probes[pid].get("evidence_level"),
                        "blocker_kind": probes[pid].get("blocker_kind"),
                        "why": probes[pid].get("why_not_passed"),
                    } for pid in limitations],
                }

    totals = {}
    for chosen in accepted_row.values():
        st = chosen["status"]
        totals[st] = totals.get(st, 0) + 1

    cases_flat = []
    for (harness, year, scenario, probe_id), chosen in sorted(accepted_row.items()):
        cases_flat.append({
            "harness": harness, "revit_year": year, "scenario": scenario, "probe": probe_id,
            "result_status": chosen["status"],
            "evidence_level": chosen.get("evidence_level"),
            "blocker_kind": chosen.get("blocker_kind"),
            "why_not_passed": chosen.get("why_not_passed"),
            "candidate": chosen.get("candidate"), "contract_hash": chosen.get("contract"),
            "server_sha256": chosen.get("server_sha256"), "addin_sha256": chosen.get("addin_sha256"),
            "addin_unsigned_sha256": chosen.get("addin_unsigned_sha256"),
            "run_id": chosen.get("run_id"), "artifact": chosen.get("artifact"),
            "document": chosen.get("document"),
        })

    by_blocker = {}
    for row in cases_flat:
        if row["result_status"] == "passed":
            continue
        by_blocker.setdefault(row["blocker_kind"] or "unclassified", []).append(
            "%s/%s %s (%s)" % (row["harness"], row["probe"], row["revit_year"], row["result_status"]))

    return {
        "cases": cases_flat,
        "by_blocker_kind": {k: sorted(v) for k, v in sorted(by_blocker.items())},
        "means": ("one CASE is one probe id, of one harness, in one Revit year, against one SCENARIO - the stable "
                  "name of the model, not the title Revit invented for a detached copy. A case measured twice is "
                  "still one case; its accepted result is the LATEST run that QUALIFIES - clean tree, a candidate "
                  "this acceptance names, the right contract - and the others stay as history. The totals here "
                  "count cases; the per-run totals count executions, and the two are not the same number."),
        "accepted_candidates": list(candidates),
        "required_contract": contract,
        "unique_cases": len(cases),
        "results_recorded": sum(len(v) for v in cases.values()),
        "repeated_cases": sorted(repeats.keys()),
        "history_only_runs": sorted({row["run_id"] for rows in cases.values() for row in rows
                                     if eligible(row, candidates, contract) is not None}),
        "cases_without_accepted_run": unacceptable,
        "case_totals": totals,
        "matrix": per_cell,
    }


def group_by_binary(runs):
    """Results are grouped by the bytes that produced them. Two groups are never
    added together without saying so."""
    groups = {}
    for r in runs:
        key = "|".join([str(r.get("code_candidate_commit")), str(r.get("server_sha256")),
                        str(r.get("addin_sha256"))])
        g = groups.setdefault(key, {
            "code_candidate_commit": r.get("code_candidate_commit"),
            "server_sha256": r.get("server_sha256"),
            "addin_sha256": r.get("addin_sha256"),
            "revit_years": [], "runs": [], "totals": {},
        })
        if str(r.get("revit_year")) not in g["revit_years"]:
            g["revit_years"].append(str(r.get("revit_year")))
        g["runs"].append(r.get("run_id"))
        for q in r["probes"]:
            g["totals"][q["status"]] = g["totals"].get(q["status"], 0) + 1
    return list(groups.values())


def main():
    if "--self-test" in sys.argv[1:]:
        self_test()
        return
    ap = argparse.ArgumentParser()
    ap.add_argument("--session", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--label", default="development session")
    ap.add_argument("--accept-candidate", action="append", default=[],
                    help="a commit (or prefix) whose runs may carry an acceptance. Given none, any candidate "
                         "qualifies and the record says so.")
    ap.add_argument("--require-contract", default=None,
                    help="the contract hash an accepted run must carry.")
    ap.add_argument("--accept-repeat", action="append", default=[],
                    help="harness/year/probe measured more than once on purpose, or * to accept every "
                         "repetition. The LAST run of a repeated case is the accepted one.")
    a = ap.parse_args()

    runs = []
    for p in sorted(glob.glob(os.path.join(a.session, "**", "*.json"), recursive=True)):
        try:
            d = json.load(open(p, encoding="utf-8"))
        except Exception:
            continue
        if not isinstance(d, dict) or d.get("schema") != "horizun.live-evidence/2":
            continue
        runs.append({
            "harness": d.get("harness_file"),
            "run_id": d.get("run_id"),
            "generated_utc": d.get("generated_utc"),
            "code_candidate_commit": d.get("code_candidate_commit"),
            "repo_tracked_clean": d.get("repo_tracked_clean"),
            "revit_year": d.get("revit_year"), "revit_build": d.get("revit_build"),
            "horizun_version": d.get("horizun_version"),
            "server_sha256": d.get("server_sha256"), "addin_sha256": d.get("addin_sha256"),
            "contract_hash": d.get("contract_hash"), "tool_count": d.get("tool_count"),
            "document": d.get("document"),
            "artifact_path": os.path.relpath(p, a.session).replace(chr(92), "/"),
            "addin_unsigned_sha256": d.get("addin_unsigned_sha256"),
            "totals": {k: d.get(k) for k in (
                "passed", "failed", "unverified", "not_covered", "fixture_missing", "not_assessable",
                "not_applicable", "available", "implemented_not_live_verified", "calls_made")},
            "probes": [{
                "id": q.get("id"), "title": (q.get("title") or q.get("name") or "")[:CUT],
                "status": q.get("status"), "observed": (q.get("observed") or "")[:CUT],
                "reason": (q.get("reason") or q.get("because") or "")[:CUT],
            } for q in d.get("probes") or []],
            "notes": [str(n)[:CUT] for n in (d.get("notes") or [])],
        })

    smoke = []
    for p in sorted(glob.glob(os.path.join(a.session, "smoke", "*.out.json"))) + \
             sorted(glob.glob(os.path.join(a.session, "budget", "*.out.json"))) + \
             sorted(glob.glob(os.path.join(a.session, "health-*.json"))):
        d, body, raw = reply_of(p)
        summary = {}
        if isinstance(body, dict):
            for k in ("status", "horizun_commit", "built_from_clean_tree", "revit_version", "revit_build", "registry",
                      "active_document", "confirmed_active", "mode", "rows_matching", "truncated", "coverage",
                      "document_fingerprint", "finding_set_fingerprint", "dry_run", "executed", "rollback_scope",
                      "skipped", "tally", "re_audit", "saved", "prevention", "idempotency", "replayed",
                      "destinations", "code", "path", "allowed", "did_you_mean"):
                if k in body:
                    summary[k] = json.loads(json.dumps(body[k])[:CUT * 3] if len(json.dumps(body[k])) <= CUT * 3
                                            else json.dumps(str(body[k])[:CUT * 3]))
            if "comparison" in body and isinstance(body["comparison"], dict):
                summary["comparison_summary"] = body["comparison"].get("summary")
        smoke.append({
            "call": os.path.basename(p),
            "tool": d.get("tool"),
            "called_utc": d.get("called_utc"),
            "server_sha256": d.get("server_sha256"),
            "is_error": d.get("is_error"),
            "error": (raw[:CUT] if d.get("is_error") else None),
            "summary": summary,
            "truncation": "field values cut at " + str(CUT * 3) + " characters",
        })

    # Smoke replies quote the scratch paths they were given (a disposable
    # workbook under the profile). Those are sanitised to a placeholder rather
    # than refused: the record needs to say WHERE the disposable file was, and
    # a user-profile prefix is not evidence of anything.
    smoke = json.loads(redact(json.dumps(smoke)))

    problems = []
    check_classification(problems)
    check_vocabulary(runs, problems)
    check_completeness(runs, problems)
    check_totals(runs, problems)
    coverage = build_coverage(runs, set(a.accept_repeat), problems,
                              candidates=tuple(a.accept_candidate), contract=a.require_contract)
    if problems:
        sys.exit("the session was NOT consolidated; %d problem(s):\n  - %s"
                 % (len(problems), "\n  - ".join(problems)))

    out = {
        "schema": "horizun.live-session-evidence/1",
        "label": a.label,
        "consolidated_from": "the scratch directory of one scripts/live/dev-addin-session.ps1 session; copied by scripts/consolidate-live-session.py, nothing recomputed",
        "harness_runs": runs,
        "coverage": coverage,
        "binary_groups": group_by_binary(runs),
        "smoke_calls": smoke,
    }
    scrub_check(out, "the consolidated record")
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("wrote", a.out, "runs", len(runs), "smoke calls", len(smoke),
          "unique cases", coverage["unique_cases"], "results", coverage["results_recorded"])


if __name__ == "__main__":
    main()
