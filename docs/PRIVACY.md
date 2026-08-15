# Privacy policy

Horizun Revit MCP is a local bridge between an MCP client selected by the user
and Autodesk Revit running on the same Windows machine.

## Data Horizun does not collect

The project has no Horizun-operated telemetry, analytics, advertising, crash
reporting or cloud backend. It does not automatically upload models, element
data, prompts, credentials, file paths or usage statistics to Horizun Group or
to the maintainers.

Local operational state is stored under `%USERPROFILE%\.horizun\` and
`%LOCALAPPDATA%\Horizun\`. This includes settings, discovery records, durable job
and idempotency state, installation status and logs needed to diagnose the local
bridge. The uninstall helper identifies which state is preserved and removes it
only when the user explicitly selects that option.

## User-requested network operations

Horizun transfers information to another networked system only when the user or
the person operating the MCP client specifically requests an operation that
requires it:

- The one-command release installer downloads the installer and checksum from
  the project's public GitHub release.
- `horizun_power_bi_push` sends only the rows and destination selected by the
  user to Microsoft's Power BI API. Credentials are supplied in the local MCP
  server environment and are not accepted as tool arguments or stored by
  Horizun.
- `horizun_execute_python` runs code supplied by the user on the local Revit UI
  thread. Because that code may use Python networking libraries, its network
  behavior is determined by the explicit script, not by an automatic Horizun
  service.
- A user's MCP client or language-model provider may transmit prompts, tool
  arguments or returned model information according to that provider's own
  configuration and privacy policy. Horizun does not select that provider and
  does not receive a copy.

All other bridge traffic uses local standard input/output and authenticated
Windows named pipes.

## Credentials and sensitive model data

Credentials remain in user-controlled environment variables or client
configuration. Users should not place secrets in tool arguments, scripts,
screenshots, models or public issue reports. Model data is processed locally and
returned only to the MCP client that invoked the operation.

## Contact

Security-sensitive reports should follow [`SECURITY.md`](../SECURITY.md).
Ordinary privacy questions may be opened as a public repository issue when they
contain no model data, credentials or personal information.
