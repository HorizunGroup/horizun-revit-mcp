# Horizun Revit MCP — guía para agentes

Estás en el repositorio de **Horizun Revit MCP**: el puente MCP entre un cliente
(Claude Code, Codex, cualquier cliente MCP) y un Autodesk Revit corriendo en esta
máquina. Parte del ecosistema [Horizun Hub](https://horizunhub.com).

Si el usuario te pidió "instálalo", este documento es el procedimiento completo.

## Instalar

Todo se compila desde este árbol, contra el Revit ya instalado en la máquina.
No se descarga ningún ejecutable.

**Requisitos** (compruébalos antes, el script también lo hace):

- Windows con al menos un Revit 2023–2027 instalado
  (`C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` existe).
- El SDK de .NET 8.0+ en el PATH (`dotnet --version` responde). Revit ≤ 2024
  necesita además el targeting pack de .NET Framework 4.8.
- **Revit cerrado.** El script se niega a correr con Revit abierto y no cambia nada.

**El comando:**

```bash
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Detecta los años de Revit presentes, compila el add-in para cada uno contra su
propia API, compila el servidor MCP, lo instala todo, y **verifica leyendo de
vuelta cada binario instalado** (commit estampado + SHA-256 contra lo stageado).
Un fallo de compilación no cambia nada; un fallo posterior revierte con su
libro de deshacer y reporta el estado exacto.

Rutas resultantes:

- Add-in: `%APPDATA%\Autodesk\Revit\Addins\<año>\Horizun\`
- Servidor: `%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe`

## Configurar el cliente MCP

```bash
claude mcp add horizun -- "%LOCALAPPDATA%\Programs\Horizun\MCP\server\horizun-mcp.exe"
```

Para Codex, en `~/.codex/config.toml`:

```toml
[mcp_servers.horizun]
command = 'C:\Users\<usuario>\AppData\Local\Programs\Horizun\MCP\server\horizun-mcp.exe'
```

## Primer arranque de Revit — avisa al usuario de esto

- Revit mostrará el diálogo **"Security - Unsigned Add-In"** (este build no va
  firmado). Hay que elegir **Always Load**. Dos cosas que sorprenden: el diálogo
  **vuelve tras cada actualización** (la decisión se recuerda por binario), y
  puede abrirse **en otro monitor** — un Revit que lleva minutos "arrancando"
  con la CPU quieta casi siempre es este diálogo escondido.
- Con un documento abierto aparece la pestaña **Horizun Hub** en la cinta. Su
  botón **Estado del puente** responde "¿está funcionando y qué versión?" sin
  salir de Revit.

## Verificar

Con Revit abierto y el cliente reiniciado, llama a `horizun_health`. Debe
responder `status: healthy` con la versión y el commit del árbol que compilaste.
Un error de "contract hash mismatch" significa que una mitad quedó en un build
anterior: cierra Revit y vuelve a correr `install.ps1`.

## Cómo trabajar con el puente

- **`horizun_health` primero, siempre.** Los comandos actúan sobre el documento
  activo, y health es lo que te dice cuál es.
- **Un comando a la vez.** El segundo se rechaza con el motivo, no se encola.
  Cancelar una petición MCP detiene tu espera, no el trabajo en Revit.
- **El contrato**: ningún comando reporta trabajo que no verificó. Toda
  escritura se relee del modelo tras el commit.
- **`horizun_execute_python` viene apagado.** Se enciende por máquina en
  `%USERPROFILE%\.horizun\settings.json` con `{"enable_execute_python": true}`.
  No lo enciendas sin que el usuario lo pida: es la API completa de Revit.
- Este puente es **neutral por diseño**: no lleva estándares ni catálogos de
  ninguna organización compilados dentro. Donde un comando necesita uno, se pasa
  como argumento. Los flujos de entrega construidos encima viven en
  [Horizun Hub](https://horizunhub.com).

## Actualizar

```bash
git pull
```

Cierra Revit y vuelve a correr `install.ps1`. El servidor y el add-in comparten
un hash de contrato y se actualizan **juntos**; no hay despliegue parcial.

## Desinstalar

Con Revit cerrado: borra `%APPDATA%\Autodesk\Revit\Addins\<año>\Horizun\` y el
`Horizun.addin` de cada año, y `%LOCALAPPDATA%\Programs\Horizun\MCP\`. El estado
local (settings, logs, registros de jobs) vive en `%USERPROFILE%\.horizun\`.
