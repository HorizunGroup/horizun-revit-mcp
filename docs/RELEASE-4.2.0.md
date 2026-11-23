# Horizun Revit MCP 4.2.0

Esta versión mejora las consultas, los planos y la seguridad del trabajo con Revit 2023 a 2027.

- Las consultas grandes piden confirmación cuando corresponde y entregan resultados en páginas ajustadas a un presupuesto. La consulta de parámetros que cerraba Revit ya puede completarse por partes; el presupuesto no garantiza un límite de memoria total.
- Cuando no se encuentra el documento pedido, el error muestra los documentos abiertos. Algunos errores incluyen `suggested_call` con una llamada corregida.
- Las advertencias se consultan por páginas; la geometría tiene un resumen, se identifican vistas sin colocar y `return_fields` permite elegir campos.
- Las herramientas que trabajan sobre documentos admiten `target_document` para comprobar el destino. Las operaciones sobre elementos comprueban su identidad antes de cambiarlos.
- Se pueden preparar notas desde capas del modelo y desde cuadros. Las notas libres sin origen se informan como `untracked`; `clone_tag` avisa si el keynote está vacío o no se pudo leer.
- Se informa `is_modified` por documento cuando Revit permite leerlo. El servidor advierte si es anterior a la instalación y se limpian respaldos antiguos conservando los dos más recientes y los que están en uso.
- Se corrigió la activación de proyectos que aún no se han guardado; la verificación en Revit de este caso sigue pendiente.
- Hay mejoras para planos y PDF. La sincronización con el central presenta los cambios pendientes antes de confirmar.

Python sigue desactivado por defecto y requiere autorización del dueño del equipo. Consulta el registro de cambios para conocer las condiciones y los límites de cada función.
