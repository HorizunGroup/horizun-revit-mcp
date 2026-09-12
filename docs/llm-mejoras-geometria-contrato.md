# Encargo para un LLM: geometría y contrato de Horizun Revit MCP

Actúa como desarrollador senior de Revit API, C# y protocolos MCP. Trabaja en el repositorio el checkout local de Horizun Revit MCP, o en su checkout equivalente. Implementa mejoras verificables de fidelidad geométrica, seguridad de ensayo y precisión del contrato. Empieza a trabajar en código; un diagnóstico o un plan por sí solos no completan el encargo.

## Contexto y alcance

Las observaciones provienen de sesiones con House-Plan, Revit 2024 y el add-in 1.2.1. Una de las sesiones registró 25 llamadas a Python, 9 a herramientas tipadas, 12 capturas y un reinicio de Revit. Son evidencia reportada por el usuario, no una reproducción en el checkout actual. Comprueba cada hallazgo antes de atribuirlo a la versión actual. Si ya está resuelto, conserva la solución y demuestra su cobertura con una regresión pertinente.

Problema principal: un elemento puede existir y pertenecer a la categoría correcta, pero tener una cota, posición, dimensión o pendiente distinta de la solicitada. La ausencia de advertencias de Revit tampoco demuestra fidelidad al plano.

Preserva lo que funciona: identificación de instancia y documento, selección tras reinicio, idempotencia, reversión, detección de transacciones abiertas, comprobación de cascadas al borrar, guardado con evidencia del archivo y SHA-256, consultas agrupadas y registro de diálogos. Mantén la neutralidad organizacional: los estándares y referencias de un proyecto son datos de entrada.

## Reglas de trabajo

1. Lee `AGENTS.md` y las instrucciones aplicables antes de editar. Inspecciona Git y conserva los cambios ajenos. No sincronices ni escribas en el Core compartido ni publiques aportes de memoria: este encargo es de desarrollo local.
2. Puedes modificar código, documentación y pruebas, y compilar. No instales ni reemplaces binarios cargados, cierres aplicaciones, guardes modelos de producción, hagas push ni publiques una versión como consecuencia implícita de este encargo. Sigue el procedimiento del repositorio cuando se autorice una instalación.
3. Antes de cualquier llamada al puente, usa `horizun_health` y comprueba instancia, versión y documento. Para pruebas que escriban, usa únicamente fixtures desechables identificadas para ello. Si no hay una disponible, prepara la prueba y declara pendiente su ejecución; no uses el documento activo como fixture por defecto.
4. No habilites Python ni cambies permisos por tu cuenta. Preserva `target_document`, idempotencia, confirmaciones y reglas de fallback. Decide el fallback por su bloque estructurado, no por el texto del error.
5. Consulta la API y la documentación oficial de la versión correspondiente cuando haya dudas. No generalices entre sobrecargas ni entre versiones de Revit.
6. Divide el trabajo en incrementos pequeños con criterios de aceptación. Resuelve las decisiones rutinarias sin pedir confirmación. Ante una ambigüedad que afecte compatibilidad o modelos reales, prepara las alternativas concretas y continúa el trabajo independiente.

## Entrada al repositorio

Estos son puntos de partida verificados al redactar el encargo; sigue el flujo completo de ejecución y localiza los módulos adicionales que hagan falta:

- `src/Horizun.Contracts/Contract.cs`: contrato compartido y esquemas publicados. Evita inventar otro catálogo paralelo.
- `src/Horizun.Server/Tools.cs` y `PipeClient.cs`: herramientas y transporte del servidor.
- `src/Horizun.Revit/Commands/DocumentSessionCommand.cs`: abrir, guardar y cerrar.
- `src/Horizun.Revit/Commands/CreateElementsCommand.cs`: planificación, creación y comprobaciones.
- `src/Horizun.Revit/Commands/ExecutePythonCommand.cs`: resolución de fuente, ejecución y evidencia.
- `src/Horizun.Revit/Commands/CaptureViewCommand.cs`: exportación de imágenes.
- `src/Horizun.Revit/Core/Dispatcher.cs`, `RequestGate.cs`, `Idempotency.cs`, `DurableCommandIdempotency.cs`, `PostconditionCheck.cs` y `ApplicationOutcome.cs`.
- `src/Horizun.Revit/Transport/PipeServer.cs` y `PipeEnvelope.cs`.
- `tests/Horizun.Core.Tests`, `tests/Horizun.Server.Tests`, `scripts/verify-live.ps1`, `scripts/verify-idempotency-live.ps1` y `docs/live-fixtures.example.json`.

