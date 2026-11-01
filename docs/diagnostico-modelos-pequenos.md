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
| `HORIZUN_TOOLSETS=local`, después | 10 | 61 645 | 15 400 | 9 |
| `HORIZUN_SCHEMA_MODE=facade`, antes | 4 | 4 045 | 1 000 | 4 |
| `HORIZUN_SCHEMA_MODE=facade`, después (con `query`) | 4 | 4 352 | 1 100 | 4 |
| **`local` + `HORIZUN_SCHEMA_MODE=compact`, después** | **10** | **14 257** | **3 600** | **0** |
| `local` + `compact`, `full_write` | 11 | 15 206 | 3 800 | 0 |

El modo completo queda **byte a byte igual**. En `local`, la décima herramienta de
`safe_write` es `horizun_activate_toolset`; con `full_write` aparece además
`horizun_capture_view`.

**Dónde estaba el peso de `local` antes:** `horizun_create_elements` ocupaba 31 838
caracteres (un tercio de todo), `horizun_manage_views` 11 710, `horizun_query_model` 9 241
(33 argumentos) y `horizun_model_scan` 8 779. En `compact` ocupan 3 504, 1 922, 2 262 y
1 135 caracteres; `manage_views` y `model_scan` además salieron de `local`.

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

Una sola corrida por configuración: una diferencia de 1–2 tareas está dentro del ruido.

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
| **qwen3:8b, después** | **`compact`** | **15 (56 %)** | 21 (78 %) | 22 (81 %) | **23 677** | **12,4** |

`compact` es la iteración 2. La primera versión de `compact` dio 11/27 con qwen3:8b; la
diferencia se explica en la sección 4.

**Qué mejoró, en números:**
- **qwen3:8b, de `local` antes a `compact`:** pasa de 12 a 15 aciertos (+3 tareas). Los tokens
  de entrada por tarea bajan **79 %** y el tiempo por tarea **81 %** (de 64 s a 12 s).
- **Haiku, de `local` antes a `compact`:** mantiene los aciertos (26 → 25, dentro del ruido) con
  **58 % menos tokens de entrada** por tarea.
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

**Regresión por una decisión de esta rama: r13 («resumen de salud del modelo»).** Antes, Haiku
la resolvía con `horizun_model_scan`. Con `model_scan` fuera de `local` falla en todas las
configuraciones nuevas. Haiku tampoco recurre a `horizun_activate_toolset`. Hay dos salidas:
volver a poner `model_scan` en `local` (1 135 caracteres en `compact`, medido), o dejarla en `read` y
enseñar el camino desde el buscador.

**Haiku falla poco y por razones distintas:**
- `w18` (mover una puerta) en `compact`;
- `r13`, por la regresión anterior;
- `s26` (interferencias) en `local` antes: resolvió la pregunta con lecturas y no con
  `horizun_clash`.

## 5. Lo que no se midió

- **Una sola corrida por configuración.** Con 27 tareas, ±1–2 tareas es ruido. Una
  comparación más firme necesita 3 o más repeticiones.
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
