# Identity guard (P1-4): what a write checks before it touches the model

Synchronizing with central can renumber ElementIds. An id read before a sync may name another element after it
(field case 2026-10-30: three text notes and a tag). A write that carries what it expects to find refuses the whole
call, writing nothing and opening no transaction, when the model disagrees (`precondicion_no_cumplida`,
`reason: elemento_no_coincide`, `write_started: false`, expected versus found, `key`, `path`, `position`).
A request without those fields behaves exactly as before.
The coverage tables live in the contract assembly (`IdentityCoverage`) and ALL of them are part of the contract hash, so a server and an
add-in that disagree about any of them do not connect: the verified tools, the operations that write nothing, the capture options, the
always-deleting tools, the deletion words and operation keys, the deleting operations, the keys they really delete, the delete keys (`DeleteKeys`: `remove_sheet_ids` of `manage_revisions update_revision`), the operations that look like deletions and are not, the deleting conditions,
the tools that take no ids (`NoSubjectTools`), the misspellings (`NearNames`), the paths where `identity` is the tool's own text (`OwnIdentityTextPaths`), the names an
identity can arrive under (`AcceptedNames`) and, for each covered tool, the id keys a bare `unique_id` / `unique_ids` is compared with (`DocumentedIdKeys`) and filled into
(`DocumentedDefaultKeys`). The guard resolves the keys with its own code, `SpecFor`, and tests prove the two are the same, so changing one moves the hash through the other.
The tools whose request is a batch of `actions[]` (`PerActionTools`: `annotate`, `edit_dimensions`; an identity at their top level is refused) are part of the hash too (R2V m-e), and a test
checks that every value of every public table of `IdentityCoverage` is in its own section of the summary.
They are exposed as read-only views, never as arrays. What stays outside the hash: `LinkedDocumentKeys` and the names of the maps keyed by names the user chose
(`ElementIdentityGuard.UserKeyedMaps`, a read-only list), which only the add-in reads when it verifies (the server never does).

Implementation: `ElementIdentityGuard` (Revit-free). It runs **once, in the dispatcher, before the command parses its
arguments** (`IdentityPrepass` -> `DocumentGate.CheckIdentityEarly`): the identity is verified against the active document,
the verified ids are written where the command reads them, and the identity fields are removed. No command validator, however
closed its list of fields, ever sees them, at any depth. `DocumentGate.ForMutation` still runs the same check (it finds
nothing left) and still refuses identity fields on a writer the guard does not cover. In the server, `UndeclaredArguments`
lets the identity fields through for every covered tool even where its schema is closed and does not repeat them.

**Only `unique_id` identifies an element.** `expected_category` / `expected_type` without a `unique_id` cannot tell two elements of the
same category or type apart (the field case: three text notes swapped by a synchronize all stay "Text Notes"). So:

| Identity without `unique_id` on... | Result |
|---|---|
| a **deletion** | refused: `entrada_invalida`, asks for the `unique_id`, nothing written (a deletion on a renumbered id cannot be undone) |
| **any element of a collaborative model** (`Document.IsWorkshared`, a local file of a central included; if it cannot be read: collaborative) | refused, whoever made the element and whether or not a synchronize happened (R2U) |
| in a model that is **not** collaborative, an element that is **not ours**: no mark of THIS user, a mark without a user (older), another user's mark, an unreadable mark, a copy of a mark made in this session | refused: `entrada_invalida`, asks for the `unique_id`, nothing written |
| in a model that is not collaborative, an element **of ours** | proceeds, with the warning below |
| any of them, **with** a `unique_id` | proceeds |

**The refusal says exactly** (the tests tie this text to the code and to AGENTS.md; `{why}` is one of the three reasons, `{id_key}` the id key checked):

```
expected_category / expected_type alone do not identify an element: {why}. Send the unique_id of the element you mean (identity.{id_key} or unique_id / unique_ids). Take it from the read that gave you that id, or find the element again by its content (text, location, view) with horizun_query_model (response_mode full or compact, never summary) or horizun_list_elements. Never take it from a lookup by an id you held from before a synchronize: that id may now name another element, and horizun_selection_context operation=validate_ids returns holder_unique_id, not an identity, for an id sent without its unique_id. Nothing was written
```