En la revisión inicial se encontró que la descripción de `code_path` distingue el hash de la fuente de la identidad durable basada en la petición que contiene la ruta. También existen comprobaciones parciales de perfiles y categorías. Investiga y amplía esas implementaciones; no des por ausente todo el trabajo previo.

## Primera entrega: cerrar los fallos de mayor impacto

### P0.1 — `dry_run` nunca puede guardar

Reproduce `document_session(operation="save_as", dry_run=true)` y revisa también `save`, `open` y `close`. Cada operación debe admitir el ensayo con semántica documentada o rechazarlo explícitamente antes de producir efectos. Un campo aceptado no puede ignorarse.

Para guardar, el ensayo solo valida y describe: no llames a `Save` ni `SaveAs`. Una transacción revertida no deshace la escritura de un archivo. Separa las variantes del esquema por operación y valida las combinaciones también en el handler. Conserva compatibilidad deliberadamente y documenta cualquier cambio de valores predeterminados.

Aceptación: una prueba demuestra que el ensayo no crea ni reemplaza archivos, no cambia `Document.PathName` y no llama al guardado. Cubre destino nuevo, destino existente y argumentos incompatibles. Prueba por separado el guardado real en una fixture temporal y conserva su verificación del archivo.

### P0.2 — Verificar lo solicitado, no solo la existencia

Define una matriz por categoría con argumentos admitidos, unidades, restricciones, forma de aplicación y lectura de comprobación. Rechaza antes de escribir cualquier argumento desconocido, incompatible o que no pueda aplicarse; no lo descartes silenciosamente.

Comprueba nivel, desfase, posición, altura, dimensiones, orientación, anfitrión y pendiente cuando formen parte de la solicitud. Relee parámetros y geometría relevantes del modelo; no construyas la evidencia copiando los valores de entrada. Distingue punto de inserción, plano de referencia y límites geométricos: una caja envolvente por sí sola no verifica todos esos valores.

La evidencia por propiedad debe expresar valor solicitado, valor efectivo leído, unidad, tolerancia, método y resultado. Las tolerancias deben corresponder a la magnitud y a la precisión de Revit; no ensancharse para ocultar un fallo. Un resultado no comprobable queda explícitamente sin verificar.

Integra las comprobaciones con el contrato existente de resultados y transacciones. No devuelvas `verified_applied` si falla una postcondición solicitada. Diseña la reversión del lote cuando sea posible, manteniendo la relectura posterior al commit. Si ya hubo un commit irreversible o una reversión falló, informa cambios y estado real: nunca declares rollback por intención. No reintentes automáticamente una escritura parcialmente aplicada.

Aceptación: el cielo solicitado a **9′1⅛″ = 9.09375 ft** se mide respecto del nivel definido y no se acepta a 8 ft. Añade casos negativos que prueben que detectar el ID o la categoría correcta no basta para declarar éxito.

### P0.3 — Semántica explícita de Z

Introduce un modo explícito para colocación: coordenadas absolutas o desfase respecto del nivel. Define también el sistema de referencia de las coordenadas absolutas; no confundas origen interno, de proyecto y compartido. Identifica contradicciones entre Z, nivel y desfase antes de escribir.

Normaliza cada ruta de API según su comportamiento real para familias libres, basadas en nivel y alojadas. Devuelve cota absoluta efectiva, elevación del nivel, desfase efectivo y referencia usada. El anfitrión no autoriza a ignorar una coordenada solicitada: aplica una regla documentada o rechaza la combinación.

Aceptación: familia en un nivel distinto de cero sin sumar dos veces la elevación; reproduce el caso reportado de aproximadamente 20.65 ft frente a 10.32 ft. Usa valores exactos y un punto de referencia definido en la fixture; las cifras redondeadas del relato no son la tolerancia del test. Define cómo migrar clientes antiguos sin cambiar silenciosamente el significado de Z.

### P0.4 — Idempotencia de Python ligada a la fuente ejecutada

