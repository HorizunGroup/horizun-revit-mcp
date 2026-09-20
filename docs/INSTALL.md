# Download and install Horizun Revit MCP / Descargar e instalar

**Horizun Revit MCP has a public Windows installer.** Download the
`horizun-mcp-<version>-setup.exe` asset from the
[latest stable release](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest).
The installer includes the MCP server runtime and the Revit add-ins. **No Git,
Visual Studio or .NET SDK is required for installation.**

**Horizun Revit MCP tiene instalador público para Windows.** En la
[última release estable](https://github.com/HorizunGroup/horizun-revit-mcp/releases/latest),
abre **Assets** y descarga `horizun-mcp-<version>-setup.exe`. Incluye el runtime
del servidor MCP y los add-ins. **No necesitas Git, Visual Studio ni el SDK de
.NET para instalarlo.**

## Requirements / Requisitos

- Windows x64 and an installed, licensed Revit 2023–2027. Revit itself is not
  included. / Windows x64 y Revit 2023–2027 instalado y con licencia; Revit no está incluido.
- Close Revit before running Setup. / Cierra Revit antes de ejecutar Setup.
- An MCP client; see its final connection step in
  [CLIENTS.md](CLIENTS.md). / Un cliente MCP; consulta su paso final de conexión.

## Install / Instalar

1. Download the installer and `SHA256SUMS.txt` from the **same release**.
   / Descarga el instalador y `SHA256SUMS.txt` de la **misma release**.
2. Compare the installer's SHA-256 using the commands below; both hashes must match.
   / Compara el SHA-256 con los comandos siguientes; ambos hashes deben coincidir.
3. Run Setup, then complete the client-specific connection step and start Revit.
   / Ejecuta Setup, completa la conexión del cliente y abre Revit.
4. Call `horizun_health` to verify the loaded version and active document.
   / Llama a `horizun_health` para verificar la versión y el documento activo.

Public releases are unsigned by policy. Hashes verify the downloaded bytes but
do not authenticate a Windows publisher; Windows/Revit may show a publisher
warning. Las releases públicas no tienen firma de editor de Windows. Consulta
la [política de firma](../CODE-SIGNING-POLICY.md).

The optional [bootstrap](../install-release.ps1) performs the download and hash
comparison automatically and requires the explicit `-AllowUnsigned` flag.
El bootstrap opcional descarga y compara el hash automáticamente; requiere
`-AllowUnsigned`.

## Verify the downloaded installer / Verificar la descarga

Replace `<version>` with the filename you downloaded. Compare the complete
hashes; a mismatch means do not run the file. Sustituye `<version>` por el nombre
descargado y compara los hashes completos antes de ejecutar.

```powershell
Get-FileHash -LiteralPath '.\horizun-mcp-<version>-setup.exe' -Algorithm SHA256
Select-String -Path .\SHA256SUMS.txt -Pattern 'setup.exe'
```

For Claude Desktop, Setup delivers the extension and illustrated instructions
in **Documents\Horizun-Revit-MCP**. Install the `.mcpb` inside Claude Desktop
and restart it. En Claude Desktop debes instalar el `.mcpb` dentro de la app y
reiniciarla; el archivo queda en **Documentos\Horizun-Revit-MCP**.

**Video — Claude Desktop Free:** [installation tutorial / tutorial de instalación](https://www.youtube.com/watch?v=3kp-we7MIvk)
by Horizun Hub, in Spanish, using the free Claude Desktop plan. Guía en español
con el plan gratuito; [pasos completos / complete steps](CLIENTS.md#claude-desktop).

## First Revit start / Primer arranque de Revit

Revit may show an add-in security prompt on first launch. After verifying the
package and accepting the unsigned-release policy, choose **Always Load** to
allow the add-in. Check the other monitors if Revit appears to be waiting with
no visible dialog. With a document open, the **Horizun Hub** ribbon tab and its
**Estado del puente** button show the local bridge state; verify the client
connection separately with `horizun_health`.

En el primer arranque Revit puede mostrar un aviso de seguridad del add-in.
Después de verificar el paquete y aceptar la política de firma, elige
**Always Load**. Revisa los demás monitores si Revit parece detenido. Con un
documento abierto, **Horizun Hub → Estado del puente** muestra el estado local;
comprueba también la conexión desde el cliente con `horizun_health`.

## Update / Actualizar

Close Revit, run the new release's Setup and complete the
[client connection steps](CLIENTS.md). Cierra Revit, ejecuta el nuevo Setup y
completa los pasos del cliente. Source compilation is described separately in
[BUILDING.md](BUILDING.md); it is not required to update a release installation.

### Upgrading to 2.0 / Actualizar a la 2.0

Installing is the same. **Two calls that worked before now fail**, and both are in
`horizun_apply_cad_update`. Instalar es igual; dos llamadas que antes funcionaban
ahora fallan, ambas en `horizun_apply_cad_update`.

| What changed | What to do |
|---|---|
| `apply_binding` is a **required** argument | Copy the `apply_binding` block from the `horizun_plan_cad_update` reply, verbatim. Any caller following the documented flow already receives it. |
| A plan that **releases fittings** is refused | Either drop the pairing that needs the release and plan again, or send `accept_connections_not_rebuilt: true` — which says you will run `horizun_cad_connect` over the result and accept its verdict. |

Nothing else in the tool set changed shape. No other tool was removed, renamed or
given a required argument. Ninguna otra herramienta cambió de forma.

**Reading a reply.** `state: applied` has never meant the network was built and
still does not. 2.0 says so in the reply: `verdict.geometry` is verified by
re-reading the model, and `verdict.network` is `not_asserted`. Joining is
`horizun_cad_connect`, and its result is what makes a revision *built*.

### Going back / Volver atrás

Install the older Setup over the new one; close Revit first. Settings are read
with unknown keys preserved, so nothing a newer version wrote is destroyed.
Instala el Setup anterior encima; los ajustes se conservan.

Two limits worth knowing before you do:

- **What 2.0 recorded, 1.3 does not read.** Operation records (used to continue an
  update that stopped part-way) and the provenance stamped on fittings are ignored
  by older versions. Nothing breaks; the protections they enable simply stop
  applying, and an older `horizun_apply_cad_update` will again apply a plan without
  re-measuring the world it was made against.
- **Models are not downgraded.** Elements built or re-shaped by 2.0 stay exactly as
  they are. It is the checking that goes away, not the geometry.

## Common questions / Preguntas frecuentes

**Must I clone or compile the repository? / ¿Tengo que clonar o compilar?**
No. “Source code (zip)” is the developer source archive, not the Windows
installer. Choose the `.exe` under Assets. “Source code (zip)” contiene el código
fuente; para instalar, elige el `.exe`.

**Does it include every dependency? / ¿Incluye todas las dependencias?**
It includes the bridge runtime and add-ins. Revit and your MCP client remain
separate prerequisites. Incluye el runtime del puente y sus add-ins; debes tener
Revit y el cliente MCP.

**Does installation mean the client is connected? / ¿Al instalar ya está conectado?**
Some clients need a restart, extension installation or tunnel connection. Setup
reports pending steps; follow [CLIENTS.md](CLIENTS.md). Algunos clientes requieren
reinicio, instalar una extensión o conectar un túnel; Setup informa los pasos pendientes.

**What should I send if installation fails? / ¿Qué envío si falla?**
The release tag, downloaded filename, Revit year, client name and the exact error
or installation log. Include the durable status from
`%LOCALAPPDATA%\Horizun\install-status.json` when present, after checking it for
personal paths. Envía esos datos; «no funciona» no identifica en qué paso falló.
