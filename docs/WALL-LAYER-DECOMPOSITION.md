# Wall layer decomposition — `horizun_split_multilayer_walls`

Cómo un muro multicapa se descompone en un muro por capa **sin perder nada de lo
que colgaba de él**, y por qué la implementación anterior no podía prometerlo.

Este documento tiene dos mitades:

- **Fase 0 — red-team** de la implementación vigente (el recipe Python). Cada
  hallazgo dice qué se declara, qué hace realmente, qué se pierde, y con qué
  prueba se demuestra.
- **Fase 1 — especificación e invariantes** de la implementación tipada que la
  reemplaza.

Baseline de la auditoría: rama `feat/dimension-production`, HEAD
`69462e6b8a85e8cd42b48bb0e8bf1b54a8cf24d2`. Suite offline en ese punto:
2568 passed / 0 failed / 0 skipped.

---

## Fase 0 — red-team de la implementación vigente

### Qué es hoy

`SplitMultilayerWallsCommand : RecipeCommand` delega toda la geometría a
`src/Horizun.Revit/Recipes/split_multilayer_walls.py`, un port del botón pyRevit
"Partir Muro Multicapa". El host (`Core/Recipe.cs`) aporta lo que el botón no
tenía: `target_document`, `dry_run` por defecto, token de confirmación, **una**
transacción, y una verificación post-commit.

Esa verificación es el problema. `Verifications` declara exactamente dos cuentas:

    new VerifiedCount("layer walls created",    "created", "created_present"),
    new VerifiedCount("original walls deleted", "deleted", "deleted_gone")

y `verify()` en el recipe las contesta contando ids que siguen siendo `Wall` e
ids que ya no existen. **Nada más se relee.** El resultado es que
`all_verified: true` es compatible con: todas las puertas perdidas, todos los
muros en la posición equivocada, todas las aperturas ausentes y todas las
uniones rotas. La honestidad del host es real, pero está apuntando a las dos
cantidades que no son las que importan.

### Cómo leer la matriz

- **Confirmado (código)** — se lee directamente en la fuente; no necesita Revit.
- **Confirmado (contrato)** — el propio contrato o comentario lo declara.
- **A medir** — la lectura del código lo hace muy probable, pero el veredicto
  honesto exige medirlo en Revit. No cuenta como defecto probado hasta la Fase 8.

Riesgo: **P0** = pérdida silenciosa de datos del usuario (se reporta éxito).
**P1** = resultado geométricamente incorrecto pero visible. **P2** = capacidad
ausente, correctamente rechazada, o coste operativo.

---

### A. Núcleo y geometría

| # | Capacidad declarada | Comportamiento real | Se pierde | Riesgo | Estado | Prueba que lo demuestra |
|---|---|---|---|---|---|---|
| D-01 | "re-hosting doors and windows on the structural layer" | `struct_idx = 0`, sustituido sólo por el índice de la **primera** capa con `Function == Structure`. Si ninguna capa es `Structure` se queda en **0** — la capa más exterior, típicamente un acabado. El núcleo real (`GetFirstCoreLayerIndex` / `GetLastCoreLayerIndex`) no se consulta nunca. | Puertas y ventanas quedan alojadas en un acabado de 2 cm | P0 | Confirmado (código) | Casos vivos 3 y 4 |
| D-02 | — | `get_wall_layers` descarta las capas con `Width < 1e-6` **antes** de sumar `total_thickness` y antes de numerar. Las capas membrana (barrera de vapor, lámina) son legítimas y tienen ancho cero. | El ensamblaje reconstruido no es el original; además todo offset queda desplazado y los índices reportados dejan de ser los de `CompoundStructure` | P1 | Confirmado (código) | Unit test de plan de capas con membrana |
| D-03 | — | `acc_offset = total_thickness / 2.0`: el reparto asume que la `LocationCurve` **es la línea central del muro**. `WallLocationLine` tiene seis valores. Un muro en `FinishFaceExterior` (habitual) tiene su curva sobre la cara exterior. | Toda la pila de capas desplazada hasta `espesor/2`, commiteada y "verificada" | P0 | Confirmado (código) | Caso vivo 6: un muro por cada `WallLocationLine` |
| D-30 | — | `Wall.Create(..., flip, ...)` pasa `wall.Flipped` **y** después `ensure_wall_orientation` puede volver a llamar `Flip()`. Las dos correcciones pueden cancelarse o sumarse; el orden exterior→interior de las capas depende de acertar esto. | Un muro del revés: los acabados en la cara equivocada. Plausible a la vista | P1 | Confirmado (código) | Caso vivo 5 + unit tests de signo |
| D-21 | "CURVED WALLS ARE REFUSED, not straightened" | Correcto y honesto: `is_straight()` rechaza todo lo que no sea `Line` y explica por qué (offsetear por extremos de un arco devuelve la cuerda). | Nada — es un rechazo seguro | P2 | Confirmado (contrato) | **Brecha de capacidad**, no defecto: la Fase 4 implementa arcos |
| D-22 | — | `ensure_wall_orientation` llama `doc.Regenerate()` por muro, más los `Regenerate` entre fases: O(N·M) regeneraciones en el hilo de UI. | Nada, pero un lote grande agota el tool timeout y el puente parece roto | P2 | Confirmado (código) | Medición de tiempo en el caso vivo 16 |

### B. Dependencias, hosts y objetos anidados

| # | Capacidad declarada | Comportamiento real | Se pierde | Riesgo | Estado | Prueba |
|---|---|---|---|---|---|---|
| D-05 | Contrato del puente: "ningún comando reporta trabajo que no verificó" | Los fallos de `restore_hosted_element`, `restore_wall_opening` y `try_join` se capturan y se **imprimen**. `apply()` borra el original en cuanto `new_ids` no está vacío. | Las puertas que no se pudieron recolocar se pierden **con el original ya borrado**, y la respuesta dice `all_verified: true` | **P0 — el defecto central** | Confirmado (código) | Casos vivos 27–29 |
| D-08 | "re-hosting doors and windows" | No hay re-host: hay **destrucción y recreación**. `restore_hosted_element` llama `NewFamilyInstance` sobre el muro nuevo. | ElementId y UniqueId de cada puerta/ventana. Con ellos: tags, cotas, schedules por id, filtros por id, la federación en Navisworks/ACC y cualquier referencia externa | P0 | Confirmado (código) | Casos vivos 33, 34 |
| D-09 | — | `capture_hosted_info` captura cinco hechos: símbolo, punto, nivel, `HandFlipped`, `FacingFlipped`. Nada más. | Sill/head height, **todos** los parámetros de instancia, fase creada/demolida, workset, design option, mark, comments, parámetros compartidos y de proyecto, subcomponentes (`GetSubComponentIds`), instancias anidadas compartidas | P0 | Confirmado (código) | Casos vivos 13, 14, 15 |
| D-10 | — | `capture_hosted_info` hace `if loc is None: continue`. La instancia se cae de la lista, no se recrea y **no se reporta**. | La instancia entera, en silencio | P0 | Confirmado (código) | Unit test del censo + caso vivo 16 |
| D-11 | — | `get_hosted_elements` sólo acepta `FamilyInstance` con `Host.Id == wall.Id`. `Wall.FindInserts(...)` no se llama nunca. | Curtain walls embebidos, `WallSweep` (sweeps y reveals), armadura alojada, MEP embebido, in-place alojado — todo se va en cascada con `doc.Delete(wall.Id)` sin aparecer en ningún reporte | P0 | Confirmado (código) | Casos vivos 20, 26 |
| D-25 | "Stacked walls are handled through their members" | `split_stacked_wall_into_layers` recorre los **miembros** y llama `get_hosted_elements` sobre cada miembro. Los inserts de un muro apilado los aloja la **raíz**, no el miembro. Después `apply()` borra la raíz. | Las puertas de un muro apilado se borran con la raíz sin haber sido capturadas nunca | P0 | Confirmado (código) | Caso vivo 10 |

### C. Aperturas, cortes y uniones

| # | Declarado | Real | Se pierde | Riesgo | Estado | Prueba |
|---|---|---|---|---|---|---|
| D-12 | "the finish walls are joined to it so Revit cuts their openings" | `restore_wall_openings` recrea cada opening sobre **todas** las capas desplazando `p0/p1` a lo largo de la **normal** del muro. Un `NewOpening(wall, p0, p1)` rectangular se define en el plano del propio muro; trasladar los puntos por la normal no mueve la apertura dentro del plano y puede sacarlos de la cara host. | Apertura mal construida o excepción capturada | P1 | **A medir** | Casos vivos 17, 18 |
| D-13 | — | Los fallos de `restore_wall_opening` se imprimen. Ningún contador los cuenta. | Un hueco que falta en una capa de acabado, en silencio | P0 | Confirmado (código) | Caso vivo 17 |
| D-23 | "every other warning reaches you" | `WallOverlapPreprocessor` filtra por **texto**: `if "overlap" in desc and "wall" in desc`. En un Revit en español, francés o alemán esa cadena no aparece nunca. | En Revit no-inglés la advertencia **no** se descarta → modal → el hilo de UI de Revit retenido hasta el timeout del cliente. Y a la inversa: cualquier otra advertencia cuyo texto contenga ambas palabras se borra en silencio | P0 | Confirmado (código) | Unit test sobre `FailureDefinitionId` + caso vivo 1 |
| D-24 | — | `try_join` sólo intenta portador↔capa. Las uniones del muro original **en sus extremos** no se capturan ni se restauran. Y `JoinGeometryUtils.JoinGeometry` se asume suficiente para que exista el hueco: nunca se comprueba. | Las uniones de extremo; y una apertura que se creyó propagada por el join | P1 | Confirmado (código) | Caso vivo 19 |

