# Geometry verification

`horizun_join_geometry` distinguishes an empty intersection from a failed measurement.
A successful Boolean returning null, or a zero-volume intersection, contributes zero.
An exception in either operand order remains unknown; the reply carries its cause.
A complete traversal containing no positive-volume solids also contributes zero;
an unreadable branch is never treated as empty.

`verification_mode=atomic` remains the default for `join` and `auto_priority`.
`verification_mode=per_pair` rehearses the same scope and isolates each changed pair
in a subtransaction. A pair that cannot satisfy the requested join, order and volume
checks is rolled back independently. Other verified pairs remain applied. After
commit the tool measures again: any pair whose state subsequently became unreadable
is reported as unverified, with `changes_applied=true` when its join was retained.
Such an outcome is partial, never fully verified.

The `unverified` array names every excluded pair and its reason. `coverage_complete`
must be true before interpreting retained-pair closure as closure of the entire
request. For `auto_priority`, `shared_volume_scope_after_m3` measures the complete
level separately, including skipped pairs; `scope_closure_verified` is false if
that total is unknown or above the tolerance. Member volume before/after and its
reduction are supporting evidence, not a substitute for an intersection measurement.
Volumes are pairwise sums, not geometric unions or certified takeoff deductions.

`horizun_wall_finish match_core` requires positive overlap of the actual vertical
ranges in host coordinates, as well as the planar parallelism/offset/overlap tests.
Linked bounding boxes are transformed using all eight corners. Touching at one
horizontal face alone does not qualify; an unreadable height range cannot match.

`horizun_join_geometry fix_corners` adjusts both nearby legs of an L within the
declared tolerance, then verifies their reciprocal endpoint joins after regeneration.
It also checks incident open ends in the same meeting, so moving an opening to a
third wall cannot pass. A persistent opening is `unresolved` and fails the edit's
postconditions. T intersections require the approaching end to join the receiving
wall interior, without requiring an unrelated receiving-wall endpoint to move.
