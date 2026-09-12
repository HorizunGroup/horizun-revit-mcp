# Comparativa MCP para producción, contenido y diagnóstico de Revit

Fecha de consulta: 2026-09-07. Evaluación documental y de código; no es una carrera de rendimiento ejecutada entre productos.

## Dictamen

RevitCortex es un comparador principal pertinente: incorpora caché de consultas, estado de sesión, capacidades por documento y operaciones orientadas a tareas. Son decisiones concretas que pueden mejorar la experiencia del agente. Su superioridad global en rapidez, precisión o producción no queda demostrada sin ejecutar los mismos casos en Revit.

Horizun tiene una base diferenciada para autoría RFA y diagnóstico con cobertura explícita. Su oportunidad inmediata es reducir el trabajo del agente y el coste de las consultas, manteniendo sus garantías de verificación. El número de herramientas y el número de pruebas unitarias no determinan el ganador.

## Alcance y trazabilidad

Se revisaron siete candidatos: Horizun, RevitCortex, KenLP, Shuotao, BIMwright, Nonica y Autodesk. El estudio profundiza en código de Cortex y Horizun; para los demás, caracteriza la oferta documentada. Esta asimetría impide convertir la tabla en un ranking numérico homogéneo.

| Candidato | Referencia consultada | Evidencia usada |
| --- | --- | --- |
| Horizun | `5498141c46a68f2551ff68f8186836797c59af12`, árbol local sobre v1.2.1 | Código; pruebas ejecutadas en esta conversación; informes históricos separados |
| RevitCortex | `8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33` | Código de router, caché, capacidades, auditoría y documentación de habitaciones; README y flujos |
| KenLP | `af98c1c6c54876dfd75c3639ebad0eb9df974233` | README fijado a commit |
| Shuotao | `2faacc5f948e4588c8e2c48036550e834220afbb` | README fijado a commit |
| BIMwright | `a14b73614effdb20c3cd0c54180423d024631c6e` | README fijado a commit |
| Nonica | Web oficial consultada en la fecha del informe | Oferta del proveedor; sin inspección interna |
| Autodesk | Publicación oficial del MCP público y ayuda de Revit 2027 | Documentación del proveedor; sin inspección interna |

