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

## Update / Actualizar

Close Revit, run the new release's Setup and complete the
[client connection steps](CLIENTS.md). Cierra Revit, ejecuta el nuevo Setup y
completa los pasos del cliente. Source compilation is described separately in
[BUILDING.md](BUILDING.md); it is not required to update a release installation.

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
