# Horizun Hub and Horizun Revit MCP

Horizun Hub is the BIM, data and AI ecosystem from Horizun Group. The public
Horizun Revit MCP is the open-source Revit automation layer inside that
ecosystem.

## What the MCP provides

The MCP connects an MCP client such as Codex or Claude to a running Autodesk
Revit instance. It exposes native, typed operations over a guarded bridge for
Revit 2023–2027. Every typed mutation uses dry-run planning where appropriate,
confirmation for writes, one Revit API operation at a time, durable
idempotency and post-commit read-back verification.

Its public source includes:

- architectural, structural and MEP creation;
- model queries, quantities, clashes, audits and linked-model coverage;
- views, sheets, annotations and native schedules;
- loadable-family authoring from RFT to verified RFA;
- system-family type authoring and compound-layer editing;
- IFC, Navisworks, FBX, PDF, DWG, image and schedule export;
- Power BI push ingestion with bounded payloads and durable duplicate protection;
- asynchronous jobs, Excel writing and catalog lookup;
- installation, client registration, tests, CI, security documentation and
  reproducible benchmark scripts.

See the complete tool table in the main [README](../README.md), the family
authoring contract in [FAMILY-AUTHORING.md](FAMILY-AUTHORING.md), and the
benchmark methodology in [BENCHMARK.md](BENCHMARK.md).

## What Horizun Hub adds

The MCP is deliberately generic and organisation-neutral. Horizun Hub adds the
specialised delivery layer around it:

### Apps

- PowerBIM Exporter for Revit
- PowerBIM Online
- PowerBIM Exporter for Civil 3D
- BuildMotion (`.pbiviz`)
- CopyToExcel (`.pbiviz`)
- Family Browser (Revit add-in)

### Learning and delivery

- Academia with PowerBIM + IA training and additional courses;
- quantification and 4D/5D workflows using Revit and Navisworks;
- Power BI dashboards, `.pbit` templates and visual assets;
- BIM Para Todos videos and tutorials for Revit, Power BI, Speckle, Civil 3D
  and AI;
- IA and automation workflows using agents, MCP and scripts;
- APS/Autodesk Construction Cloud data extraction into Power BI;
- two monthly hours of direct expert advisory through Zoom or Teams.

The current ecosystem landing page is [horizunhub.com](https://horizunhub.com).

## Why the separation matters

The public repository contains the full MCP source and the same installation
layout used by the release installer. Hub-specific catalogues, standards,
client workflows and audit rules are inputs or downstream products; they are not
silently hard-coded into the generic bridge. This keeps the gateway reusable
while allowing Horizun Hub to provide opinionated BIM delivery workflows.