- deletion: `this operation deletes, and a deletion on an id that a synchronize may have renumbered cannot be undone`
- collaborative model: `this is a collaborative (workshared) model: a synchronize with central renumbers ElementIds and other people's elements live in it, so a category / type cannot tell which element an id names, even on elements Horizun made`
- not ours: `element {id} does not carry a Horizun ownership mark of this user (it is somebody else's, an older mark, a copy, or the mark could not be read), so it may be somebody else's work`

**Getting the `unique_id`** (R2V B-1). From the read that gave you the id, or by finding the element again by its CONTENT (text, location, view) with
`horizun_query_model` (`response_mode` full or compact; `summary` has no rows) or `horizun_list_elements`, whose rows carry it. **Never from a lookup by an id you held
from before a synchronize.** The previous wording sent the agent to `horizun_selection_context` `operation=validate_ids` with `{id, expected_category}` and to
"read `found.unique_id`". After a synchronize that id names another element, `validate_ids` answered `coincide_categoria` with the `unique_id` of THAT element, the
agent sent it, and the write passed the guard with `identity_strength: "unique_id"` on the wrong note. The safe design (chosen over a conditional one, which would depend on
state the reply cannot prove): a check sent with an id and NO `unique_id` never returns an identity. Its `found` carries `holder_unique_id` (the element that holds the id
today), the row carries `identifies: false` and a `warning` (`id_sin_revalidar`), and the statuses stay `coincide_categoria` / `sin_expectativa` / `cambio`. With the `unique_id`
you read, the answer is `coincide`, or `cambio` with `current_id` (where that element lives now; `null` when it no longer exists). **R2W B-2.** `found.unique_id` is offered ONLY when it is the very `unique_id` you sent
(`coincide`; or the element found by that `unique_id` under another id, `id_renumerado`). When the id you sent with a `unique_id` now names ANOTHER element (`id_reasignado`, status `cambio`),
`found` is that other element, so it carries `holder_unique_id`, `identifies: false` and a `warning`, never `found.unique_id`: before R2W the answer put the occupant's `unique_id` there, the
agent resent it and the write passed the guard on the wrong note. `current_id` says where the element you really asked for lives, and is `null` when its `unique_id` no longer exists (R2Y M-1: it used to fall back to the id of the note that took the old id, and a write sent on it landed on that note). The sync notice reads `holder_unique_id` too (what the model holds
under that id now) and a `no_existe` for an id the session never tracked does not count as a revalidation.

**R2Y.** Past the tracking cap the notice is turned off only by a `validate_ids` on an id this session handed out (the ids past the cap are remembered, the id only, up to 1,000,000): a `coincide`, `cambio` or `no_existe` about an id it never handed out is not that proof. When the ledgers follow a document to another key (the first save, Save As) the destination keeps ONLY what the renamed document brings (creations, pending writes and last sync, change history, notice state): what a closed document left under that path is not merged. A Save As is one at a time: a `SavedAs` whose status is not Succeeded moves nothing and discards its pending entry, a new `SavingAs` drops an older entry that never ended, the target path alone never pairs an entry with another document's event (the event's `OriginalPath` must be the path the entry started from) and two wrappers of one document are told apart with `Document.Equals`. The document key (`PathKey`) is the full path with one separator, no trailing separator and lower case. `query_model` rows asked by `element_ids` keep `unique_id` (the real unique_id of the element the read returns, which is what the refusal text tells you to take) and, in a collaborative model, `holder_note`.

