# Tags of every kind

`horizun_annotate operation=tag` creates the tag; `horizun_plan_annotations operation=auto_tags`
plans a batch of them (read only) and hands back a complete `horizun_annotate` dry run. No new
tool: both were extended in 5.2. Pure rules live in `src/Horizun.Revit/Core/TagRules.cs` and are
covered by `tests/Horizun.Core.Tests/TagRulesTests.cs`.

## What can be tagged and how each one is verified

Every tag is created in the rehearsal (rolled back) and again in the apply; the apply re-reads
it before the transaction closes and rolls the whole batch back if a check fails.

| Target | Argument | Revit API | Re-read after the commit |
|---|---|---|---|
| Element of a category (90 mapped categories: doors, windows, walls, MEP, structure, stairs, site, groups...) | `tag_mode=by_category` (default) | `IndependentTag.Create` | owner view, tagged element, type of the expected tag category, head point, leader, orientation, `TagText` when `require_tag_text` |
| Any category with one tag | `tag_mode=multi_category` | same, type of `OST_MultiCategoryTags` | as above + `IsMulticategoryTag` |
| Material of an element | `tag_mode=material` | same, type of `OST_MaterialTags` | as above + `IsMaterialTag` |
| Element keynote | `tag_mode=keynote_element` | `IndependentTag.Create(symId...)` | type of `OST_KeynoteTags`; `KEY_SOURCE_PARAM` read back must be 0 |
| Material keynote | `tag_mode=keynote_material` | a KEYNOTE-category tag (`OST_KeynoteTags`) placed on a FACE of the element whose material carries a keynote (Key Source is read-only on the instance, measured in 2025.4, so it cannot be set after creation); the faces are tried in order and the first Revit accepts wins | Key Source must read Material, Key Value must be that face's material keynote and the tag text must show it or its table text (fail closed otherwise). Measured: in a plan view Revit refuses every wall face ("The reference can not be tagged"), so the row fails with that reason and writes nothing; not yet measured in a section or elevation |
| User keynote | `tag_mode=keynote_user` + `keynote_key` | same + writes key source 2 and `KEY_VALUE` | key source 2 and the key read back, AND the tag's own text must show the key or the table's text for it (an unreadable text fails) |
| Room | `element_id` of a room | `NewRoomTag` | tag points at the room, head inside it, leader |
| Room of a link | `link_instance_id` + `linked_element_id` | `NewRoomTag(LinkElementId(link, room))` | the tag's `TaggedRoomId` and link id; inside test done in link coordinates |
| Area / MEP Space | `element_id` | `NewAreaTag` / `NewSpaceTag` | tagged area/space, head, leader |
| Element of a link | `link_instance_id` + `linked_element_id` | `IndependentTag.Create` on a link reference | tagged linked element |
| Several elements, one tag | `element_ids` (up to 50 more) | `IndependentTag.AddReferences` | the committed tagged set equals the requested one exactly |

`tag_mode` is case-insensitive. Leader shape (any IndependentTag and spatial tag, with `add_leader=true`;
points in `units`, host model coordinates): `leader_end` sets a
free end (`LeaderEndCondition.Free` + `SetLeaderEnd`), `leader_elbow` an elbow
(`SetLeaderElbow`); both are read back in the view plane with a 0.5 mm tolerance.

## Type chosen when none is named

A room, area or space tag, and a multi-category tag over a link, take the single loaded type of
their tag category, else the document's own default for it (`GetDefaultFamilyTypeId`) when that
default is loaded. Only when neither answers is the call refused, listing every candidate as
`id Family: Type`. A tag that reads an empty text is flagged on its rehearsal and apply rows
(`tag_text_empty: true` + `warnings`): it would print blank; `require_tag_text=true` refuses it.
`tag_evidence.extent` is `minR,minU,maxR,maxU` in tenths of a millimetre in the view frame, from
the view origin (`extent_units`); `extent_check` says whether that box holds the tag head
(`contains_head`), holds it only divided by the view scale (`contains_head_divided_by_view_scale`,
the scale trap seen on text notes) or not at all (`head_outside`). It is evidence only: it never decides whether the tag verified.

## Refused before anything is created (with the reason)

- A `tag_type_id` of the wrong tag category for the element (a window tag on a door, a keynote
  type without a keynote mode, a multi-category type without `multi_category`).
- No tag type of the needed category loaded: the reply names the category and
  `horizun_annotate load_tag_family` (which already loads keynote, material, multi-category,
  area and space tag families).
- Several keynote tag types loaded and none named; an element keynote on an element with no
  keynote (use `horizun_set_keynote` first); a `keynote_key` that is not in the keynote table.
- `element_ids` are covered by the identity guard like `element_id`: send `unique_ids` (compared with
  `element_ids`) or `identity: {"element_ids": [...]}`; a renumbered id of the same category is refused.
- `element_ids` with a repeated id, another category under a category tag, a room/area/space,
  more than 50, a keynote or material mode, or over a link.
- Material or material-keynote tag over a link (Revit throws `InternalException`, measured).
- Area or Space of a link (`NewAreaTag`/`NewSpaceTag` take host objects only in 2023-2027) and a
  nested link (a link reference is one level deep).
- `leader_end`/`leader_elbow` without `add_leader=true`.

## auto_tags: sweep a whole view

Without `element_ids`, `auto_tags` sweeps every element visible in the view whose category has a
tag category: rooms, areas and spaces included (at their location point, no leader), group
members included, already tagged elements skipped. `categories` limits the sweep (`OST_*` names);
`include_links=true` adds the visible elements of loaded links (Revit 2024+; on 2023 the
link part is refused per category as `links_need_revit_2024`). Up to 500 actions.

The reply carries `by_category`: per category `visible`, `planned`, `already_tagged`,
`not_taggable`, `in_groups`, `from_links` and `not_taggable_by_reason` (`count`, `why`, up to 5
sample ids). Reasons: `no_tag_category_known`, `no_tag_type_loaded`, `ambiguous_tag_type`,
`no_bounding_box`, `no_collision_free_point`, `unplaced_room_area_or_space`,
`linked_area_or_space`, `linked_material`, `nested_link`, `link_not_loaded`, `links_need_revit_2024`, `over_limit`.
A sweep's `next_arguments` uses `on_failure="best_effort"`, so one element Revit refuses does not
roll back the others; each refused row says why. In a sweep `safe_to_execute` only means "there are
rows to rehearse": the per-category report says what was left out, and only the rehearsal proves the
rows. A material tag over a link is reported as `linked_material`, never planned. With explicit
`element_ids` a room, area or space now becomes a spatial tag at its location point (no leader, the
default type), and an element whose category has no tag type loaded is reported as
`no_tag_type_loaded` instead of becoming a row.

## Not yet measured in a live Revit

The API surface was measured by reflection on the five `RevitAPI.dll` (2023-2027: identical for
everything used here except the link view collector, 2024+). Still to confirm live: the numbering
of `KEY_SOURCE_PARAM` 0/1/2 (read back for every keynote mode, so a different numbering fails closed),
the leader free end on spatial tags, and multi-reference tags with `avoid_collisions`.