Incluye el contenido resuelto de `code_path` en la identidad de ejecución antes de reclamar la clave durable. Resuelve la fuente una vez y ejecuta esa misma instantánea para evitar cambios entre lectura, hash y ejecución. Define si el hash cubre bytes originales o texto normalizado y publica esa distinción.

Misma clave y misma fuente: replay fiel, sin ejecutar. Misma clave y fuente diferente: conflicto estructurado sin ejecutar, con hash anterior y nuevo; nunca ejecutar como trabajo nuevo bajo la misma clave. Considera documento, argumentos relevantes y, cuando existan, includes y versión de helpers. Conserva claves, recibos y resultados durables anteriores con una política de compatibilidad explícita.

Los trabajos encolados deben ejecutar la instantánea admitida al enviarlos, aunque después cambie el archivo. Cubre carrera entre envíos, reinicio y replay. Un recibo antiguo sin hash suficiente no puede fingir igualdad de contenido.

Aceptación: pruebas de replay intacto, edición de archivo con clave repetida, nueva clave, modificación después de encolar y reinicio del servidor. Si el archivo ya no puede leerse al pedir replay, documenta y prueba una respuesta determinista que no ejecute nada ni afirme haber comparado una fuente inaccesible.

## Segunda entrega: contratos y diagnóstico

### P1.1 — Perfiles correctos

Corrige el esquema a **contornos → puntos → coordenadas**. Por ejemplo, con unidades métricas declaradas en el campo que corresponda al contrato:

```json
{
  "profile": [
    [[0,0,0],[10,0,0],[10,8,0],[0,8,0]],
    [[3,3,0],[3,5,0],[5,5,0],[5,3,0]]
  ]
}
```

El ejemplo ilustra la forma de `profile`; completa una llamada ejecutable con tipo, nivel y demás campos requeridos. Define si el cierre es implícito o explícito y cómo se trata un último punto repetido. Valida coordenadas finitas, unidades, puntos repetidos, segmentos demasiado cortos, coplanaridad, autointersecciones, contención de huecos y contacto/cruce entre contornos. Distingue restricciones por categoría y versión. Añade ejemplos válidos con y sin huecos y errores con ruta precisa al dato.

### P1.2 — Errores estructurados de extremo a extremo

Mantén un error serializable desde Revit hasta `structuredContent` y el texto compatible del cliente. Incluye código estable, herramienta, operación, argumento/índice, tipo y mensaje de excepción, identificador de correlación, fase y estado de transacción, `write_started`, cambios efectivos y estado de rollback. Distingue cero cambios de cambios desconocidos. Incluye diálogos y fallos de Revit cuando existan.

No serialices directamente objetos de Revit ni excepciones con referencias complejas. Un fallo secundario de serialización debe conservar al menos una envoltura mínima con la causa original. Prueba el recorrido handler → pipe → servidor → respuesta MCP, incluyendo excepciones antes de escribir, durante commit y durante rollback.

### P1.3 — Validar argumentos no equivale a ensayar en Revit

Expón claramente la profundidad de validación: estática, ensayo real revertido o aplicación verificada. Un `preflight` que solo compila Python no demuestra que la API vaya a aceptar la operación.

Para operaciones compatibles, ensaya provisionalmente con las transacciones apropiadas y comprueba la reversión. Para tipos compuestos y Toposolid, reproduce el fallo `EndCap`; hereda valores compatibles del origen y valida las invariantes del conjunto. No inventes una regla global de `EndCap` sin comprobar la API de cada versión.

Aceptación: una composición incompatible falla antes de aplicar o durante un ensayo explícitamente revertido. No quedan tipos ni elementos provisionales y se reportan efectos residuales si la API impide una restauración completa. Nunca uses este mecanismo para simular que un guardado en disco es reversible.

## Tercera entrega: reducir la necesidad de scripts

Prioriza después de estabilizar P0 y P1:

