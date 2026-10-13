# Esquemas bajo demanda: la fachada de `tools/list` (3.0.0, fase 1)

Estado: **implementado e integrado en el candidato 3.0.0**. La integración se probó sin Revit.
El modo por defecto para 3.0.0 es **`full`**. La batería real de la Fase 4.3 no pudo autenticarse
en un `CODEX_HOME` aislado (401) y Claude no tenía cuota; `facade` queda opt-in hasta medirla.

## 1. Medición (2026-10-12, sin Revit)

**Cómo.** El `horizun-mcp.exe` compilado en el worktree contesta por stdio un `initialize` (2025-11-25) y un `tools/list`.
La salida se lee como bytes UTF-8 crudos (sin la página de códigos de la consola, que infla cada «…»). Se mide
`{"tools":[...]}` compacto; la línea JSON-RPC completa pesa 34 B más. Cada perfil corre con su propia `HORIZUN_DATA_ROOT`
temporal y su `settings.json`, sin `HORIZUN_TOOLSETS`.

**Reproducción.** `pwsh -NoProfile -File scripts/schema-facade-probe.ps1`: lee stdio con
`StandardOutputEncoding = UTF8Encoding(false)`, conserva el JSON compacto original y registra bytes, caracteres,
SHA-256, techo y respuestas en `%TEMP%/horizun-schema-facade-probe.json`.
La captura sin rutas personales está en `lazy-schema-3.0-measurements.json`.

**Tokens.** `ceil(caracteres UTF-16 / 4)`, el mismo estimador que `horizun://session/toolsets` y
`horizun_health.toolsets`. Es una aproximación documentada, no una medición con tokenizador.

### Antes (el contrato de hoy, que es `HORIZUN_SCHEMA_MODE=full`)

| Perfil | Herramientas | Bytes | ≈ tokens | vs 2.2.0 (475 647 B) | vs techo 512 KiB (524 288 B) |
|---|---:|---:|---:|---:|---:|
| `safe_write` (por defecto) | 113 | 421 788 | 105 289 | −53 859 B | 80,5 % (102 500 B libres) |
| `unsafe_code` | 124 | 482 666 | 120 485 | +7 019 B | 92,1 % (41 622 B libres) |
| `unsafe_code` + Python | 125 | 487 330 | 121 647 | +11 683 B | 93,0 % (36 958 B libres) |

Coinciden con el ledger (`tools-list-ledger.json`): 421 778 / 482 656 / 487 320 B para el array; los 10 B de diferencia son
`{"tools":}`.

### Después

| Modo | Perfil | Herramientas en `tools/list` | Bytes | ≈ tokens | Ahorro |
|---|---|---:|---:|---:|---:|
| `facade` (opt-in) | `safe_write` | 4 | 4 045 | 1 012 | −99,03 % |
| `facade` | `unsafe_code` | 4 | 4 045 | 1 012 | −99,15 % |
| `facade` | `unsafe_code` + Python | 4 | 4 045 | 1 012 | −99,16 % |
| `full` | `safe_write` | 113 | 421 788 | 105 289 | 0 (byte a byte) |
| `full` | `unsafe_code` | 124 | 482 666 | 120 485 | 0 |
| `full` | `unsafe_code` + Python | 125 | 487 330 | 121 647 | 0 |

La fachada pesa lo mismo en todos los perfiles: `horizun_health` más tres herramientas fijas. Lo que cambia por perfil es lo
que `horizun_list_tools` responde.

**Coste bajo demanda** (structuredContent compacto; el texto de la respuesta es la misma cadena):

| Llamada | Bytes | ≈ tokens |
|---|---:|---:|
| `horizun_list_tools {}` en `safe_write` (113 herramientas) | 21 105 | ≈5 277 |
| `horizun_list_tools {}` en `unsafe_code` + Python (125) | 23 339 | ≈5 835 |

El esquema descrito es el EXACTO (sin abreviar), así que una herramienta concreta puede costar más que su entrada
abreviada de hoy. La respuesta lista las variantes y la descripción de `horizun_describe_tool` recomienda pedir una.
Los tamaños de esta tabla cuentan solo `structuredContent`: el texto duplica ese contenido para clientes antiguos,
de modo que un cliente que incorpore ambas copias al contexto pagará más.

## 2. Diseño en diez líneas