The warning is at the TOP level of the reply: `identity_strength: "categoria"`, `identity_warning` (the sentence "Only `unique_id`
identifies; the category or type alone do not detect a swap inside the same category") and `identity_check` with
`keys_without_unique_id`. With a `unique_id` on every checked key, `identity_strength` is `"unique_id"`.

**Why collaborative models are decided by `IsWorkshared` and not by the sync notice (R2U).** The previous rule hung from `health.id_freshness`, which any
read cleared (a query of the levels hid the three renumbered text notes of the Colon T3 field case). Only a workshared document is renumbered by a
synchronize and holds other people's elements, so there the rule does not depend on what was read or synchronized: a `unique_id` is always required.
The notice is still reported (it is a prompt), but it no longer decides.

**The notice, R2V.** It tracks the `{element_id, unique_id}` pairs of what the id-returning reads AND the writes return (`create_elements`, `set_keynote`, `delete_verified`, every tool whose reply carries them; the rows of `cascades` of `delete_verified` carry `unique_id` as well, read by the rehearsal while the element still existed, and a cascade confirmed gone leaves the tracking, R2W m-3); a row that says its element was deleted leaves the tracking. After a synchronize or a "Reload Latest" it clears only
when each tracked id came back with its `unique_id` (a read or a write) or was proven by `validate_ids`. An id that comes back with ANOTHER unique_id is reported in that reply as
`ids_changed` (the notice then clears for it: the caller was told). A row of a linked model (`link_instance_id`, `source_kind: "link"`) is never taken for a host id. At most 50,000 ids
are tracked per document; past that the block says `tracking_overflow` and a `validate_ids` with at least one row that proves something turns it off: revalidate only the ids you are
going to write. The state of a document is dropped when it has closed (`DocumentClosed`, R2W m-2; `DocumentClosing` only remembers which key the document had, because the closing can still be cancelled
and then nothing is lost). Reopening a document in the same session therefore starts without the notice: what the session handed out before is forgotten (the ledger of creations is kept: a document
reopened in the same session still holds marks of this session). Rows of a linked model always say so (`source_kind: "link"`, `link_instance_id`) even when `return_fields` leaves them out (R2W m-1), so the
notice never takes them for host ids.

**`query_model` by `element_ids` in a collaborative model (R2W M-c).** Each row asked by id carries `holder_note`: its `unique_id` is the one of the element that holds that id TODAY, and after a synchronize it does
not prove that it is the element you meant. Take the `unique_id` from the read that gave you the id. The note travels in the reply; `tools/list` is unchanged.

**What "carries the mark" means (R2T M-2, R2U M-B).** The mark is the `HorizunOwnership` entity (version 1: tool, utc, session) plus, since R2U, the
`HorizunOwnershipUserV2` entity (a NEW schema GUID `ea8c326c-9b78-4025-bf3e-b44621bdd938`: tool, utc, session, **user**, **winuser**, **sid**; see the table of GUIDs below). Adding the user as a field of version 1 would
make every document that already holds it unreadable, so version 2 is separate; a stamp writes BOTH, so an add-in that only knows version 1 keeps reading its
mark. The guard believes a mark only when version 2 names THIS user: Revit's `Application.Username`, or the Windows user prefixed `os:` when Revit has none (one function,
`HrzOwnershipRules.UserIdentity`, for stamping and reading). Therefore: **an element Horizun marked before version 2 has no user, counts as somebody else's, and needs
its `unique_id` for a category / type alone even in a model that is not collaborative** (a deliberate fail-closed choice; send the `unique_id`). Another user's mark
is somebody else's. Extensible storage travels with a copy, so a person's copy of a marked element, a copy between documents and a new instance of a group with marked
members all carry the mark. A copy made INSIDE the same document by a Horizun tool (`transform_elements` `copy`, `manage_views` `duplicate`) is something that write ADDED, so it is stamped and
enters the ledger of creations: it counts as ours (R2W m-6). Told apart cheaply: a mark that says it was made in THIS Revit session must correspond to an element a Horizun write of this session
ADDED, remembered **by UniqueId** (`SessionCreationLedger`; an ElementId is renumbered by a synchronize and can be handed to another element), otherwise it is a
copy and the element counts as someone else's. The ledger follows the document through the first save and Save As (`DocumentSavingAs` / `DocumentSavedAs`, and `document_session save_as` itself).