### D. Tipos, parámetros y restricciones

| # | Declarado | Real | Se pierde | Riesgo | Estado | Prueba |
|---|---|---|---|---|---|---|
| D-14 | — | `get_or_create_wall_type` devuelve el **primer** `WallType` del documento cuyo *nombre* sea `"{material}_{cm:.1f}cm"`. Un tipo preexistente con ese nombre y otra `CompoundStructure` se usa tal cual; dos materiales que redondeen al mismo nombre colisionan; y si `SetCompoundStructure` lanza, el `except: pass` deja el tipo duplicado **con la estructura multicapa entera**. | El material real de la geometría; en el peor caso una "capa" que es el muro compuesto completo, commiteado y verificado | P0 | Confirmado (código) | Fase 9: nombres en conflicto, materiales sin nombre, capas repetidas |
| D-15 | — | A los muros de capa no se les copia **ningún** parámetro salvo el `origin_group_param` opcional. | Top/base constraint, offsets, room bounding, fase, workset, design option, mark, comments, parámetros de proyecto y compartidos | P1 | Confirmado (código) | Unit tests de política de copia + caso vivo 11 |
| D-16 | — | `Wall.Create(..., wall_height, base_offset, ...)` con `WALL_USER_HEIGHT_PARAM` crea un muro **unconnected**. Un muro restringido a un nivel superior no se detecta. | La restricción superior: el muro deja de seguir al nivel | P1 | Confirmado (código) | Caso vivo 11 |
| D-17 | — | `is_structural = (StructuralUsage != NonBearing)` aplasta `Bearing`, `Shear` y `StructuralCombined` en un booleano. | El valor real de `StructuralUsage` | P1 | Confirmado (código) | Unit test de copia de parámetros |
| D-18 | — | `Wall.Create` desde la curva reconstruye un muro de elevación rectangular. Un perfil editado, un attachment a cubierta o un slant/taper se reconstruyen como muro plano. **Sin detección y sin rechazo.** | El perfil editado, el attachment, el ángulo | P0 | Confirmado (código) | Casos vivos 21, 22, 23 |
| D-19 | — | Sin comprobación de grupo, design option, workset ni editabilidad. Un muro prestado por otro usuario falla en `doc.Delete`, se captura como error — y las capas ya creadas se quedan. | Original + duplicados, commiteados | P0 | Confirmado (código) | Casos vivos 24, 25, 26 |
| D-20 | — | `split_wall_into_layers` nunca copia `Pinned` a las capas (sólo lo hace la ruta `copy_wall_as_independent` de miembros apilados). | El estado pinned | P2 | Confirmado (código) | Caso vivo 12 |

### E. Atomicidad, idempotencia y plan

| # | Declarado | Real | Se pierde | Riesgo | Estado | Prueba |
|---|---|---|---|---|---|---|
| D-06 | — | `Recipe.Run` abre **una** transacción para todo el lote y `apply()` captura la excepción por muro y sigue. Un muro que falla a mitad (3 de 5 capas creadas) deja esas 3 en el modelo **y** conserva el original. | Geometría duplicada, commiteada | P0 | Confirmado (código) | Casos vivos 27–29, 32 |
| D-07 | — | Si `doc.Delete` del original falla, `created` ya contiene los muros nuevos y la transacción **igual commitea**. `created_present` los verifica encantado. | Original + capas completas conviviendo | P0 | Confirmado (código) | Caso vivo 26 |
| D-26 | — | No hay marca de procedencia ni Extensible Storage. Un segundo apply "funciona" por accidente (las capas ya son de una sola capa y se saltan), pero `already_split`, `existing_plan_conflict` y `repairable_partial_state` no existen, y un muro parcialmente partido no tiene ruta de reparación. | La capacidad de responder qué pasó | P1 | Confirmado (código) | Caso vivo 30 |
| D-27 | El comentario lo declara: "It does not bind per-element identity" | El token liga `recipe_sha256` + las cuentas intencionadas. Borrar un muro elegible y añadir otro entre dry-run y apply puede dejar las cuentas iguales → el token se acepta y se parte **otro conjunto de muros**. | La garantía de que se escribe lo aprobado | P0 | Confirmado (contrato) | Caso vivo 31 (`stale_plan`) |
| D-28 | "would_create is reported as null (not guessed) when a stacked wall is in scope" | Honesto en el reporte, pero `ReadCount` convierte ese null en `-1` y el hash del plan queda `created=-1`, que coincide con cualquier otro plan con muro apilado. | La ligadura del token, justo donde la operación es más compleja | P0 | Confirmado (código) | Unit test de fingerprint |
| D-29 | "select any member and the whole stack is processed once" | Documentado, pero significa que el elemento que el llamador nombró **no** es el elemento que se escribe. Junto con D-27 es una ruta real de "escribí otra cosa". | — | P1 | Confirmado (contrato) | Fase 9 |
| D-04 | "VERIFIED AFTER THE COMMIT" | Se verifican dos cuentas: cuántos ids creados siguen siendo `Wall` y cuántos originales ya no están. Ni posición, ni hosts, ni aperturas, ni joins, ni parámetros. | La verificación misma | **P0 — habilita todos los demás** | Confirmado (código) | Toda la Fase 5 |

### Resumen

30 hallazgos: **17 P0**, **10 P1**, **3 P2**. 29 confirmados leyendo el código o
el contrato; 1 (D-12) queda marcado **a medir** y no cuenta como defecto probado
hasta la Fase 8.

El patrón que los une: la implementación trata "se crearon N muros y el original
ya no está" como prueba de que la operación salió bien. Todo lo que un muro
sostiene — los inserts, sus parámetros, sus subcomponentes, sus aperturas, sus
uniones y su identidad — queda fuera de esa afirmación, y por tanto puede
perderse mientras la respuesta dice `all_verified: true`. **Es un fallo de
diseño, no una acumulación de bugs**, y por eso la corrección es estructural.

### Lo que sí está bien y se conserva

Decir cuál es la parte sana también es parte de la auditoría:

- El rechazo de muros curvos (D-21) es la respuesta correcta con la aritmética
  que había, y está bien explicado. Se convierte en capacidad; no se "arregla".
- El host (`Recipe.cs` / `RecipeCommand.cs`) ya aporta `target_document`,
  `dry_run` por defecto, token y commit verificado. La implementación tipada
  hereda esas garantías en vez de reinventarlas.
- `hz.resolve` distingue `missing_ids` de `wrong_type_ids`. Ese vocabulario se
  conserva.
- El recipe hermano `split_multilayer_slabs` **ya** hace rollback por elemento en
  su propia `SubTransaction` cuando no puede devolver las familias alojadas. Ese
  es el precedente del repositorio para la atomicidad que el muro necesita, y la
  implementación tipada lo sigue en vez de inventar otro.

---

## Fase 1 — especificación e invariantes

Versión del esquema: **`wall_split_v1`**. Todo fingerprint, todo registro de
procedencia y todo plan llevan esta cadena; una versión distinta es un plan
distinto y se rechaza como `existing_plan_conflict`, nunca se interpreta.

### 1.1 El invariante que define la herramienta

> Un muro compuesto se convierte en un muro por capa **conservando el elemento
> original** como portador de una capa del núcleo. Si no se puede garantizar que
> cada dependencia del muro sobrevivió, el muro queda **exactamente como
> estaba**.

De ahí salen cinco invariantes verificables, y ninguno se da por supuesto: los
cinco se releen del modelo después del commit y antes de confirmar la
`SubTransaction`.

- **I0 — Cardinalidad.** Un muro cuyo ensamblaje tiene N capas **con volumen**
  produce **exactamente N muros independientes**, cada uno con un tipo de una
  sola capa. El portador es uno de esos N y pierde su `CompoundStructure`
  multicapa. Ningún muro resultante sigue siendo multicapa. Las capas de ancho
  cero no cuentan y no se materializan, pero se reportan.
- **I1 — Identidad.** El `ElementId` y el `UniqueId` del muro original siguen
  existiendo y siguen siendo un `Wall`. Ese muro es el `core_carrier`.
- **I2 — Posición.** El centro de cada capa está donde estaba dentro del muro
  compuesto, medido en el modelo, dentro de la tolerancia. Incluye al portador.
- **I3 — Alojamiento.** Cada insert que el muro alojaba sigue existiendo con su
  mismo `ElementId` y `UniqueId`, su `Host.Id` es el portador, y su símbolo,
  colocación, flips, nivel, fase y subcomponentes coinciden.
- **I4 — Corte.** Cada capa secundaria presenta la apertura que le corresponde a
  cada insert, medida en la geometría, no deducida de que exista un join.
- **I5 — Atomicidad.** O se cumplen I0–I4 para ese muro, o el muro no cambió.
  Nunca hay original y duplicados, ni capas creadas sobre un original intacto.

### 1.2 Núcleo: definiciones que no son sinónimos

Cuatro conceptos que la implementación anterior confundía (D-01, D-03):

