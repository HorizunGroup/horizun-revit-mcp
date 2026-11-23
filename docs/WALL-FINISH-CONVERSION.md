# In-place wall finish conversion

`horizun_wall_finish operation=convert_to_finish` changes an existing host wall,
without creating or deleting a wall. Give `target_document`, `wall_ids`,
`link_instance_id`, `finish_type_id`, `thickness_mm` and `side` (`exterior` or
`interior` relative to the original wall orientation). The type must already
exist, be basic and single-layer, and match the declared thickness within 0.1 mm
(maximum 100 mm). Type names do not establish material or authorship.

Rehearse first. Apply the same arguments with its emitted confirmation token,
`dry_run=false` and a fresh idempotency key. The planner requires faces parallel
within 0.1 mm along the retained axis; it does not rotate or shorten a host.
The matched overlap ratio is reported; the full axis may extend beyond the core.
It retains base/top constraints, existing profiles and the full length,
sets the wall centerline, disables end joins to avoid moving adjacent walls,
changes type and translates the wall onto the selected core face. Slanted and
tapered sections are refused. Nine inner-face samples over the matched overlap
must project inside a real bounded vertical core face within 1 mm; an inferred
offset and zero overlap alone cannot establish contact.

Each wall has a transaction and reversible group, so deferred commit failures
cannot poison compatible walls in the same request. Unmeasurable or incompatible inserts are
reported in `skipped`; a failed wall is rolled back individually. Door/window
IDs, types and hosts must survive, and their insertion points must follow the
host translation. Nine interior aperture rays per insert must remain clear
through the new thickness. This is sampled clearance, not proof of the entire
void topology or of manufacturer suitability. Optional family parameters
`Minimum Host Thickness` and `Maximum Host Thickness` are enforced when present.
Non-door/window inserts are refused rather than silently rehosted.

Re-reads inside the reversible group and after assimilation check wall identity,
type, thickness, endpoints, both endpoint offsets from the core axis, hosted
inserts and boolean volume against the matched linked core. Shared volume must
be at most the bridge's 1 cm³ boolean tolerance; its unrounded value and coverage
are reported. Failure to measure is not zero. `application` counts converted
walls and skipped walls; a mixed result remains partial, and zero verified
changes cannot become successful transport.

Existing separate finish walls and clashes with other linked elements are not
removed by conversion. Review those independently in a disposable copy before
using this operation on a wider set.
