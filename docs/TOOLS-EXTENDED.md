# Extended tool notes

Detail that does not fit a tool description in `tools/list`. Each section belongs to
one feature; the tool descriptions stay short and point here.

## Impact preview (MCP Apps)

**What it is.** An MCP App served at `ui://horizun/impact-preview`
(`text/html;profile=mcp-app`, MCP Apps extension, spec 2026-01-26). A host that
supports MCP Apps shows it beside the reply of a bulk write's **rehearsal**
(`dry_run=true`, the default) so a person can see what the write will touch before
approving it, and take some elements out.

**Which tools declare it.** Only the five whose rehearsal payload it knows how to read,
through `_meta.ui.resourceUri` in `tools/list`:

| Tool | Rows shown | What "exclude" removes from the request |
|---|---|---|
| `horizun_write_params_verified` | one per write (`rows[].index`), before -> requested | that entry of `writes[]` |
| `horizun_set_keynote` | one per resolved target (type or instance), current -> new keynote | every id in `element_ids` that resolved to that target |
| `horizun_delete_verified` | one per requested id and per cascade (`change_preview`) | `mode=ids`: the id from `ids`; `mode=purge_unused`: the id is added to `protect_ids`. A cascade row cannot be excluded alone: exclude the id that causes it |
| `horizun_transform_elements` | one per element (`change_preview`), operation / type / pin before -> after | the id from each operation's `element_ids`; an operation left empty is dropped |
| `horizun_create_elements` | one per planned element (`create:<index>`), kind, type, level | that entry of `elements[]` |

A rehearsal expanded from a CSV (`tabular_source`) is shown but cannot be narrowed row by
row: the rows are generated from the file on every call.

**What it shows.** How many elements the resolved plan holds (`plan_resolved.elements`),
counts by category and by level, a table of the rows with before -> after where the reply
has both, and warnings: elements you did not name that change too (shared type,
cascade), a blast radius that is a lower bound, unresolved entries, a withheld token,
and "only N of M rows are shown - the rest WILL be applied" when `change_preview` was
truncated (it carries at most 50 rows; exclusion is only possible for shown rows).

**The contract it keeps.** A `confirmation_token` approves ONE request: all five commands
fold the field the app narrows into their plan hash (`ImpactPreviewAppTests` asserts it
against the command sources). So the app never spends an old token on fewer elements:

1. With rows excluded, **Rehearse without N excluded** calls the same tool again with
   `dry_run=true`, the reduced request, and no token or idempotency key. The new
   rehearsal replaces the old one on screen, with a check that the excluded rows left
   the plan and that it did not grow.
2. With nothing excluded, **Apply** calls the tool with the arguments of the rehearsal on
   screen, `dry_run=false`, that rehearsal's own `confirmation_token` and a fresh
   `idempotency_key` (kept for a retry of the same token, so a retry replays instead of
   writing twice). The reply's `application.state` is shown; the button is then spent.

The app only calls tools when the host grants it (`hostCapabilities.serverTools`) and it
knows the original arguments (`ui/notifications/tool-input`); otherwise it is a read-only
view and the apply goes through the chat as always. After a re-rehearsal or an apply it
tells the model what happened through `ui/update-model-context`. The command's own gate
still decides: a stale plan, an expired token or a different document is refused by the
bridge exactly as for any other client.

**What it does not do.** It fetches nothing and loads nothing: no URL, no CDN, empty CSP
(`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`). It renders only
what the rehearsal said. No view thumbnail: `horizun_capture_view` holds Revit's UI thread
and would be a tool call the user did not ask for. A host without MCP Apps sees exactly
the reply it saw before.

**Hosts.** The MCP Apps project lists ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use and Alpic Playground as supporting hosts
([ext-apps README](https://github.com/modelcontextprotocol/ext-apps), read 2026-09-24).
Not measured here in any of them; the flow was exercised against a scripted host that
speaks the 2026-01-26 messages.

**Tests.** `tests/Horizun.Server.Tests/ImpactPreviewAppTests.cs` (resource, mime type,
CSP, declaring tools, tools/list cost, no network, plan-hash coverage, field names tied to
the emitting source) and `ImpactPreview/adapter.test.js`, the pure adapter under node
against five synthetic fixtures (no live capture of these rehearsals existed when it was
written). `scripts/live-probes/impact-preview.probes.ps1` measures the real replies in
Revit, including that a full-plan token is refused for a narrowed request.

### Resumen (español)

`ui://horizun/impact-preview` es una MCP App que muestra el **ensayo** (dry_run) de las
cinco escrituras masivas - `write_params_verified`, `set_keynote`, `delete_verified`,
`transform_elements`, `create_elements`: total por categoría y nivel, antes -> después por
fila, advertencias (colaterales, cascada, vista truncada, token retenido) y una casilla
por fila para **excluirla**. El token ata la petición completa (el campo que se reduce
entra en el hash del plan de cada comando), así que excluir filas pide un **nuevo
ensayo** de la petición reducida, y **Aplicar** solo gasta el token del ensayo que está en
pantalla, con una idempotency_key nueva. HTML autocontenido sin URLs ni CSP abierta, modo
claro/oscuro, tabla accesible. Sin herramientas nuevas: tools/list crece 305 bytes.
Hosts según el proyecto MCP Apps: ChatGPT, Claude, VS Code, Goose, Postman, MCPJam,
mcp-use y Alpic Playground (no medido aquí en ninguno).