- `MaterialFunctionAssignment.Structure` — la **función** declarada de una capa.
- Los **límites del núcleo** — `CompoundStructure.GetFirstCoreLayerIndex()` y
  `GetLastCoreLayerIndex()`. Una capa puede ser `Structure` y estar **fuera** del
  núcleo; una capa del núcleo puede **no** ser `Structure`.
- El **centro geométrico del núcleo** — la media de sus dos límites.
- La **línea de ubicación** del muro (`WallLocationLine`) — dónde está la
  `LocationCurve` respecto de todo lo anterior.
- El **centro del muro completo** — `T/2`.

### 1.3 El eje `u` y la ecuación de offsets

Sea `L0 … L(n-1)` el orden que devuelve `CompoundStructure.GetLayers()`, con
anchos `w_i`, y `T = Σ w_i`.

Se define un eje escalar `u` que **crece desde la cara exterior hacia la
interior**: la cara exterior está en `u = 0` y la interior en `u = T`. El centro
de la capa `i` es

    c_i = Σ_{j<i} w_j + w_i / 2

Con `f = GetFirstCoreLayerIndex()` y `l = GetLastCoreLayerIndex()`:

    uCoreExt  = Σ_{j<f}  w_j
    uCoreInt  = Σ_{j<=l} w_j

La posición de la `LocationCurve` sobre ese eje queda **completamente
determinada por `WallLocationLine`**, sin depender del signo de ninguna API:

| `WallLocationLine` | `u_loc` |
|---|---|
| `WallCenterline` | `T / 2` |
| `CoreCenterline` | `(uCoreExt + uCoreInt) / 2` |
| `FinishFaceExterior` | `0` |
| `FinishFaceInterior` | `T` |
| `CoreExterior` | `uCoreExt` |
| `CoreInterior` | `uCoreInt` |

Y el offset firmado del centro de la capa `i` respecto de la curva de ubicación,
**medido a lo largo de la normal exterior `n̂`**, es

    offset_i = u_loc − c_i          (positivo = hacia el exterior)

Comprobación contra la implementación anterior: con `u_loc = T/2` esta ecuación
se reduce exactamente a su `acc_offset`. Es decir, el código viejo era el caso
particular `WallCenterline` aplicado a las seis líneas de ubicación. Eso es
D-03, escrito como aritmética.

`CompoundStructure.GetOffsetForLocationLine()` **no** se usa como fuente: se
consulta como contraste y su discrepancia se reporta, porque su convención de
signo es justamente lo que el mandato pide validar por medición y no por
documentación.

### 1.4 La normal exterior se mide, no se deduce

`Wall.Orientation` es una convención documentada y en un muro de arco no es
constante. La implementación anterior la combinaba **además** con `wall.Flipped`,
y las dos correcciones podían cancelarse (D-30).

Aquí la dirección exterior se **mide** sobre la geometría real:
`HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior)` devuelve la cara
exterior del muro; su normal saliente en el punto más cercano al medio de la
curva es `n̂`. Es una medición sobre el sólido, no una convención, y por
construcción ya incorpora `Flipped`.

`Wall.Orientation` se lee como **segunda fuente independiente** y las dos tienen
que coincidir en el lado; si discrepan, el muro se **rechaza** antes de escribir.

**Esa corroboración es obligatoria, y la primera versión de este documento decía
lo contrario.** Afirmaba que el verificador atraparía cualquier error de signo
—«una suposición equivocada aquí produce un rechazo, nunca un edificio
incorrecto»— y era falso: el ejecutor **coloca** las capas a lo largo de `n̂` y el
verificador las **mide** a lo largo del mismo `n̂`, así que una normal invertida
construye el muro del revés y lo verifica como correcto. I2 no puede atraparlo
porque está de acuerdo consigo mismo. Lo detectó una revisión adversaria del
código, no una prueba.

Cuando la cara del sólido no se pudo leer y `Orientation` **fue** la fuente, hay
una sola fuente y no hay nada que corroborar: eso se registra como tal
(`exterior_normal_corroborated: false`) en vez de disfrazarse de acuerdo. Lo que
se rechaza es la **discrepancia**, no el tener una sola fuente.

### 1.5 Selección determinista del `core_carrier`

Exactamente el orden del mandato, y cada rama emite su `core_carrier_selection_reason`:

1. Si `GetFirstCoreLayerIndex()` o `GetLastCoreLayerIndex()` no dan un rango
   válido (`f > l`, fuera de rango, o el núcleo no contiene ninguna capa de ancho
   > tolerancia) → **rechazo `no_valid_core`**. Nunca se cae a la capa 0.
2. Entre las capas del núcleo `[f..l]`, las de función `Structure`.
3. Una sola → esa. Razón: `single_structural_layer_in_core`.
4. Varias → la de **mayor espesor**. Razón: `thickest_structural_layer_in_core`.
5. Empate → el **índice original menor**. Razón: `…_tie_lowest_index`.
6. Ninguna `Structure` en el núcleo → la **más gruesa del núcleo**. Razón:
   `thickest_core_layer_no_structural`.
7. Empate → índice original menor. Razón: `…_tie_lowest_index`.

Se reporta siempre: `core_first_layer_index`, `core_last_layer_index`,
`core_carrier_layer_index`, `core_carrier_selection_reason`,
`original_location_line`, `original_core_center_offset`, y por capa el offset
esperado y el medido.

**Capas de ancho cero.** Se convierte en muro **sólo la capa que tiene volumen**.
Una membrana de ancho cero no puede serlo: Revit no admite un tipo de muro cuyo
espesor total sea cero.

Pero tampoco se descarta antes de sumar, que es D-02. Entra en el eje `u` con
`w = 0`, **conserva su índice original** —y por tanto no corre la numeración de
las capas que vienen detrás— y se reporta con `materialised: false` y
`reason: zero_width_membrane`. No se convierte en muro, y no desaparece del
ensamblaje en silencio.

### 1.6 Curvas soportadas

- **`Line`** — offset por traslación `n̂ · offset_i`.
- **`Arc` circular** — mismo centro, mismo normal de plano, mismos ángulos
  inicial y final, mismo sentido. Sólo cambia el radio:

      σ    = signo( n̂(p_m) · (p_m − C) )      con p_m el punto medio del arco
      R_i  = R + σ · offset_i

  El arco se construye con `Arc.Create` a partir de **tres** puntos —inicio, fin y
  **punto medio**— cada uno reescalado radialmente desde el centro original al
  radio nuevo. Tres puntos con el medio incluido determinan el arco exactamente:
  conservan centro, radio, ángulos, sentido y longitud. Lo que el mandato prohíbe
  —y lo que hacía la implementación anterior— es reconstruir desde **dos**
  extremos, que devuelve la cuerda. Si `R_i ≤ tolerancia`, la capa colapsaría a través del centro:
  rechazo `degenerate_layer_radius`.
- **Todo lo demás** — `NurbSpline`, `Ellipse`, `HermiteSpline`, `CylindricalHelix`,
  curvas no planas y curvas de longitud menor que la tolerancia: rechazo
  `unsupported_curve`, con el nombre real del tipo de curva. **Nunca** se
  convierte una curva no soportada en una línea.

### 1.7 Estrategia de ejecución: el original se conserva

Por muro, dentro de su propia `SubTransaction`:

1. **Revalidar** el fingerprint contra el que aprobó el token.
2. **Despinnear** si hacía falta, recordando el estado.
3. **Convertir el original en portador**: asignar el tipo de una sola capa del
   `core_carrier`, y normalizar `WALL_KEY_REF_PARAM` a `WallCenterline` — con lo
   que la curva de ubicación **es** el centro de su única capa.
4. **Reubicar la curva por medición, no por predicción.** Se calcula la curva
   objetivo, se asigna, se regenera, se **relee** dónde quedó realmente el centro
   de la capa, y se corrige el residuo. Un lazo medir-corregir es inmune a
   cualquier convención de signo que se haya supuesto mal; una predicción abierta
   no lo es.
5. **Crear** un muro por cada capa restante de ancho > tolerancia.
6. **Restaurar** joins de extremo y establecer los cortes de las capas.
7. `doc.Regenerate()`.
8. **Releer todo del modelo** y ejecutar I1–I5.
9. Repinnear si correspondía.
10. Commit de la `SubTransaction` **sólo** si todo pasó; si no, `RollBack()` y el
    muro se reporta con su código y su medición.

Ninguna capa se crea antes de que el portador exista y esté verificado como
tal, de modo que el estado "capas creadas sobre un original intacto" no es
alcanzable ni con una excepción a mitad.

**Por qué conservar el original importa tanto**: los `Opening` alojados, los
`WallSweep`, los reveals, la armadura, el MEP embebido, los tags, las cotas y
los perfiles editados **no se tocan** porque el elemento que los sostiene nunca
deja de existir. La lista de cosas que hay que reconstruir se reduce a los muros
de las capas secundarias y a sus cortes.

### 1.8 Censo de dependencias

Antes de abrir transacción se construye un `dependency_ledger` por muro, a partir
de `Wall.FindInserts(true, true, true, true)` y `GetDependentElements(null)`, más
las lecturas directas de attachments, perfil, sección transversal, grupo, design
option, workset y editabilidad.

Cada entrada se clasifica con el vocabulario cerrado del mandato:

| Clase | Qué significa aquí |
|---|---|
| `preserved_by_identity` | Sigue colgando del elemento original, que no se borra. Se **verifica** igualmente. |
| `reconstructable_and_verified` | Se recrea y se relee (los cortes de las capas secundarias). |
| `reference_rebound_and_verified` | Su referencia se reapunta y se relee. |
| `unsupported_blocking` | No se puede garantizar equivalencia → el muro se rechaza **antes** de abrir transacción. |
| `not_applicable` | Inspeccionado y ausente. No es lo mismo que "no se miró". |