**What is NOT ours (R2V M-1).** What `horizun_execute_python` creates is not stamped and does not enter the ledger (the bridge does not know what a script did or whose elements
it copied); neither does what a copy between documents (Pro edition) brings in (it can duplicate somebody else's elements, and the origin of every copy is not known). Both stay in the change
ledger and in `verify_changes` (the record of changes and the record of ownership are separate: `ChangeLedger` keeps every write, `SessionCreationLedger.RecordFor` only the
creations that are ours). Cost: after a script or a copy, a category / type alone is refused on its elements even in a model that is not collaborative; send the `unique_id`.

**One belief for every ownership gate (R2V M-2).** `HrzOwnershipRules.IsTrusted` is the single question: the identity guard, the delete cascade (`ForeignDependents` of `delete_verified`
through `OwnershipProbe` and `OwnershipRules.Decide`), `RequireOwn` of `view_visibility` and `HrzOwnership.Require` (`view_crop`, `sheet_layout`) all start from it. A mark of another user, a mark of
version 1 (no user) and a mark whose user cannot be established are somebody else's in all of them. Consequence to know: elements Horizun marked BEFORE version 2, by this same user,
are no longer "ours" for the cascade of `delete_verified` (they ask for `allow_foreign_dependents`) nor for `view_visibility`. `view_visibility` has NO override flag: it refuses and asks you to duplicate the
view (`manage_views` `duplicate_view`) and work on the copy, which counts as ours (R2W m-6). `view_crop` and `sheet_layout` unlock with `allow_unmarked_view` / `allow_unmarked` or when the element was created in this
session. **Exception (R2W M-b):** in a document that is NOT collaborative a mark of version 1 (rc2, no user) counts as ours for `view_crop` / `sheet_layout`, because nobody else works in the file; in a collaborative
document they stay strict. The refusal names the real reason (no mark; a version 1 mark in a collaborative model; the mark of another user; a mark of this session with no creation behind it).

**Who the user is (R2V M-4).** The mark records Revit's user name (or `os:` plus the Windows user when Revit has none), the Windows account and its SID. Users are compared by
`HrzOwnershipRules.SameUser`: the SID decides when BOTH sides have it; otherwise the names, without domain (`DOM\x` = `x`), without the `os:` prefix and without case. A mark made in a
session where Revit had no name (`os:x`) and one where it had (`x`) are the same user when the Windows user is. R2W M-a: with the SAME SID, if both sides have a Revit user name (not the `os:` fallback) and the names differ, they are two people (Ana and Beto on one `DOM\cad` account). Limits: the same Windows account AND the same
Revit user name look like the same person, and two people who share one generic Revit account and have no SID in the mark look like the same user; a local (non-domain) SID differs per machine, so one person on two machines looks like two users (fail closed). Version 2 of the mark gained the
two fields under its own GUID (R2W m-5). **Table of GUIDs:** `c5a8bf46-fb81-48fe-963e-fd79fb4b700d` = version 1 (rc2: tool, utc, session); `7e3b1d52-0c4a-4a8e-9f6b-2d51c8a3e9b7` = version 2 as R2U defined it
(4 fields), NEVER published, which this build does not read or write; `ea8c326c-9b78-4025-bf3e-b44621bdd938` = version 2 as it is now (6 fields). An element stamped only by the discarded R2U schema reads as
unmarked (fail closed).

**5.0.4: no personal data in the mark.** Version 2 put a person's Revit user name, Windows account (which names the machine) and SID into every model it marked. Since 5.0.4 a stamp writes version 1 and
version 3 (`3f6d2a91-7c4e-4b58-a1d0-9e8b5c27f4a3`: tool, utc, session, an anonymous owner id and the MCP version) and deletes the element's version 2 entity. The owner id is a random GUID per Windows user and
machine, kept in `%USERPROFILE%\.horizun\owner-id`; a version 3 mark is ours exactly when its owner id is this one (32 hex digits, fail closed otherwise), and names play no part. Version 2 marks already in a
document are still read for the rule above, but replies never show their personal fields. `horizun_manage_parameters` `operation=purge_legacy_marks` deletes every version 2 entity of a document (rehearsed,
token, count re-read; elements owned by another user of a collaborative model are skipped and listed). Limit: two people on one Windows account on one machine are one owner.

**A stamp is both versions or it is not a stamp (R2V m-g).** Version 1 is written first, then version 2. If version 2 fails, version 1 is deleted again from that element; if that
fails too, the element is reported `skipped` (version 1 has no user and counts as somebody else's). `ownership_stamp.stamped` counts only elements that have both.
**Limits, documented, not guessed:** (1) a mark of ANOTHER session of the same user (ours earlier, or a copy made then) cannot be told apart from a genuine one
without a registry of every session, so it is believed; (2) the ledger keeps the last 500 writes of a session, so an old creation of this session beyond them is
refused (fail closed: send the `unique_id`); (3) a mark whose session field cannot be read is believed when the user is ours; (4) a Save As done by someone other than the
MCP and whose events Revit does not raise cannot be followed (R2X: `document_session save_as` renames the ledgers itself, right after `SaveAs`; the Revit events
do the same, paired by document, by `OriginalPath` and by target path; the sync ledger and `pending_sync` follow the document as well).

Deleting operations (`IdentityCoverage` in the contract, part of the contract hash): every call of `delete_verified`,
`split_floor_loops`, `split_multilayer_slabs`, `split_by_level`, `rectangularize_walls` and `ungroup_and_mark` (they replace or
ungroup what they are given), and any node of a covered tool whose `operation`, `action`, `mode`, `decision` or `original_wall` is, or
contains between underscores, `delete`, `remove`, `purge`, `erase` or `ungroup` (for example `manage_curtain` `remove_grid_line`,
`wall_finish` `remove`, `framing` `remove`, `manage_groups` `remove_members`, `manage_parameters` `global_delete`). The rule is
deliberately wide: refusing a weak identity on an operation that only detaches a member costs one `unique_id`.
In 4.2, a node that carries `remove_sheet_ids` (`manage_revisions` `update_revision`) also counts (`IdentityCoverage.DeleteKeys`), for the ids that key holds (the revision itself stays); the other 4.2 tools
(`planimetry_ops`, `sheet_layout`, `pack_sheets`, `room_finishes`...) have no operation that deletes what the caller names:
`planimetry_ops legend_from_families` deletes inside the legend it has just duplicated and `pack_sheets` deletes its own provisional placement.

Deletions whose name carries no deletion word are listed by tool in `IdentityCoverage.DeletingOperations` (R2T M-1): `manage_assemblies_parts`
`dissolve_parts` (deletes the part maker of the named element) and `disassemble` (deletes the named assembly); `manage_groups` `add_members` and
`edit_type_members` (the named group instance is ungrouped and the group type the caller named is deleted and redefined). For `add_members` only the keys that are really deleted count (`IdentityCoverage.DeletingKeys`: `group_ids` and `type_id`); the elements it adds (`element_ids`) are not deleted. Operations whose name carries a deletion word but that do not delete what they name (`IdentityCoverage.NotDeletingOperations`: `manage_schedules` `remove_fields`, `manage_parameters` `remove_binding`) are not deletions, and neither are their children. And one condition
(`DeletingConditions`): `framing` with `spec.wall.method = curtain` replaces the carrier wall by a curtain wall and deletes it when the curtain
covers it whole (**kept conservative**: whether the curtain covers the wall is decided by the geometry of the rehearsal, the `carrier` action of the plan, not by any argument, so it cannot be known before writing). **The operation of an ancestor applies to its children:** a node that carries ids inherits "deletes" from the node above it
that names the deleting operation. The table comes from a sweep of every call that deletes, ungroups or disassembles, on ANY variable (`.Delete(`, `UngroupMembers(`, `.Disassemble(`), in
`Commands/` and `Core/`, **classified call by call** (file plus the order of the call, with the text of the line to detect a shift), and a test fails when a new call is not classified. Classified as NOT deleting a named element
(only what the command itself created, a temporary or a rolled-back what-if): `annotate` (a row that did not verify), `create_elements`
(a reflected copy; the removable middle piece of an inline break, where the pipe the caller named stays), `fix_planimetry`
(dimensions Revit added), `pack_sheets` (a provisional placement), `planimetry_ops` (the contents of the legend it duplicated),
`transform_elements` `edit_sketch` (the sketch curves of the element being redrawn; the element stays), `split_multilayer_walls` (a duplicate type
inside a rolled-back subtransaction), `view_visibility` (a temporary override).

## The two forms

**By key (preferred).** On the node that holds the ids, `identity` names each id key and what it must be:

```json
{ "operation": "place_view", "view_id": 31, "sheet_id": 30,
  "identity": { "view_id": "UID-OF-VIEW", "sheet_id": { "unique_id": "UID-OF-SHEET", "expected_category": "OST_Sheets" } } }
```

- A key that holds one id: a `unique_id` string, or `{unique_id, expected_category, expected_type}`.
- A key that holds a list: a list of the same length (each entry a string or the object form), or one shared
  `{unique_ids, expected_category, expected_type}`.
- Every key named is verified. The key must be present in the node; the id must be an integer (an unresolved
  `${...}` reference is refused, never overwritten).
- An id inside an array of arrays is named with its position: `join_geometry` `pairs: [[7,8]]` is checked with
  `identity: {"pairs[0][0]": "UID-7", "pairs[0][1]": "UID-8"}`.
- `expected_category` is the name the model shows or the BuiltInCategory (`OST_Walls`), which does not depend on
  Revit's language. `expected_type` is the type name or `Family: Type`, compared whole.

**Bare (compatibility).** `unique_id` / `unique_ids` / `expected_category` / `expected_type` beside the ids are
compared with ONE key: the first present of the tool's list below. With a list present, a bare `unique_id` is
refused (use `unique_ids`, or `identity` to say which key). Both forms may share a node as long as no key is named twice.

On success the identity fields are removed from the request: the command reads the verified ids, and the plan hash is
computed over resolved ids.

## Where it applies

At every depth. Each node of a request that carries identity fields is checked, so the children of
`horizun_execute_plan`, `pack_sheets` `sheets[]`/`items[]`, and `manage_revisions` `actions[]` are all covered. A plan child that calls an unverified writer, and the
plan's own level, refuse the fields. Nothing that receives identity fields ignores them.

Refused with `entrada_invalida` (reason `identidad_invalida`):

- a node with identity fields and no ids to check (a selector, `purge_unused`, a `query` mode);
- identity at the top level of `horizun_annotate` and `horizun_edit_dimensions` (it would protect no action): send it
  inside each action;
- any other writer, at any depth.

- identity fields on a call that **writes nothing** (a read): `capture_view` with none of its temporary options, `join_geometry`/`manage_curtain`/`slab_shape`/`view_crop`/
  `view_visibility`/`wall_sweep` `operation=read`, `sheet_layout` `get_viewport_box`, `wall_finish` `match_core`,
  `manage_groups` `list`/`find_duplicates`/`annotation_report`: "this operation does not write; use validate_ids";
- a field spelt like an identity field but not exactly (`uniqueId`, `UniqueId`, `unique-id`, `uniqueIds`, `expectedCategory`,
  `expectedType`, `Identity`...), at any depth: the error names the right one (inside a plan only the children of covered
  tools are read);
- **a tool the guard does not cover, whatever its schema**: `unique_id`, `unique_ids`, `expected_category`, `expected_type` or `identity`
  at the top level that the tool does not declare itself (`quantities`, `plan_structure` and the other open
  schemas). The server refuses it (`esquema_invalido`); the add-in repeats the refusal (`entrada_invalida`) as a backstop;
- a **correction plan** (`apply_corrections` with `correction_plan.pasos[]`): each step is verified with the Spec of ITS tool
  (`herramienta`), as the children of `execute_plan` are; a step whose tool is not covered is refused if it carries identity;
- **an identity outside the arguments that run** (R2T m-2): in `apply_corrections` with a `correction_plan`, anywhere other than
  `correction_plan.pasos[i].argumentos` (for example `manual[]`, or a step's `evidencia[]`); in `execute_plan`, anywhere other than
  `actions[i].arguments` (for example `actions[0].notes[0]`, or a `meta` object). It would be ignored, so the call is refused with
  `entrada_invalida`, naming the path, writing nothing;
- fields that look like identity inside a **map keyed by names the user chose** (`write_params_verified` `set`, `tabular_source.value_columns`,
  `values`, `parameters`, `dimensions`... the list is `ElementIdentityGuard.UserKeyedMaps`, kept in step with the schemas by a test)
  are data and are not read: a parameter called `identity` or `Identity` is not an error;
- ids of a node that has `link_instance_id` (other than the link instance itself): they name elements of the linked
  document, which the host cannot vouch for;
- an `identity` that is text, except where the tool defines that text itself (exact paths: `require_gate.override.identity`
  of `save_document`/`export`, `prevention_gate.override.identity` of `audit_model`, `identity` of `plan_views`).

Ids that are not accepted in the map: the workflow `document_rooms` (`room_ids`, `plan_view_id`, `template_view_id`) does not
take identity fields at all; validate its ids first with `validate_ids`.

**A confirmation token is bound to the identity.** The digest is canonical (keys sorted, the string shorthand written out), so the same
map sent in another order is the same identity. The digest of the identity (taken before it is removed) is stored with the
token and joins the plan hash. An apply that omits the identity of its rehearsal, adds one the rehearsal did not have, or
changes it is refused as a request that was not rehearsed. (The digest counts as hash, not as presentation, though no tool
schema lists `identity`: it changes what the caller asked to be protected against.)

Not protectable by construction: `horizun_execute_python`. The script picks its ids inside code the bridge does not
parse; it must read `unique_id`s and verify them itself (`horizun_selection_context` `operation=validate_ids`).

## Tools, and the key a bare `unique_id` / `unique_ids` is compared with

Single = bare `unique_id` (first present). List = bare `unique_ids` (first present). Any id key of the node can be
named in `identity`.

| Tool | Single | List |
|---|---|---|
| `horizun_write_params_verified` | target_id, element_id, host_id (alone: target_id) | ids, element_ids, host_ids |
| `horizun_delete_verified`, `horizun_set_keynote`, `horizun_transform_elements` | target_id, element_id, host_id (alone: element_id) | ids, element_ids, host_ids (alone: ids for delete, element_ids otherwise) |
| `horizun_execute_plan` | each child as its own tool | each child as its own tool |
| `horizun_annotate` | `tag`: element_id, link_instance_id; other operations: view_id (per action) | `tag`: element_ids (the further elements of one tag) |
| `horizun_manage_views` | view_id, sheet_id, viewport_id | element_ids, view_ids, viewport_ids |
| `horizun_fix_planimetry` | element_id, viewport_id, schedule_instance_id, sheet_id, view_id | element_ids |
| `horizun_edit_dimensions` | element_id (per action) | - |
| `horizun_detail_2d` | element_id, view_id | element_ids (draw_order) |
| `horizun_manage_groups` | type_id | element_ids, group_ids |
| `horizun_view_visibility`, `horizun_manage_worksets` | target_id, element_id, host_id, instance_id, view_id, sheet_id | ids, element_ids, host_ids, wall_ids, member_ids, room_ids, view_ids |
| `horizun_embed_floors_in_toposolid`, `horizun_grade_toposolid_around_floors` | toposolid_id | element_ids (the floors) |
| `horizun_export` | schedule_id, ifc_filter_view_id | view_ids, family_ids, nwc_export_element_ids |
| `horizun_sheet_layout` | viewport_id, sheet_id | - |
| `horizun_planimetry_ops` | view_id, source_legend_id | grid_ids, datum_ids, view_ids, viewport_ids |
| `horizun_room_finishes` | - | room_ids |
| `horizun_pack_sheets` | sheet_id, viewport_id, schedule_instance_id, view_id, schedule_id | - |
| `horizun_manage_revisions` | revision_id, view_id | sheet_ids, remove_sheet_ids |
| `horizun_manage_schedules` | schedule_id | - |
| `horizun_manage_styles` | scheme_id, view_id (colour fill schemes; the `list_*` operations read and refuse identity fields) | - |
| `horizun_structural_connections` | - | member_ids |
| `horizun_create_elements`, `horizun_manage_curtain`, `horizun_slab_shape`, `horizun_manage_parameters`, `horizun_framing`, `horizun_create_railing`, `horizun_view_crop`, `horizun_wall_sweep`, `horizun_join_geometry`, `horizun_wall_finish`, `horizun_manage_phases`, `horizun_manage_assemblies_parts`, `horizun_apply_corrections`, `horizun_split_floor_loops`, `horizun_split_by_level`, `horizun_split_multilayer_walls`, `horizun_split_multilayer_slabs`, `horizun_ungroup_and_mark`, `horizun_copy_slab_elevations`, `horizun_rectangularize_walls`, `horizun_manage_materials`, `horizun_link_schedule`, `horizun_capture_view`, `horizun_model_diff` | target_id, element_id, host_id, instance_id, view_id, sheet_id | ids, element_ids, host_ids, wall_ids, member_ids, room_ids |

Schemas: the tools whose `tools/list` schema declares the fields say in the `unique_id` description which key it checks
(`manage_views`, `annotate`, `fix_planimetry`, `edit_dimensions`, `detail_2d`, `manage_groups`;
`delete_verified` and `transform_elements` declare `unique_ids`). **Every other covered tool accepts the same fields
without declaring them in `tools/list`** (the list has no room) and the server lets them through; the exact schema of any
covered tool, identity properties included, is the resource `horizun://contract/tools/{tool}`, and the error of a refused
call says the tool also accepts them. A tool that is not covered still refuses them (`esquema_invalido`).

## Writers that do not verify, and why

Each is listed with its reason in the test `M4_every_mutating_contract_tool_with_id_arguments_is_covered_refused_or_justified`:
Power BI (`budget_compare`, `power_bi_push`) and cloud (`cde_cloud`) ids belong to those services; `exchange`, `information_container`, `project_context`, `request_python_access` and `submit_job` ids name
records of the bridge; `navigate` moves the user's selection and writes nothing; `verify_changes` writes nothing to the active model.

## A tag over a link

`horizun_annotate` `operation=tag` on an element of a Revit link is sent with `link_instance_id` (the link INSTANCE in
the host model) and `linked_element_id` (an id inside the LINKED document). The bare `unique_id` and
`identity.link_instance_id` therefore compare with the link instance, never with the linked element: the active
document cannot vouch for an element of another file, and a UniqueId of the linked document would not resolve here.
Verify the linked element in its own document when it matters.

## validate_ids and the notice in health

`horizun_selection_context` `operation=validate_ids` (read-only): per `{id and/or unique_id, expected_category,
expected_type}` it answers `coincide` (with a `unique_id`), `coincide_categoria` (category/type match but no `unique_id`:
it does not detect a swap inside the category),  `cambio` (`id_reasignado`, `id_renumerado` with `current_id`, or the category or
type moved), `no_existe`, `entrada_invalida` or `sin_expectativa`. An entry with an id and nothing to compare (no
`unique_id`, no `expected_*`) is `sin_expectativa`: an element exists under that id, which proves nothing about it being
the one you read. `summary.all_match` is true only when every entry is `coincide`, so it needs a `unique_id` on every row.

`health.id_freshness` appears when a synchronize with central (the person's, the interface's or the bridge's) or a "Reload Latest" (R2U m-4: the
`DocumentReloadedLatest` event) happened after this session handed element ids out. It clears **only** when every id handed out before has been revalidated
afterwards (R2U M-A): by a `validate_ids` row that says `coincide`, `cambio` or `no_existe` for that id, or by a read that returns that same id again with its
`unique_id` (`query_model` rows, `list_elements`, `query_planimetry`, `query_relations`, `selection_context`, `query_structure`, `query_dimensions`,
`query_detail_2d`, `measure_geometry`, `get_dimension_references`, `model_scan`); and at least one such revalidation must have happened. A read of other
elements (the levels, after the three text notes) does **not** clear it. Ids are tracked from the `{element_id | id, unique_id}` pairs of those replies
(up to 200,000 per document; beyond that only a `validate_ids` can clear it). An id the session never received with a `unique_id` is not tracked: for those the
collaborative rule, not the notice, is what protects. The notice is a prompt; it does not decide the identity check.

## Live scenario

See the R2S report (renumbering by a second user, the three sync routes, one case per tool above) and the R2U report (collaborative rule in the test central, a non-collaborative model with Save As, Reload Latest).
