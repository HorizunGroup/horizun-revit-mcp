# Geometría y contratos de Horizun Revit MCP

Implementación local del backlog de House-Plan. Los hallazgos facilitados describen
Revit 2024/add-in 1.2.1; este árbol partía del commit `522e6c7`, versión fuente 0.9.6.
Son antecedentes de reproducción, no una identificación del binario actualmente instalado.

La reproducción inicial se hizo en la línea 0.9.6 (48 herramientas), mediante
manifiestos de desarrollo reversibles. La candidata 1.3.0 integra esos cambios
sobre `codex/bim-production-product-layer` (`0fd4493`, 79 herramientas). La
instalación de uso diario se conserva mientras se valida el par integrado.

La campaña anterior pasó once casos en Revit 2024 y once en Revit 2026. En 2026
se añadieron sección calibrada, negativos de captura, uniones de muros,
discrepancia de referencia con rollback y trazabilidad persistente. Se aplicaron
los casos de geometría, se guardó la copia y se reabrió; los IDs consultados y la
referencia almacenada siguieron presentes. Esto no sustituye medir otra vez cada
sólido después de reabrir. Ambos manifiestos de desarrollo quedaron restaurados.

En la integración pasaron 3909 pruebas de núcleo y 496 de servidor, más 14
regresiones nuevas de compatibilidad. Es evidencia de código; la matriz Revit
del instalador definitivo sigue pendiente. Los informes anteriores identifican
su propio build y no se presentan como validación de los binarios 1.3.0.

Evidencia nueva: `save_as` con ensayo preservó el destino centinela; cielo a
9.09375 pies y losa con hueco a 10.32 pies pasaron construcción/relectura/reversión.
La calibración de captura pasó tres comprobaciones independientes y restauró la
vista. Una escalera recta sobre nivel 10.32 pasó. El muro de perfil pasó tras
fijar explícitamente `WALL_BASE_OFFSET`; moverlo por bounding box era insuficiente.
La cubierta necesita inicializar el `ModelCurveArray` de salida; se sustituyó la
lectura de referencias de caras (una era huérfana) por caras físicas del sólido,
con comprobación adicional del área proyectada. El descanso conecta ambos tramos;
su contorno se compara por cobertura bidireccional de segmentos, porque Revit
divide aristas al conectar. Ambos casos pasaron después de estas correcciones.
Los JSON conservan tanto los fallos originales como los resultados corregidos.

Se reprodujo una espera de 111821 ms antes de una escritura: la retención
desactivada inventariaba 26462 registros de idempotencia. La ruta de reclamación
ahora omite ese inventario cuando ambos límites son cero. El replay del recibo
se verificó en 516 ms sin ejecutar de nuevo. Núcleo actualizado: 934 pruebas pasan;
servidor: 371. Total: 1305. Las cinco compilaciones 2023–2027 siguen sin errores
ni advertencias.

Aceptación 2024: once casos de construcción/relectura/reversión y captura,
en `artifacts/geometry-live-20260912/acceptance-2024/summary.json` (nueve) y
`extras-2024/summary.json` (dos), ambos `passed=true`. Incluye familias libres
y alojadas en un nivel elevado, puerta duplicada con ancho/alto, composición
Toposolid, vano y desplazamiento que conserva geometría física. El ensayo de
`save_as` preservó archivo centinela y RVT. Se verificaron además aplicación,
replay y reapertura de los elementos auxiliares de la copia; esto no equivale
a aplicar y reabrir cada uno de los once casos. Se rechazó `height` en un cielo.

La sesión de 2024 terminó y su manifiesto original fue restaurado por hash.
La sesión aislada de 2026 quedó preparada con una copia independiente, pero
su arranque se detuvo en el aviso de editor no verificado de un add-in externo.
Se requiere intervención del usuario en ese aviso; no se cambió su confianza.
Python continúa deshabilitado por el dueño: no se ejecutaron pruebas Python
en el host ni se modificaron esos permisos.

