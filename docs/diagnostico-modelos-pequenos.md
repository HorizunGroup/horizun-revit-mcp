# Diagnóstico: Horizun Community con modelos pequeños

Qué cuesta `tools/list` a un modelo pequeño, qué herramientas confunde, y cómo le fue a
**Claude Haiku** y a **qwen3:8b** (Ollama) en 27 tareas típicas de Revit, antes y después de
los cambios de esta rama. Todo lo que aparece aquí se midió; lo que no se pudo medir está al
final.

- Producto: Horizun Community, base `main` 3.0.6 (`000f262`). `Contract.Hash`
  `e44f15ba2c5bbc64f7555ae8` antes y después: el add-in no cambia.
- Fecha: 2026-10-31. Revit 2024 en **inglés** (`Revit.exe /language ENU`), plantilla
  `English\Construction-DefaultMetric.rte`. Las peticiones van en español y en inglés.
- Tokens: caracteres / 4, la misma regla de `horizun_health`, no un tokenizador.

## 1. Lo que pesa `tools/list` (sin Revit)

`scripts/eval/small-model-diagnostics.py` arranca el servidor real por stdio con una carpeta
de datos temporal y vacía, y mide la respuesta `{"tools":[...]}` en UTF-8.

| Configuración (`safe_write`) | Herramientas | Bytes | Tokens ≈ | Descripciones > 200 caracteres |
|---|---:|---:|---:|---:|
| completa (`full`, por defecto), **antes y después** | 113 | 421 731 | 105 300 | 112 |
| `HORIZUN_TOOLSETS=local`, antes | 17 | 99 400 | 24 800 | 16 |
| `HORIZUN_TOOLSETS=local`, después | 11 | 70 470 | 17 600 | 10 |
| `HORIZUN_SCHEMA_MODE=facade`, antes | 4 | 4 045 | 1 000 | 4 |
| `HORIZUN_SCHEMA_MODE=facade`, después (con `query`) | 4 | 4 352 | 1 100 | 4 |
| **`local` + `HORIZUN_SCHEMA_MODE=compact`, después** | **11** | **15 393** | **3 850** | **0** |
| `local` + `compact`, `full_write` | 12 | 16 342 | 4 100 | 0 |

El modo completo queda **byte a byte igual**. En `local`, la undécima herramienta de
`safe_write` es `horizun_activate_toolset`; con `full_write` aparece además
`horizun_capture_view`. `horizun_model_scan` volvió a `local` después de medir la iteración 2
(sección 4): sin ella, 10 herramientas y 14 257 bytes.

**Dónde estaba el peso de `local` antes:** `horizun_create_elements` ocupaba 31 838
caracteres (un tercio de todo), `horizun_manage_views` 11 710, `horizun_query_model` 9 241
(33 argumentos) y `horizun_model_scan` 8 779. En `compact` ocupan 3 504, 1 922, 2 262 y
1 135 caracteres; `manage_views` además salió de `local`.

**Herramientas que se confunden** (coseno entre nombre y descripción, sin tildes ni palabras
vacías, umbral 0,35). En el modo completo hay 40 parejas parecidas: las más altas son
`apply_cad_plan`/`apply_ifc_plan` (0,67), `health`/`target` (0,55),
`apply_cad_update`/`plan_cad_update` (0,55) y
`embed_floors_in_toposolid`/`grade_toposolid_around_floors` (0,52). En `local` antes había dos:
`health`/`target` (0,55) y `navigate`/`undo` (0,38). Después no queda ninguna.
`health`/`target` es la que se vio en la práctica: qwen3:8b llamó a `horizun_target` 8 veces
en lugar de `horizun_health`.

## 2. La batería

- `scripts/eval/small-model-fixture.py` crea un modelo desechable con escrituras tipadas
  (ensayo y aplicación): 6 muros (4 en Level 1, 2 en Level 2), 1 suelo, 2 puertas y 1
  habitación. Registra los IDs y los valores esperados.
