# Horizun Revit MCP — guide for agents / guía para agentes

**[English](#english) · [Español](#español)**

You are in the repository of **Horizun Revit MCP**: the MCP bridge between a
client (Claude Code, Codex, any MCP client) and an Autodesk Revit running on
this machine. Part of the [Horizun Hub](https://horizunhub.com) ecosystem.

Estás en el repositorio de **Horizun Revit MCP**: el puente MCP entre un cliente
(Claude Code, Codex, cualquier cliente MCP) y un Autodesk Revit corriendo en esta
máquina. Parte del ecosistema [Horizun Hub](https://horizunhub.com).

---

## English

If the user asked you to install it, this is the whole procedure.

### Install

Everything is compiled from this tree, against the Revit already installed on
this machine. No executable is downloaded.

**Prerequisites** — check them first; the script checks them too:

- Windows with at least one Revit 2023–2027 installed
  (`C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` exists).
- The .NET SDK on PATH (`dotnet --version` answers): 8.0+ for Revit 2023–2026,
  and 10.0+ when building for Revit 2027. Revit ≤ 2024 builds against .NET
  Framework 4.8; NuGet supplies the reference assemblies, so the Visual Studio
  targeting pack is needed only on a fully offline machine.
- **Revit closed.** The script refuses to run with Revit open and changes
  nothing when it refuses.

**The command:**

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It detects the Revit years present, compiles the add-in for each against its own
API, compiles the MCP server, installs everything, and **verifies by reading
every installed binary back** (stamped commit + SHA-256 against what was
staged). A build failure changes nothing; a later failure rolls back through its
undo ledger and reports the exact state.

Resulting paths:

- Add-in: `%APPDATA%\Autodesk\Revit\Addins\<year>\Horizun\`
- Server: `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe`

### Configure the MCP client

**Use the EXACT path the installer printed**, already expanded for this machine.
Do not retype it with `%LOCALAPPDATA%`: `cmd.exe` expands that variable and
**PowerShell does not**, so a config written that way points somewhere that does
not exist and the client shows no tools without saying why.

Installation and registration are two internal phases but one user action. Do
not edit the configuration of the Claude/Codex process that is currently
running: it may overwrite the change when it exits. The installer runs
`complete-install.ps1`, which waits for active clients to close, registers
beside existing MCP entries, verifies the configuration and completes
`horizun_health` after Revit's first start. Report the durable state from
`%LOCALAPPDATA%\Horizun\install-status.json`. The commands below are manual
recovery only.

```powershell
# Claude Code — user scope is available across projects
claude mcp add --scope user horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"

# Codex
codex mcp add horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex timeout settings — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop and other MCP clients
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe"
    }
  }
}
```

TOML literal strings (single quotes) take Windows paths as written; JSON needs
every backslash doubled. **Raise the tool timeout** if your client has one: a
`model_scan` or a batch open holds Revit's UI thread for minutes, and a
60-second default abandons work that is still running — the bridge looks broken
when it is merely busy.

### First Revit start — tell the user about this

- Without an already trusted local signing certificate, Revit will show a
  **Security** dialog. Choose **Always Load** after verifying the build. A source
  install can reuse a certificate the user explicitly self-signed and trusted;
  that is local trust, not a public publisher identity. It can open **on another
  monitor** — a Revit that has been
  "starting" for minutes with the CPU idle is often this dialog hiding.
- With a document open, a **Horizun Hub** tab appears in the ribbon. Its
  **Estado del puente** button answers "is this working, and which version?"
  without leaving Revit.

### Verify

With Revit open and the client restarted, call `horizun_health`. It must answer
`status: healthy` with the version and commit of the tree you compiled. A
"contract hash mismatch" error means one half is on an older build: close Revit
and run `install.ps1` again.

### How to work with the bridge

- **`horizun_health` first, always.** The commands act on the *active* document,
  and health is what tells you which one that is.
- **One command executes at a time, but concurrent calls are queued.** Up to 16
  requests wait in bounded FIFO order; the next receives explicit backpressure.
  Cancelling removes work only before it starts. Use `horizun_submit_job` plus
  `horizun_job_status` for work that should outlive the MCP request.
- **The contract**: no command reports work it did not verify. Every typed write
  is re-read from the model after the commit. `horizun_execute_python` does not
  provide that guarantee. Scripts report structured `__output__`, with states
  `self_reported_verified`, `completed_unverified`, `partial` or `failed`.
  `host_verified` is always false; never describe this as host verification.
- **Typed first, Python as the fallback — never "not supported".** Fall back only
  when the typed response carries `fallback.allowed: true`. No block, or
  `allowed: false`, means do not retry in Python. A mixed invalid batch must be
  corrected and resent typed first; never infer permission from error wording.
- **`horizun_execute_python` is enabled by default.** A machine owner can switch
  it off (`enable_execute_python=false` or a profile below `unsafe_code` in
  `%USERPROFILE%\.horizun\settings.json`); that explicit choice is always
  respected — never edit that file to reverse it.
- This bridge is **organisation-neutral by design**: no company's standards or
  catalogues are compiled in. Where a command needs one, it is passed as an
  argument. The delivery workflows built on top live in
  [Horizun Hub](https://horizunhub.com).

### Update

```bash
git pull
```

Close Revit and run `install.ps1` again. The server and the add-in share a
contract hash and are updated **together**; there is no partial deployment.

### Uninstall

Close Revit and the MCP client, then uninstall **Horizun Revit MCP** from Windows
Installed apps. Before uninstalling, the Start-menu **Advanced cleanup before
uninstall** shortcut can remove only the named `horizun-revit` entries. State
and signing trust are preserved by default and purged only when explicitly chosen.

---

## Español

Si el usuario te pidió "instálalo", este documento es el procedimiento completo.

### Instalar

Todo se compila desde este árbol, contra el Revit ya instalado en la máquina.
No se descarga ningún ejecutable.

**Requisitos** — compruébalos antes; el script también lo hace:

- Windows con al menos un Revit 2023–2027 instalado
  (`C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` existe).
- El SDK de .NET en el PATH (`dotnet --version` responde): 8.0+ para Revit
  2023–2026 y 10.0+ al compilar para Revit 2027. Revit ≤ 2024 compila contra
  .NET Framework 4.8; NuGet aporta los reference assemblies, así que el targeting
  pack solo hace falta en una máquina totalmente offline.
- **Revit cerrado.** El script se niega a correr con Revit abierto y no cambia
  nada cuando se niega.

**El comando:**

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Detecta los años de Revit presentes, compila el add-in para cada uno contra su
propia API, compila el servidor MCP, lo instala todo, y **verifica leyendo de
vuelta cada binario instalado** (commit estampado + SHA-256 contra lo stageado).
Un fallo de compilación no cambia nada; un fallo posterior revierte con su libro
de deshacer y reporta el estado exacto.

Rutas resultantes:

- Add-in: `%APPDATA%\Autodesk\Revit\Addins\<año>\Horizun\`
- Servidor: `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe`

### Configurar el cliente MCP

**Usa la ruta EXACTA que imprimió el instalador**, ya expandida para esta
máquina. No la reescribas con `%LOCALAPPDATA%`: `cmd.exe` expande esa variable y
**PowerShell no**, así que una config escrita así apunta a un sitio que no
existe y el cliente no muestra herramientas sin decir por qué.

La instalación y el registro son dos fases internas pero una sola acción del
usuario. No edites la configuración del proceso de Claude/Codex que está
corriendo: puede pisar el cambio al cerrarse. El instalador ejecuta
`complete-install.ps1`, que espera a que cierren los clientes activos, registra
sin eliminar otros MCP, verifica la configuración y completa `horizun_health`
después del primer arranque de Revit. Reporta el estado durable de
`%LOCALAPPDATA%\Horizun\install-status.json`. Los comandos siguientes quedan
solo como recuperación manual.

```powershell
# Claude Code — el scope user queda disponible en todos los proyectos
claude mcp add --scope user horizun-revit -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"

