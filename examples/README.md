# Examples / Ejemplos

**[English](#english) · [Español](#español)**

---

## English

Real, copyable payloads for Horizun Revit MCP tools. Every `*.json` here is one
tool call:

```json
{
  "tool": "horizun_create_elements",
  "title": { "en": "...", "es": "..." },
  "about": { "en": "what it does, what it verifies, what to replace", "es": "..." },
  "next": "the file of the next step, when it is a sequence",
  "files": [ "companion files in the same folder" ],
  "arguments": { "...": "exactly what you send as the tool's arguments" }
}
```

Send **`arguments`** as the arguments of **`tool`**. Everything else is commentary.

**They cannot go stale.** A test (`tests/Horizun.Server.Tests/ExamplePayloadTests.cs`)
loads every `examples/**/*.json`, reads the tool it declares and validates
`arguments` against that tool's `InputSchema` in the current contract; a renamed
property, a narrowed enum or a new required field fails the build, not your first
call. The host-resident examples are also run (the project-context draft must
rehearse to a valid, coherent document; every container must compose a valid name),
and `tests/Horizun.Core.Tests/ExampleDeliveryFilesTests.cs` proves that the example
IDS and property-set mapping agree with each other on a hand-written IFC.

**No project data.** Names, codes and paths are placeholders under
`C:/proyectos/demo/`. Element ids (`1001`, `2001`, `5001`...) are placeholders too:
resolve the real ones in your model first (`horizun_query_model`,
`horizun_list_elements`). Every write rehearses by default (`dry_run: true`); the
apply is the same call with `dry_run: false`, the rehearsal's `confirmation_token`
and a new `idempotency_key`.

| Folder | What it shows |
|---|---|
| [`iso19650-startup/`](iso19650-startup/) | ISO 19650 start-up with `horizun_project_context`: `questions` → `draft` (rehearsal) → `draft` (write) → `validate`, plus a complete [`project-context.example.json`](iso19650-startup/project-context.example.json). |
| [`information-containers/`](information-containers/) | `horizun_information_container`: `name` → `stamp` → `inspect` → `transition` (WIP → Shared). |
| [`ifc-delivery/`](ifc-delivery/) | `horizun_deliver_ifc` with a TAB-separated Pset mapping ([`hz-delivery-psets.txt`](ifc-delivery/hz-delivery-psets.txt)) and an IDS 1.0 file ([`hz-delivery.ids`](ifc-delivery/hz-delivery.ids)) that match: `HZ_Delivery.Code` on every `IfcDuctSegment`. |
| [`disciplines/`](disciplines/) | One call per discipline. Architecture: walls and a slab (`horizun_create_elements`). Structure: a stirrup plan from a requirement set (`horizun_plan_reinforcement`). MEP: a rectangular and a round duct (`horizun_create_elements`). Documentation: sheets to one PDF named as a container (`horizun_export`). |
| [`dwg-revision-update/`](dwg-revision-update/) | A reproducible DWG revision cycle with its own synthetic fixture. |

### QA/QC report template

[`qaqc-report-template.xlsx`](qaqc-report-template.xlsx) is an empty QA/QC
report template for copying evidence produced by Horizun workflows. It is not a
sample audit result and does not represent a real Revit model as clean or
non-conformant.

Use it after a read-only audit or a verified correction cycle: preserve the
document identity, profile, coverage state, cited findings and operation receipt.
Do not replace `unknown` or incomplete coverage with a passing status.

---

## Español

Payloads reales y copiables para las herramientas de Horizun Revit MCP. Cada
`*.json` de esta carpeta es una llamada a una herramienta: `tool` es la
herramienta, `arguments` es exactamente lo que se envía como argumentos, y
`title`/`about` (en inglés y español) explican qué hace, qué verifica y qué hay
que reemplazar. `next` apunta al paso siguiente cuando es una secuencia; `files`
nombra los archivos que la acompañan.

**No se desactualizan.** Un test (`tests/Horizun.Server.Tests/ExamplePayloadTests.cs`)
carga cada `examples/**/*.json`, lee la herramienta que declara y valida
`arguments` contra el `InputSchema` de esa herramienta en el contrato actual: una
propiedad renombrada, un enum más estrecho o un campo obligatorio nuevo rompen la
compilación, no tu primera llamada. Los ejemplos que se resuelven en el servidor
también se ejecutan (el borrador del contexto debe ensayar a un documento válido y
coherente; cada contenedor debe componer un nombre válido), y
`tests/Horizun.Core.Tests/ExampleDeliveryFilesTests.cs` demuestra que el IDS y el
mapeo de Psets de ejemplo coinciden entre sí sobre un IFC escrito a mano.

**Sin datos de proyecto.** Nombres, códigos y rutas son marcadores bajo
`C:/proyectos/demo/`. Los ids de elementos (`1001`, `2001`, `5001`...) también:
resuelve primero los reales en tu modelo (`horizun_query_model`,
`horizun_list_elements`). Toda escritura ensaya por defecto (`dry_run: true`);
aplicar es la misma llamada con `dry_run: false`, el `confirmation_token` del
ensayo y un `idempotency_key` nuevo.

| Carpeta | Qué muestra |
|---|---|
| [`iso19650-startup/`](iso19650-startup/) | Arranque ISO 19650 con `horizun_project_context`: `questions` → `draft` (ensayo) → `draft` (escritura) → `validate`, más un [`project-context.example.json`](iso19650-startup/project-context.example.json) completo. |
| [`information-containers/`](information-containers/) | `horizun_information_container`: `name` → `stamp` → `inspect` → `transition` (WIP → Compartido). |
| [`ifc-delivery/`](ifc-delivery/) | `horizun_deliver_ifc` con un mapeo de Psets separado por TAB y un IDS 1.0 que coinciden: `HZ_Delivery.Code` en cada `IfcDuctSegment`. |
| [`disciplines/`](disciplines/) | Una llamada por disciplina. Arquitectura: muros y losa (`horizun_create_elements`). Estructura: plan de estribos desde un conjunto de requisitos (`horizun_plan_reinforcement`). MEP: un ducto rectangular y uno circular (`horizun_create_elements`). Documentación: planos a un PDF con nombre de contenedor (`horizun_export`). |
| [`dwg-revision-update/`](dwg-revision-update/) | Un ciclo reproducible de revisión de DWG con su propio fixture sintético. |

### Plantilla de informe QA/QC

[`qaqc-report-template.xlsx`](qaqc-report-template.xlsx) es una plantilla vacía
de informe QA/QC para copiar la evidencia que producen los flujos de Horizun. No
es un resultado de auditoría de ejemplo ni presenta un modelo real como limpio o
no conforme. Úsala tras una auditoría de solo lectura o un ciclo de corrección
verificado, conservando identidad del documento, perfil, cobertura, hallazgos
citados y recibo de la operación. No reemplaces una cobertura `unknown` o
incompleta por un estado aprobado.
