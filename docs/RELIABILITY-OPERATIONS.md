# Operaciones de confiabilidad Q90

Implementación verificada con código, contratos y pruebas sin abrir Revit. La
compilación contra las API no demuestra el comportamiento en un modelo abierto en Revit.
El procedimiento de lotes está en [DOCUMENT-BATCH.md](DOCUMENT-BATCH.md).

Las escrituras nuevas ensayan por defecto. Para aplicar se repiten los argumentos
del ensayo con `dry_run=false`, su `confirmation_token` y un `idempotency_key`
nuevo. Solo un reintento de respuesta perdida conserva la misma clave. Las
identidades de elementos existentes siguen las guardas generales de `unique_id`.
Los esquemas completos se consultan en `horizun://contract/tools/{tool}`.

## Salir del proceso propio

`horizun_document_session operation=exit_application` exige `expected_pid`,
`expected_started_utc` y `owner_session`. Antes de iniciar **su propio** Revit,
el responsable establece `HORIZUN_OWNER_SESSION` a un GUID impredecible en el
entorno de ese proceso y conserva el GUID, PID y hora de inicio UTC. El add-in
captura el marcador al registrarse. Una llamada no puede reclamar un proceso ya
abierto sin marcador. Es prueba de pertenencia a la sesión, no autenticación
frente a otro programa con los mismos derechos de usuario.

El ensayo enumera documentos, estado modificado, trabajo pendiente y fingerprint.
Los modificados, ilegibles o con trabajo pendiente deben guardarse antes, o
autorizarse expresamente por fingerprint en `discard_documents` y ensayarse de
nuevo. La confirmación incluye la revisión del observador de ciclo de vida:
editar, cerrar o reabrir entre ensayo y aplicación exige otro ensayo. La salida
no guarda ni sincroniza. Antes de cada cierre, incluso después de activar otra
vista, coteja el documento con el estado aprobado. Solo solicita el descarte
para los fingerprints autorizados. Una edición posterior a la confirmación
impide el descarte del documento que la recibió. Si el puente no puede
identificar ese documento, conserva los documentos que requerían descarte.
Cuando rechaza un cierre, `close_results` conserva los cierres previos y la
fila rechazada indica `close_started:false`. Si ya cambió el documento activo,
la fila incluye `activation` y la respuesta indica `write_started:true`.
Si completa los cierres, solicita el comando nativo `ExitRevit`.

`exit_queued=true` y `termination_verified=false` significan solicitud encolada.
El cliente debe comprobar externamente, con plazo acotado, la desaparición del
PID y de su hora de inicio exactos. Si no termina, el resultado sigue pendiente;
no debe declararse recarga del add-in. Un fallo después de cerrar algún documento
conserva esa evidencia: el cierre de una sesión no es una transacción reversible.

## Informe gráfico de varias vistas

`horizun_view_visibility operation=graphics_report` recibe `view_ids` (1–200).
Es de solo lectura y devuelve una fila independiente por vista: plantilla,
parámetros controlados, filtros en orden, visibilidad, habilitación y todas las
propiedades soportadas de `OverrideGraphicSettings`, más categorías ocultas o
sobrescritas. Cuando una plantilla controla el grupo se lee la fuente efectiva.

`matching_model_elements` cuenta coincidencias del filtro en el modelo anfitrión;
`matching_view_collector_elements` las cruza con el colector nativo por vista.
Recorte, fase y otras reglas pueden afectar lo finalmente dibujado. No se cuenta
el contenido de vínculos ni se afirma visibilidad final. Un error de lectura se
informa como desconocido. Si falla la lectura de una categoría, la fila conserva
el aviso en `category_warnings` y tanto la vista como el lote devuelven
`complete=false`.

## Copiar gráficos

`horizun_view_visibility operation=copy_view_graphics` recibe `view_id` como origen,
`view_ids` e `include`: `filters`, `categories`, `elements`. Si se omite `include`,
usa filtros y categorías y lo informa como SUPUESTO. El origen puede leerse sin
marca propia; los destinos deben ser propios, independientes y del mismo tipo.
Se rechazan grupos pedidos que estén controlados por plantilla del destino.

La transferencia usa una plantilla temporal limitada a los grupos elegidos,
elimina esa plantilla y verifica el estado gráfico y la plantilla original de
cada destino. `elements` copia únicamente elementos de modelo comunes; admite
`element_ids` explícitos y un máximo de 2.000, sin copiar anotaciones propias de otra vista.
Las dependencias entre vistas se rechazan para evitar cambios fuera de la lista.
La confirmación cubre las identidades de las vistas y de cada elemento que
aparece en `element_ids`. Si cambia un `unique_id`, hace falta otro ensayo
aunque los gráficos y el id numérico sigan iguales.

En un plan de edición de visibilidad, las operaciones `hide`, `unhide`,
`set_overrides`, `clear_overrides` y `hide_foreign_markers` cotejan los elementos
resueltos antes de escribir. Si al aplicar se resuelve un conjunto distinto del
aprobado, la operación devuelve `stale_plan` y el plan revierte su grupo.
Al mostrar una instancia de vínculo, `unhide_targets` en el ensayo incluye
el tipo y, si el tipo estaba oculto, las otras instancias que deben seguir
ocultas. La confirmación cubre sus identidades y su estado de ocultación.
La relectura compara la ocultación individual en la vista del anfitrión.

## Reemplazar aparatos conectados

Reemplazar aparatos conectados (`horizun_plumbing_fixtures`) está en la edición Pro.

