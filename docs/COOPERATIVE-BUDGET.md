# Presupuesto cooperativo para las lecturas pesadas (4.2, LP-P1-2)

Estas cinco lecturas no pueden ceder el hilo de Revit entre tramos (cada paso nativo es indivisible; el clash cooperativo que sí lo cede está en la edición Pro), pero sí pueden **parar entre pasos y decir exactamente qué midieron**.

## Qué es, y qué no es

- **Opt-in.** El argumento `cooperative` es el mismo objeto en las cinco herramientas:
  `{ ui_budget_ms (1000-600000, 20000 por defecto), max_units, cursor }`.
  Sin él, la herramienta se comporta como siempre: no se abre ningún alcance, no se agrega ningún campo a la respuesta.
- **Acota lo que gasta, no demuestra fluidez.** `ui_budget_ms` limita cuánto retiene el comando el hilo de UI. Nada medido
  desde dentro del proceso prueba que la ventana de Revit quedó fluida; eso lo mide un muestreador externo (ver Escenario).
- **Una llamada nativa indivisible no se interrumpe.** El presupuesto se consulta entre unidades (elementos, secciones,
  chequeos, pasadas). Una unidad que ya empezó termina. Por eso la respuesta declara su `stop_grain`.
- **`max_units` cuenta la unidad de cada lector**: elementos en `quantities`, `query_model` y `model_scan` (censos);
  chequeos en `audit_model`; ítems de una pasada en `query_planimetry`. Ahora lo respeta el alcance en todos los lectores
  (antes solo `query_model` y `list_elements` lo pasaban explícitamente; `model_scan` y la precomprobación IDS lo ignoraban).

## La respuesta parcial

Cuando la lectura se detuvo (`ui_budget_exhausted`, `max_units_exhausted` o `client_abandoned`):

| Campo | Significado |
|---|---|
| `partial: true`, `coverage_complete: false`, `partial_reason` | La respuesta no es la medición del modelo. |
| `cooperative.complete: false`, `stopped_because`, `units_done`, `elapsed_ms` | Lo que dice el alcance. |
| `cooperative.measured_account.measured` / `not_measured` | Qué se midió y qué no, por nombre o por rango. |
| `cooperative.measured_account.reading_rule` | «Lo no medido nunca se miró: no es vacío ni limpio». |
| `cooperative.cursor` | Solo donde un cursor es una continuación real (ver abajo). |
| `partial_note` | Frase para citar junto a cualquier número de la respuesta. |

Nunca se afirma nada sobre lo no medido: ni «0», ni «limpio», ni «sin hallazgos».

## Por herramienta

### `horizun_model_scan`
Ya tenía presupuesto en los dos bucles de censo. Ahora además **la compuerta antes de cada sección**: cuando el presupuesto se agotó,
las secciones restantes no empiezan y salen con `status: "not_measured"` (sin cubos, nunca «ok» ni «limpio»), listadas en
`sections_not_measured`. Una sección que ya empezó corre hasta el final (es una pasada nativa), así que la parada está acotada a
**una sección**. `sections_ok` ya no las cuenta. Los presupuestos y cursores propios de cada sección no cambian.
Sin cursor cooperativo: el escaneo pagina con su `cursor` de sección.

### `horizun_query_model`
Sin cambios de comportamiento (ya tenía presupuesto, `max_units` y cursor atado a solicitud y estado del modelo). Se agregan
las pruebas de contrato de este documento. Un parcial se pagina con su cursor sobre lo recolectado; no reanuda lo no examinado
(`collection_continuation_available: false`): se vuelve a correr con un selector más estrecho o un presupuesto mayor.

### `horizun_audit_model`
La unidad es el **chequeo** (17-18, cada uno una pasada nativa). La compuerta va antes de cada chequeo. Un chequeo que no corrió entra en
`checks_failed` con `state: "not_run"` y su motivo: la puerta de requisitos, la puerta de prevención y el índice de salud ya tratan
`checks_failed` como «desconocido, no limpio», así que **un audit parcial no puede leerse como aprobado por ninguna ruta**.
No se guarda `store_snapshot` (entraría a la tendencia como si el modelo hubiera mejorado). `cooperative.cursor` es la posición del primer
chequeo sin correr: se reenvía **la misma solicitud** con el cursor y corre el resto (el estado del modelo se verifica; si cambió, se rechaza).
Cada página es parcial por sí sola (`partial_reason: checks_of_other_pages_are_not_in_this_reply`); se unen por `finding_id`.

