# Architecture


![Horizun Revit MCP architecture: an MCP client speaks stdio to the Horizun server, which forwards over a token-authenticated named pipe to the Revit add-in, which dispatches onto Revit's UI thread](assets/architecture.svg)

- **`Horizun.Revit`** — the add-in. `App` (IExternalApplication) starts a
  named-pipe server and publishes a discovery file; `Dispatcher` crosses each
  request onto Revit's UI thread via `ExternalEvent`; `Guard` and `Reconcile` are
  the "cannot lie" commit contract; commands live under `Commands/`.
- **`Horizun.Server`** — the MCP server. The wire format is hand-rolled from the
  open MCP spec, with no third-party SDK: it discovers the pipe, speaks MCP over
  stdio and forwards to the plugin. Schemas and behavioural effects live in one
  shared contract, so `tools/list` answers with Revit closed without drifting
  from the add-in. It negotiates MCP through 2025-11-25; exposes standard Tools,
  Resources, Prompts, Completions, opt-in Logging and durable Tasks; and returns both
  backward-compatible text and `structuredContent`. Seven tools have
  **host-resident handlers** in `Tools.cs`: jobs, catalogs, Excel read/write,
  Power BI, budget comparison and Revit targeting. Some workflows, including
  budget comparison, can coordinate further Revit requests from that handler.
- **One command at a time.** Concurrent calls wait in a bounded 16-slot FIFO
  queue; a full queue applies explicit backpressure instead of dropping work.
  Every reply carries what Revit raised while the command ran — warnings, errors
  and modal dialogs — on success and on failure.