- `scripts/eval/small-model-tasks.json` define 27 tareas: 14 de lectura (por ejemplo,
  «cuántos muros hay», el área de una habitación, el parámetro Comentarios de un ID o
  capturar la vista), 10 de escritura («crea un muro de 3 m», cambiar Comentarios de dos IDs,
  mover una puerta, borrar, crear un nivel, una vista o un suelo, crear y deshacer) y 3 de
  seguridad o fuera del perfil (simular sin cambiar, Python, interferencias).
- `scripts/eval/small-model-battery.py` corre cada tarea y la **comprueba contra el modelo**:
  vuelve a leerlo por el mismo puente y no se fía de lo que diga el agente. Antes de cada
  escritura restablece el modelo desde una copia limpia.
- **Haiku** corre con `claude -p --model haiku` y solo este servidor (`--strict-mcp-config`).
- **qwen3:8b** corre con un bucle mínimo sobre `/api/chat` de Ollama: las herramientas del
  servidor, su guía y una instrucción corta. Es lo que recibe quien usa Ollama o LM Studio.
  Configuración: `num_ctx=32768`, `temperature=0`, sin *thinking*, `OLLAMA_FLASH_ATTENTION=1`
  y `OLLAMA_KV_CACHE_TYPE=q8_0`. En una GPU de portátil de 8 GB, qwen3 queda al 76 % en GPU.
- Cada respuesta de herramienta se recorta a 12 000 caracteres para qwen3. El recorte es igual
  antes y después.

Indicadores:
- **Aciertos:** la tarea quedó hecha, comprobado contra el modelo o la respuesta.
- **Herramienta correcta a la primera:** la primera llamada útil fue una de las esperadas. No
  cuenta la orientación inicial con `health`, que el propio servidor pide.
- **Primera acción correcta:** igual que el anterior, pero en las escrituras ignora las
  lecturas previas de verificación.

## 3. Resultados

Una corrida por configuración, salvo qwen3:8b final y su comparación (dos): una diferencia de 1–2 tareas está dentro del ruido.

| | Config | Aciertos | Herramienta correcta a la primera | Primera acción correcta | Tokens de entrada por tarea (mediana) | Segundos por tarea (mediana) |
|---|---|---:|---:|---:|---:|---:|
| **Haiku**, antes | `local` | 26 (96 %) | 19 (70 %) | 27 (100 %) | 149 644 | 12,8 |
| Haiku, antes | `facade` | 24 (89 %) | 21 (78 %) | 26 (96 %) | 82 569 | 15,7 |
| Haiku, después | `local` | 26 (96 %) | 18 (67 %) | 26 (96 %) | 145 392 | 12,5 |
| Haiku, después | `facade` | 24 (89 %) | 20 (74 %) | 25 (93 %) | 87 296 | 16,0 |
| **Haiku, después** | **`compact`** | **25 (93 %)** | 18 (67 %) | 26 (96 %) | **63 518** | 12,8 |
| **qwen3:8b**, antes | `local` | 12 (44 %) | 24 (89 %) | 24 (89 %) | 114 281 | 63,7 |
| qwen3:8b, antes | `facade` | 6 (22 %) | 9 (33 %) | 9 (33 %) | 20 725 | 70,4 |
| qwen3:8b, después | `local` | 11 (41 %) | 22 (81 %) | 22 (81 %) | 50 524 | 24,6 |
| qwen3:8b, después | `facade` | 7 (26 %) | 15 (56 %) | 15 (56 %) | 20 152 | 38,0 |
| qwen3:8b, después | `compact` | 15 (56 %) | 21 (78 %) | 22 (81 %) | 23 677 | 12,4 |
| Haiku, final | `compact` + `model_scan` | **26 (96 %)** | 18 (67 %) | 26 (96 %) | **63 451** | 10,4 |
| qwen3:8b, final (2 corridas) | `compact` + `model_scan` | **12 y 12 (44 %)** | 20 y 20 (74 %) | 24 y 22 | **32 588 y 24 748** | 17,5 y 29,3* |

