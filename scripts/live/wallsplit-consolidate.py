# -*- coding: utf-8 -*-
import glob, io, json, os

base = 'artifacts/live'
run = 'artifacts/live/wallsplit-20260830-174538'

cases = []


def add(n, name, status, observed, because=None):
    cases.append({"case": n, "name": name, "status": status,
                  "observed": observed, "because": because})


applied = []
for f in sorted(glob.glob(run + '/call-*apply*.json')):
    try:
        j = json.load(io.open(f, encoding='utf-8'))
        w = (j.get('result') or {}).get('walls') or []
        if w:
            applied.append({
                "wall": w[0].get('source_wall_id'),
                "applied": w[0].get('applied'),
                "code": w[0].get('code'),
                "message": w[0].get('message'),
                "rollback_status": w[0].get('rollback_status'),
                "rollback_confirmed": w[0].get('rollback_confirmed'),
                "walls_expected": w[0].get('walls_expected'),
                "walls_produced": w[0].get('walls_produced'),
                "artifact": os.path.basename(f)})
    except Exception:
        pass

# EXACTLY the 55 cases the mandate numbers, one row each. The six location lines
# are ONE case (6) and pass only if all six do; the reveal belongs to case 20 and
# the tapered wall to case 23. Inflating them into separate rows would make the
# denominator disagree with the mandate.
ROLLED_BACK = 'eligible; the apply failed with unsupported_curve at layer 01 and the wall was rolled back, CONFIRMED by Revit'
NOT_RUN = 'the session ended before this case ran: Revit terminated at case 12 and a second restart is not authorised'

add(1, 'straight multilayer wall, single-layer core', 'failed', ROLLED_BACK)
add(2, 'core made of several layers', 'failed', ROLLED_BACK)
add(3, 'core with no Structure-function layer', 'failed', ROLLED_BACK)
add(4, 'wall with no valid core', 'passed', 'refused with no_valid_core, as required')
add(5, 'flipped wall', 'failed', ROLLED_BACK)
add(6, 'each of the six wall location lines', 'failed',
    'all six were eligible and all six failed the apply with unsupported_curve; all six rolled back, confirmed')
add(7, 'arc wall, exterior', 'failed', ROLLED_BACK)
add(8, 'arc wall, the other way', 'failed', ROLLED_BACK)
add(9, 'arc wall, flipped', 'failed', ROLLED_BACK)
add(10, 'stacked wall', 'passed', 'refused with unsupported_stacked_wall, as required')
add(11, 'top-constrained wall', 'failed', ROLLED_BACK)
add(12, 'pinned wall', 'unverified',
    'Revit terminated during this case; its journal ends inside a regeneration with no exception and no dump',
    'the process died, so the case has no answer either way')
add(13, 'door with custom parameters', 'not_covered', NOT_RUN)
add(14, 'door with a nested family', 'not_covered', NOT_RUN)
add(15, 'window with sill, head and flips', 'not_covered', NOT_RUN)
add(16, 'several doors and windows', 'not_covered', NOT_RUN)
add(17, 'rectangular opening', 'not_covered', NOT_RUN)
add(18, 'opening cut from a profile', 'not_covered',
    'no fixture: Document.Create.NewOpening(curveArray) refuses a wall host - "the hostElement is not a floor, ceiling, roof or toposolid"')
add(19, 'wall joined at both ends', 'not_covered', NOT_RUN)
add(20, 'wall sweep and reveal', 'not_covered', NOT_RUN)
add(21, 'wall with an edited elevation profile', 'not_covered',
    'no fixture: the API exposes no way to edit a wall elevation profile from a script')
add(22, 'wall attached at top or base', 'not_covered',
    'no fixture: there is no public API to attach a wall to a roof or floor')
add(23, 'slanted or tapered wall', 'not_covered',
    'the slanted fixture was built and its case did not run; Revit refused to make the tapered one on this wall type')
add(24, 'wall inside a group', 'not_covered', NOT_RUN)
add(25, 'wall in a design option', 'not_covered',
    'no fixture: the API exposes no way to create a design option and the template carries none')
add(26, 'element owned by another user', 'not_covered',
    'no fixture: a second Revit user cannot be simulated in one session and the document is not workshared')