La página de releases de Cortex identifica [v1.0.50](https://github.com/LuDattilo/revitcortex-releases/releases/tag/v1.0.50). No se descargaron ni conciliaron sus binarios con el commit de código: son identidades distintas hasta demostrar esa correspondencia. Tampoco se certificó en esta revisión la instalación activa de Horizun.

## Comparación por objetivo

Las expresiones «referente» y «candidato» siguientes son juicio de selección para una prueba, no resultados medidos.

| Producto | Producción | Contenido | Diagnóstico | Papel en el benchmark |
| --- | --- | --- | --- | --- |
| Horizun | Lotes tipados, vistas/planos, anotación y verificación | Compilación RFT→RFA con parámetros, fórmulas, formas y relectura; tipos de sistema | Escaneo por secciones, cobertura, datos no leídos y auditorías configurables | Referencia local de garantías y profundidad técnica |
| Cortex | Flujos de habitaciones, planos y datos | Gestión de familias/tipos/materiales; reconstrucción IFC declarada | Salud, familias, advertencias y recomendaciones | Comparador principal de experiencia de uso y consultas repetidas |
| KenLP | Lotes atómicos, documentación y edición | Carga de familias y duplicación de tipos en la superficie descrita | Salud, worksets y recetas de coordinación | Comparador de operaciones acotadas y perfiles de herramientas |
| Shuotao | Biblioteca extensa de procedimientos BIM | Cobertura a contrastar por caso de autoría, sin equiparar SOP con ejecución | Procedimientos de control y cumplimiento | Referencia de conocimiento operativo entregado al agente |
| BIMwright | Toolsets y flujos compuestos | Gestión de familias en proyecto; excluye una suite completa de Family Editor | Lint, advertencias, auditorías y coordinación | Referencia de modularidad y extensión personal |
| Nonica | Edición y documentación en oferta Pro | No se acredita aquí un compilador paramétrico RFA | Lectura, análisis y reportes en oferta gratuita | Comparador de adopción y experiencia empaquetada |
| Autodesk | El anuncio del servidor público consultado se centra en lectura | Sin autoría RFA acreditada en ese anuncio | Consulta y comprensión del modelo | Referencia oficial para lectura; no sustituye una comparación de escritura |

Fuentes: [Cortex](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/README.md), [KenLP](https://github.com/KenLP/RevitMCPServer/blob/af98c1c6c54876dfd75c3639ebad0eb9df974233/README.md), [Shuotao](https://github.com/shuotao/REVIT_MCP_study/blob/2faacc5f948e4588c8e2c48036550e834220afbb/README.md), [BIMwright](https://github.com/bimwright/rvt-mcp/blob/a14b73614effdb20c3cd0c54180423d024631c6e/README.md), [Nonica](https://revitmcp.com/), [Autodesk](https://www.autodesk.com/blogs/aec/2026/06/17/revit-public-mcp-server/).

La [ayuda de Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-WhatsNew/files/GUID-97697CBF-0E11-484E-96E5-4277E3E8D61F.htm) también menciona edición de parámetros dentro del contexto de Autodesk Assistant. No se atribuye automáticamente esa capacidad al servidor público externo ni se declara que Autodesk carezca de ella en todos sus productos.

## Cortex frente a Horizun: hallazgos concretos

### Consultas repetidas y sesión

El router de Cortex comprueba la caché antes de enviar la ejecución al hilo de Revit. La caché distingue alcance de sesión/documento y versión del documento; un observador se suscribe a cambios, guardados y sincronización. Esto permite evitar trabajo repetido en llamadas compatibles. No se midieron aquí latencias, memoria ni todos los escenarios de invalidación.

Fuentes: [router](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Plugin/CortexRouter.cs#L195), [caché](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Core/Caching/ToolResultCache.cs), [observador](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Plugin/Caching/DocumentChangeWatcher.cs).

En Horizun, `QueryModelCommand` vuelve a recopilar resultados en cada petición. Incluso `summary` construye registros por elemento, ordena el conjunto y calcula la huella de resultados antes de quitar los campos de página. Hay margen para un acumulador de resumen y para lecturas de tipos/niveles reutilizadas dentro de la llamada. Una caché entre llamadas requiere además identidad de documento, cambios en vínculos, límites de memoria e invalidación comprobada. [Código local](../src/Horizun.Revit/Commands/QueryModelCommand.cs).

### Selección de herramientas y procedimientos

Cortex contiene una lista explícita de herramientas dinámicas para worksets, fases y vínculos. Su guía operativa diferencia consultas ligeras de auditorías completas y proporciona secuencias por objetivo. Eso es una ventaja de diseño para evaluar; las estimaciones de tokens publicadas por su autor no son mediciones realizadas aquí. [Capacidades](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Core/Discovery/DocumentCapabilities.cs), [flujos](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/WORKFLOWS.md).

Horizun ya tiene prompts, catálogo de flujos y control de permisos. La mejora consiste en guiar explícitamente la elección de respuesta y el presupuesto de lectura, no en crear otra biblioteca de herramientas equivalentes. Los perfiles de tarea deben complementar, y nunca sustituir, los permisos. [Catálogo](../src/Horizun.Server/McpWorkflowCatalog.cs), [prompts](../src/Horizun.Server/McpPrompts.cs).

### Producción de documentación

`workflow_room_documentation` de Cortex reúne selección de habitaciones, callouts y secciones en una operación. El código fija escala 1:50, elige determinados tipos/vistas y puede confirmar resultados parciales con lista de fallos. Es una ruta cómoda, pero no equivale a cumplir un estándar de documentación arbitrario. [Implementación](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Tools/Workflows/WorkflowRoomDocumentationTool.cs).

En el commit base, `WorkflowPlan` ofrecía tres intenciones: fijar elementos, aplicar plantilla y preparar planos. Horizun **ya tenía** un planificador de vistas por habitación en `horizun_plan_views`; la brecha era integrarlo en una intención atómica, no crear ese planificador desde cero. La implementación posterior lo conecta mediante `document_rooms`, con tipos, escalas, plantillas y colocaciones explícitas. Su aceptación viva sigue pendiente. [Planificador](../src/Horizun.Revit/Commands/PlanViewsCommand.cs), [estado de implementación](PRODUCTION-IMPROVEMENTS-2026-09-07.md).

### Creación de contenido

No se encontró en la superficie publicada de Cortex un equivalente tipado al compilador RFT→RFA de Horizun. Cargar, duplicar tipos o reconstruir instancias IFC no demuestra crear una familia paramétrica desde cero. Esto no prueba imposibilidad mediante código arbitrario o extensiones.

Horizun tiene creación de parámetros/fórmulas, geometría, conectores y verificación de familia reabierta, además de una opción de flexión por tipos. Su ventaja es de cobertura de código inspeccionada; la calidad geométrica, comportamiento paramétrico y coste de producir contenido comparable deben probarse. [CreateFamilyCommand](../src/Horizun.Revit/Commands/CreateFamilyCommand.cs).

### Diagnóstico: precisión además de un puntaje

En `CheckModelHealthTool`, Cortex clasifica habitaciones por área menor o igual a cero y las etiqueta como no colocadas; también cuenta `ImportInstance` sin separar `IsLinked`. Son limitaciones de clasificación observables en código, no una medición de falsos positivos sobre modelos reales. [Código](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Tools/Project/CheckModelHealthTool.cs).

Su flujo general de auditoría usa penalizaciones por umbrales fijos y ofrece un resultado breve con recomendaciones. Es útil para una revisión rápida, pero el puntaje por sí solo no acredita cobertura de un modelo federado ni cumplimiento de requisitos particulares. [WorkflowModelAuditTool](https://github.com/LuDattilo/RevitCortex/blob/8b2556daefb2bf88f0a7a17bf28f2352fe0b0e33/src/RevitCortex.Tools/Workflows/WorkflowModelAuditTool.cs).

Horizun distingue habitaciones no colocadas, no encerradas y lecturas fallidas; separa CAD importado/vinculado y publica cobertura. Su reto es presentar esa profundidad como una lista breve de acciones trazables, conservando IDs y detalles bajo demanda. [ModelScanCommand](../src/Horizun.Revit/Commands/ModelScanCommand.cs).

## Evidencia disponible y lo que no demuestra

En esta conversación pasaron 3.771 pruebas Core y 477 Server de Horizun. No se ejecutaron pruebas de los competidores ni una nueva matriz viva de Revit.

El informe local de optimización procede de `522e6c7` con cambios de trabajo, no del HEAD actual. En Revit 2026 registró una consulta de 1.053 coincidencias: full 937 ms / 29.884 bytes; summary 748 ms / 1.409 bytes. Son llamadas individuales, con transporte y orden fijo, sin serie aleatoria ni percentiles. No permiten afirmar una aceleración reproducible ni superioridad frente a Cortex. La evidencia histórica es un archivo local no distribuido (`artifacts/optimization/optimization-2026.json`); no es un artefacto público verificable de esta release.

El 115/115 de [BENCHMARK.md](BENCHMARK.md) es una valoración interna de capacidades y garantías. No se reutiliza como resultado competitivo. El documento también conserva afirmaciones contradictorias sobre la reproducción de P8 y evidencia pendiente; necesita una revisión editorial separada.

## Batería común propuesta: todavía no ejecutada

Una primera comparación Cortex/Horizun puede usar Revit 2026 y copias desechables idénticas. El resto entra donde exista soporte equivalente. La ausencia de una herramienta se registra como capacidad no cubierta; un fallo de instalación se registra aparte, sin inventar un tiempo de ejecución.

| Caso | Tarea | Condición de éxito independiente |
| --- | --- | --- |
| P1 | Crear diez juegos de documentación de habitaciones | Vistas, plantillas, escalas, nombres y colocaciones cumplen la especificación |
| P2 | Actualizar 1.000 parámetros con mezcla de entradas válidas e inválidas | Cada valor esperado se relee; fallos y política de parcialidad explícitos |
| P3 | Crear y exportar un juego de planos | Archivos abiertos/comprobados; vistas esperadas y ausencia de duplicados |
| P4 | Reintentar una escritura tras perder la respuesta | Ninguna duplicación; estado final identificado o incertidumbre explícita |
| C1 | Crear una RFA desde RFT con tres tipos y una fórmula | RFA reabierta; valores y geometría correctos en los tres tipos |
| C2 | Crear contenido con vacío, conector y parámetro compartido | Geometría, conexiones y GUID comprobados en familia y proyecto |
| C3 | Modificar un tipo de muro multicapa | Capas, funciones, materiales y espesor coinciden con lo pedido |
| C4 | Cargar y actualizar una familia existente | Política de sustitución respetada; instancias y parámetros conservados según contrato |
| D1 | Detectar defectos sembrados con lista de referencia | Precisión y exhaustividad por clase; IDs y evidencia de cada hallazgo |
| D2 | Separar habitaciones no colocadas/no encerradas y CAD importado/vinculado | Clasificación correcta, sin convertir lecturas fallidas en defectos o pases |
| D3 | Auditar host con vínculos y worksets descargados/cerrados | Cobertura incompleta identificada; ningún resultado global engañoso |
| D4 | Repetir tras corregir algunos defectos | Solo cambios reales; comparación del mismo modelo y requisitos |
| E1 | Repetir una consulta y luego cambiar el modelo/vínculo | Acierto de caché rápido sin devolver datos obsoletos tras el cambio |
| E2 | Resolver una tarea desde lenguaje natural | Resultado correcto, llamadas, reintentos, tokens y tiempo total registrados |

Protocolo: fijar versión/binarios, modelo/hash, máquina, idioma, cliente, modelo de IA y presupuesto. Separar prueba API con llamadas deterministas de prueba del agente. Para latencia, distinguir arranque en frío de consultas calientes; hacer al menos 30 repeticiones de lecturas, alternando orden, y publicar muestras, mediana y p95. Para escrituras, restaurar la misma copia de partida entre repeticiones. Para el agente, repetir cada tarea varias veces con condiciones iguales y conservar trazas.

Registrar memoria máxima, espera en cola, ejecución, serialización, bytes y mayor bloqueo continuo de UI donde sea medible. Un dato no instrumentado queda como no medido. Las verificaciones finales deben ser externas al relato del agente, comunes a ambos productos y equivalentes en profundidad. No comparar una relectura exhaustiva con un simple muestreo como si fueran la misma tarea.

Presentar tres resultados separados: producción, contenido y diagnóstico. La precisión y el cumplimiento del resultado preceden al tiempo; las negativas correctas y las escrituras inseguras se reportan separadas de los errores normales. No asignar un ganador global con pesos elegidos después de ver los resultados.

## Mejoras prioritarias para Horizun

| Orden | Implementación candidata | Cómo reconocer mejora |
| --- | --- | --- |
| 1 | Medición por fase y benchmark independiente con identidad de binarios | Muestras reproducibles y datos de corrección, no solo pruebas verdes |
| 2 | Resumen por acumulación y filtros nativos compatibles; reutilización local de tipos/niveles | Menos tiempo y asignaciones, con conteos/cobertura idénticos |
| 3 | Respuestas progresivas y guía de selección por tarea | Menos bytes y llamadas para la misma decisión correcta |
| 4 | Caché acotada para consultas elegibles con invalidación de host/vínculos | Mejora de consultas repetidas y cero resultados obsoletos en E1 |
| 5 | Flujos tipados de documentación de habitaciones y revisión→acción→relectura | Menos pasos del agente y cumplimiento íntegro de P1/D4 |
| 6 | Recetas de familias parametrizadas, flexión y revisión visual | Mayor éxito en C1/C2 sin depender de código improvisado |

Antes de medir logs centralizados hay que corregir la discrepancia ya detectada entre `success`/`duration_ms` y los campos reales `outcome`/`total_ms`. También debe corregirse el verificador público que recorre artefactos y falla en archivos vacíos. Ninguna de estas correcciones se implementó al elaborar este informe.

## Decisión

Cortex aporta referencias concretas para mejorar la experiencia y el aprovechamiento de contexto. Horizun tiene fortalezas de código en familias y diagnóstico trazable. El próximo resultado útil es una comparación controlada Cortex/Horizun por estos casos, no otra puntuación máxima basada en catálogo. Esta revisión no instaló competidores, no abrió modelos ni alteró la implementación.
