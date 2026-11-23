# Horizun Revit MCP — guide for agents / guía para agentes

**[English](#english) · [Español](#español)**

You are in the repository of **Horizun Revit MCP**: the MCP bridge between a
client (Claude Code, Codex, any MCP client) and an Autodesk Revit running on
this machine. Part of the [Horizun Hub](https://horizunhub.com) ecosystem.

Estás en el repositorio de **Horizun Revit MCP**: el puente MCP entre un cliente
(Claude Code, Codex, cualquier cliente MCP) y un Autodesk Revit corriendo en esta
máquina. Parte del ecosistema [Horizun Hub](https://horizunhub.com).

> **The name is written `Horizun`** — capital H, the rest lower case — every time
> it appears to a user, in any language. Never `HORIZUN`, never `horizun` as a
> word. It is a brand, not an acronym and not shouting. The only upper-case forms
> that exist are the tool names (`horizun_*`, always lower case) and the
> environment-variable prefix (`HORIZUN_REVIT_YEAR` and friends) — those are code
> identifiers, never how you refer to the product in prose.
>
> **El nombre se escribe `Horizun`** — H mayúscula, el resto en minúscula — cada
> vez que aparece ante un usuario, en cualquier idioma. Nunca `HORIZUN`, nunca
> `horizun` como palabra. Es una marca, no una sigla ni un grito. Las únicas
> formas en mayúscula que existen son los nombres de las herramientas (`horizun_*`,
> siempre en minúscula) y el prefijo de variables de entorno (`HORIZUN_REVIT_YEAR`
> y demás) — esos son identificadores de código, no cómo te refieres al producto.

---

## English

If the user asked you to install it, this is the whole procedure.

### Install the published release (default)

For “install this repo” or “install everything necessary”, use the published
installer. It includes the MCP server runtime and the add-ins; Git and the .NET
SDK are not required. Check Windows, an installed Revit 2023–2027, and that Revit
is closed first. Read the unsigned-release disclosure in README.md before using
the explicit acknowledgement below.

```powershell
powershell -ExecutionPolicy Bypass -File .\install-release.ps1 -AllowUnsigned
```

The bootstrap downloads the installer from the latest stable GitHub release,
verifies its SHA-256 against that same release, installs it and schedules safe
client registration. Report the durable installation status, including any
pending client restart or first Revit start; exit code zero alone is insufficient.
If it fails, preserve the error and installation log. Do not silently switch to
source compilation or install a development SDK to work around a release failure.

### Build from source (development or explicitly requested)

Everything is compiled from this tree, against the Revit already installed on
this machine. No executable is downloaded.

**Prerequisites** — check them first; the script checks them too:

- Windows with at least one Revit 2023–2027 installed
  (`C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` exists).
- The exact .NET SDK 10.0.400 on PATH (`dotnet --version` answers), fixed by
  `global.json` so release bytes do not depend on the latest installed patch.
  Revit ≤ 2024 still builds against .NET
  Framework 4.8 — the SDK-style projects restore the reference assemblies from
  NuGet, so the Visual Studio targeting pack is NOT required when NuGet restore
  is available. Verified on a machine without the pack: 2024 compiled with zero
  warnings. Only a fully offline machine needs the pack itself.
- **Revit closed.** The script refuses to run with Revit open and changes
  nothing when it refuses.

**The command:**

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It detects the Revit years present, compiles the add-in for each against its own
API, compiles the MCP server, installs everything, and **verifies by reading
every installed binary back** (stamped commit + SHA-256 against what was
staged). A build failure changes nothing; a later failure rolls back through its
undo ledger and reports the exact state.

Resulting paths:

- Add-in: `%APPDATA%\Autodesk\Revit\Addins\<year>\Horizun\`
- Server: `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe`

### Configure the MCP client

**Use the EXACT path the installer printed**, already expanded for this machine.
Do not retype it with `%LOCALAPPDATA%`: `cmd.exe` expands that variable and
**PowerShell does not**, so a config written that way points somewhere that does
not exist and the client shows no tools without saying why.

Installation and registration are two internal phases but one user action. Do not
rewrite the configuration of the Claude/Codex process that is currently running:
it may overwrite the edit when it exits. The installer runs
`complete-install.ps1` once and waits for it: it registers beside existing MCP
entries, verifies the configuration and tries `horizun_health` once. Nothing is
left running or scheduled. If Claude Code or Codex is open, it edits nothing and
reports `waiting_for_client_exit`. Setup offers Retry; afterwards the user closes
the client and runs **Horizun - complete installation** from the Start menu. If
Revit is not running, the state is `awaiting_revit`, and `horizun_health` from
the restarted client is the live check. Report the durable state from
`%LOCALAPPDATA%\Horizun\install-status.json`. The commands below are manual
recovery only.

```powershell
# Claude Code — user scope makes it available across projects
claude mcp add --scope user horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"

# Codex
codex mcp add horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex timeout settings — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf and other MCP clients
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe"
    }
  }
}
```

**Claude Desktop has its own route and does not need Claude Code.** The Windows
Setup is the universal installation path: it registers Codex and Claude Code
when their configuration exists, and delivers the Claude Desktop extension to
Documents\Horizun-Revit-MCP. The commands below are for a source checkout;
installed recovery paths are in docs/CLIENTS.md:

```powershell
pwsh -File scripts/install-claude-desktop-extension.ps1 -Extension   # the .mcpb extension
pwsh -File scripts/diagnose-integrations.ps1              # all supported clients, one screen
```

Claude Desktop installs a real Desktop Extension that names the same installed
stdio server as Codex and Claude Code. Its final **Install Extension** click is
inside Claude Desktop; the helper stages and validates the exact `.mcpb` and
records that pending action. `docs/CLIENTS.md` documents the universal installer,
every client-specific last step, and where an MSIX install really keeps
`claude_desktop_config.json`.

**ChatGPT Work uses OpenAI's Secure MCP Tunnel and does not need either CLI.**
Run `scripts/chatgpt-tunnel.ps1 -Status`; the helper names each remaining step,
protects the API key with Windows DPAPI and launches the same installed server.

TOML literal strings (single quotes) take Windows paths as written; JSON needs
every backslash doubled. **Raise the tool timeout** if your client has one: a
`model_scan` or a batch open holds Revit's UI thread for minutes, and a
60-second default abandons work that is still running — the bridge looks broken
when it is merely busy.

### First Revit start — tell the user about this

- Without an already trusted local signing certificate, Revit will show a
  **Security** dialog. Choose **Always Load** after verifying the build. A source
  install reuses an explicitly created trusted self-signing certificate when one
  exists; that is local trust, not a public publisher identity. The dialog can
  open **on another monitor** — a Revit that has been
  "starting" for minutes with the CPU idle is often this dialog hiding.
- With a document open, a **Horizun Hub** tab appears in the ribbon. Its
  **Connection status** button (**Estado de conexión** on a Spanish Revit - every
  label follows Revit's own language) answers "is this working, and which
  version?" without leaving Revit. **Advanced options** opens one menu with the
  owner-local controls (BIM mode, action history, pause MCP, central
  protection), each shown with its current state.

### Verify

With Revit open and the client restarted, call `horizun_health`. It must answer
`status: healthy` with the version and commit of the tree you compiled. A
"contract hash mismatch" error means one half is on an older build: close Revit
and run `install.ps1` again.

### How to work with the bridge

- When `model_scan` returns `resume`, continue by calling it again with `sections` = `resume.sections`.

- **`horizun_health` first, always.** The commands act on the *active* document,
  and health is what tells you which one that is.
- **Several Revit instances: one rule for every tool.** When only one instance is
  running, or `horizun_target` (or `HORIZUN_REVIT_PID`/`HORIZUN_REVIT_YEAR`) names
  one, every call goes there and every reply names it in `answered_by` (pid, year,
  `selected_by`). With two or more running and none named, every tool — reads
  included — refuses with `documento_objetivo_ambiguo`, `target_required: true` and
  the candidates in `instances`; nothing is sent to any Revit. `horizun_health` is the
  one that answers in that case (`estado: varias_instancias`, same code and list) so
  you can choose. **Except with `target_document`:** open in exactly one instance, the
  call goes there, reads at once and writes with their usual rehearsal and
  `confirmation_token`; `answered_by` names it (`selected_by: target_document`, year,
  pid, document, path). The same document open in two or more instances is refused,
  naming each (year, pid, path, unsaved changes) so the extra one can be closed; a
  document open in none is refused listing the documents that are open.
- `HORIZUN_PARENT_WATCH=0` disables parent monitoring; an unknown parent is not monitored and EOF still stops the server.
- **Client lifetime and presence.** The stdio server exits on EOF or when its original parent exits (checked each second, including its start time); outstanding requests drain through their existing deadlines. Health's `client_processes` lists recently observed server processes, including the caller, with process identity, parent liveness and last request. `orphan` is true only when the captured parent is known to have exited; null means unknown. Presence covers the last ten minutes, not all running processes; inactivity alone does not prove an orphan.
- **Read `health.python_runtime` before a Python call.** It distinguishes
  `not_authorized`, `not_loadable` with cause, `cold`, and `warm`; only the
  machine owner can grant access.
- **tools/list schemas are abridged, never incomplete.** Every argument, kind and
  operation is there, but per-variant text is folded to keep the list small. The exact
  schema of one tool or one variant is the resource
  `horizun://contract/tools/{tool}` / `horizun://contract/tools/{tool}/{variant}`,
  and a call that breaks the contract names the failing path and that URI.