`warning` genérico **no existe** en este vocabulario. Una pérdida potencial es
`unsupported_blocking` o no es nada.

Al menos una entrada `unsupported_blocking` ⇒ el muro se rechaza en el dry-run.

### 1.9 Reglas de rechazo (conjunto cerrado)

Elegibilidad: `not_a_wall`, `not_basic_wall`, `no_compound_structure`,
`single_layer`, `no_valid_core`, `unsupported_curve`,
`degenerate_layer_radius`, `unsupported_cross_section` (slanted/tapered),
`unsupported_edited_profile`, `unsupported_attached_wall`,
`unsupported_group_member`, `unsupported_design_option`,
`unsupported_stacked_wall`, `element_not_editable`, `unsupported_dependency`.

Plan: `stale_plan`, `already_split`, `existing_plan_conflict`,
`repairable_partial_state`.

Ejecución/verificación: `type_creation_failed`, `carrier_conversion_failed`,
`verify_carrier_identity`, `verify_layer_geometry`, `verify_insert_identity`,
`verify_insert_host`, `verify_insert_placement`, `verify_insert_subcomponents`,
`verify_type_mismatch`, `verify_opening_missing`, `verify_join_missing`,
`verify_parameter_mismatch`,
`verify_unexpected_warning`.

**Decisión declarada — muros apilados.** La implementación anterior los aceptaba
y borraba la raíz, que es donde Revit aloja sus inserts: sus puertas se perdían
sin reporte (D-25). Un `Stacked` no tiene `CompoundStructure` propia y su raíz no
puede convertirse en portador básico conservando identidad. Se **rechaza** con
`unsupported_stacked_wall`. Es una capacidad declarada que se retira, y se retira
porque lo que hacía era destruir datos en silencio; un rechazo seguro es
comportamiento correcto, una conversión parcial no.

### 1.10 Tipos por capa: nombre obligatorio y fingerprint

**Ningún muro resultante puede seguir siendo multicapa.** Cada uno de los N muros
—el portador incluido— lleva un tipo de **una sola capa**, y el portador pierde
por tanto su `CompoundStructure` original y su nombre de tipo multicapa.

#### El nombre

Estructura exacta, con espacio-guión-espacio como separador:

    [NOMBRE DEL TIPO ORIGINAL] - [NOMBRE DEL MATERIAL] - [NN]

    EXT_Muro Fachada 25cm - Ladrillo - 01
    EXT_Muro Fachada 25cm - Mortero - 02
    EXT_Muro Fachada 25cm - Concreto - 03

- El nombre del tipo original se conserva **completo**: no se abrevia, no se
  recorta, no se normaliza.
- El material sale **directamente de la capa** del `CompoundStructure`, textual:
  no se traduce, no se resume, no se reformula.
- `NN` es la **posición original de la capa** en el `CompoundStructure`, contada
  de exterior a interior, con base 1 y **dos dígitos** (`01`, `02`, … `10`, `11`).
  Un ensamblaje de más de 99 capas usa los dígitos que necesite: la regla es
  "al menos dos", no "exactamente dos".
- La capa exterior es **siempre `01`**. `wall.Flipped` cambia qué cara del
  edificio queda hacia fuera, pero **no** invierte la numeración lógica del
  `CompoundStructure`: el número es el del ensamblaje, no el de la vista.
- Una capa de ancho cero **conserva su número** aunque no se materialice, así que
  la numeración de las capas siguientes no se corre.
- Como el número es el índice de capa, dos capas del mismo material quedan
  distinguidas por construcción.
- No se añade `Core`, `Finish`, `Structure` ni ningún otro texto no solicitado.
- Si el material no existe, fue eliminado, o su nombre está vacío o es sólo
  espacios, se usa literalmente `MATERIAL_SIN_ASIGNAR`.
- Se limpian **únicamente** los caracteres que Revit prohíbe en un nombre de
  tipo, sustituyéndolos por `_`, más los espacios sobrantes en los extremos.
  Nada más se toca: el nombre del tipo original y el del material llegan tal
  cual.

#### La identidad

El nombre es para las personas; la **identidad** es un digest estable
`wall_split_v1` sobre, como mínimo: `UniqueId` del material (no su nombre), el
ancho exacto cuantizado en la rejilla de 0.1 mm, `MaterialFunctionAssignment`,
prioridad de envoltura, pertenencia al núcleo, `IsVariableWidth`, wrapping,
propiedades de deck cuando apliquen, y el `WallKind` base.

Orden de resolución del tipo, por capa:

1. Buscar un tipo cuyo **nombre** sea el esperado.
2. Si existe, releer su `CompoundStructure` **real** y compararla con la capa:
   una sola capa, mismo material, mismo espesor dentro de la tolerancia, misma
   función, mismo fingerprint. **Sólo entonces se reutiliza**
   (`type_reused: true`).
3. Si el nombre existe pero la composición **difiere**, no se sobrescribe y no se
   modifica: se crea una variante determinista

       [TIPO ORIGINAL] - [MATERIAL] - [NN] - [DIGEST CORTO]

   donde el digest corto son los ocho primeros caracteres hexadecimales del
   fingerprint. Es determinista: la misma capa produce siempre el mismo nombre de
   variante, así que dos corridas no acumulan tipos.
4. Si no existe ninguno, se duplica el tipo original y se le asigna la estructura
   de una sola capa (`type_created: true`).

Un tipo existente usado por otros muros **nunca** se modifica (D-14). Si
`SetCompoundStructure` falla, el tipo duplicado se elimina y el muro se rechaza
con `type_creation_failed`: un tipo duplicado que conserva la estructura
multicapa entera es exactamente el defecto que hacía pasar por "una capa" al muro
compuesto completo.

#### La verificación

Después del commit se relee el tipo **real** de cada muro resultante y se
compara con el esperado, nombre incluido, y se comprueba que su
`CompoundStructure` tiene exactamente una capa, con el material, el espesor y la
función de la capa que le tocaba. Un muro cuyo tipo no coincide —o que sigue
siendo multicapa— es `verify_type_mismatch` y hace rollback de ese muro.

#### Lo que se reporta por capa

`source_wall_type_name`, `material_name`, `layer_number`, `expected_type_name`,
`actual_type_name`, `type_reused`, `type_created`, `type_fingerprint`,
`resulting_wall_id`, `is_core_carrier`, `naming_verified` — más el offset
esperado, el offset medido y la desviación en milímetros de §1.3.

### 1.11 Parámetros

El portador conserva **todo** por identidad; no se le copia nada y se **verifica**
que no se movió lo que no debía moverse (top/base constraint y offsets,
unconnected height, room bounding, structural usage, fases, workset, design
option, mark, comments, pinned, y los parámetros compartidos y de proyecto).
Las dos excepciones deliberadas son `WALL_ATTR_WIDTH_PARAM` (lo fija el tipo
nuevo) y `WALL_KEY_REF_PARAM` (normalizado a `WallCenterline` en el paso 3), y
ambas se declaran en el resultado.

A los muros nuevos se copia por **identificador estable**: `BuiltInParameter`
primero, GUID de parámetro compartido después, y sólo entonces la definición.
Nunca por nombre traducido.

Cada parámetro sale clasificado en el resultado: `copied`,
`preserved_by_identity`, `read_only`, `computed`, `incompatible`,
`skipped_intentionally` — y los omitidos se listan, no se callan.

Política por defecto `parameter_copy_policy = safe_compatible`.

### 1.12 Aperturas, cortes y advertencias

El portador conserva sus inserts, luego conserva sus huecos. Para cada capa
secundaria el corte se establece con `JoinGeometryUtils.JoinGeometry`, y después
se **mide**: para cada insert se comprueba que la capa presenta un vacío que lo
atraviesa, comparando el sólido de la capa con y sin el hueco esperado. Que
exista el join **no** es prueba de que exista la apertura (D-24).

Las advertencias se filtran **por `FailureDefinitionId`**, nunca por texto
(D-23): `BuiltInFailures.OverlapFailures.WallsOverlap` y sus parientes de solape
entre muros, que aquí ocurren por construcción. Cualquier otro
`FailureDefinitionId` llega al llamador y hace fallar la verificación del muro
con `verify_unexpected_warning`. La comparación por texto localizado
desaparece.

Los joins de extremo del muro original con sus vecinos se capturan antes y se
restauran después. Una unión requerida que no se pueda restaurar es
`verify_join_missing` → rollback.

### 1.13 Dry-run, fingerprint y token

`dry_run = true` sigue siendo el valor por defecto y sigue sin abrir transacción.

El fingerprint por muro (`wall_split_v1`) liga: identidad del documento y su
ruta; `ElementId` y `UniqueId` del muro; su `WallType` y el digest de su
`CompoundStructure` completa (anchos, materiales por `UniqueId`, funciones,
límites de núcleo); `WallLocationLine`; la curva de ubicación cuantizada a
0.1 mm; `Flipped`; las restricciones superior e inferior; el conjunto de
`UniqueId` de sus dependencias; la política de portador; el plan de capas
resultante; y la versión del esquema. Más la marca de tiempo y la expiración que
ya aporta el host.