# Codex
codex mcp add horizun-revit -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Ajustes de timeout de Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun-revit]
command = 'C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop y otros clientes MCP
{
  "mcpServers": {
    "horizun-revit": {
      "command": "C:\\Users\\<usuario>\\AppData\\Local\\Programs\\Horizun\\MCP\\server\\horizun-mcp.exe"
    }
  }
}
```

Comillas simples en TOML (cadena literal: las barras van tal cual); en JSON hay
que doblar cada barra. **Sube el tool timeout** si el cliente tiene uno: un
`model_scan` o una apertura por lote ocupa el hilo de UI de Revit durante
minutos, y un timeout de 60 s por defecto abandona trabajo que sigue corriendo
— el puente parece roto cuando solo está ocupado.

### Primer arranque de Revit — avisa al usuario de esto

- Sin un certificado local de firma ya confiable, Revit mostrará un diálogo de
  **Security**. Tras verificar el build, elige **Always Load**. La instalación
  desde fuente puede reutilizar un certificado autofirmado y confiado
  explícitamente; eso no es identidad pública. Puede abrirse **en otro monitor** — un Revit
  que lleva minutos "arrancando" con la CPU quieta suele tener este diálogo
  escondido.
- Con un documento abierto aparece la pestaña **Horizun Hub** en la cinta. Su
  botón **Estado del puente** responde "¿está funcionando y qué versión?" sin
  salir de Revit.

### Verificar

Con Revit abierto y el cliente reiniciado, llama a `horizun_health`. Debe
responder `status: healthy` con la versión y el commit del árbol que compilaste.
Un error de "contract hash mismatch" significa que una mitad quedó en un build
anterior: cierra Revit y vuelve a correr `install.ps1`.

### Cómo trabajar con el puente

- **`horizun_health` primero, siempre.** Los comandos actúan sobre el documento
  *activo*, y health es lo que te dice cuál es.
- **Se ejecuta un comando a la vez, pero las llamadas concurrentes se encolan.**
  Hasta 16 esperan en FIFO acotada; la siguiente recibe backpressure explícito.
  La cancelación solo elimina trabajo antes de empezar. Usa
  `horizun_submit_job` y `horizun_job_status` para trabajos largos.
- **El contrato**: ningún comando reporta trabajo que no verificó. Toda escritura
  tipada se relee del modelo tras el commit. `horizun_execute_python` no ofrece
  esa garantía. Los scripts reportan `__output__` estructurado con estados
  `self_reported_verified`, `completed_unverified`, `partial` o `failed`.
  `host_verified` siempre es false; nunca lo describas como verificación del host.
- **Tipado primero, Python como respaldo — nunca "no soportado".** Cae a Python
  solo cuando la respuesta tipada trae `fallback.allowed: true`. Sin bloque, o
  con `allowed: false`, no reintentes. Un lote mixto inválido se corrige y se
  reenvía primero por la ruta tipada; no infieras permiso del texto del error.
- **`horizun_execute_python` viene habilitado por defecto.** El dueño de la
  máquina puede apagarlo (`enable_execute_python=false` o un perfil por debajo
  de `unsafe_code` en `%USERPROFILE%\.horizun\settings.json`); esa elección
  explícita siempre se respeta — nunca edites ese archivo para revertirla.
- Este puente es **neutral por diseño**: no lleva estándares ni catálogos de
  ninguna organización compilados dentro. Donde un comando necesita uno, se pasa
  como argumento. Los flujos de entrega construidos encima viven en
  [Horizun Hub](https://horizunhub.com).

### Actualizar

```bash
git pull
```

Cierra Revit y vuelve a correr `install.ps1`. El servidor y el add-in comparten
un hash de contrato y se actualizan **juntos**; no hay despliegue parcial.

### Desinstalar

Cierra Revit y el cliente MCP, y desinstala **Horizun Revit MCP** desde
Aplicaciones instaladas de Windows. Antes, el acceso **Limpieza avanzada antes de
desinstalar** puede quitar solo las entradas `horizun-revit`. El estado y la
confianza de firma se conservan salvo que el usuario elija purgarlos.
