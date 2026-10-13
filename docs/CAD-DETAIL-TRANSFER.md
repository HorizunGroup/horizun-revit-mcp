# CAD to native detail: measured transfer

`horizun_query_cad mode=instances` identifies the import. `horizun_cad_extract`
with `view_id`, `read_dwg_text=true` and `detail_plan=true` then answers two
different questions: which geometry Revit retained, and whether the source DWG
is available to read TEXT/MTEXT through AutoCAD. An embedded import with no
source file reports `source_dwg_not_readable`; it does not establish that the
drawing contains no text. A missing AutoCAD engine is reported separately.

`detail_plan` pages lines, arcs and polylines, including geometry Revit exposed
inside nested blocks, into actions for `horizun_detail_2d`. Each action retains
its source entity ID, CAD layer and block path in a parallel array. Pass an
explicit `line_style_by_layer` map of CAD layer to Revit line style ID. Missing
styles and unconverted entities are counted as gaps. Geometry farther than
0.5 mm from the requested view plane is refused rather than projected. For
each page, rehearse with `horizun_detail_2d` (the default dry run), then apply
its confirmation token. Re-read the target view with
`horizun_query_detail_2d` and compare the verified action counts.

The plan is read-only and **never a completion verdict**. Its denominator is
the set of entities Revit exposes, which can be smaller than the original DWG.
The source-file census, text placement, hatches, style fidelity and every
post-commit reread must agree before a whole detail can be called converted.
TEXT and MTEXT metadata include height and justification when the DWG reader
is available; `horizun_annotate` accepts a matching TextNoteType height,
alignment and rotation and verifies them after commit. Block-definition text
has local coordinates; it must be expanded through all INSERT placements and
aligned to the import transform before annotation. The raw text page does not
claim that placement. An unavailable source, unresolved placement or missing
TextNoteType remains a named gap.