Antes del apply se **recalcula**. Cualquier diferencia ⇒ `stale_plan`, no se
escribe nada, y hace falta un dry-run nuevo. Esto sustituye la ligadura por
cuentas, que aceptaba un conjunto distinto de muros con el mismo total (D-27), y
elimina el caso en que un `null` se convertía en `-1` y coincidía con cualquier
otro plan (D-28).

### 1.14 Idempotencia y procedencia

Cada muro producido o convertido lleva un `Entity` de Extensible Storage
`HorizunWallSplit` con: `schema_version`, `source_wall_unique_id`,
`plan_fingerprint`, `original_wall_type_id`, `layer_index`, `role`
(`core_carrier` | `core_secondary` | `shell` | `finish`), los `UniqueId` de sus
hermanos, y la fecha/versión de la conversión.

Una segunda llamada responde con vocabulario, nunca con duplicados:
`not_split`, `already_split`, `matches_existing_plan`, `existing_plan_conflict`,
`repairable_partial_state`. Un estado parcial **no** se repara automáticamente:
exige su propio dry-run y su propia confirmación.

### 1.15 Contrato público

Se conservan `target_document`, `element_ids`, `view_id`, `origin_group_param`,
`dry_run` y `confirmation_token`. Se añaden, todos opcionales y con valor por
defecto compatible:

| Argumento | Valores | Defecto |
|---|---|---|
| `core_carrier_policy` | `structural_in_core_then_thickest` | ese mismo |
| `parameter_copy_policy` | `safe_compatible` | ese mismo |
| `allow_arc_walls` | booleano | `true` |
| `failure_policy` | `rollback_wall` | ese mismo |

No existe ningún valor que acepte pérdida de objetos en silencio: `failure_policy`
sólo admite `rollback_wall`, y está en el contrato para que el rechazo de
cualquier otro valor sea explícito en vez de ser una omisión.

### 1.16 Tolerancia

Tolerancia geométrica interna **0.5 mm** (`0.00164042 ft`), reportada como
`tolerance_mm: 0.5`. Toda comparación de posición, radio, espesor y colocación es
una **distancia medida** contra esa tolerancia; no hay comparación exacta de
`double` en ninguna decisión, y ninguna aceptación se apoya en el nombre de un
tipo.

---

## Fase 6b — matriz de afirmaciones contractuales

Qué promete el contrato, cómo se comprueba, **cuándo**, qué prueba automatizada
lo sostiene, y qué queda pendiente de medir en Revit.

Momentos:

- **P** — preflight, antes de que exista transacción. Un fallo es un rechazo.
- **S** — dentro de la `SubTransaction` del muro. Un fallo **revierte ese muro entero**.
- **C** — después del commit exterior, sobre el documento confirmado. Un fallo se
  **reporta** y tumba `all_verified`; **no puede deshacer nada**.

`WLR` = `WallLayerRulesTests`, `WDC` = `WallDependencyCoverageTests`,
`WTI` = `WallTypeIdentityAlignmentTests`, `WSW` = `WallSplitWiringTests`.

| Afirmación del contrato | Cómo se verifica | Cuándo | Prueba automatizada | Resultado | Pendiente en Revit |
|---|---|---|---|---|---|
| El muro original no se borra: conserva ElementId y UniqueId | se relee el elemento y se compara su UniqueId | S, C | `WSW` (doble pasada) | verde | medir sobre modelo real |
| N capas con volumen ⇒ exactamente N muros | se cuentan las capas materializadas verificadas | S, C | `WLR.NLayersWithVolume…` | verde | medir |
| Ningún muro resultante sigue siendo multicapa | `GetCompoundStructure().GetLayers().Count == 1`, releído | S, C | `WSW.The_verifier_holds…` | verde | medir |
| Una membrana de ancho cero no crea muro y conserva su número | plan puro + comprobación `number_preserved` | P, S, C | `WLR.AZeroWidthMembrane…` | verde | medir |
| El núcleo sale de `GetFirst/LastCoreLayerIndex`, no de la primera capa `Structure` | aritmética pura sobre los límites reales | P | `WLR.AStructuralLayerOUTSIDE…`, `…NoValidCore…` | verde | medir con ensamblajes reales |
| Un muro sin núcleo válido se rechaza, nunca cae a la capa 0 | `HasValidCore` + código `no_valid_core` | P | `WLR.AWallWithNoValidCore…` | verde | medir |
| Los offsets salen de las seis `WallLocationLine` | `u_loc` por tabla + offset medido contra el planeado | P, S, C | `WLR.EachLocationLine…`, `…DisplacedByHalfTheWall` | verde | **medir las seis en vivo** |
| La dirección exterior se mide sobre la cara del sólido | `HostObjectUtils.GetSideFaces` + normal de cara; fallback reportado | P | `WSW` (fuente) | verde | **medir, incluido flipped** |
| Los arcos conservan centro, ángulos y sentido; solo cambia el radio | `Arc.Create` por tres puntos reescalados radialmente | S, C | — (necesita Revit) | **no cubierto offline** | **medir arcos exterior/interior/flipped** |
| Splines y elipses se rechazan, nunca se rectifican | clase de curva + código `unsupported_curve` | P | `WSW` (fuente) | verde | medir |
| Cada dependencia `preserved_by_identity` tiene verificador | regla cerrada `DependencyKinds.DispositionFor` | P | `WDC` (6 pruebas), `WSW.Every_registered…` | verde | — |
| Una clase sin verificador bloquea el muro antes de escribir | `unsupported_blocking` ⇒ `unsupported_dependency` | P | `WDC.AKindWithNoVerifier…` | verde | medir con un elemento no reconocido |
| Puertas y ventanas: id, UniqueId, host, símbolo, colocación, rotación, flips, mirrored, nivel, fases, workset, design option, pinned, bbox, subcomponentes y parámetros | comparación campo a campo contra el snapshot | S, C | `WSW.Every_captured_instance…` | verde | **medir** |
| Solo cambian los parámetros con motivo documentado | tabla cerrada de 4 entradas con su razón | S, C | `WSW.The_allowed_to_change_list…` | verde | **medir: puede faltar alguno legítimo** |
| Openings: host, rectangular/perfilado, nº de curvas, longitud y desplazamiento | relectura de `BoundaryRect`/`BoundaryCurves` | S, C | `WSW` (despacho) | verde | **medir** |
| Sweeps y reveals: hosts, tipo, perfil, distancia, offset, vertical | `GetWallSweepInfo` releído | S, C | `WSW` (despacho) | verde | **medir** |
| Muros embebidos: curtain, niveles, offsets, curva | parámetros + digest de curva | S, C | `WSW` (despacho) | verde | **medir** |
| Cotas: no quedan huérfanas; conservan nº y representación de referencias | `References` + representaciones estables | S, C | `WSW` (despacho) | verde | **medir** |
| Tags: **todos** los elementos etiquetados, vista y cabeza | conjunto completo comparado en orden + nº de referencias, incluidas las no locales | S, C | `WSW.A_tag_keeps_its_whole_set…` (muta: BITES) | verde | **medir un tag multi-referencia** |
| Cotas y tags que Revit no devuelve como dependientes | censo inverso sobre `Dimension` e `IndependentTag`, una vez por llamada | P | `WSW.The_reverse_census_asks_the_annotations…` (muta: BITES) | verde | **medir** |
| El vano atraviesa cada capa secundaria | 5 rayos por insert; un punto no medible es fallo | S, C | `WSW.The_cut_is_probed_at_five…` | verde | **medir, incluida una capa mal cortada** |
| Los joins originales se restauran o el muro se rechaza | **todos** los campos capturados se releen: ids, orden de corte por vecino, flags por extremo y elementos en cada extremo **en orden** | P/S, S, C | `WSW.Every_field_WallJoinFacts_captures_is_compared…`, `…compared_IN_ORDER`, `…cut_order_is_re_read…` (mutan: BITES) | verde | **medir un muro unido por ambos extremos** |
| Las capas secundarias se unen solo al portador | política declarada y reportada | S | `WSW.The_secondary_wall_join_policy…` | verde | medir |
| Los tipos siguen `[TIPO] - [MATERIAL] - [NN]` | composición pura + relectura del nombre real | P, S, C | `WLR` (7 pruebas de nombrado) | verde | medir |
| Fingerprint, constructor y comparador cubren lo mismo | lista única `TypeIdentityFacts` + `CompareIdentity` compartida | P, S | `WTI` (4 pruebas), `WSW.The_matcher_and_the_builder…` | verde | medir reutilización real |
| Un nombre ocupado por otra composición genera variante determinista | digest corto de 8 hex | P | `WLR.TheVariantNameIsDeterministic…` | verde | medir colisión real |
| La procedencia se escribe **y se relee** | `WriteVerified` compara los 8 campos | S | `WSW.Provenance_is_written_through…` | verde | **medir un fallo de escritura real** |
| Un fallo de procedencia revierte el muro | `provenance_verification_failed` | S | `WSW.A_provenance_failure_rolls…` | verde | **medir** |
| El segundo llamado responde `already_split` sin abrir transacción | la procedencia se lee **antes** de planificar; `Read` corta ahí | P | `WSW.Provenance_is_read_before_anything_is_planned` (muta: BITES) | verde | **medir segundo apply** |
| Un hermano secundario da el mismo diagnóstico que el portador | `FindCarrier` recorre la lista de hermanos | P | `WSW.A_secondary_sibling_is_diagnosed_through_its_carrier` | verde | **medir** |
| `already_split` solo si el conjunto está completo y coherente | 12 señales: faltante, adicional, lista divergente, dos carriers, role incorrecto, índice ausente o duplicado, fingerprint y nombre de tipo, monocapa, otra conversión, barrido no corrido | P | `WSW.The_sibling_check_detects_every_failure_mode…`, `…Already_split_is_returned_only_when_nothing_fired` (mutan: BITES) | verde | **medir hermano borrado** |
| El token liga el ESTADO de cada dependencia, no su id | digest por dependencia + joins + estado del muro, todo por `FactBook` | P | `WallPlanFingerprintTests` (13), `FactBookTests` (12) (mutan: BITES) | verde | **medir `stale_plan` real** |
| La expectativa se construye desde el estado aprobado | `approved`, nunca `now` | S | `WSW.The_expectation_is_built_from_the_APPROVED_state` (muta: BITES) | verde | medir |
| Un muro que falla revierte solo, el lote sigue | `SubTransaction` por muro | S | — (necesita Revit) | **no cubierto offline** | **medir con inyección de fallo** |
| Solo se suprime `WallsOverlap`, por `FailureDefinitionId` | conjunto de un elemento; el resto se reporta | S/C | `WSW` (fuente) | verde | **medir en Revit no inglés** |
| El verificador completo corre otra vez tras el commit exterior | misma función, fase `AfterOuterCommit` | C | `WSW.The_detailed_verifier_runs…`, `…not_a_mere_existence_check` | verde | medir |
| Tras el commit exterior nada puede revertirse | declarado en `post_commit_limitation` y `can_roll_back:false` | C | `WSW.The_limit_of_the_post_commit…` | verde | — |

