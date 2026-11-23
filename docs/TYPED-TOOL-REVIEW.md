# Typed tool review

This review uses local source and unit tests. No live Revit or cloud service was
contacted. Model behavior and cloud authentication remain pending in vivo.

| Surface | Finding and resulting behavior | Evidence and live limits |
|---|---|---|
| `capture_view` | Ordinary capture accepted `target_document` without checking it. The shared read guard now runs before resolving a view or exporting. Schedule rejection points to `get_schedule_data`. | Ordering regression in `TypedToolReviewTests`. Existing `CaptureFrameRulesTests`, `CaptureFramingRulesTests` and `CaptureFreshnessTests` cover framing/freshness arithmetic. Actual PNG rendering and temporary view restoration require Revit. |
| `open_document` cloud | Successful opens already re-read the active document and compare project/model GUIDs. Failure text previously asserted that nothing opened and offered a speculative GUID diagnosis. Exceptions and null returns now report an unknown opened state, the active title, and only the dialogs/failures recorded during that open. | `CloudOpenFailureRules` unit tests cover canceled dialogs, per-call attribution and missing observation. Existing `OpenDecisionTests` cover cloud central permission and unknown version. Native cloud loading remains untested here. |
| `cde_cloud list_states` | Existing exact case-sensitive matching, pagination, access failures and budgets already expose incomplete coverage. Two distinct folders with the same exact name previously selected the first. They now yield an unresolved ambiguous state. Repeated pagination rows of the same folder ID remain resolvable. | Fake HTTP tests in `CdeCloudToolTests`; all requests use injected handlers. Live state mapping requires the user's configured service and permission. |
| `query_model partial` | Row output already considered the cooperative scope, but summaries and grouped counts omitted that condition and could enter the cache as complete. All response shapes now stamp collection completeness and partial evidence before caching, and summary wording describes observed counts. | `TypedToolReviewTests` cover every shape and preserve the difference between pagination and an unfinished collection. An incomplete collection cannot resume unexamined elements with the existing matched-row cursor; the report now states that limitation and asks for a narrower selector or larger budget. Actual budget stops across host and links require Revit. |

The partial result retains the observed counts/rows and coverage findings. No
test here proves model visibility, actual cloud authorization, or a native loader
result.
