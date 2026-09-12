# Revisión del candidato de producción — 2026-09-07

> Continuación: el bloque de estabilización que cierra estos requisitos está en
> [RELEASE-CANDIDATE-REVIEW-2026-09-08.md](RELEASE-CANDIDATE-REVIEW-2026-09-08.md)
> y su bitácora en [STABILIZATION-LOG-2026-09-08.md](STABILIZATION-LOG-2026-09-08.md).
> Este documento se conserva como historia; sus cifras describen el candidato del 07-09.

Decisión provisional: **no publicar todavía**. Esta campaña valida un árbol de
desarrollo sobre `5498141`, no una versión nueva ni el instalador estable.
Las cifras históricas de otras versiones no certifican este candidato.

## Requisitos de salida que faltan cerrar

| Prioridad | Hallazgo concreto | Criterio de cierre |
| --- | --- | --- |
| P0 | La nueva producción de entregables necesita ensayos de aplicación y fallos, no solo contratos y compilación. El ensayo anterior de etiquetas reconstruía una acción y omitía los campos nuevos del planificador. | Pasar la acción completa; ensayar cotas, colisiones, desbordamiento entre planos, exportación y relectura independiente; conservar cada petición y respuesta. |
| P0 | El ensayo con una familia legible sigue bloqueado por un `SpaceTag` del modelo de muestra sin extensión legible. La exclusión de categorías explícitamente ocultas resolvió el primer caso, pero no toda la visibilidad de anotaciones. | Resolver de forma verificable qué anotaciones participan en la vista (recorte, dependencias y anotaciones espaciales), sin descartar incertidumbre silenciosamente; pasar casos positivos y negativos con familias legibles y líderes. |
| P0 | `DeliveryPlan` devuelve instrucciones y estados `pending`; no mantiene un registro propio de etapas, ni enlaza aprobación visual/auditoría con un estado concreto del modelo. Los recibos de comandos existen, pero no equivalen a un ejecutor reanudable de entregas. | Registro persistente por entrega/perfil/documento, estados por etapa, detección de cambios y reanudación sin duplicar; cualquier cambio invalida la aprobación correspondiente. No convertir la auditoría en permiso global de escritura. |
| P1 | PDF verifica existencia, páginas y hashes, pero configura únicamente `Combine` y `FileName`. El tamaño/orientación/escala de impresión no es un contrato explícito del perfil. | Perfil de impresión con opciones compatibles por Revit; comprobar tamaño de página esperado y escala con fixtures conocidos; declarar lo que no se puede verificar semánticamente. |
| P1 | Las cajas de anotación son conservadoras, el límite usa el recorte del modelo y no hay solución tipada completa de rutas de líderes ni posición arbitraria del texto de cada segmento de cota. | Casos densos y recorte de anotación; operaciones de ajuste explícitas, medidas y reversibles. No confundir ausencia de solapamiento de cajas con legibilidad aprobada. |
| P1 | `view_scale` en `ManageViewsCommand` se admite en secciones, elevaciones, duplicación y aplicación de plantilla, pero no en creación directa de plantas, techos, estructuras o drafting. Existen otras rutas de edición, pero la experiencia de creación no es homogénea. | Selección y relectura de escala en todas las vistas escalables, con rechazo explícito de vistas no escalables o controladas por plantilla. |
| P1 | El perfil de entrega valida su estructura principal, pero varios errores de los argumentos internos solo se descubren cuando se ejecuta la etapa correspondiente. | Preflight completo del perfil: campos cerrados, familias/tipos disponibles, referencias, destino de las vistas, reglas y rutas de salida, antes de empezar a producir. Mantener nuevas comprobaciones de estado en cada etapa. |
| P0 de publicación | Un candidato `-dirty` sirve para desarrollo, no satisface la política de publicación del repo. La matriz debe corresponder a bytes identificados, y el instalador debe llevar esos mismos bytes. | Congelar un candidato, ejecutar la matriz de los años soportados y verificar paquete/instalación/contrato. No sumar ejecuciones de builds distintos como una única aprobación. |

