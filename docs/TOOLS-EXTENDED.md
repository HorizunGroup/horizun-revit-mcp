# Extended tool notes

Detail that does not belong in `tools/list` descriptions. One section per topic.

## Wire parse errors and cancellation retries

### Parse errors (-32700)

A line on stdin that is not valid JSON is answered with JSON-RPC error -32700,
`id: null`, and an `error.data` object:

| field | meaning |
|---|---|
| `hint` | `unescaped_backslash` (a Windows path such as `C:\x` not written as `C:\\x`), `concatenated_messages` (two messages on one line), `truncated_message` (the line ended inside a string or object, usually a raw newline inside a string value), `byte_order_mark`, `bare_word` (an unquoted word where JSON needs a quoted string, `:` or `,`), `not_json`, `invalid_json` |
| `line`, `column`, `offset`, `length` | where the parser stopped; `offset` is the zero-based character index in the received line |
| `shape`, `shape_caret` | a window of up to 20 characters either side of `offset`, with every letter shown as `a`, every digit as `0` and every non-ASCII character as `?`; quotes, backslashes and punctuation are kept |
| `content_echoed` | always `false`. The words, numbers, paths and tokens you sent are never repeated |

The server log line carries the same fields and the client name that
`initialize` declared (`clientInfo.name`, reduced to 40 safe characters).

Build every message with a JSON serializer (`ConvertTo-Json`, `json.dumps`) and end each one with a newline.
`scripts/hz-call.ps1` checks its arguments before it starts a server. It takes
`-ArgumentsObject @{ path = 'C:\folder\a.rvt' }` so a caller never has to escape
JSON by hand.

### Cancellation and timeout: what a retry does

When a tool call is cancelled by the client or times out, the error detail
(`revit_transport_failed`) includes a `retry` object, and the message ends with a
matching `RETRY:` sentence:

| `retry.verdict` | when | what to do |
|---|---|---|
| `same_key_runs_fresh` | the request was removed from Revit's queue before it started, or was never sent | send the identical call again. If it had an `idempotency_key` and `confirmation_token`, reuse them: neither was consumed |
| `same_key_replays_recorded_answer` | the request may have started, and it carried an `idempotency_key` | send the identical call with the **same** key. Revit runs one command at a time, so the retry waits behind the original and then replays the recorded answer from the durable ledger without writing again. A new key would write a second time |
| `inspect_model_first` | the request may have started, and it carried no key | nothing can prove what happened, so inspect the model before sending anything |

If the call waited 60 s or more, the sentence also names `horizun_submit_job`
and `horizun_job_status`, and `retry.prefer` is set to `horizun_submit_job`.
The size of the batch (`retry.batch_items`) is reported but does not trigger
this advice: in 1,447 logged `horizun_create_elements` calls the slowest took
3.4 s. Progress: any `tools/call` that sends `_meta.progressToken` already gets
`notifications/progress` every 5 s, with the bridge's queue or run state when
the bridge can see it.

---

**Resumen (español).** Un -32700 ahora dice dónde falló (línea, columna,
posición), qué tipo de fallo parece (`hint`: barra invertida sin escapar, dos
mensajes en una línea, mensaje cortado, BOM, palabra sin comillas) y una ventana
de texto donde cada letra se muestra como `a` y cada dígito como `0`. Nunca se
repite el contenido enviado. Una llamada cancelada o que agotó el tiempo indica
qué pasa si se reintenta. Si el trabajo no empezó, la misma llamada corre de
nuevo. Si pudo empezar y lleva `idempotency_key`, la misma clave devuelve la
respuesta registrada sin escribir otra vez; una clave nueva duplicaría la
escritura. Si pudo empezar y no lleva clave, hay que revisar el modelo antes de
reenviar nada. Si la llamada esperó 60 s o más, se recomienda
`horizun_submit_job`.



## Phases, design options, parts and assemblies

Two tools, each with an `operation` switch. Reads need no token. Every write takes
`target_document`, runs as a dry run by default and returns a single-use
`confirmation_token`; the apply spends it.

**How a write is verified.** The write runs inside a `TransactionGroup`. The dry
run applies it (every inner transaction commits), reads a `PostconditionCheck`
back from the model and rolls the whole group back. The apply does the same
write, checks it while the group can still be undone (any unverified property
rolls everything back), assimilates, then reads the checklist again from the
committed model. That second reading is what the reply publishes in
`postconditions`.

