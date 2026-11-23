# Stagger grid bubbles

`horizun_planimetry_ops operation=stagger_grid_bubbles` extends the selected straight grids' view-specific 2D endpoints outward until visible bubble envelopes clear one another. It never moves the model grid axis.

Required: `target_document`, `view_id`. Optional: `grid_ids` (otherwise visible grids), `ends=end0|end1|both`, printed `bubble_diameter_mm` (8), `gap_mm` (2), `max_offset_mm` (200). Dimensions are paper millimetres at the view's measured scale. The caller declares the symbol diameter; the bridge measures circular envelopes in view coordinates, not rendered glyphs. Curved grids and existing leaders are refused before a transaction. Unselected visible heads remain fixed obstacles.

The rehearsal reports planned offsets and measured `overlaps_before`. Apply uses its emitted token and a fresh idempotency key, rereads every endpoint within 1 mm, retains visibility and measures `overlaps_after=0` before keeping the transaction group. The token binds scale, visible endpoints, targets and request fields. The reply declares `visual_symbol_size_measured=false`. An already separated layout is an explicit no-op.
