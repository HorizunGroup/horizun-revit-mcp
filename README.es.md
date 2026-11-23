# Horizun Revit MCP Community 6.0 — modela, documenta y entrega en Revit

**Hecho en Colombia 🇨🇴 por Horizun Group.**

Horizun Revit MCP Community es un servidor MCP y add-in de Windows gratuito y de
código abierto (Apache-2.0) para **Autodesk Revit 2023–2027**. Su catálogo contiene
**110 herramientas** <!--inventory:tools--> con **573 suboperaciones y modos de despacho nombrados** <!--inventory:operations-->
para modelado arquitectónico y estructural, planos y láminas, auditoría,
cantidades, Excel, Power BI y exportación. El modelado MEP, DWG a BIM, el
refuerzo, IFC a BIM y la entrega IFC, las nubes de puntos, la autoría de familias
y la coordinación están en la edición Pro: ver [Community frente a Pro](#community-frente-a-pro).

Trabaja con objetivos en lenguaje natural desde tu cliente MCP para crear
contenido BIM nuevo, consultar modelos y ejecutar flujos de producción de varios
pasos. Los cambios tipados incluyen ensayo, objetivos explícitos y verificación
posterior al commit; Python habilitado por el propietario amplía el puente a
automatizaciones específicas de la API de Revit. El instalador de Windows incluye
el runtime del servidor y los add-ins.

**[English](README.md)** · **Español**

[![ci](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/ci.yml?branch=main&label=ci&logo=githubactions&logoColor=white)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/ci.yml) [![codeql](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/codeql.yml?branch=main&label=codeql&logo=github)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/codeql.yml) [![release](https://img.shields.io/github/v/release/HorizunGroup/horizun-revit-mcp?label=release&color=0696D7)](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest) [![Revit 2023–2027](https://img.shields.io/badge/Revit-2023%E2%80%932027-0696D7)](#instalar) [![MCP registry](https://img.shields.io/badge/MCP%20registry-io.github.HorizunGroup%2Fhorizun--revit--mcp-6E56CF)](https://registry.modelcontextprotocol.io/) [![license Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE) [![GitHub stars](https://img.shields.io/github/stars/HorizunGroup/horizun-revit-mcp?label=GitHub%20stars)](https://github.com/HorizunGroup/horizun-revit-mcp/stargazers)

**[Descargar](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest)** ·
**[Ver demostración](https://www.youtube.com/watch?v=tlFs5p3EM4M)** ·
[Novedades de la 6.0 Community](#novedades-de-la-60-community) ·
[Community frente a Pro](#community-frente-a-pro) ·
[Instalar con Claude Desktop Free](#video-de-instalación-claude-desktop-free) ·
[Todas las herramientas](#catálogo-completo-de-herramientas) ·
[Suboperaciones](#suboperaciones-y-modos) · [Pruebas](#probado-en-revit-evidencia-publicada) · [Instalar](#instalar)

## Novedades de la 6.0 Community

Primera versión Community desde la 4.0.1. El detalle está en el [CHANGELOG](CHANGELOG.md).

- **Revit en español:** en un Revit en español, los nombres en inglés de los parámetros y categorías comunes se resuelven, y cada fila que nombra una categoría trae `built_in_category`. Planos de referencia, diálogos que se cierran solos, reintentos de `set_curve`, puertas de muro cortina y volúmenes ya no dependen del idioma.
- **Escrituras tipadas más seguras:** un número suelto en un parámetro de longitud son pies, así que `horizun_write_params_verified` y `horizun_create_elements` rechazan uno que no es una medida de edificio; `accept_raw_feet=true` lo acepta y la respuesta dice cómo se leyó (`number_read_as`). `horizun_execute_python` se niega después de una escritura tipada que falló a mitad, hasta releer el modelo o deshacer.
- **Guarda de identidad:** los escritores tipados que reciben ids de elementos existentes aceptan `identity` / `unique_id` y rechazan la llamada entera, sin escribir nada, si el elemento no es el que se ensayó. En un borrado, en un modelo colaborativo o sobre un elemento que no es de este usuario, la categoría o el tipo solos no bastan. Ver [docs/IDENTITY-GUARD.md](docs/IDENTITY-GUARD.md).
- **Láminas dentro del marco del rótulo:** `horizun_sheet_layout` `auto_layout` sin `margins` acomoda los viewports dentro del marco interior leído de las líneas de la familia del rótulo y deja libre la caja de datos de una esquina. `get_viewport_box` trae `drawing_area_check`; `repair_outside=true` mueve solo los viewports que se salen, y una reparación que crea un solape se rechaza y se revierte.
- **Grupos de herramientas y perfiles:** cada herramienta tiene un grupo; un perfil (`--profile`, `HORIZUN_PROFILE` o `tool_profile`) decide qué se LISTA, nunca qué puede correr, y `horizun_toolsets` activa grupos en la sesión. Ver [docs/TOOL-GROUPS.md](docs/TOOL-GROUPS.md).
- **Anotación, vistas y entrega:** etiquetas que buscan un sitio libre y rechazan el lote si no lo encuentran, directrices de notas verificadas contra su destino, recortes poligonales de vista con ensayo, `horizun_selection_context` `operation=view_contents`, `horizun_manage_worksets` `operation=loans`, carpetas de exportación creadas al aplicar (`require_existing_folder` para exigirlas) y `horizun_export` `pdf_qa`.
- **Modelos colaborativos:** `horizun_save_document` `operation=sync_with_central` ensaya y aplica con su `confirmation_token`, y se niega mientras una escritura de la sesión tenga una verificación fallida o sin medir.

## Community frente a Pro

| Área | Community (este repositorio) | Pro |
|---|---|---|
| Conexión, health, instancias, trabajos, grupos de herramientas | Sí | Sí |
| Consultas, diagnóstico del modelo, auditorías y correcciones verificadas | Sí | Sí |
| Modelado arquitectónico y estructural (muros, losas, cubiertas, escaleras, vigas, columnas, cimentaciones, entramados, muros cortina, terreno) | Sí | Sí |
| Parámetros, materiales, fases, subproyectos, grupos, ensamblajes | Sí | Sí |
| Planos, vistas, láminas, tablas, cotas, etiquetas, detalles 2D | Sí | Sí |
| Cantidades, comparación con presupuesto, Excel, Power BI | Sí | Sí |
| Exportación (PDF, DWG, IFC, NWC, FBX, imágenes, CSV de tablas) y revisión de PDF | Sí | Sí |
| Contexto de proyecto ISO 19650 y contenedores de información | Sí | Sí |
| Planes componibles, procedimientos y prompts, Python habilitado por el dueño | Sí | Sí |
| Modelado y conexión MEP (trazado, uniones, sistemas, aparatos, eléctrico, auditoría de conectividad) | — | Sí |
| DWG / CAD a BIM y actualización por revisiones del DWG | — | Sí |
| Refuerzo estructural (planear, aplicar, auditar) | — | Sí |
| IFC a BIM y entrega IFC verificada (`horizun_deliver_ifc`) | — | Sí |
| Nubes de puntos a modelo | — | Sí |
| Autoría y edición de familias (crear, recargar, aplicar, consultar su interior) | — | Sí |
| Coordinación: vínculos RVT, detección y resolución de interferencias, vistas de coordinación, chequeo de federación, copia entre documentos | — | Sí |

Las 41 herramientas Pro no están en este repositorio: su código y su documentación
se retiraron del árbol, y el contrato de Community no las anuncia.

## Modelos locales pequeños (Ollama / qwen3:8b)

Configura `HORIZUN_SCHEMA_MODE=local` (o `HORIZUN_PROFILE=local`) en el **proceso
del servidor MCP** y `qwen3:8b` con `options.num_ctx=16384` en las llamadas de tu
agente a Ollama. El listado ofrece hasta 11 herramientas esenciales, incluidas
buscar/describir/ejecutar, con argumentos comunes y descripciones de máximo
200 caracteres. Se mantienen los permisos y toolsets activos; con `safe_write`
se publican actualmente 10 de esas herramientas.
Los argumentos avanzados y el resto del catálogo siguen disponibles mediante
`horizun_describe_tool` y `horizun_call_tool`. Busca con `horizun_list_tools`,
por ejemplo `query="contar muros"` o `query="count walls"`.

La búsqueda devuelve hasta tres coincidencias relevantes, también parciales, como
`create_sheet`, `place_view`, `tag de puertas visibles` y `planta`.
Antes de usar un ID de vista o nivel, llama `get_document_info` con
`{"operation":"context","max_rows":100}`. Lista niveles (ID, nombre, elevación en metros)
y vistas no plantilla (ID, nombre, tipo, nivel asociado); sigue `next_offset` hasta null.
Los errores de vista/nivel en captura, navegación y creación sugieren nombres e IDs válidos
sin sustituirlos automáticamente. Se mantienen los filtros de permisos y toolsets.

El perfil activa respuestas compactas de lecturas exitosas: resumen corto y datos
una sola vez en `structuredContent`, sin bloques verbosos de cola o seguridad.
Conserva cobertura, paginación, sanitización y hallazgos de seguridad; las escrituras
y los errores mantienen su evidencia de verificación y diagnóstico. Para contar,
usa `max_rows=1` en `horizun_list_elements` o `response_mode="summary"` en
`horizun_query_model`. No se descartan filas silenciosamente.
`HORIZUN_RESPONSE=compact` también funciona en otros modos; `full` lo desactiva.

Medido por stdio aislado: **local 2.753 tokens estimados** con `safe_write`,
2.948 con las 11 herramientas (`ceil(caracteres UTF-16/4)`). El JSON completo
de las 11 herramientas midió además **2.647 tokens con Qwen3:8b** mediante
evaluación de prompt raw en Ollama; excluye conversación y envoltura de herramientas del agente.
Consulta la [configuración y prueba en vivo](docs/CLIENTS.md#ollama--qwen-small-local-models).
Este cambio aún no tiene una nueva prueba de rendimiento con Revit/Ollama.

## Antes: lo que trajo la 3.0

- **Cartillas desde planos:** `horizun_read_schedule` lee cartillas vectoriales PDF o DWG y rechaza filas ambiguas. Las reglas de refuerzo de la 3.0 están en la edición Pro.
- **Interoperabilidad:** `horizun_exchange` emite y verifica manifiestos `horizun.exchange/0`, transporta identidad de modelo y comprueba planes de corrección antes y después de aplicarlos. Véanse los [detalles de herramientas](docs/TOOLS-EXTENDED.md).
- **Esquemas a petición:** `full` sigue predeterminado. `HORIZUN_SCHEMA_MODE=facade` por cliente muestra health y tres herramientas de descubrimiento y llamada que obtienen los esquemas exactos cuando se necesitan. La reducción medida de `tools/list` aún no tiene una comparación de agentes completada. Véanse la [configuración](docs/CLIENTS.md) y las [mediciones](docs/plans/lazy-schema-3.0.md).
- **Toolset local:** `HORIZUN_TOOLSETS=local` reduce la superficie tipada anunciada para clientes de contexto corto y conserva los permisos. El modo de esquema y el toolset son independientes.

Para actualizar una instalación existente, sigue la [migración a 3.0](docs/MIGRATION-3.0.md): el protocolo 3 exige actualizar juntos servidor y add-in. Las [notas de versión](docs/plans/release-notes-3.0.0.md) resumen la entrega.

## El producto en cifras

| Superficie | Qué ofrece | Dónde comprobarlo |
|---|---|---|
| Entradas MCP | **110 herramientas** <!--inventory:tools-->, incluidas **38 de solo lectura** <!--inventory:reads--> y **72 con posibles efectos** <!--inventory:writes--> | [Inventario generado](docs/inventory.json) y catálogo completo más abajo |
| Acciones internas | **573 suboperaciones y modos de despacho nombrados** <!--inventory:operations--> dentro de herramientas compuestas | Valores exactos de los selectores más abajo |
| Cobertura Revit | 2023, 2024, 2025, 2026 y 2027 | Cinco add-ins y sus informes de pruebas versionados |
| Contenido nuevo | 26 clases de creación de elementos; planificación estructural | [Creación de arquitectura](docs/ARCHITECTURE-CREATION.md) |
| Planos y entregables | 24 acciones de vistas/láminas, 10 acciones de anotación, tablas nativas y distribución de láminas | [Producción de planos](docs/PLANIMETRY-PRODUCTION.md) |
| Formatos de exportación | PDF, DWG, DGN, DWFx, IFC, NWC, FBX, gbXML, COBie, familias RFA, imágenes y CSV de tablas | `horizun_export` |
| Ejemplo de release verificada | 1.230 ejecuciones de pruebas aprobadas entre cinco años de Revit en **v1.3.3** | [Informes publicados](#probado-en-revit-evidencia-publicada) |
| Instalación | Setup de Windows con runtime incluido; sin Git, Visual Studio ni SDK de .NET para el usuario | [Pasos finales por cliente](docs/CLIENTS.md) |

Los recuentos describen la superficie completa de Community. El perfil de
permisos y los paquetes elegidos determinan qué herramientas anuncia una sesión;
más abajo se explican los perfiles medidos de 70, 79 y 80 herramientas.

## Lo que trajo la versión 2.1

Cada punto se construyó con el mismo contrato de escritura verificada (ensayo,
token, aplicación, relectura) y, donde hay un modelo de prueba, se midió en vivo en
Revit. El detalle de cada uno está en el [CHANGELOG](CHANGELOG.md). Los puntos de
la 2.1 en áreas Pro (trazado y pendientes MEP, aberturas y camisas para
interferencias, análisis de sistemas MEP, ida y vuelta con Navisworks, BCF,
coordenadas compartidas, nubes de puntos y desviación respecto al escaneo) están
en la edición Pro.

**Herramientas nuevas y nuevas formas de modelar**

1. **Entramado liviano, `horizun_framing`** — montantes, canales, jambas, dinteles, antepechos, cortos y bloqueos dentro de una capa del muro, y entramado de cielos suspendidos, a partir de un detalle que el cliente MCP lee de una imagen o DWG (prompt `framing-from-detail`).
2. **Entramado por el método curtain** — el mismo entramado modelado con muros cortina y capas de sloped glazing planas (montantes como mullions, rejillas a distancia fija, colgantes hasta la losa superior), para equipos que trabajan así.
3. **Explotar muros multicapa con vanos** — `horizun_split_multilayer_walls` ahora convierte muros que alojan puertas, ventanas y componentes anidados: el muro portador conserva su identidad y sus insertos, y cada capa pasa a ser su propio muro.
4. **Libro COBie 2.4** — `horizun_export format=cobie`: hojas Facility, Floor, Space, Zone, Type, Component y System, releídas celda por celda, con `deliverable_ready` y un hallazgo por cada valor obligatorio que falte.
5. **Nuevos tipos de elemento** — rociador, tubería flexible, ducto flexible, espacio, área y límite de área en `horizun_create_elements`; habitaciones y espacios para cada recinto vacío (`placement=all_enclosed`); toposólidos (Revit 2024+).
6. **Editar el boceto sin perder el elemento** — `edit_sketch` en losas, cielos y aberturas conserva el id y los datos alojados.

**CDE y gestión de la información**

7. **Incidencias de ACC** — `horizun_cde_cloud issues_list / issue_create / issue_update`, como el usuario que inició sesión, idempotentes por una clave externa y releídas después de cada escritura.
8. **Gestión de la información ISO 19650** — contexto del proyecto (`project-intake`) y contenedores de información entre WIP/Compartido/Publicado/Archivado. La entrega IFC demostrada contra IDS está en la edición Pro.
9. **Sincronizar con el central** — `horizun_document_session sync_with_central`, apagado por defecto y habilitado solo por el dueño del equipo desde Revit; liberar todo, conservar lo prestado o nada, verificado después de sincronizar.

**Verificación que se puede ver**

10. **Revisión espacial después de cada escritura** — superposiciones, puertas bloqueadas, duplicados y MEP atravesando estructura se reportan con la escritura; `horizun_verify_changes` devuelve una imagen con los elementos cambiados resaltados.
11. **Capturas antes/después** — `snapshot / compare_to` con la misma cámara y una diferencia en rojo vinculada a los elementos escritos.
12. **Zonas libres de equipos** — `clearance_rules` verifica zonas de trabajo contra el modelo y sus vínculos.
13. **Distancia de evacuación** — `horizun_code_check` con el recorrido de evacuación de Revit, reportada como cota inferior o `not_decidable` con la razón.
14. **Preparación para análisis energético** — `energy_readiness` construye y revierte el modelo energético y reporta cerramientos, construcciones y relación ventana-muro.

**Documentación, cantidades e intercambio**

15. **Más formatos de exportación** — DWG de muchas vistas o láminas en un solo trabajo, gbXML, DGN, DWFx y familias cargadas a `.rfa`, cada archivo releído.
16. **Acabados por recinto** — `horizun_quantities room_finishes`: caras de muro, piso y cielo por habitación o espacio, por material, con los vanos descontados.
17. **Presupuesto a BC3** — `horizun_budget_compare export_bc3` (FIEBDC-3) desde las cantidades y los precios del usuario.
18. **Láminas y vistas** — `renumber_sheets` con las colisiones revisadas antes, y `set_view_display` corrige nivel de detalle o disciplina citando el hallazgo de la auditoría.
19. **Modelo analítico estructural y cargas** — `horizun_query_structure analytical` y `loads`, agrupadas por caso de carga.
20. **Una lista de herramientas más liviana** — `tools/list` pesa unos 64 KB menos sin quitar ninguna herramienta, argumento u operación, así que los clientes la cargan más rápido.

## Demostración: de un PDF de planos a un modelo Revit

[![Ver la demostración de Horizun: de planos PDF a un modelo Revit con IA](https://i.ytimg.com/vi/tlFs5p3EM4M/hqdefault.jpg)](https://www.youtube.com/watch?v=tlFs5p3EM4M)

**[Planos PDF → modelo Revit con IA — ver en YouTube](https://www.youtube.com/watch?v=tlFs5p3EM4M)**
es una demostración publicada en español por el canal Horizun Hub. Presenta un
flujo de PDF a Revit dirigido por un cliente de IA. La interpretación del PDF
corresponde al cliente/modelo; Horizun aporta las operaciones ejecutadas en
Revit. El flujo tipado DWG a BIM, que consulta geometría CAD y reglas
versionadas, está en la edición Pro.

## Qué puedes crear y entregar

### Modelado arquitectónico y estructural

`horizun_create_elements` admite lotes con dependencias de niveles, ejes, muros,
suelos, cielos, cubiertas, habitaciones, instancias de familia, vigas y columnas,
sistemas de vigas, cimentaciones de muro, conductos, tuberías, conduit,
bandejas, accesorios, sistemas MEP, aperturas, huecos verticales, separadores de
habitación, muros por perfil, conjuntos de desplazamiento y escaleras soportadas.

El desglose lista las 26 clases de creación y cinco opciones de accesorios MEP.
`horizun_transform_elements` permite mover, copiar, rotar, gestionar uniones de
muros, cambiar tipos/curvas y ajustar etiquetas. El planificador estructural
genera peticiones para esos mismos creadores. El trazado, las uniones, las
conexiones, los tipos de sistema y el planificador MEP están en la edición Pro.

### Planos, anotación automática y producción de láminas

Crea plantas arquitectónicas/de cielos/estructurales/de áreas, secciones,
elevaciones, llamadas, vistas de dibujo y 3D. Gestiona plantillas, fases,
recortes, rangos y cajas de referencia; crea, duplica y llena láminas; coloca
tablas y alinea ventanas gráficas. Anota textos, etiquetas, cotas lineales,
angulares, radiales, diametrales o de arco y cotas de elevación, coordenadas o
pendiente.

Planea cotas y etiquetas con criterios explícitos, consulta referencias
geométricas, crea detalles 2D, audita planimetría, corrige hallazgos y distribuye
zonas de láminas. `horizun_plan_views` y el prompt `deliverable-production`
coordinan etapas de entrega y estados de aprobación.
[Flujos de planos](docs/PLANIMETRY-PRODUCTION.md).

### Auditoría, cantidades e intercambio de datos

Consulta anfitrión y vínculos cargados por categoría, familia/tipo, nivel,
parámetros y límites espaciales. Diagnostica salud del modelo, audita requisitos
proporcionados y corrige parámetros con verificación.

Obtén cómputos de materiales, compáralos con una base presupuestal Excel,
lee o añade filas XLSX sin Excel/COM, envía datos aprobados a Power BI y exporta
entregables verificados. [Cantidades](docs/QUANTITIES-AND-BUDGET.md),
[Power BI](docs/POWER-BI.md), [primera auditoría](docs/QUICK-START-BIM.md).

### Transformaciones especializadas y terreno

Separa capas de muros o losas, divide suelos por bucles, rectangulariza geometría
de muros soportada, desagrupa conservando el origen y reagrupa por parámetro.
Transfiere elevaciones entre losas, integra suelos en sólidos topográficos y
conforma terreno con líneas de quiebre y taludes definidos. La descomposición de
muros conserva la identidad del muro del núcleo y sus elementos hospedados.
Las recetas incluidas usan Python distribuido para geometría bajo control del
host para ensayo, transacciones y sus comprobaciones declaradas; el Python
arbitrario generado por el cliente tiene un contrato de permisos/evidencia aparte.

### En la edición Pro

Las familias paramétricas nuevas con conectores MEP, DWG a BIM con seguimiento de
revisiones, el modelado MEP, la coordinación y resolución de interferencias, el
refuerzo, IFC a BIM y la entrega IFC, y las nubes de puntos están en Horizun Revit
MCP Pro ([Community frente a Pro](#community-frente-a-pro)).

## Instalar

**[Descargar el instalador de Windows](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest)**

- Requiere **Windows x64, Revit 2023–2027 y un cliente MCP**.
- Setup incluye el runtime del servidor y los add-ins de Revit.
  **No necesitas Git, Visual Studio ni el SDK de .NET.**
- Cierra Revit antes de instalar.
- Las releases públicas **no tienen firma de editor**. El SHA-256 verifica los
  bytes descargados; no autentica al editor de Windows. Windows y Revit pueden
  mostrar un aviso. Consulta la [política de firma](CODE-SIGNING-POLICY.md).

1. Abre la release del enlace y descarga `horizun-mcp-<version>-setup.exe` y
   `SHA256SUMS.txt` desde **Assets**. El ZIP de código fuente es para desarrollo.
2. Comprueba el hash con la [guía de instalación](docs/INSTALL.md) y ejecuta Setup.
3. Completa la conexión de tu cliente:

| Cliente | Paso final de conexión |
|---|---|
| **Codex / Claude Code** | Deja que el asistente registre el MCP cuando cierres el cliente; después vuelve a abrirlo. |
| **Claude Desktop** | Instala el `.mcpb` entregado en **Documentos\Horizun-Revit-MCP** desde **Settings → Extensions** y reinicia Claude Desktop. |
| **ChatGPT Work** | Completa la configuración de Secure MCP Tunnel para el servidor instalado. |
| **Otros clientes stdio** | Registra el ejecutable instalado mediante su ruta completa. |

4. Abre Revit y un documento, y pide al cliente que llame a `horizun_health`.
   Confirma el documento activo y la versión cargada.

**Claude Desktop requiere instalar la extensión dentro de la aplicación.** Setup
entrega el paquete y las instrucciones ilustradas en la carpeta de Documentos.
Arrastra el archivo a Extensions o usa **Advanced settings → Install extension**.
La extensión conecta con el servidor instalado; no sustituye a Setup.
Consulta las [instrucciones por cliente y recuperación](docs/CLIENTS.md).

### Video de instalación: Claude Desktop Free

[![Instalar Horizun Revit MCP con Claude Desktop Free](https://i.ytimg.com/vi/3kp-we7MIvk/hqdefault.jpg)](https://www.youtube.com/watch?v=3kp-we7MIvk)

**[Conectar Revit con Claude Desktop Free — ver tutorial de instalación](https://www.youtube.com/watch?v=3kp-we7MIvk)**
es una guía en español publicada por Horizun Hub para instalar y conectar
Horizun Revit MCP con el plan gratuito de Claude Desktop. Sigue los pasos de
Setup y del `.mcpb` indicados arriba; la [guía del cliente](docs/CLIENTS.md#claude-desktop)
incluye las instrucciones escritas.

### Bootstrap opcional de PowerShell

Descarga el instalador publicado, comprueba su hash y ejecuta Setup en silencio.
`-AllowUnsigned` reconoce la ausencia de firma indicada arriba. Los pasos finales
por cliente siguen siendo necesarios; usa `-Interactive` para ver el asistente.

```powershell
$s = irm https://raw.githubusercontent.com/HorizunGroup/horizun-revit-mcp/main/install-release.ps1; & ([scriptblock]::Create($s)) -AllowUnsigned
```

El script se descarga de `main`; su verificación de hash corresponde al Setup
descargado. Puedes descargar y revisar el script antes de ejecutarlo.
[Opciones de instalación](docs/INSTALL.md).

## Catálogo completo de herramientas

Aquí aparece cada herramienta MCP de la edición Community, agrupada por el trabajo que realiza. Estos
grupos facilitan la lectura; los **paquetes de sesión** configurables están
implementados en [ToolPacks.cs](src/Horizun.Revit/Core/ToolPacks.cs). Las
descripciones se mantienen en el [catálogo bilingüe](docs/readme-catalog.json);
los recuentos y los nombres se comprueban contra el
[inventario generado por el servidor](docs/inventory.json).
La [referencia detallada](docs/TOOLS.md) documenta argumentos y límites.

<!-- BEGIN TOOL CATALOG -->
### Conexión, documentos y trabajos en segundo plano

| Herramienta | Capacidad |
|---|---|
| `get_document_info` | Consultar identidad y cantidades de elementos del documento abierto. |
| `horizun_document_session` | Inspeccionar, abrir, guardar, guardar como y cerrar documentos, o crear un proyecto en blanco desde una plantilla, mediante operaciones explícitas. |
| `horizun_file_info` | Leer cabeceras RVT/RFA, versión y datos de trabajo compartido sin abrirlos ni actualizarlos. |
| `horizun_health` | Consultar estado del puente, documento activo, versión, commit y estado de Python. |
| `horizun_job_status` | Consultar progreso y estado de recuperación mientras Revit está ocupado o después de reiniciar un proceso. |
| `horizun_open_document` | Abrir modelos comprobando actualización de versión y archivos centrales compartidos. |
| `horizun_relinquish_all` | Liberar elementos prestados y reportar los que siguen perteneciendo al usuario. |
| `horizun_repair_memory` | Reportar y recuperar el estado durable de este puente cuando un registro no se puede leer. |
| `horizun_save_document` | Guardar y comprobar fecha y tamaño del archivo resultante. |
| `horizun_selection_exchange` | Leer lo que una persona seleccionó en Revit y ofrecerle un conjunto para seleccionar. |
| `horizun_submit_job` | Enviar operaciones largas de Revit con un identificador de trabajo persistente. |
| `horizun_target` | Seleccionar la instancia de Revit cuando hay varias sesiones o versiones abiertas. |

### Consultas, auditoría y corrección de modelos

| Herramienta | Capacidad |
|---|---|
| `horizun_apply_corrections` | Ensayar y aplicar correcciones de auditoría soportadas, verificando sus resultados. |
| `horizun_selection_context` | Leer el contexto de la selección o de ids dados, fotografiarlo y compararlo, y revalidar ids tras sincronizar. |
| `horizun_audit_access` | Reportar qué puede hacer este puente en esta máquina y quién lo decidió. |
| `horizun_audit_model` | Evaluar requisitos proporcionados del modelo con hallazgos, cobertura y evidencia de controles previos. |
| `horizun_capture_view` | Exportar una vista como imagen para que el cliente pueda revisarla visualmente. |
| `horizun_verify_changes` | Revisar lo que cambió la última escritura en busca de conflictos espaciales y devolver una imagen con ellos resaltados. |
| `horizun_delete_verified` | Eliminar IDs explícitos o purgar contenido sin uso; anticipar dependencias y verificar eliminaciones. |
| `horizun_list_elements` | Listar y paginar elementos del anfitrión y vínculos cargados, identificando su modelo. |
| `horizun_model_scan` | Medir salud del modelo, advertencias, worksets, vínculos, familias, vistas y candidatos de limpieza. |
| `horizun_navigate` | Seleccionar elementos, limpiar la selección, encuadrar y abrir vistas. |
| `horizun_query_model` | Filtrar por categoría, tipo, nivel, parámetros y límites espaciales; seleccionar campos y obtener resúmenes agrupados o compactos. |
| `horizun_validate_ids` | Comprobar un modelo contra una especificación IDS y reportar cada requisito que incumple. |
| `horizun_query_classification` | Leer las tablas de keynotes y códigos de ensamble, su uso en el modelo, códigos faltantes y sin uso, y las tablas de búsqueda de familias. |
| `horizun_model_diff` | Tomar instantáneas y comparar entregas: elementos añadidos, borrados y modificados con parámetros, vista coloreada, resumen y historial de calidad. |
| `horizun_code_check` | Revisar el modelo contra conjuntos de requisitos declarativos, incluidas reglas colombianas de accesibilidad, evacuación e iluminación, con not_decidable en vez de suposiciones. |
| `horizun_undo` | Listar y deshacer el último lote verificado de Horizun, negándose si el modelo o el archivo cambiaron desde entonces. |
| `horizun_measure_geometry` | Medir volumen sólido, perfil de sección, caja envolvente, puntos extremos y normales de caras de ids o de un selector; lo ilegible se nombra, nunca es cero. |
| `horizun_query_relations` | Leer a qué están unidos los elementos, qué los corta, qué cortan, quién los aloja, qué alojan y qué hay cerca, con categoría y tipo de relación. |
| `horizun_audit_support` | Encontrar elementos en el aire, vigas sin apoyo en sus extremos, recintos sin piso debajo y un modelo corrido frente a su vínculo (solo lectura). |

### Creación de modelos, parámetros y familias

| Herramienta | Capacidad |
|---|---|
| `horizun_bind_shared_param` | Vincular parámetros compartidos a categorías y comprobar su comportamiento entre grupos. |
| `horizun_create_elements` | Crear niveles, ejes, muros, losas, cubiertas, habitaciones, instancias, estructura, redes MEP, aperturas y escaleras en lotes tipados. |
| `horizun_template_kit` | Cargar, consultar y auditar plantillas de proyecto mediante un kit reutilizable. |
| `horizun_manage_materials` | Leer, crear y asignar materiales con sus datos de apariencia e identidad. |
| `horizun_manage_classification_table` | Consultar y gestionar tablas de clasificación usadas por tipos y familias del modelo. |
| `horizun_set_keynote` | Asignar notas clave con alcance explícito sobre instancias y tipos. |
| `horizun_wall_sweep` | Crear y verificar molduras y ranuras en muros explícitos. |
| `horizun_join_geometry` | Unir, desunir y cambiar quién corta entre muros, losas, vigas, columnas y cimentaciones con una prioridad dicha; el volumen compartido se relee en cero. Encuentra y cierra esquinas L/T de muros abiertas; auto_priority une un nivel completo. |
| `horizun_room_finishes` | Crear pisos de acabado y guardaescobas por recinto: contorno desde las caras de acabado, tipo según su uso, área releída y sin traslapo con muros. |
| `horizun_wall_finish` | Emparejar muros de ARQ con los núcleos de EST de un vínculo y poner muros de acabado en sus caras sin volumen compartido. |
| `horizun_transform_elements` | Mover, copiar, rotar, fijar, cambiar tipos o curvas y ajustar etiquetas sobre elementos explícitos. |
| `horizun_write_params_verified` | Escribir parámetros por lotes y releer cada valor solicitado. |
| `horizun_manage_parameters` | Leer el mapa completo de bindings, crear y bindear parámetros compartidos, reasignarlos o quitarlos, y gestionar parámetros globales. |
| `horizun_manage_curtain` | Leer y editar muros cortina: líneas de rejilla, montantes y tipos de panel, remedidos tras cada cambio. |
| `horizun_slab_shape` | Editar la forma de losas y cubiertas: puntos, líneas de división y elevación de vértices, con restablecimiento. |
| `horizun_create_railing` | Crear barandas sobre escaleras o rampas, o desde un recorrido dibujado en un nivel. |
| `horizun_manage_phases` | Leer fases, filtros y opciones de diseño; asignar fases a elementos y crear o editar filtros de fase. |
| `horizun_manage_assemblies_parts` | Crear, dividir, excluir y disolver partes; crear ensamblajes con sus vistas y desensamblarlos. |
| `horizun_manage_styles` | Leer y ajustar estilos de objeto, subcategorías, estilos de línea, patrones de línea y de relleno. |
| `horizun_manage_units` | Leer y ajustar unidades del proyecto, información del proyecto y los puntos base y de reconocimiento (con consentimiento explícito). |

### Planos, cotas, láminas y tablas

| Herramienta | Capacidad |
|---|---|
| `horizun_manage_views` | Crear y configurar plantas, secciones, elevaciones, llamadas, vistas 3D, láminas, ventanas gráficas y ubicación de tablas. |
| `horizun_view_crop` | Ajustar y verificar recortes de vistas a partir de elementos o habitaciones. |
| `horizun_sheet_layout` | Consultar, mover y distribuir ventanas gráficas según la geometría de sus recortes. |
| `horizun_view_visibility` | Controlar la visibilidad en vistas propias y verificar el resultado. |
| `horizun_plan_views` | Planear vistas por habitación y conjuntos de entrega; seguir estados, aprobaciones e invalidaciones. |
| `horizun_planimetry_ops` | Ajustar ejes y niveles por vista, llevar viewports dentro del marco, separar etiquetas y textos superpuestos y armar una leyenda con las familias usadas. |
| `horizun_query_planimetry` | Consultar láminas, vistas, ubicaciones, anotaciones y referencias de planos con geometría y cobertura. |
| `horizun_room_boundaries` | Leer contornos nativos de habitaciones y sus referencias de modelo. |
| `horizun_audit_planimetry` | Auditar requisitos de planos y devolver hallazgos con evidencia para corregirlos. |
| `horizun_fix_planimetry` | Corregir hallazgos de vistas, láminas, rótulos, ventanas gráficas, tablas y recortes. |
| `horizun_pack_sheets` | Distribuir vistas y tablas en zonas definidas de las láminas y comprobar su ubicación. |
| `horizun_plan_annotations` | Planear etiquetas y cotas automáticas para ejes, niveles, muros cortina y aperturas con criterios explícitos. |
| `horizun_annotate` | Crear textos, etiquetas, cotas lineales/angulares/radiales/diametrales/de arco y cotas de elevación, coordenadas o pendiente. |
| `horizun_get_dimension_references` | Descubrir referencias geométricas utilizables para acotación. |
| `horizun_query_dimensions` | Consultar cotas existentes, segmentos, referencias y valores medidos. |
| `horizun_edit_dimensions` | Aplicar cambios soportados a cotas y verificar su estado resultante. |
| `horizun_query_detail_2d` | Leer geometría de detalle, estilos de línea, tipos de región y símbolos colocables de una vista. |
| `horizun_detail_2d` | Crear líneas/arcos/polilíneas de detalle, regiones rellenas/de máscara y componentes en lotes atómicos verificados. |
| `horizun_manage_revisions` | Crear y actualizar registros de revisión de planos. |
| `horizun_create_schedule` | Crear una tabla nativa con campos, ordenación y elementos vinculados opcionales. |
| `horizun_manage_schedules` | Crear, duplicar y configurar tablas, cómputos de materiales, listas de planos/vistas, revisiones y leyendas de notas clave. |
| `horizun_list_schedules` | Listar tablas y consultar campos, configuración de vínculos y dimensiones mostradas. |
| `horizun_get_schedule_data` | Leer celdas mostradas en tablas con límites explícitos de filas/columnas e información de truncamiento. |

### Estructura, refuerzo, MEP y coordinación

| Herramienta | Capacidad |
|---|---|
| `horizun_acc_upload_status` | Leer evidencia de los registros de Desktop Connector sobre asignación de archivos locales a carpetas ACC. |
| `horizun_read_schedule` | Leer hojas de despiece (PDF vectorial, entidades DWG) en filas de barras y autocomprobaciones con nombre, y proponer las cartillas de vigas como un conjunto de requisitos por marca; rechazar lo que no se lee con certeza. |
| `horizun_plan_structure` | Planear columnas en intersecciones de ejes y vigas entre cruces consecutivos. |
| `horizun_query_structure` | Consultar miembros, anfitriones, recubrimientos, barras, sistemas de refuerzo, conexiones y cantidades. |
| `horizun_structural_connections` | Leer y aplicar tipos de conexión estructural entre elementos de estructura. |

### Capas, grupos y terreno

| Herramienta | Capacidad |
|---|---|
| `horizun_split_floor_loops` | Separar un suelo por bucles de boceto conservando desfases de altura. |
| `horizun_split_multilayer_walls` | Separar capas de muros conservando la identidad del muro original del núcleo y sus elementos hospedados. |
| `horizun_framing` | Construir estructura de perfilería liviana dentro de una capa de muro y la estructura de cielo suspendido (principales, omegas, perimetral y cuelgues a la estructura) desde un spec de detalle, verificada, legible y removible. |
| `horizun_split_multilayer_slabs` | Separar capas de materiales de suelos/cielos, conservando perfiles y con reversión por losa. |
| `horizun_rectangularize_walls` | Descomponer geometría ortogonal soportada de muros en fragmentos rectangulares. |
| `horizun_ungroup_and_mark` | Desagrupar grupos del modelo registrando el grupo original de cada miembro. |
| `horizun_regroup_by_param` | Reconstruir grupos del modelo mediante un parámetro de agrupación. |
| `horizun_copy_slab_elevations` | Transferir la superficie de un suelo modificado a suelos destino explícitos. |
| `horizun_embed_floors_in_toposolid` | Integrar contornos y elevaciones de suelos en un sólido topográfico. |
| `horizun_grade_toposolid_around_floors` | Conformar terreno alrededor de suelos con desfases, líneas de quiebre y taludes definidos. |
| `horizun_manage_groups` | Gestionar grupos, hallar duplicados, anticipar anotaciones y conservar la colocación patrón. |
| `horizun_manage_worksets` | Listar, crear y renombrar worksets, mover elementos entre ellos y ajustar su visibilidad por vista. |
| `horizun_split_by_level` | Partir un muro, columna o tramo MEP vertical en cada nivel que cruza y verificar las piezas. |

### Cantidades, Excel, Power BI y entregables

| Herramienta | Capacidad |
|---|---|
| `horizun_quantities` | Medir volúmenes y cómputos de materiales con unidades, agrupación y procedencia del modelo. |
| `horizun_budget_compare` | Comparar cantidades del modelo con una base Excel; escribir salidas aprobadas a Excel/Power BI de forma opcional. |
| `horizun_catalog_lookup` | Resolver elementos de un catálogo proporcionado con estado de hoja y hash del archivo de origen. |
| `horizun_excel_read_rows` | Leer filas XLSX, tipos y valores de fórmulas en caché sin Excel ni COM. |
| `horizun_excel_write_rows` | Añadir filas XLSX (o crear un libro nuevo), guardar un respaldo en la carpeta de estado de Horizun y releer las celdas escritas sin Excel ni COM. |
| `horizun_power_bi_push` | Enviar filas a una tabla de un modelo semántico push de Power BI con protección frente a reintentos y recibos de destino. |
| `horizun_export` | Exportar PDF, DWG, IFC, NWC, FBX, imágenes y CSV de tablas verificando los archivos resultantes. |
| `horizun_link_schedule` | Importar cronogramas de MS Project, Primavera o CSV, vincular actividades a elementos, escribir fechas y colorear una vista de estado 4D. |

### Flujos componibles y automatización personalizada de la API

| Herramienta | Capacidad |
|---|---|
| `horizun_execute_plan` | Componer hasta 100 acciones tipadas con dependencias, referencias a resultados previos y reversión del grupo de transacciones. |
| `horizun_execute_python` | Ejecutar Python generado por el cliente contra la API de Revit, con preflight y evidencia del script; requiere habilitación del propietario. |
| `horizun_promote_script` | Promover un script verificado a procedimiento nombrado para que deje de ser código improvisado. |
| `horizun_request_python_access` | Mostrar en Revit una solicitud de aprobación al propietario para ejecutar Python personalizado. |
| `horizun_run_procedure` | Ejecutar un procedimiento nombrado y versionado guardado en esta máquina, con su propio consentimiento. |
| `horizun_api_help` | Describir miembros de la API de Revit y comprobar firmas de llamada reflejando la API que este Revit cargó. |

### Gestión de información ISO 19650 y entrega openBIM

| Herramienta | Capacidad |
|---|---|
| `horizun_project_context` | Validar, preguntar y redactar el contexto ISO 19650 del proyecto: designación, EIR/BEP/MIDP, estados del CDE, nomenclatura, entrega; registrar sus modelos por evidencia fuerte de identidad. |
| `horizun_exchange` | Envolver un salto entre apps en un manifiesto horizun.exchange/0 verificado, comprobarlo antes de consumirlo y conciliar un plan de correcciones de revisión. |
| `horizun_information_container` | Nombrar, sellar, verificar, inspeccionar y promover contenedores de información entre carpetas WIP, Compartido, Publicado y Archivado. |
| `horizun_cde_cloud` | Leer un CDE en la nube (Autodesk Construction Cloud o un servidor OpenCDE) por estado y cruzarlo con el MIDP, en solo lectura. |

<!-- END TOOL CATALOG -->

## Suboperaciones y modos

Una herramienta MCP puede ejecutar muchas acciones. Crear un muro, una tubería
y una escalera son opciones de `horizun_create_elements`; crear una sección y
colocar una tabla son acciones diferentes de `horizun_manage_views`.

La tabla contiene **573 suboperaciones y modos de despacho nombrados** <!--inventory:operations-->
en herramientas compuestas. Cada opción se cuenta una vez por herramienta,
propiedad selectora y valor, incluidos selectores anidados. Las rutas repetidas
del esquema `oneOf` se cuentan una sola vez. Algunos selectores afinan otra
acción: estas cifras describen el vocabulario operativo expuesto, no
herramientas MCP adicionales de primer nivel.

<!-- BEGIN SUBOPERATIONS -->
| Herramienta | Selector | Suboperaciones y modos nombrados |
|---|---|---|
| `get_document_info` | `operation` | `info`, `context` |
| `horizun_document_session` | `operation` | `open`, `save`, `save_as`, `close`, `inspect`, `sync_with_central`, `new_project`, `activate`, `batch`, `exit_application` |
| `horizun_repair_memory` | `operation` | `list`, `advice`, `observe`, `remedy`, `quarantine`, `release` |
| `horizun_save_document` | `operation` | `save_local`, `sync_with_central`, `relinquish_own` |
| `horizun_selection_exchange` | `operation` | `publish`, `read`, `clear`, `capabilities` |
| `horizun_target` | `operation` | `report`, `focus_window` |
| `horizun_selection_context` | `operation` | `context`, `snapshot`, `diff`, `validate_ids`, `view_contents` |
| `horizun_audit_model` | `operation` | `warnings`, `wall_slab`, `save`, `save_as`, `sync_with_central`, `export`, `publish`, `close_with_save`, `batch_open_close` |
| `horizun_verify_changes` | `operation` | `check`, `snapshot`, `compare_to`, `what_if` |
| `horizun_verify_changes` | `action` | `delete`, `join`, `unjoin`, `move` |
| `horizun_delete_verified` | `mode` | `ids`, `purge_unused`, `query` |
| `horizun_navigate` | `operation` | `select`, `clear_selection`, `zoom`, `select_and_zoom`, `open_view` |
| `horizun_validate_ids` | `operation` | `precheck`, `validate` |
| `horizun_query_classification` | `operation` | `keynote_table`, `assembly_code`, `family_lookup_tables`, `unused_codes`, `missing_codes` |
| `horizun_model_diff` | `operation` | `snapshot`, `list`, `compare`, `colorize`, `explain`, `record_quality`, `quality_trend` |
| `horizun_code_check` | `operation` | `check`, `travel_distance`, `energy_readiness`, `headroom` |
| `horizun_undo` | `operation` | `list`, `undo_last` |
| `horizun_audit_support` | `mode` | `floating`, `slab_supports`, `loose_beam_ends`, `coverage_gaps`, `model_offset` |
| `horizun_create_elements` | `kind` | `level`, `grid`, `wall`, `floor`, `ceiling`, `roof`, `room`, `family_instance`, `sprinkler`, `structural_framing`, `structural_column`, `duct`, `pipe`, `conduit`, `cable_tray`, `flex_pipe`, `flex_duct`, `fitting`, `wall_opening`, `slab_opening`, `beam_system`, `wall_foundation`, `accessory_inline`, `mep_system`, `shaft`, `room_separator`, `space`, `area`, `area_boundary`, `reference_plane`, `wall_profile`, `displacement`, `stairs`, `toposolid`, `door`, `window`, `wall_chain` |
| `horizun_create_elements` | `fitting` | `elbow`, `union`, `transition`, `tee`, `takeoff`, `takeoff_or_tee`, `cross`, `cap` |
| `horizun_template_kit` | `operation` | `list`, `load`, `find`, `type`, `rules`, `playbook`, `checks`, `validate`, `audit`, `evidence_schema` |
| `horizun_manage_materials` | `operation` | `create`, `duplicate`, `update`, `set_appearance` |
| `horizun_manage_classification_table` | `operation` | `set_path`, `reload`, `diagnose` |
| `horizun_wall_sweep` | `operation` | `read`, `create_type`, `place`, `place_edge` |
| `horizun_wall_sweep` | `kind` | `slab_edge`, `fascia`, `gutter` |
| `horizun_join_geometry` | `operation` | `read`, `join`, `unjoin`, `switch_order`, `corners`, `fix_corners`, `auto_priority`, `excavate`, `unexcavate` |
| `horizun_room_finishes` | `operation` | `floors`, `baseboards` |
| `horizun_wall_finish` | `operation` | `match_core`, `create_finish_layers`, `convert_to_finish`, `paint_faces` |
| `horizun_wall_finish` | `action` | `paint`, `remove` |
| `horizun_transform_elements` | `operation` | `wall_join`, `wall_attach_top`, `wall_attach_base`, `move`, `move_preserve_pin`, `copy`, `rotate`, `mirror`, `pin`, `unpin`, `change_type`, `change_type_by_rule`, `realign_wall_sketch`, `set_curve`, `move_tag_head`, `set_tag_leader`, `array_linear`, `array_radial`, `rename_level`, `edit_sketch`, `column_attach`, `column_detach` |
| `horizun_manage_parameters` | `operation` | `list_bindings`, `create_shared`, `create_project`, `rebind`, `remove_binding`, `global_list`, `global_create`, `global_set`, `global_delete`, `purge_legacy_marks` |
| `horizun_manage_curtain` | `operation` | `read`, `add_grid_line`, `remove_grid_line`, `set_mullions`, `set_panel_type` |
| `horizun_manage_curtain` | `mode` | `add`, `remove` |
| `horizun_slab_shape` | `operation` | `read`, `add_point`, `add_split_line`, `modify_subelement`, `reset_shape` |
| `horizun_manage_phases` | `operation` | `list`, `element_status`, `set_element_phases`, `create_phase_filter`, `edit_phase_filter`, `rename_phase`, `create_phase`, `assign_design_option` |
| `horizun_manage_assemblies_parts` | `operation` | `list`, `create_parts`, `divide_parts`, `exclude_parts`, `restore_parts`, `dissolve_parts`, `create_assembly`, `assembly_views`, `disassemble` |
| `horizun_manage_styles` | `operation` | `list_object_styles`, `set_object_style`, `create_subcategory`, `list_line_styles`, `create_line_style`, `list_line_patterns`, `create_line_pattern`, `list_fill_patterns`, `create_fill_pattern`, `list_color_schemes`, `create_color_scheme`, `edit_color_scheme`, `apply_color_scheme`, `place_color_legend` |
| `horizun_manage_units` | `operation` | `read`, `set`, `project_information`, `base_points` |
| `horizun_manage_views` | `operation` | `create_floor_plan`, `create_ceiling_plan`, `create_structural_plan`, `create_area_plan`, `create_3d`, `create_drafting`, `create_section`, `create_elevation`, `create_callout`, `duplicate_view`, `apply_template`, `set_phase`, `assign_scope_box`, `set_view_range`, `set_crop`, `set_annotation_crop`, `create_sheet`, `create_placeholder_sheet`, `convert_placeholder_sheet`, `duplicate_sheet`, `place_view`, `place_schedule`, `set_viewport_type`, `align_viewports`, `create_filter`, `apply_filter`, `color_by_value`, `set_element_overrides`, `hide_elements`, `isolate_elements`, `reset_temporary`, `set_category_visibility`, `create_legend`, `place_legend_component`, `edit_filter`, `order_filters`, `explain_graphics`, `create_template`, `set_template_controls`, `sheet_set_list`, `sheet_set_create`, `sheet_set_update`, `sheet_set_delete`, `renumber_sheets`, `create_perspective`, `set_sun_study`, `set_graphic_display`, `hide_outside_crop`, `set_starting_view`, `extend_grids_3d`, `create_room_isometric`, `set_view_properties`, `create_selection_filter` |
| `horizun_manage_views` | `mode` | `center`, `center_x`, `center_y`, `left`, `right`, `top`, `bottom` |
| `horizun_view_crop` | `operation` | `read`, `set_crop_box`, `set_crop_shape`, `reset_crop_shape`, `set_crop_from_elements`, `set_crop_from_room`, `set_annotation_crop_offsets`, `crop_active`, `crop_visible`, `read_section_box`, `section_box_active`, `set_section_box` |
| `horizun_sheet_layout` | `operation` | `get_viewport_box`, `move_viewport`, `auto_layout`, `layout_grid`, `set_view_scale`, `rename`, `set_title` |
| `horizun_view_visibility` | `operation` | `read`, `hide`, `unhide`, `set_overrides`, `clear_overrides`, `hide_foreign_markers`, `marker_scale`, `copy_link_overrides`, `datum_cleanup`, `set_scale`, `explain_overrides`, `override_report`, `link_datum_cleanup`, `graphics_report`, `copy_view_graphics` |
| `horizun_plan_views` | `operation` | `room_views`, `deliverable_set`, `delivery_open`, `delivery_status`, `delivery_record`, `delivery_approve`, `delivery_invalidate` |
| `horizun_planimetry_ops` | `operation` | `datum_extents`, `fit_viewport`, `resolve_overlaps`, `legend_from_families`, `stagger_grid_bubbles`, `move_note`, `note_from_model`, `clone_tag`, `arrange_column`, `set_leader_end`, `reseat_leaders`, `required_notes`, `replace_text`, `sync_sheet_references` |
| `horizun_query_planimetry` | `mode` | `inventory`, `sheets`, `views`, `unplaced_views`, `placements`, `annotations`, `references`, `annotation_overlaps`, `leader_targets`, `annotation_staleness`, `sheet_conflicts`, `free_spot`, `view_ray`, `section_mark_diagnosis`, `iso_inventory`, `rasters` |
| `horizun_fix_planimetry` | `operation` | `set_view_template`, `set_view_scale`, `rename_view`, `rename_sheet`, `place_title_block`, `move_viewport`, `move_schedule`, `clear_element_override`, `set_crop`, `set_view_display` |
| `horizun_plan_annotations` | `operation` | `auto_tags`, `model_notes`, `intent_dimension`, `dimension_set`, `auto_dimension_grids`, `auto_dimension_levels`, `auto_dimension_curtain_walls`, `auto_dimension_openings`, `auto_dimension_heights`, `auto_dimension_room`, `dimension_mode` |
| `horizun_plan_annotations` | `mode` | `wall_thickness`, `opening_width`, `opening_height`, `room_clear_width`, `grid_to_grid`, `chain_along_line`, `level_heights`, `spot_elevation` |
| `horizun_annotate` | `operation` | `text`, `tag`, `dimension`, `angular_dimension`, `radial_dimension`, `diameter_dimension`, `arc_length_dimension`, `spot_elevation`, `spot_coordinate`, `spot_slope` |
| `horizun_query_detail_2d` | `mode` | `resources`, `elements` |
| `horizun_detail_2d` | `operation` | `create_detail_line`, `create_detail_arc`, `create_detail_polyline`, `create_filled_region`, `create_masking_region`, `place_detail_component`, `place_symbol`, `set_line_style`, `draw_order` |
| `horizun_manage_revisions` | `operation` | `create_revision`, `update_revision` |
| `horizun_manage_schedules` | `operation` | `create`, `duplicate`, `rename`, `add_fields`, `remove_fields`, `set_field`, `set_filters`, `set_sorting`, `set_options`, `set_appearance`, `split`, `unsplit` |
| `horizun_manage_schedules` | `kind` | `material_takeoff`, `sheet_list`, `view_list`, `revision_schedule`, `keynote_legend` |
| `horizun_plan_structure` | `operation` | `columns_on_grid_intersections`, `beams_along_grids` |
| `horizun_query_structure` | `mode` | `members`, `hosts`, `covers`, `rebar`, `reinforcement_systems`, `connections`, `coverage`, `quantities`, `analytical`, `loads`, `reconcile`, `bending_schedule`, `parse_notation`, `compare_bars`, `steel_review` |
| `horizun_framing` | `operation` | `wall`, `ceiling`, `read`, `remove` |
| `horizun_manage_groups` | `operation` | `list`, `find_duplicates`, `annotation_report`, `create`, `add_members`, `remove_members`, `rename_type`, `duplicate_type`, `swap_type`, `change_group_type`, `set_group_level`, `place_group`, `ungroup`, `edit_type_members`, `convert_to_link`, `copy_to_levels`, `check_variation` |
| `horizun_manage_worksets` | `operation` | `list`, `status`, `loans`, `create`, `rename`, `move_elements`, `set_default`, `visibility`, `enable`, `create_local`, `borrow` |
| `horizun_quantities` | `mode` | `volume`, `takeoff`, `room_finishes`, `carbon` |
| `horizun_budget_compare` | `operation` | `compare`, `export_bc3` |
| `horizun_catalog_lookup` | `operation` | `leaf`, `search`, `match`, `products`, `bsdd_search`, `bsdd_search_dictionary`, `bsdd_class`, `bsdd_property`, `bsdd_dictionaries` |
| `horizun_link_schedule` | `operation` | `import`, `match`, `write`, `status_view` |
| `horizun_execute_plan` | `kind` | `plan`, `section`, `elevation` |
| `horizun_promote_script` | `operation` | `list`, `show`, `propose`, `review`, `approve`, `activate`, `deactivate`, `source`, `resolve`, `invocation` |
| `horizun_request_python_access` | `operation` | `request`, `status` |
| `horizun_run_procedure` | `operation` | `start`, `advance`, `decide`, `record`, `reconcile`, `status`, `abandon` |
| `horizun_api_help` | `operation` | `describe`, `check_signature` |
| `horizun_project_context` | `operation` | `schema`, `validate`, `questions`, `draft`, `elicit`, `ids_from_loin`, `integrations`, `register_model`, `resolve_model` |
| `horizun_exchange` | `operation` | `emit`, `verify`, `correction_plan_check`, `correction_plan_reverify` |
| `horizun_exchange` | `kind` | `coordination.ledger`, `coordination.status`, `quantities.rows`, `schedule.element_4d`, `model.element_lineage`, `receipt` |
| `horizun_information_container` | `operation` | `name`, `stamp`, `verify`, `inspect`, `transition`, `transmittal`, `record_review`, `register` |
| `horizun_cde_cloud` | `operation` | `list_projects`, `list_states`, `inspect`, `versions`, `issues_list`, `issue_create`, `issue_update` |
<!-- END SUBOPERATIONS -->

Otras opciones tipadas incluyen los siete valores de `horizun_export.format`:
`pdf`, `dwg`, `ifc`, `nwc`, `fbx`, `image`, `schedule_csv`. El inventario también
registra **1971 apariciones de valores de argumentos enumerados** <!--inventory:enumerated_variants-->
entre todas las propiedades y rutas; incluye configuraciones y rutas repetidas,
por lo que esa cifra no se utiliza como número de herramientas.

## Extensibilidad, permisos y descubrimiento de herramientas

**Componer flujos nuevos:** `horizun_execute_plan` conecta hasta 100 acciones
tipadas con resultados previos y dependencias nombradas, con reversión del grupo
de transacciones. Los estándares, catálogos y reglas CAD del proyecto se reciben
como entradas. **Ampliar a trabajo personalizado de la API:** el propietario
puede habilitar `horizun_execute_python` para Python generado por el cliente,
incluida la biblioteca estándar, preflight y evidencia estructurada del script.
La concesión Python ON/OFF persiste hasta revocarla. Los resultados Python son
autorreportados y mantienen `host_verified: false`.

Estos perfiles se midieron contra el mismo binario **1.3.3**, con configuraciones
aisladas y llamadas MCP reales a `tools/list`:

| Perfil y paquetes | Herramientas anunciadas | Motivo |
|---|---:|---|
| `safe_write`, todos los paquetes, Python apagado | 70 | Trabajo habitual dentro del modelo; filtra herramientas de efectos externos |
| `full_write`, todos los paquetes, Python apagado | 79 | Añade operaciones de archivos, exportación, documentos y otros efectos externos |
| `unsafe_code`, todos los paquetes, Python habilitado | 80 | Catálogo completo, incluido Python personalizado |
| `safe_write`, solo `core` | 4 | Selección mínima deliberada de conexión y gestión de trabajos |

Una sesión con menos herramientas puede estar correctamente configurada para un
trabajo concreto. Consulta `horizun_health` y el recurso
`horizun://security/current-profile` antes de comparar una sesión con el catálogo
completo. Los propietarios controlan los permisos; un evaluador puede leer este
catálogo sin habilitar ejecución arbitraria de código.

En clientes compatibles, el servidor también ofrece MCP Resources, Prompts,
Completions, logging y Tasks durables. Los recursos exponen el contrato compilado,
identidad del build, perfil efectivo y guía de flujos BIM. Los prompts nombrados
cubren control de calidad de familias, documentación de habitaciones, auditorías y entregas.
Estas funciones del protocolo y sus prompts son adicionales al recuento de herramientas.
Los esquemas de `tools/list` van abreviados; el exacto está en `horizun://contract/tools/{tool}`
(o `.../{tool}/{variant}` para un `kind` de `create_elements` o una operación de
`document_session`), y una llamada fallida que lo incumple nombra la ruta que falla y esa URI
(con `structuredContent.schema_help` cuando la respuesta es estructurada) ([detalles](docs/TOOLS-EXTENDED.md#advertised-schemas-and-the-contract)).

Los paquetes de herramientas, consultas compactas/resumidas, selección de campos
y trabajos durables permiten gestionar contexto y operaciones largas. Las
llamadas usan una cola FIFO acotada de 16 plazas; se pueden cancelar antes de
ejecutarse. Las operaciones largas exponen un ID persistente y estado consultable.
[Arquitectura](docs/ARCHITECTURE.md).

## Probado en Revit: evidencia publicada

**v1.3.3**, publicada el **2026-09-15**, incluye informes de la validación de
release sobre el servidor instalado y los cinco años de Revit soportados.
Suman **1.230 ejecuciones de pruebas aprobadas**: 246 por año, con cero pruebas
fallidas, sin verificar o sin cubrir en esa suite. Cada informe identifica
commit, hashes de binarios y harness.
[Archivos de la release](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v1.3.3).

| Revit | Aprobadas | Fallidas | Sin verificar | Sin cubrir en esta suite | Informe |
|---|---:|---:|---:|---:|---|
| 2023 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2023.json) |
| 2024 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2024.json) |
| 2025 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2025.json) |
| 2026 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2026.json) |
| 2027 | 246 | 0 | 0 | 0 | [JSON](https://github.com/HorizunGroup/horizun-revit-mcp/releases/download/v1.3.3/live-2027.json) |

Es una suite de release ejecutada en cinco versiones de Revit. Es evidencia del
mantenedor para esos casos; no son 1.230 funciones diferentes ni una comparación
contra otro producto. El [índice legible por máquina](docs/release-evidence.json)
registra enlaces y hashes de origen. GitHub CI comprueba continuamente el núcleo
y servidor, instalación Windows, consistencia del inventario/documentación y CodeQL.

El historial público comienza con
[v0.5.0 del 2026-08-02](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v0.5.0),
incluye [v1.0.0 del 2026-08-26](https://github.com/HorizunGroup/horizun-revit-mcp/releases/tag/v1.0.0)
y contiene **16 releases públicas sin marca de prerelease al 2026-09-15**.
La insignia de estrellas de GitHub muestra el recuento actual de la comunidad;
el [historial de releases](https://github.com/HorizunGroup/horizun-revit-mcp/releases)
y el [historial de commits](https://github.com/HorizunGroup/horizun-revit-mcp/commits/main/)
permiten comprobar el mantenimiento directamente.

## Códigos de error y estados de health

Toda llamada fallida trae un `error_code` estable y legible por máquina (snake_case, nunca datos del proyecto) en `structuredContent` y, como última línea, en el texto. Donde aplica se agregan `exception_type` (solo el nombre del tipo, p. ej. `InvalidOperationException`) y `limite_ms` (el límite que se alcanzó); el mensaje y los diagnósticos originales no cambian. Decide por el código, no por la redacción. `error_no_clasificado` es el código genérico y debe ser raro. Los errores de protocolo JSON-RPC (petición mal formada, herramienta desconocida) conservan su código numérico JSON-RPC; la tabla cubre los fallos de llamadas a herramientas.

| `error_code` | Significado | Qué hacer |
|---|---|---|
| `revit_no_disponible` | Revit no responde: no está abierto, el puente quedó obsoleto o la tubería se cerró. | Pide a la persona que abra Revit con el complemento cargado; no reintentes en bucle. |
| `revit_sin_documento` | Revit está abierto pero no hay documento activo. | Pide que abran el modelo y vuelve a llamar a `horizun_health`. |
| `revit_ocupado` | Revit está dentro de un comando o un diálogo modal y no atiende al puente. | Pide que cierren el diálogo; reintenta una vez tras `retry_after_ms`. |
| `cancelado` | Se canceló la solicitud o la operación. | Lee la evidencia de ejecución y transacción antes de repetir una escritura; la cancelación sola no prueba rollback. |
| `sin_vista` | La operación necesita una vista activa y no hay ninguna disponible. | Activa una vista apropiada y reintenta. |
| `documento_no_activo` | El documento indicado existe pero no está activo para esta operación. | Activa el documento previsto y repite el ensayo. |
| `documento_objetivo_ambiguo` | El destino es ambiguo o falta: varias instancias de Revit, o `target_document` ausente, sin coincidencia o cambiado. | Elige con `horizun_target` y manda el `target_document` exacto; no adivines. |
| `python_error_sintaxis` | El script de `horizun_execute_python` no compila. Se rechazó antes de abrir ninguna transacción; el modelo no se tocó. | Corrige la línea que indica el mensaje y reenvía; no hay nada que deshacer. |
| `python_excepcion` | El propio script lanzó una excepción (`exception_type`: ValueError, NameError, ...). | Lee el traceback, corrige el script y revisa el modelo antes de reintentar. |
| `api_revit_excepcion` | La API de Revit lanzó una excepción (`exception_type`: InvalidOperationException, ...), en un comando tipado o en un script. | Lee el mensaje; casi siempre el elemento, la vista o el estado no permite la operación. No la repitas igual. |
| `family_document_open` | La familia de origen está abierta en Revit; la recarga se rechazó antes de escribir. | Cierra la familia, guarda los cambios si hace falta o usa una copia y vuelve a intentar. |
| `transaccion_revertida` | La transacción se revirtió y el modelo conserva su estado anterior (`causa_error_code` nombra la causa original en scripts). | Corrige la causa y vuelve a ejecutar; mira antes `changes_applied` y `transaction_status`. |
| `elemento_no_encontrado` | Una vista, elemento, tipo o nivel que la llamada nombra no existe (o ya no). | Vuelve a listar (`horizun_list_elements`, `horizun_query_model`) y usa los ids o nombres vigentes. |
| `esquema_invalido` | Los argumentos incumplen el contrato de la herramienta (faltan, tipo o rango incorrecto, argumento desconocido). | Lee `schema_help` o `horizun://contract/tools/{tool}` y reenvía argumentos válidos. |
| `confirmacion_requerida` | Se envió una escritura sin el `confirmation_token` de su ensayo. | Ejecuta la misma llamada con `dry_run=true`, revisa el plan y aplica con su token. |
| `token_invalido_o_vencido` | El token no existe, venció, ya se usó, o el plan o el modelo cambiaron tras el ensayo. | Repite el ensayo con la solicitud que de verdad quieres y usa el token nuevo una sola vez. |
| `tiempo_agotado` | La llamada superó su límite (`limite_ms`). Puede seguir corriendo dentro de Revit. | Revisa `never_started` y el consejo de reintento del detalle; no reenvíes una escritura sin su idempotency key. |
| `annotation_budget_exhausted` | Annotate se detuvo en un punto de control al agotar su presupuesto de 120 segundos en Revit. El detalle incluye `budget_ms`, `elapsed_ms` y `rows_attempted`. | Si se abrió una transacción, revisa `rollback_confirmed`. Confirma el estado del modelo antes de reintentar con un lote más pequeño. |
| `permiso_python_requerido` | `horizun_execute_python` está apagado en esta máquina; solo el dueño lo puede habilitar. | Usa herramientas tipadas, o pide al dueño que lo habilite (botón Python ON/OFF en Revit). |
| `contrato_incompatible` | Servidor y complemento se compilaron con contratos o versiones distintas; no se envió nada. | Actualiza o redespliega el complemento y reinicia Revit. |
| `herramienta_desactivada` | La herramienta está apagada por la configuración o por su conjunto. | Pide al dueño que la habilite; usa otra herramienta mientras tanto. |
| `permiso_insuficiente` | El perfil de permisos no permite esta escritura. | Pide al dueño el perfil necesario; no lo rodees. |
| `llamada_en_cuarentena` | La misma llamada ya falló de una forma que una persona marcó como no reintentable. | Cambia la llamada o consulta a quien la puso en cuarentena. |
| `adjunto_imagen_fallido` | Revit produjo la imagen pero el servidor no pudo adjuntarla. | Lee `image_path` en el resultado, o captura con un `pixel_size` menor. |
| `respuesta_demasiado_grande` | La respuesta superó el límite de bytes; el comando corrió pero su respuesta se descartó. | Pide menos (categoría más estrecha, menos ids, una vista) en vez de repetir. |
| `transporte_fallido` | La tubería hacia Revit falló por una razón que no es timeout ni Revit cerrado. | Lee `phase` y `retry` del detalle antes de decidir reenvíar. |
| `captura_sin_resultado` | `horizun_capture_view` no exportó nada utilizable (sin archivo, vacío, o mayor que `pixel_size`). | No des la vista por vista; encuadra menos elementos o cambia la vista y reintenta. |
| `precondicion_no_cumplida` | La llamada es válida pero el modelo, el plan o el destino no la permite; el comando declinó antes de actuar (no se cambió nada). | Lee el mensaje para ver la condición que falta (archivo existente con `overwrite=false`, tipo de vista incorrecto, documento de solo lectura, plan inutilizable), corrígela y reenvía. |
| `entrada_invalida` | La entrada tiene forma válida pero un valor no se puede resolver o no es válido para la operación, por ejemplo un nivel inexistente en un filtro de tabla. | Lee los errores por acción y los candidatos, corrige el valor y repite el ensayo; consulta `transaction_status` para saber si comenzó una transacción. |
| `verificacion_fallida` | La escritura corrió pero la verificación posterior no la pudo confirmar; no se declara éxito y el estado puede ser incierto. | Relee el modelo (`horizun_verify_changes`, una consulta) antes de reintentar; no repitas la escritura a ciegas. |
| `archivo_no_accesible` | Un archivo que la llamada necesita no se puede leer, interpretar o escribir (ruta, permisos, formato). | Revisa la ruta y que el archivo esté cerrado y sea válido, y reenvía. |
| `familia_version_mas_nueva` | Una familia (.rfa) está guardada en un Revit más nuevo que el que corre; Revit no la carga y la API no la baja de versión. Se revisa antes de abrir nada. | Usa una de las `alternates` que trae la respuesta (la misma familia guardada en este año o antes) o pide una copia guardada en el año de este Revit. |
| `miembro_de_grupo` | Un elemento que se pidió mover, girar o cambiar de línea es miembro de un grupo de modelo. Revit deja quieto un miembro de grupo sin dar error (MoveElement vuelve y luego el commit falla si el grupo tiene varias instancias). Se rechaza antes de escribir nada. | `horizun_manage_groups` con `operation=edit_type_members` y `scope=type` (todas las instancias, verificadas una a una, con las conexiones MEP externas), u `operation=ungroup` si solo debe cambiar esa copia. |
| `documentacion_violada` | `horizun_execute_python` con `documentation_only=true` cambió algo fuera de vistas, láminas, anotación y elementos propios de vista (un muro movido, un tipo editado) o no pudo demostrar que no lo hizo; toda la corrida se revirtió y se releyó. La respuesta nombra cada id ofensor en `documentation_only_check.offending_ids`. | Usa la herramienta tipada para ese cambio, o quita `documentation_only` si el cambio al modelo es intencional. |
| `error_interno` | Una excepción fuera de la API de Revit (un fallo del puente); `exception_type` la nombra. | Repórtala con `exception_type` y el nombre de la herramienta; no la repitas en bucle. |
| `error_no_clasificado` | Nada de lo anterior aplicó. Debe ser rara; cada caso es un hueco del catálogo. | Lee el mensaje y repórtalo para añadirle un código. |

Los códigos de error que solo levanta la edición Pro (`licencia_requerida`, `colision_tipos_destino`, `ifc_strict`, `ifc_strict_post`) se documentan con la edición Pro.

`horizun_execute_python` compila el script antes de que pueda abrirse una transacción: un error de sintaxis devuelve `python_error_sintaxis` y el modelo no se toca. Una ejecución fallida se distingue como `python_excepcion` (el script), `api_revit_excepcion` (la API de Revit) o `transaccion_revertida` (Revit descartó el trabajo).

`horizun_health` siempre responde, nunca con una excepción. Junto a sus campos de siempre informa `estado`, y `retry_after_ms` cuando lo correcto es esperar:

| `estado` | Significado | Qué hacer |
|---|---|---|
| `listo` | Revit respondió y hay documento activo. | Continúa. |
| `sin_documento` | Revit respondió; no hay documento abierto. | Pide que abran el modelo. |
| `arrancando` | Hay un proceso de Revit pero su puente aún no está arriba. | Espera `retry_after_ms` (5 s) y reintenta una vez. |
| `host_cerrado` | No hay un proceso de Revit abierto. | Pide a la persona abrir Revit con el complemento cargado. |
| `ocupado` | El puente existe pero Revit no respondió a tiempo o está en un diálogo. | Espera `retry_after_ms` (10 s), reintenta una vez y luego pregunta a la persona. |
| `puente_inaccesible` | No hay puente utilizable (por ejemplo, contrato distinto). | Espera `retry_after_ms` (15 s) una vez; si persiste, avisa a la persona con el `error_code`. |
| `varias_instancias` | Hay varios Revit con el puente y ninguno elegido; `instances` trae pid, año, build y documento activo, y `target_required` es true. | Llama `horizun_target` con el pid de la instancia que tiene el modelo y luego `horizun_health` otra vez. |

## Versión y compatibilidad

| Pregunta | Fuente autorizada |
|---|---|
| Última descarga estable | [Release más reciente](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest), con fecha y archivos |
| Versión de este código fuente | [Directory.Build.props](Directory.Build.props) |
| Versión realmente cargada | `horizun_health`: versión, commit, año de Revit y documento activo |
| Protocolo MCP implementado | **2025-11-25**; revisiones anteriores en [ProtocolNegotiation.cs](src/Horizun.Server/ProtocolNegotiation.cs) |
| Metadatos publicados del registro | [Registro oficial](https://registry.modelcontextprotocol.io/v0.1/servers/io.github.HorizunGroup%2Fhorizun-revit-mcp/versions/latest) |

La compatibilidad con MCP 2026-07-28 está pendiente. Una release reciente del
producto no implica soporte de esa revisión. Los buscadores y directorios pueden
mostrar capturas antiguas: consulta la release antes de elegir versión. Para
actualizar una instalación publicada, ejecuta el nuevo Setup con Revit cerrado.

## Alcance y referencias

Los resultados tipados identifican verificación, resultados parciales y
cobertura. Los vínculos descargados y los casos no soportados por API, plantilla
o exportador se reportan explícitamente. Consulta [TOOLS.md](docs/TOOLS.md) para los límites de cada operación.
La [metodología de benchmark](docs/BENCHMARK.md) distingue la puntuación histórica
de diseño de la evidencia de ejecución real.

[Compilar desde fuente](docs/BUILDING.md) · [Contribuir](CONTRIBUTING.md) ·
[Instrucciones para agentes](AGENTS.md) · [Resumen para LLM](llms.txt) ·
[Seguridad](docs/security-model.md) · [Privacidad](docs/PRIVACY.md) ·
[Política de releases](docs/RELEASE-POLICY.md)

Hecho en Colombia 🇨🇴 y mantenido por Horizun Group como parte de
[Horizun Hub](https://horizunhub.com). El puente es neutral respecto a
organizaciones: los estándares y catálogos del proyecto son entradas. El
[paquete de estándares](standards/README.md) opcional ofrece ejemplos editables.

**Apache-2.0:** [LICENSE](LICENSE), [NOTICE](NOTICE), [avisos de terceros](THIRD-PARTY-NOTICES.md).
La API de Revit no se redistribuye. Autodesk y Revit son marcas de Autodesk;
este proyecto no está afiliado, avalado ni patrocinado por Autodesk.