## Malla JSON

`horizun_export format=mesh_json` recibe `output_path` nuevo, `categories` con
nombres `OST_*`, `include_links` y `skip_directshape`. Por defecto toma categorías
de modelo con cantidades de material del anfitrión; excluye habitaciones, espacios,
áreas, líneas y contenedores de vínculos, omite vínculos y excluye DirectShape; informa los valores
omitidos como SUPUESTO. La carpeta debe existir y no se reemplazan archivos.

`triangles` es una matriz plana de nueve números por triángulo; `triangle_count`
es su cantidad. Las coordenadas están en metros, sistema interno del anfitrión.
`provenance` contiene categoría, documento, `element_id`, `unique_id`, ruta de
instancias de vínculo e intervalo de triángulos. Compone transformaciones de
vínculos anidados y corrige el orden de vértices en transformaciones reflejadas.

Se reutiliza `HostSolidMesh`, incluidos objetos Mesh para esta exportación. La
teselación nativa con detalle Fine no aporta un error de cuerda medido. Vínculos
sin cargar, geometrías ilegibles, ciclos y límites aparecen en `omitted` y dejan
`complete=false`. Esto no es prueba de cobertura total para una nube de puntos.

El ensayo calcula el SHA-256 sin crear archivo. La aplicación confirma la misma
geometría, escribe un temporal nuevo, relee hash, estructura, números finitos y
cantidad de coordenadas, y mueve al destino nuevo. Relee el hash final. Si falla,
informa cualquier archivo nuevo que haya quedado; no borra archivos existentes.
Las respuestas de éxito y de fallo tras usar los valores predeterminados incluyen
`has_assumptions` y `assumptions` en el nivel superior.

La exportación recorre los miembros de los grupos una vez por elemento. Procesa
escaleras y barandas antes de sus partes. En la selección predeterminada incluye
sus tramos, descansos, soportes y pasamanos reconocidos. De cada parte recorta
las superficies coplanares ya exportadas por su padre aunque las diagonales
difieran. Usa una tolerancia de 10⁻⁹ m para la comparación geométrica. Las caras
exclusivas y los fragmentos que quedan después del recorte conservan la procedencia
de la parte. Dos elementos independientes con caras iguales conservan cada uno
su conteo y procedencia. La comparación se limita a cada instancia de vínculo.
Una jerarquía ilegible se informa en `omitted`.

## Rechazos de ensayo

`horizun_manage_views` comprueba los nombres de las vistas nuevas contra el
documento y contra las otras vistas del lote. Un conflicto devuelve
`precondicion_no_cumplida`, `view_name_conflict`, el nombre y el índice de la
acción, sin token de confirmación. Las hojas conservan su control por número.

`horizun_create_elements` devuelve `esquema_invalido` cuando una fila usa un
campo desconocido. La respuesta da `field_path`, el recurso del contrato y
`suggested_field` cuando identifica una alternativa. En un lote, el primer
diagnóstico estructurado aparece también en el nivel superior y `errors`
conserva los diagnósticos por fila. Por ejemplo, `elements[1].start_point` en
un muro sugiere `start`. Si otra fila tiene una brecha de capacidad, la respuesta
conserva `capability_gaps` y mantiene `fallback.allowed=false` para el lote.

## Cambiar curva liberando uniones

`horizun_transform_elements`, elemento con `operation=set_curve` y
`release_end_joins=true`, admite esta opción en muros. La omisión equivale a false
y se informa como SUPUESTO. Guarda los dos estados de unión, los libera, cambia
la curva, regenera y restaura los permisos anteriores y relee con qué muros quedó unido. Comprueba
curva, elevación, permisos y vecinos unidos después. La geometría de los vecinos
entra en el plan y se relee. El token cubre los `unique_id` de los vecinos unidos
a cada extremo. Si cambia la unión, pide otro ensayo antes de escribir.
Una diferencia solo de uniones se informa aparte
de `curve_mismatches`. Falta medir en Revit los muros que vuelven a su largo. Si las uniones restauradas alteran la curva,
revierte en lugar de afirmar éxito. No extiende esta opción a otras categorías.


### Preparar un proceso propio para exit_application

`scripts/dev-addin-session.ps1` prepara los manifiestos; **no lanza Revit**.
El siguiente ejemplo documenta el lanzamiento para quien ejecute ese banco;
no forma parte de una compilación ni debe ejecutarse para probar código puro:

```powershell
$launchOwner = [guid]::NewGuid().ToString('N')
$env:HORIZUN_OWNER_SESSION = $launchOwner
$ownedRevit = Start-Process -FilePath 'C:\Program Files\Autodesk\Revit 2025\Revit.exe' -WindowStyle Hidden -PassThru
$launchRecord = @{ owner_session=$launchOwner; expected_pid=$ownedRevit.Id; expected_started_utc=$ownedRevit.StartTime.ToUniversalTime().ToString('O') }
Remove-Item Env:HORIZUN_OWNER_SESSION
```

Se tolera una diferencia de **hasta 1 segundo** en `expected_started_utc`.
Un rechazo por PID/hora devuelve `pid` y `started_utc` observados, sin cerrar
nada. Un marcador distinto sigue rechazado. No se puede atribuir un proceso
preexistente a la tarea. El cierre avisa sobre documentos colaborativos y
posibles préstamos. Desde `exit_queued=true` se congela el dispatcher y las
solicitudes pendientes o nuevas se rechazan con `Revit is exiting`.