### `horizun_manage_phases`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Phases in Revit's order; each phase filter with `presentation` for `new`, `existing`, `demolished`, `temporary` (`by_category`, `overridden`, `hidden`); design options with option set, `is_primary`, member count and the first 100 member ids; the active option. `design_options_writable` is always `false`. |
| `element_status` | no | For `element_ids`: created and demolished phase, `ElementOnPhaseStatus` in `phase_id` (default: last phase), and the design option (`main_model` when the element is in none). |
| `set_element_phases` | yes | Sets `created_phase_id` and/or `demolished_phase_id` (`-1` clears the demolition). The final pair is checked in order: demolition in the same phase is allowed (Temporary), before the creation is refused. Only elements where `HasPhases` and `ArePhasesModifiable` are true. |
| `create_phase_filter` | yes | `PhaseFilter.Create` with a unique `name`, then the presentations you name. |
| `edit_phase_filter` | yes | `filter_id` plus a new `name` and/or `presentation`. |
| `rename_phase` | yes | `phase_id` plus a unique `name`. |
| `create_phase` | refused | `no_phase_creation_api`. |
| `assign_design_option` | refused | `no_design_option_assignment_api`. |

**API limits (checked in RevitAPI.xml for 2023–2027, the same in every year).** No
call creates, inserts, deletes or reorders a phase. `Element.DesignOption` is
read-only, and nothing adds elements to an option set, moves them between options
or sets the active option. Python cannot do these things either, so the refusals
offer no fallback. Moving an element into a secondary option hides it from every
view that shows the primary option or the main model. That is why the refusal
tells you to review views before you do it by hand.

### `horizun_manage_assemblies_parts`

| operation | writes | what it does |
|---|---|---|
| `list` | no | Every part (id, excluded, source ids, original category), every assembly (name, naming category, members). With `element_ids`: whether each one is valid for parts, its part ids and its assembly. |
| `create_parts` | yes | `PartUtils.CreateParts` on `element_ids`. It refuses elements that already have parts or that fail `AreElementsValidForCreateParts`. Verified: each source has associated parts. |
| `divide_parts` | yes | `PartUtils.DivideParts` of part `element_ids` by `reference_ids`, which must be levels, grids or reference planes. The API requires a sketch plane even without curves, so a horizontal one is created. Verified: each divided part has at least two derived parts. The cut geometry itself is not checked. |
| `exclude_parts` / `restore_parts` | yes | Sets `Part.Excluded`, re-read per part. |
| `dissolve_parts` | yes | Deletes the `PartMaker` of each original in `element_ids`. This is the only way the API removes parts, and it loses every division, exclusion and part parameter. Verified: the originals still exist and have no parts. |
| `create_assembly` | yes | `AssemblyInstance.Create` with `naming_category_id` (default: the first member's category, checked with `IsValidNamingCategory`) and an optional unique `name`. The name is set in a second transaction because Revit only allows it after the creating transaction commits. Verified: members, naming category, name. |
| `assembly_views` | yes | `views` from `3d`, `plan`, `section_a`, `section_b`, `elevation_front`, `part_list`. Verified: each view's `AssociatedAssemblyInstanceId`. |
| `disassemble` | yes | `AssemblyInstance.Disassemble`. Verified: the instance is gone and every former member exists outside any assembly. |

The tool is marked destructive because `dissolve_parts` and `disassemble` remove elements.

### Live probes

`scripts/live-probes/phases-options-parts.probes.ps1` runs in the write tier of
`verify-live.ps1`. It creates three walls of its own at x ≥ 720 m and runs these
cases:

1. `list` reads the phases and the phase filters.
2. `create_phase` refuses with the API reason.
3. `set_element_phases` sets the first/last phase on one wall, reads the wall back as demolished, then restores it.
4. Design options `list`. It reports `not_covered` when the fixture has no options.
5. `create_parts` then `dissolve_parts` on one wall.
6. `create_assembly` of the other two walls, then `disassemble`.

The probe deletes the three walls afterwards. `phases-options-parts.tests.ps1` runs
the module offline against a fake `Call` and `Apply`. `divide_parts`,
`exclude_parts`, `assembly_views`, the phase-filter writes and `rename_phase` have
no live probe yet.

### Resumen (español)

- `horizun_manage_phases` lee:
  - las fases en orden;
  - los filtros de fase, con su presentación por estado nuevo, existente, demolido y temporal;
  - las opciones de diseño y el estado de fase de cada elemento.
- `horizun_manage_phases` escribe fases de elementos, crea y edita filtros de fase y renombra fases.
- La API de Revit 2023–2027 no permite dos cosas, y la herramienta las rechaza con el motivo y sin fallback a Python:
  - crear o reordenar fases;
  - mover elementos entre opciones de diseño.
- `horizun_manage_assemblies_parts` lista, crea, divide, excluye, restaura y disuelve partes. También crea ensamblajes, genera sus vistas y los desensambla.
- Cada escritura:
  1. se ensaya dentro de un TransactionGroup que se revierte;
  2. se aplica con el token;
  3. se relee del modelo confirmado.

  Si algo no cuadra, se revierte entera.