`compact` es la iteración 2. La primera versión de `compact` dio 11/27 con qwen3:8b; la
diferencia se explica en la sección 4. «Final» es la iteración 2 con `horizun_model_scan` de
vuelta en `local`, que es lo que queda en esta rama.

\* La segunda corrida (y la repetición sin `model_scan`) compartió Ollama y la GPU con otras
corridas de qwen3:8b en la misma máquina: sus segundos no son comparables.

**Qué mejoró, en números (de `local` antes a lo que queda en la rama):**
- **Haiku:** mantiene los aciertos (26 → 26) con **58 % menos tokens de entrada** por tarea.
- **qwen3:8b:** mantiene los aciertos (12 → 12, en dos corridas) con **71–78 % menos tokens de
  entrada** por tarea. Sin `model_scan` llegó a 15 y 14 aciertos (ver sección 4).
- El tiempo por tarea de qwen3:8b bajó de 64 s a 12 s en la iteración 2 sin carga externa; las
  corridas finales no son comparables en tiempo.
- **La fachada con el buscador** sube un poco con qwen3:8b (6 → 7 aciertos, y la herramienta
  correcta a la primera de 33 % a 56 %). Sigue siendo la peor opción para un modelo de 8B:
  listar, describir y llamar en tres pasos le cuesta demasiado.

## 4. Qué falla todavía, y por qué

**qwen3:8b elige bien y llena mal.** Antes del cambio elegía la herramienta correcta a la
primera en el 89 % de las tareas y aun así fallaba más de la mitad. En la primera versión de
`compact`, las llamadas guardadas muestran valores de relleno inventados:
- `target_document` con «Active Document», «ACTIVE» o «»;
- `view_name` con «Active View», o `view_id` y `level_id` con 0;
- `target="project_info"` junto al ID de un muro;
- `units` dentro de cada elemento en lugar del nivel superior.

La iteración 2 dice en cada argumento de dónde sale su valor: el título exacto según
`horizun_health`, el ID de nivel según `query_model` con `OST_Levels`, que se omita la vista
para capturar la activa, y que `target` es solo para Project Information. También indica que se
aplique un ensayo limpio cuando la persona pidió el cambio. Con eso, qwen3 pasa de 11 a 15
aciertos.

**Fallos que quedan en qwen3:8b con `compact`:**
- **Se queda en el ensayo** en 3 escrituras (w16, w22 y w27): ensaya bien, no aplica y le pide
  confirmación a la persona. No es inseguro, pero no termina la tarea.
- **Lee mal respuestas correctas**: por ejemplo, nombra «Autodesk Revit 2024» como documento
  activo (r01) o resume mal una agrupación por categorías (r14).
- **Repite `capture_view`** cuando la captura temporal falla (r12 y s24) y no prueba otra vía.
- **Se le escapan las peticiones de varios pasos**: interferencias, vista nueva, crear y
  deshacer.

**r13 («resumen de salud del modelo») y `model_scan`.** Con `model_scan` fuera de `local`, r13
fallaba en todas las configuraciones nuevas y Haiku no recurría a `horizun_activate_toolset`.
Con `model_scan` de vuelta (1 135 caracteres más en `compact`), r13 pasa en 3 de 3 corridas
aisladas con cada modelo y en las baterías completas. **El costo, medido con dos corridas de
cada versión:** qwen3:8b baja de 15 y 14 aciertos a 12 y 12. Pierde en las dos corridas r05, r06,
r09 y w19, casi siempre por poner el relleno `ACTIVE_DOCUMENT_TITLE` en `target_document`; ese
relleno ya aparecía antes (16 llamadas de 68 contra 17 de 64), pero cae en otras tareas. La
descripción de `model_scan` no lo explica: qwen3:8b es sensible a cualquier cambio de la lista.
Haiku sube de 25 a 26. La decisión fue dejar `model_scan` en `local`.

**Haiku falla poco y por razones distintas:**
- `w18` (mover una puerta) en `compact` sin `model_scan`;
- `r13` sin `model_scan` (ver arriba);
- `s26` (interferencias) en `local` antes: resolvió la pregunta con lecturas y no con
  `horizun_clash`.