### `horizun_quantities`
Modos `volume` y `takeoff`. Con `cooperative` los elementos se recorren en **orden de id** (en `takeoff`: orden de entrada de alcance y luego id),
de modo que una posición conserve su significado. La respuesta es **una página, no el modelo**: totales, `by_code`, cobertura y titular
cubren solo el rango de `measured_account.measured`; el rango anterior y el posterior salen en `not_measured`. `cooperative.cursor`
continúa en la posición donde paró; las páginas se suman por quien las pidió. En `takeoff`, la respuesta parcial lleva `truncated: true`
y `partial: true` y **`horizun_budget_compare` la rechaza** a propósito (comparar una página es comparar un edificio más pequeño).
`rows_file` no se combina con `cooperative` (ese archivo es justo lo que se entrega a `model_rows_path`). Los modos `room_finishes` y `carbon`
**rechazan** `cooperative` con el nombre del modo, en lugar de aceptarlo e ignorarlo.

### `horizun_query_planimetry`
Las pasadas de recolección (vistas, hojas, ubicaciones, anotaciones por tipo, referencias, cobertura de etiquetas) preguntan entre ítems y
antes de empezar. Los totales del censo son conteos exactos del colector tomados **antes** de cualquier pasada con presupuesto. La respuesta
dice qué pasadas terminaron, cuáles se cortaron a mitad (con cuántos ítems leyeron) y cuáles no empezaron. `annotation_overlaps` con un
parcial devuelve `overlap_report.status = "not_assessable"` (un veredicto de solapes sobre anotaciones nunca recolectadas leería «no se solapan»).
El `next_cursor` propio solo pagina las filas recolectadas; `cooperative.cursor` se rechaza (hay un solo cursor por herramienta).
Para medir el resto: presupuesto mayor o `sheet_ids`/`view_ids` más estrechos.

## Escenario en vivo (por escribir tiempos; no se corrió en Revit)

Modelo desechable SINTÉTICO, sin cliente: un host de ensayo con ~20 000 elementos (muros, vigas, ductos como cajas), 300 hojas y 1 500 vistas con
anotaciones sintéticas, 3 worksets abiertos. Reloj externo (Stopwatch del cliente) y un muestreador de 1 Hz que intenta `horizun_health`.
Revit 2024 y 2027.

1. **Línea base sin `cooperative`** de cada herramienta (`model_scan` completo; `query_model` categoría de 5 000; `audit_model`; `quantities` volume de 5 000 vigas;
   `query_planimetry` mode annotations). Anotar tiempo total y que la respuesta **no trae** `cooperative` ni `partial`.
2. **Mismo pedido con `cooperative: {ui_budget_ms: 5000}`**. Esperado: cada una vuelve en ~5 s + la unidad en curso. Anotar tiempo real, `stopped_because`,
   `units_done`, y el tamaño de la unidad que pasó el límite (sobrepaso máximo por herramienta: chequeo más largo del audit, sección más larga del scan).
3. **Contenido del parcial**: comprobar que `measured_account` coincide con la realidad (audit: los chequeos de `findings` == `measured`; quantities: la suma de dos páginas
   == el total sin presupuesto; planimetry: `items_read` == filas de la población).
4. **Cursor**: audit y quantities, reenviar con `cooperative.cursor` y la misma solicitud hasta `partial:false`; las páginas se unen sin huecos ni repetidos.
   Mover un muro entre páginas: el cursor se rechaza («estado del modelo»).
5. **max_units**: `max_units: 50` en `quantities` y en `model_scan`; verificar que paran en 50 (antes `model_scan` lo ignoraba).
6. **Cancelación**: cerrar el cliente a los 3 s; el comando debe parar con `client_abandoned` en la siguiente unidad (no hay respuesta que ver; comprobar en el registro del job y que el hilo se libera).
7. **Fluidez**: el muestreador externo, durante 2: latencia máxima de `horizun_health` entre llamadas. La herramienta no la garantiza; el dato decide si el límite por defecto es útil.
Registrar tiempos de 1 y 2 por herramienta y versión.