## Estado y alcance

| Área | Cambio implementado | Límite de la evidencia |
| --- | --- | --- |
| Geometría creada | Relectura de nivel, coordenadas, desfase, orientación, perfiles, caras y pendientes; comparación con intención y tolerancias; reversión del grupo ante discrepancias | Compilación en 2023–2027; once casos en Revit 2024 aprobados. Contraste 2026 pendiente |
| `document_session` | Esquemas por operación; `save`/`save_as` con ensayo sin llamar a Save; `open` rechaza `dry_run=true`; argumentos inaplicables rechazados | El ensayo de guardado valida argumentos/documento, no prueba permisos de escritura ni aceptación de Save |
| Familias | `coordinate_mode` obligatorio; posicionamiento y lectura de Z absoluta, elevación del nivel y desfase | Ruta puntual libre/basada en nivel o alojada en muro compatible. No pretende cubrir caras, planos de trabajo o todas las familias adaptativas |
| Perfiles | Contornos → puntos → XYZ; cierre implícito o último punto repetido; validación de coplanaridad, intersecciones, huecos y aristas cortas | Rectas; un contorno para cubierta y muro de perfil; huecos en pisos/cielos |
| Tipos | Duplicación de tipos de sistema y `FamilySymbol`; parámetros comparados contra valores calculados antes de escribir; origen releído e intacto | Escribir ancho/alto verifica el parámetro; no demuestra que una familia mal construida responda físicamente a él |
| Toposolid/composición | Herencia de EndCap y envolvimiento cuando se omiten; ensayo opcional con construcción y reversión | Toposolid requiere una versión que contenga esa API, desde Revit 2024 |
| Idempotencia Python | Congela el código y los includes antes de reclamar la clave; mismo contenido reproduce el recibo; contenido diferente entra en conflicto | Los hashes se calculan sobre el texto decodificado/normalizado, no sobre bytes originales con BOM o CRLF |
| Python | Rutas largas, `scripts_root`, includes ordenados y helpers v1; respuesta compacta; salida estructurada grande preservada en archivo temporal verificado | No modifica permisos de Python; sigue siendo ejecución autorreportada |
| Observaciones Python | Re-resuelve `created_ids`, existencia/categoría y delta de advertencias persistentes | No certifica autoría ni geometría. Advertencias eliminadas por un preprocesador tienen cobertura desconocida |
| Errores/transporte | Excepción original y estado de transacción conservados al serializar; fallos de pipe incluyen correlación, fase y certeza de envío | Una respuesta perdida no prueba rollback; cambios/estado se devuelven desconocidos |
| Arquitectura tipada | Pendiente por borde; muro de perfil; vano rectangular; escaleras rectas con descansos explícitos; desplazamientos; uniones de muros; mapa de parámetros por instancia | Casos de cubierta, perfil, vano, escalera y desplazamiento aprobados en 2024. Uniones de muros todavía sin campaña específica |
| Captura | Encuadre por elementos y restauración; calibración opcional mediante seis marcadores en un PNG separado, ajuste con tres y comprobación independiente con tres | Planos y secciones con crop rectangular no dividido. Tolerancia 1.5 píxeles; comparación del fondo entre exportaciones. Calibración y restauración aprobadas en planta de Revit 2024; secciones y negativas aún pendientes |
| PDF → BIM | Referencia por elemento en ExtensibleStorage, consulta optativa y comparación de cotas contra mediciones del modelo | No extrae ni interpreta automáticamente el PDF; el llamador proporciona página, referencia, medición y supuesto |
| Contrato compacto/progreso | Estado de inicialización cold/warming/ready/failed en health; canal autenticado de estado fuera del hilo UI; heartbeat con observación de cola/ejecución | El cliente necesita progressToken para recibir progreso. Una consulta fallida se informa como no observada; una solicitud ausente puede haber terminado, nunca se supone cancelada |