## 4b. Perfiles de herramientas (lo que escribe una instalación nueva)

Un perfil decide qué herramientas MUESTRA `tools/list`, no cuáles se pueden llamar: las demás se
alcanzan con `horizun_list_tools`, `horizun_call_tool` y `horizun_activate_toolset`. Una
instalación nueva escribe `"tool_profile": "esencial"`; una actualización no cambia. Tamaño de
`tools/list` (`safe_write`): `esencial` 14 herramientas y 72 566 B (≈18 100 tokens, −83 % frente a
la lista completa); `esencial` + `compact` 17 687 B (≈4 400 tokens, −96 %).

Batería de 27 tareas, Revit 2024 en inglés, perfil en `settings.json` sin `HORIZUN_TOOLSETS`:

| | Config | Aciertos | Primera acción correcta | Tokens de entrada por tarea (mediana) |
|---|---|---:|---:|---:|
| Haiku (2 corridas) | `esencial` | 26 y 25 | 27 y 26 | 155 947 y 116 557 |
| **Haiku (2 corridas)** | **`esencial` + `compact`** | **26 y 26** | 26 y 27 | **54 644 y 55 609** |
| qwen3:8b | `esencial` | 11 | 24 | 66 435 |
| **qwen3:8b** | **`esencial` + `compact`** | **13** | 25 | **23 398** |

- Con `compact`, `esencial` rinde igual o un poco mejor que `local` + `compact` (Haiku 26 frente a
  26; qwen3:8b 13 frente a 12 y 12) y además deja alcanzable todo lo demás.
- Sin `compact`, qwen3:8b llama 25 veces a las herramientas de servidor (`horizun_call_tool` y
  compañía) en lugar de llamar directo, y acierta menos: para un modelo de 8B conviene
  `esencial` + `compact`.
- Una corrida de qwen3:8b por configuración, sin carga externa en la GPU.

## 5. Lo que no se midió

- **Pocas corridas.** Una por configuración, dos en la comparación de `model_scan` con qwen3:8b.
  Con 27 tareas, ±1–2 tareas es ruido; una comparación firme necesita 3 o más.
- **Otros modelos** (llama 3.x, gpt-4o-mini, LM Studio con otros modelos): no se corrieron.
- **Clientes reales** (Claude Desktop, LM Studio, un cliente MCP para Ollama): la batería usa
  `claude -p` y un bucle propio sobre Ollama. La guía de clientes marca qué está verificado.
- **`tools/list_changed` de punta a punta** al activar un grupo: está probado en el servidor,
  no en un cliente real.
- **Un artefacto del arnés:** al restablecer el modelo queda abierto un documento ancla vacío,
  y qwen3:8b lo eligió una vez como `target_document`. Pasó igual antes y después.

## 6. Cómo reproducirlo

```powershell
# 1. coste de tools/list, sin Revit
python scripts/eval/small-model-diagnostics.py --server <horizun-mcp.exe> --out diag.json

# 2. modelo desechable (Revit en inglés abierto, add-in de este árbol)
python scripts/eval/small-model-fixture.py --server <horizun-mcp.exe> --path C:/scratch/fixture.rvt `
  --template "C:/ProgramData/Autodesk/RVT 2024/Templates/English/Construction-DefaultMetric.rte" `
  --out C:/scratch/fixture.json
Copy-Item C:/scratch/fixture.rvt C:/scratch/fixture-pristine.rvt

# 3. batería (una por modelo y configuración: local, facade o compact)
python scripts/eval/small-model-battery.py --agent ollama --model qwen3:8b --config compact `
  --server <horizun-mcp.exe> --fixture C:/scratch/fixture.json --pristine C:/scratch/fixture-pristine.rvt --out results/
python scripts/eval/small-model-battery.py --agent claude --model haiku --config compact ...

# 4. informe
python scripts/eval/small-model-report.py results/
```
