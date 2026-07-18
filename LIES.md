# The lies this MCP cannot tell

Every tool in this distribution is built on one rule: **never report an outcome you did not verify.** This document is the evidence. Each lie below was *measured* against a real Revit model with the tools this MCP replaces, and each is now either caught at runtime by the [`HorizunGuard`](src/shared/Infrastructure/HorizunGuard.cs) contract or pinned by a unit test that fails the build if the logic regresses.

The unit tests run in CI on every push (`tests-xunit` job). The runtime behaviours are reproducible live against Revit with the scripts under `scripts/`.

---

## 1. The purge that reported 758 deletions and deleted nothing

**What happened.** A batch purge reported `758 types purged`. Nothing was purged. Revit had rolled the whole transaction back and `Transaction.Commit()` returned `TransactionStatus.RolledBack` **without throwing**. The script counted its own `Delete()` calls and called that success.

**Why it is the worst kind of wrong.** It is not an error you can catch. It is a confident, successful, empty result — and nobody re-runs a purge that "worked".

**How Horizun catches it.**
- `HorizunGuard.Commit(t, what)` turns a non-`Committed` status into a thrown `HorizunSilentRollbackException` instead of a silent success.
- `horizun_delete_verified` re-resolves every id against the model *after* the commit; `deleted` means the model says it is gone, `deleted_total` is `null` on a dry run, and cascades are counted as `confirmed_gone` / `confirmed_surviving` / `could not look`.

**Pinned by.** `HorizunReconcileTests.Seven_hundred_fifty_eight_intended_zero_actual_is_not_verified` — `Verified(758, 0) == false`.

---

## 2. The keynote "written to one element" that re-coded 51

**What happened.** In Revit the Keynote parameter normally lives on the **type**, not the instance. Asked to code one beam, the tool wrote the type and reported the element id — silently re-coding every one of the 51 beams that shared the type. A separate run placed keynote tags on 12 elements and reported "12 tagged"; every tag rendered empty.

**How Horizun catches it.**
- `horizun_set_keynote` resolves the write target first, reports the blast radius (`elements_now_carrying_this_keynote`, `collateral_elements`) *before* writing, checks `Parameter.Set()`'s return value, and re-reads the value from the model afterwards.
- Verified live: requested 1, reported 8 affected / 7 collateral, confirmed by an independent Python re-read.

**Pinned by.** `HorizunReconcileTests.One_intended_eight_actual_is_not_verified` — `Verified(1, 8) == false`.

---

## 3. The volume that disagreed with itself by 42.7%

**What happened.** Revit reports an element's volume from three places, and they do not always agree. On a real beam: the `Volume` parameter said **23.1065 m³**, the solid geometry and the material takeoff both said **40.3562 m³** — a **42.7%** gap on the quantity you bill. Handlers that read only the parameter (the cheap cached one) reported that as "the volume". Billing from it bills 57% of the concrete poured.

**How Horizun catches it.**
- `horizun_quantities` reports all three sources side by side and refuses to pick one — which source is correct depends on your measurement criteria, and whoever signs the takeoff decides.
- The agree/disagree arithmetic lives in `HorizunReconcile.Compare`, Revit-free and unit-tested.

**Pinned by.** `HorizunReconcileTests.The_measured_beam_gap_is_flagged_as_disagreement` — `Compare(23.1065, 40.3562)` returns `Agree == false`, `DifferencePct == 42.7`.

---

## 4. The clash report of "0 clashes" against ducts it never opened

**What happened.** Clash detection only ever collected from the host document. On a federated project the other discipline lives in a **link** — so "walls vs ducts" returned `0 clashes` about ducts in a file the tool never opened, `warnings: []`, `success: true`.

**How Horizun catches it.**
- `horizun_clash` collects from the host and every loaded link, transforms link geometry into host coordinates, and names the source model of both elements in every clash. If a link was excluded or unloaded, the result is labelled `partial` — a zero from this tool means zero, or it says why not.
- Verified live: a duct placed through a beam in a linked model — old tool `clashes: []`, Horizun `1 clash, cross_model: true`.

---

## 5. The chapter code accepted as an assignable item

**What happened.** A classification catalog is a tree; only leaves are assignable. A code with children is a chapter. Confirming a code "exists" by prefix, substring, segment-count or regex shape waves chapters through — and a 3-segment code can be a chapter (`D001-A1-A01` "PILOTES TIPO TORNILLO" is the parent of nine).

**How Horizun catches it.**
- `horizun_catalog_lookup` derives leaf-ness strictly from the parent column — a code that is nobody's parent — never from code shape. A chapter is refused with the count of children that rejected it; a non-existent code returns `is_leaf: null` (unknown), not `false`.

**Pinned by.** `HorizunCatalogLookupTests` (11 tests), including `Three_segment_code_that_is_a_parent_is_a_chapter_not_a_leaf`.

---

*The point is not that other tools are careless. It is that a Revit API which rolls back by returning a status, stores keynotes on types, and reports volume three ways will let any handler report work it did not do — unless the handler is built so it cannot. This one is.*
