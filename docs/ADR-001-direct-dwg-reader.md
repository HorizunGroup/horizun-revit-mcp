# ADR-001 — Should this bridge read DWG files directly?

**Status:** decided — NO, and the decision is revisitable on stated conditions.
**Date:** 2026-08-27
**Scope:** the DWG→BIM path (`horizun_query_cad`, `horizun_plan_from_cad`,
`horizun_audit_cad_model`, `horizun_plan_cad_update`, `horizun_manage_cad_links`).

---

## The question

Today the bridge never opens a DWG. It asks **Revit** to link one, and then
reads the geometry Revit exposes through `GeometryInstance` — lines, arcs,
polylines, solids — at the position and scale Revit has placed them.

Reading the file directly instead, with a DWG library, would in principle give
more: block names, attributes, text, layer colours, xrefs, and entity handles.

That last one is the reason the question keeps coming up. Everything in the
incremental path — provenance, audit matching, `moved` versus `added` — hangs on
identity, and **measured on Revit 2026 there is no handle anywhere in the CAD
API**: `GeometryObject.Id` collides (35 objects, 24 distinct ids). So identity is
computed from a quantised surrogate of what the entity IS and where it sits. A
real DWG handle would be a stable identity given to us rather than derived.

Text is the second reason. **Measured**: no string is reachable from imported
DWG geometry at any depth. Text arrives as curves on its own layer — the layer
name survives, the words do not. Grid bubbles, room names and door numbers are
therefore unreadable today, and the bridge says so rather than inventing them
(`harvest_coverage.text_is_unavailable`).

---

## What a direct reader would have to satisfy

These are requirements, not preferences. A library that fails any one of them is
not a candidate.

1. **All five targets.** The add-in compiles to `net48` for Revit 2023–2024,
   `net8.0-windows` for 2025–2026 and `net10.0-windows` for 2027, from one
   source tree. A reader must run on all three runtimes.
2. **A licence compatible with Apache-2.0 distribution**, with no per-developer
   or per-seat royalty, and no term that changes what a user of this bridge may
   do with it.
3. **No activation, no licence server, no network call.** The bridge runs on a
   modeller's machine, often offline, and must not acquire a phone-home.
4. **No native binary this repository cannot verify.** Every installed byte is
   re-read and hashed after install; a native DLL that cannot be built from
   source or verified by publisher signature breaks that chain.
5. **It must earn its place.** Revit already reads the file. A second reader
   introduces a second answer to "what does this drawing say", and two readers
   that disagree is a worse position than one reader that is limited.

Requirement 5 is the one that decides this.

---

## The options, and why each was set aside

| Option | Targets | Licence | Verdict |
|---|---|---|---|
| **ODA Drawings SDK** | native + .NET wrapper | commercial, per-developer + membership | fails 2, 3, 4 |
| **Autodesk RealDWG** | native + .NET wrapper | commercial, ADN membership required | fails 2, 3, 4 |
| **Aspose.CAD** | .NET | commercial per-developer | fails 2 |
| **LibreDWG** | native | GPL-3.0 | fails 2 — copyleft would reach this repository's terms |
| **ACadSharp** | .NET, MIT | MIT | passes 1–4, fails 5 |
| **netDxf** | .NET, MIT | MIT | DXF only; does not read DWG at all |

**ACadSharp is the only one that clears the licence and platform bars**, and it
is a genuinely good library. It still fails requirement 5, for three reasons:

- **It would be a second reader, not a better one.** Revit places the link:
  units, transform, shared coordinates, xref resolution. Reading the raw file
  gives entity coordinates in the file's own space, and reconciling those with
  where Revit put the instance re-implements the part that is currently free and
  correct. Any disagreement between the two would surface as a conversion that
  builds walls in the wrong place — the exact failure this path exists to
  prevent.
- **The identity win is smaller than it looks.** A DWG handle is stable within
  one file. An incremental update compares a file against its *successor* — a
  different file, exported or re-saved — and handles are not guaranteed to
  survive that. The surrogate identity is derived from geometry precisely
  because geometry is what two revisions of a drawing actually share. A handle
  would be a useful extra rung on the matching ladder, not a replacement for it.
- **Text is worth having and is not worth this.** Grid names and room numbers
  are the real prize. They can be obtained without a second geometry reader —
  see below — and taking a whole DWG parser to get at strings is the wrong
  trade.

---

## Decision

**No direct DWG reader.** The bridge continues to read what Revit exposes, and
continues to say plainly what it therefore cannot see.

This is not "unsupported". It is a measured limitation with a published
consequence: `harvest_coverage` names every primitive it could not use and
states that text is unreachable, so a caller is never told a drawing is fully
read when it is not.

---

## What to do instead, in order of value

1. **Let the requirement set carry what the drawing cannot.** Grid names, room
   numbers and door types are project data, not geometry. A rule that says
   "grids on `S-GRID`, named from this list, in this order" is more reliable
   than any text extraction, because a person checked it.
2. **Accept a companion DXF when one exists.** A DXF of the same drawing is
   text-readable with a small MIT library (`netDxf`) and no native binary, and
   it is *additive*: geometry still comes from Revit, and the DXF supplies only
   strings, matched by position. One geometry reader, still.
3. **Read the CAD link's own layer table for names.** Already available, already
   used — the layer name is the strongest signal a DWG reliably carries, and the
   requirement set is built around it.

---

## When to revisit

Reopen this decision if any of these becomes true, and record the measurement:

- Revit exposes DWG entity handles or text through a supported API — the
  measurements above are dated 2026 and are the reason, not the conclusion.
- A drawing set arrives where the layer name genuinely cannot identify the
  discipline, and options 1–3 have been tried on it and failed.
- ACadSharp (or an equivalent) grows a mode that reads *only* attributes and text
  from a file already linked in Revit, so no second geometry reader is
  introduced.

Until then: one reader, and an honest account of what it cannot see.
