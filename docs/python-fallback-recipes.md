# Python fallback recipes

## API by year (2023–2027)

- To create a project from a template use `horizun_document_session` with
  `operation=new_project`; it rehearses by default.
- For every deliberate write use a fresh UUID as `idempotency_key`; reuse it only
  to retry the identical request. For example, generate one with
  `str(uuid.uuid4())` and keep it for retries of that request.
- The prelude binds `Autodesk`, `clr` and the names from `Autodesk.Revit.DB`.
  Explicit imports remain valid.
- Element ids: `ElementId.IntegerValue` was removed in Revit 2026 (deprecated
  since 2024) and `ElementId.Value` does not exist before 2024. Use
  `horizun.id_value(element.Id)`, which returns the number on every Revit
  2023–2027; a script that fails on either property gets this advice in its reply.
- `horizun.param_data_type(definition)` calls `GetDataType()` when present,
  otherwise reads `ParameterType`. The return types differ across API versions.
- Filter by class before reading class-specific members: check
  `isinstance(e, Wall)` before accessing `e.WallType`.
- Setting `FamilyManager.CurrentType` requires a transaction. To read values for
  each type without changing the current type, use `FamilyType.AsDouble(p)`,
  `AsInteger(p)`, `AsString(p)` or `AsElementId(p)` as appropriate.
- Write paths into JSON with `json.dump` rather than hand-building JSON strings:
  Windows backslashes can otherwise form an invalid JSON escape such as `\e`.

Portable helpers in the per-run `horizun` module:

```python
n = horizun.id_value(element.Id)  # int on 2023-2027 (no IntegerValue / Value split)
walls = horizun.of_class(elements, Wall)  # filter before reading WallType
data_type = horizun.param_data_type(definition)
value = horizun.family_type_value(family_type, family_parameter)  # dispatches by StorageType
path_literal = horizun.safe_json_path(path)  # JSON string literal, including quotes
```

`family_type_value` uses `AsDouble`, `AsInteger`, `AsString` or `AsElementId`
without changing `CurrentType`; unsupported storage types raise `ValueError`.
Prefer `json.dumps` on the entire object when building JSON. For subtransactions,
open a transaction in the same document first. `with horizun.transaction('outer'):`
starts one when needed, and a nested `with horizun.transaction('inner'):` uses a
subtransaction; the context commits on success and rolls back on error.

How to turn a missing typed capability into a minimal, transactional, verified
`horizun_execute_python` script. This is the companion to the fallback policy in
the server instructions: **typed first, Python as the fallback — never "not
supported"**.

The engine is IronPython 3 with the standard library bundled (`json`, `re`,
`csv`, `datetime`, `math`). The shared `hz_compat` module is available as a global
inside `horizun_execute_python` and can also be imported by shipped recipes. It
covers the API transitions across Revit 2023–2027; prefer its helpers over
version checks or direct access to members that moved.

```python
# ElementId is 32-bit in Revit 2023 and 64-bit from 2024. These helpers handle
# both reading an id and constructing one from an int (including large ids).
number = hz_compat.eid_int(element.Id)
element_id = hz_compat.element_id(number)

# Python 3 removed the Python 2 names still found in older snippets.
if isinstance(value, hz_compat.string_types):
    text = hz_compat.unicode(value)
large_number = hz_compat.long(number)  # alias for int in Python 3

# ForgeTypeId / UnitTypeId and legacy DisplayUnitType use distinct enum names.
millimeters = hz_compat.unit_type_id("Millimeters", "DUT_MILLIMETERS")
definition_type = hz_compat.parameter_data_type(parameter.Definition)
unit = hz_compat.parameter_unit_type(parameter)
group = hz_compat.parameter_group("Data")
```

When the replacement depends on the API loaded by the running Revit, inspect it
before choosing a member. For example:

```json
{"tool":"horizun_api_help","arguments":{"operation":"describe","type_name":"Autodesk.Revit.DB.ElementId","member_filter":"Value"}}
```

`horizun_api_help` reads the selected Revit's loaded API without changing the
model. Its answer tells you whether this Revit exposes `Value`, `IntegerValue`,
or another member; `hz_compat.eid_int` is the portable choice for scripts that
must run across years.

**Execution has no enforced runtime limit.** Python runs on Revit's UI thread.
A response timeout or cancellation stops waiting, not an already started script;
`timeout_phase=execution` and `execution_may_still_run=true` mean its outcome is
still unknown. Follow the returned job ID when available, and do not retry while
it may be running. Other Revit requests can remain queued indefinitely. A Python
trace callback can interrupt some Python loops, but a script can disable or catch
it and it cannot interrupt a native call already in progress; it is not a reliable
deadline for arbitrary scripts.

`horizun_submit_job` refuses `horizun_execute_python`; cancellation through a
job submitted by that tool therefore does not apply to Python. Python's separate
`run_async` path has its own job record and cancellation mechanism.