add(27, 'fault injected after the layers are created', 'not_covered', NOT_RUN)
add(28, 'fault injected during the openings', 'not_covered', NOT_RUN)
add(29, 'fault injected during host verification', 'not_covered', NOT_RUN)
add(30, 'a second identical apply is idempotent', 'not_covered', NOT_RUN)
add(31, 'a stale plan is refused', 'not_covered', NOT_RUN)
add(32, 'mixed batch, one valid and one invalid', 'not_covered', NOT_RUN)
add(33, 'tags and dimensions on the carrier', 'not_covered', NOT_RUN)
add(34, 'insert ElementId and UniqueId preserved', 'not_covered', NOT_RUN)
add(35, 'geometry of every layer measured', 'not_covered', NOT_RUN)
for n, name in ((36, 'multilayer structural wall with a WallFoundation'),
                (37, 'wall with one bar'),
                (38, 'wall with a distributed bar set'),
                (39, 'wall with stirrups or ties'),
                (40, 'wall with AreaReinforcement'),
                (41, 'wall with PathReinforcement'),
                (42, 'wall with FabricArea or FabricSheet'),
                (43, 'wall with a different cover per face'),
                (44, 'foundation and rebar together'),
                (45, 'door and rebar together'),
                (46, 'door, foundation and rebar together'),
                (47, 'rebar deliberately outside the future core: rollback'),
                (48, 'foundation that cannot keep its alignment: rollback'),
                (49, 'reinforcement system with an unreadable member: prior refusal'),
                (50, 'a bar changed between dry run and apply: stale_plan'),
                (51, 'second apply on a structural wall: already_split'),
                (52, 'a sibling deleted: repairable_partial_state'),
                (53, 'mixed architectural and structural batch'),
                (54, 'post-commit verification of every structural object'),
                (55, 'zero duplicated objects')):
    why = NOT_RUN
    if n in (40, 41, 42):
        why = 'no fixture: this template carries no AreaReinforcementType, PathReinforcementType or Fabric types, and none resolves as a default'
    if n in (39,):
        why = 'no fixture built for stirrups; the session ended first'
    add(n, name, 'not_covered', why)

buckets = {}
for c in cases:
    buckets[c['status']] = buckets.get(c['status'], 0) + 1

summary = {
    "attempt": "second live session",
    "candidate_installed": "9a89fd71da82d82eca9ca2b572871f8021195018",
    "candidate_verified_by": "manifest Commit == HEAD, CleanTree true, 5 add-ins, 6 binaries Authenticode Valid, horizun_health reported the same commit",
    "revit": "2026, build 26.4.0.32",
    "document": "C:\\hz-live\\HZ_WALLSPLIT.rvt - disposable, created from Revit's own multi-discipline metric template, never anybody's project",
    "outcome": "second_live_defect_found",
    "first_defect_fixed_and_proved": {
        "was": "eligibility read WALL_CROSS_SECTION as an integer and refused everything != 0",
        "measured": "WallCrossSection is SingleSlanted=0, Vertical=1, Tapered=2 on Revit 2023-2027",
        "before": "21 of 21 cases refused as unsupported_cross_section",
        "after": "every wall reached the executor; the two negative cases still refuse for their own reasons"
    },
    "second_defect": {
        "code_seen": "unsupported_curve",
        "message_seen": "layer 01's curve could not be built.",
        "occurrences": len(applied),
        "diagnosis": "the executor held the LIVE LocationCurve.Curve wrapper. Converting the carrier REPLACES that curve, so every secondary layer's curve was derived from a stale reference and came back null. The carrier's own target was computed before the conversion, which is why it succeeded and everything after it failed.",
        "evidence": "geometry_class Line, exterior normal measured off the shell face and corroborated (agreement 1.0), 7 layers planned with 2 zero-width membranes correctly not materialised, carrier placed, then every layer curve null",
        "fixed_in_working_tree": True
    },
    "revit_termination": {
        "when": "during the apply of case 12 (pinned wall), 2026-08-30 17:46:05",
        "journal": "ends inside a regeneration in a Horizun.Dispatcher external event",
        "exception_recorded": False,
        "crash_dump": False,
        "conclusion": "the process terminated abruptly. The cause is NOT established. It is reported as an open finding, not attributed to the curve defect."
    },
    "atomicity_evidence": {
        "applies_attempted": len(applied),
        "applies_rolled_back": sum(1 for a in applied if a['rollback_status'] == 'RolledBack'),
        "rollbacks_confirmed": sum(1 for a in applied if a['rollback_confirmed']),
        "walls_produced_on_failure": sorted(set(a['walls_produced'] for a in applied)),
        "means": "every failed apply rolled its wall back and Revit CONFIRMED the rollback; no wall was left half-converted"
    },
    "buckets": buckets,
    "bucket_total": sum(buckets.values()),
    "cases": cases,
    "applies": applied
}

io.open(base + '/second-session-summary.json', 'w', encoding='utf-8').write(
    json.dumps(summary, indent=2, ensure_ascii=False))

print("buckets:", json.dumps(buckets))
print("total  :", sum(buckets.values()))
print("rollbacks confirmed:", summary['atomicity_evidence']['rollbacks_confirmed'],
      "of", summary['atomicity_evidence']['applies_attempted'])
