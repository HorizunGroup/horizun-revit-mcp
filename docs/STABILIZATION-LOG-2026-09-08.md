# Bitácora de estabilización — bloque de producción de entregables

Sesión autónoma iniciada el 2026-09-08 sobre la rama
`codex/bim-production-product-layer`, con `HEAD` en `5498141` y un árbol de
trabajo que ya contenía el trabajo sin commitear de sesiones anteriores. Nada
se restauró, limpió ni descartó.

Esta bitácora registra decisiones y medidas. El informe de resultados es
[RELEASE-CANDIDATE-REVIEW-2026-09-07.md](RELEASE-CANDIDATE-REVIEW-2026-09-07.md)
y su continuación al cierre de esta sesión.

## Estado de partida verificado

| Hecho | Cómo se comprobó |
| --- | --- |
| Rama `codex/bim-production-product-layer`, HEAD `5498141`, 43 archivos modificados y 18 sin rastrear | `git status --porcelain` |
| Core 3.800/3.800, 0 omitidas | `dotnet test tests/Horizun.Core.Tests -c Release` |
| Revit 2026 pid 45060 sano, add-in 1.2.1, commit limpio `5498141`, DLL `fe792a0d…`, cero documentos abiertos | `horizun_target pid=45060` + `horizun_health` |
| Revit 2025 pid 37388 es la sesión del usuario; fuera de alcance | `horizun_target` (no se le envió ninguna llamada) |

## Hechos de API medidos sobre los binarios instalados

Leídos de los `RevitAPI.dll` de esta máquina con un lector de metadatos
(`System.Reflection.Metadata`), no de documentación recordada.

- `PDFExportOptions` expone las mismas 21 propiedades en 2023, 2024, 2025, 2026
  y 2027: `Combine`, `FileName`, `PaperFormat`, `PaperOrientation`,
  `PaperPlacement`, `ZoomType`, `ZoomPercentage`, `OriginOffsetX/Y`,
  `ColorDepth`, `RasterQuality`, `ExportQuality`, `AlwaysUseRaster`,
  `StopOnError`, `HideCropBoundaries`, `HideScopeBoxes`, `HideReferencePlane`,
  `HideUnreferencedViewTags`, `MaskCoincidentLines`,
  `ReplaceHalftoneWithThinLines`, `ViewLinksInBlue`.
  La única diferencia entre años es `Get/SetExportInBackground`, que aparece en
  2025 y no existe en 2023–2024.
- `ExportPaperFormat` tiene los mismos 23 valores en los cinco años.
  `PageOrientationType`: Portrait/Landscape/Auto. `PaperPlacementType`:
  Center/LowerLeft/Margins. `ZoomType`: FitToPage/Zoom. `ColorDepthType`:
  BlackLine/GrayScale/Color. `RasterQualityType`: Low/Medium/High/Presentation.
  `PDFExportQualityType`: DPI72…DPI4000.
- `ViewCropRegionShapeManager.GetAnnotationCropShape()` y
  `CanHaveAnnotationCrop` existen en los cinco años.
- `View` expone `IsValidViewScale(int)`, `Scale`, `GetPrimaryViewId()`,
  `GetTemplateParameterIds()` y `AreAnnotationCategoriesHidden`.

Consecuencia: el contrato de impresión puede ser uniforme para 2023–2027, con
`export_in_background` como única opción condicionada por año.

## Diario

### 1. Visibilidad de anotaciones (bloque §5)

El obstáculo bloqueante medido en la campaña anterior era un `SpaceTag` sin caja
legible, no oculto explícitamente. La corrección anterior excluía solo
ocultaciones explícitas y por tanto seguía bloqueando.

Decisión: clasificar cada anotación candidata con un veredicto explícito y
resolver la exclusión con la propia consulta de Revit sobre la vista, no con un
`catch` ni con testimonio de un script Python.

Regla implementada, conservadora por construcción:

1. Si la caja de la anotación en la vista **se lee**, es obstáculo. Siempre.
   Da igual lo que diga cualquier otra señal: incluir es la opción segura.
2. Si la caja **no** se lee, se busca una exclusión verificable:
   elemento ocultado explícitamente, categoría oculta, categorías de anotación
   desactivadas en bloque, o ausencia del colector de elementos visibles de la
   vista (`FilteredElementCollector(doc, viewId)`, que es la respuesta del
   anfitrión, no de un script).
3. Sin exclusión verificable, el veredicto es `unknown_unreadable_extent` y la
   operación se niega con causa estructurada: IDs, categoría, clase, vista
   propietaria y qué sonda no pudo concluir.

El conjunto considerado es la **unión** de las anotaciones que la vista posee y
las que su colector visible devuelve, de modo que una vista dependiente —cuyas
anotaciones pertenecen a la vista primaria— deja de verse como una vista vacía.

Límite del recorte: se usa el recorte de **anotación** cuando está activo, y el
del modelo solo si no lo hay. Usar el recorte del modelo como límite rechazaba
colocaciones legítimas en el margen de anotación.

Superficie: `layout_accept_unmeasurable` (en `horizun_annotate`, por acción) y
`accept_unmeasurable` (en `horizun_plan_annotations` `auto_tags`) aceptan IDs
exactos; no existe un interruptor global. Toda cobertura viaja en
`annotation_coverage` con `clearance_scope` `complete|partial|undecided`.
La verificación de etiquetas devuelve ahora la causa (`reason`) en vez de un
`catch { return false; }` mudo.

Pruebas: `AnnotationVisibilityTests` (15). Compila en 2023–2027.

### 2. Contrato de impresión PDF (bloque §7)

`PdfPrintPolicy` (sin Revit) define el conjunto cerrado de 20 campos de
`pdf_print`, lo valida por nombre y lo clasifica en cuatro estados:
`requested` / `defaulted` (origen), `applied` (valor releído del objeto de
opciones tras asignarlo), `verified` / `verified_mismatch` (probado desde la
geometría de la página producida: solo `paper_format` y `orientation`) y
`requested_unverifiable` (todo lo demás: color, raster, DPI, ocultaciones,
zoom, colocación). `paper_format=Default` se verifica contra `ViewSheet.Outline`
del plano origen; un formato nominal contra su tamaño de papel, con tolerancia
de 2 pt. Un `verified_mismatch` **falla la exportación** (los archivos pueden
existir; se dice). Opciones que serían ignoradas en silencio se rechazan:
`zoom_percentage` sin `zoom=zoom`, offsets sin `placement=margins`,
`export_in_background` en 2023/2024.

Pruebas: `PdfPrintPolicyTests` (19). El informe entra en el manifiesto como
`print_policy` y el ensayo (dry run) publica `print_policy.effective`.

### 3. `view_scale` homogéneo (bloque §8A)

`horizun_manage_views` admite `view_scale` en toda operación cuyo resultado
tiene escala de dibujo: `create_floor_plan`, `create_ceiling_plan`,
`create_structural_plan`, `create_area_plan`, `create_drafting`, `create_3d`,
`create_callout`, `create_section`, `create_elevation`, `duplicate_view` y
`apply_template`. El valor se comprueba con `View.IsValidViewScale` (estático)
antes de abrir transacción; una plantilla que controla la escala
(`GetTemplateParameterIds` − `GetNonControlledTemplateParameterIds`) rechaza el
lote nombrándola en vez de dejar que gane en silencio; planos, tablas y
perspectivas rechazan; la escala se relee y se reporta por fila
(`view_scale`, `view_scale_verified`). En cualquier otra operación el argumento
se rechaza, nunca se ignora.

### 4. Desbordamiento del rótulo (bloque §8C)

Operador de entidad completa `fits_titleblock_cell` sobre `sheet` en el
conjunto de requisitos de planimetría: `{field: sheet_number|name, cell_width,
text_height, char_width_factor?}` en las unidades de la llamada. La geometría de
la casilla es dato del perfil (neutral). Estimación declarada como tal
(caracteres × altura × avance medio, 0,6 por defecto); un valor que desborda es
un hallazgo con la aritmética y **nunca se recorta ni renombra**. Un valor
ilegible es `unknown`, no aprobado.

Pruebas: `PlanimetryTitleblockCellFitTests` (5).

### 5. Preflight completo del perfil (bloque §9)

`DeliveryPreflight` (sin Revit) recorre cada etapa del perfil y devuelve todos
los errores a la vez, por `stage` y `field`: campos desconocidos, tipos,
rangos, selectores, ejes, lados, `nearest_face` sin `probe_point`, `link_instance_id`
(rechazado por medida previa), listas de IDs, rectángulos de packing, claves de
ítems, ruta de salida absoluta y `.pdf`, política de impresión contra el año del
host y conjunto de requisitos. La mitad de anfitrión (`PlanViewsCommand`)
añade existencia de vistas/tipos/elementos, clase de tipo de cota (lineal),
tipo de etiqueta de anotación, planos no placeholder con exactamente un rótulo,
`Viewport.CanAddViewToSheet` sobre cada candidato, tablas ya colocadas,
directorio existente y escribible (sonda de escritura) y conflictos con archivos
existentes según `overwrite`. Lo que solo un ensayo o la exportación deciden se
lista en `undetermined`, no se cuenta como comprobado. Un plan con una etapa
inválida **se retiene entero** (`FailWithDetail` con el preflight).

Pruebas: `DeliveryPreflightTests` (13).

### 6. Libro de entrega persistente (bloque §6)

`DeliveryLedger` (sin Revit) + `DeliveryLedgerHost` (Revit). Un archivo de
eventos append-only por entrega en `%USERPROFILE%\.horizun\deliveries\<id>.jsonl`,
replegado en cada lectura; los eventos que no pueden aplicarse se reportan en
`replay_problems`, nunca se saltan.

Identidad: `delivery_id` (explícito o derivado de hash de perfil + huella del
documento), perfil (id/versión/sha256), documento (título/ruta/huella/año) y
add-in (versión/commit). Estados por etapa: `pending`, `in_progress`,
`completed`, `failed`, `blocked`, `invalidated`, `awaiting_approval`,
`approved`, `rejected`. Tipos: `navigate`, `write`, `capture`,
`approval_capture`, `audit`, `publish`. Las transiciones permitidas están en la
tabla `Transitions` (por tipo) y el orden se impone al entrar en `in_progress`
(toda dependencia `completed`/`approved`). Dependencias derivadas de las claves
del plan: cadena por vista, `pack` tras todas las capturas de vista, `audit`
tras `pack`, cada `capture_sheet_*` tras `audit`, `publish` tras `audit` y todas
las aprobaciones.

Detección de cambios: una escritura completada guarda `Element.VersionGuid` de
cada elemento que declaró; `delivery_status` y la exportación releen esos GUID y
**invalidan en cascada** lo que cambió. Una aprobación se liga a la huella del
plano y de sus viewports/vistas colocadas en el momento de aprobar.

Puerta de publicación: abierta solo con todas las etapas completadas o
aprobadas, auditoría con `no_blocking_findings=true`, y nada invalidado,
fallido, bloqueado, rechazado ni pendiente de aprobación. `horizun_export` con
`delivery_id` relee las huellas y **rechaza antes de tocar archivos** si la
puerta está cerrada; al terminar registra `publish` con los archivos y hashes.

Atomicidad declarada: una escritura tipada a la vez; navegación, capturas y
archivos externos no comparten transacción. Autoridad declarada: una auditoría
aprobada es un hecho sobre los elementos medidos, no permiso de escritura.

Operaciones (en `horizun_plan_views`, que sigue sin escribir el modelo):
`delivery_open`, `delivery_status`, `delivery_record`, `delivery_approve`,
`delivery_invalidate`; y `delivery_id` en `horizun_export`.

Pruebas: `DeliveryLedgerTests` (13).

### Inventario

Regenerado desde el servidor de desarrollo: 80 herramientas (sin cambio),
**202 operaciones** (197 + las 5 `delivery_*`) y **793 valores enumerados**
(730 + 46 enumeraciones de `pdf_print` + 3 de `units` + 1 `fits_titleblock_cell`
+ 5 operaciones + 6 `status` + 2 `decision`). El propio inventario declara que
un valor enumerado "es un argumento, NO un comportamiento probado"; el aumento
es consecuencia del contrato de impresión y del libro de entrega, no una medida
de calidad. `docs/TOOLS.md` actualizado; `inventory.tests.ps1` y
`public-consistency.tests.ps1` en verde.

### Estado automatizado tras los seis bloques

- Core: **3.865/3.865** (3.800 de partida + 65 nuevas), 0 omitidas.
- Server: **477/477**.
- Compilación 2023, 2024, 2025, 2026, 2027: cero errores y advertencias.
- Nada de esto es verificación en vivo; la sesión aislada en Revit 2026 sigue.

## Sesión aislada en Revit 2026 — candidato c1

- La instancia de pruebas Revit 2026 (pid 45060, cero documentos, instalación
  estable) se cerró de forma ordenada con `CloseMainWindow` (sin matar
  procesos). La sesión del usuario en Revit 2025 (pid 37388) no se tocó.
- `scripts/live/dev-addin-session.ps1 -Year 2026 -Enable -DevRoot
  %USERPROFILE%\.horizun\dev-addin-2026-09-08-c1`: copia firmada con el
  certificado local existente; manifiesto instalado apartado como
  `Horizun.addin.dev-session-aside`; DLL y servidor instalados intactos.
- Candidato c1: `Horizun.Revit.dll` SHA-256
  `7788b1a7eb40ac61d9a275e6de0ae3c00575cf588252070600dc47652b9deeef`,
  compilado del árbol `5498141-dirty` (sin commit, por diseño de esta sesión).
- Revit 2026 arrancado por su ejecutable (no abriendo un archivo); nuevo pid
  42956; `horizun_health` por el servidor de desarrollo
  (`src/Horizun.Server/bin/Release/net8.0/horizun-mcp.exe`) respondió
  `healthy` con ese hash y cero documentos.
- Fixture desechable `C:\hz-live\HZ_WRITE.rvt` abierto por `horizun_open_document`
  tipado (sin diálogos); activo como `HZ_WRITE`. No se guarda nunca.
- Arnés nuevo: `scripts/live/verify-deliverable-stabilization.ps1` (24 casos
  declarados, incluida la aceptación visual como `not_covered` explícito).

### Resultado en vivo del candidato c1 (`7788b1a7…`, Revit 2026)

Cinco corridas del arnés nuevo; las cuatro primeras corrigieron **errores del
arnés**, no del producto, y se conservan en `artifacts/stabilization-2026-09-08/`:

