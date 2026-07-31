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
- The .NET SDK 8.0+ on PATH (`dotnet --version` answers). Revit ≤ 2024 also
  needs the .NET Framework 4.8 targeting pack.
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

```powershell
# Claude Code
claude mcp add horizun -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun]
command = 'C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop and other MCP clients
{
  "mcpServers": {
    "horizun": {
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

- Revit will show the **"Security - Unsigned Add-In"** dialog (this build is
  unsigned). They must choose **Always Load**. Two things surprise people: the
  dialog **returns after every update** (the decision is remembered per binary),
  and it can open **on another monitor** — a Revit that has been "starting" for
  minutes with the CPU idle is almost always this dialog hiding.
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
- **One command at a time.** The second is refused with the reason, not queued.
  Cancelling an MCP request stops your waiting, not the work inside Revit.
- **The contract**: no command reports work it did not verify. Every write is
  re-read from the model after the commit.
- **`horizun_execute_python` ships disabled.** It is enabled per machine in
  `%USERPROFILE%\.horizun\settings.json` with `{"enable_execute_python": true}`.
  Do not enable it unless the user asks: it is the full Revit API.
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

With Revit closed: delete `%APPDATA%\Autodesk\Revit\Addins\<year>\Horizun\` and
each year's `Horizun.addin`, plus `%LOCALAPPDATA%\Programs\Horizun\MCP\`. Local
state (settings, logs, job records) lives in `%USERPROFILE%\.horizun\`.

---

## Español

Si el usuario te pidió "instálalo", este documento es el procedimiento completo.

### Instalar

Todo se compila desde este árbol, contra el Revit ya instalado en la máquina.
No se descarga ningún ejecutable.

**Requisitos** — compruébalos antes; el script también lo hace:

- Windows con al menos un Revit 2023–2027 instalado
  (`C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` existe).
- El SDK de .NET 8.0+ en el PATH (`dotnet --version` responde). Revit ≤ 2024
  necesita además el targeting pack de .NET Framework 4.8.
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

```powershell
# Claude Code
claude mcp add horizun -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
[mcp_servers.horizun]
command = 'C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
args = []
startup_timeout_sec = 120
tool_timeout_sec = 600
```

```json
// Cursor, Cline, Windsurf, Claude Desktop y otros clientes MCP
{
  "mcpServers": {
    "horizun": {
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

- Revit mostrará el diálogo **"Security - Unsigned Add-In"** (este build no va
  firmado). Hay que elegir **Always Load**. Dos cosas que sorprenden: el diálogo
  **vuelve tras cada actualización** (la decisión se recuerda por binario), y
  puede abrirse **en otro monitor** — un Revit que lleva minutos "arrancando"
  con la CPU quieta casi siempre es este diálogo escondido.
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
- **Un comando a la vez.** El segundo se rechaza con el motivo, no se encola.
  Cancelar una petición MCP detiene tu espera, no el trabajo dentro de Revit.
- **El contrato**: ningún comando reporta trabajo que no verificó. Toda
  escritura se relee del modelo tras el commit.
- **`horizun_execute_python` viene apagado.** Se enciende por máquina en
  `%USERPROFILE%\.horizun\settings.json` con `{"enable_execute_python": true}`.
  No lo enciendas sin que el usuario lo pida: es la API completa de Revit.
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

Con Revit cerrado: borra `%APPDATA%\Autodesk\Revit\Addins\<año>\Horizun\` y el
`Horizun.addin` de cada año, y `%LOCALAPPDATA%\Programs\Horizun\MCP\`. El estado
local (settings, logs, registros de jobs) vive en `%USERPROFILE%\.horizun\`.