## Alcance de la campaña

- Revit 2025 con la plantilla del usuario permanece fuera de alcance.
- Revit 2026 usa `scripts/live/dev-addin-session.ps1`: copia separada del add-in,
  manifiesto temporal y servidor de desarrollo; no reemplaza los DLL estables.
- `HZ_WRITE` y `HZ_LIVE_B` son fixtures desechables declarados en la configuración
  local de pruebas. No guardar ni sincronizar los cambios de ensayo.
- La configuración original de Revit 2026 se restaura al finalizar la sesión.
- Evidencias locales en `artifacts/release-validation-2026-09-07/`.

## Política recomendada para esta entrega

Cerrar estos requisitos como un único bloque, congelar alcance y pasar una fase
de estabilización. Las nuevas ideas quedan para el siguiente bloque. Solo una
corrección crítica de seguridad o pérdida de datos justifica interrumpirlo con
un parche urgente; no emitir una versión por cada capacidad básica añadida.

Este documento no promete que todo esté probado: el resultado final de la
campaña debe distinguir fallos del producto, fallos del ensayo, fixtures
ausentes y comprobaciones aún no ejecutadas.

## Hallazgos medidos y correcciones de esta campaña

- Candidato inicial en Revit 2026: 238 pruebas pasaron, 2 fallaron y 1 quedó
  sin verificar, sobre 241. El rechazo por árbol de desarrollo sucio es un
  requisito de publicación, no una avería del puente.
- La prueba de etiquetas encontró una cota de elevación de categoría oculta
  sin caja legible. La inspección diagnóstica por Python informó
  `category_hidden=true`, `in_visible_collector=false` y caja nula; es evidencia
  del script, no verificación del host. `AnnotationLayout` ahora excluye solo
  ocultaciones explícitas; las cajas desconocidas potencialmente visibles
  siguen bloqueando la operación.
- El exportador nativo produjo `combined.pdf.pdf`: `PDFExportOptions.FileName`
  debe recibir el nombre sin extensión. Corregido el argumento, conservando
  la verificación estricta de la ruta y los archivos esperados.
- La siguiente ejecución produjo el PDF correcto, pero rechazó el manifiesto:
  el parseo de Json.NET convertía la fecha ISO de cadena a token de fecha y
  `DeepEquals` encontraba una diferencia artificial. La relectura ahora
  compara el documento serializado exacto. Dos nuevas pruebas cubren fechas,
  Unicode y la negativa a sobrescribir sin permiso.
- El ensayo de entregables también necesitó correcciones propias: una geometría
  de drafting demasiado grande, lectura incorrecta de la forma `min/max` y
  recuento que confundía tablas de revisiones del rótulo con viewports creados.
  Esos intentos fallidos se conservan; no se cuentan como defectos del producto
  ni se borran para presentar una campaña verde.
- Inventario regenerado y contrastado: 80 herramientas, 197 operaciones y
  730 variantes. Corregido el aislamiento de una prueba que dejaba un
  directorio de evidencia vacío y contaminaba el control siguiente.
- El benchmark ahora comprueba el hash de la DLL que Revit realmente cargó,
  no el de una ruta de instalación que podría pertenecer a otro candidato.

La repetición del candidato corregido debe registrarse aparte: compartir
`1.2.1` y el sufijo `-dirty` no demuestra igualdad de bytes.

La segunda regresión general registró 239 aprobadas, 3 fallidas y 1 sin verificar
(243 comprobaciones), más 1 requisito no cubierto. Dos fallos corresponden a
publicación: árbol sucio y comparación del hash contra la DLL **instalada**,
que intencionalmente no fue reemplazada por la DLL aislada de desarrollo.
El fallo funcional restante del ensayo de etiquetas ya no es el obstáculo
oculto: el plan se genera, pero la etiqueta provisional de la familia vacía
no ofrece una caja legible y se revierte. Esto no certifica el etiquetado:
se añadió un ensayo aparte con una familia existente y legible del fixture.
Las evidencias completas del plan y del rechazo se conservan en
`tag-plan-fixed.json` y `tag-dry-fixed.json` dentro de la carpeta local de campaña.