1. `tools/list` en modo **facade** anuncia 4 herramientas: `horizun_health` (igual que hoy) y la fachada.
2. `horizun_list_tools {toolset?}`: las herramientas que esta sesión PUEDE llamar, por toolset, una línea cada una.
3. `horizun_describe_tool {name, variant?}`: el contrato EXACTO (el de `horizun://contract/tools/{tool}[/{variant}]`).
4. `horizun_call_tool {name, arguments}`: se **desenvuelve** en `DispatchToolCall` en el `tools/call` que representa.
5. Desde ahí todo es el camino directo: validación, `schema_help`, perfil, toolset, tareas, progreso y cancelación.
6. Alcanzable = `Tools.Advertisable()`, el mismo filtro que la lista completa; una llamada directa sigue funcionando.
7. Las tres herramientas no son filas del contrato: `Contract.Hash` no cambia; la fachada tiene su `SchemaFacade.Hash`.
8. `HORIZUN_SCHEMA_MODE=full` da la lista de 2.x byte a byte; en `full` la fachada no existe («Unknown tool»).
9. El modo por defecto vive en UN punto: `SchemaMode.Default` en `src/Horizun.Server/SchemaFacade.cs`.
10. `notifications/tools/list_changed` sigue vigilando el conjunto ALCANZABLE: avisa a quien debe volver a listar.

## 3. La fachada

| Herramienta | Argumentos | Responde |
|---|---|---|
| `horizun_list_tools` | `toolset?` (uno de los toolsets conocidos) | `tools` {nombre → primera frase, ≤160 car.}, `toolsets` {toolset activo → nombres}, `toolsets_not_active`, `next`, `contract_hash`, `facade_hash` |
| `horizun_describe_tool` | `name`, `variant?` | la fila del contrato (`input_schema` y `description` completos, `effect`, `toolsets`, …), `contract_uri`, `variants` con su plantilla de URI, `callable` y `refusal`; con `variant`, el esquema de la variante tal cual (`ContractVariants.Describe`) y su `uri` |
| `horizun_call_tool` | `name`, `arguments` | la respuesta de la herramienta llamada, idéntica a la de una llamada directa |

- **Validación.** `horizun_call_tool` valida sus propios argumentos (solo `name` y `arguments`; `name` existente; no a sí
  misma; `arguments` objeto). Si fallan, contesta `isError` con el motivo y no ejecuta nada. Si están bien, la llamada
  interior se valida contra el esquema EXACTO de la herramienta igual que hoy, porque es el mismo código. Los campos
  opcionales se pueden omitir; `null` explícito se rechaza si el esquema exige objeto o cadena.
- **Tareas.** `execution.taskSupport` es `optional` en la llamada (la tarea es la de la herramienta llamada, que decide con
  `McpTasks.Supports` como siempre) y `forbidden` en listar y describir.
- **Texto compacto.** Las respuestas de la fachada llevan como texto el JSON compacto (no indentado): el esquema de
  `create_elements` ocupa 90 KB en vez de 201 KB.
- **Sin escalada.** Listar solo muestra lo alcanzable. Describir funciona para cualquier herramienta del contrato (como
  los recursos), pero dice `callable: false` y la razón exacta de la puerta. Llamar pasa por `Tools.DisabledReason`, la
  misma puerta de una llamada directa: un perfil `read_only` rechaza `horizun_delete_verified` con el mismo texto por las
  dos vías, y `horizun_execute_python` en `safe_write` se rechaza antes de buscar Revit.

## 4. Impacto

### Contrato y hash

- **`Contract.Hash` no cambia.** Las tres herramientas viven solo en el servidor (`SchemaFacade.cs`), no en
  `Horizun.Contracts`. El add-in no las ve ni las necesita, y un servidor 3.0 con fachada y un add-in del mismo contrato
  siguen aceptándose. Meterlas en el hash rechazaría cada llamada contra un add-in idéntico.
- **`SchemaFacade.Hash`** (24 hex, el formato de `Contract.Hash`) es el SHA-256 de las tres definiciones publicadas. Cambia
  si cambia la fachada y nada más.
- **Qué ve health.** `horizun_health.schema_mode` (añadido por el servidor, como `toolsets`): `mode`, `default`, `source`
  (`default` | `environment`), `variable`, `problem` (un valor desconocido de la variable se nombra aquí y se sirve el
  defecto), `contract_hash`, `facade_hash`, `tools_list` {herramientas, bytes, tokens estimados} y `reachable_tools`.
  `horizun://build/identity` lleva `facade_hash` y `schema_mode` sin necesitar Revit.
- `horizun_health.toolsets` no cambia: sigue describiendo la selección de toolsets sobre el conjunto alcanzable (lo que
  `full` listaría). Lo que `tools/list` lleva de verdad está en `schema_mode.tools_list`.

### `schema_help`

Sin cambios de código: la llamada desenvuelta entra en `CallTool` con el nombre y los argumentos interiores, y
`SchemaHelp.Attach` nombra la ruta que falla y la URI del esquema exacto. Probado por el cable en los dos modos: el mismo
`get_document_info {bogus_argument}` devuelve «Arguments violate the contract at /bogus_argument … horizun://contract/tools/get_document_info»
con el texto idéntico, directo en `full` y por `horizun_call_tool` en `facade`.