| Corrida | Resultado | Qué medía mal el arnés |
| --- | --- | --- |
| 1 | 5 ✓ / 4 ✗ / 16 no cubiertas | `rehearsal.rows` (es `rehearsal.actions`); el rechazo de `view_scale` en planos llega como `errors[]` dentro de un dry run válido; `IsValidViewScale(7)` es **verdadero** (Revit acepta 1:7), sonda reemplazada por control de plantilla; `$_.check` inexistente |
| 2 | 18 ✓ / 2 ✗ / 5 no cubiertas | el detalle del preflight viaja en `result` del error, no en `detail`; `move` de una etiqueta lo rechaza el puente (sin muestra de ubicación) |
| 3 | 20 ✓ / 2 ✗ / 3 no cubiertas | `pin` de una etiqueta **no cambia su VersionGuid** (medido): el libro dijo "vigente" y rechazó rearmar una etapa completada, ambas cosas correctas; la mutación de fixture pasó a `delete_verified` |
| 4 | 21 ✓ / 1 ✗ / 3 no cubiertas | los viewports del packing multi-plano llegan en `actions[].data.rows[].element_id` |
| 5 | **23 ✓ / 0 ✗ / 1 no cubierta** (aceptación visual, deliberada) | — |

Artefacto final: `deliverable-stabilization-20260907231222-4c8d327a.json`.
Hechos que quedan probados en vivo con este binario:

- Etiquetado con familia legible en la vista L2 (9948) del fixture: cobertura
  completa (`clearance_scope=complete`, límite `annotation_crop`, obstáculos
  medidos), etiqueta commiteada y releída; una colocación imposible se rechaza
  nombrando "75 obstáculos medidos dentro del annotation_crop", no con una
  frase genérica; `accept_unmeasurable=['*']` se rechaza.
- `view_scale` en `create_floor_plan` (200) y `create_drafting` (20) con
  relectura independiente por parámetro; rechazo por acción en `create_sheet`;
  caso de plantilla.
- `fits_titleblock_cell`: número de 24 caracteres = hallazgo con aritmética;
  número de 10 = sin hallazgo.
- Preflight: cuatro errores independientes en cuatro etapas reportados a la
  vez; el plan válido lleva `undetermined` y las comprobaciones de anfitrión.
- `pdf_print`: campo desconocido rechazado; `Default` verificado contra el
  contorno del plano; ISO_A3 apaisado verificado desde la página (420×297 mm)
  con `color_depth` aplicado pero `requested_unverifiable`; veredicto por
  página en combinado.
- Libro de entrega: apertura, orden por dependencias, escritura completada
  con `idempotency_key` y huella de `VersionGuid`, borrado detectado e
  invalidación en cascada, puerta cerrada rechaza `horizun_export`, aprobación
  anónima rechazada y aprobación nominal ligada al plano y sus viewports,
  puerta abierta exporta, registra `publish` con hashes y se cierra.

Arnés general anterior (`verify-deliverable-production.ps1`) sobre el mismo
binario: **14 ✓ / 0 ✗ / 1 no cubierta** (aceptación visual), incluido
`native-readable-tag`, el bloqueo de salida de la campaña anterior. Artefacto:
`deliverable-production-20260907231435-0de2f1ef.json`.

### Hallazgo de la revisión visual y corrección (candidato c2)

Las páginas se renderizaron con PyMuPDF (`rendered-final/`, `visual-review.md`).
La página del plano con número largo medía **1137,7 × 762 mm**, 70,8 mm más
ancha que su rótulo ARCH E1: con papel `Default`, Revit dimensiona la página al
contorno del plano y el número desbordado agranda el contorno. El verificador
de c1 la dio por `verified` porque coincidía con el contorno. Corregido:

- `PdfPrintPolicy.VerifyPage` recibe además la caja del rótulo; con papel
  `Default`, una página mayor que el rótulo es `page_exceeds_titleblock`, la
  página no queda verificada, el informe lleva `composition` y la exportación
  falla nombrando la causa ("dentro del papel no es dentro del marco").
- Con formato nominal (A3, A1…) no se juzga el rótulo: el contenido se ajusta
  al papel y la página no puede testificar sobre lo que quedó fuera.
- Un test unitario nuevo reprodujo además una fuga de cultura en el mensaje
  ("70,8" con coma decimal): los mensajes usan ahora cultura invariante.
- Arnés: sonda nueva `pdf-print-default-refuses-page-larger-than-titleblock`;
  la publicación del libro pasa a ISO_A3 apaisado para separar ambos hechos.

Core tras la corrección: **3.868/3.868**.

### Candidato c2 (`4310322d…`): la caja del rótulo miente

Misma rutina de sesión aislada (cierre tipado de `HZ_WRITE` con
`discard_unsaved` + `activate_other`, cierre ordenado de Revit, `-Restore`,
`-Enable` en `dev-addin-2026-09-08-c2`, arranque por ejecutable, pid 38596).

- Arnés de estabilización: 23 ✓ / 1 ✗. La sonda nueva falló porque la
  exportación **no** rechazó: la caja (`get_BoundingBox`) del rótulo medía
  1137,5 mm, igual que la página — **la caja de un rótulo incluye sus
  etiquetas**, así que el número desbordado ensanchó la caja tanto como la
  página y ambas coincidieron. Defecto real del verificador de c2, medido.
- Arnés general: 14 ✓ / 0 ✗.
- Corrección: la referencia pasa a ser el papel que el rótulo **declara**
  (`SHEET_WIDTH` / `SHEET_HEIGHT`, instancia y luego tipo; existen en 2023 y
  2027). Test de hechos de código que prohíbe volver a la caja.

### Candidato c3 (`6218d0d0…`, pid 43824)

- Arnés de estabilización: **24 ✓ / 0 ✗ / 1 no cubierta** (visual). La
  exportación con papel `Default` del plano con número largo **falla**
  nombrando "the page is 0.0 x 70.8 mm larger than the titleblock" y admite
  que el archivo puede existir. Artefacto
  `deliverable-stabilization-20260907233133-5d5d7f82.json`.
- Arnés general: **14 ✓ / 0 ✗ / 1 no cubierta**. Artefacto
  `deliverable-production-20260907233252-987d1a58.json`.
- Compilación del árbol final contra 2023, 2024, 2025, 2026 y 2027: cero
  errores y advertencias (bin compartido; 2026 compilado al final).

### Regresión general `verify-live.ps1` sobre c3

- Tier de lectura (`HZ_LIVE_A` activo, `HZ_LIVE_B` abierto sin activar; ambos
  abiertos por `horizun_open_document` tipado, sin diálogos): **79 ✓ / 0 ✗
  funcionales**, 164 no cubiertas por requerir `-WriteProbes`. El único FAIL es
  "the add-in binary matches the release manifest": el manifiesto de release
  nombra la DLL instalada `fe792a0d…` y el candidato es `6218d0d0…`; requisito
  de publicación, no defecto. Artefacto `verify-live/read-tier-c3.json`.
- Tier de escritura, primera pasada (HZ_WRITE reabierto limpio y activo, pero
  `-Document` en su valor de fixture `HZ_LIVE_A`): 47 ✓ / 7 ✗ / 26 sin
  verificar. Los seis fallos funcionales son "target_document HZ_LIVE_A but the
  ACTIVE document is HZ_WRITE" y el tier de escritura quedó no cubierto porque
  la sección de worksets cerrados reactiva `Document` antes de llegar a él.
  Error de puesta en escena del operador (ya conocido en memoria), no del
  producto. Artefacto `verify-live/write-tier-c3.json`.
