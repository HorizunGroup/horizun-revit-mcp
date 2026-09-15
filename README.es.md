# Horizun Revit MCP — automatización de Autodesk Revit

**[English](README.md)** · **Español**

Conecta un cliente MCP con Autodesk Revit para consultar modelos, realizar
ediciones BIM verificadas, crear familias y preparar planos, cantidades y
exportaciones. Gratuito y de código abierto, Apache-2.0. Hecho en Colombia 🇨🇴,
parte de [Horizun Hub](https://horizunhub.com).

[![ci](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/ci.yml?branch=main&label=ci&logo=githubactions&logoColor=white)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/ci.yml) [![codeql](https://img.shields.io/github/actions/workflow/status/HorizunGroup/horizun-revit-mcp/codeql.yml?branch=main&label=codeql&logo=github)](https://github.com/HorizunGroup/horizun-revit-mcp/actions/workflows/codeql.yml) [![release](https://img.shields.io/github/v/release/HorizunGroup/horizun-revit-mcp?label=release&color=0696D7)](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest) [![Revit 2023–2027](https://img.shields.io/badge/Revit-2023%E2%80%932027-0696D7)](#instalar) [![MCP registry](https://img.shields.io/badge/MCP%20registry-io.github.HorizunGroup%2Fhorizun--revit--mcp-6E56CF)](https://registry.modelcontextprotocol.io/) [![license Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

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

## Qué puedes hacer

| Tarea | Ejemplos y referencia |
|---|---|
| Consultar y auditar modelos | Consultas de anfitrión/vínculos, cantidades, interferencias, tablas y diagnóstico. [Primera auditoría de lectura](docs/QUICK-START-BIM.md) |
| Crear y editar elementos BIM | Niveles, ejes, arquitectura, estructura, MEP, parámetros y planes ordenados. [Herramientas](docs/TOOLS.md) |
| Producir planos | Vistas, láminas, etiquetas, cotas, detalles y revisión de distribución. [Flujos de planos](docs/PLANIMETRY-PRODUCTION.md) |
| Crear familias | Parámetros, fórmulas, tipos, sólidos/vacíos y conectores. [Familias](docs/FAMILY-AUTHORING.md) |
| Convertir DWG a BIM | Consultar, planear y aplicar requisitos proporcionados por el usuario. [DWG a BIM](docs/DWG-TO-BIM.md) |
| Cuantificar y entregar | Cantidades por código de presupuesto, Excel, PDF/DWG/IFC/NWC/FBX y envío a Power BI. [Cantidades](docs/QUANTITIES-AND-BUDGET.md), [Power BI](docs/POWER-BI.md) |

Los [prompts de trabajo](docs/WORKFLOWS.md) indican alcance, permisos y evidencia.
Los [paquetes de herramientas](docs/WHAT-CAN-HORIZUN-DO.md) y los permisos locales
determinan qué ve cada cliente. Que una herramienta esté oculta no demuestra
que el producto carezca de ella.

## Verificación y evidencia

**Las escrituras tipadas se releen después del commit.** Consulta el resultado
para conocer reversiones, resultados parciales y límites. Python arbitrario está
**deshabilitado por defecto**. El permiso del propietario mediante Python ON/OFF
persiste hasta revocarlo; los resultados de Python son **autorreportados**, con
`host_verified: false`.

Las releases estables incluyen hashes, manifiesto del contenido, SBOM e informes de Revit
por año soportado. Son evidencia del publicador para esa versión. No acreditan
una comparación contra otros MCP ni una tasa de éxito de instalación de clientes
en máquinas limpias.

- [Método de benchmark y resultados históricos](docs/BENCHMARK.md)
- [Política de releases](docs/RELEASE-POLICY.md) y [estado de evidencia](docs/production-readiness.md)
- [Seguridad](docs/security-model.md), [privacidad](docs/PRIVACY.md) y [reporte de vulnerabilidades](SECURITY.md)

Hay límites por operación, API y exportador; la cancelación solo evita comenzar
un comando, y los vínculos descargados no se pueden consultar. Revisa el
[catálogo de herramientas](docs/TOOLS.md) para cada caso.

## Desarrollo y ecosistema

[Compilar desde fuente](docs/BUILDING.md) · [Arquitectura](docs/ARCHITECTURE.md) ·
[Contribuir](CONTRIBUTING.md) · [Instrucciones de agentes](AGENTS.md) · [Resumen para LLM](llms.txt)

El puente es neutral por organización. Los estándares y catálogos del proyecto
entran como datos. [Horizun Hub](docs/HORIZUN-HUB.md) ofrece el ecosistema más
amplio; el [paquete de estándares](standards/README.md) contiene ejemplos editables.

**Apache-2.0:** [licencia](LICENSE), [avisos](NOTICE), [componentes de terceros](THIRD-PARTY-NOTICES.md).
La API de Revit no se redistribuye. Autodesk y Revit son marcas de Autodesk;
este proyecto no está afiliado, avalado ni patrocinado por Autodesk.