El arnés general se mejoró para conservar el rechazo completo de etiquetas y
serializar evidencia con profundidad 40; sus informes anteriores no se
reescribieron ni se atribuyen a esa revisión posterior del arnés.

## Resultado del último candidato

DLL aislada de Revit 2026:
`fc549d620367b1160cf65a3fb7c7b374c56a74a41753514edbf11ee1c03b7bfb`.
No es la DLL estable instalada. La regresión general completa anterior
corresponde a otro hash; no se presenta como aprobación integral de este.

- Core: **3.800/3.800**; Server: **477/477**, sin omisiones.
- Compilación contra las API de Revit 2023, 2024, 2025, 2026 y 2027:
  cero errores y advertencias. Solo 2026 tuvo pruebas en vivo en esta campaña.
- Ensayo específico final: **13 aprobados, 1 fallido, 1 no cubierto**.
  Pasaron cotas de 6.000 mm a 1:50/100/200, rechazo de roles duplicados,
  desbordamiento sin viewports parciales, reparto entre dos planos,
  PDF combinado/separado, páginas, manifiestos, hashes, protección contra
  sobrescritura y captura nativa de ambos planos.
- El fallo pendiente es el autoetiquetado: `SpaceTag` 1455410 sin caja, no
  oculto explícitamente, pero ausente del colector visible según el diagnóstico
  Python. Esa información es del script; no se eleva a garantía del host.
- Se revisaron después las capturas. **No se aprueban como entrega visual**:
  el número largo desborda su casilla y la geometría sintética mínima no prueba
  composición de planos reales. Revisión local en `visual-review.md`.
  La revisión visual de las páginas PDF renderizadas sigue pendiente.
- Evidencia del ensayo: `deliverable-production-20260907215515-cc3b6e97.json`.

## Benchmark de consultas (no ranking frente a Cortex)

Treinta consultas: diez por modo, orden rotatorio, caché omitida, mismo fixture
y DLL final; **1.053 conductos**, primeras 100 filas en full/compact.

| Modo | Mediana de bytes | Mediana comando | Mediana total cliente |
| --- | ---: | ---: | ---: |
| full | 32.920 | 99 ms | 717 ms |
| compact | 15.072 | 90 ms | 692 ms |
| summary | 1.146 | 68 ms | 655 ms |

Los totales y resúmenes permanecieron iguales. El ahorro de datos de summary
fue 96,5%; la reducción de tiempo total fue 8,6% en esta muestra, no una
aceleración universal. No se midieron tokens, escritura, geometría ni Cortex.
El primer intento con muros estaba vacío y **no vale como evidencia de
rendimiento**, aunque la revisión anterior del script lo marcó aprobado.
El arnés ahora rechaza poblaciones vacías. Resultado válido:
`query-benchmark-ducts/benchmark.json` en la carpeta local de campaña.

**Decisión: mantener el candidato sin publicar.** Cerrar los requisitos de
salida en un único bloque, congelar un candidato limpio y después ejecutar la
matriz completa y la verificación del instalador sobre los mismos bytes.

## Cierre de la sesión

Los fixtures se cerraron descartando los cambios de ensayo. El manifiesto de
desarrollo se retiró y se restauró la instalación estable. `horizun_health`
confirmó `healthy`, versión 1.2.1, commit limpio `5498141`, DLL instalada
`fe792a0d8fcd2e7b095991059e5fbdf39e0f05542595aff5ee78f2e3cf9da214`
y cero documentos abiertos en Revit 2026. La sesión del usuario en Revit 2025
no fue modificada. Evidencia local: `restored-health.json`.
No se publicó, versionó ni instaló permanentemente el candidato.