### Lo que esta matriz **no** dice

Ninguna fila marcada "medir" está demostrada. Las pruebas de cableado comprueban
que el código *hace la llamada*; no comprueban que la llamada *devuelva lo
correcto sobre un modelo real*. Dos filas están además **sin cubrir offline** por
construcción —los arcos y el rollback por inyección de fallo— porque ambas exigen
un `Document`.

**Dos** pruebas de esta suite nacieron **vacuas**, y las dos se detectaron
mutando el código, no leyéndolo:

1. Afirmaba que `case DependencyKinds.Tag:` aparecía en el archivo, y la
   satisfacía una *segunda* `switch` —la de códigos de error—, así que borrar el
   despacho real la dejaba verde.
2. Afirmaba que `already_converted` aparecía en el comando, y la satisfacía la
   ruta de apply, así que borrarlo del **dry-run** la dejaba verde.

Ambas se reescribieron para extraer primero la región relevante y afirmar dentro
de ella. **Toda conexión crítica se somete ahora al ejercicio**: hay un arnés de
mutación con 16 mutaciones, una por conexión, y las 16 hacen fallar su prueba.
Las que llevan «(muta: BITES)» en esta matriz están en él.

---

## Fase 6c — el flujo público, por caso

Qué recorre exactamente una llamada, y en qué punto se decide cada respuesta. El
orden importa: **la procedencia se lee antes de planificar**, y ese orden es la
diferencia entre que `already_split` sea alcanzable o sea una promesa muerta.

```
horizun_split_multilayer_walls
  │
  ├─ DocumentGate.ForMutation ............ target_document, documento activo
  ├─ ReadOptions ........................ políticas contra sus conjuntos cerrados
  ├─ WallReverseCensus.Build(doc) ....... UNA vez por llamada: cotas y tags que
  │                                        apuntan al muro sin volver por
  │                                        GetDependentElements
  └─ por cada muro: WallSplitFacts.Read
       │
       ├─ 0. ReadProvenanceState  ◄────── PRIMERO, antes de todo lo demás
       │      │
       │      ├─ sin sello ──────────────► sigue a 1
       │      └─ con sello ─ FindCarrier ─ InspectSiblingSet ─► NO ELEGIBLE
       │
       ├─ 1. ReadBlockingConditions ..... workshared, grupo, design option,
       │                                   slanted/tapered, attachments, perfil
       │                                   editado, clase de curva
       ├─ 2. ReadAssembly → WallLayerRules.Plan
       ├─ 3. normal exterior MEDIDA sobre la cara del sólido
       ├─ 4. censo + censo inverso, con vocabulario cerrado
       └─ 5. PlanFingerprint (estado completo de todo)
```

### Caso A — muro nuevo

```
Read → sin sello → elegible
  dry_run:  eligible[] con plan de capas, núcleo, portador y razón,
            dependency_ledger, secondary_walls_to_create, token
  apply:    RequireConfirmation → StillTheSame → Transaction
              └─ SubTransaction por muro
                   ├─ revalida fingerprint (stale_plan si cambió algo)
                   ├─ resuelve TODOS los tipos antes de tocar nada
                   ├─ portador: ChangeTypeId + LocationLine + medir-y-corregir
                   ├─ crea las demás capas, copia constraints y parámetros
                   ├─ une capas al portador y RESTAURA los joins originales
                   ├─ escribe procedencia y LA RELEE (8+5 campos)
                   ├─ WallSplitVerifier.Run(BeforeSubTransactionCommit)
                   └─ pasa → Commit    |    falla → RollBack de ESE muro
            commit exterior
            WallSplitVerifier.Run(AfterOuterCommit)  ← mismo verificador
```

### Caso B — muro `already_split`

```
Read → 0. sello presente, role = core_carrier
     → FindCarrier devuelve el propio muro
     → InspectSiblingSet: todos presentes, sellados, misma conversión,
       listas coincidentes, un solo carrier, índices completos y sin duplicar,
       roles correctos por índice, monocapa, fingerprint de tipo correcto,
       y NINGÚN muro extra con este plan en el documento
     → already_split
  dry_run:  already_converted[] con el estado del conjunto completo
  apply:    tampoco es elegible; NUNCA se abre transacción para él
```

**Por qué el orden es el arreglo.** Tras la conversión el portador es monocapa.
Si se planificara primero, `WallLayerRules.Plan` lo rechazaría como
`single_layer` y el sello no se consultaría jamás: `already_split` sería
inalcanzable desde el flujo público. Hay una prueba que compara los índices de
las tres llamadas dentro de `Read` y falla si el orden se invierte.

### Caso C — el usuario selecciona una capa secundaria

```
Read → 0. sello presente, role = finish | shell | core_secondary
     → SelectedSecondarySibling = true
     → FindCarrier recorre la lista de hermanos hasta el role core_carrier
     → InspectSiblingSet sobre EL PORTADOR
     → el mismo diagnóstico que en el caso B
  El reporte nombra el core_carrier_id, así que quien seleccionó un acabado ve
  de qué conversión forma parte.
```

### Caso D — estado parcial

```
Read → 0. sello presente
     → InspectSiblingSet encuentra al menos uno de:
         hermano faltante · hermano adicional con este mismo plan ·
         lista de hermanos divergente entre miembros · dos carriers ·
         role incorrecto para su índice · índice esperado ausente ·
         índice duplicado · fingerprint de tipo distinto ·
         nombre de tipo distinto · ya no es monocapa ·
         hermano de otra conversión · el barrido no pudo correr
     → repairable_partial_state
  NO se repara automáticamente. Necesita su propio dry-run y su propia
  confirmación, porque decidir qué reparar no es algo que esta llamada pueda
  resolver sola.

  Si el sello existe pero no describe una conversión interpretable
  (esquema ajeno, sin fingerprint, sin origen, sin cuenta, sin hermanos)
     → provenance_invalid, que no es un estado parcial a reparar.
```

### Caso E — `stale_plan`

```
dry_run  → PlanFingerprint_1 → ResolvedPlan → token
           (alguien mueve una puerta, cambia un tipo, rompe un join,
            invierte un orden de corte, edita una restricción…)
apply    → Read vuelve a leer todo → PlanFingerprint_2
           DocumentGate.RequireConfirmation compara el plan resuelto
           SubTransaction: Convert compara now.PlanFingerprint contra el aprobado
           → distintos → stale_plan, RollBack, cero escrituras
```

La expectativa que el verificador compara se construye **desde el estado
aprobado**, nunca desde la relectura. Si al fingerprint se le escapara algo
alguna vez, verificar contra el modelo cambiado haría que la conversión
estuviera de acuerdo consigo misma.

### Qué incluye el fingerprint del plan

Por muro: `document`, `wall_unique_id`, `wall_element_id`,
`wall_type_unique_id`, `wall_type_name`, `wall_kind`, `location_line`,
`core_first`, `core_last`, `opening_wrapping`, `end_cap`, `flipped`,
`core_carrier_policy`, `carrier_layer_index`, `carrier_reason`,
`compound_structure` (ordenado), `layer_plan` (ordenado), `curve` (ordenado),
`dependencies` (**conjunto de digests de estado completo**, sin orden),
`joins` (digest), `wall_state` (digest).

**`dependencies` es el arreglo del P0-4.** Antes era la lista de `UniqueId`, que
solo detecta que una dependencia apareció o desapareció. Ahora cada dependencia
aporta un digest de todo su estado:

| Clase | Qué entra en su digest |
|---|---|
| todas | kind, ElementId, UniqueId, categoría, tipo, host, vista, **todos** los parámetros por clave estable |
| instancia | símbolo, nivel, punto, rotación, flips, mirrored, orientación, fases, workset, design option, pinned, bounding box, subcomponentes por UniqueId **y por símbolo** |
| opening | rectangular o perfilado, nº de curvas, longitud, puntos del contorno |
| sweep/reveal | tipo, perfil, distancia, offset, vertical, hosts |
| muro embebido | curtain, niveles, offsets, digest de curva |
| cota | nº y representaciones estables de sus referencias, valor |
| tag | **todos** los ids y UniqueIds etiquetados, nº de referencias, si alguna no es local, posición de cabeza |

`joins` aporta ids unidos, orden de corte por vecino, flags por extremo y
**elementos en cada extremo, en orden**. `wall_state` aporta las quince
restricciones, fases, workset, room bounding, structural usage, línea de
ubicación, sección transversal, attachments, pinned, grupo, design option, nivel
y sketch.

Todos se construyen con `FactBook`, que garantiza —y las pruebas lo demuestran—
que reordenar un diccionario **no** mueve el digest, que el jitter por debajo de
0.1 mm **no** lo mueve, que un movimiento real de 0.2 mm **sí**, y que una clave
duplicada o un número no medido se rechazan en vez de hashearse.

---

## Fase 9a — revisión adversaria del candidato

Cinco revisores independientes, uno por lente (idempotencia, staleness,
dependencias, atomicidad y honestidad del contrato), cada hallazgo sometido a un
refutador que intentaba tumbarlo. **22 hallazgos levantados, 12 sobrevivieron.**
Los doce están corregidos; además, cuatro de los «refutados» resistieron mi
propia relectura y también se corrigieron.

### Los que rompían algo de verdad

| # | Hallazgo | Consecuencia |
|---|---|---|
| 1 | `already_split` se decidía **solo** con sellos y tipos, sin tocar la posición de nada | Una conversión que **esta misma herramienta** reportó como fallida post-commit —irreversible, porque la transacción ya cerró— volvía a leerse como «un split completado, presente y coherente», con `partial_state_walls: 0`. El único muro que sabíamos malo se leía limpio, y encima la herramienta se negaba a tocarlo por eso |
| 2 | El re-leído de apply omitía los dos barridos de documento | **Rompía la herramienta**: cualquier muro con una cota o un tag rechazaba como `stale_plan` y no podía convertirse nunca |
| 3 | `VerifyCuts` filtraba a `FamilyInstance` con bbox legible y salía si no quedaba nada | Los `Opening` —que son inserts de primera clase— y los curtain walls embebidos **no se sondeaban jamás**; y un muro con todos sus inserts ilegibles reportaba `cut_verified: true` habiendo medido nada |
| 4 | `VerifyEmbeddedWall` era el único verificador que ni recibía el portador | El contrato decía «sigue embebido en él» sin nada detrás |
| 5 | El rollback nunca consultaba `RollbackResult.Confirmed` | Un `Pending` o un `Error` se reportaban como «quedó exactamente como estaba» |
| 6 | `ObservedOffsetMm` se emitía como medición y no se asignaba en ningún sitio | Siempre `0.0`. El helper que podía calcularlo tampoco tenía llamador |
| 7 | Los parámetros se capturaban para siete clases y se comparaban para **una** | Un opening, un sweep, una cota o un tag podían volver con todo cambiado y pasar |
| 8 | La normal exterior no tenía **ninguna** corroboración | Se coloca a lo largo de ella y se mide a lo largo de ella: una normal invertida construye el muro del revés y lo verifica como correcto |
| 9 | El flip de las capas decía «verified below either way» y nada de abajo releía orientación alguna | Afirmación sin nada detrás |
| 10 | `origin_group_param` no se copiaba en silencio si faltaba, era de solo lectura o no era texto | Y todo el bloque dentro de un `catch` |
| 11 | Dos códigos publicados —`matches_existing_plan`, `verify_unexpected_warning`— no los emitía nadie | Un cliente ramifica por un valor que nunca recibirá |
| 12 | Un sweep se medía solo con `Distance` y `WallOffset`, que se miden **desde el host** | No pueden cambiar cuando el host se re-tipa y se mueve: la comprobación de posición no podía fallar |

Y una que encontró mi propia prueba de enumeración al escribirla: los cuatro
códigos específicos de insert (`verify_insert_identity`, `..._host`,
`..._subcomponents`, `verify_parameter_mismatch`) dejaron de emitirse cuando
reencaminé todo por un código genérico por clase.

### Consecuencia operativa que hay que decidir antes del vivo

La regla de cobertura cerrada hace que **cualquier clase de dependencia sin
verificador bloquee el muro**. Eso incluye `WallFoundation` (zapata corrida) y
`Rebar`. En la práctica: **un muro estructural con zapata o con armadura se
rechaza** con `unsupported_dependency`.

Es el comportamiento que el mandato pide —un rechazo seguro es correcto, una
conversión parcial no— pero es una clase de muro muy corriente. La decisión de si
se les escribe verificador o se acepta el rechazo es del director, y es mejor
tomarla antes de la sesión viva que descubrirla dentro de ella.

### Sobre las pruebas de esta ronda

**Cuatro** aserciones de este trabajo nacieron vacuas, y las cuatro las encontró
la mutación, no la lectura. Todas por la misma causa: **afirmar presencia no es
afirmar cableado.**

1. `case DependencyKinds.Tag:` existía… en otra `switch`.
2. `already_converted` existía… en la ruta de apply, no en el dry-run.
3. El mensaje del insert no medible existía… con la guarda anulada.
4. `CorroborateNormal` existía… sin que nada la llamara.

Las cuatro se reescribieron para extraer primero la región y afirmar la guarda
dentro de ella. El arnés está en `scripts/wall-split-mutation-harness.py`:
**30 mutaciones, 30 muerden.** Correrlo es la única evidencia que tengo de que
estas pruebas valen algo.

## Fase 12b — tercera sesión viva: el primer muro que sí convirtió

Revit 2026 build 26.4.0.32, candidato `fd11af2`, documento desechable
`HZ_WALLSPLIT`. Siete de siete binarios con el sello `fd11af2`, ninguno *dirty*,
Authenticode `Valid` en todos, y `horizun_health` reportando el mismo commit con
`built_from_clean_tree: true`.

### Lo que se midió

El defecto de la curva viva está resuelto: **un muro real convirtió de extremo a
extremo por primera vez en toda la campaña**. Sobre un muro limpio, aislado, sin
nada alojado:

| Propiedad | Medida |
|---|---|
| El original no se eliminó | `originals_deleted = 0` |
| Conservó ElementId y UniqueId | sí, y quedó como portador del núcleo |
| Capas con volumen → muros | 5 planificados, 5 producidos |
| Membranas de ancho cero | 2, **no** materializaron y **conservaron su número** |
| Numeración resultante | `01, 02, 04, 05, 07` — faltan 03 y 06 a propósito |
| Desviación por capa | `0.0 mm` en las cinco |
| Nombre de tipo | `[tipo] - [material] - [NN]`, verificado contra el modelo |
| Normal exterior | medida y corroborada |
| Verificación post-commit | **pasó** |

Y aun así: `all_verified: false`.

### El defecto que solo aparece cuando la conversión funciona

Revit dejó **dos advertencias permanentes** en el modelo:

```
"Highlighted elements are joined but do not intersect."
   (portador 1664073, capa 01)
   (portador 1664073, capa 02)
```

`WallSplitExecutor` une cada muro de capa al portador **a propósito**, para que
las aberturas del portador corten a través de ellos. El portador es la capa que
elija la regla de núcleo — aquí la 05 de 7 — así que las capas 01 y 02 están
separadas de él por las capas 03 y 04 y **no pueden intersecarlo**. Unidas por
diseño, disjuntas por geometría.

El preprocesador de fallos solo esperaba la advertencia de solape de muros, así
que esta contaba en contra. Consecuencia real: **`all_verified` no podía ser
`true` para ningún muro cuyo portador no fuera una capa extrema.** Toda
conversión correcta se reportaba a sí misma como no verificada.

Nadie lo había visto porque **ninguna conversión había terminado nunca**: las
catorce de la sesión anterior fallaron antes, por la curva.

### La corrección

Un segundo conjunto de esperadas, descartado **solo cuando todos los elementos
que la advertencia nombra son muros que esta operación creó o convirtió**. Una
entrada general también borraría una advertencia legítima sobre los elementos
del usuario, que es justo el hallazgo que esta clase existe para conservar.

**No está verificada en vivo.** Revit publica dos identificadores con ese
significado (`JoiningDisjoint` y `JoiningDisjointWarn`) y no se capturó cuál se
levantó; se aceptan ambos, así que la corrección no depende de distinguirlos —
pero *que funcione* no se ha medido. El canario de la próxima sesión lo resuelve:
si el identificador fuera el equivocado, falla igual.

### Cobertura reinvestigada

| Caso | Antes | Ahora | Medida |
|---|---|---|---|
| 18 abertura por perfil | «no hay fixture» | `unsupported_api` | `NewOpening`: *the hostElement is not a floor, ceiling, roof or toposolid* |
| 21 perfil editado | «no hay fixture» | `unsupported_api` | no hay API pública |
| 22 attachment | «no hay fixture» | `unsupported_api` | no hay API pública |
| 25 design option | «no hay fixture» | `unsupported_api` | no hay API pública de creación |
| 26 propiedad de otro | «no hay fixture» | `blocked_environment` | hace falta un segundo usuario |
| 23 tapered | «no hay fixture» | `blocked_fixture` | *the current wall does not support the cross section*; el **slanted** sí se construyó |
| 40 / 41 area y path | «la plantilla no trae tipos» | `blocked_fixture` | `AreaReinforcementType=0`, `PathReinforcementType=0`, `RebarBarType=11` |
| 39 estribos | «no se construyó» | **fixture construido** | barra `StirrupTie` creada |
| 42 fabric | «la plantilla no trae tipos» | **fixture construido — la afirmación anterior era FALSA** | hay 10 `FabricSheetType` y 1 `FabricAreaType`; la llamada estaba mal, no el documento |