### Cooperative deadlines and checkpoint cancellation

Call `checkpoint()` inside each long loop. `timeout_s` and the optional
`deadline_s` count from script start; when both are present, `deadline_s` must
be no greater than `timeout_s`. Cancellation is cooperative: at a checkpoint
the bridge raises `HorizunCancelled`, unwinds open transactions and rolls back
the active document's transaction group, including transactions the script
already committed. A failed response contains `cancelled_at_checkpoint` with
the line when available, elapsed seconds, `rolled_back_verified` and
`rollback_gaps`. Verification requires a confirmed group rollback, no observed
changes in another document, and re-reading created and deleted elements.
Modified values are not compared individually: the group rollback is their
guarantee. Files and other effects outside the group are not reverted by it.

The bridge cannot interrupt a native call or a script that never reaches a
checkpoint. If `timeout_s` expires with no checkpoint, `not_preemptible` gives
the real elapsed time; it does not claim the script was cancelled. Inspect the
result and model before retrying.

**What is in scope, without importing anything:**

| Name | Is | Notes |
|---|---|---|
| `doc` | `Document` | The document `target_document` named, re-checked against the active one |
| `uidoc` | `UIDocument` | |
| `uiapp`, `__revit__` | `UIApplication` | `__revit__` is pyRevit's name for it, aliased so it resolves |
| `app` | `Application` | Same object as `doc.Application` |
| `checkpoint(label, done, total)` | | Reaches the disk immediately; readable from outside via `horizun_job_status` |
| `revit_raised(since=0)` | | What Revit raised so far — see §6 |
| `dialog_answer(answer)` | | Scoped modal answer — see §6 |

**The source can be a file.** Send `code_path` instead of `code` for anything
long: it is read on the machine running Revit, as UTF-8 (a BOM or a
`# -*- coding: -*-` line is honoured), CRLF normalised, and measured and hashed
exactly like inline code. It also makes tracebacks name the real file and line
instead of `<string>`. Exactly one of the two, and a file that does not decode is
refused naming the byte and the offset — never decoded leniently into a script
nobody wrote.

One thing to know about it: **idempotency is bound to the request, and the request
names the path, not the bytes.** Re-sending the same `idempotency_key` after
editing the file replays the original answer and runs nothing. That is
at-most-once working; a changed script is new work and needs a new key.

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

### IronPython environment gotchas (measured in the field, 2026-08-07)

Two traps a 123-model batch day paid for, so the next script does not:

- **`System` is NOT pre-imported, even though `clr` is.** `clr.AddReference(...)`
  works on the first line, and `System.IO.File` on the next raises `NameError`.
  Write `import System` explicitly before touching anything under it.
- **`System.Array.CreateInstance` fails where the generic indexer works.** Build
  .NET arrays with `System.Array[System.Byte](sequence)` — the
  `CreateInstance`-then-assign route dies in this host where the one-shot
  constructor form succeeds.

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

## 6. Opening models in a batch, and the dialog that stops one

The bridge **cancels** every modal dialog Revit raises during a script, because
nobody is at the keyboard. That is right, and it has a cost: all Revit tells your
script is

```
Opening was canceled.
```

which is not a diagnosis. Three models were reported unauditable with no cause on
2026-08-13, and four the month before, for exactly this.

**Read the record.** `revit_raised(since=0)` returns what Revit has raised so far
during this script — dialogs, warnings and errors, each with `kind`,
`description`, `answered`, `elements` and `while`. Bookmark before the call and
ask again after it, and you get exactly what that call raised:

```python
checkpoint(name)                    # `while` on anything raised from here on
before = len(revit_raised())
try:
    opened = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), opts)
except Exception as ex:
    reason = str(ex)
    raised = revit_raised(before)   # the dialogs THIS open raised
    row["failed_because"] = raised[0]["description"] if raised else reason
```

The same records come back on the reply as `dialogs` and `failures`, beside
`__output__`, with `revit_raised_observed` — **an empty list from a run nobody
watched is not a quiet run**, and the reply says which it was.

`while` is the script's own last `checkpoint()` label at that instant. The bridge
cannot see which API call was in flight and does not pretend to, so checkpoint per
model if you want every dialog attributed to one.

**Letting an open continue.** Some models raise a dialog whose only sensible
unattended answer is "acknowledge and continue". Around that one call:

```python
with dialog_answer('dismiss'):
    opened = app.OpenDocumentFile(model_path, opts)
```

Scoped deliberately, and never as a run-wide switch: `dismiss` answers OK to
*everything*, and Revit reads OK on a close-with-changes dialog as **Save** — a
read-only audit could write to every model it touched. Outside the block, dialogs
cancel as before.

And a model that will not open unattended is still a **finding**. `dismiss` makes
it measurable; it does not make it fine.
