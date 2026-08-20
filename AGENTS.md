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
- The exact .NET SDK 10.0.400 on PATH (`dotnet --version` answers), fixed by
  `global.json` so release bytes do not depend on the latest installed patch.
  Revit ≤ 2024 still builds against .NET
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

Installation and registration are two internal phases but one user action. Do not
rewrite the configuration of the Claude/Codex process that is currently running:
it may overwrite the edit when it exits. The installer runs
`complete-install.ps1`, which waits for active clients to close, registers beside
existing MCP entries, verifies the configuration, and completes
`horizun_health` after Revit's first start. Report the durable state from
`%LOCALAPPDATA%\Horizun\install-status.json`. The commands below are manual
recovery only.

```powershell
# Claude Code — user scope makes it available across projects
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
  install reuses an explicitly created trusted self-signing certificate when one
  exists; that is local trust, not a public publisher identity. The dialog can
  open **on another monitor** — a Revit that has been
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
- **When a script opens models, read `dialogs` before you report a failure.** The
  bridge CANCELS every modal dialog raised during a script — nobody is at the
  keyboard — so all Revit tells the script is `Opening was canceled`, which is not
  a diagnosis. The reply carries `dialogs` and `failures` beside `__output__`, and
  `revit_raised(since)` reads the same records from INSIDE the script, windowed to
  one call: `len(revit_raised())` before the open, the same number after it. Send
  long scripts as `code_path` rather than an inline string. To let ONE call
  continue past its dialog: `with dialog_answer('dismiss'):` — around that call
  only, never a whole run, because Revit reads OK on a close-with-changes dialog as
  Save. A model that will not open unattended remains a finding either way.
- **`horizun_execute_python` is disabled by default.** A fresh install uses
  `safe_write`: typed in-model writes are available, but arbitrary code is not.
  The machine owner may use Revit's **Python ON/OFF** button for a 60-minute
  grant, or the admin script `scripts/enable-execute-python.ps1` for a durable
  developer opt-in; `-Disable` revokes it. Never edit settings to reverse the
  owner's choice. Compatible MCP clients refresh tools/list automatically after a
  change; restart once only when the client does not implement list-change notifications.
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
Installed apps. Before uninstalling, the Start-menu shortcut **Advanced cleanup
before uninstall** can remove only the named `horizun-revit` entries from Claude
and Codex. State in `%USERPROFILE%\.horizun\` and signing trust are preserved by
default; the helper purges either only when the user explicitly selects it.

---

## Español

Si el usuario te pidió "instálalo", este documento es el procedimiento completo.

### Instalar

Todo se compila desde este árbol, contra el Revit ya instalado en la máquina.
No se descarga ningún ejecutable.

**Requisitos** — compruébalos antes; el script también lo hace:

- Windows con al menos un Revit 2023–2027 instalado
  (`C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` existe).
- El SDK exacto .NET 10.0.400 en el PATH (`dotnet --version` responde), fijado
  por `global.json` para que los bytes no dependan del último parche instalado.
  Revit ≤ 2024 sigue compilando contra
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

La instalación y el registro son dos fases internas pero una sola acción del
usuario. No reescribas la configuración del proceso de Claude/Codex que está
corriendo: puede pisar el cambio al cerrarse. El instalador ejecuta
`complete-install.ps1`, que espera a que cierren los clientes activos, registra
sin eliminar otros MCP, verifica la configuración y completa `horizun_health`
después del primer arranque de Revit. Reporta el estado durable de
`%LOCALAPPDATA%\Horizun\install-status.json`. Los comandos siguientes quedan
solo como recuperación manual.

```powershell
# Claude Code — el scope user lo deja disponible en todos los proyectos
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
  **Security**. Tras verificar el build, hay que elegir **Always Load**. Una
  instalación desde fuente reutiliza el certificado autofirmado que el usuario
  haya creado y confiado explícitamente; eso no es identidad pública. El diálogo
  puede abrirse **en otro monitor** — un Revit
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
- **Cuando un script abre modelos, lee `dialogs` antes de reportar un fallo.** El
  puente CANCELA todo diálogo modal levantado durante un script —no hay nadie al
  teclado—, así que lo único que Revit le dice al script es `Opening was canceled`,
  que no es un diagnóstico. La respuesta trae `dialogs` y `failures` junto a
  `__output__`, y `revit_raised(since)` lee los mismos registros DESDE DENTRO del
  script, acotados a una llamada: `len(revit_raised())` antes de abrir, ese mismo
  número después. Manda los scripts largos como `code_path` en vez de una cadena
  inline. Para dejar que UNA llamada continúe pese a su diálogo:
  `with dialog_answer('dismiss'):` — alrededor de esa llamada y nada más, nunca de
  una corrida entera, porque Revit lee OK en el diálogo de cerrar-con-cambios como
  Guardar. Un modelo que no abre desatendido sigue siendo un hallazgo igual.
- **`horizun_execute_python` viene deshabilitado por defecto.** El dueño de la
  máquina puede habilitarlo durante 60 minutos con el botón **Python ON/OFF** de
  Revit o administrarlo de forma durable con
  `scripts/enable-execute-python.ps1`; `-Disable` revoca ambas rutas. Un
  `enable_execute_python=false` explícito o un perfil por debajo de
  `unsafe_code` siempre se respeta, y no debes editar `settings.json` para
  revertirlo. Los clientes compatibles refrescan la lista automáticamente;
  reinicia solo si el cliente no implementa la notificación de cambio.
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
desinstalar** del menú Inicio puede quitar únicamente las entradas
`horizun-revit` de Claude y Codex. El estado en `%USERPROFILE%\.horizun\` y la
confianza de firma se conservan por defecto; el helper solo purga cada uno si el
usuario lo selecciona explícitamente.