1. Cubiertas por huella con pendiente por arista, identificación inequívoca de bordes y bordes sin pendiente para hastiales. Distingue relación subida/avance, grados y radianes. La unidad admitida por una sobrecarga no se extrapola a otra.
2. Muros con perfil y `top_level`, `top_offset`, `base_offset`; valida compatibilidad de restricciones y conserva cotas mundiales del perfil cuando Revit normalice su base. Añade control de uniones de muros con verificación geométrica.
3. Escaleras con tramos y descansos, alturas de niveles, número de contrahuellas, huellas y continuidad comprobadas.
4. Duplicar y editar tipos arquitectónicos: muros, pisos, cielos, cubiertas y símbolos de puerta/ventana con dimensiones. Respeta parámetros de tipo/instancia, fórmulas y solo lectura. Verifica que duplicar y editar no altera el tipo origen.
5. Conjuntos de desplazamiento para vistas explotadas; verifica membresía y desplazamiento sin confundirlos con mover elementos físicos.
6. Mapa `parameters` por elemento en `create_elements`, aplicado y comprobado dentro del lote. Valida almacenamiento, unidades, alcance, disponibilidad y escritura. No presupongas que un vano de `NewOpening` tiene `Comments`.

Para cada capacidad añade contrato breve, ejemplo ejecutable, restricciones por versión, ensayo, prueba de geometría y comportamiento de fallo. No declares cobertura por tener un wrapper que no verifica el resultado.

## Cuarta entrega: capturas, Python y trazabilidad

### Capturas para comparar geometría

- Añade encuadre por IDs, margen y orientación, con ocultación temporal de categorías, crop y estilo visual.
- Restaura la vista y sus propiedades incluso tras errores; el mecanismo de restauración debe verificarse y no depender solo de un `finally` que también podría fallar. Informa cualquier cambio residual.
- Devuelve las dimensiones reales del PNG y una correspondencia modelo–vista–píxel verificable: origen, ejes, transformaciones, límites efectivos, orientación vertical de píxel y región rasterizada. Comprueba con puntos de control, no solo con el crop box solicitado.
- Para vistas ortográficas puede corresponder una transformación afín. En perspectiva no prometas un único factor píxeles/unidad: devuelve los datos de proyección necesarios o declara que esa conversión no está disponible.
- Verifica que anotaciones o márgenes de exportación no invaliden la correspondencia ni dominen el encuadre.

### Python: ergonomía y evidencia honesta

- Soporta rutas largas con las capacidades reales del host, especialmente .NET Framework 4.8, o introduce `scripts_root` y rutas relativas bien definidas. Prueba una ruta de más de 260 caracteres sin cambiar globalmente políticas del sistema de forma implícita.
- Diseña `includes` ordenados y helpers versionados: transacciones compatibles con el contexto, `report()` de evidencia y wrappers de parámetros de salida. Inclúyelos en límites de tamaño, permisos e identidad de ejecución. No introduzcas una segunda transacción incompatible con la del host.
- El puente puede re-resolver `created_ids` y comprobar existencia/categoría. Esa comprobación es parcial: no certifica geometría, autoría del ID ni todas las acciones de un script arbitrario. Conserva los estados autorreportados y `host_verified=false` del contrato Python actual; publica las comprobaciones del host por separado y con alcance explícito.
- Captura advertencias antes/después y, cuando sea técnicamente observable, los fallos emitidos durante la operación. Separa advertencias persistentes, nuevas, resueltas, suprimidas observadas y cobertura desconocida. Un preprocesador que borra warnings puede impedir reconstruirlos: un delta vacío no demuestra que nunca se produjeron.
- Añade salida compacta sin omitir errores ni evidencia crítica. Implementa límites con truncamiento explícito, conteos y acceso al resultado completo cuando corresponda; conserva JSON válido. Mide la reducción respecto de las notas repetidas reportadas de unos 2,500 caracteres por llamada.
- Documenta el runtime detectado por versión y el uso de parámetros de salida. Verifica el caso reportado de IronPython 3.4 sobre .NET 4.8 y ausencia de `execfile`, sin asumir que describe todos los hosts.
- Amplía los avisos de alternativa tipada a `Wall.Create`, `Floor.Create` y `NewOpening`, evitando falsos positivos en comentarios y cadenas.

### Estado durante calentamiento

Investiga la primera llamada de unos 118 segundos tras reiniciar Revit. Distingue conexión, espera en cola, calentamiento, ejecución y bloqueo conocido. Usa progreso/Tasks/notificaciones compatibles con el protocolo negociado, sin escribir texto libre en stdout MCP ni ejecutar llamadas concurrentes a la API de Revit. No inventes porcentajes ni declares `healthy` si todavía no se verificó el estado requerido.

