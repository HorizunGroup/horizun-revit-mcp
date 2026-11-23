# Lotes tipados de documentos

`horizun_document_session` con `operation=batch` procesa de 1 a 100 archivos
locales cerrados. Cada modelo es independiente: abre con los guardas existentes,
ejecuta `reads`, ensaya/aplica `actions` mediante `horizun_execute_plan` y cierra.
Una falla no impide procesar el siguiente modelo. El resultado global falla si
algún modelo falla y conserva todas las filas, también las exitosas.

```json
{
  "operation": "batch",
  "models": [
    {
      "file_path": "D:/trabajo/copia.rvt",
      "expected_version": "2025",
      "detach": true,
      "reads": [
        {"tool": "horizun_model_scan", "arguments": {"sections": ["document"]}}
      ],
      "close": "discard"
    }
  ]
}
```

El ensayo es el valor predeterminado: **abre y activa documentos**, ejecuta las
lecturas y ensaya los cambios; no aplica acciones ni guarda archivos. Cierra
solo lo que abrió. Puede dejar abierto el proyecto vacío que el puente utiliza
para poder cerrar el último documento activo. No usa inyección de teclado ni
mouse. Una apertura que requiere un diálogo conserva la política de cancelación
del guardián de apertura.

Si al menos un modelo pudo ensayarse y cerrarse, devuelve un
`confirmation_token`. En un resultado parcial se encuentra en
`structuredContent` junto a las filas rechazadas. Para aplicar, repite exactamente
`models`, agrega `dry_run=false`, el token y un `idempotency_key` nuevo.
El token es de un solo uso, expira y cubre la petición ordenada, el año del
anfitrión, la selección de filas aprobadas, su huella de la fuente y la huella
del plan resuelto cuando hay acciones. La
aplicación salta las filas rechazadas en el ensayo con
`skipped=not_approved_in_rehearsal`. Si cambia una fuente aprobada, falla esa
fila y se requiere otro ensayo para incluirla.
La idempotencia del dispatcher cubre el lote completo: conserva la misma clave
solo para reintentar una respuesta perdida, nunca para una ejecución distinta.

`actions` usa el mismo formato, herramientas permitidas y referencias a pasos
anteriores de `horizun_execute_plan`: `key`, `tool`, `arguments`. Sus guardas de
identidad se ejecutan contra el documento recién abierto, por separado en cada
modelo. Las creaciones conservan el marcado y la relectura del comando tipado.
Las acciones de un modelo son atómicas; el lote completo no lo es.
Al reabrir el modelo, el lote compara el plan de acciones con el que aprobó el
usuario. Si cambia, la fila devuelve `stale_plan` antes de aplicar o guardar.
En `horizun_create_elements`, la huella incluye el SHA-256 del CSV de
`tabular_source` y el del LandXML cuando corresponde. La creación vuelve a
comparar esa huella antes de escribir dentro del plan atómico.

`reads` admite herramientas de solo lectura y operaciones de lectura de
`MutatingUnlessDryRun` enumeradas en `IdentityCoverage.ReadOperations`, como
`read`, `override_report` y `graphics_report` de `horizun_view_visibility`.
Siempre consulta `Settings.IsToolAllowed`; un `dry_run` no convierte una
escritura en lectura. `target_document` se fija al
modelo abierto. Los resultados se conservan completos; si una lectura devuelve
cobertura parcial o `resume`, esa evidencia sigue siendo parcial, no una
auditoría completa. Se ejecutan antes de `actions`, tanto en ensayo como en
aplicación. No se admiten scripts Python ni operaciones de sesión anidadas.

Los archivos colaborativos, centrales o locales de un central, se abren siempre
con `detach=true`. Si se omite, se informa como SUPUESTO con override `detach`;
`detach=false` explícito rechaza esa fila y continúa. El lote no fuerza
`force_workshared`, no toma préstamos ni sincroniza. Si Revit devolviera un
colaborativo conectado, rechaza sus acciones y guardado, avisa e intenta liberar
lo que posee el usuario actual, sin sincronizar. `force_read_only_on_workshared`
sigue impidiendo las escrituras cuando está activado.

`close=discard` es el valor predeterminado. `close=save` admite dos opciones:

- Con `save_as_path`, escribe un archivo nuevo local de la misma extensión.
- Sin `save_as_path`, esa fila debe contener explícitamente `close:"save"`.
  Guarda la fuente con `doc.Save()` si no es colaborativa; una copia desacoplada
  se guarda por SaveAs como central nuevo, con `SaveAsCentral=true` y la ruta de
  la fuente. Nunca guarda un colaborativo conectado.

Guardar sobre la fuente requiere un SHA-256 legible durante el ensayo y que
sea el mismo al aplicar antes de abrir y justo antes de guardar. Los destinos
nuevos deben ser inexistentes y tener rutas distintas de las fuentes.
`source_verified` indica por modelo si se comprobó el SHA-256. Al guardar en
otra ruta pueden coincidir dos lecturas fallidas y el lote puede continuar.
En ese caso la fila devuelve `source_verified: false` y
`source_verification_error` con la etapa y las causas de lectura.
El guardado relee archivo, versión, ruta y `IsModified`.
Si la comprobación espacial de las acciones aplicadas detecta un conflicto o queda incompleta,
la fila devuelve `state=spatial_check_failed` y conserva el hallazgo en
`steps_apply.data.spatial_check`. El lote omite el guardado, intenta cerrar el
documento descartando sus cambios y continúa con el siguiente modelo.
Un archivo escrito antes de otro fallo puede permanecer; la evidencia lo dice.
No hay atomicidad de archivos ni restauración de una fuente ya guardada.

`copy_view_graphics` y `replace_family` se admiten como acciones a través de
`horizun_execute_plan` y `ModelEditRunner`, con sus guardas, ensayo y token.
Un fallo de una acción revierte el grupo de ese modelo.

Para **11 o más modelos**, usa `horizun_submit_job` con `tool` igual a `horizun_document_session` y `arguments` con el batch confirmado. Usa la forma simple
`tool` + `arguments`; `sequence` no admite batch.
Así la ejecución tiene un registro durable y no depende del timeout de la llamada.
El ensayo también puede enviarse como trabajo. El token y los SHA-256 conservan
su vigencia habitual: si expiran mientras se espera, hace falta otro ensayo.
La opción `models` de `submit_job` ya expande un barrido de solo lectura a
abrir/auditar/cerrar por modelo, sin guardar ni sincronizar, y se detiene en el
primer paso fallido (con limpieza de cierres). El batch admite acciones y
guardado por fila, sigue después de fallos y liga las fuentes y los planes resueltos al token.

Los documentos ya abiertos se rechazan por modelo y nunca se toman prestados ni
se cierran. Los fallos de cierre se informan como `cleanup_error`; no se declara
éxito del modelo. Los valores omitidos de año esperado (año del anfitrión,
siempre contrastado con el archivo) y cierre se señalan como `SUPUESTO` en cada
fila. No se habilitan permisos ni se modifican ajustes del dueño.

Validación de esta implementación: código y pruebas sin ejecutar Revit. Antes de
usarla en producción hay que medir la matriz de casos del informe de entrega.

Los supuestos aparecen arriba de la respuesta y en cada fila afectada como
`has_assumptions:true`, `assumptions:[{flag:"SUPUESTO",what,why,override}]`.
Sin supuestos no se emite una lista vacía.