### Recursos `horizun://contract/...`

Sin cambios: siguen sirviendo cada palabra. `horizun_describe_tool` sirve el mismo contenido para clientes que no leen
recursos MCP.

### `notifications/tools/list_changed`

`ToolListMonitor` sigue comparando el conjunto ALCANZABLE (`Tools.List`), no la respuesta de `tools/list`. En modo
facade la lista anunciada casi no cambia, pero `horizun_list_tools` sí; la notificación le dice al cliente que vuelva a
listar. Probado (`List_changed_still_fires_in_facade_mode_when_the_reachable_set_changes`).

### Instrucciones del servidor

`initialize` y `server/discover` mandan `ServerInstructions.HeadFor(modo)`. En `full` es el `Head` de siempre; en
`facade` cambia solo la regla 7, que nombra las tres herramientas. El `Head` completo mide 2 044 B de 2 048: la regla 7 de
la fachada es 3 B más corta: 2 041 B, 7 B de holgura (verificado por test).

El recurso `horizun://guidance/typed-first` sirve ese mismo encabezado según el modo; en `full` conserva el texto
anterior byte a byte. El parcial aún publicaba allí instrucciones de la lista completa en modo fachada.
`The_guidance_resource_describes_the_surface_actually_served` falló antes de corregirlo (1 fallo, salida 1;
`%TEMP%/hz-lazy-guidance-red.log`).

### Lo que la fachada pierde (documentado, no oculto)

- **MCP Apps.** `horizun_clash` y las cinco escrituras con vista previa declaran `_meta.ui` en `tools/list`; un host que
  precarga la app por esa entrada no la ve en modo facade. En `full` todo sigue igual.
- **Anotaciones por herramienta.** `readOnlyHint`/`destructiveHint` de cada herramienta no están en `tools/list`;
  `horizun_call_tool` lleva las más fuertes (no solo lectura, destructiva, mundo abierto) y `horizun_describe_tool`
  devuelve `effect` y `destructive` de cada una.
- **Una ronda más** por herramienta nueva (describir antes de llamar).

## 5. El modo por defecto: un solo punto

```csharp
// src/Horizun.Server/SchemaFacade.cs
public const SchemaSurface Default = SchemaSurface.Full;
```

Para cambiarlo: esa línea y el CHANGELOG. Los tests que dependen del defecto lo leen de `SchemaMode.Default`
(`JsonRpcErrorCodeTests.Initialize_sends_the_bounded_instructions_head`, `ModernProtocolTests.Discover_returns_the_instructions_head`)
y `The_default_is_full_until_the_agent_battery_can_be_measured` fija el valor conservador.
Por máquina o cliente: `HORIZUN_SCHEMA_MODE=full|facade` en el `env` de la configuración MCP.

## 6. Tests (rojo primero)

`SchemaFacadeTests` (en proceso) y `SchemaFacadeWireTests` (el exe real por stdio, con `HORIZUN_DATA_ROOT` desechable: sin
ficheros de descubrimiento, ningún Revit es alcanzable).

| Propiedad | Test |
|---|---|
| Techo de bytes declarado en el test: `tools/list` facade ≤ 8 KiB y `horizun_list_tools` ≤ 40 KiB, en los 5 perfiles | `Facade_tools_list_fits_its_declared_ceiling_in_every_posture` |
| Ninguna herramienta inalcanzable: 5 perfiles × (sin selección + 18 toolsets); cada herramienta de la lista completa se lista, se describe con su esquema exacto y se desenvuelve con sus argumentos intactos | `Every_tool_the_full_list_advertises_is_listed_described_and_callable_through_the_facade`, `Every_variant_is_described_verbatim_through_the_facade` |
| `full` = ledger byte a byte (5 perfiles y 18 toolsets; y por stdio) | `Full_mode_is_the_committed_ledger_byte_for_byte`, `Full_mode_tools_list_over_stdio_is_the_committed_ledger` |
| Errores de contrato con ruta y URI en los dos modos | `Contract_errors_name_the_path_and_the_uri_in_both_modes` |
| Sin escalada: lo deshabilitado no se lista, se describe `callable:false` con la razón de la puerta, y se rechaza por las dos vías con el mismo texto | `A_tool_the_profile_or_toolset_switches_off_stays_uncallable_through_the_facade`, `The_facade_is_not_a_way_around_the_profile`, `The_call_tool_cannot_call_itself` |
| El hash del contrato no se mueve | `The_facade_tools_are_not_contract_rows_so_the_contract_hash_cannot_move` |
| Fin a fin con modo explícito | `The_facade_lists_describes_and_calls_end_to_end_when_selected`, `The_default_wire_list_is_the_full_contract`, `In_full_mode_the_facade_tools_do_not_exist`, `Initialize_sends_the_head_of_the_surface_it_serves` |

