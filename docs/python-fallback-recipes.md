# Python fallback recipes

How to turn a missing typed capability into a minimal, transactional, verified
`horizun_execute_python` script. This is the companion to the fallback policy in
the server instructions: **typed first, Python as the fallback — never "not
supported"**.

The engine is IronPython 3 with the standard library bundled (`json`, `re`,
`csv`, `datetime`, `math`). `doc`, `uidoc`, `uiapp` and `app` are injected;
`checkpoint(label, done, total)` is available without import for long runs.
Element ids are 64-bit in Revit 2024+: wrap `ElementId.Value` in `int()` before
serializing.

## 1. When to reach for a recipe

1. **A typed command covers the whole case** → use the typed command. It
   rehearses (`dry_run`), verifies, and re-reads after commit; nothing here can
   match that.
2. **A failed typed call returned `fallback.allowed: true`** → write the minimal
   script for exactly the uncovered part and run it.
3. **Anything else** → do **not** fall back. No `fallback` block, or
   `allowed: false`, means the failure was a fixable argument, a Revit error, or
   a write that may already have landed. A Python retry of a half-finished write
   is a second write, not a recovery.

**Decide on the block, never on the wording.** A failed typed call may carry:

```json
"fallback": {
  "recommended_tool": "horizun_execute_python",
  "allowed": true,
  "reason": "unsupported_kind",
  "write_started": false
}
```

**It arrives on the first, ordinary call.** `dry_run` defaults to `true`, and the
rehearsal publishes the verdict in `structuredContent` beside its own payload —
a *successful* reply carrying `invalid` rows still carries the block. You never
need to send `dry_run: false`, or an apply you have no reason to send, to learn
that Python is the way. On a typed refusal the same block arrives in
`structuredContent` and is repeated in the error text for a human.

`allowed: true` means this bridge has no typed capability for the request **and**
refused before writing anything — those two facts together are what make Python safe
here. `write_started: true` never accompanies `allowed: true`. Reasons are
`unsupported_capability`, `unsupported_operation`, `unsupported_kind`,
`unsupported_category` and `out_of_contract_combination`. An error that merely
*sounds* like a capability gap, with no block attached, is not one.

### A mixed batch never grants the fallback

The grant is a property of the **whole request**, not of one entry. A batch that
contains both an action no typed command covers and an action whose arguments
are wrong comes back with `allowed: false` and
`reason: "mixed_capability_and_invalid_input"` — because the request still holds
input you should correct, and generating a script around your own typo is not
the fix.

You do get a map of which is which:

```json
"capability_gaps": [
  { "index": 1, "reason": "unsupported_kind",
    "recommended_tool": "horizun_execute_python",
    "error": "unsupported kind 'sprinkler_head' - ..." }
]
```

Fix the invalid entries, resend the typed call, and only once every remaining
failure is a capability gap does the request earn `allowed: true`.
**`capability_gaps` is a map, not a licence.**

Optionally call the tool once with `preflight: true` (no `idempotency_key`
needed): it checks permission, document, size, script SHA-256 and syntax, and
returns advisory warnings. If the objective is unambiguous and preflight
passes, continue straight to execution — preflight is a check, not an approval
step.

## 2. The evidence contract

**Self-reported, not host-verified.** A typed command is verified *by the
bridge*: it re-reads the model after the commit, in code this repository owns.
Arbitrary Python is not re-read by anything, so the strongest state this path
can return is `self_reported_verified` — your script said it checked and
attached evidence, and nothing on the host side confirmed it. There is no
`verified` state here at all, and `host_verified` is always `false`. Say so when
you report to a user: the evidence is the script's testimony, not the bridge's
finding.

Assign `__output__` this shape. The bridge classifies it strictly and **never
upgrades**: a `verified` claim without re-read evidence is downgraded to
`completed_unverified`, and a `verified` claim *with* evidence becomes
`self_reported_verified`.

```json
{
  "status": "verified | completed_unverified | partial | failed",
  "summary": "one short sentence of what happened",
  "created_ids": [],
  "modified_ids": [],
  "deleted_ids": [],
  "verification": { "checked": true, "evidence": [] },
  "warnings": []
}
```

You write `status: "verified"` when you re-read what you wrote; the response
comes back classified as one of:

- `self_reported_verified` — you declared `verified`, set `checked: true` and
  attached non-empty `evidence`. **The host confirmed none of it.**