Se preservan el destino explícito, el consentimiento de Python, la idempotencia
durable, la confirmación de planes, las transacciones, la comprobación de cascadas
al borrar y la lectura/verificación del archivo guardado. Los binarios de uso
diario se conservan; se activaron manifiestos temporales de desarrollo y se
guardó únicamente la copia de prueba. No se sincronizó el Core.

## Migración del contrato

- Servidor y add-in de pruebas deben pertenecer al mismo árbol. El hash del
  contrato cambia; `health` lo devuelve explícitamente. Conservar la instalación
  de 79 herramientas y usar la sesión de desarrollo; una integración de ramas
  y publicación es un trabajo distinto a sustituir un manifiesto para probar.
- Los argumentos de otra categoría/operación se rechazan aunque valgan cero o false.
- Familias puntuales y columnas requieren `coordinate_mode: "absolute"` o
  `"level_offset"`. XYZ usa el origen interno; el modo relativo cambia solamente Z.
  El nivel se mide mediante `Level.ProjectElevation`.
- Muros convencionales mantienen `height` obligatorio. Con `top_level_id` y
  `top_offset`, la altura debe concordar con la cota superior. `offset` y
  `base_offset` son alternativas, y deben concordar con la Z absoluta de la base.
- `profile` siempre tiene tres niveles de arrays, incluso sin huecos. Z expresa
  la cota absoluta de referencia; `offset`, si se envía, debe ser consistente.
- `slope_ratio` es subida/avance: una cubierta 8:12 usa `0.6666666666666666`.
  `slope_degrees` se convierte a esa relación. Son mutuamente excluyentes con
  `edge_slopes`, que sigue el orden de aristas del perímetro.
- `dry_run` de creación y tipos sigue siendo true por defecto. La profundidad
  predeterminada `validation_mode: "arguments"` no construye elementos.
  `"revit_rollback"` construye, confirma una transacción interna, mide y revierte
  el grupo completo. Sus IDs son provisionales: no deben reutilizarse.
- Una aplicación conserva la secuencia ensayo/token/idempotency_key. Una lectura
  fallida después de la asimilación se informa como cambio cometido sin verificación
  completa; nunca como rollback ficticio.
- `source_reference.measurements[].property` debe nombrar una poscondición numérica
  disponible, por ejemplo `reference_face_elevation`, `height`, `start_z` o `slope_0`.
  Una propiedad ausente o con unidades incompatibles se considera no medida.
- La respuesta Python compacta elimina prosa fija, pero conserva errores y cobertura.
  `host_verified` sigue siendo false. `host_observations` es evidencia limitada e
  independiente; nunca eleva el estado a verificación geométrica del host.
- `max_output_chars` limita `__output__`, no el tamaño total de todos los metadatos
  de la respuesta. La salida demasiado grande se retira del cuerpo, se marca y se
  devuelve una ruta si fue posible preservar y releer el JSON completo.
- Los conflictos Python incluyen hashes del paquete ejecutable (código principal,
  includes ordenados, versión de helpers). Consultar `hash_scope` y los campos
  `previous_execution_sha256`/`submitted_execution_sha256`; los campos históricos
  `*_source_sha256` del conflicto se mantienen como alias.

## Ejemplos

Los IDs y el documento siguientes son ilustrativos. Deben resolverse en la copia
de prueba mediante herramientas de consulta antes de enviar el comando.

Piso con perímetro y hueco, coordenadas en milímetros:

```json
{
  "target_document": "GeometryFixture",
  "units": "mm",
  "dry_run": true,
  "validation_mode": "revit_rollback",
  "elements": [{
    "kind": "floor", "level_id": 100,
    "profile": [
      [[0,0,0],[6000,0,0],[6000,4000,0],[0,4000,0]],
      [[2000,1000,0],[2000,2000,0],[3000,2000,0],[3000,1000,0]]
    ]
  }]
}
```

Cielo solicitado a **9′1⅛″ = 9.09375 pies** y cubierta a dos aguas 8:12:

```json
{
  "target_document": "GeometryFixture",
  "units": "feet",
  "dry_run": true,
  "validation_mode": "revit_rollback",
  "elements": [
    {"kind":"ceiling","level_id":100,
     "profile":[[[0,0,9.09375],[20,0,9.09375],[20,12,9.09375],[0,12,9.09375]]]},
    {"kind":"roof","level_id":100,
     "profile":[[[0,0,12],[20,0,12],[20,12,12],[0,12,12]]],
     "edge_slopes":[
       {"defines_slope":true,"slope_ratio":0.6666666666666666},
       {"defines_slope":false},
       {"defines_slope":true,"slope_ratio":0.6666666666666666},
       {"defines_slope":false}]}
  ]
}
```

Familia sobre un nivel cuya elevación interna es 10.32 pies:

```json
{
  "target_document":"GeometryFixture","units":"feet","dry_run":true,
  "validation_mode":"revit_rollback",
  "elements":[{"kind":"family_instance","type_id":200,"level_id":101,
    "point":[4,4,0],"coordinate_mode":"level_offset",
    "parameters":{"Comments":"geometry-regression"}}]
}
```

Ese punto debe quedar en Z=10.32 pies. El equivalente absoluto es
`point:[4,4,10.32]` con `coordinate_mode:"absolute"`.

Python con helpers y código compartido:

```json
{
  "target_document":"GeometryFixture",
  "scripts_root":"C:\\HorizunTests\\scripts",
  "code_path":"main.py","includes":["geometry.py"],
  "response_mode":"compact","max_output_chars":16000,
  "idempotency_key":"UUID-nuevo-para-esta-operacion"
}
```

```python
with horizun.transaction("Fixture operation"):
    # Operación mínima y comprobaciones del script.
    pass
horizun.report("completed_unverified", created_ids=[])
# Para APIs con parámetros de salida: horizun.out_reference(TipoDotNet).
```

`includes` comparte el ámbito del script, se ejecuta en orden y participa en el
hash. Editar un include y reenviar la misma clave debe devolver conflicto.
IronPython y CLR se identifican en la respuesta; no se presupone `execfile`.

## Verificación reproducible

### Fase anterior sin interfaz (resultado histórico)

Resultado de esta continuación: **932/932 pruebas del núcleo y 371/371 del
servidor aprobadas (1303 en total)**. Servidor y add-in para los cinco años
2023–2027 compilaron con cero errores y advertencias. Los tres scripts modificados
pasaron el análisis sintáctico y `git diff --check` no encontró errores.
Como prueba negativa deliberada, se ejecutó el caso de inicialización sin el
runtime aislado: falló mostrando la causa real (falta .NET 8.0.30), el ejecutable
y el código de salida. La batería completa pasó después con el runtime aislado,
sin cambiar el pin ni la instalación global de .NET.

Se implementaron además:

- `calibrate_world_to_pixel:true`, con `hide_annotations:true`, para planos y
  secciones de crop rectangular activo, no dividido. Máximo 4096 píxeles por eje.
  Se entrega el PNG limpio y se conserva otro PNG de calibración con hashes,
  anclas mundiales/píxel, matriz afín 2×4, píxeles por pie, error de reproyección
  independiente y comparación del fondo. Las coordenadas de píxel comienzan en
  (0.5, 0.5), centro del primer píxel. La fase posterior validó la calibración en planta de Revit 2024.
- Estado de inicialización medido sin inicializar Python desde health. El canal
  `__horizun_request_status` exige el mismo token local que el resto del pipe y
  no entra a la cola UI. El servidor lo consulta durante llamadas con progreso.
  Las pruebas usan un pipe de simulación y verifican la correlación de solicitud.
- El arnés JSON-RPC drena stderr/stdout, acota la espera y reporta ejecutable,
  exit code y causa de arranque. La ausencia de .NET 8.0.30 deja de ocultarse detrás
  de una lista vacía de respuestas.