**El rojo reproducido en esta revisión.** Se guardó `Program.cs` parcial en `%TEMP%`, se repuso temporalmente su versión
de `HEAD` y se ejecutó `SchemaFacadeWireTests`: **4 fallos, 2 pases**, salida 1. Fallaron la lista por defecto, las
instrucciones, la llamada con perfil restringido y `schema_help`; pasaron `full` contra el ledger y la ausencia de
meta-tools en `full`. Se restauró el archivo antes de continuar. Evidencia: `%TEMP%/hz-lazy-red.log` y
`%TEMP%/hz-lazy-testresults-*/lazy-red.trx` (conservados fuera del repo).

**Defecto encontrado en el parcial.** Aceptaba `null` en `toolset`, `variant` y los argumentos interiores aunque el
esquema publicaba cadena/objeto. `Facade_arguments_obey_the_published_nonnullable_types` se añadió primero y falló
(1 fallo, salida 1; TRX `%TEMP%/hz-lazy-testresults-*/lazy-null-red.trx`). Se corrigió el código y
la suite de fachada pasó. Se añadieron además filtros por cada toolset y una comprobación por stdio de que un toolset
deshabilitado rechaza la llamada directa y la fachada de la misma manera.

**Tests existentes ajustados** (misma fuerza): dos comparaban las instrucciones con `ServerInstructions.Head` y ahora con
`HeadFor(SchemaMode.Default)`; el caso `full` = `Head` exacto está en `Initialize_sends_the_head_of_the_surface_it_serves`.
`Listed_tools_publish_modern_schemas_and_behavior_annotations` exigía `outputSchema` en cada herramienta listada y obligó a
dárselo a la fachada (`{"type":"object","additionalProperties":true}`, el de cada fila del contrato). El ledger se
regrabó con `HORIZUN_UPDATE_TOOLS_LEDGER=1` siguiendo el procedimiento del repo (los tests salen rojos deliberadamente
al regrabar); el archivo resultante quedó idéntico. Después se ejecutó Server sin la variable.

## 7. Prueba con clientes instalados (aislada)

Servidor: el exe del worktree. Configuración temporal con `HORIZUN_DATA_ROOT` propia y `HORIZUN_SCHEMA_MODE`; nada de la
configuración del dueño. Sin Revit.

| Cliente | Modo | Ve | Describe | Llama |
|---|---|---|---|---|
| Codex CLI 0.162.0-alpha.17.2 | facade | `codex mcp list`: entrada habilitada con el exe del worktree y env temporal; `codex login status`: **Not logged in** | no hay turno autenticado | no hay turno autenticado |
| Codex CLI 0.162.0-alpha.17.2 | full | mismo resultado, con otro `CODEX_HOME` temporal | no hay turno autenticado | no hay turno autenticado |
| Probe JSON-RPC stdio (`scripts/schema-facade-probe.ps1`) | facade | 4 herramientas, en los tres perfiles medidos | `horizun_describe_tool` devuelve el contrato exacto de `horizun_target` | `horizun_call_tool` → `horizun_target`: 0 instancias y sin destino |
| Probe JSON-RPC stdio | full | 113 / 124 / 125 herramientas | `resources/read horizun://contract/tools/horizun_target` devuelve el contrato exacto; la fachada está ausente | `horizun_target` directo: 0 instancias y sin destino |

No se ejecutó Claude Code. No se copiaron archivos de autenticación. Las dos configuraciones y los resultados de
`mcp list` y `login status` viven en `%TEMP%/hz-lazy-codex-{facade,full}`. Ante la falta de autenticación se usó el
fallback autorizado de stdio. En cada ejecución aislada, `horizun_health` respondió que ningún Revit era alcanzable.
Claude Desktop y Cursor: no probados.

## 8. Pendiente

- **Fase 4.3:** batería real sin medir por falta de autenticación en el cliente aislado; `full` queda por defecto hasta disponer de datos comparables.
- **pending live:** `scripts/live/schema-facade.ps1`, preparado y revisado por sintaxis; no ejecutado. Comprueba
  health y ambos hashes, consulta el esquema exacto y compara una lectura directa y por fachada del documento
  seleccionado expresamente por el dueño. Las escrituras completas pertenecen a la batería posterior de Fase 4.3.
- Repetir con un cliente autenticado y entorno aislado, sin copiar credenciales del dueño.

## 9. Gates de integración

- Core: **6909/6909**; Server: **1286/1286** después de fijar `full` por defecto mediante prueba roja primero.
- Server Release: cero warnings. Inventario: **125 herramientas, 475 operaciones**; ledger 4/4 e `inventory.tests.ps1` verdes.
- Sonda stdio aislada: los seis perfiles/modos de la tabla anterior; la captura JSON conserva solo datos portables.
- El escaneo completo previo al commit de integración se registra en el historial de la rama.