### Trazabilidad PDF → BIM

Permite referencias externas por elemento: documento y revisión/hash, página, región o cota, valor y unidad, método de medición, supuesto, confianza y referencia geométrica que se comparará. Usa una identidad estable dentro del documento, con asociación a `UniqueId` cuando proceda; no dependas solo de `ElementId` ni de `Comments`.

Compara dimensiones del modelo con esas referencias y entrega discrepancias con tolerancia, evidencia numérica y captura útil. Distingue cotas expresas de medidas inferidas a escala. Los datos pueden vivir en un sidecar o mecanismo adecuado a la arquitectura; justifica la elección. No amplíes esta entrega a construir un sistema completo de OCR.

## Pruebas y regresiones obligatorias

Primero registra la línea base y separa fallos previos de los introducidos. Usa pruebas unitarias para reglas puras, de contrato para esquemas y transporte, y Revit real para colocación, geometría, transacciones y exportación. Un mock o una búsqueda de texto en el código no demuestran geometría correcta.

Fixtures mínimas:

1. Cielo a 9′1⅛″ sobre un nivel explícito.
2. Familia libre y familia alojada sobre nivel distinto de cero, con modos de Z bien definidos.
3. Piso con perímetro y hueco; perfiles autointersecantes y no coplanares rechazados.
4. Cubierta **8:12**, midiendo subida/avance = 8/12 y verificando qué unidad requiere la sobrecarga usada. Incluye dos bordes sin pendiente.
5. Muro con perfil elevado: vértices, base y cotas finales después de la normalización de Revit.
6. `save_as` en ensayo sin cambios en disco, y guardado real con archivo verificado.
7. Edición de `code_path` bajo la misma clave; replay tras reinicio; instantánea de trabajo encolado.
8. Tipo compuesto con la incompatibilidad `EndCap` reproducida y diagnóstico correcto.
9. Excepción de Revit preservada en la respuesta MCP; fallo de serialización y rollback.
10. Captura con anotaciones lejanas, transformación contrastada y restauración tras fallo.
11. Advertencias persistentes y suprimidas, con límites de observación declarados.

Los errores de uso de API reportados por el usuario —pendiente en unidad equivocada y normalización de base del perfil— son regresiones útiles, no defectos demostrados del puente.

El repositorio fija el SDK en `global.json`; al redactarse este encargo era 10.0.400. Usa la configuración vigente. Comandos de referencia:

```powershell
dotnet test tests/Horizun.Core.Tests
dotnet test tests/Horizun.Server.Tests
dotnet build src/Horizun.Server -c Release
dotnet build src/Horizun.Revit -c Release -p:RevitYear=2024
```

Compila el add-in únicamente contra APIs instaladas y declara cuáles probaste. Revit 2024 es prioritario por la evidencia original; conserva compatibilidad con las versiones soportadas por el repositorio. Revisa las opciones de los scripts de verificación antes de ejecutarlos y aliméntalos con fixtures adecuadas. No equipares compilación con validación en Revit.

## Ejecución y entregables

Empieza ahora:

1. Registra commit/versión, estado del árbol y pruebas de línea base.
2. Construye una matriz breve: hallazgo, evidencia actual, archivo responsable, estado —reproducido, ya cubierto o pendiente— y prueba de aceptación.
3. Implementa primero P0.1 y P0.2; incorpora P0.3 al resolver la colocación y completa P0.4. Evita ampliar capacidades antes de cerrar la veracidad de los resultados existentes.
4. Continúa por P1 y las entregas posteriores según dependencias. Si una validación requiere Revit o una fixture ausente, deja el harness listo y avanza sobre trabajo independiente.
5. Mantén actualizado un documento de seguimiento con cambios, pruebas realizadas, resultados, restricciones y siguiente paso concreto.

Entrega código y pruebas, contratos y ejemplos actualizados, y evidencia de las versiones comprobadas. Explica cambios incompatibles y migración. Resume al final qué quedó implementado, qué se verificó en Revit real, qué solo se probó de forma automatizada y qué sigue pendiente. No declares completado todo el backlog por haber terminado la primera entrega; no declares verificación si falta evidencia.
