# Horizun Revit MCP — modela, documenta, coordina y entrega en Revit

**Hecho en Colombia 🇨🇴 por Horizun Group.**

Horizun Revit MCP es un servidor MCP y add-in de Windows gratuito y de código
abierto para **Autodesk Revit 2023–2027**. Su catálogo completo contiene
**122 herramientas** <!--inventory:tools--> con **405 suboperaciones y modos de despacho nombrados** <!--inventory:operations-->
para modelado arquitectónico y estructural, MEP, familias paramétricas, planos,
CAD a BIM, auditoría, cantidades, Excel, Power BI y exportación.

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
[Instalar con Claude Desktop Free](#video-de-instalación-claude-desktop-free) ·
[Todas las herramientas](#catálogo-completo-de-herramientas) ·
[Suboperaciones](#suboperaciones-y-modos) · [Pruebas](#probado-en-revit-evidencia-publicada) · [Instalar](#instalar)

## El producto en cifras

| Superficie | Qué ofrece | Dónde comprobarlo |
|---|---|---|
| Entradas MCP | **122 herramientas** <!--inventory:tools-->, incluidas **47 de solo lectura** <!--inventory:reads--> y **75 con posibles efectos** <!--inventory:writes--> | [Inventario generado](docs/inventory.json) y catálogo completo más abajo |
| Acciones internas | **405 suboperaciones y modos de despacho nombrados** <!--inventory:operations--> dentro de herramientas compuestas | Valores exactos de los selectores más abajo |
| Cobertura Revit | 2023, 2024, 2025, 2026 y 2027 | Cinco add-ins y sus informes de pruebas versionados |
| Contenido nuevo | 26 clases de creación de elementos; autoría RFA paramétrica; planificación estructural y MEP | [Referencia de familias](docs/FAMILY-AUTHORING.md) |
| Planos y entregables | 24 acciones de vistas/láminas, 10 acciones de anotación, tablas nativas y distribución de láminas | [Producción de planos](docs/PLANIMETRY-PRODUCTION.md) |
| Formatos de exportación | PDF, DWG, IFC, NWC, FBX, imágenes y CSV de tablas | `horizun_export` |
| Ejemplo de release verificada | 1.230 ejecuciones de pruebas aprobadas entre cinco años de Revit en **v1.3.3** | [Informes publicados](#probado-en-revit-evidencia-publicada) |
| Instalación | Setup de Windows con runtime incluido; sin Git, Visual Studio ni SDK de .NET para el usuario | [Pasos finales por cliente](docs/CLIENTS.md) |

Los recuentos describen la superficie completa del producto. El perfil de
permisos y los paquetes elegidos determinan qué herramientas anuncia una sesión;
más abajo se explican los perfiles medidos de 70, 79 y 80 herramientas.

## Demostración: de un PDF de planos a un modelo Revit

[![Ver la demostración de Horizun: de planos PDF a un modelo Revit con IA](https://i.ytimg.com/vi/tlFs5p3EM4M/hqdefault.jpg)](https://www.youtube.com/watch?v=tlFs5p3EM4M)

**[Planos PDF → modelo Revit con IA — ver en YouTube](https://www.youtube.com/watch?v=tlFs5p3EM4M)**
es una demostración publicada en español por el canal Horizun Hub. Presenta un
flujo de PDF a Revit dirigido por un cliente de IA. La interpretación del PDF
corresponde al cliente/modelo; Horizun aporta las operaciones ejecutadas en
Revit. El flujo tipado DWG descrito más abajo consulta geometría CAD y reglas
versionadas.

## Qué puedes crear y entregar

### Modelado arquitectónico, estructural y MEP

`horizun_create_elements` admite lotes con dependencias de niveles, ejes, muros,
suelos, cielos, cubiertas, habitaciones, instancias de familia, vigas y columnas,
sistemas de vigas, cimentaciones de muro, conductos, tuberías, conduit,
bandejas, accesorios, sistemas MEP, aperturas, huecos verticales, separadores de
habitación, muros por perfil, conjuntos de desplazamiento y escaleras soportadas.

El desglose lista las 26 clases de creación y cinco opciones de accesorios MEP.
`horizun_transform_elements` permite mover, copiar, rotar, gestionar uniones de
muros, cambiar tipos/curvas y ajustar etiquetas. `horizun_manage_system_types`
gestiona estructuras de capas de materiales y preferencias de unión MEP.
Los planificadores estructural y MEP generan peticiones para esos mismos creadores.

### Familias paramétricas nuevas con conectores MEP

`horizun_create_family` crea un `.rfa` nuevo desde una plantilla `.rft` instalada:
parámetros, fórmulas, tipos, sólidos/vacíos por extrusión, mezcla, revolución,
barrido y mezcla barrida; planos de referencia y cotas etiquetadas; familias
anidadas soportadas; y conectores de tubería, conducto, electricidad, conduit o
bandeja. Verifica el guardado y la carga opcional al proyecto.
`horizun_family_apply` modifica datos de familias existentes controlando que su
geometría no cambie. [Ejemplos RFA y alcance de plantillas](docs/FAMILY-AUTHORING.md).

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

### DWG a BIM con seguimiento de revisiones

Consulta instancias DWG, capas y curvas. Proporciona un conjunto versionado de
requisitos para convertir el contenido del dibujo en un plan BIM ordenado.
Ensaya y aplica el plan con herramientas tipadas, registrando huellas del origen
y procedencia en los elementos creados. Audita el modelo contra el dibujo;
planea y aplica revisiones posteriores del DWG identificando los cambios
manuales del modelo que requieren revisión.
Son siete herramientas CAD dedicadas, listadas individualmente más abajo.
[Ejemplos DWG a BIM](docs/DWG-TO-BIM.md).

### Auditoría, coordinación, cantidades e intercambio de datos

Consulta anfitrión y vínculos cargados por categoría, familia/tipo, nivel,
parámetros y límites espaciales. Diagnostica salud del modelo, audita requisitos
proporcionados y corrige parámetros con verificación. Coordina interferencias
con hallazgos persistentes, asignaciones, decisiones y resolución medida en el
modelo. Planea refuerzo, aplica las peticiones soportadas y audita el resultado.

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

Aquí aparece cada herramienta MCP, agrupada por el trabajo que realiza. Estos
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
| `horizun_document_session` | Inspeccionar, abrir, guardar, guardar como y cerrar documentos mediante operaciones explícitas. |
| `horizun_file_info` | Leer cabeceras RVT/RFA, versión y datos de trabajo compartido sin abrirlos ni actualizarlos. |
| `horizun_health` | Consultar estado del puente, documento activo, año de Revit, versión y commit cargados. |
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
| `horizun_apply_ifc_plan` | Ejecutar un plan de IFC mediante comandos tipados que ensayan y releen su propio trabajo. |
| `horizun_audit_access` | Reportar qué puede hacer este puente en esta máquina y quién lo decidió. |
| `horizun_audit_model` | Evaluar requisitos proporcionados del modelo con hallazgos, cobertura y evidencia de controles previos. |
| `horizun_capture_view` | Exportar una vista como imagen para que el cliente pueda revisarla visualmente. |
| `horizun_verify_changes` | Revisar lo que cambió la última escritura en busca de conflictos espaciales y devolver una imagen con ellos resaltados. |
| `horizun_delete_verified` | Eliminar IDs explícitos o purgar contenido sin uso; anticipar dependencias y verificar eliminaciones. |
| `horizun_list_elements` | Listar y paginar elementos del anfitrión y vínculos cargados, identificando su modelo. |
| `horizun_model_scan` | Medir salud del modelo, advertencias, worksets, vínculos, familias, vistas y candidatos de limpieza. |
| `horizun_navigate` | Seleccionar elementos, limpiar la selección, encuadrar y abrir vistas. |
| `horizun_plan_from_ifc` | Planificar elementos de Revit desde un IFC bajo un mapeo declarado, sin importarlo. |
| `horizun_query_model` | Filtrar por categoría, tipo, nivel, parámetros y límites espaciales; seleccionar campos y obtener resúmenes agrupados o compactos. |
| `horizun_validate_ids` | Comprobar un modelo contra una especificación IDS y reportar cada requisito que incumple. |
| `horizun_query_classification` | Leer las tablas de keynotes y códigos de ensamble, su uso en el modelo, códigos faltantes y sin uso, y las tablas de búsqueda de familias. |
| `horizun_model_diff` | Tomar instantáneas y comparar entregas: elementos añadidos, borrados y modificados con parámetros, vista coloreada, resumen y historial de calidad. |
| `horizun_code_check` | Revisar el modelo contra conjuntos de requisitos declarativos, incluidas reglas colombianas de accesibilidad, evacuación e iluminación, con not_decidable en vez de suposiciones. |
| `horizun_undo` | Listar y deshacer el último lote verificado de Horizun, negándose si el modelo o el archivo cambiaron desde entonces. |

### Creación de modelos, parámetros y familias

| Herramienta | Capacidad |
|---|---|
| `horizun_bind_shared_param` | Vincular parámetros compartidos a categorías y comprobar su comportamiento entre grupos. |
| `horizun_copy_between_documents` | Copiar elementos entre documentos abiertos, conservando identidad y reportando lo sustituido. |
| `horizun_create_elements` | Crear niveles, ejes, muros, losas, cubiertas, habitaciones, instancias, estructura, redes MEP, aperturas y escaleras en lotes tipados. |
| `horizun_create_family` | Crear archivos RFA paramétricos nuevos: sólidos/vacíos, parámetros, fórmulas, tipos, familias anidadas y conectores MEP; cargarlos opcionalmente. |
| `horizun_family_apply` | Aplicar cambios de familia en una transacción con control de geometría y reversión ante desviaciones. |
| `horizun_manage_materials` | Leer, crear y asignar materiales con sus datos de apariencia e identidad. |
| `horizun_manage_system_types` | Duplicar tipos de sistema; editar parámetros, capas de muros/losas/cubiertas/cielos y preferencias de unión MEP. |
| `horizun_set_keynote` | Asignar notas clave con alcance explícito sobre instancias y tipos. |
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
| `horizun_plan_views` | Planear vistas por habitación y conjuntos de entrega; seguir estados, aprobaciones e invalidaciones. |
| `horizun_query_planimetry` | Consultar láminas, vistas, ubicaciones, anotaciones y referencias de planos con geometría y cobertura. |
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

### CAD / DWG a BIM y actualizaciones de revisión

| Herramienta | Capacidad |
|---|---|
| `horizun_apply_cad_plan` | Construir el plan mediante comandos tipados; comprobar hashes de origen y registrar procedencia CAD en los elementos. |
| `horizun_apply_cad_update` | Aplicar cambios de revisión soportados y conservar la procedencia para la siguiente actualización. |
| `horizun_audit_cad_model` | Comparar dibujo y modelo mediante procedencia, geometría y diferencias medidas. |
| `horizun_cad_connect` | Construir las uniones que declara un dibujo, colocando el accesorio que pide cada nudo y verificándolo. |
| `horizun_cad_extract` | Leer un DWG vinculado directamente -capas, líneas, arcos, textos y bloques- sin importarlo. |
| `horizun_cad_networks` | Derivar la red que describe un dibujo: qué extremos se encuentran, qué va entre ellos y cuáles quedan abiertos. |
| `horizun_cad_review` | Reportar lo que una conversión no pudo resolver, con la evidencia de cada candidato retenido. |
| `horizun_cad_symbols` | Listar los símbolos de bloque que define un dibujo y dónde se coloca cada uno. |
| `horizun_cad_unit_instances` | Encontrar unidades repetidas en un dibujo y la transformación que coloca cada aparición. |
| `horizun_manage_cad_links` | Listar, añadir, recargar y cambiar rutas de vínculos CAD con estado medido. |
| `horizun_plan_cad_update` | Planear cambios entre revisiones DWG e identificar modificaciones manuales del modelo que requieren revisión. |
| `horizun_plan_from_cad` | Interpretar un DWG con reglas versionadas proporcionadas y producir un plan BIM ordenado con omisiones y procedencia. |
| `horizun_query_cad` | Consultar instancias DWG, capas, curvas, perfiles y cobertura legible. |

### Estructura, refuerzo, MEP y coordinación

| Herramienta | Capacidad |
|---|---|
| `horizun_acc_upload_status` | Leer evidencia de los registros de Desktop Connector sobre asignación de archivos locales a carpetas ACC. |
| `horizun_apply_reinforcement` | Aplicar planes de refuerzo soportados mediante comprobaciones tipadas y verificación. |
| `horizun_audit_reinforcement` | Auditar refuerzo contra requisitos proporcionados y reportar cobertura. |
| `horizun_clash` | Detectar interferencias en un alcance definido, planear penetraciones soportadas y registrar hallazgos. |
| `horizun_connect_mep` | Conectar o desconectar conectores MEP nombrados, negándose a cerrar un hueco visible moviendo geometría. |
| `horizun_coordination` | Seguir hallazgos, asignaciones, decisiones, evidencia y resolución o reaparición medida en el modelo. |
| `horizun_manage_links` | Añadir y gestionar vínculos/instancias de Revit, rutas, carga y fijación. |
| `horizun_plan_mep` | Planear rutas y accesorios de tuberías/conductos; consultar redes mediante conectividad real de conectores. |
| `horizun_plan_reinforcement` | Planear refuerzo a partir de anfitriones y requisitos proporcionados. |
| `horizun_plan_structure` | Planear columnas en intersecciones de ejes y vigas entre cruces consecutivos. |
| `horizun_query_structure` | Consultar miembros, anfitriones, recubrimientos, barras, sistemas de refuerzo, conexiones y cantidades. |
| `horizun_structural_connections` | Leer y aplicar tipos de conexión estructural entre elementos de estructura. |
| `horizun_mep_routing` | Leer y editar preferencias de enrutamiento y tamaños de catálogo, redimensionar tramos MEP y planear tamaños por caudal. |
| `horizun_electrical` | Listar tableros y circuitos, crear circuitos, asignar tableros, añadir o quitar elementos y crear cuadros de carga. |
| `horizun_resolve_clash` | Proponer y aplicar el menor movimiento seguro para clashes MEP, probado redetectando y revertido si aparece un clash nuevo. |
| `horizun_federation_check` | Comprobar que cada modelo federado contiene solo sus disciplinas, sus vínculos esperados y coordenadas compartidas coherentes. |

### Capas, grupos y terreno

| Herramienta | Capacidad |
|---|---|
| `horizun_split_floor_loops` | Separar un suelo por bucles de boceto conservando desfases de altura. |
| `horizun_split_multilayer_walls` | Separar capas de muros conservando la identidad del muro original del núcleo y sus elementos hospedados. |
| `horizun_split_multilayer_slabs` | Separar capas de materiales de suelos/cielos, conservando perfiles y con reversión por losa. |
| `horizun_rectangularize_walls` | Descomponer geometría ortogonal soportada de muros en fragmentos rectangulares. |
| `horizun_ungroup_and_mark` | Desagrupar grupos del modelo registrando el grupo original de cada miembro. |
| `horizun_regroup_by_param` | Reconstruir grupos del modelo mediante un parámetro de agrupación. |
| `horizun_copy_slab_elevations` | Transferir la superficie de un suelo modificado a suelos destino explícitos. |
| `horizun_embed_floors_in_toposolid` | Integrar contornos y elevaciones de suelos en un sólido topográfico. |
| `horizun_grade_toposolid_around_floors` | Conformar terreno alrededor de suelos con desfases, líneas de quiebre y taludes definidos. |
| `horizun_manage_groups` | Listar, crear, redefinir, renombrar, duplicar y cambiar tipos de grupo de modelo, preguntando antes de tocar otras instancias. |
| `horizun_manage_worksets` | Listar, crear y renombrar worksets, mover elementos entre ellos y ajustar su visibilidad por vista. |

### Cantidades, Excel, Power BI y entregables

| Herramienta | Capacidad |
|---|---|
| `horizun_quantities` | Medir volúmenes y cómputos de materiales con unidades, agrupación y procedencia del modelo. |
| `horizun_budget_compare` | Comparar cantidades del modelo con una base Excel; escribir salidas aprobadas a Excel/Power BI de forma opcional. |
| `horizun_catalog_lookup` | Resolver elementos de un catálogo proporcionado con estado de hoja y hash del archivo de origen. |
| `horizun_excel_read_rows` | Leer filas XLSX, tipos y valores de fórmulas en caché sin Excel ni COM. |
| `horizun_excel_write_rows` | Añadir filas XLSX, crear respaldo y releer las celdas escritas sin Excel ni COM. |
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

### Gestión de información ISO 19650 y entrega openBIM

| Herramienta | Capacidad |
|---|---|
| `horizun_project_context` | Validar, preguntar y redactar el contexto ISO 19650 del proyecto: designación, EIR/BEP/MIDP, estados del CDE, nomenclatura, entrega. |
| `horizun_information_container` | Nombrar, sellar, verificar, inspeccionar y promover contenedores de información entre carpetas WIP, Compartido, Publicado y Archivado. |
| `horizun_deliver_ifc` | Exportar un IFC y demostrarlo: IDS sobre el archivo, cobertura del mapeo de Psets, georreferencia, BCF de fallos y contenedor sellado. |
| `horizun_cde_cloud` | Leer un CDE en la nube (Autodesk Construction Cloud o un servidor OpenCDE) por estado y cruzarlo con el MIDP, en solo lectura. |

<!-- END TOOL CATALOG -->

## Suboperaciones y modos

Una herramienta MCP puede ejecutar muchas acciones. Crear un muro, una tubería
y una escalera son opciones de `horizun_create_elements`; crear una sección y
colocar una tabla son acciones diferentes de `horizun_manage_views`.

La tabla contiene **405 suboperaciones y modos de despacho nombrados** <!--inventory:operations-->
en 26 herramientas compuestas. Cada opción se cuenta una vez por herramienta,
propiedad selectora y valor, incluidos selectores anidados. Las rutas repetidas
del esquema `oneOf` se cuentan una sola vez. Algunos selectores afinan otra
acción: estas cifras describen el vocabulario operativo expuesto, no 208
herramientas MCP adicionales de primer nivel.

<!-- BEGIN SUBOPERATIONS -->
| Herramienta | Selector | Suboperaciones y modos nombrados |
|---|---|---|
| `horizun_document_session` | `operation` | `open`, `save`, `save_as`, `close`, `inspect` |
| `horizun_repair_memory` | `operation` | `list`, `advice`, `observe`, `remedy`, `quarantine`, `release` |
| `horizun_selection_exchange` | `operation` | `publish`, `read`, `clear`, `capabilities` |
| `horizun_audit_model` | `operation` | `save`, `save_as`, `sync_with_central`, `export`, `publish`, `close_with_save`, `batch_open_close` |
| `horizun_delete_verified` | `mode` | `ids`, `purge_unused` |
| `horizun_navigate` | `operation` | `select`, `clear_selection`, `zoom`, `select_and_zoom`, `open_view` |
| `horizun_validate_ids` | `operation` | `precheck`, `validate` |
| `horizun_query_classification` | `operation` | `keynote_table`, `assembly_code`, `family_lookup_tables`, `unused_codes`, `missing_codes` |
| `horizun_model_diff` | `operation` | `snapshot`, `list`, `compare`, `colorize`, `explain`, `record_quality`, `quality_trend` |
| `horizun_undo` | `operation` | `list`, `undo_last` |
| `horizun_create_elements` | `kind` | `level`, `grid`, `wall`, `floor`, `ceiling`, `roof`, `room`, `family_instance`, `structural_framing`, `structural_column`, `duct`, `pipe`, `conduit`, `cable_tray`, `fitting`, `wall_opening`, `slab_opening`, `beam_system`, `wall_foundation`, `accessory_inline`, `mep_system`, `shaft`, `room_separator`, `wall_profile`, `displacement`, `stairs` |
| `horizun_create_elements` | `fitting` | `elbow`, `union`, `transition`, `tee`, `takeoff`, `cross` |
| `horizun_create_family` | `kind` | `extrusion`, `blend`, `revolution`, `sweep`, `swept_blend`, `pipe`, `duct`, `electrical`, `conduit`, `cable_tray`, `symbolic`, `model` |
| `horizun_manage_materials` | `operation` | `create`, `duplicate`, `update` |
| `horizun_transform_elements` | `operation` | `wall_join`, `move`, `copy`, `rotate`, `mirror`, `pin`, `unpin`, `change_type`, `change_type_by_rule`, `realign_wall_sketch`, `set_curve`, `move_tag_head`, `set_tag_leader`, `array_linear`, `array_radial`, `rename_level` |
| `horizun_manage_parameters` | `operation` | `list_bindings`, `create_shared`, `create_project`, `rebind`, `remove_binding`, `global_list`, `global_create`, `global_set`, `global_delete` |
| `horizun_manage_curtain` | `operation` | `read`, `add_grid_line`, `remove_grid_line`, `set_mullions`, `set_panel_type` |
| `horizun_manage_curtain` | `mode` | `add`, `remove` |
| `horizun_slab_shape` | `operation` | `read`, `add_point`, `add_split_line`, `modify_subelement`, `reset_shape` |
| `horizun_manage_phases` | `operation` | `list`, `element_status`, `set_element_phases`, `create_phase_filter`, `edit_phase_filter`, `rename_phase`, `create_phase`, `assign_design_option` |
| `horizun_manage_assemblies_parts` | `operation` | `list`, `create_parts`, `divide_parts`, `exclude_parts`, `restore_parts`, `dissolve_parts`, `create_assembly`, `assembly_views`, `disassemble` |
| `horizun_manage_styles` | `operation` | `list_object_styles`, `set_object_style`, `create_subcategory`, `list_line_styles`, `create_line_style`, `list_line_patterns`, `create_line_pattern`, `list_fill_patterns`, `create_fill_pattern` |
| `horizun_manage_units` | `operation` | `read`, `set`, `project_information`, `base_points` |
| `horizun_manage_views` | `operation` | `create_floor_plan`, `create_ceiling_plan`, `create_structural_plan`, `create_area_plan`, `create_3d`, `create_drafting`, `create_section`, `create_elevation`, `create_callout`, `duplicate_view`, `apply_template`, `set_phase`, `assign_scope_box`, `set_view_range`, `set_crop`, `set_annotation_crop`, `create_sheet`, `create_placeholder_sheet`, `convert_placeholder_sheet`, `duplicate_sheet`, `place_view`, `place_schedule`, `set_viewport_type`, `align_viewports`, `create_filter`, `apply_filter`, `color_by_value`, `set_element_overrides`, `hide_elements`, `isolate_elements`, `reset_temporary`, `set_category_visibility`, `create_legend`, `place_legend_component`, `edit_filter`, `order_filters`, `explain_graphics`, `create_template`, `set_template_controls`, `sheet_set_list`, `sheet_set_create`, `sheet_set_update`, `sheet_set_delete` |
| `horizun_manage_views` | `mode` | `center`, `center_x`, `center_y`, `left`, `right`, `top`, `bottom` |
| `horizun_plan_views` | `operation` | `room_views`, `deliverable_set`, `delivery_open`, `delivery_status`, `delivery_record`, `delivery_approve`, `delivery_invalidate` |
| `horizun_query_planimetry` | `mode` | `inventory`, `sheets`, `views`, `placements`, `annotations`, `references` |
| `horizun_fix_planimetry` | `operation` | `set_view_template`, `set_view_scale`, `rename_view`, `rename_sheet`, `place_title_block`, `move_viewport`, `move_schedule`, `clear_element_override`, `set_crop` |
| `horizun_plan_annotations` | `operation` | `auto_tags`, `intent_dimension`, `dimension_set`, `auto_dimension_grids`, `auto_dimension_levels`, `auto_dimension_curtain_walls`, `auto_dimension_openings` |
| `horizun_annotate` | `operation` | `text`, `tag`, `dimension`, `angular_dimension`, `radial_dimension`, `diameter_dimension`, `arc_length_dimension`, `spot_elevation`, `spot_coordinate`, `spot_slope` |
| `horizun_query_detail_2d` | `mode` | `resources`, `elements` |
| `horizun_detail_2d` | `operation` | `create_detail_line`, `create_detail_arc`, `create_detail_polyline`, `create_filled_region`, `create_masking_region`, `place_detail_component`, `place_symbol`, `set_line_style` |
| `horizun_manage_revisions` | `operation` | `create_revision`, `update_revision` |
| `horizun_manage_schedules` | `operation` | `create`, `duplicate`, `rename`, `add_fields`, `remove_fields`, `set_field`, `set_filters`, `set_sorting`, `set_options` |
| `horizun_manage_schedules` | `kind` | `material_takeoff`, `sheet_list`, `view_list`, `revision_schedule`, `keynote_legend` |
| `horizun_cad_connect` | `fitting` | `direct`, `elbow`, `tee`, `cross`, `transition`, `none` |
| `horizun_manage_cad_links` | `operation` | `list`, `add`, `reload`, `repoint` |
| `horizun_query_cad` | `mode` | `instances`, `layers`, `geometry`, `coverage`, `profile`, `blocks` |
| `horizun_connect_mep` | `operation` | `connect`, `disconnect` |
| `horizun_coordination` | `operation` | `list`, `update`, `export`, `import`, `import_navisworks`, `show`, `evidence` |
| `horizun_manage_links` | `operation` | `list`, `unload`, `reload`, `pin`, `unpin`, `add`, `add_instance`, `change_path` |
| `horizun_plan_mep` | `operation` | `route_run`, `network_census` |
| `horizun_plan_mep` | `kind` | `pipe`, `duct` |
| `horizun_plan_structure` | `operation` | `columns_on_grid_intersections`, `beams_along_grids` |
| `horizun_query_structure` | `mode` | `members`, `hosts`, `covers`, `rebar`, `reinforcement_systems`, `connections`, `coverage`, `quantities` |
| `horizun_mep_routing` | `operation` | `read`, `set_rules`, `add_sizes`, `remove_sizes`, `resize`, `size_by_flow` |
| `horizun_mep_routing` | `action` | `add`, `remove`, `move` |
| `horizun_electrical` | `operation` | `list_panels`, `list_circuits`, `create_circuit`, `assign_panel`, `add_to_circuit`, `remove_from_circuit`, `panel_schedule` |
| `horizun_resolve_clash` | `operation` | `propose`, `apply` |
| `horizun_manage_groups` | `operation` | `list`, `create`, `add_members`, `remove_members`, `rename_type`, `duplicate_type`, `swap_type`, `ungroup`, `convert_to_link` |
| `horizun_manage_worksets` | `operation` | `list`, `create`, `rename`, `move_elements`, `set_default`, `visibility` |
| `horizun_quantities` | `mode` | `volume`, `takeoff` |
| `horizun_catalog_lookup` | `operation` | `leaf`, `search`, `bsdd_search`, `bsdd_search_dictionary`, `bsdd_class`, `bsdd_property`, `bsdd_dictionaries` |
| `horizun_link_schedule` | `operation` | `import`, `match`, `write`, `status_view` |
| `horizun_execute_plan` | `kind` | `plan`, `section`, `elevation` |
| `horizun_promote_script` | `operation` | `list`, `show`, `propose`, `review`, `approve`, `activate`, `deactivate`, `source`, `resolve`, `invocation` |
| `horizun_run_procedure` | `operation` | `start`, `advance`, `decide`, `record`, `reconcile`, `status`, `abandon` |
| `horizun_project_context` | `operation` | `schema`, `validate`, `questions`, `draft`, `elicit`, `ids_from_loin` |
| `horizun_information_container` | `operation` | `name`, `stamp`, `verify`, `inspect`, `transition`, `transmittal`, `record_review`, `register` |
| `horizun_cde_cloud` | `operation` | `list_projects`, `list_states`, `inspect`, `versions` |
<!-- END SUBOPERATIONS -->

Otras opciones tipadas incluyen los siete valores de `horizun_export.format`:
`pdf`, `dwg`, `ifc`, `nwc`, `fbx`, `image`, `schedule_csv`. El inventario también
registra **1506 apariciones de valores de argumentos enumerados** <!--inventory:enumerated_variants-->
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
cubren recetas de familias, documentación de habitaciones, auditorías y entregas.
Estas funciones del protocolo y sus prompts son adicionales al recuento de herramientas.

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
o exportador se reportan explícitamente. Consulta [TOOLS.md](docs/TOOLS.md) y el
[alcance de familias](docs/FAMILY-AUTHORING.md) para los límites de cada operación.
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
