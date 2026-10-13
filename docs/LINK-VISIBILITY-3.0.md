# Reading elements in a linked model from a host view

`horizun_manage_links` with `operation=visible_elements`, `link_instance_id` and
`view_id` reads a loaded RVT link without calling Revit's three-argument
`FilteredElementCollector(document, viewId, linkId)` overload. The
`horizun_execute_python` preflight warns when a script uses that overload.

The command scans the linked document and measures the link instance's workset
and hide state, open worksets in the link, category visibility, the host plan
view's Z range after the link transform, and hidden parameter filters. It
reports whole-set counts, explicit exclusion reasons, unreadable rows and
bounded candidate rows. `visibility_proven=false` is intentional: this
independent read cannot prove the final rendering across crop boundaries,
phases, design options and per-link display settings. A candidate is an element
that passed the measured gates, not a claim that Revit drew it. An unloaded
link and an unreadable view range are named refusals.

`horizun_job_status` reads process liveness without contacting Revit. For an
unfinished job it also checks whether Windows wrote a crash dump for the
recorded Revit PID after that job started. It distinguishes an alive process,
an alive process with a recent dump, a dead process, and a dead process with a
dump. A dump is evidence of a fault; it does not prove which model operation
caused it. Reusing a PID cannot attach an older dump to a new job.
