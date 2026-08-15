# Retroalimentación de uso real — 2026-08-07

Una jornada auditando **123 modelos** con Horizun 0.8.0 contra Revit 2025 y 2026, en lote y sin
nadie al teclado. Todo lo de abajo se midió, no se supone. Ordenado por lo que costó tiempo de
verdad. Conservado verbatim como llegó; las historias que salen de aquí viven en
[BACKLOG.md](BACKLOG.md) (5.19–5.24) y el análisis contra el árbol está allá, no aquí.

---

## P1 — El timeout de 600 s con un diálogo modal. Costó 30 minutos

Un diálogo "New Project" quedó abierto y llamé `horizun_health` tres veces. Cada llamada esperó
10 minutos completos antes de rendirse. Lo grave es lo que dice el propio log:

```
'horizun_health' TIMED OUT after 600000 ms - Revit busy or on a modal dialog (it never started)
```

El comando nunca llegó a ejecutarse. El bridge ya sabía desde el primer segundo que estaba
encolado sin arrancar, y aun así tardó diez minutos en decirlo. Tres veces.

Tres arreglos, de menos a más ambicioso: darle a `horizun_health` un timeout propio y corto —es
el comando de diagnóstico, si no contesta rápido eso ya es la respuesta—; permitir `timeout_ms`
por llamada; y el bueno, detectar el modal y devolverlo como resultado en vez de como timeout:
"Revit tiene abierto `<título>`; nada se encoló". La pieza ya existe, porque
`horizun_open_document` enumera diálogos perfectamente.

## P2 — No se puede cerrar el documento activo

`The active document may not be closed from the API`. Me pasó dos veces. Al final del lote de 54
el último modelo quedó abierto, y eso importa: si relanzas el lote, ese modelo se salta.

El workaround funciona y debería estar dentro del MCP: abrir un documento ancla para que el otro
deje de ser el activo, y entonces `Close(False)` sí funciona. Propongo
`horizun_close_document(title, save=false)` que lo haga por dentro. Aviso para quien lo intente
por interfaz: Ctrl+W no sirve, cierra la vista, no el documento.

## P3 — Exige documento activo aunque el script no lo use

El triage de formatos solo lee `BasicFileInfo` de archivos en disco. No necesita ningún
documento. Aun así tuve que crear un proyecto en blanco por interfaz como ancla, porque
`horizun_open_document` no crea documentos ni acepta `.rte`.

Y de aquí sale la petición que más me ahorraría: `horizun_file_info(paths[])` tipado — leer
formato, `IsWorkshared` e `IsCentral` de una carpeta entera sin abrir nada. Es literalmente lo
primero que se hace en cualquier lote, y hoy hay que escribirlo a mano cada vez.

## P4 — La telemetría de diálogos es excelente… y está en un solo sitio

Fue lo que resolvió el caso del día. Los tres modelos que fallaban con un seco "Opening was
canceled" resultaron ser un `Dialog_Revit_DocWarnDialog` que el bridge cancela porque no hay
nadie al teclado, más los errores que Revit levantó antes. Sin ese bloque el diagnóstico era
imposible.

El problema es que el driver de lote abre con `app.OpenDocumentFile` desde `execute_python`, y
ahí ese detalle no aparece. Por eso los 4 modelos caídos de un mismo proyecto quedaron sin
diagnóstico. Exponer el mismo registro de avisos en `execute_python` cerraría el hueco.

## P5 — Poder contestar el diálogo de apertura

6 de 123 modelos no se pueden auditar desatendidos por esto. Cancelar por defecto es correcto;
lo que falta es un `on_open_dialog: cancel | dismiss` para lectura. Que un modelo no abra solo
sigue siendo un hallazgo — pero hoy ni siquiera se puede medir su calidad.

## P6 — El contrato de `__output__` se contradice

El texto del comando explica qué significa `self_reported_verified`, y cuando lo usas te avisa
de que "no es uno de verified|completed_unverified|partial|failed". Nombra un valor que después
rechaza. O se acepta o se quita del texto.

## P7 — Discovery huérfano

Al matar Revit quedó `revit-2025-21068.json` y el siguiente comando falló. El mensaje de error
era buenísimo y explicaba exactamente qué había pasado, pero el MCP ya comprueba `process_alive`
en `horizun_job_status`: podría barrer al arrancar los discovery cuyo pid ya no existe.

## Lo que funcionó y no hay que tocar

`run_async` con `horizun_job_status` movió 54 modelos en 7 minutos y 69 en unos 12 sin perder
uno. La guarda de versión es justo lo que la regla necesita: rehúsa abrir otra versión sin
`allow_upgrade`. Y la idempotencia hizo su trabajo — reenviar el lote devolvió el mismo `job_id`
sin duplicar nada.

Una corrección a mi propia sospecha: `open_all_worksets` no era la causa de los fallos de
apertura. Lo probé con `false` y fallan igual. La documentación dice que es lo primero que hay
que soltar cuando un modelo muere al abrir, lo cual es cierto en general, pero hoy me despistó.

También dos detalles de IronPython que vale documentar: `System` no viene importado aunque `clr`
sí, y `System.Array.CreateInstance` falla donde `System.Array[System.Byte](...)` funciona.