- **3.0 discovery mode defaults to `full`.** An owner may opt a client into
  `HORIZUN_SCHEMA_MODE=facade`; then `horizun_list_tools` finds reachable
  tools, `horizun_describe_tool` reads an exact schema and `horizun_call_tool`
  dispatches under the same permission, toolset and contract checks. The
  smaller list alone does not establish better agent completion.
- **4.0 tool groups decide what is LISTED, never what may run.** A profile
  (`--profile`, `HORIZUN_PROFILE`, or `tool_profile` in settings) lists core
  plus its groups; `horizun_toolsets` activates a group for this session; a
  tool you cannot see is still callable through `horizun_call_tool` when the
  ceiling (permission profile, 3.x toolsets) admits it. See docs/TOOL-GROUPS.md.
- **A cross-app handoff needs evidence.** `horizun_exchange` emits and verifies
  `horizun.exchange/0` manifests and checks correction plans. Bind records to
  the `model_ref` of the intended model and check the receiving app's evidence
  before declaring the hop complete.
- **Understand the objective before the first write, and say it back.** A model is
  somebody's deliverable. Before the first typed write of a task you must know
  three things: WHAT outcome is wanted in the model, WHICH elements it applies to,
  and HOW the result will be recognised as correct. If any of the three is missing
  or reads two ways, **ask — with options and their trade-offs**, not an open
  question. Do not treat a missing instruction as permission to choose. One round
  of questions is cheap; a committed batch aimed at the wrong elements is not.
  Where the bridge itself cannot tell two readings apart it refuses rather than
  guesses; hold yourself to the same standard one level up. **And when nobody is
  at the keyboard** — a scheduled audit, a batch, the verification harness — state
  the ambiguity and do nothing, because a run that stops to ask has failed as
  surely as one that guessed.