El caso 42 es el que más conviene recordar: se había reportado una limitación del
documento y era un error de quien llamaba. Leyendo las excepciones argumento por
argumento apareció la firma real —
`Create(doc, host, IList<CurveLoop>, major, minor, areaTypeId, sheetTypeId)` — y
el `FabricArea` se creó sin problema. **Un fixture que no se pudo construir no es
lo mismo que un fixture que no se puede construir.**

### Resultado de la matriz

`0 passed, 0 failed, 0 unverified, 55 not_run`. El canario falló y el runner se
detiene ahí por diseño: los 55 casos habrían recorrido el mismo camino y
reportado el mismo `all_verified: false`, que es **un hallazgo impreso 55 veces,
no 55 mediciones**. `not_run` es un hecho sobre la corrida y no dice nada sobre
el producto.

### Fase 12c — la supresión, revertida

La corrección de 12b fue **rechazada por el director y revertida**, y tenía razón.

Meter `JoiningDisjoint` y `JoiningDisjointWarn` en la lista blanca **cambia el
veredicto, no la geometría**. La unión entre el portador y una capa que no lo
toca sigue ahí y sigue sin significar nada; silenciar la queja elimina la única
evidencia de que existe.

Y es peor que inútil, por **lo que no se midió**. El muro que produjo esas
advertencias no tenía puerta, ni ventana, ni abertura. El verificador es honesto
a nivel de campo — con cero inserts escribe `cut_coverage.probed = false` y la
nota *«No probe was run and none is claimed»* — pero **el canario no leía ese
campo**. Sus 17 comprobaciones incluían «la verificación post-commit pasó»
mientras la única propiedad para la que existen esas uniones —que un hueco del
portador llegue a cada capa— **no se había probado en absoluto**.

Con la advertencia suprimida, un muro sin inserts pasa todo. Eso es convertir
ausencia de evidencia en éxito.

Dos consecuencias, ambas aplicadas:

1. **La advertencia vuelve a ser inesperada** y lo seguirá siendo *hasta que la
   construcción deje de producirla*. La corrección pertenece a la topología del
   ejecutor, no al conjunto de esperadas. Hay pruebas y mutaciones que fijan la
   **ausencia** de la supresión, porque una política rechazada necesita una
   prueba igual que una aceptada: sin ella, el siguiente lector ve
   `all_verified: false` sobre una conversión correcta y vuelve a meter los ids,
   que desde dentro se siente exactamente como arreglarlo.

2. **El canario declara su cobertura de corte.** Registra
   `cut_coverage_probed`, y cuando es `false` dice en el artefacto y en pantalla
   que prueba *la conversión* y no *los cortes*, y que eso lo miden los casos
   13–17.

### Fase 12d — el pase vacuo del corte

Investigando la topología apareció un defecto **del producto**, no del harness, y
es el mismo de fondo que el director señaló: convertir ausencia de evidencia en
éxito.

En la única conversión completa que existe
(`artifacts/live/wallsplit-20260830-190310/call-003-apply-1664073.json`):

```
cut_coverage.probed = false
cut_checks          = 0
cut_verified        = TRUE en las SIETE capas
```

incluidas las capas 03 y 06, que son membranas de ancho cero y **no tienen muro
ninguno**. La causa es una línea:

```csharp
CutVerified = layer.IsCoreCarrier || verdict.CutChecks.Children<JObject>()
    .Where(c => c.Value<int>("layer_number") == layer.LayerNumber)
    .All(c => c.Value<bool>("cut_verified"))
```

`.All()` sobre una secuencia vacía es `true`. Un campo llamado `cut_verified`
—que un lector toma por una medición— decía que el hueco estaba probado sin
haber lanzado un solo rayo. El verificador sí era honesto un nivel más abajo
(`cut_coverage.probed = false`), pero **el ejecutor nunca lo consultaba**.

La decisión pasa al núcleo sin Revit, `WallLayerRules.CutClaim`, con **tres
estados** porque hay tres y solo dos son un veredicto:

| valor | significado |
|---|---|
| `true` | se sondeó y todos los rayos salieron limpios |
| `false` | se sondeó y algún rayo encontró material |
| `null` | **no se sondeó** — ni aprobado ni suspendido |

`null` también para el portador: aloja los insertos nativamente, así que no hay
hueco que reproducir y «verificado» describiría una prueba que no le aplica.
Cada `null` va acompañado de su razón; un `null` sin razón es un encogimiento de
hombros.

También era falsa la prosa. `verification_note` afirmaba sin condición que *«each
secondary layer was ray-cast to prove the opening passes through it»*. Ahora
cuenta cuántos muros llevaban inserto y lo dice: si ninguno, dice que **no se
sondeó ningún corte y no se afirma nada**.

Ocho pruebas reales —no de texto: `CutClaim` vive en el núcleo y se ejecuta— más
ocho mutaciones. Una de las pruebas reproduce las siete capas del muro medido y
exige que **ninguna** pueda responder `true`.

## Fase 13 — el experimento de topología, medido

Revit 2026 build 26.4.0.32, candidato `a3b83b9` (7/7 binarios sellados, ninguno
dirty, Authenticode `Valid`), fixture desechable, cuatro muros idénticos de siete
capas con una puerta real cada uno, en una zona nueva a partir de x = 1 200 000 mm.

### Las cinco preguntas

| # | Pregunta | Respuesta medida |
|---|---|---|
| 1 | ¿La estrella transmite el corte? | **SÍ**, a todas las capas, incluidas las que no tocan el portador |
| 2 | ¿Solo a las adyacentes? | **NO** — las capas 01 y 02, separadas del portador, quedan cortadas |
| 3 | ¿Una cadena entre vecinas lo transmite? | **SÍ**, transitivamente, a dos saltos |
| 4 | ¿Sin joins? | **NINGUNA** capa se corta |
| 5 | ¿Hacen falta aberturas explícitas? | **NO** — A y B funcionan; la variante D no se construyó |

### El control es lo que da sentido a lo demás

Sin joins, cada capa secundaria midió **exactamente su propio espesor** de
material: 92.0, 75.0, 19.5 y 12.5 mm contra anchos nominales de 92.0, 75.0, 19.5
y 12.5. El hueco en A y B lo produce **el join**, no la puerta cortando lo que
tiene cerca.

### Estrella contra cadena — la diferencia real

El volumen de intersección **no** los separa: *todo* par de muros de capa
paralelos comparte volumen cero, también los que se tocan. Medido: 4 de 4 pares
unidos dan volumen cero en **ambas** topologías. El discriminador es la
**separación**:

| topología | pares unidos | con separación |
|---|---|---|
| estrella | L05–L01, L05–L02, L05–L04, L05–L07 | **2** (94.5 mm y 19.5 mm) |
| cadena | L01–L02, L02–L04, L04–L05, L05–L07 | **0** |

Y esos dos pares separados son **exactamente** los dos sobre los que la
herramienta avisó en el canario anterior: `(portador, 01)` y `(portador, 02)`.

**La cadena corta igual que la estrella y no crea ni un solo join separado.**
Elimina el aviso *por construcción*, que es lo que se pedía.

### Un defecto del producto, encontrado por poner una puerta

La variante A se ejecutó con la herramienta real. **Se revirtió**, con rollback
confirmado:

```
verify_parameter_mismatch
"it came out with 1 parameter(s) this conversion has no reason to change:
 bip:HOST_AREA_COMPUTED"
```

Dos tablas codifican el mismo hecho y no coinciden: `NeverCopied`
(`WallSplitExecutor.cs:989`) tiene 14 entradas e **incluye**
`bip:HOST_AREA_COMPUTED`; `AllowedToChange` (`WallSplitVerifier.cs:516`) tiene 4
y no lo incluye. El copiador declina copiar un parámetro **calculado** y el
verificador llama a su cambio inexplicado y revierte el muro.

**La herramienta publicada no puede convertir ningún muro con puerta.** No se
había visto porque ningún muro con puerta había llegado nunca al ejecutor.

La reversión fue completa: el muro volvió a medirse después y seguía siendo el
tipo compuesto original, 5/5 limpio, sin un muro ni un tipo de más en el
documento. Y **cada capa reportó `cut_verified: null`** — la retirada de
afirmaciones de `a3b83b9`, funcionando sobre un rollback real.

### Lo que este experimento NO establece

- Si los joins separados de la estrella levantan el aviso **siempre**. Las
  construcciones manuales no dejaron aviso nuevo y la corrida de la herramienta
  sí dejó dos; el puente descarta diálogos modales durante un script, lo que
  puede resolver avisos, así que esta sesión no puede comparar las dos y no lo
  pretende.
- Si el corte sobrevive a operaciones posteriores (mover la puerta, cambiar su
  tipo, un regenerado más tarde). Solo se midió el estado inmediatamente después.
- Si una cabecera no rectangular corta bien: cinco puntos con un cuarto de
  margen no distinguen un arco de un rectángulo.
