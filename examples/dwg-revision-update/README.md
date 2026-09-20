# A revision that divides a run, and one that takes the division out

Everything here is reproducible on any machine with Revit and AutoCAD: the drawing is written from
scratch by `make-fixture.py` (six revisions of four small duct networks and their labels — no project
data of any kind), and every step below is a typed call any MCP client can make. No element id in this
page is real; the ones your run produces are its own.

## The fixture

```bash
python make-fixture.py C:\some\scratch\mep-split
```

Six folders, each with `MEP-SPLIT.dwg`, and `MEP-SPLIT.truth.json` beside them saying what each
revision shows. Set `HZ_ACCORECONSOLE` if your AutoCAD is not where the script looks.

| revision | what it is for |
|---|---|
| R0 | the network as first issued |
| R1 | run A is drawn as two pieces of different size — a division appears, and a transition with it |
| R2 | the division of A moves |
| R3 | A is one run again — the division disappears |
| R4 | the division of B moves, and B2 has an elbow at each end |
| R5 | R1 again, for failure and recovery runs |

## Build the model from R0

```
horizun_manage_cad_links  operation=add, file_path=<R0>\MEP-SPLIT.dwg, units=inch, placement=origin
horizun_plan_from_cad     instance_id=<the link>, requirement_set=<mep-split-1.0.0.json>, level_name="Level 1"
horizun_apply_cad_plan    apply_binding + actions + candidate_index from that plan
horizun_cad_networks  →  horizun_cad_connect          (joins what the drawing draws as meeting)
```

The plan carries **`coherence`** and **`applicable`**. Only `sources_match_the_link` grants applicable,
and `horizun_apply_cad_plan` re-checks it before writing: a plan made while the link and the files could
not be shown to be the same issue is refused `plan_not_applicable`, and the remedy is one reload.

## The revision arrives

```
horizun_manage_cad_links  operation=repoint, instance_id=<the link>, file_path=<R1>\MEP-SPLIT.dwg
horizun_plan_cad_update   instance_id=<the link>, requirement_set=<the same file>, level_name="Level 1"
```

Read **`divisions`** before anything else. One row per split or merge, held or accepted:

```json
{ "what": "split", "state": "held",
  "origin": { "element_id": 5001, "built_from_entity": "cadsem:…",
              "built_under": { "source_file_sha256": "…", "source_set_sha256": "set:…" } },
  "pieces": [ { "candidate_id": "cadrev:…", "keeps_the_element": true,  "element_id": 5001 },
              { "candidate_id": "cadrev:…", "keeps_the_element": false, "element_id": null } ],
  "keeps": [5001], "creates": ["cadrev:…"], "removes": [],
  "id_substitutions": [],
  "fittings_affected": [], "connections_protected": [ … ],
  "decisions_required": [ { "what": "is this element the same run, divided?",
                            "how": "accept_pairings: [{ element_id: 5001, candidate_id: … }]" } ],
  "geometry_before_mm": [[0,0],[10160,0]], "geometry_after_mm": [ … ] }
```

Nothing in a DWG says a run was divided rather than one removed and two drawn, so it is **offered and
held**. Answer it and plan again:

```
horizun_plan_cad_update   … accept_pairings=[{element_id: 5001, candidate_id: "cadrev:…"}]
horizun_apply_cad_update  actions + apply_binding + provenance + candidate_index from THAT plan
```

The element is re-shaped to its longest piece and **keeps its id**; the other piece is built new and
stamped. Run `horizun_cad_networks` → `horizun_cad_connect` again and the transition appears between
them, because the two pieces now carry different sections.

## What it will not do on its own

- **Build on ground an element still holds.** A create whose line lies inside a standing element is
  held with that element named and how much of the line it holds — whatever else the plan decides.
  Deciding that element away (`resolve: [{element_id, decision: "delete"}]`) frees the ground and the
  piece is built in the same plan.
- **Re-shape a run whose ends are in a network.** Revit refuses it, and a fitting cannot be released
  and put back — re-connecting builds a new one, with a new id. The row names every fitting, the id it
  will lose and what rebuilds it; `release_fittings: [ids]` is how you agree to that, and the verified
  delete then goes in before the re-shape.
- **Delete anything because a pairing was accepted.** A merge re-shapes one element to the whole line
  and leaves the others standing inside it; removing them is a `delete` decision of its own.

## Taking the division out again (R3)

The create that covers both pieces comes back classified `merge`, naming `may_be_the_merge_of` and
which element would keep its id. Accept that pairing and the run is one again, still carrying the id it
had before the division. The absorbed piece stands until you decide it away.

## Checking the result

`horizun_audit_cad_model` scores the model against the drawing. The fixture also ships its own truth,
so a verifier can measure the model directly — runs, sizes, heights, connections and which ids
survived — rather than re-reading the planner's account of what it did.

Two things worth measuring that are easy to miss:

- **height.** A plan drawing carries no height. A re-shaped run keeps the one it was built at; if your
  check only compares X and Y, a run on the floor looks perfect.
- **trim.** A connected run is shorter than its drawn line by the arm each fitting takes out of it
  (1.5 × W × tan(half the turn) for the Autodesk radius elbow, measured across fifteen of them — the
  law of that family, not of fittings in general). Comparing a joined duct with the drawn ends and a
  millimetre tolerance can only ever fail.