- Script de desarrollo con compilación por año, copia del par servidor/add-in, firma solo
  con certificado ya confiable, manifiesto temporal y ledger de hashes para
  restaurar. No reemplaza DLLs ni el servidor instalados. `hz-call.ps1` acepta
  `HORIZUN_SERVER_EXE`, conservando prioridad de su argumento explícito `-Server`.

En aquella fase el script de desarrollo y los arneses se prepararon sin ejecutarse
La fase final autorizada se registra al comienzo de este documento. La preferencia `enable_execute_python:false`
del propietario se mantiene: no se elude usando otra raíz de configuración.

### Resultado anterior

Resultado de esta ejecución: **927 pruebas del núcleo y 370 del servidor
aprobadas (1297 en total)**. Servidor y add-in para Revit **2023, 2024, 2025,
2026 y 2027** compilaron con cero errores y cero advertencias. `git diff --check`
no encontró errores de whitespace; el script PowerShell pasó el análisis sintáctico.
La consulta final de `horizun_health` respondió que no hay Revit accesible: la
batería de construcción en el host **no se ejecutó**.

Pruebas automatizadas:

```powershell
dotnet build src/Horizun.Server/Horizun.Server.csproj -c Release
dotnet test tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj -c Release
dotnet test tests/Horizun.Server.Tests/Horizun.Server.Tests.csproj -c Release
foreach ($revitTestYear in 2023..2027) {
    dotnet build src/Horizun.Revit/Horizun.Revit.csproj -c Release -p:RevitYear=$revitTestYear
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $revitTestYear" }
}
```

La prueba de transporte inicia el servidor real compilado. Este árbol fija el
runtime del servidor en .NET 8.0.30. En esta máquina se utilizó una copia aislada de
ese runtime desde la caché NuGet y variables `DOTNET_ROOT_X64`/`DOTNET_ROOT` del
proceso de pruebas; no se cambió el pin de release ni se instaló otro runtime global.

La batería adicional usa **solo un RVT desechable abierto expresamente**:

```powershell
& .\scripts\verify-geometry-live.ps1 `
  -Fixture C:\HorizunTests\geometry-fixture.json `
  -Server C:\ruta\verificada\horizun-mcp.exe `
  -EvidenceDirectory C:\HorizunTests\evidence-new-run
```

El manifiesto debe contener `disposable_fixture:true`, `document_path` absoluto,
`expected_revit_year`, `expected_addin_commit`, `expected_contract_hash`,
`expected_server_sha256` y `cases`. Cada caso contiene `name`, `tool` y `arguments`.
Las herramientas permitidas son creación, tipos y captura. El harness impone destino,
ensayo y reversión; sus argumentos no se duplican dentro de los casos.
El directorio de evidencia debe ser nuevo. Un error detiene la batería y conserva
solicitudes/respuestas y `summary.json`.

Ejemplo de caso dentro de `cases`:

```json
{"name":"cielo_9ft_1_1_8in","tool":"horizun_create_elements",
 "arguments":{"units":"feet","elements":[{"kind":"ceiling","level_id":100,
 "profile":[[[0,0,9.09375],[20,0,9.09375],[20,12,9.09375],[0,12,9.09375]]]}]}}
```

La batería comprueba primero `save_as + dry_run` contra un archivo centinela:
su hash y la ruta del documento deben permanecer iguales. Después ejecuta los
ensayos configurados y exige construcción real, medición y ausencia de IDs tras
rollback; en capturas exige restauración de la vista. Comprueba también el hash
del RVT en disco. Esto **no sustituye una prueba de aplicación persistente y
reapertura** ni una revisión visual del modelo.

Pendiente: repetir la campaña sobre el par integrado 1.3.0 y completar la
matriz del paquete definitivo para los años publicados. Python sigue requiriendo
el consentimiento del propietario; no se ha cambiado su configuración.
Cero advertencias no es una comprobación de fidelidad al plano.