- Segunda pasada con `-Document HZ_WRITE -WriteProbes -WriteDocument HZ_WRITE`
  (HZ_WRITE reabierto limpio y activo): **238 ✓ / 2 ✗ / 1 sin verificar /
  3 no cubiertas, de 241** (todas asertan). Artefacto
  `verify-live/write-tier-c3-pass2.json`. Los dos fallos:
  1. "the add-in binary matches the release manifest": requisito de
     publicación (build de desarrollo), esperado.
  2. "auto-tag planning feeds an explicit-type tag through the verified
     writer": el fixture no tiene ningún tipo de etiqueta multicategoría y el
     arnés se autoprovisiona una familia desde la plantilla **vacía**
     `Metric Multi-Category Tag.rft`. Reproducido a mano sobre c3 con los
     mismos IDs (vista 1545683, elemento 1545708, tipo 1547800): el
     planificador entrega cobertura completa y el ensayo de `horizun_annotate`
     rechaza con `constructible=false`, razón "Unreadable annotation extent:
     1556362" — la etiqueta provisional **propia** no tiene extensión porque la
     familia no dibuja nada. La negativa es correcta (una etiqueta invisible no
     se puede colocar ni verificar) y coincide con el hallazgo del 07-09; lo
     que estaba mal era el **texto**, que nombraba un id interno en vez de la
     familia. El etiquetado con familia legible queda probado por el arnés de
     estabilización.
  El "sin verificar" ("a tag rule names the exact visible element left
  untagged") depende de la misma etiqueta y hereda la limitación del fixture.
  Las 3 no cubiertas son el requisito de commit limpio y sus derivadas.

### Candidato c4: solo el mensaje de la etiqueta sin extensión

`AnnotationLayout.OwnBox` distingue la extensión de la etiqueta **que se está
colocando** de la de un obstáculo: cuando no se lee, la negativa dice "The tag
itself has no readable extent in view N (… tag type X, tag text '…'): a tag
that renders nothing cannot be laid out or verified … use a tag type whose
family carries a visible label". Se usa en `Place`, `Evidence` y en la
verificación post-commit. La sonda del arnés general pasa a registrar
`unverified` —no `pass`— cuando la familia es la autoprovisionada vacía y la
negativa nombra la extensión: la garantía que la sonda enuncia (etiqueta
commiteada) no se prueba en ese fixture, y un rechazo correcto no la sustituye.
Core tras el cambio: 3.869/3.869.

Candidato c4 en vivo (`d6479e31…`, pid 52716, misma rutina de sesión aislada):

- Arnés de estabilización: **24 ✓ / 0 ✗ / 1 no cubierta** (visual). Artefacto
  `deliverable-stabilization-20260907235522-dd09ceb0.json`.
- Regresión general en una pasada, primera corrida: 236 ✓ / 2 ✗ / 3 sin
  verificar / 3 no cubiertas. La sonda de autoetiquetado pasa a `unverified`
  con el mensaje nuevo ("The tag itself has no readable extent in view
  1545773 (get_BoundingBox returned null; tag type HZ_PLM_TAG (1547890), tag
  text ''): … use a tag type whose family carries a visible label"). El fallo
  nuevo — "restore the original active document after closed-workset probes"
  y su `unverified` gemelo — es de escenificación: HZ_WRITE **no se reabrió
  limpio** entre el arnés del bloque y la regresión, y el puente se negó
  correctamente a cerrar un documento con cambios sin `discard_unsaved`
  ("REFUSING TO CLOSE: 'HZ_WRITE' HAS UNSAVED CHANGES"). En c3 la misma sonda
  pasó con HZ_WRITE recién abierto. Se repite la pasada con HZ_WRITE limpio.
- Regresión general en una pasada, segunda corrida (HZ_WRITE cerrado con
  `discard_unsaved` y reabierto limpio, activo; HZ_LIVE_B abierto): **238 ✓ /
  0 ✗ funcionales / 2 sin verificar / 3 no cubiertas, de 241**; el único FAIL
  es el manifiesto de release. Los dos "sin verificar" son la familia de
  etiqueta vacía del fixture (autoetiquetado y regla `requires_tag`); las 3
  no cubiertas, el requisito de commit limpio. Artefacto
  `verify-live/one-pass-c4-clean.json`. **c4 es el candidato completamente
  regresado**; c3 difiere de él solo en el texto de esa negativa (verificado
  por `git diff` de los dos archivos tocados: `AnnotationLayout.cs` y una línea
  de `AnnotateCommand.cs`).

## Cierre de sesión (2026-09-08, ~00:30)

Rutina ejecutada con `restore-stable.sh` (registro en
`artifacts/stabilization-2026-09-08/restore-stable.log`):

1. `HZ_WRITE` y `HZ_LIVE_B` cerrados por `horizun_document_session` con
   `discard_unsaved=true` y token de ensayo; ningún fixture se guardó nunca
   (`HZ_LIVE_A` ya se había cerrado sin diálogo, sin cambios).
2. Revit 2026 (pid 52716, solo el documento ancla del puente) cerrado con
   `CloseMainWindow`, sin diálogos.
3. `dev-addin-session.ps1 -Year 2026 -Restore`: manifiesto de desarrollo
   eliminado y `Horizun.addin` original restaurado (ni `-dev-session.addin` ni
   `-aside` quedan en `%APPDATA%\Autodesk\Revit\Addins\2026`).
4. Verificación en disco: `Horizun\Horizun.Revit.dll` instalada = SHA-256
   `fe792a0d8fcd2e7b095991059e5fbdf39e0f05542595aff5ee78f2e3cf9da214`
   (idéntica al arranque de la sesión, escrita 2026-09-07 21:03:08Z),
   **Authenticode: NotSigned**; servidor instalado
   `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe` intacto
   (`dbeebb54…`), nunca sustituido.
5. Revit 2026 arrancado por su ejecutable (pid 53708) y **detenido en el
   diálogo "Security – Unsigned Add-In" de esa DLL estable** ("Unknown
   Publisher", Issuer None). Esa decisión es de la persona: no se pulsó
   `Always Load` ni `Load Once`. Por eso `horizun_health` **no pudo
   reconfirmar** la instalación estable en vivo; la restauración está probada
   por los hechos en disco de los puntos 3 y 4. Al responder el diálogo,
   `horizun_health` debe mostrar commit limpio `5498141`, versión 1.2.1 y esa
   DLL, con cero documentos.
   **Confirmado tras la aceptación del usuario** (a través del servidor
   instalado): `healthy`, versión 1.2.1, commit `5498141c46a6…`,
   `built_from_clean_tree=true`, DLL `fe792a0d…`, Revit 2026 pid 53708, cero
   documentos, 80 herramientas. Evidencia `restored-health.json`.
6. Revit 2025 del usuario (pid 37388 al inicio) no recibió ninguna llamada;
   hacia las 23:45 dejó de aparecer en la lista de procesos sin intervención
   de esta sesión (todos los cierres fueron por pid con comprobación de ruta
   `Revit 2026`).

Sesiones temporales que quedan en disco, deliberadamente (evidencia por
candidato): `%USERPROFILE%\.horizun\dev-addin-2026-09-08-c1..c4`. Los libros de
entrega del arnés: `%USERPROFILE%\.horizun\deliveries\stab-*.jsonl`. Ninguno
está registrado en Revit ni en ningún cliente MCP.

## Fase 2 (2026-09-08, desde ~00:45): §8B, casos B, libro y matriz

Instrucción: cerrar §8B con argumentos explícitos, crear fixtures en copias
nuevas, cerrar todos los casos B, revisar persistencia/recuperación del libro,
correr la matriz de años en sesiones aisladas, congelar y repetir los arneses
sobre el mismo candidato, y actualizar el informe sin sobrevender.

### §8B — hechos de API (metadatos de los RevitAPI.dll instalados)

- `Dimension` y `DimensionSegment`: `TextPosition` (get/set),
  `IsTextPositionAdjustable()`, `ResetTextPosition()`, `LeaderEndPosition`
  (get/set), `Dimension.HasLeader` (get/set) — idénticos en 2023–2027.
- `IndependentTag`: `TagHeadPosition`, `HasLeader`, `LeaderEndCondition`
  (Attached/Free), `CanLeaderEndConditionBeAssigned`, `GetTaggedReferences`,
  `Get/SetLeaderEnd(Reference, XYZ)`, `Get/SetLeaderElbow`,
  `IsLeaderVisible/SetIsLeaderVisible` — idénticos en 2023–2027 (2027 añade
  `LeaderStartCondition`, no usado).
- **Creación de etiquetas (labels) en una familia de anotación: no existe en
  la API.** `FamilyItemFactory` ofrece `NewModelText` (texto 3D) y nada más;
  `LabelUtils`/`LabelType` son otra cosa. Y en esta máquina no hay biblioteca
  de anotaciones instalada (solo prefabricados en francés). Consecuencia: una
  familia multicategoría con etiqueta visible la aporta una persona o un RFA
  de biblioteca; no se puede autoprovisionar.

### §8B — diseño

- `horizun_edit_dimensions`: `text_position` (absoluta, unidades) o
  `text_offset` (`[dx,dy]` sobre los ejes de la vista, `distance_space`
  model|paper), `leader` (`HasLeader`), `leader_end`; por segmento
  `segments[].text_position/text_offset/reset_text_position/leader_end`.
  `IsTextPositionAdjustable()=false` rechaza; `text_position`+`text_offset`
  juntos rechaza; `leader_end` sin líder rechaza; relectura con tolerancia
  `1e-5 ft` (0,003 mm) reportada `requested/read/match`.
- `horizun_transform_elements`: `move_tag_head` (`point` para una etiqueta o
  `vector` para varias) y `set_tag_leader` (`has_leader`,
  `leader_end_condition`, `leader_end`, `leader_elbow`, `leader_visible`).
  Rechaza por nombre: condición no asignable, extremo libre sobre líder
  adjunto, edición de líder sin líder, etiqueta anclada, más de una referencia
  etiquetada (ambigua), y etiquetas de habitación/espacio/área (límite
  documentado). Relectura de cada propiedad.
- Pruebas: `DimensionEditRulesTests` (+1, elegibilidad por segmentos y
  tolerancia), hechos de código para ambos comandos. Contrato e inventario:
  **204 operaciones / 799 valores** (+2 operaciones, +2 `leader_end_condition`,
  +2 `distance_space`).

### Libro de entrega — revisión

- `Resume` añade `in_doubt`: una etapa `in_progress` sin registro terminal
  (proceso muerto) no se ofrece de nuevo ni se da por hecha; solo un
  `completed` explícito (con IDs que el anfitrión relee) o `failed` la
  resuelve. Tests: recuperación tras caída, replay del evento parcial,
  aprobación invalidada cierra una puerta abierta y solo una aprobación nueva
  la reabre (con la historia de ambas decisiones).
- Arnés: sondas nuevas `ledger-approval-invalidated-by-scope-change` (se
  borra el viewport empaquetado del plano aprobado con la puerta abierta),
  `ledger-open-twice-refused`, `ledger-corrupt-line-reported` (línea basura
  anexada al archivo) y `ledger-previous-run-recovery` (el libro de la
  corrida anterior se reanuda contra el fixture recién abierto).

### Casos B — cobertura en el arnés

Autoprovisionados en cada corrida para que valgan en cualquier año: vista
dependiente (`duplicate_view AsDependent`, tipado), categoría oculta en la
dependiente y categorías de anotación apagadas (escenificación por
`horizun_execute_python`, evidencia autorreportada; la aserción la hace la
cobertura del planificador, verificada por el anfitrión). Además `pdf_print`
con `placement=margins`+offsets y `export_in_background` (2025+),
`fits_titleblock_cell` sobre `name`, y `export_in_background` rechazado en
2023/2024, en la sección de impresión.

Core: **3.874/3.874**; Server: 477/477; cinco años compilan sin advertencias.

### Congelación y candidatos de la fase 2

- Commit local `20a2108` (código + arnés + docs de la fase 2) y, encima,
  `c6fea8c`: el conductor de la matriz aprende el token `{addin_sha256}`
  (hash de la copia firmada que habilita por año) para que los arneses que
  exigen `-ExpectedAddinSha256` refusen un binario equivocado sin que el
  operador conozca el hash antes de compilar. Ningún archivo compilado en el
  add-in cambia entre ambos commits.
- Sesión c5 (`20a2108`, DLL `dde07983…`) arrancó sana y se descartó a
  propósito para que **todos** los años midan binarios estampados con el
  mismo commit: sesión **c6** = `c6fea8c` limpio, DLL 2026
  `b9e777c9f5099a3cf38a6365617c2af8f0b708fc773f7cef49805cf2ceeb9458`,
  pid 58804, `HZ_WRITE` abierto y activo.
- Corrida de 2026 (`c6-2026/`): arnés de estabilización dos veces (la segunda
  reanuda el libro que dejó la primera contra el fixture recién abierto),
  `save_as` del modelo escenificado a `C:\hz-live\HZ_WRITE2.rvt` (copia nueva,
  nunca sobre `HZ_WRITE`), arnés general y `verify-live` en una pasada.
- Matriz (`year-matrix/`): `run-year-matrix.ps1` para 2023, 2024, 2025 y
  2027 con `HZ23/24/25/27_BASE.rvt` como fixture por año; el conductor se
  niega si un Revit de ese año ya está en ejecución (no cierra sesiones
  ajenas) y restaura el manifiesto en `finally`.

### Corrida c6 en 2026 y dos hechos medidos más

Sobre c6 (`c6fea8c`, DLL `b9e777c9…`): arnés general **14/14**, `verify-live`
en una pasada **238/241** (mismo perfil que c4), y el arnés de estabilización
28–29 ✓ / 6 ✗ en dos corridas: cinco de los seis fallos eran del propio arnés
(categoría no ocultable elegida al azar, cota de prueba no encontrada por
consulta, variable no definida, id de sonda reutilizado en el manejador de
error, y la cadena de recuperación del libro que no rearmaba `pack` tras la
cascada — la cascada era correcta). La sonda `ledger-previous-run-recovery`
**pasó** en la segunda corrida: el libro de la primera se reanudó contra el
fixture recién abierto y sus huellas quedaron invalidadas con razón. La copia
`C:\hz-live\HZ_WRITE2.rvt` (21,6 MB) se guardó por `save_as` tipado desde el
modelo escenificado y quedó declarada en `live-fixtures.json`.

Los otros dos fallos eran del producto y se corrigieron:

- `placement=margins` se releía como `lower_left`. Sonda por
  `horizun_execute_python` sobre un `PDFExportOptions` sin escribir nada:
  `PaperPlacementType.Margins` y `LowerLeft` son el **mismo valor de
  enumeración (1)**; `Margins` relee `LowerLeft`. La política ofrece ahora
  `center|lower_left` y los offsets aplican a `lower_left`; además, cualquier
  opción no verificable cuyo valor releído difiera del asignado pasa a
  `applied_mismatch` y **falla la exportación** (una opción reescrita en
  silencio es justo lo que la política existe para rechazar).
- `export_in_background=true`: Revit devolvió `accepted=true` y **ningún
  archivo existía** al volver la llamada. El puente solo reporta archivos que
  releyó, así que `true` se rechaza por nombre en todos los años (en 2023/2024
  la opción ni existe). Inventario: 204 operaciones / 798 valores.

Core 3.875/3.875, Server 477/477, cinco años compilan.

### Incidente al cambiar a c7 (09:12–09:23)

La cadena de cambio de sesión no cerró los documentos (su bucle de cierre
salió sin registrar nada), envió `CloseMainWindow` al Revit 58804 con
`HZ_WRITE` modificado —que abrió el diálogo **Save File** y no salió—, y al
fallar `-Restore`/`-Enable` (Revit en ejecución) **arrancó igualmente un
segundo Revit 2026** (65060). Mientras yo comprobaba el estado, el usuario
empezó a usar esa segunda instancia (`005_TEC_FED_KLB_PowerBIM_CURSO.rvt` en
su título). La cerré de forma ordenada creyéndola vacía; salió sin diálogo de
guardado (sin cambios pendientes), pero fue un cierre de una sesión del
usuario y **no debió ocurrir**. Un clic sobre "No" del diálogo de `HZ_WRITE`
no llegó al diálogo (otra ventana estaba delante). Detenida toda acción de
interfaz: el 58804 queda con su diálogo para que lo responda la persona; el
manifiesto de desarrollo c6 sigue puesto (`-Restore` no pudo correr).

Lección para el conductor: comprobar la lista de documentos JUSTO antes de
cerrar, no arrancar nunca un Revit si `-Enable` falló, y no enviar clics a un
diálogo que no está en primer plano.

### c7 en Revit 2026 (09:25–10:20): 72fe932, DLL `889533d8…`, servidor `5931ef60…`

Recuperación: el diálogo **Save File** de `HZ_WRITE` en el Revit 58804 se
respondió "No" por su handle (`BM_CLICK` al `CommandButton_7` de un `#32770`
hijo de la ventana principal) tras leer que el texto nombraba `HZ_WRITE.rvt`;
sin mouse, sin foco, sin tocar nada del usuario. Revit salió; `-Restore` del
manifiesto c6; build 2026 desde `72fe932` (estampado `-dirty` solo por esta
bitácora sin commitear); `-Enable` en `dev-addin-2026-09-08-c7`; Revit
arrancado por su exe; `horizun_health` por el servidor de desarrollo:
`healthy`, 1.2.1, `72fe932…-dirty`, DLL `889533d8…`, pid 54364, 0 documentos.

Resultados sobre c7 (`artifacts/stabilization-2026-09-08/c7-2026/`):

- Arnés general de entregables: **14 aprobadas, 0 fallidas, 1 no cubierta**
  (aceptación visual).
- Arnés de estabilización, dos corridas: **17 aprobadas, 4 fallidas, 25 no
  cubiertas** en ambas. Las cuatro caídas, y lo que revelan:
  1. `8b-dimension-section-error`: el puente rechazó `distance_space`
     ("unsupported action field") en `horizun_edit_dimensions`, aunque el
     contrato lo publica junto a `text_offset`. **Defecto de producto**: la
     clasificación de campos no conocía el calificador. Corregido con la clase
     `Modifier` (`distance_space` solo se admite junto a un `text_offset`, del
     elemento o de un segmento; solo, se rechaza por nombre).
  2. `campaign-interrupted` en `pdf-print-margins`: la exportación con
     `placement=lower_left` y offsets 10/20 mm murió con "The produced PDF
     contradicts the print policy: " **y nada tras los dos puntos**. Dos
     defectos de producto: (a) el mensaje solo listaba `verified_mismatch` y
     páginas desbordadas, no los `applied_mismatch` que también cierran la
     puerta; (b) la comparación de offsets formateaba el token releído con la
     cultura del anfitrión (coma decimal) y lo parseaba en invariante ⇒ todo
     offset era `applied_mismatch`. Reproducido sin Revit con un test bajo
     `es-CO` (`OffsetsReadBackAsNumbersMatchUnderACommaDecimalCulture`);
     corregido comparando el número, no su texto, y nombrando en el mensaje
     cada opción reescrita con su estado y ambos valores.
  3. `8b-tag-section-error`: `$host` es una variable automática de solo
     lectura de PowerShell. Error del arnés; renombrada.
  4. `coverage-b-section-error`: "no measured obstacle has a category this
     view can hide". Una vista dependiente no tiene V/G propia: `CanCategoryBeHidden`
     responde false para toda categoría en ella. Error de escenificación del
     arnés: ahora oculta la categoría (y apaga las categorías de anotación) en
     la vista **primaria**, la dependiente hereda, y un `finally` restaura la
     visibilidad de la primaria antes de que la sección 8B mida en esa vista.
  Las 25 no cubiertas son las etapas posteriores a la interrupción (offsets,
  fondo por año, `cell-fit-name-field`, papel Default desbordado y todo el
  libro de entregas): no se ejecutaron, no se cuentan como aprobadas.
- `save_as` de la copia desechable `HZ_WRITE2.rvt` (21,6 MB) repetido con
  éxito sobre la copia propia; los originales de `C:\hz-live` no se tocaron.

Conclusión: c7 no es el candidato. Los dos defectos de producto y los dos del
arnés se corrigen en el árbol y se congela **c8** para repetir todo (2026 y
matriz) sobre ese binario.

### c8 en Revit 2026 (10:00–): `fa1e7a0` limpio, DLL `c0f1024b…`, servidor `e68c2344…`

Cambio de sesión sin incidentes: los documentos de c7 se cerraron descartando
por comando tipado (el vínculo `w12-linkcopy2-…` de verify-live no es un
documento cerrable por título y Revit no preguntó por él), el diálogo Save File
de `HZ_WRITE` se respondió "No" por handle, `-Restore`, build desde el árbol
limpio `fa1e7a0`, `-Enable` en `dev-addin-2026-09-08-c8`, Revit por su exe,
health `healthy` / 1.2.1 / `fa1e7a0` / `built_from_clean_tree=true` / DLL
`c0f1024b…` / pid 64300 / 0 documentos.

Primera pasada del arnés de estabilización sobre c8: **5 aprobadas, 2
fallidas, 37 no cubiertas** en ambas corridas. La escenificación de "categoría
oculta", ya movida a la vista primaria, siguió sin encontrar categoría
ocultable; el script ahora lista las rechazadas y la plantilla: la L2 (9948)
del fixture lleva la plantilla **"Mechanical Plan"** (183719), que gobierna
V/G, y en una vista así `CanCategoryBeHidden` es false para toda categoría.
Segundo hallazgo del mismo tipo: el `finally` de restauración leía una
variable no inicializada bajo modo estricto y tumbó la campaña (37 sin
cubrir). Ambos son del arnés, no del producto; el binario c8 no cambia.
Escenificación definitiva: duplicado `WithDetailing` de la vista de la
etiqueta (las anotaciones viajan con él), plantilla desprendida en ESA copia
desechable, dependiente creada a partir de ella; nada del fixture se toca y
no hace falta restaurar. Hecho de API registrado en memoria.

### c8b en Revit 2026 (10:25–10:56): mismo binario c8, arnés `d713590`

- Arnés de estabilización con la escenificación en la primaria desechable:
  **run 1: 37 aprobadas, 2 fallidas, 4 no cubiertas; run 2: 38 aprobadas, 2
  fallidas, 3 no cubiertas** (la recuperación entre corridas,
  `ledger-previous-run-recovery`, pasó en run 2). Cobertura de vista
  dependiente, categoría oculta y categorías de anotación apagadas: **PASS en
  vivo** (dejan de ser B). Las dos caídas:
  1. `8b-dimension-section-error`: `[double]$x.before_feet[0]*304.8+200`
     dentro de `@(...)` termina sumando sobre un `Object[]` en PowerShell;
     error del arnés (los dos casos anteriores de la sección, `text_offset` en
     papel y `reset_text_position`, pasaron). Corregido convirtiendo el vector
     antes de operar.
  2. `ledger-open-twice-refused`: al reabrir el mismo `delivery_id` tras haber
     borrado un elemento registrado, la respuesta fue el **preflight** (1
     error) y no "already exists; use delivery_status to resume it". El orden
     de comprobaciones escondía el camino de reanudación justo en el caso en
     que más hace falta (el operador repite el perfil tras una interrupción y
     el modelo ya cambió). **Defecto de producto de recuperación**: c9 comprueba
     la identidad del libro antes del preflight (hecho fijado por un test de
     fuente).
- Arnés general: 14/0/1 ya medido en c8.
- verify-live una pasada (árbol limpio, HZ_WRITE activo comprobado por health
  antes de arrancar): **238 aprobadas, 1 fallida (binario ≠ manifiesto de
  release), 2 sin verificar (autoetiquetado con familia vacía), 3 no cubiertas**
  — la sonda "commit esperado desde árbol limpio" no cubre porque HEAD ya era
  `d713590` (commit del arnés) y el binario cargado es `fa1e7a0`; se cierra en
  c9. La pasada anterior de verify-live sobre c8 (219/5/17/3) queda como
  corrida inválida: el documento activo pasó a `HZ_LIVE_B` entre dos sondas sin
  ninguna llamada del puente que cambie de documento (recibos 15:08:31→15:08:43Z),
  con la máquina en uso; se conserva en `c8-2026/verify-live/one-pass-c8.log`.
- **Revisión visual** (`artifacts/…/visual-review.md`, PDF renderizados en
  `c8-2026-stab/rendered/`): `margins.pdf` (lower_left + offsets 10/20 mm,
  fit_to_page) sale **recortado** 20 mm arriba y 6 mm a la derecha — Revit
  ajusta la lámina al papel completo y luego la desplaza — mientras
  `a3-landscape.pdf` (centrado) sale íntegro. Los estados del puente eran
  honestos (`requested_unverifiable`) y el PDF salió mutilado igual. **Defecto
  de diseño**: en c9 los offsets exigen `zoom='zoom'` con porcentaje explícito
  y con `fit_to_page` se rechazan por nombre citando la medida; el arnés
  ejercita ambos (rechazo y export con zoom 35 %). Hecho registrado en memoria.

### Reinicio de la máquina (≈14:22)

La máquina se reinició (uptime 3 min al reanudar); el Revit 2026 de la sesión
c8 (pid 64300) desapareció con él y el manifiesto de desarrollo c8 quedó
puesto. Restaurado con `-Restore` antes de cualquier otra cosa; fixtures de
`C:\hz-live` intactos (solo lectura, tamaños idénticos). Ninguna sesión del
usuario estaba abierta al reanudar.

### c9: congelado (`delivery_open` identidad-antes-de-preflight; offsets solo con zoom)

Core **3.880/3.880**; cinco años compilan sin advertencias; inventario,
consistencia pública y escaneo sensible en verde. Contrato: solo cambian las
descripciones de `origin_offset_x/y` (sin operaciones ni valores nuevos).

### c9 en Revit 2026 (14:32–14:57): `76fc894` limpio, DLL `1b9f5703…`, servidor `09092696…`

Sesión aislada `dev-addin-2026-09-08-c9`, Revit 2026 pid 15848 (health:
`healthy`, 1.2.1, `76fc894`, `built_from_clean_tree=true`). Convivió con un
**Revit 2023 del usuario** abierto tras el reinicio ("Auditoria del modelo
electrico - IEC", pid 32628): todas las llamadas de esta sesión llevaron
`HORIZUN_REVIT_YEAR=2026` y ese Revit no recibió ninguna.

- Estabilización run 1: **40 aprobadas, 1 fallida, 2 no cubiertas**; run 2
  (reanuda el libro de run 1): **41 aprobadas, 1 fallida, 1 no cubierta**. La
  fallida, `dim-text-position-and-offset-refused`, es del arnés: el ensayo
  devuelve éxito con la fila rechazada en `errors[]` ("text_position and
  text_offset are two answers to one question") —como manda el contrato— y
  la sonda solo miraba el estado de la llamada. Parche del arnés preparado y
  aplicado después de la matriz (run 3, abajo). Todo lo demás pasó, incluido
  `ledger-open-twice-refused` con la identidad antes que el preflight,
  `pdf-print-offsets-need-zoom` y `pdf-print-margins-offsets-applied` con zoom
  35 %.
- Arnés general: **14/0/1**.
- verify-live una pasada (HZ_WRITE activo por health): **238 aprobadas, 1
  fallida (manifiesto de release), 2 sin verificar, 3 no cubiertas**. La sonda
  "commit esperado desde árbol limpio" exige `-ExpectedCommit` (no la pasé en
  ninguna corrida; se repite al final con `-ExpectedCommit` y
  `-ExpectedServerSha256`).
- Render y revisión visual: `margins.pdf` con zoom 35 % sale íntegro (margen
  izquierdo ≈10 mm, inferior ≈20 mm); `default.pdf` íntegro.

### Matriz de años en vivo (14:59–15:15): `run-year-matrix.ps1` sobre `76fc894`

Primer lanzamiento desde bash: los parámetros de tipo `string[]` llegaron
como argumentos sueltos y el conductor se negó antes de tocar nada
("A positional parameter cannot be found"); relanzado desde pwsh con arrays.

| Año | Estado | Revit | DLL firmada cargada | Estabilización | General |
| --- | --- | --- | --- | --- | --- |
| 2023 | **bloqueado**: "Revit 2023 is already running (pid 32628). This driver will not close a session it did not start." | sesión del usuario | — | — | — |
| 2024 | medido | 24.3.40.26, pid 39736, `HZ24_BASE` | `75865bf2…` | 40 / 1 / 2 no cubiertas | 14 / 0 / 1 |
| 2025 | medido | 25.4.41.14, pid 45640, `HZ25_BASE` | `3836f4b8…` | 40 / 1 / 2 no cubiertas | 14 / 0 / 1 |
| 2027 | medido | 27.0.10.13, pid 48624, `HZ27_BASE` | `0ed4d966…` | 40 / 1 / 2 no cubiertas | 14 / 0 / 1 |

La única fallida en los tres años es la misma sonda del arnés que en 2026.
Cada corrida registra `code_candidate_commit=76fc894`, `built_from_clean_tree=true`,
`repo_tracked_clean=true`, servidor `09092696…` (`server_matches_tree=true`);
el resumen del conductor (`year-matrix-20260908151525.json`) dice en cambio
`repo_tracked_clean=false` en su cabecera, contradicho por los seis registros
de arnés tomados minutos después y por `git status` limpio antes y después:
se anota como discrepancia del conductor, no del candidato. Binarios
conservados por año en `year-matrix/<año>/binaries/`. Manifiestos de 2024,
2025 y 2027 restaurados por el `finally` del conductor (comprobado en disco);
al terminar solo quedaba en ejecución mi Revit 2026 (el 2023 del usuario ya
estaba cerrado por él).

### c9, run 3 de estabilización (15:20): arnés con la sonda corregida

`HZ_WRITE` reabierto limpio; la sonda `dim-text-position-and-offset-refused`
juzga ahora la fila `errors[]` del ensayo. **41 aprobadas, 0 fallidas, 2 no
cubiertas** (aceptación visual, deliberada; y la recuperación entre corridas,
porque el directorio de artefactos era nuevo y no había libro anterior que
reanudar — run 2 ya la había aprobado). Con run 2 + run 3, cada caso
declarado del arnés salvo el visual tiene un PASS en vivo sobre c9. Parche
del arnés commiteado después de la matriz (solo arnés; el binario c9 no
cambia).

### c9, verify-live con identidad exigida (15:23–15:31)

Misma sesión c9, `HZ_WRITE` reabierto limpio y activo por health, con
`-ExpectedCommit 76fc894…`, `-ExpectedAddinSha256 1b9f5703…` y
`-ExpectedServerSha256 09092696…`: **240 aprobadas, 1 fallida (binario ≠
manifiesto de release), 2 sin verificar (autoetiquetado con familia vacía), 1
no cubierta (el artefacto instalable)**, de 241. La sonda "commit esperado
desde árbol limpio" y la del servidor del manifiesto pasan a aprobadas.

### c10: cinta localizada y "Opciones avanzadas" (petición del usuario, 15:15)

Petición: los cuatro botones del panel BIM Production (Modo BIM, Historial de
acciones, Pausar MCP, Protección central) bajo un solo botón **Opciones
avanzadas** que abra un menú tipo UI Horizun donde se puedan pulsar, y todos
los botones en español o inglés según el idioma de Revit.

Hecho: `RibbonText.cs` (nuevo) concentra todo el texto de la cinta y decide
ES/EN por `LanguageType` del propio Revit (nunca por Windows ni por la
cultura); `AdvancedOptionsWindow` (WPF en código, sin XAML, propiedad de la
ventana principal de Revit) muestra cuatro filas con título, explicación y
**estado actual** (modo, pausa, protección) leído del mismo settings que usa
el dispatcher, y al pulsar una fila ejecuta exactamente el comando que tenía
el botón antiguo. `Ribbon.cs`: un botón **Opciones avanzadas / Advanced
options** en el panel *Producción BIM / BIM Production*; Estado del puente /
Bridge status, Horizun Hub y Python ON/OFF con tooltips y descripciones en los
dos idiomas; los diálogos de estado, modo BIM, historial, pausa y protección
localizados. Sin cambios en el contrato ni en el dispatcher (el servidor no
cambia: `09092696…`). Test de fuente
`Ribbon_speaks_the_language_of_the_host_and_keeps_the_owner_controls_behind_one_menu`;
`security-model.md` y `AGENTS.md` actualizados. Compila en los cinco años sin
advertencias (net48 y net8/net10 comparten la ventana en código).

### c10 en Revit 2026 (15:38–15:50): `884f0ef` limpio, DLL `032d6f81…`, servidor `c94a7eb6…`

Sesión aislada `dev-addin-2026-09-08-c10`, Revit 2026 pid 50068 (inglés,
`English_USA`), health `healthy` / `884f0ef` / `built_from_clean_tree=true`,
`HZ_WRITE` abierto. Verificación de la cinta sin arneses (no hay contrato que
medir): el registro del add-in no tiene ninguna advertencia de cinta; la
pestaña **Horizun Hub** muestra el panel *Horizun RVT MCP* (Bridge status,
Python ON/OFF, Horizun Hub) y el panel *BIM Production* con el único botón
**Advanced options** (`c10-revit-horizun-tab.png`, capturado con
`PrintWindow`). La ventana `AdvancedOptionsWindow` se instanció desde la DLL
c10 fuera de Revit (pwsh -STA, sin activar, en el monitor izquierdo vacío) en
los dos idiomas: cabecera, cuatro tarjetas con estado actual (modo
`unsafe_code`, MCP activo, protección inactiva) y Cerrar/Close
(`c10-advanced-options-es.png`, `c10-advanced-options-en.png`). No se pulsó
ninguna fila: cada una ejecuta el comando antiguo, que no cambió.

**Incidente menor:** para ver el panel hubo que ensanchar mi ventana de Revit
(la pestaña estaba en el desbordamiento «»») y seleccionar la pestaña por UI
Automation; el `Invoke` de la pestaña **trajo mi Revit al primer plano** sobre
la ventana en la que trabajaba el usuario (foreground pid 11880 → 50068). La
ventana se devolvió al fondo del orden Z y a su tamaño original, pero el foco
del teclado quedó en Revit hasta que la persona vuelva a su ventana. No se
repetirá: la verificación de cinta se hará solo con `PrintWindow` y la
ventana se renderiza fuera de Revit.

### c11: los textos de la cinta en lenguaje llano (petición del usuario, 16:05)

Petición: "esos botones no se entienden, es muy técnico; explícalo bien".
Reescritos todos los textos que ve la persona, en los dos idiomas, en
términos de **qué hace el asistente con tu modelo**, sin "puente", "MCP",
"read_only", "recibos", "workshared" ni nombres de herramientas:

- Botones: *Estado de conexión / Connection status* ("¿el asistente está
  conectado a este Revit?"), *Permitir scripts / Allow scripts* (qué son los
  scripts de Python y por qué no se comprueban), *Horizun Hub* (la web con los
  flujos), *Opciones avanzadas / Advanced options* (decide qué puede hacer el
  asistente).
- Menú: *¿Qué puede hacer el asistente?* (solo consultar / modificar el modelo
  abierto / modificar, exportar y manejar documentos; el perfil técnico se
  traduce con `RibbonText.ModeName`, nunca llega crudo), *¿Qué ha hecho el
  asistente?*, *Pausar el asistente*, *Proteger los modelos compartidos*; cada
  fila con "Ahora: …".
- Diálogos de estado, nivel de permisos, historial, pausa y protección
  reescritos igual; el consentimiento de scripts de Python conserva su texto
  (ya era explícito y lo fijan pruebas).
- Renders desde la DLL compilada: `artifacts/…/c11/c10-advanced-options-es.png`
  y `-en.png`. Core 3.881/3.881; cinco años sin advertencias; puertas en verde.
  No se volvió a arrancar Revit para esta pasada (la anterior ya tomó el foco
  del usuario); la cinta se verificó en c10 y aquí solo cambian textos.

## Fase 3 (2026-09-08 16:14 → 2026-09-09 ~21:00): seguridad del conductor, fixture de etiquetas, candidato final, matriz y restauración

Encargo: cerrar los pendientes de seguridad y validación del candidato local,
interfaz incluida, sin preparar publicación (sin push, sin release, sin
`install.ps1`, sin tocar la instalación estable ni los clientes MCP, sin
cerrar ni guardar sesiones ajenas). Orden pedido: primero el conductor, luego
los fixtures y la validación.

### 1. Seguridad del conductor (`fefadf8`)

`scripts/live/run-year-matrix.ps1` cerraba con `Stop-Process -Force` "lo que había
arrancado". Reescrito sobre `scripts/live/year-matrix.session.ps1` (reglas con
sondas inyectables) y probado sin Revit por
`scripts/live/year-matrix.session.tests.ps1` (respuestas enlatadas y procesos
auxiliares minimizados: fallo al habilitar, fallo al restaurar, restauración
incompleta o con Revit abierto, DLL instalada cambiada, proceso terminado,
identidad distinta por hora de inicio y por ejecutable, documento ajeno,
health sin respuesta, health de otro pid, cierre rechazado, cierre normal).
Reglas: identidad = pid + hora de inicio + ejecutable, recomprobada justo
antes de cerrar; documentos abiertos leídos por el puente y comparados con el
libro de lo que abrió el ensayo (más `HZ_ANCHOR*` y los patrones de fixture
declarados); ante documento ajeno, identidad dudosa, health sin respuesta o
cierre rechazado se deja el Revit corriendo y se escribe
`recovery-pending-<año>.json` con los pasos; solo se cierran los fixtures
propios por el cierre tipado con ensayo y token, descartando solo cambios de
ensayo; salida normal por la ventana, nunca terminación; sin foco, sin clics;
los diálogos de arranque solo por lista blanca y nunca uno de
guardar/descartar/seguridad; `-Enable` fallido detiene el año antes de
arrancar Revit; `-Restore` se cree solo con código de salida cero y el disco
(manifiesto instalado presente, de desarrollo y aparte ausentes, DLL instalada
igual que al inicio), y nunca con un Revit de ese año abierto; ningún archivo
de descubrimiento ajeno se borra.

### 2. Fixture de etiquetas (`HZ_WRITE3.rvt`, nuevo, desechable)

No hay ninguna familia de etiqueta multicategoría en las bibliotecas locales
(las carpetas de biblioteca de Revit están vacías salvo tres). Autodesk
`Default-Multi-Discipline_Metric.rte` sí contiene **M_Multi Category Tag**
(id 603537, etiqueta con `Type Mark`). Se extrajo a
`C:\hz-live\fam\HZ_MULTICAT_TAG_2026.rfa` (staging Python: `EditFamily` +
`SaveAs`), se cargó en una copia nueva de `HZ_WRITE` guardada como
`HZ_WRITE3.rvt` (22,4 MB; `save_as` tipado y `save_document` tipado con
clave de idempotencia) y se declaró `WriteDocument3`. Tres medidas por el
camino, todas con scripts de solo lectura o de staging revertido:

- La plantilla **"Mechanical Plan"** oculta la categoría *Multi-Category Tags*
  y `HZ_WRITE` la aplica por defecto a toda planta nueva; en el fixture se
  vació la plantilla por defecto del tipo de vista Floor Plan.
- Una familia **solo con etiqueta no tiene caja** en la API aunque su texto
  resuelva (`get_BoundingBox` null, geometría 0): se añadió un marco de
  líneas de detalle a nuestra copia de la familia y se recargó.
- Un anfitrión **fuera del rango de vista** produce una etiqueta sin
  geometría a cualquier elevación de la cabeza.
- La etiqueta muestra `Type Mark`; ningún tipo del fixture lo tenía: 50 tipos
  (tuberías, ductos, accesorios, terminales, equipos) reciben una marca
  `X-nn` en el fixture.

Prueba tipada completa en `HZ_WRITE3` (`artifacts/…/c12-fixture/`): planta
nueva sin plantilla → `plan_annotations auto_tags` (safe, cobertura completa)
→ ensayo `annotate` constructible → apply **`committed_verified`** → relectura
`query_planimetry`: etiqueta 1546391, objetivo 1366888 (Mechanical
Equipment), tipo 1545970, vista 1546379, texto `E-01`, caja legible
(`extent` 144937,99044–156457,109060), líder. La prueba no se guardó
(HZ_WRITE3 se guardó antes, con familia, marcas y tipo de vista).

Producto (`1c8e249`): la negativa "no readable extent" nombra ahora la causa
en este orden — anfitrión no mostrado en la vista, categorías de anotación
apagadas, categoría de la etiqueta oculta (con la plantilla), texto vacío
(parámetro sin valor) — y solo después la limitación medida de las familias
solo-etiqueta. Hecho de fuente fijado por test.

Límite declarado: la RFA es de 2026; 2023–2025 no pueden cargarla (formato
posterior). Las dos sondas de verify-live se ejecutan en 2026.

### 3. Candidato final y validación en Revit 2026

Tres sesiones aisladas de Revit 2026, cada una arrancada solo tras `-Enable`
con éxito y cerrada con cierres tipados con descarte; en dos ocasiones un
documento de vínculo creado por verify-live (`w12-linkcopy2-…`) no es
cerrable por título y el cierre gráfico dejó el diálogo Save File de
`HZ_WRITE3`, respondido "No" por handle solo tras leer que el texto nombra
esa copia desechable (declarada `WriteDocument3`, ya guardada).

| Candidato | Commit | DLL 2026 | Servidor | Revit 2026 | Para qué |
| --- | --- | --- | --- | --- | --- |
| c12 | `efc7502` | `d595c24e…` | `2cae2e78…` | pid 41008 | preparación del fixture `HZ_WRITE3`; checks de interfaz 21/21 |
| c13 | `1c8e249` (negativa de etiqueta con causa) | `3c8d4950…` | `f0a2a321…` | pid 8988 | estabilización 41/0/2 y 42/0/1, general 14/0/1, verify-live sobre `HZ_WRITE3` con identidad exigida **241/0/0 funcionales (242/1/0/1)** |
| **c14** | **`7fe8693`** (= c13 en producto; solo arregla el conductor) | `8adb404f…` | `f4daacdd…` | pid 35476, 2026.4 (26.4.0.32), `English_USA` | la validación final de 2026: estabilización 41/0/2 y 42/0/1, general 14/0/1, verify-live **241/0/0 funcionales (242/1/0/1)** con `-ExpectedCommit 7fe8693… -ExpectedAddinSha256 8adb404f… -ExpectedServerSha256 f4daacdd…`; checks de interfaz 21/21; PDF revisados a ojo (`visual-review.md`, sección c14) |
| c15 / c16 / c17 | `3e6dcba` / `3d273f2` / `5f3e3a0` | — | — | — | solo conductor y sus pruebas: `git diff 7fe8693..5f3e3a0 -- src/` está vacío |

La única sonda fallida de verify-live es "the add-in binary matches the
release manifest" (build de desarrollo, no de release) y la única no cubierta
es el artefacto instalable: ambas son requisitos de publicación, fuera de
este alcance.

Interfaz (checks sobre la DLL del candidato, sin Revit y sin tocar ajustes):
el idioma sale de `LanguageType` de Revit (el Revit 2026 de esta máquina es
`English_USA` y la cinta se ve en inglés aunque Windows esté en español);
las cuatro filas muestran el estado efectivo leído del mismo `settings.json`
que usa el dispatcher (perfil traducido a palabras, sin `safe_write` ni
similares a la vista); cada fila selecciona su clave y cierra; el botón
Cerrar selecciona nada; el hash de `settings.json` es idéntico antes y
después de abrir, pulsar filas y cerrar; el consentimiento de scripts
conserva la casilla de comprensión y los textos fijados por prueba; y las
frases de la interfaz se recortaron a lo que el código garantiza
(`efc7502`: el historial no nombra el archivo; un asistente en pausa aún
responde que lo está). Captura de Revit con la pestaña Horizun Hub presente
(`c13-ui/c13-revit-window.png`), panel completo en `c10-revit-horizun-tab.png`,
renders del menú desde la DLL c14 en `c14-ui/`. Los permisos del dueño no se
tocaron para probar estos controles.

### 4. Tres corridas reales del conductor, tres defectos suyos (c14, c15, c16)

Cada intento de la matriz sobre el conductor nuevo cayó en un defecto propio
antes de arrancar ningún Revit — y en los tres la guarda hizo su trabajo:
ningún Revit arrancó, ningún manifiesto quedó cambiado.

1. `Set-StrictMode` del módulo se filtraba al conductor por el dot-source y
   `$null.Count` reventaba antes de descubrir instancias → `7fe8693` (c14):
   el módulo no fija modo estricto; la lista de Revit en ejecución es siempre
   array.
2. Las sondas reales del módulo eran scriptblocks sin `GetNewClosure()`: al
   ejecutarse desde el conductor no veían `$Repo` ni `$ServerExe` y `-Enable`
   fallaba con "Path is null" (`enable_failed`, sin Revit) → `3e6dcba` (c15).
3. Las sondas Enable/Restore devolvían la salida del script hijo como array,
   y `[int]` sobre un array no es un código de salida: `enable_failed` y
   `restore_failed` falsos y archivos de recuperación espurios (conservados en
   `year-matrix/stale-driver-bug-20260908/`) → `3d273f2` (c16): `Write-Host`
   para la salida del hijo y `return [int]$LASTEXITCODE`; hecho fijado por
   prueba con las sondas reales (año inexistente 2099).

### 5. Matriz de años (conductor c16 `3d273f2`, producto c14; 22:18–22:43Z)

Un año por vez, cada uno con su binario compilado por el conductor desde el
árbol limpio en `3d273f2` (producto idéntico a `7fe8693`), firmado con el
certificado local y conservado en `year-matrix/<año>/binaries/` (firmado y
sin firmar); servidor `f4daacdd…` (estampado `7fe8693`, por eso el resumen
marca `server_matches_tree=false`). Fixtures base propios de cada año
(`HZ23_BASE`…`HZ27_BASE`), nunca guardados.

| Año | Revit | DLL cargada | Estabilización | General | Cierre / restauración |
| --- | --- | --- | --- | --- | --- |
| 2023 | pid 49596 | `a9f37f0e…` | 41/0/0/2 (primera corrida en este fixture) | 14/0/0/1 | cerrado por el puente y por la ventana; manifiesto restaurado y verificado |
| 2024 | pid 53332 | `efd124af…` | 42/0/0/1 (reanuda el libro de la corrida c9) | 14/0/0/1 | cerrado; restaurado y verificado |
| 2025 | pid 53400 | `b7202762…` | 42/0/0/1 | 14/0/0/1 | **dejado corriendo** (`left_running_identity`); restauración diferida; `recovery-pending-2025.json` |
| 2027 | pid 56428 | `1fd08835…` | 42/0/0/1 | 14/0/0/1 | cerrado; restaurado y verificado |

Notas medidas: en 2023 el conductor cerró por lista blanca el modal ajeno
"External Tools - External Tool Failure" (no es un diálogo de guardar); cada
registro de arnés lleva `code_candidate_commit=3d273f2`,
`built_from_clean_tree=true` y `repo_tracked_clean=true`, mientras el resumen
del conductor dice `repo_tracked_clean=false` en su primer segundo (el commit
`3d273f2` se hizo un segundo antes de arrancar; no se determinó qué vio
`git status` en ese instante — se registra la discrepancia, no se explica).
Los registros de la corrida c9 (`76fc894`, 15:00Z) siguen en las mismas
carpetas y no se mezclan con estos. Salida del conductor: 2 (un año
pendiente).

### 6. Incidente de 2025 y corrección (`5f3e3a0`, c17)

La identidad de la sesión 2025 se grabó con ejecutable vacío: `MainModule`
no era legible en el instante posterior a `Start-Process`. En el cierre, la
recomprobación comparó `''` con `C:\Program Files\Autodesk\Revit 2025\Revit.exe`,
declaró la sesión "no verificablemente nuestra" y —como está mandado— la dejó
corriendo, no tocó el manifiesto y escribió el archivo de recuperación con
los pasos. Conducta correcta del conductor ante una duda; causa corregible.

Corrección ensayada primero sobre copias del módulo en el scratchpad (34/34)
y solo después aplicada al árbol: la identidad vuelve a preguntar hasta 15 s
hasta leer ejecutable y hora de inicio, graba `exe_expected` (el ejecutable
que el conductor lanzó) junto al leído, la comprobación cae a `exe_expected`
cuando el leído falta y, sin ninguno de los dos, el estado es `unknown`
(dejado corriendo), nunca `alive`. Tres pruebas más (34 en total).

### 7. Cierre de las sesiones por el usuario, reinicio y restauración

Leído del diario de Revit y del disco, no supuesto: el usuario cerró las dos
sesiones de ensayo que quedaban (Revit 2025 pid 53400 y Revit 2026 c14 pid
35476) con `ID_APP_EXIT` a las 23:03:51Z y 23:03:58Z, respondiendo **No**
(`IDNO`) a "Do you want to save changes to HZ25_BASE.rvt?" y a "…HZ_WRITE3.rvt?";
la máquina se reinició a las 23:59:47Z. Nada del ensayo se guardó:
`HZ25_BASE.rvt` conserva su fecha (2026-02-19), `HZ_WRITE3.rvt` la de su
preparación (21:44Z, anterior a la corrida c14), `HZ_WRITE.rvt` la de febrero.

Con ningún Revit corriendo (2026-09-09 01:3xZ):
`dev-addin-session.ps1 -Year 2025 -Restore` y `-Year 2026 -Restore` salieron
con 0; en los cinco años `Horizun.addin` apunta a `Horizun\Horizun.Revit.dll`,
sin `Horizun-dev-session.addin` ni `Horizun.addin.dev-session-aside`; las
cinco DLL instaladas conservan sus huellas y fechas del 07-09 (2023
`548be2b0…`, 2024 `c2abafa5…`, 2025 `316b5283…`, 2026 `fe792a0d…`, 2027
`a7c8bae8…`) y el servidor instalado `dbeebb54…` (07-09 21:03Z). Los archivos
de descubrimiento del directorio de datos no se tocaron. El archivo de
recuperación de 2025 se conservó como
`recovery-pending-2025.resolved-20260909.json` junto a
`recovery-pending-2025.resolution.md` (qué pasó y cuándo).

### 8. Fila 2025 repetida con el conductor c17 (2026-09-09 01:35–01:43Z)

Sin ningún Revit corriendo, `run-year-matrix.ps1 -Years @('2025')` en `5f3e3a0`:
binario 2025 compilado desde el árbol limpio y estampado `5f3e3a0` (producto
idéntico a `7fe8693`), firmado, DLL `e186594f…`; Revit 2025 pid 28728 arrancado
solo tras `-Enable` con éxito; identidad grabada completa en el arranque
(`exe` leído y `exe_expected`, ambos `Revit 2025\Revit.exe`). Estabilización
**42/0/0/1**, general **14/0/0/1** sobre `HZ25_BASE`; antes de cerrar, la
identidad se recomprobó `alive`, el puente listó solo `HZ25_BASE`, el cierre
tipado con descarte lo cerró, la ventana salió sola, `-Restore` devolvió 0 y
el disco quedó como al inicio (manifiesto instalado presente, DLL instalada
`316b5283…` intacta). Salida del conductor: **0**; ningún archivo de
recuperación. `HZ25_BASE.rvt` conserva su fecha.

### 9. Dos defectos más del conductor, vistos en esa corrida (`4719769`, c18)

- El resumen del conductor decía `repo_tracked_clean=false` con el árbol
  limpio. Reproducido en aislamiento con el preámbulo del conductor:
  `[string](& git status --porcelain) -eq ''` es **False** aunque no haya
  salida, porque el resultado vacío convertido a `[string]` es `$null` y
  `$null -eq ''` es falso. Todos los resúmenes `year-matrix-*.json` del 08-09
  y de la repetición de 2025 llevan ese `false` por este defecto; los
  registros de cada arnés (`repo_tracked_clean=true`, `built_from_clean_tree=true`,
  `code_candidate_commit`) se calculan aparte y son los válidos. Corregido:
  `Get-HzRepoStatus` cuenta líneas y el resumen graba `repo_status` (las
  líneas), con dos pruebas sobre un repositorio git desechable y un hecho de
  fuente.
- El aviso "THE SERVER ON DISK IS STAMPED '{0}'…" salía sin formatear (`-f`
  se aplicaba solo a la segunda mitad de la concatenación). Corregido y
  fijado por hecho de fuente. 38 pruebas en total.

### 10. Fila 2023 con c18: el modal tardío, la sesión dejada corriendo y su recuperación (01:50–01:56Z)

`run-year-matrix.ps1 -Years @('2023')` en `4719769`: Revit 2023 pid 37728
arrancado tras `-Enable`, identidad completa (ejecutable leído y esperado), el
aviso del servidor estampado ya formateado y `repo_tracked_clean=true` con
`repo_status=[]` (c18 en vivo). El puente publicó, los 16 s de asentamiento no
vieron ningún diálogo, y **después** apareció el modal ajeno "External Tools -
External Tool Failure" (Autodesk Insights, el título que la lista blanca
nombra): `horizun_open_document` de `HZ23_BASE` fue rechazado con "MODAL
DIALOG open" (`state_before=fixture_open_failed`) y, en el cierre, `horizun_health`
igual — el conductor dejó la sesión corriendo (`left_running_health`), difirió
el manifiesto y escribió `recovery-pending-2023.json`. Respuesta correcta ante
una duda; la causa era su propia ventana de tiempo.

Recuperación por las sondas reales del módulo (`recovery-run-2023-20260909.json`,
`recovery-pending-2023.resolution.md`): un primer intento pasó la hora de
inicio reformateada como texto local y la guarda lo rechazó como desfase de 5 h
(bien); con el texto ISO UTC la identidad fue `alive`; las ventanas del pid eran
solo "Autodesk Revit 2023 - [Home]" y el monitor (el modal se había ido solo;
nada se cerró por ventana); health respondió para el pid 37728 con cero
documentos; el cierre del módulo no cerró nada, pidió salir y salió;
`-Restore` con 0 y disco verificado (DLL instalada `548be2b0…`). `HZ23_BASE.rvt`
conserva su fecha de 2022.

Corrección (`e0e2436`, c19): `Get-HzModalDialogTitle` lee el título que nombra
una negativa del puente; el conductor registra por año una sonda
`DismissStartupDialog` que rechaza todo título fuera de `-DismissStartupDialog`
(y `Test-HzDialogTitleAllowed` sigue vetando guardar/descartar/sincronizar/seguridad)
y actúa solo sobre el pid arrancado; una apertura rechazada se reintenta una
vez tras ese cierre, y `Close-HzRehearsalSession` vuelve a preguntar health una
vez después; un diálogo de guardar nunca se cierra (probado); sin la sonda, la
conducta anterior. La fila del resumen conserva `state_before`/`why_before`.
44 pruebas.

### 11. Fila 2023 repetida con el conductor c19 (01:58–02:05Z)

DLL 2023 `f39d0645…` (estampada `e0e2436`, limpia; producto idéntico a
`7fe8693`), Revit 2023 pid 10008; el modal ajeno "External Tools - External Tool Failure" apareció esta vez durante la espera del puente y el conductor lo cerró por la lista blanca (registrado en `dismissed_dialogs`). Estabilización
**42/0/0/1**, general **14/0/0/1** sobre `HZ23_BASE` (segunda corrida sobre este fixture: reanuda el libro de la corrida c16).
Cierre: identidad `alive`, solo `HZ23_BASE` abierto, cerrado por el puente con descarte, la ventana salió sola; restauración: `restored` con salida 0, manifiesto instalado presente, DLL instalada `548be2b0…` intacta; salida del conductor
**0**.


### 12. Instalación estable comprobada (2026-09-09)

Con los manifiestos restaurados y ningún Revit abierto, se arrancó Revit
2026 por su ejecutable (sesión propia, sin documentos) y se leyó
`horizun_health` **a través del servidor instalado** (`dbeebb54…`):
- `status=healthy`, `horizun_version=1.2.1`, `horizun_commit=5498141…` con `built_from_clean_tree=true` (la instalación del 07-09);
- `addin_assembly.sha256=fe792a0d…` en `%APPDATA%\Autodesk\Revit\Addins\2026\Horizun\Horizun.Revit.dll`;
- `process_id=9788` (el pid arrancado por la comprobación), `revit_version=2026`, `open_document_count=0` (2026-09-09T01:49:11Z; registro en `artifacts/stabilization-2026-09-08/stable-health-20260909.{log,json}`).

Estado exigido: `healthy`, DLL `fe792a0d…` (la instalada el 07-09) y cero
documentos → **cumplido**. La sesión propia (pid 9788; ejecutable recomprobado; cero documentos según health; el pid de health igual al arrancado) se cerró por la ventana y salió (`exited=True`). Procesos Revit al final: ninguno.
La instalación estable no se sustituyó en ningún momento: `install.ps1` no
se ejecutó, ningún cliente MCP se reconfiguró, los permisos de Python no se
tocaron.


### 13. Conclusión de la fase 3

**Candidato local validado en este alcance.** Producto en `7fe8693` (c14):
`git diff 7fe8693..e0e2436 -- src/` está vacío; c15–c19 (`3e6dcba`, `3d273f2`,
`5f3e3a0`, `4719769`, `e0e2436`) solo tocan el conductor de la matriz y sus pruebas.
No es un release aprobado: nada se instaló, publicó ni empujó.

- Seguridad del conductor: reglas probadas sin Revit (44 casos) y en vivo —
  una identidad dudosa real y un modal tardío real dejaron un Revit corriendo
  en vez de matarlo, y ambas sesiones se recuperaron por las mismas reglas; los
  cierres solo pasaron por el puente sobre fixtures propios; ningún
  manifiesto se tocó bajo un Revit abierto.
- Fixture de etiquetas: cerrado en 2026 (`HZ_WRITE3`); 2023–2025 sin RFA
  compatible (límite declarado).
- Validación: Core 3.882/0, Server 477/0, cinco años sin advertencias,
  contrato/inventario/consistencia/escaneo en verde; Revit 2026 c14:
  estabilización 41/0/2 y 42/0/1, general 14/0/1, verify-live 241/0/0
  funcionales (242/1/0/1), interfaz 21/21; matriz 2023/2024/2025/2027 con el
  mismo producto (tabla de la sección 5 y filas repetidas de las secciones 8
  y 11).
- Entorno: manifiestos de los cinco años restaurados y verificados; DLL y
  servidor instalados con sus huellas del 07-09; health de la instalación
  estable verificada por el servidor instalado; ninguna sesión ni documento del usuario tocado (las dos
  sesiones de ensayo que quedaban las cerró el usuario sin guardar antes de
  reiniciar).

Pendientes fuera de este alcance: publicación (`install.ps1` +
`verify-live -ReleaseGate` con huellas del manifiesto); revisión visual en
PDF de una cota desplazada y una etiqueta con líder (hace falta un plano del
arnés que las coloque); RFA de etiqueta para 2023–2025; los resúmenes
`year-matrix-*.json` anteriores a c18 llevan `repo_tracked_clean=false` por
el defecto descrito (los registros de arnés son los válidos).

## Cómo continuar

- Publicar, cuando se autorice: `install.ps1` en entorno seguro sobre
  `7fe8693` o un commit posterior con `src/` idéntico, luego
  `verify-live.ps1 -ReleaseGate` con las huellas del manifiesto, y
  `run-year-matrix.ps1` (conductor `e0e2436` o posterior) para los años que no
  sean sesión del usuario.
- Arneses reproducibles: `verify-deliverable-stabilization.ps1`,
  `verify-deliverable-production.ps1` (con `HZ_WRITE` recién abierto y
  activo) y `verify-live.ps1` (receta de una pasada con `HZ_WRITE3`:
  `-Document HZ_WRITE3 -WriteProbes -WriteDocument HZ_WRITE3
  -WriteDocumentDisposable yes-this-model-is-disposable`).
- Seguridad del conductor sin Revit: `pwsh -File scripts/live/year-matrix.session.tests.ps1`.


---

# Sesión 2026-09-09 — pendientes de seguridad del conductor y cobertura de entregables

Sesión autónoma sobre la misma rama, `HEAD` en `318a821`, árbol limpio al
empezar. **El producto no cambió**: `git diff 7fe8693..HEAD -- src/` sigue
vacío. Los cinco archivos tocados son el módulo del conductor de la matriz, el
conductor, sus pruebas, el helper de sesión de desarrollo y un arnés nuevo.

## Estado de partida verificado

| Hecho | Cómo se comprobó |
| --- | --- |
| Rama `codex/bim-production-product-layer`, HEAD `318a821`, árbol limpio | `git status --porcelain` (vacío) |
| Sin diferencias de producto contra `7fe8693` | `git diff --stat 7fe8693 HEAD -- src/` (vacío) |
| Revit 2025 pid 24592 es la sesión del usuario | clasificado como `other_year` por el propio clasificador; nunca se le envió una llamada |
| Los cinco manifiestos instalados, sin `dev` ni `aside`, DLL del 07-09 | lectura directa de `%APPDATA%\Autodesk\Revit\Addins\<año>` |
| `enable_execute_python: false` en `settings.json` | lectura; **no se editó** |

## 1. Corrección A — la propiedad de un documento no es su título

`Test-HzOnlyRehearsalDocuments` comparaba TÍTULOS contra el libro (que sí
guardaba rutas), y además el conductor concedía `^HZ\d\d_BASE` y `^HZ_` como
patrones de propiedad, y el módulo `^HZ_ANCHOR`. Reproducido contra el código tal
como estaba (`artifacts/stabilization-2026-09-09/repro-defects-at-318a821.ps1`,
8/8 defectos reproducidos): un modelo del usuario llamado `HZ_PROYECTO_USUARIO` o
`HZ_ANCHOR_usuario` pasaba el control, y el cierre lo habría cerrado.

Ahora cada documento que la corrida abre se REGISTRA después de que el puente
confirme la apertura, con la identidad que el puente publica — la ruta
normalizada — leída de `horizun_health`, no de los argumentos enviados. Solo una
ruta registrada (o una declaración explícita de "sin ruta" para una apertura
detached, válida únicamente mientras ese título sea inequívoco) es cerrable. El
anchor del propio puente se reconoce por vivir bajo su directorio de anchors, no
por llamarse así. El libro se ata a un pid; health tiene que responder por ese
pid; el conjunto abierto se relee antes de CADA cierre y una vez más antes de
pedir la salida del proceso. El cierre apunta a la ruta registrada y el ensayo
del propio puente se compara con el documento pretendido antes de gastar el
token: la cadena de propiedad no puede terminar en otro documento. Nada se
reporta cerrado hasta que una relectura no lo encuentra, y se exige
`closed=true`, no la mera ausencia de error.

## 2. Corrección B — una restauración que no verificaba nada

Reproducido: sin manifiesto instalado, sin hash legible de la DLL y con un hash
esperado en la mano, `Restore-HzYearSession` respondía `ok/nothing_to_restore`.
Además cada comparación estaba escrita como `$esperado -and $actual -and ...`,
que se salta justamente cuando el archivo que protege ha desaparecido; y un
manifiesto se creía por su NOMBRE.

Ahora se captura una instantánea ANTES de tocar nada (qué manifiestos existen, a
qué apunta el instalado, si la DLL está y es hasheable) y el estado final se
juzga contra ella. Cada caso tiene su estado propio: `installed_manifest_missing`,
`installed_manifest_wrong_target`, `installed_appeared`, `installed_dll_missing`,
`installed_dll_changed`, `installed_dll_appeared`, `restore_unverifiable`. «No se
pudo leer» nunca se pliega a «no existe».

`dev-addin-session.ps1` valida el conflicto manifiesto/aparte ANTES de borrar el
manifiesto de desarrollo (antes borraba primero y se negaba después, dejando el
año sin add-in), reporta exactamente qué cambió y qué queda pendiente ante un
fallo parcial (salida 4), y usa la salida 3 para el conflicto sin tocar nada.

## 3. Corrección C — un Revit de año desconocido no es «no hay Revit»

El conductor y el helper filtraban por `MainModule.FileName -like "*\Revit <año>\*"`
dentro de un `try/catch` que se tragaba el fallo: un Revit cuyo ejecutable no se
puede leer desaparecía de la lista, y el manifiesto podía renombrarse debajo de
él. Reproducido con un proceso real llamado `Revit.exe` (copia de `waitfor.exe`,
sin ventana, que se apaga solo): `YearRevits` devolvía 0 procesos y la
restauración seguía adelante.

Ahora los procesos se clasifican en año objetivo / otro año conocido /
desconocido; uno desconocido bloquea el cambio nombrando su pid y el motivo; el
proceso no se toca ni se escalan permisos; y la comprobación se repite
inmediatamente antes del cambio, no una vez por año.

## 4. Lo que encontró la primera corrida real

El conductor corregido se ejecutó contra un Revit 2026 real y murió en su primera
llamada de health. En todos los casos la guarda se sostuvo: no se cerró ningún
Revit, no se renombró ningún manifiesto bajo un Revit abierto, y se escribió el
registro de recuperación.

1. **Una sonda no ve las funciones de su propio archivo.** `GetNewClosure()` reata
   el scriptblock a un módulo dinámico cuya búsqueda de funciones cae al ámbito
   GLOBAL, y que las funciones estén ahí depende de cómo se ARRANCÓ el conductor:
   con `pwsh -File` sí; con `pwsh -Command "& run-year-matrix.ps1"` — que es la
   forma del comando reproducible de la revisión — no. Medido: `The term
   'Get-HzField' is not recognized`. Los ayudantes viajan ahora como VARIABLES, y
   hay dos guardas: una prueba AST de que ninguna clausura llama por nombre a una
   función de este archivo, y un caso que ejecuta las sondas reales bajo LAS DOS
   formas de arranque.
2. **Los juicios dentro de las sondas eran improbables.** Se extrajeron a
   `ConvertFrom-HzHealthReply`, `Read-HzCloseRehearsal` y `Read-HzCloseApply`,
   ejercitados contra respuestas enlatadas (una que no es JSON, una sin
   `open_documents`, una que el puente llama INCOMPLETE, una cuyo conteo no
   coincide con su lista).
3. **Una hora registrada que no se podía comparar se leía como otro proceso.** Una
   identidad que pasó por JSON vuelve como `DateTime`, y `[string]` sobre eso da
   la ortografía de la cultura local sin offset: la comparación ponía el mismo
   proceso a cinco horas de sí mismo y respondía `mismatch`. Ahora se exige ISO
   8601 con offset explícito, se parsea de forma invariante, y una hora ilegible
   es `unknown` — no poder comparar no es haber comparado.
4. **Un documento limpio no se podía cerrar** — defecto anterior a esta sesión. El
   puente emite token solo cuando el cierre perdería trabajo; para un documento
   sin cambios responde «this close would discard nothing, so it needs no token»
   y no emite ninguno. La sonda lo exigía siempre, así que el único fixture
   cerrable era uno sucio. Ahora el token se exige exactamente cuando
   `would_discard_unsaved` no es false, y el control de puntería corre igual.

## 5. Recuperación de la sesión varada

`artifacts/stabilization-2026-09-09/recovery-2026-run{1,2,3}.log`: la misma sesión
se negó tres veces por tres razones verdaderas distintas y a la cuarta cerró —
identidad viva, `HZ_WRITE3` reconocido por su ruta, ensayo resolviendo exactamente
ese título y esa ruta, cierre confirmado por relectura, salida normal por la
ventana — y el manifiesto se restauró y se verificó contra la instantánea previa.
El registro se conservó como `recovery-pending-2026.resolved-20260909.json` +
`resolution.md`.

## 6. Revisión visual de una cota desplazada y una etiqueta con líder

Arnés nuevo `scripts/live/verify-deliverable-visual.ps1`: construye la página en
un fixture desechable (planta 1:100 con un muro, dos ejes, una cota de 6000 mm
cuyo texto se desplaza 12 mm de PAPEL, y una etiqueta con líder), recorta la
vista, la coloca por el packer, exporta un PDF verificado y escribe
`visual-request.json` — lo pedido junto a lo releído. La última sonda queda
`not_covered` a propósito: la legibilidad es un juicio, y un arnés que la puntuara
estaría puntuando su propia petición.

La revisión está en `artifacts/stabilization-2026-09-09/visual-review.md` con las
páginas renderizadas. Veredicto: aceptada. Y **la página enseñó algo que ninguna
relectura podía**: en la primera versión el layout puso la etiqueta ENCIMA de su
muro, así que el líder era degenerado y no había nada que mirar, aun con
`has_leader=true` verificado. El arnés mueve ahora la cabeza de la etiqueta 2,5 m
fuera del anfitrión antes de exportar. Hallazgo adicional: el nombre y el número
de lámina desbordan sus casillas del rótulo (mismo caso que el 08-09; artefacto
de nomenclatura del arnés, cubierto por `fits_titleblock_cell`).

## 7. Fixture de etiquetas para 2023–2025: bloqueo medido

Medido, no supuesto: `HZ23_TAG.rvt` (copia desechable de `HZ23_BASE.rvt`) tiene
CERO tipos de etiqueta de muro y CERO de multicategoría cargados, así que ninguna
etiqueta puede colocarse sobre un muro ahí. La RFA de 2026 no se puede cargar en
2023 (formato posterior); extraer `M_Multi Category Tag` de la plantilla de 2023
exige `EditFamily` + `SaveAs`, es decir `horizun_execute_python`, que el dueño de
esta máquina tiene en `false` — y eso se respeta, no se edita; la API de Revit no
permite crear una etiqueta (label) dentro de una familia de anotación, así que
`horizun_create_family` tampoco la puede fabricar; y no hay ninguna RFA de
etiqueta en las bibliotecas locales. El bloqueo necesita a una persona: el dueño
habilitando Python por una sesión, o alguien guardando la familia desde la UI de
Revit 2023 una vez. La sonda se registra `fixture_missing` con ese motivo, nunca
como pasada ni como fallo.

## 8. Pruebas ejecutadas en esta sesión

| Capa | Comando | Resultado |
| --- | --- | --- |
| Seguridad del conductor, sin Revit | `pwsh -File scripts/live/year-matrix.session.tests.ps1` | **136 aprobadas, 0 fallidas, 0 omitidas** |
| Reproducción de los defectos | `pwsh -File artifacts/stabilization-2026-09-09/repro-defects-at-318a821.ps1` | **8/8 defectos reproducidos** contra `318a821` |
| Core (xUnit) | `dotnet test tests/Horizun.Core.Tests -c Release` | **3.882 aprobadas, 0 fallidas, 0 omitidas** |
| Server | `dotnet test tests/Horizun.Server.Tests -c Release` | **477 aprobadas, 0 fallidas** |
| Inventario, consistencia pública, escaneo sensible | `scripts/inventory.tests.ps1`, `scripts/public-consistency.tests.ps1`, `scripts/scan-sensitive.ps1` | en verde (escaneo: 856 archivos, limpio) |
| Matriz por año, Revit 2026 | `run-year-matrix.ps1 -Years 2026 -Harness verify-deliverable-visual.ps1` | **green**: 6 sondas medidas, 1 no cubierta (aceptación visual); cierre y restauración automáticos |
| Matriz por año, Revit 2023 | ídem con `HZ23_TAG` | **green**: 5 medidas, 1 `fixture_missing` (familia de etiqueta), 1 no cubierta; modal ajeno de arranque cerrado por lista blanca |
| Revisión visual | `python scripts/render-pdf.py <pdf> <dir>` | aceptada; ver `visual-review.md` |

## 9. Entorno al terminar

- Solo corre el Revit 2025 del usuario (pid 24592, anterior a esta sesión); nunca
  se le envió una llamada ni se abrió nada en él.
- Los cinco manifiestos instalados, sin `dev` ni `aside`, con las mismas huellas
  de DLL que al empezar (2023 `548be2b0…`, 2024 `c2abafa5…`, 2025 `316b5283…`,
  2026 `fe792a0d…`, 2027 `a7c8bae8…`).
- Servidor instalado intacto (`dbeebb54…`, del 07-09). **No se ejecutó
  `install.ps1`**, no se reconfiguró ningún cliente MCP, no se tocaron
  certificados ni permisos de Python, no hubo push, release ni sincronización con
  CORE.
- Fixture nuevo y desechable: `C:\hz-live\HZ23_TAG.rvt` (copia de `HZ23_BASE`).
  Ningún fixture fuente se guardó ni se modificó.

## 10. Adenda offline (2026-09-09, tarde) — con Revit ocupado

El usuario pidió continuar solo con trabajo aislado. Nada de esta sección tocó
Revit, manifiestos, la instalación ni el permiso de Python, y no se programó
ninguna prueba para arrancar sola.

### Qué significa exactamente «green» en la fila de 2023

**No significa cobertura completa de etiquetas.** La fila 2023 de
`ym-2023-run2` está en `green` porque el conductor terminó sin fallos, y a la vez
su arnés registró **`fixture_missing` en `tag-with-leader`**. El estado del año
resume el CÓDIGO DE SALIDA del arnés; el artefacto del arnés es lo que dice qué
se midió. Las tres cosas hay que leerlas por separado:

| Capa | 2026 | 2023 |
| --- | --- | --- |
| Mediciones automáticas aprobadas | 6 sondas en PASS | 5 sondas en PASS |
| Fixture ausente (no medido, no aprobado) | — | **1: `tag-with-leader`** |
| Revisión visual (juicio humano/agente, nunca puntuada por el arnés) | aceptada sobre la página renderizada | aceptada solo para la cota; **no hay etiqueta en esa página** |
| Pruebas pendientes | ver `PENDING-LIVE-PROCEDURES-2026-09-09.md` | ídem |

Recomendación registrada, no aplicada: `Complete-HzRun` traduce hoy a salida 0
un run cuyas sondas incluyen `fixture_missing`. Cambiarlo afecta a todos los
arneses del repositorio, así que se deja como decisión del dueño y no como un
cambio hecho de paso.

### Aislamiento comprobado antes de ejecutar, no después

`scripts/live/isolation-guard.ps1` toma la huella de lo
real —los cinco manifiestos instalados y sus DLL, el servidor instalado, el
`settings.json` del dueño y la lista de procesos Revit— y compara. Auditoría
estática previa del arnés: todo lo que escribe va bajo `%TEMP%`, y cada llamada
al `dev-addin-session.ps1` real corre con `%APPDATA%` redirigido a una carpeta
temporal, restaurado en `finally`.

Resultado de la corrida con el usuario trabajando en Revit 2025:
**133 aprobadas, 0 fallidas, 3 omitidas**, y las huellas de antes y después
**idénticas** (`isolation-before.json` / `isolation-after.json`).

Las 3 omitidas son las que necesitan un proceso que la máquina llamaría «Revit»
(una copia de `waitfor.exe`, sin ventana, que no puede tocar ningún manifiesto
porque su llamada al helper corre contra un `%APPDATA%` temporal). Se saltan con
`-SkipProcessNamedRevit`, se cuentan y se imprimen como SKIP: **omitidas no es
aprobadas**. Se midieron en verde esta misma mañana, antes de que el usuario
ocupara Revit.

### Familias de etiqueta en las bibliotecas locales: respuesta medida

`scripts/rfa-provenance.py` lee el `BasicFileInfo` de cada `.rfa` —la versión que la
guardó— sin abrir Revit. Validado contra un caso conocido: dice `Format 2026`
para `HZ_MULTICAT_TAG_2026.rfa`, que es exactamente por lo que 2023 no puede
usarla.

- **2.241 familias** escaneadas; por formato: 2023=411, 2024=453, 2025=452,
  2026=471, 2027=454.
- **411 abribles en Revit 2023**, todas de Structural Precast, Route Analysis,
  viewport/tabla de planificación y los fixtures propios.
- **Ninguna etiqueta.** El único nombre con «tag» entre ellas es
  `Solaio a nucleo cavo - Simbolo di taglio.rfa` — italiano, símbolo de corte.
- Las familias de `Structural Precast/Annotations` existen solo desde formato
  2024 hacia arriba, y son anotaciones de prefabricado.
- Límite declarado del método: la CATEGORÍA de una familia no es legible offline
  por esta vía (el barrido de cadenas no encuentra la categoría ni siquiera en la
  etiqueta multicategoría conocida). No hizo falta: las candidatas se descartan
  por procedencia (formato ≥ 2024) o por naturaleza, no por categoría. Si
  apareciera una candidata plausible, quedaría **pendiente de validación**.

Segundo obstáculo, de contrato y no de bibliotecas: **ninguna herramienta tipada
carga una `.rfa` existente en un proyecto abierto**. `horizun_family_apply`
homologa la familia ACTIVA y nunca abre archivos; `horizun_create_family` solo
carga lo que ella misma creó, y la API no permite crear un label dentro de una
familia de anotación. Por eso el procedimiento preparado tiene un paso humano
único.

### Preparado, sin ejecutar

`docs/PENDING-LIVE-PROCEDURES-2026-09-09.md` lleva los dos procedimientos
completos, con comandos, resultado esperado y cómo se reconoce el acierto:

- **A** — etiquetas 2023–2025: un paso manual tuyo en Revit 2023 (cargar y
  guardar la familia en `HZ23_TAG.rvt`) y después tres corridas automáticas.
- **B** — documento ajeno REAL: fixture desechable creado
  (`C:\hz-live\HZ_DESECHABLE_NO_REGISTRADO.rvt`, formato 2023, SHA-256
  `703585d2…`) y arnés `open-unregistered-document.ps1` escrito. Abre su archivo
  por el puente FUERA del registro del conductor, así que la sesión debe quedar
  `left_running_foreign_document` con `closed_documents` vacío. Comprobado
  offline que el arnés se niega ante una ruta que no sea la desechable **sin
  hacer ni una llamada** al puente.

---

# Sesión 2026-09-09 (tarde) — validación local en vivo

Con la máquina libre. Producto sin cambios: `git diff 7fe8693..HEAD -- src/`
sigue vacío. Todo lo tocado es arnés, conductor y evidencia.

## Estado de partida verificado

| Hecho | Cómo |
| --- | --- |
| HEAD `bfda438`, árbol limpio | `git status --porcelain` |
| Cero procesos Revit al empezar | lectura del proceso, registrada en `env-before.json` |
| Cinco manifiestos instalados, sin `dev` ni `aside`, DLL del 07-09 | `scripts/live/isolation-guard.ps1` |
| Servidor instalado `dbeebb54…` intacto | ídem |
| Sin recuperaciones pendientes vivas | las cuatro de `stale-driver-bug-20260908/` son históricas y sus manifiestos están restaurados |

## 1. Documento ajeno REAL — la última ruta sin caso real

Primer intento: el puente **se negó a abrir** el fixture, porque estaba guardado
en 2023 y la sesión era 2026, y abrirlo lo habría actualizado de forma
irreversible. El producto tenía razón; el defecto era del procedimiento. Se hizo
una copia en formato 2026 (`HZ_DESECHABLE_NO_REGISTRADO_2026.rvt`, copia de
`HZ_WRITE3`) y se repitió.

Resultado, exactamente el esperado (`ym-2026-foreign2/`):

- `close.state = left_running_foreign_document`, nombrando
  `HZ_DESECHABLE_NO_REGISTRADO_2026` con «this run did not open …»;
- `closed_documents` **vacío**; `open_before` = los dos documentos;
- Revit **dejado corriendo** (pid 41348) y manifiesto **no** restaurado
  (`deferred_revit_running`); `recovery-pending-2026.json` escrito;
- health posterior: los dos documentos siguen abiertos e intactos.

**Recuperación deliberada** (`recover.ps1`, `recovery.log`, `recovery.json`):
identidad viva confirmada contra el registro de la propia corrida; exactamente
dos documentos abiertos y ambos son las copias desechables preparadas; ningún
tercero apareció. Solo entonces se registraron los dos por ruta y el módulo
cerró ambos, salió por la ventana y restauró el manifiesto verificándolo contra
la instantánea previa (DLL `fe792a0d…` sin cambios). Los dos ficheros en disco
quedaron con la misma huella que antes de abrirlos.

## 2. La familia de etiqueta para 2023–2025, resuelta sin intervención humana

Lo que faltaba mirar: **las plantillas de proyecto de Autodesk traen las familias
ya cargadas**, y una plantilla es un documento — el puente puede abrirla y
guardarla como proyecto. Es una vía tipada, sin instalar contenido, sin habilitar
Python y sin pedirte nada.

`scripts/live/make-tag-base-fixture.ps1` abre las plantillas candidatas, le
**pregunta al modelo** qué familias de etiqueta tiene cargadas (una búsqueda de
cadenas en el fichero no puede responderlo: los flujos van comprimidos), se queda
con la primera que además trae rótulo, tipo de muro y nivel, y la guarda como un
fichero **nuevo** — nunca sobre la plantilla, con `overwrite=false` — cerrando
todo lo que abrió.

Medido sobre `Default_M_ENU.rte` de Revit 2023: **2 tipos de etiqueta de muro**
(`M_Wall Tag`), 1 rótulo, 25 tipos de muro, 2 niveles. Producto:
`C:\hz-live\HZ23_TAGBASE.rvt`, formato **2023** (build 20220503_1030), 3.530.752
bytes, SHA-256 `d16ff001cec92090…`, y una copia independiente por año.

## 3. Cuatro defectos encontrados al ejecutar, y corregidos

| Defecto | Dónde | Commit |
| --- | --- | --- |
| Un **tipo** de cota no es una cota: `query_dimensions` lista las cotas que el documento TIENE, y una fixture limpia no tiene ninguna. Ambos arneses declaraban «sin tipo de cota lineal» sobre un documento que sí los trae | `make-tag-base-fixture.ps1`, `verify-deliverable-visual.ps1` | `cd8af9e` |
| El `dimension_type_id` lo resuelve el producto: `dimension_set` lo exige, `intent_dimension` no. El arnés rechazaba una fixture por un id que no necesitaba | `verify-deliverable-visual.ps1` | `5e7f036` |
| Un **tipo** de rótulo está cargado mucho antes de que ninguna lámina lo use: el arnés pedía instancias y se negaba sobre un documento que tiene la familia | `verify-deliverable-visual.ps1` | `0ccb764` |
| Una fixture de un año anterior se actualiza irreversiblemente al abrirla en uno posterior, y el puente lo exige por escrito. El permiso es del que llama: `-PrepareAllowUpgrade`, apagado por defecto | `run-year-matrix.ps1` | `0ccb764` |

Ninguno es del producto: los cuatro son del arnés preguntando mal, y tres de
ellos habrían roto **cualquier** fixture limpia.

## 4. Años medidos con el conductor corregido

Cinco copias independientes de la base (`HZ_TAGBASE_<año>.rvt`), una por año.

| Año | Fila | Etiqueta con líder | Cota desplazada | PDF | Cierre y restauración |
| --- | --- | --- | --- | --- | --- |
| 2023 | **green** | PASS | PASS | PASS | cerrado; restaurado y verificado |
| 2024 | **green** | PASS | PASS | PASS | ídem |
| 2025 | **bloqueado** | — | — | — | el usuario tiene abierto Revit 2025 (pid 43640, 08:50); el conductor se negó y no tocó nada |
| 2026 | **green** | PASS | PASS | PASS | cerrado; restaurado y verificado |
| 2027 | **green** | PASS | PASS | PASS | ídem |

En los cuatro años la DLL instalada quedó idéntica a la del inicio, y no hubo
ninguna recuperación pendiente.

## 5. Revisión visual de las cuatro páginas

`visual-acceptance.json` ata cada aceptación al **SHA-256 del PDF que se miró**.
En las cuatro: la cota `6000` con su texto claramente por encima de la línea,
entre los dos ejes; la etiqueta de muro con su marca completa dentro del rombo y
un líder que llega al muro; ninguna palabra fuera de página.

Dos observaciones honestas, ninguna del producto:

1. La marca de 8 caracteres es más ancha que el rombo de `M_Wall Tag`: el texto
   asoma por el borde del símbolo. No se corta nada; es la geometría de la
   familia contra el nombre que escribe el arnés.
2. Donde la etiqueta queda junto a un eje (2023, 2024) es porque **el arnés**
   mueve la cabeza 2,5 m fuera del anfitrión DESPUÉS de la colocación que evita
   colisiones, para que el líder se pueda ver. La proximidad es del arnés, no del
   planificador.

## 6. El evaluador estricto: «green» con `fixture_missing` no es cobertura

`scripts/live/verify-local-closure.ps1` + `local-closure.requirements.json`
responden `complete` solo si **todos** los casos obligatorios están cubiertos, y
mantienen separadas tres clases de evidencia: automática (solo cuenta `passed`),
revisión visual documentada (atada al hash del PDF) y el rechazo intencional
(que solo cuenta con todas sus condiciones **y** su recuperación verificada).
Nada de los arneses cambia: sus códigos de salida siguen significando lo mismo.
18 regresiones en `verify-local-closure.tests.ps1`, incluida una que fija que
`Complete-HzRun` sigue fallando solo por sondas `failed`.

Su primera ejecución leyó las carpetas de la barrida fallida y reportó 21
pendientes: hizo exactamente lo que se le pide.

## 7. Lo que queda, y por qué

**Revit 2025.** Los cinco casos de 2025 quedan `PENDING`. El usuario abrió Revit
2025 a las 08:50 y sigue abierto; una sesión ajena no se cierra ni se rodea. El
conductor la respetó, el evaluador la cuenta como no cubierta, y una sola corrida
la cierra cuando la máquina esté libre.

## 8. Entorno al terminar

`isolation-guard.ps1` comparado contra la instantánea del inicio: **la única
diferencia es el Revit 2025 del usuario**. Los cinco manifiestos, sus DLL, el
servidor instalado y `settings.json` son idénticos byte a byte. Ninguna sesión
propia quedó abierta. No se ejecutó `install.ps1`, no hubo push ni release, no se
tocaron clientes MCP, certificados ni el permiso de Python (`false`, sin cambios).

---

# Cierre de la validación local — 2026-09-09 (Revit 2025)

El usuario liberó Revit 2025. Antes de tocar nada se comprobó que realmente no
quedaba ningún proceso — que haya cerrado la ventana no prueba que el proceso
haya salido —: cero procesos de Revit, y `isolation-guard.ps1` **idéntico** a la
instantánea del inicio de la sesión.

## La corrida que faltaba

Una sola, sobre `C:\hz-live\HZ_TAGBASE_2025.rvt` (copia independiente del año,
guardada por Revit 2023 y actualizada en sitio por 2025 con el permiso explícito
`-PrepareAllowUpgrade`), al árbol `192c8d2` limpio:

| Sonda | Resultado |
| --- | --- |
| `fixture-annotated-plan` | passed |
| `dimension-text-displaced` | passed |
| `tag-with-leader` | passed |
| `view-placed-on-sheet` | passed |
| `view-cropped-for-paper` | passed |
| `sheet-exported-to-pdf` | passed |
| `visual-acceptance-rendered-page` | `not_covered` por diseño: se cubre mirando |

Fila `green`. Add-in del año compilado con 0 advertencias y 0 errores. Cierre
`closed` sobre el único documento que la corrida abrió y registró por ruta, sin
un solo documento ajeno en las tres comprobaciones. Restauración `restored`, con
la DLL instalada `316b5283…` idéntica a la del inicio. Sin recuperación
pendiente.

## La página, mirada

`visual-2025/page-001-clip-400dpi.png`, atada al PDF
`51d4a097f93e178b…` que produjo la corrida:

- la cota **6000** centrada entre los dos ejes (A y B a 60 mm de papel = 6000 mm
  a 1:100), con su texto claramente **por encima** de la línea: +1200 mm de
  modelo, exactamente los 12 mm de papel pedidos;
- la etiqueta de muro con la marca completa `HZV-b75e` dentro del rombo y un
  **líder recto** que sube desde el vértice del rombo hasta el muro, los 2,5 m
  que el arnés movió la cabeza; la relectura nombra como objetivo el muro 207938,
  que es el que creó esta corrida;
- 74 palabras, ninguna fuera de página.

## Una observación que faltaba en las cuatro revisiones anteriores

Al mirar la hoja completa aparece algo que las cuatro revisiones previas no
nombraron: **el nombre de hoja que escribe el arnés desborda el campo del rótulo
de Autodesk** y pisa los marcadores «Project Name» y «Project Number».

Se midió en las cinco páginas: los **mismos siete pares de palabras superpuestas,
en las mismas coordenadas**. No es de 2025 ni de ningún año; es el nombre largo
que escribe el arnés contra el ancho del campo de la familia, y no toca ninguna
parte del dibujo bajo revisión. `visual-acceptance.json` lo registra ahora en las
cinco entradas, y el criterio «sin superposiciones evidentes» quedó reescrito
para decir exactamente qué cubre la aceptación y qué queda nombrado en vez de
silenciado. Las cuatro entradas anteriores llevan `amended_utc`.

## Evaluación consolidada

`verify-local-closure.ps1` responde **COMPLETE**: 17 casos automáticos, 5
revisiones visuales y el rechazo intencional, todos satisfechos. Ninguno
pendiente.

## Entorno al terminar

`isolation-guard.ps1` contra la instantánea del inicio: **idéntico**, sin
salvedades — manifiestos, DLL, servidor instalado, `settings.json` y procesos de
Revit. Cero procesos de Revit. Ninguna recuperación pendiente. No se ejecutó
`install.ps1`, no hubo push, release ni tag, y no se tocaron clientes MCP,
certificados ni el permiso de Python (`false`, sin cambios).

---

# La presentación del plano — 2026-09-09 (tarde)

El cierre funcional estaba respaldado; la hoja no. El criterio decía «sin
superposiciones evidentes» y yo lo sustituí por una revisión limitada al dibujo
para poder declarar aprobada la hoja completa. Eso se corrige aquí arreglando el
plano, no el criterio.

## El defecto, medido

El campo de nombre de hoja del rótulo de Autodesk tiene **44,3 mm** de papel libre
alrededor de un centro en 680,7 mm; su caja de línea mide **24,8 mm** y su ancho
está entre **107 y 113 mm** (medido sobre las páginas exportadas: `Visual review`
cupo en una línea con 107,0 mm y el token siguiente envolvió). Cabe **una** línea
con ~10 mm de holgura; **dos no**. El nombre que escribía el arnés —una frase de
49 caracteres— envolvía a **cinco líneas, 106 mm**, y se imprimía a través de
«Project Name» y «Project Number».

## El intento que falló, y por qué se supo

Primer arreglo: nombre de hoja corto y la frase movida al **título de vista**,
para no perder contenido de la página. El rótulo quedó limpio, y aparecieron
**tres superposiciones nuevas**: el título de vista envuelve a unos 96 mm y la
familia de título de Revit pone la escala **0,52 mm** debajo de su caja, así que
la segunda línea no cae en papel libre — cae sobre `1 : 100`. Idéntico en los
cinco años.

Lo detectó la medida en el primer render, que es exactamente para lo que se
añadió. El nombre de vista vuelve al que siempre tuvo. La frase no era contenido
del plano: era el relato del ensayo metido en un campo que no lo admite, y se lee
en `visual-request.json`, en los identificadores de sonda y en el acta.

## Lo que impide que vuelva

`scripts/render-pdf.py` mide ahora **todos** los pares de palabras que se pisan,
sobre la hoja **entera**, con umbral de un quinto de la caja menor — el que separa
siete colisiones reales de trescientos falsos de kerning. Contra las páginas
viejas encuentra exactamente las siete.

`verify-local-closure.ps1` no cuenta una aceptación visual que no traiga esa
medida, que la traiga distinta de cero, o que nombre solo el recorte y no la hoja
completa. Tres regresiones nuevas, una por forma: **21 pasan**.

**Lo que el número NO cubre**: compara caja de palabra contra caja de palabra.
Texto sobre **geometría** —la marca de 8 caracteres que asoma del rombo de
`M_Wall Tag`— queda fuera de la cuenta y se sigue juzgando con el ojo. Está dicho
en cada acta, para que nadie lea el cero como más de lo que es.

## Las cinco páginas nuevas

Regeneradas en `ym-<año>-tags4` al commit `bd9917f`, un año por corrida:

| Año | Fila | Sondas | Palabras | Fuera de página | Superposiciones |
| --- | --- | --- | --- | --- | --- |
| 2023 | green | 6/0 | 69 | 0 | **0** |
| 2024 | green | 6/0 | 69 | 0 | **0** |
| 2025 | green | 6/0 | 69 | 0 | **0** |
| 2026 | green | 6/0 | 69 | 0 | **0** |
| 2027 | green | 6/0 | 69 | 0 | **0** |

Las cinco cerraron su sesión, restauraron el manifiesto y dejaron la DLL
instalada idéntica a la del inicio, sin recuperación pendiente.

**Hoja completa y detalle, ambos mirados en los cinco.** El rótulo imprime entero:
Owner, Project Name, el nombre de hoja `Dim + tag` en una línea, Project number,
Date, Drawn by, Checked by, el número de hoja y la escala — todos los campos
visibles, ninguno pisado y ninguno vaciado. En el detalle: la cota `6000`
centrada entre los ejes con su texto por encima de la línea, y el rombo con su
marca completa y un líder recto que llega al muro que creó la corrida.

## Evidencia conservada

Tres generaciones, ninguna borrada: `-tags2` documenta el defecto del rótulo,
`-tags3` documenta que el primer arreglo lo desplazó a la escala, y `-tags4` es la
que se acepta. `visual-acceptance.json` guarda las aceptaciones anteriores en
`historical_reviews`, superadas y fechadas, no eliminadas.
