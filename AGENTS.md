# Horizun Revit MCP — guide for agents / guía para agentes

**[English](#english) · [Español](#español)**

You are in the repository of **Horizun Revit MCP**: the MCP bridge between a
client (Claude Code, Codex, any MCP client) and an Autodesk Revit running on
this machine. Part of the [Horizun Hub](https://horizunhub.com) ecosystem.

Estás en el repositorio de **Horizun Revit MCP**: el puente MCP entre un cliente
(Claude Code, Codex, cualquier cliente MCP) y un Autodesk Revit corriendo en esta
máquina. Parte del ecosistema [Horizun Hub](https://horizunhub.com).

> **The name is written `Horizun`** — capital H, the rest lower case — every time
> it appears to a user, in any language. Never `HORIZUN`, never `horizun` as a
> word. It is a brand, not an acronym and not shouting. The only upper-case forms
> that exist are the tool names (`horizun_*`, always lower case) and the
> environment-variable prefix (`HORIZUN_REVIT_YEAR` and friends) — those are code
> identifiers, never how you refer to the product in prose.
>
> **El nombre se escribe `Horizun`** — H mayúscula, el resto en minúscula — cada
> vez que aparece ante un usuario, en cualquier idioma. Nunca `HORIZUN`, nunca
> `horizun` como palabra. Es una marca, no una sigla ni un grito. Las únicas
> formas en mayúscula que existen son los nombres de las herramientas (`horizun_*`,
> siempre en minúscula) y el prefijo de variables de entorno (`HORIZUN_REVIT_YEAR`
> y demás) — esos son identificadores de código, no cómo te refieres al producto.

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
  Framework 4.8 — the SDK-style projects restore the reference assemblies from
  NuGet, so the Visual Studio targeting pack is NOT required when NuGet restore
  is available. Verified on a machine without the pack: 2024 compiled with zero
  warnings. Only a fully offline machine needs the pack itself.
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
claude mcp add horizun-revit -- "C:\Users\<you>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
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

- Revit will show the **"Security - Unsigned Add-In"** dialog (this build is
  unsigned). They must choose **Always Load**. Revit normally remembers that
  choice for this add-in's identity, though a trust or policy reset can bring the
  prompt back. It can open **on another monitor** — a Revit that has been
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
- **Understand the objective before the first write, and say it back.** A model is
  somebody's deliverable. Before the first typed write of a task you must know
  three things: WHAT outcome is wanted in the model, WHICH elements it applies to,
  and HOW the result will be recognised as correct. If any of the three is missing
  or reads two ways, **ask — with options and their trade-offs**, not an open
  question. Do not treat a missing instruction as permission to choose. One round
  of questions is cheap; a committed batch aimed at the wrong elements is not.
  Where the bridge itself cannot tell two readings apart it refuses rather than
  guesses; hold yourself to the same standard one level up. **And when nobody is
  at the keyboard** — a scheduled audit, a batch, the verification harness — state
  the ambiguity and do nothing, because a run that stops to ask has failed as
  surely as one that guessed.
- **One command executes at a time, but concurrent calls are queued.** Up to 16
  requests wait in bounded FIFO order; the next receives explicit backpressure.
  Cancelling removes work only before it starts. Use `horizun_submit_job` plus
  `horizun_job_status` for work that should outlive the MCP request.
- **The contract**: no command reports work it did not verify. Every typed write
  is re-read from the model after the commit. `horizun_execute_python` does not
  provide that guarantee **at all**, which is why scripts run through it verify
  their own work in the structured `__output__` and why what comes back is
  labelled self-reported: the states are `self_reported_verified`,
  `completed_unverified`, `partial`, `failed`. There is no `verified` on the
  Python path and `host_verified` is always false — report it to the user as the
  script's testimony, not as the bridge's finding.
- **Typed first, Python as the fallback — never "not supported".** Prefer a typed
  command whenever one fully covers the operation. When none exists, or a failed
  typed call returns `fallback.allowed: true` — its machine-readable signal that
  no typed capability covers the request *and* nothing was written — generate
  minimal Revit Python and run it through `horizun_execute_python` (optionally
  `preflight=true` first, then execute in the same task). **Decide on that block,
  never on the wording of an error**: no block, or `allowed: false`, means do not
  fall back. **It arrives on the first, ordinary call** — `dry_run` defaults to
  true and the rehearsal publishes the verdict in `structuredContent` beside its
  own payload, so a successful reply with invalid rows still carries it; you
  never need to send `dry_run: false` to find out. `write_started: true` never accompanies `allowed: true`, because a
  typed write that failed mid-operation may have partially written and a Python
  retry would be a second write. **A mixed batch never grants the fallback**: if
  one action is uncovered and another has bad arguments, `allowed` is false and
  you get `capability_gaps` naming the uncovered indices — fix the invalid
  entries and resend the typed call first. `target_document` plus the
  active-document check apply to Python exactly as to every typed write.
- **`horizun_execute_python` is enabled by default** during this early stage:
  a fresh install exposes it, and it is the expected fallback path. A machine
  owner can switch it off — an explicit `enable_execute_python=false` or a
  profile below `unsafe_code` in `%USERPROFILE%\.horizun\settings.json` is
  always respected, and you must not edit that file to reverse it. The admin
  script `scripts/enable-execute-python.ps1` re-enables (or restores) an
  explicitly disabled setup and reverts with `-Disable`; after a change,
  restart the MCP client so the tool list refreshes.
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
- El SDK de .NET en el PATH (`dotnet --version` responde): 8.0+ para Revit
  2023–2026 y 10.0+ al compilar para Revit 2027. Revit ≤ 2024 compila contra
  .NET Framework 4.8 — los proyectos SDK-style restauran los reference
  assemblies desde NuGet, así que el targeting pack de Visual Studio NO hace
  falta cuando hay restauración de NuGet disponible. Verificado en una máquina
  sin el pack: 2024 compiló sin una sola advertencia. Solo una máquina
  totalmente offline necesita el pack como tal.
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
claude mcp add horizun-revit -- "C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

```toml
# Codex — %USERPROFILE%\.codex\config.toml
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

- Revit mostrará el diálogo **"Security - Unsigned Add-In"** (este build no va
  firmado). Hay que elegir **Always Load**. Revit normalmente recuerda esa
  elección por la identidad del add-in, aunque un cambio de política o confianza
  puede hacer que el aviso vuelva. Puede abrirse **en otro monitor** — un Revit
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
- **Entiende el objetivo antes de la primera escritura, y devuélvelo dicho.** Un
  modelo es el entregable de alguien. Antes de la primera escritura tipada de una
  tarea tienes que saber tres cosas: QUÉ resultado se quiere en el modelo, A QUÉ
  elementos aplica, y CÓMO se reconocerá que quedó bien. Si falta alguna de las
  tres o se lee de dos maneras, **pregunta — con opciones y su contrapartida**, no
  con una pregunta abierta. Que falte una instrucción no es permiso para elegir.
  Una ronda de preguntas es barata; un lote commiteado sobre los elementos
  equivocados no. Donde el puente mismo no puede distinguir dos lecturas, se
  niega en vez de adivinar; sostén ese mismo estándar un nivel más arriba. **Y
  cuando no hay nadie al teclado** — una auditoría programada, un lote, el harness
  de verificación — enuncia la ambigüedad y no hagas nada: una corrida que se
  detiene a preguntar ha fallado igual que una que adivinó.
- **Se ejecuta un comando a la vez, pero las llamadas concurrentes se encolan.**
  Hasta 16 esperan en FIFO acotada; la siguiente recibe backpressure explícito.
  La cancelación solo elimina trabajo antes de empezar. Usa
  `horizun_submit_job` y `horizun_job_status` para trabajos largos.
- **El contrato**: ningún comando reporta trabajo que no verificó. Toda escritura
  tipada se relee del modelo tras el commit. `horizun_execute_python` **no ofrece
  esa garantía en absoluto**, y por eso los scripts verifican su propio trabajo en
  el `__output__` estructurado y lo que vuelve se etiqueta como autorreportado:
  los estados son `self_reported_verified`, `completed_unverified`, `partial`,
  `failed`. En la ruta Python no existe `verified` y `host_verified` siempre es
  false — repórtalo al usuario como testimonio del script, no como hallazgo del
  puente.
- **Tipado primero, Python como respaldo — nunca "no soportado".** Prefiere un
  comando tipado cuando cubra la operación completa. Cuando no exista, o una
  llamada tipada fallida devuelva `fallback.allowed: true` — su señal legible por
  máquina de que ninguna capacidad tipada cubre lo pedido *y* no se escribió nada
  — genera el Python de Revit mínimo y córrelo con `horizun_execute_python` (si
  quieres, `preflight=true` primero y ejecuta en la misma tarea). **Decide por ese
  bloque, nunca por cómo esté redactado un error**: sin bloque, o con
  `allowed: false`, no caigas a Python. **Llega en la primera llamada normal** —
  `dry_run` viene en true y el ensayo publica el veredicto en `structuredContent`
  junto a su propio payload, así que una respuesta exitosa con filas inválidas
  también lo trae; nunca hace falta mandar `dry_run: false` para enterarte. `write_started: true` nunca acompaña a
  `allowed: true`, porque una escritura tipada que falló a mitad pudo escribir
  parcialmente y un reintento en Python sería una segunda escritura.
  **Un lote mixto nunca concede el fallback**: si una acción no está cubierta y
  otra trae argumentos malos, `allowed` es false y llegan `capability_gaps` con
  los índices no cubiertos — corrige primero las entradas inválidas y reenvía la
  llamada tipada. `target_document` más el control de documento activo aplican a
  Python igual que a toda escritura tipada.
- **`horizun_execute_python` viene habilitado por defecto** en esta etapa
  temprana: una instalación nueva lo expone y es la ruta de respaldo esperada.
  El dueño de la máquina puede apagarlo — un `enable_execute_python=false`
  explícito o un perfil por debajo de `unsafe_code` en
  `%USERPROFILE%\.horizun\settings.json` siempre se respeta, y no debes editar
  ese archivo para revertirlo. El script de administración
  `scripts/enable-execute-python.ps1` re-habilita (o restaura) una configuración
  apagada explícitamente y se revierte con `-Disable`; tras un cambio, reinicia
  el cliente MCP para que la lista de herramientas se refresque.
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