- **Synchronizing with central can renumber ElementIds.** An id read before a sync may name another element
  afterwards. Typed writers that take existing-element ids accept, on the node that holds them,
  `identity: {<id_key>: <unique_id | {unique_id, expected_category, expected_type}>}` (every key named is verified;
  `expected_category` takes the name or `OST_*`, `expected_type` the name or "Family: Type"), or the bare
  `unique_id`/`unique_ids`/`expected_*`, which are compared with ONE key (the first present of the tool's list in
  [docs/IDENTITY-GUARD.md](docs/IDENTITY-GUARD.md)). On a mismatch the whole call is refused, writing nothing. The
  check covers every node of the request at any depth, plan children included; a node or tool with no ids to check
  refuses the fields (`entrada_invalida`) instead of ignoring them. Limits: a tag over a link compares with the link
  instance, not the linked element; `horizun_execute_python` **cannot be protected this way** (the script picks its ids
  inside code the bridge does not parse), so it must read `unique_id`s and verify them itself.
  Every covered tool accepts these fields whether or not `tools/list` declares them (the list has no room); the
  exact schema, identity properties included, is the resource `horizun://contract/tools/{tool}`, and the add-in verifies
  and consumes them before the command parses anything. **Only `unique_id` identifies an element**: `expected_category` /
  `expected_type` alone cannot tell two elements of the same category or type apart (the field case:
  three text notes swapped). So a category/type-only identity is **refused** (`entrada_invalida`, asking for the `unique_id`, nothing written):
  (1) on a **deletion** (`delete_verified`, the deleting operations of the other covered tools, the splits that replace the original; also
  `manage_assemblies_parts` `dissolve_parts`/`disassemble`, `manage_groups`
  `add_members` (only the group and its type, not the members it adds)/`edit_type_members`, `manage_revisions` `update_revision` with `remove_sheet_ids` (the ids that key
  holds, not the revision), `framing` with `spec.wall.method=curtain` (kept
  conservative: whether the curtain covers the wall is known only after the rehearsal) and the children of a node that names a deleting operation;
  `manage_schedules` `remove_fields`, `manage_parameters` `remove_binding` do
  not delete the element they name and are not deletions);
  (2) in **any collaborative model** (`Document.IsWorkshared`, so a local file of a central too), whoever made the element and whether or not a
  synchronize happened: that is where a synchronize renumbers ElementIds and where other people's elements live. If `IsWorkshared` cannot be read
  it counts as collaborative;
  (3) in a model that is **not** collaborative, on any element that is not ours: no Horizun mark of THIS user (Revit's `Application.Username`, or the
  Windows user when Revit has none), a mark made before marks named a user, another user's mark, an unreadable mark, or the copy of a mark made in
  this session (a mark of this session must correspond to an element a write of this session created, compared by `UniqueId` and followed through
  Save As and the first save). The user is compared without domain (`DOM\x` = `x`), without the `os:` prefix and without case, and by Windows SID
  when both the mark and this session have one. What `horizun_execute_python` creates is NOT ours (not marked, not in the ledger), and neither are the
  elements copied between documents by the Pro edition (they can duplicate somebody else's): they stay in the change ledger and in `verify_changes`, but a category / type
  alone is refused on them. The same belief governs the delete cascade (`allow_foreign_dependents`), `view_visibility` (no override: duplicate the view) and
  `view_crop` / `sheet_layout` (`allow_unmarked*`): a mark of another user, or a version 1 mark (no user), is somebody else's there too, except that in a model that is not
  collaborative a version 1 mark counts as ours for `view_crop` / `sheet_layout`. With the same SID, two different Revit user names are two people. Limits, documented: a mark of ANOTHER session of
  the same user cannot be told from a genuine one; two people on one generic account with no SID in the mark look like the same user, and a local
  (non-domain) SID differs on each machine; the ledger keeps the last 500 writes, so an older creation is refused (fail closed). **Since 5.0.4 the mark holds no personal data**: version 3 records an anonymous owner id (a random id per Windows user and machine) and the MCP version, and "this user" means that owner id; marks of version 2 (user, account, SID) are still read but never shown, and `horizun_manage_parameters` `operation=purge_legacy_marks` deletes them from a model (rehearsed, token, count re-read). Run it before handing a model out.
  Only on an element of ours, in a model that is not collaborative, does it proceed, and the reply carries, at its top level, `identity_strength: "categoria"`,
  `identity_warning` and `identity_check` with the keys that lacked a `unique_id`.
  **The refusal says exactly** (`<why>` is one of the three reasons below, `<id_key>` the id key that was checked):

  ```
  expected_category / expected_type alone do not identify an element: {why}. Send the unique_id of the element you mean (identity.{id_key} or unique_id / unique_ids). Take it from the read that gave you that id, or find the element again by its content (text, location, view) with horizun_query_model (response_mode full or compact, never summary) or horizun_list_elements. Never take it from a lookup by an id you held from before a synchronize: that id may now name another element, and horizun_selection_context operation=validate_ids returns holder_unique_id, not an identity, for an id sent without its unique_id. Nothing was written
  ```

  - deletion: `this operation deletes, and a deletion on an id that a synchronize may have renumbered cannot be undone`
  - collaborative model: `this is a collaborative (workshared) model: a synchronize with central renumbers ElementIds and other people's elements live in it, so a category / type cannot tell which element an id names, even on elements Horizun made`
  - not ours: `element {id} does not carry a Horizun ownership mark of this user (it is somebody else's, an older mark, a copy, or the mark could not be read), so it may be somebody else's work`

  **Getting the `unique_id`** (R2V B-1): from the read that gave you the id, or by finding the element again by its CONTENT (text, location, view) with
  `horizun_query_model` (`response_mode` full or compact; `summary` has no rows) or `horizun_list_elements`, whose rows carry it. Never from a lookup by an id
  you held from before a synchronize: `horizun_selection_context` `operation=validate_ids` with `{id, expected_category}` and no `unique_id` answers
  `coincide_categoria` / `sin_expectativa` / `cambio` with `found.holder_unique_id` (the element that holds that id TODAY), `identifies: false` and a
  warning, and never a `found.unique_id`: following the old wording ("read `found.unique_id`") copied the unique_id of the wrong note into the write.
  Sent with the `unique_id` you read, it answers `coincide`, or `cambio` with `current_id` (where that element lives now; `null` when its unique_id no longer exists, never the id of the element that took the old one).
  A tool the guard does not cover refuses the
  identity fields by name even when its schema is open (`quantities`, `plan_structure`). In `execute_plan` and in
  `apply_corrections` with a `correction_plan`, an identity anywhere outside the arguments that run (`manual[]`, `actions[0].notes[0]`...) is refused
  with `entrada_invalida` naming the path, never ignored. The whole coverage (accepted names and the id keys of every tool included) is in the contract
  hash. A confirmation token is bound to the identity of its rehearsal: an apply that omits or changes it
  is refused. Operations that write nothing (a capture_view with no temporary option, join_geometry
  `read`...) refuse identity fields saying so; a misspelt field (`uniqueId`, `unique-id`, `expectedType`, `Identity`)
  is refused naming the right one; ids inside arrays of arrays are named like `pairs[0][1]`; ids of a node with
  `link_instance_id` belong to the linked document and are not checked against the host.
  `horizun_selection_context` `operation=validate_ids` answers `coincide`/`coincide_categoria`/`cambio`/`no_existe`/`sin_expectativa`
  per id (an id with nothing to compare is never a match; `all_match` needs a `unique_id` on every row), and `health.id_freshness` appears when a synchronize or a "Reload Latest" happened after this session handed ids out (by a read OR by a write: the
  rows `{element_id, unique_id}` a write returns are tracked too); it clears only when every id handed out before has been revalidated (a `validate_ids` row
  that says `coincide`, `cambio` or `no_existe`, or a read or write that returns that same id again with its `unique_id`); a read of other elements does not
  clear it. An id that comes back with ANOTHER unique_id is reported in that reply as `ids_changed` (id, `was_unique_id`, `now_unique_id`). Rows of a linked
  model never revalidate a host id. Past 50,000 tracked ids per document the notice says `tracking_overflow` and is turned off by a `validate_ids` on the ids
  you are going to write. It is a prompt: it no longer decides the identity check (the collaborative rule does).
  This reaches the 4.2 writers too (`planimetry_ops`, `sheet_layout`, `view_visibility`, `manage_worksets`, `annotate`,
  `edit_dimensions`); a node that names several ids is checked on each key named in `identity`.
- **One command executes at a time, but concurrent calls are queued.** Up to 16
  requests wait in bounded FIFO order; the next receives explicit backpressure.
  Cancelling removes work only before it starts. Use `horizun_submit_job` plus
  `horizun_job_status` for work that should outlive the MCP request.
- **The contract**: no command reports work it did not verify. Every typed write
  is re-read from the model after the commit. `horizun_execute_python` does not
  provide that guarantee **at all**, which is why scripts run through it verify
  their own work in the structured `__output__` and why what comes back is
  labelled self-reported: the states are `self_reported_verified`,
  `completed_unverified`, `partial`, `failed`. There is no `verified` on the
  Python path and `host_verified` is always false — report it to the user as the
  script's testimony, not as the bridge's finding.
- **Typed first, Python as the fallback — never "not supported".** Prefer a typed
  command whenever one fully covers the operation. When none exists, or a failed
  typed call returns `fallback.allowed: true` — its machine-readable signal that
  no typed capability covers the request *and* nothing was written — generate
  minimal Revit Python and run it through `horizun_execute_python` (optionally
  `preflight=true` first, then execute in the same task). **Decide on that block,
  never on the wording of an error**: no block, or `allowed: false`, means do not
  fall back. **It arrives on the first, ordinary call** — `dry_run` defaults to
  true and the rehearsal publishes the verdict in `structuredContent` beside its
  own payload, so a successful reply with invalid rows still carries it; you
  never need to send `dry_run: false` to find out. `write_started: true` never accompanies `allowed: true`, because a
  typed write that failed mid-operation may have partially written and a Python
  retry would be a second write. **A mixed batch never grants the fallback**: if
  one action is uncovered and another has bad arguments, `allowed` is false and
  you get `capability_gaps` naming the uncovered indices — fix the invalid
  entries and resend the typed call first. `target_document` plus the
  active-document check apply to Python exactly as to every typed write.
- **When a script opens models, read `dialogs` before you report a failure.** The
  bridge CANCELS every modal dialog raised during a script — nobody is at the
  keyboard — so all Revit tells the script is `Opening was canceled`, which is not
  a diagnosis. The reply carries `dialogs` and `failures` beside `__output__`, and
  `revit_raised(since)` reads the same records from INSIDE the script, windowed to
  one call: `len(revit_raised())` before the open, the same number after it. Send
  long scripts as `code_path` rather than an inline string. To let ONE call
  continue past its dialog: `with dialog_answer('dismiss'):` — around that call
  only, never a whole run, because Revit reads OK on a close-with-changes dialog as
  Save. A model that will not open unattended remains a finding either way.
- **`horizun_execute_python` is disabled by default.** A fresh install uses
  `safe_write`: typed in-model writes are available, but arbitrary code is not.
  `safe_write` also lists `horizun_save_document` (save_local, relinquish_own, and
  `operation=sync_with_central` only with the owner's grant of "Synchronize with central") and
  `horizun_export` (format=pdf, and format=image of one view); each refuses the rest per call.
  The side profile `documentation` admits reads, the documentation toolset (minus styles and
  phases), PDF, one-view images and the local save; `delete_verified`/`write_params_verified` there
  touch only views, sheets, view-owned and annotation elements.
  A client may call `horizun_request_python_access` to show the question in Revit,
  but only the machine owner can approve it. The **Python ON/OFF** button grants
  persistent access until that same user revokes it; the admin script
  `scripts/enable-execute-python.ps1` provides the same durable opt-in and
  `-Disable` revokes it. Never edit settings to reverse the
  owner's choice. Compatible MCP clients refresh tools/list automatically after a
  change; restart once only when the client does not implement list-change notifications.
- This bridge is **organisation-neutral by design**: no company's standards or
  catalogues are compiled in. Where a command needs one, it is passed as an
  argument. The delivery workflows built on top live in
  [Horizun Hub](https://horizunhub.com).

- **Workshared models: work locally, synchronize once at the end.** While a task
  runs, save only the local file (`horizun_save_document` `save_local` never
  synchronizes). Synchronizing with central is a separate action at the end, when
  the work is verified and the person confirms: `operation=sync_with_central`
  rehearses (session writes, owned/borrowed elements, update-status sample) and
  applies with its `confirmation_token`; `horizun_save_document operation=sync_with_central` needs
  `safe_write` (the default) plus the owner's grant of "Synchronize with central"
  (Advanced options) - that grant opens this operation and nothing else of
  `full_write`; `horizun_document_session` needs `full_write`/`unsafe_code`. This is the policy in force in this
  branch (unreadable settings count as not granted). It is refused while a write of the session has a failed or
  unmeasured check (fix and re-check with `horizun_verify_changes`, or the person
  accepts with `accept_findings=true`, an argument of `horizun_save_document`;
  `horizun_document_session` does not declare it). **Each acceptance takes one kind of row**
  (INT-11): `accept_findings=true` accepts ONLY `unverified_python` rows (a script whose result is
  self-reported). A `failed_check` row (a spatial or post-commit check that failed or could not be
  measured: the live case was two duplicated walls) is cleared only by a clean `horizun_verify_changes`
  or by `accept_findings_ids`, an array that names EVERY element id of those rows (also an argument of
  `horizun_save_document` only). If one id is missing the sync is refused (`sync_blocked_unverified`,
  `write_started: false`) naming the missing ids and both ways out (`accept_findings_with.failed_checks`);
  a row without ids can only be rechecked. The confirmation token is bound to that list: an apply with
  other ids is refused. Replies carry `pending_sync` while local work is unsynced: remind the person to
  synchronize and relinquish. On `horizun_save_document` (every operation) and `horizun_document_session
  sync_with_central`, `pending_sync` is the block of the document operated on (`target_document`); when the
  active document is another one, its block comes apart in `active_document_pending_sync`, named.
  **Policy against 4.1 (CT-P1-2):** in 4.1 `horizun_save_document operation=sync_with_central` needed `full_write`
  or `unsafe_code`. Here `safe_write` plus the owner's grant of "Synchronize with central" opens it as well (that grant alone
  opens this operation, nothing else of `full_write`); `relinquish=borrowed_only` and the `precheck` block of the preview are new.
  **Sync-only option. Experimental option, off by default. It is not a finished feature, do not offer it as one; only the owner
  turns it on:** the owner may
  switch on `allow_sync_only_grant` in Revit (Advanced options > Sync only). Through
  `horizun_save_document`, `safe_write` (and `full_write`/`unsafe_code`, which never get
  less than `safe_write`) then sync without the other grant, always STRICT, in the dry
  run and again in the apply; if the older grant is also on, `safe_write` is still STRICT: non-empty comment, no unverified scripts and no unaccepted failed checks
  (`accept_findings` and `accept_findings_ids` are refused); failed checks must be corrected and re-checked, `relinquish=all` or `borrowed_only`, a preview that
  lists what the user holds and a token that binds the comment and that list, and a
  re-read afterwards (nothing borrowed left, no pending local changes, update status;
  a leftover is a failure). Only what the current user owns can be given back
  (guaranteed by Revit's `RelinquishOptions`); the re-read lists what others held
  before and no longer hold. An agent cannot turn it on; `horizun_health` shows
  `sync_only_grant_enabled`. With it off the decision is the one `int/4.2` had before this option (safe_write plus the owner's grant,
  full_write, unsafe_code); against 4.1 it is NEW (see the paragraph above). The option only adds texts, health and the contract hash.
  **What protects the owner's settings, honestly.** The two sync grants (`allow_sync_only_grant`, `sync_with_central_owner_granted`)
  are plain booleans in `settings.json` and are read as written. Any process that runs with the user's rights can edit that file:
  Python run through `horizun_execute_python` (`unsafe_code`), another program, or the agent's own shell. Nothing inside Revit can
  stop that, and this bridge does not pretend to. The real protection is the permission profile and the owner's decision NOT to
  grant `unsafe_code` (and not to give the agent a shell with that user's rights). What the bridge does: if the value of
  either sync grant is different after a `horizun_execute_python` run than before it, the script's reply carries
  `settings_changed: [...]` (key, before, after, `possible_cause`, `reverted: false`). Nothing is put back; it is a notice for the
  person. If the owner's writer stamped the key during the run, the notice says another Revit instance or the owner may have done
  it and does not blame the script. Sensitive keys of `settings.json`, all of which a process with the user's rights can change:
  the two grants above, `force_read_only_on_workshared`, `mcp_paused` and `permission_profile`.

- **ISO 19650 is a standard, not an organisation's rule set**, so its concepts are
  modelled directly. On a new project, before the first write, offer the MCP prompt
  `project-intake`: `horizun_project_context` asks for what is missing (role, stage,
  EIR, BEP, MIDP/TIDP, CDE folders and approvers, naming, classification, IDS,
  georeference, delivery) and writes one `project-context.json`.
  `horizun_information_container` names, seals and promotes containers across
  WIP/Shared/Published/Archived folders. IFC delivery proved against IDS
  (`horizun_deliver_ifc`) is in the Pro edition. See
  [docs/INFORMATION-MANAGEMENT.md](docs/INFORMATION-MANAGEMENT.md).

### Update

```bash
git pull
```

Close Revit and run `install.ps1` again. The server and the add-in share a
contract hash and are updated **together**; there is no partial deployment.

### Uninstall

Close Revit and the MCP client, then uninstall **Horizun Revit MCP** from Windows
Installed apps. Before uninstalling, the installed `server/client-tools/uninstall-cleanup.ps1`
helper can remove only the named `horizun-revit` entries from Claude and Codex. State in `%USERPROFILE%\.horizun\` and signing trust are preserved by
default; the helper purges either only when the user explicitly selects it.

### For contributors: before you call a merge or a change good

- Build the add-ins of **every Revit year, 2023 through 2027**, not only the one you work in. Each year compiles against its own API, and a type, overload or member that exists in one can be missing in another.
- The core and server test suites **do not see type errors in the add-in**: they never compile it against the Revit API. Green suites say nothing about whether the add-ins build, so a merge or change is not good until all five years compile with zero warnings.

---

## Español

Si el usuario te pidió "instálalo", este documento es el procedimiento completo.

### Instalar la versión publicada (opción habitual)

Ante «instala este repo» o «instala todo lo necesario», usa el instalador
publicado. Incluye el runtime del servidor MCP y los add-ins; no necesita Git ni
el SDK de .NET. Comprueba primero Windows, Revit 2023–2027 instalado y Revit
cerrado. Lee el aviso sobre las releases sin firma en README.md antes de usar
la aceptación explícita del siguiente comando.

```powershell
powershell -ExecutionPolicy Bypass -File .\install-release.ps1 -AllowUnsigned
```

El bootstrap descarga el instalador de la última release estable de GitHub,
comprueba su SHA-256 contra esa misma release, instala y programa el registro
seguro de los clientes. Reporta el estado durable, incluido cualquier reinicio
del cliente o primer arranque de Revit pendiente; un código de salida cero no
basta. Si falla, conserva el error y el log. No cambies silenciosamente a
compilación desde fuente ni instales un SDK de desarrollo para ocultar un fallo
de la instalación publicada.

### Compilar desde fuente (desarrollo o petición explícita)

Todo se compila desde este árbol, contra el Revit ya instalado en la máquina.
No se descarga ningún ejecutable.

**Requisitos** — compruébalos antes; el script también lo hace:

- Windows con al menos un Revit 2023–2027 instalado
  (`C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` existe).
- El SDK exacto .NET 10.0.400 en el PATH (`dotnet --version` responde), fijado
  por `global.json` para que los bytes no dependan del último parche instalado.
  Revit ≤ 2024 sigue compilando contra
  .NET Framework 4.8 — los proyectos SDK-style restauran los reference
  assemblies desde NuGet, así que el targeting pack de Visual Studio NO hace
  falta cuando hay restauración de NuGet disponible. Verificado en una máquina
  sin el pack: 2024 compiló sin una sola advertencia. Solo una máquina
  totalmente offline necesita el pack como tal.
- **Revit cerrado.** El script se niega a correr con Revit abierto y no cambia
  nada cuando se niega.

**El comando:**

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Detecta los años de Revit presentes, compila el add-in para cada uno contra su
propia API, compila el servidor MCP, lo instala todo, y **verifica leyendo de
vuelta cada binario instalado** (commit estampado + SHA-256 contra lo stageado).
Un fallo de compilación no cambia nada; un fallo posterior revierte con su libro
de deshacer y reporta el estado exacto.

Rutas resultantes:

- Add-in: `%APPDATA%\Autodesk\Revit\Addins\<año>\Horizun\`
- Servidor: `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe`

### Configurar el cliente MCP

**Usa la ruta EXACTA que imprimió el instalador**, ya expandida para esta
máquina. No la reescribas con `%LOCALAPPDATA%`: `cmd.exe` expande esa variable y
**PowerShell no**, así que una config escrita así apunta a un sitio que no
existe y el cliente no muestra herramientas sin decir por qué.

La instalación y el registro son dos fases internas pero una sola acción del
usuario. No reescribas la configuración del proceso de Claude/Codex que está
corriendo: puede pisar el cambio al cerrarse. El instalador ejecuta
`complete-install.ps1` una vez y lo espera: registra sin eliminar otros MCP,
verifica la configuración e intenta `horizun_health` una vez. No deja nada
corriendo ni programado. Si Claude Code o Codex están abiertos, no edita nada y
reporta `waiting_for_client_exit`. El Setup ofrece Reintentar; después, la
persona cierra el cliente y ejecuta **Horizun - completar instalacion** desde el
menú Inicio. Si Revit no está abierto, el estado es `awaiting_revit` y la
comprobación en vivo es `horizun_health` desde el cliente reiniciado. Reporta el
estado durable de `%LOCALAPPDATA%\Horizun\install-status.json`. Los comandos
siguientes quedan solo como recuperación manual.

```powershell
# Claude Code — el scope user lo deja disponible en todos los proyectos
claude mcp add --scope user horizun-revit -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"

# Codex
codex mcp add horizun-revit -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Ajustes de timeout de Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf y otros clientes MCP
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<usuario>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe"
    }
  }
}
```

**Claude Desktop tiene su propia ruta y no necesita Claude Code.** El Setup de
Windows es la instalación universal: registra Codex y Claude Code cuando existe
su configuración, y entrega la extensión de Claude Desktop en
Documentos\Horizun-Revit-MCP. Estos comandos son para un checkout del código;
las rutas de recuperación instaladas están en docs/CLIENTS.md:

```powershell
pwsh -File scripts/install-claude-desktop-extension.ps1 -Extension   # la extension .mcpb
pwsh -File scripts/diagnose-integrations.ps1              # todos los clientes soportados
```

Claude Desktop instala una extensión real que apunta al mismo servidor stdio
instalado que usan Codex y Claude Code. El último clic, **Install Extension**, se
hace dentro de Claude Desktop; el asistente prepara y valida el `.mcpb` exacto y
registra esa acción pendiente. `docs/CLIENTS.md` documenta el instalador universal,
el último paso de cada cliente y dónde guarda realmente
`claude_desktop_config.json` una instalación MSIX.

**ChatGPT Work usa Secure MCP Tunnel de OpenAI y no necesita ninguno de los dos
CLI.** Ejecuta `scripts/chatgpt-tunnel.ps1 -Status`; el asistente indica cada
paso pendiente, protege la clave API con Windows DPAPI y lanza el mismo servidor.

Comillas simples en TOML (cadena literal: las barras van tal cual); en JSON hay
que doblar cada barra. **Sube el tool timeout** si el cliente tiene uno: un
`model_scan` o una apertura por lote ocupa el hilo de UI de Revit durante
minutos, y un timeout de 60 s por defecto abandona trabajo que sigue corriendo
— el puente parece roto cuando solo está ocupado.

### Primer arranque de Revit — avisa al usuario de esto

- Sin un certificado local de firma ya confiable, Revit mostrará un diálogo de
  **Security**. Tras verificar el build, hay que elegir **Always Load**. Una
  instalación desde fuente reutiliza el certificado autofirmado que el usuario
  haya creado y confiado explícitamente; eso no es identidad pública. El diálogo
  puede abrirse **en otro monitor** — un Revit
  que lleva minutos "arrancando" con la CPU quieta suele tener este diálogo
  escondido.
- Con un documento abierto aparece la pestaña **Horizun Hub** en la cinta. Su
  botón **Estado de conexión** (en un Revit en inglés, **Connection status**: todos
  los rótulos siguen el idioma del propio Revit) responde "¿está funcionando y
  qué versión?" sin salir de Revit. **Opciones avanzadas** abre un solo menú
  con los controles locales del dueño (modo BIM, historial de acciones, pausar
  MCP, protección central), cada uno con su estado actual.

### Verificar

Con Revit abierto y el cliente reiniciado, llama a `horizun_health`. Debe
responder `status: healthy` con la versión y el commit del árbol que compilaste.
Un error de "contract hash mismatch" significa que una mitad quedó en un build
anterior: cierra Revit y vuelve a correr `install.ps1`.

### Cómo trabajar con el puente

- Si `model_scan` devuelve `resume`, continúa llamándolo de nuevo con `sections` = `resume.sections`.

- **`horizun_health` primero, siempre.** Los comandos actúan sobre el documento
  *activo*, y health es lo que te dice cuál es.
- **Varias instancias de Revit: una sola regla para todas las herramientas.** Con
  una sola instancia abierta, o cuando `horizun_target` (o `HORIZUN_REVIT_PID`/
  `HORIZUN_REVIT_YEAR`) nombra una, cada llamada va ahí y cada respuesta la nombra en
  `answered_by` (pid, año, `selected_by`). Con dos o más abiertas y ninguna elegida,
  todas las herramientas —también las lecturas— se niegan con
  `documento_objetivo_ambiguo`, `target_required: true` y los candidatos en
  `instances`; no se envía nada a ningún Revit. `horizun_health` es la que responde en
  ese caso (`estado: varias_instancias`, mismo código y lista) para que elijas.
  **Salvo con `target_document`:** si está abierto en una sola instancia, la llamada va
  ahí: las lecturas se ejecutan de una y las escrituras con su ensayo y `confirmation_token`
  normales; `answered_by` la nombra (`selected_by: target_document`, año, pid, documento,
  ruta). El mismo documento abierto en dos o más instancias se rechaza nombrando cada una
  (año, pid, ruta, cambios sin guardar) para cerrar la que sobra; un documento que no está
  abierto en ninguna se rechaza con la lista de los documentos abiertos.
- `HORIZUN_PARENT_WATCH=0` desactiva la vigilancia del padre; si es desconocido no se vigila y EOF sigue cerrando el servidor.
- **Vida y presencia de clientes.** El servidor stdio sale al cerrar stdin o al terminar su padre original (lo revisa cada segundo y compara la hora de inicio); las solicitudes pendientes se drenan con sus plazos existentes. `client_processes` en health lista los servidores observados recientemente, incluido el llamador, con identidad, estado del padre y última petición. `orphan` es true solo si se sabe que el padre terminó; null significa desconocido. La presencia cubre los últimos diez minutos, no todos los procesos vivos; la inactividad por sí sola no prueba que sea huérfano.
- **Lee `health.python_runtime` antes de Python.** Distingue
  `not_authorized`, `not_loadable` con causa, `cold` y `warm`; solo el dueño
  de la máquina concede acceso.
- **Los esquemas de tools/list están abreviados, nunca incompletos.** Están todos los
  argumentos, tipos y operaciones, pero el texto de cada variante se pliega para que la
  lista sea corta. El esquema exacto de una herramienta o de una variante es el recurso
  `horizun://contract/tools/{tool}` / `horizun://contract/tools/{tool}/{variant}`,
  y una llamada que rompe el contrato nombra la ruta que falla y esa URI.
- **El modo de descubrimiento 3.0 predeterminado es `full`.** El dueño puede
  optar por `HORIZUN_SCHEMA_MODE=facade` en un cliente: `horizun_list_tools`
  encuentra herramientas disponibles, `horizun_describe_tool` lee el esquema
  exacto y `horizun_call_tool` despacha bajo las mismas comprobaciones de
  permisos, toolsets y contrato. Una lista menor por sí sola no demuestra
  mejor desempeño del agente.
- **En 4.0 los grupos deciden qué se LISTA, nunca qué puede correr.** Un
  perfil (`--profile`, `HORIZUN_PROFILE` o `tool_profile` en settings) lista
  el núcleo y sus grupos; `horizun_toolsets` activa un grupo en la sesión; una
  herramienta que no ves se puede llamar con `horizun_call_tool` si el techo
  (perfil de permisos, toolsets 3.x) la admite. Ver docs/TOOL-GROUPS.md.
- **Un salto entre aplicaciones necesita evidencia.** `horizun_exchange`
  emite y verifica manifiestos `horizun.exchange/0` y comprueba planes de
  corrección. Vincula cada registro al `model_ref` previsto y comprueba la
  evidencia de la aplicación receptora antes de declarar terminado el salto.
- **Entiende el objetivo antes de la primera escritura, y devuélvelo dicho.** Un
  modelo es el entregable de alguien. Antes de la primera escritura tipada de una
  tarea tienes que saber tres cosas: QUÉ resultado se quiere en el modelo, A QUÉ
  elementos aplica, y CÓMO se reconocerá que quedó bien. Si falta alguna de las
  tres o se lee de dos maneras, **pregunta — con opciones y su contrapartida**, no
  con una pregunta abierta. Que falte una instrucción no es permiso para elegir.
  Una ronda de preguntas es barata; un lote commiteado sobre los elementos
  equivocados no. Donde el puente mismo no puede distinguir dos lecturas, se
  niega en vez de adivinar; sostén ese mismo estándar un nivel más arriba. **Y
  cuando no hay nadie al teclado** — una auditoría programada, un lote, el harness
  de verificación — enuncia la ambigüedad y no hagas nada: una corrida que se
  detiene a preguntar ha fallado igual que una que adivinó.
- **Sincronizar con el central puede renumerar los ElementId.** Un id leído antes de sincronizar puede nombrar otro
  elemento después. Los escritores tipados que reciben ids de elementos existentes aceptan, en el nodo que los lleva,
  `identity: {<clave_id>: <unique_id | {unique_id, expected_category, expected_type}>}` (se verifica cada clave nombrada;
  `expected_category` admite el nombre u `OST_*`, `expected_type` el nombre o «Familia: Tipo»), o los campos sueltos
  `unique_id`/`unique_ids`/`expected_*`, que se comparan con UNA sola clave (la primera presente de la lista de la
  herramienta en [docs/IDENTITY-GUARD.md](docs/IDENTITY-GUARD.md)). Si no coincide, la llamada entera se rechaza sin
  escribir nada. La comprobación cubre todos los nodos de la petición, a cualquier profundidad, hijos de un plan
  incluidos; un nodo o herramienta sin ids que comprobar rechaza los campos (`entrada_invalida`) en vez de ignorarlos.
  Límites: un tag sobre un vínculo se compara con la instancia del vínculo, no con el elemento vinculado;
  `horizun_execute_python` **no se puede proteger así** (el script elige sus ids dentro de código que el puente no
  interpreta), y debe leer `unique_id` y verificarlos él mismo. Toda herramienta cubierta acepta estos campos aunque
  `tools/list` no los declare (no hay margen); el esquema exacto, con las propiedades de identidad, es el recurso
  `horizun://contract/tools/{tool}`, y el add-in los verifica y consume antes de que el comando lea nada. **Solo
  `unique_id` identifica un elemento**: `expected_category`/`expected_type` solos no distinguen dos
  elementos de la misma categoría o tipo (el caso de campo: tres notas de texto intercambiadas). Por eso una
  identidad de solo categoría/tipo se **rechaza** (`entrada_invalida`, pidiendo el `unique_id`, sin escribir nada):
  (1) en un **borrado** (`delete_verified`, las operaciones de borrado de las demás herramientas cubiertas y las divisiones que reemplazan
  el original; también `manage_assemblies_parts` `dissolve_parts`/`disassemble`,
  `manage_groups` `add_members` (solo el grupo y su tipo, no los miembros que agrega)/`edit_type_members`, `manage_revisions` `update_revision` con `remove_sheet_ids` (los ids de esa clave, no la revisión), `framing` con `spec.wall.method=curtain`
  (se deja conservador: si la cortina cubre el muro entero solo se sabe después del ensayo) y los hijos de un nodo que nombra una operación de
  borrado; `manage_schedules` `remove_fields`, `manage_parameters` `remove_binding`
  no borran el elemento que nombran y no cuentan como borrado);
  (2) en **cualquier modelo colaborativo** (`Document.IsWorkshared`, también un archivo local de un central), sea quien sea quien hizo el elemento y
  haya o no una sincronización: ahí es donde una sincronización renumera los ElementId y donde viven elementos de otros. Si no se puede leer
  `IsWorkshared`, cuenta como colaborativo;
  (3) en un modelo que **no** es colaborativo, en cualquier elemento que no es nuestro: sin marca Horizun de ESTE usuario (`Application.Username` de
  Revit, o el usuario de Windows si Revit no tiene), una marca hecha antes de que las marcas nombraran un usuario, la marca de otro usuario, una marca
  ilegible, o la copia de una marca hecha en esta sesión (una marca de esta sesión debe corresponder a un elemento que una escritura de esta sesión
  creó, comparado por `UniqueId` y que sigue al documento en «Guardar como» y en el primer guardado). El usuario se compara sin dominio (`DOM\x` = `x`), sin el
  prefijo `os:` y sin distinguir mayúsculas, y por SID de Windows cuando lo tienen la marca y esta sesión. Lo que crea `horizun_execute_python` NO es nuestro (ni
  se marca ni entra al libro), y tampoco lo copiado entre documentos por la edición Pro (puede duplicar elementos ajenos): quedan en el libro de cambios y en
  `verify_changes`, pero una categoría / tipo sola se rechaza sobre ellos. La misma creencia rige el borrado en cascada (`allow_foreign_dependents`),
  `view_visibility` (sin anulación: duplica la vista) y `view_crop` / `sheet_layout` (`allow_unmarked*`): la marca de otro usuario, o una marca de versión 1 (sin usuario), es ajena también ahí; salvo que en un modelo no colaborativo una marca de versión 1 cuenta como nuestra para `view_crop` / `sheet_layout`. Con el mismo SID, dos nombres de usuario de Revit distintos son dos personas.
  Límites documentados: una marca de OTRA sesión del mismo usuario no se distingue de una genuina; dos personas con una misma cuenta genérica y sin SID en la
  marca se ven como el mismo usuario, y un SID local (no de dominio) cambia en cada máquina; el libro guarda las últimas 500 escrituras, así que una creación
  más vieja se rechaza (falla cerrada). **Desde la 5.0.4 la marca no lleva datos personales**: la versión 3 guarda un id anónimo de dueño (un id aleatorio por usuario de Windows y equipo) y la versión del MCP, y «este usuario» es ese id; las marcas versión 2 (usuario, cuenta, SID) se siguen leyendo pero nunca se muestran, y `horizun_manage_parameters` `operation=purge_legacy_marks` las borra de un modelo (ensayo, token, conteo releído). Córrelo antes de repartir un modelo.
  Solo sobre un elemento nuestro, en un modelo que no es colaborativo, la escritura procede, y la respuesta trae, en el nivel superior,
  `identity_strength: "categoria"`, `identity_warning` e `identity_check` con las claves sin `unique_id`.
  **El rechazo dice exactamente** (`<why>` es una de las tres razones de abajo, `<id_key>` la clave de id verificada):

  ```
  expected_category / expected_type alone do not identify an element: {why}. Send the unique_id of the element you mean (identity.{id_key} or unique_id / unique_ids). Take it from the read that gave you that id, or find the element again by its content (text, location, view) with horizun_query_model (response_mode full or compact, never summary) or horizun_list_elements. Never take it from a lookup by an id you held from before a synchronize: that id may now name another element, and horizun_selection_context operation=validate_ids returns holder_unique_id, not an identity, for an id sent without its unique_id. Nothing was written
  ```

  - borrado: `this operation deletes, and a deletion on an id that a synchronize may have renumbered cannot be undone`
  - modelo colaborativo: `this is a collaborative (workshared) model: a synchronize with central renumbers ElementIds and other people's elements live in it, so a category / type cannot tell which element an id names, even on elements Horizun made`
  - no es nuestro: `element {id} does not carry a Horizun ownership mark of this user (it is somebody else's, an older mark, a copy, or the mark could not be read), so it may be somebody else's work`

  **Cómo obtener el `unique_id`** (R2V B-1): de la lectura que te dio el id, o volviendo a encontrar el elemento por su CONTENIDO (texto, ubicación, vista)
  con `horizun_query_model` (`response_mode` full o compact; `summary` no trae filas) o `horizun_list_elements`, cuyas filas lo traen. Nunca de una búsqueda
  por un id que tenías desde antes de una sincronización: `horizun_selection_context` `operation=validate_ids` con `{id, expected_category}` y sin `unique_id`
  responde `coincide_categoria` / `sin_expectativa` / `cambio` con `found.holder_unique_id` (el elemento que ocupa ese id HOY), `identifies: false` y un
  aviso, y nunca un `found.unique_id`: seguir el texto anterior («lee `found.unique_id`») copiaba a la escritura el unique_id de la nota equivocada.
  Con el `unique_id` que leíste responde `coincide`, o `cambio` con `current_id` (dónde vive hoy ese elemento; `null` si su unique_id ya no existe, nunca el id del elemento que ocupó el anterior).
  Una herramienta no cubierta rechaza por su nombre
  los campos de identidad aunque su esquema sea abierto (`quantities`, `plan_structure`). En `execute_plan` y en
  `apply_corrections` con un `correction_plan`, una identidad fuera de los argumentos que se ejecutan (`manual[]`, `actions[0].notes[0]`...) se
  rechaza con `entrada_invalida` nombrando la ruta, nunca se ignora. Toda la cobertura (nombres aceptados y claves de id de cada herramienta
  incluidos) está en el hash del contrato. Un token de confirmación queda
  atado a la identidad de su ensayo: un apply que la omite o la cambia se rechaza. Las operaciones que no escriben
  (un capture_view sin opciones temporales, `read` de join_geometry...) rechazan los campos de
  identidad diciéndolo; un campo mal escrito (`uniqueId`, `unique-id`, `expectedType`, `Identity`) se rechaza con el
  nombre correcto; los ids dentro de arreglos de arreglos se nombran como `pairs[0][1]`; los ids de un nodo con
  `link_instance_id` son del documento vinculado y no se comprueban contra el anfitrión. `horizun_selection_context`
  `operation=validate_ids` responde `coincide`/`coincide_categoria`/`cambio`/`no_existe`/`sin_expectativa` por id (un id sin nada
  con qué compararlo nunca es una coincidencia; `all_match` exige `unique_id` en cada fila), y `health.id_freshness` aparece cuando hubo una sincronización o un «Reload Latest» después de que esta sesión entregara ids (por una lectura O por una escritura:
  las filas `{element_id, unique_id}` que devuelve una escritura también se rastrean); solo se limpia cuando se revalidó cada id entregado antes (una fila de
  `validate_ids` con `coincide`, `cambio` o `no_existe`, o una lectura o escritura que devuelve ese mismo id con su `unique_id`); una lectura de otros elementos no lo
  limpia. Un id que vuelve con OTRO unique_id se informa en esa respuesta como `ids_changed` (id, `was_unique_id`, `now_unique_id`). Las filas de un modelo vinculado
  nunca revalidan un id del anfitrión. Pasados 50.000 ids rastreados por documento el aviso dice `tracking_overflow` y se apaga con un `validate_ids` de los ids que vas a
  escribir. Es un aviso: ya no decide la identidad (la regla colaborativa lo hace).
  Alcanza también a los escritores de 4.2 (`planimetry_ops`, `sheet_layout`, `view_visibility`, `manage_worksets`, `annotate`,
  `edit_dimensions`); un nodo que nombra varios ids se comprueba en cada clave nombrada en `identity`.
- **Se ejecuta un comando a la vez, pero las llamadas concurrentes se encolan.**
  Hasta 16 esperan en FIFO acotada; la siguiente recibe backpressure explícito.
  La cancelación solo elimina trabajo antes de empezar. Usa
  `horizun_submit_job` y `horizun_job_status` para trabajos largos.
- **El contrato**: ningún comando reporta trabajo que no verificó. Toda escritura
  tipada se relee del modelo tras el commit. `horizun_execute_python` **no ofrece
  esa garantía en absoluto**, y por eso los scripts verifican su propio trabajo en
  el `__output__` estructurado y lo que vuelve se etiqueta como autorreportado:
  los estados son `self_reported_verified`, `completed_unverified`, `partial`,
  `failed`. En la ruta Python no existe `verified` y `host_verified` siempre es
  false — repórtalo al usuario como testimonio del script, no como hallazgo del
  puente.
- **Tipado primero, Python como respaldo — nunca "no soportado".** Prefiere un
  comando tipado cuando cubra la operación completa. Cuando no exista, o una
  llamada tipada fallida devuelva `fallback.allowed: true` — su señal legible por
  máquina de que ninguna capacidad tipada cubre lo pedido *y* no se escribió nada
  — genera el Python de Revit mínimo y córrelo con `horizun_execute_python` (si
  quieres, `preflight=true` primero y ejecuta en la misma tarea). **Decide por ese
  bloque, nunca por cómo esté redactado un error**: sin bloque, o con
  `allowed: false`, no caigas a Python. **Llega en la primera llamada normal** —
  `dry_run` viene en true y el ensayo publica el veredicto en `structuredContent`
  junto a su propio payload, así que una respuesta exitosa con filas inválidas
  también lo trae; nunca hace falta mandar `dry_run: false` para enterarte. `write_started: true` nunca acompaña a
  `allowed: true`, porque una escritura tipada que falló a mitad pudo escribir
  parcialmente y un reintento en Python sería una segunda escritura.
  **Un lote mixto nunca concede el fallback**: si una acción no está cubierta y
  otra trae argumentos malos, `allowed` es false y llegan `capability_gaps` con
  los índices no cubiertos — corrige primero las entradas inválidas y reenvía la
  llamada tipada. `target_document` más el control de documento activo aplican a
  Python igual que a toda escritura tipada.
- **Cuando un script abre modelos, lee `dialogs` antes de reportar un fallo.** El
  puente CANCELA todo diálogo modal levantado durante un script —no hay nadie al
  teclado—, así que lo único que Revit le dice al script es `Opening was canceled`,
  que no es un diagnóstico. La respuesta trae `dialogs` y `failures` junto a
  `__output__`, y `revit_raised(since)` lee los mismos registros DESDE DENTRO del
  script, acotados a una llamada: `len(revit_raised())` antes de abrir, ese mismo
  número después. Manda los scripts largos como `code_path` en vez de una cadena
  inline. Para dejar que UNA llamada continúe pese a su diálogo:
  `with dialog_answer('dismiss'):` — alrededor de esa llamada y nada más, nunca de
  una corrida entera, porque Revit lee OK en el diálogo de cerrar-con-cambios como
  Guardar. Un modelo que no abre desatendido sigue siendo un hallazgo igual.
- **`horizun_execute_python` viene deshabilitado por defecto.** Un cliente puede
  llamar `horizun_request_python_access` para mostrar la solicitud en Revit, pero
  solo el dueño de la máquina puede aprobarla. El botón **Python ON/OFF** concede
  acceso persistente hasta que ese mismo usuario lo revoque; el script
  `scripts/enable-execute-python.ps1` ofrece el mismo opt-in durable y `-Disable`
  revoca ambas rutas. Un
  `enable_execute_python=false` explícito o un perfil por debajo de
  `unsafe_code` siempre se respeta, y no debes editar `settings.json` para
  revertirlo. Los clientes compatibles refrescan la lista automáticamente;
  reinicia solo si el cliente no implementa la notificación de cambio.
- Este puente es **neutral por diseño**: no lleva estándares ni catálogos de
  ninguna organización compilados dentro. Donde un comando necesita uno, se pasa
  como argumento. Los flujos de entrega construidos encima viven en
  [Horizun Hub](https://horizunhub.com).

- **Modelos colaborativos: trabaja en local y sincroniza una vez, al final.**
  Mientras corre la tarea, guarda solo el archivo local (`horizun_save_document`
  `save_local` nunca sincroniza). La sincronización con el central es una acción
  aparte al final, cuando el trabajo está verificado y la persona lo confirma:
  `operation=sync_with_central` ensaya (escrituras de la sesión, elementos propios
  y prestados, muestra de estado) y aplica con su `confirmation_token`; `horizun_save_document operation=sync_with_central` requiere
  `safe_write` (el predeterminado) más la autorización del dueño de «Sincronizar con
  el central» (Opciones avanzadas): esa autorización abre esta operación y nada más
  de `full_write`; `horizun_document_session` requiere `full_write`/`unsafe_code`. Esta es la política
  vigente en esta rama (una configuración ilegible cuenta como no concedida). Se niega mientras una
  escritura de la sesión tenga una verificación fallida o sin medir (corrige y
  revisa con `horizun_verify_changes`, o la persona la acepta con
  `accept_findings=true`, argumento de `horizun_save_document`; `horizun_document_session`
  no lo declara). **Cada aceptación cubre un solo tipo de fila** (INT-11): `accept_findings=true`
  acepta SOLO filas `unverified_python` (un script cuyo resultado es autorreportado). Una fila
  `failed_check` (una verificación espacial o posterior al commit que falló o no se pudo medir: el
  caso en vivo fueron dos muros duplicados) solo se quita con una revisión limpia de
  `horizun_verify_changes` o con `accept_findings_ids`, un arreglo que nombra CADA id de elemento de
  esas filas (también argumento solo de `horizun_save_document`). Si falta un id, la sincronización se
  niega (`sync_blocked_unverified`, `write_started: false`) nombrando los ids que faltan y las dos
  salidas (`accept_findings_with.failed_checks`); una fila sin ids solo se puede revisar de nuevo. El
  token de confirmación queda atado a esa lista: una aplicación con otros ids se rechaza. Las respuestas
  traen `pending_sync` mientras haya trabajo local sin sincronizar: recuérdale a la persona sincronizar
  y liberar préstamos. En `horizun_save_document` (todas las operaciones) y en `horizun_document_session
  sync_with_central`, `pending_sync` es el del documento sobre el que se operó (`target_document`); si el
  activo es otro, el suyo va aparte en `active_document_pending_sync`, con su nombre.
  **Política frente a 4.1 (CT-P1-2):** en 4.1 `horizun_save_document operation=sync_with_central` pedía `full_write`
  o `unsafe_code`. Aquí también la abre `safe_write` más la autorización del dueño de «Sincronizar con el central» (esa
  autorización abre esta operación y nada más de `full_write`); `relinquish=borrowed_only` y el bloque `precheck` de la vista previa son nuevos.
  **Opción «solo sincronizar». Opción experimental, apagada por defecto. No es una función terminada, no la ofrezcas como tal; solo el dueño la enciende:** el
  dueño puede encender `allow_sync_only_grant` en Revit (Opciones avanzadas > Solo
  sincronizar). Por `horizun_save_document`, `safe_write` (y `full_write`/`unsafe_code`,
  que nunca tienen menos que `safe_write`) sincronizan entonces sin la otra concesión,
  siempre ESTRICTAS, en el ensayo y de nuevo en la aplicación; si la concesión
  anterior también está encendida, `safe_write` sigue en modo ESTRICTO:
  comentario no vacío, sin scripts no verificados ni verificaciones fallidas; se rechazan `accept_findings` y `accept_findings_ids` y hay que corregir y revisar los hallazgos, `relinquish=all` o `borrowed_only`, una vista previa que lista lo que
  posee el usuario y un token que cubre el comentario y esa lista, y una relectura
  después (nada prestado, sin cambios locales pendientes, estado de actualización;
  un sobrante es un fallo). Solo se puede ceder lo que posee el usuario actual
  (lo garantiza `RelinquishOptions` de Revit); la relectura lista lo que otros tenían
  y ya no tienen. Un agente no puede encenderla; `horizun_health` muestra
  `sync_only_grant_enabled`. Apagada, la decisión es la que `int/4.2` tenía antes de esta opción (safe_write más la
  autorización del dueño, full_write, unsafe_code); frente a 4.1 es NUEVA (véase el párrafo anterior). La opción solo agrega textos,
  health y el hash de contrato.
  **Qué protege de verdad los ajustes del dueño.** Las dos concesiones de sincronización (`allow_sync_only_grant`,
  `sync_with_central_owner_granted`) son booleanos simples en `settings.json` y se leen tal como están escritos. Cualquier proceso
  que corra con los permisos del usuario puede editar ese archivo: Python ejecutado con `horizun_execute_python` (`unsafe_code`),
  otro programa o el propio shell del agente. Nada dentro de Revit puede impedirlo y este puente no finge que sí. La protección
  real es el perfil de permisos y la decisión del dueño de NO conceder `unsafe_code` (ni darle al agente un shell con los permisos
  de ese usuario). Lo que el puente sí hace: si el valor de alguna de las dos concesiones es distinto después de una corrida de
  `horizun_execute_python` que antes, la respuesta del script trae `settings_changed: [...]` (clave, antes, después,
  `possible_cause`, `reverted: false`). No se revierte nada; es un aviso para la persona. Si el escritor del dueño selló la clave
  durante la corrida, el aviso dice que pudo ser otra instancia de Revit o el dueño y no culpa al script. Claves sensibles de
  `settings.json`, todas modificables por un proceso con los permisos del usuario: las dos concesiones anteriores,
  `force_read_only_on_workshared`, `mcp_paused` y `permission_profile`.

- **ISO 19650 es una norma, no las reglas de una organización**, así que sus
  conceptos se modelan directamente. En un proyecto nuevo, antes de la primera
  escritura, ofrece el prompt MCP `project-intake`: `horizun_project_context` pregunta
  lo que falta (rol, fase, EIR, BEP, MIDP/TIDP, carpetas del CDE y quién aprueba,
  nomenclatura, clasificación, IDS, georreferencia, entrega) y escribe un único
  `project-context.json`. `horizun_information_container` nombra, sella y promueve
  contenedores entre WIP/Compartido/Publicado/Archivado. La entrega IFC demostrada
  contra IDS (`horizun_deliver_ifc`) está en la edición Pro. Ver
  [docs/INFORMATION-MANAGEMENT.md](docs/INFORMATION-MANAGEMENT.md).

### Actualizar

```bash
git pull
```

Cierra Revit y vuelve a correr `install.ps1`. El servidor y el add-in comparten
un hash de contrato y se actualizan **juntos**; no hay despliegue parcial.

### Desinstalar

Cierra Revit y el cliente MCP, y desinstala **Horizun Revit MCP** desde
Aplicaciones instaladas de Windows. Antes, el asistente instalado `server/client-tools/uninstall-cleanup.ps1`
puede quitar únicamente las entradas `horizun-revit` de Claude y Codex. El estado en `%USERPROFILE%\.horizun\` y la
confianza de firma se conservan por defecto; el helper solo purga cada uno si el
usuario lo selecciona explícitamente.

### Para quien desarrolla o contribuye: antes de dar por buena una fusión o un cambio

- Compila los add-ins de **todos los años de Revit, de 2023 a 2027**, no solo el año en el que trabajas. Cada año compila contra su propia API, y un tipo, una sobrecarga o un miembro que existe en uno puede faltar en otro.
- Las suites de núcleo y de servidor **no ven errores de tipos del add-in**: nunca lo compilan contra la API de Revit. Que estén en verde no dice si los add-ins compilan, así que una fusión o un cambio no está bien hasta que los cinco años compilen con cero advertencias.