- `completed_unverified` — the script finished but presented nothing checkable
  (including a `verified` claim with empty evidence, which is downgraded here).
- `partial` — part of the operation happened, or only part could be verified.
- `failed` — the script's own failure report (an uncaught exception fails the
  whole command anyway).

The response also carries `script_reported_status` (what you declared, verbatim)
so classifying never destroys information.

Make the evidence worth reading. `["ok"]` and an echo of `doc.Title` satisfy the
shape and demonstrate nothing — the classifier cannot tell them from a real
re-read, which is exactly why it refuses to call any of it verified. Put the ids
and property values you actually read back from the model in there.

`print()` still works and still comes back in `printed`, but it is
compatibility output; the structured `__output__` is what a client should read.

## 3. The reusable template

Every mutating recipe is this skeleton with a different middle. It validates
the document, groups the work in a `TransactionGroup`, guarantees `Commit()` or
`RollBack()` in `finally`, re-reads what it touched, and serializes evidence.

```python
import json
from Autodesk.Revit.DB import Transaction, TransactionGroup, ElementId

result = {
    "status": "failed", "summary": "", 
    "created_ids": [], "modified_ids": [], "deleted_ids": [],
    "verification": {"checked": False, "evidence": []},
    "warnings": [],
}

# -- 1. Validate the document before touching anything. -----------------------
if doc is None or doc.IsFamilyDocument:  # adjust the guard to the task
    result["summary"] = "expected a project document and did not get one"
    __output__ = result
else:
    tg = TransactionGroup(doc, "fallback: <name the operation>")
    tg.Start()
    try:
        t = Transaction(doc, "fallback: <step>")
        t.Start()
        committed = False
        try:
            # -- 2. The actual work goes here. --------------------------------
            # new_element = ...
            # result["created_ids"].append(int(new_element.Id.Value))
            t.Commit()
            committed = True
        finally:
            if not committed and t.HasStarted() and not t.HasEnded():
                t.RollBack()   # never leave a transaction open

        # -- 3. Re-read AFTER the commit; evidence comes from the model. ------
        evidence = []
        for eid in result["created_ids"]:
            e = doc.GetElement(ElementId(eid))
            if e is None:
                result["warnings"].append("id %d not found on re-read" % eid)
            else:
                evidence.append({"id": eid, "category": e.Category.Name if e.Category else None})

        tg.Assimilate()  # one undo step; use tg.RollBack() to abort everything

        # -- 4. Classify honestly. -------------------------------------------
        if evidence and len(evidence) == len(result["created_ids"]):
            result["status"] = "verified"
            result["verification"] = {"checked": True, "evidence": evidence}
        elif evidence:
            result["status"] = "partial"
            result["verification"] = {"checked": True, "evidence": evidence}
        else:
            result["status"] = "completed_unverified"
        result["summary"] = "<what was done>, %d element(s) re-read" % len(evidence)
    except Exception as ex:
        if tg.HasStarted() and not tg.HasEnded():
            tg.RollBack()
        result["status"] = "failed"
        result["summary"] = str(ex)
    __output__ = result
```

Notes on the skeleton:

- The `finally` with `RollBack()` is not decoration. The command cannot close a
  transaction your script leaves open — the Revit API gives it no handle — and
  a run that leaves the document modifiable is reported as a **failure**, with
  the script's writes discarded by Revit's own cleanup.
- Re-reading through `doc.GetElement` after the commit is what earns
  `verified`. Evidence is what you re-read, not what you intended.
- For read-only scripts, drop the transactions and report
  `status: "verified"` with the read values as evidence — a read that shows its
  data has verified itself.

## 4. Worked examples

### 4a. A detail line in a drafting view (no typed capability)

No typed command draws a `DetailCurve`. The fallback, complete:

```python
import json
from Autodesk.Revit.DB import Transaction, Line, XYZ, ElementId

VIEW_NAME = "DET-100"           # the drafting/plan view to draw in
P0 = (0.0, 0.0)                  # feet, view plane
P1 = (10.0, 0.0)

result = {"status": "failed", "summary": "", "created_ids": [],
          "modified_ids": [], "deleted_ids": [],
          "verification": {"checked": False, "evidence": []}, "warnings": []}

from Autodesk.Revit.DB import FilteredElementCollector, View
view = None
for v in FilteredElementCollector(doc).OfClass(View):
    if not v.IsTemplate and v.Name == VIEW_NAME:
        view = v
        break

if view is None:
    result["summary"] = "view '%s' not found" % VIEW_NAME
    __output__ = result
else:
    t = Transaction(doc, "fallback: detail line in %s" % VIEW_NAME)
    t.Start()
    committed = False
    try:
        line = Line.CreateBound(XYZ(P0[0], P0[1], 0), XYZ(P1[0], P1[1], 0))
        dc = doc.Create.NewDetailCurve(view, line)
        new_id = int(dc.Id.Value)
        t.Commit()
        committed = True
    finally:
        if not committed and t.HasStarted() and not t.HasEnded():
            t.RollBack()

    if committed:
        re_read = doc.GetElement(ElementId(new_id))
        if re_read is not None:
            curve = re_read.GeometryCurve
            result["status"] = "verified"
            result["created_ids"] = [new_id]
            result["verification"] = {"checked": True, "evidence": [{
                "id": new_id,
                "owner_view": int(re_read.OwnerViewId.Value),
                "length_ft": curve.Length,
            }]}
            result["summary"] = "detail line %d drawn in '%s'" % (new_id, VIEW_NAME)
        else:
            result["status"] = "completed_unverified"
            result["summary"] = "commit succeeded but the line could not be re-read"
    __output__ = result
```

### 4b. Reading a parameter no typed tool projects

Read-only: no transaction, and the read values are their own evidence.

```python
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory

PARAM = "Fire Rating"            # any parameter name
rows, missing = [], 0
for w in (FilteredElementCollector(doc)
          .OfCategory(BuiltInCategory.OST_Walls)
          .WhereElementIsNotElementType()):
    p = w.LookupParameter(PARAM)
    if p is None:
        missing += 1
    else:
        rows.append({"id": int(w.Id.Value), "value": p.AsValueString() or p.AsString()})

__output__ = {
    "status": "verified",
    "summary": "%d walls read, %d without the parameter" % (len(rows), missing),
    "created_ids": [], "modified_ids": [], "deleted_ids": [],
    "verification": {"checked": True, "evidence": rows[:50]},
    "warnings": (["evidence truncated to 50 of %d rows" % len(rows)] if len(rows) > 50 else []),
}
```

(`absent` and `empty` are different facts — count them separately, as above.)

### 4c. A transactional modification, verified

Writing a value the typed writer does not cover (say, flipping a flag on a
collection resolved by geometry). The pattern is the template's: write inside
the transaction, collect ids, **re-read after commit**, compare the re-read
value to the intended one, and let the comparison choose between `verified`,
`partial` and `completed_unverified`:

```python
# inside the template's step 2:
p = element.LookupParameter("Comments")
p.Set("checked-by-fallback")
result["modified_ids"].append(int(element.Id.Value))

# inside the template's step 3, per modified id:
e = doc.GetElement(ElementId(eid))
actual = e.LookupParameter("Comments").AsString()
evidence.append({"id": eid, "expected": "checked-by-fallback", "actual": actual,
                 "match": actual == "checked-by-fallback"})
# only all-match earns "verified"; any mismatch is "partial" with the rows shown
```

## 5. What the fallback must never do

- Duplicate a typed command wholesale. The advisory in the response names the
  typed replacement when your script calls one of those APIs; the typed command
  re-reads its work with machinery a script has to rebuild by hand. (The
  advisory scans code only: it is masked with Python's own lexer, so a mention in
  a comment, a string or a docstring does not trigger it. **Documented limit:**
  an f-string is a single string token to that lexer, so an API call inside
  `f"{...}"` is masked too and raises no advisory — the conservative direction
  for a hint, and asserted in the tests so nobody reads the tokenizer as a
  precision promise it does not make.)
- Retry a failed typed write. See §1.
- Report a Python result to a user as "verified". It is `self_reported_verified`
  at best — the script's testimony, not the bridge's finding.
- Claim `verified` without evidence. The bridge downgrades the claim; write the
  re-read instead.
- Leave a transaction open. The command detects it, fails the run, and the
  writes are lost.
- Assume rollback. There is **no automatic rollback** for Python: what your
  script committed stays committed even when a later line throws. The
  `TransactionGroup` pattern in §3 is how you make "all or nothing" true.
