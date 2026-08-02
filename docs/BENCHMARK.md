# Revit MCP benchmark

Updated: 2026-08-01. This benchmark measures useful outcomes, not the number of
tool names. One broad, composable and verified operation can be more valuable
than twenty thin wrappers.

## Rules

A case earns:

- **0 — absent:** no public capability.
- **1 — escape hatch:** possible only through arbitrary Python/reflection/UI automation.
- **2 — dedicated:** a public tool or documented workflow exists.
- **3 — typed:** bounded schema and invalid references/options are rejected before mutation.
- **4 — verified:** level 3 plus the result is re-read from Revit, disk or the destination API.
- **5 — resilient:** level 4 plus dry-run/confirmation where applicable and durable replay or
  fail-closed handling of a lost response.

Evidence grades are separate from scores:

- **L:** live run against the real application/destination.
- **T:** automated test without Revit.
- **B:** compiled against the real Revit API for every claimed year.
- **S:** public schema/source inspection.
- **C:** README/marketing claim only.

No case receives evidence grade L merely because an API call did not throw.
Every mutation case defines what must be re-read. “Supported” with only grade C
is a research lead, not a verified benchmark result.

## Objective cases

| ID | Case | Pass condition |
| --- | --- | --- |
| O1 | Architectural creation | One atomic batch can create levels, grids, walls, floors, ceilings, roofs and rooms; created ids are re-read as the requested runtime classes. |
| O2 | Structural creation | Beam/brace curve instances and columns are typed; symbols/levels resolve before the transaction; structural type is re-read. |
| O3 | MEP creation | Duct, pipe, conduit and cable tray creation uses native APIs and explicit type/system/level references. |
| O4 | Documentation | Floor/ceiling/structural plans, sections, elevations, drafting/3D views, sheets and placements can be composed in one dependency-aware batch. |
| O5 | Loaded-family compiler | From an RFT: parameters, formulas, types, reference planes, labeled dimensions, symbolic/model lines, nested point instances with parameter propagation, solid/void extrusion/blend/revolution/sweep/swept-blend forms and MEP connectors; RFA saved, re-read and optionally loaded/re-read in the project. |
| O6 | System-family authoring | Project-resident non-RFA ElementTypes can be duplicated and parameterized atomically; host types also receive a fully typed homogeneous compound-layer graph with post-commit verification. |
| O7 | Federated schedules | Native schedules can include linked elements and their displayed cells can be read with explicit host/link coverage. |
| X1 | IFC | Native export exposes meaningful IFC version/quantity/splitting/space-boundary/view-filter options and verifies changed non-empty files. |
| X2 | Navisworks | Native NWC model/view export exposes coordinates, links, parameters, ids, rooms and parts; optional-exporter absence is explicit. |
| X3 | FBX | One or more 3D views export through the native API with options and verified output files. |
| X4 | Power BI | Direct fixed-endpoint row ingestion supports My workspace/workspace, keeps credentials outside MCP payloads, enforces Microsoft limits and prevents blind duplicate retry. |
| R1 | Concurrency | Concurrent calls wait in a bounded FIFO queue; full capacity applies explicit backpressure; queued cancellation proves the command never ran. |
| R2 | At-most-once mutation | Every typed mutation requires an idempotency key; completed retries replay, conflicting keys refuse, claim-only records become in-doubt. |
| D1 | Distribution | A Windows user without Git or an SDK can install all supported Revit years from one release artifact; release has checksum, manifest and SBOM. |
| D2 | Version coverage | Separate binaries compile against the actual Revit APIs for 2023, 2024, 2025, 2026 and 2027. |

In-place families are deliberately not a pass condition: Autodesk's public
Revit API does not provide general creation of them. A competitor receives no
credit for driving the modal editor through screen coordinates. Loaded RFAs and
project-resident system types are measured separately because they are different
Revit concepts.

## Horizun v0.5.0 public implementation result

| Case | Score | Evidence now | Implementation |
| --- | ---: | --- | --- |
| O1 | 5 | B/S; L pending for new kinds | `horizun_create_elements`, one transaction, confirmation and durable replay. |
| O2 | 5 | B/S; L pending | Typed structural curve/point creation and structural-type re-read. |
| O3 | 5 | B/S; L pending for cable tray | Native typed MEP APIs. |
| O4 | 5 | B/S; L pending for new views | `horizun_manage_views`, aliases and post-commit class checks. |
| O5 | 5 | B/S; L pending | `horizun_create_family`; RFT→RFA compiler with connector face selection and file/project verification. |
| O6 | 5 | B/S; L pending | `horizun_manage_system_types`; generic type parameters plus verified host compound structures. |
| O7 | 5 | L/T/S | Native linked schedule creation/list/read path. |
| X1 | 5 | B/S; L pending for expanded options | `horizun_export` IFC branch. |
| X2 | 5 | B/S; L pending | Native optional NWC exporter branch. |
| X3 | 5 | B/S; L pending | Native multi-View3D FBX branch. |
| X4 | 5 | T/S; tenant L pending | Direct REST; tests prove bounds, one send on replay and fail-closed lost response. |
| R1 | 5 | L/T/S | Bounded FIFO, fairness, capacity and cancellation harnesses. |
| R2 | 5 | L/T/S | Append-only durable ledger shared by Revit and Power BI mutations. |
| D1 | 4 | L/T/S | One setup, checksum, payload manifest, SBOM and verified-release bootstrap. Missing publicly trusted code signature keeps this below 5. |
| D2 | 5 | B/L | Five independently compiled payloads. |

Current implementation score: **74/75**. The score measures the public contract,
implementation and failure guarantees; it is not a claim that every row has live
evidence. The only structural point not earned is public code signing. Cases
marked “L pending” are not called live-verified until a release-gate fixture runs
and its result is retained; the evidence column states that limitation explicitly.

## Competitor baseline

The comparison set is pinned by repository, not by recollection:

- [SAM AEC Model Bridge at `bc31aef`](https://github.com/Sam-AEC/aec-model-bridge/tree/bc31aeffece0f1d5cb9812f46e80462b3b0f93cd): publishes 100+ tools,
  Revit 2024–2027, async orchestration, IFC/Speckle, reflection/Python and an MCPB package;
  its public roadmap lists Navisworks and Power BI as in progress at this review, and its latest
  commit publishes a bundled Windows installer.
- [Demolinator Revit MCP Server at `40af5a7`](https://github.com/Demolinator/revit-mcp-server/tree/40af5a7860b4470ad8f80ea327cf4a9cd31ca0a6): publishes 48
  tools over pyRevit for Revit 2024–2027, broad element/view/MEP creation and PDF/image/IFC export.
- [revit-mcp-server](https://github.com/mcp-servers-for-revit/revit-mcp): archived predecessor,
  retained only as historical context.

Published breadth is not converted automatically into levels 4–5. To receive
those points a competitor run must capture the post-operation model/file/API
measurement and repeat the same idempotency key after a deliberately lost
reply. This rule applies equally to Horizun.

### Current public-interface comparison

| Capability | Horizun public repository | SAM public repository | Demolinator public repository |
| --- | --- | --- | --- |
| Revit years | 2023–2027, per-year binary | 2024–2027 | 2024–2027 |
| Typed breadth | Broad batched authoring + specialized QA | Broadest published tool count | Broad 48-tool surface |
| Post-commit invariant | Repository-wide typed-write rule | Not established from README claim alone | Not established from README claim alone |
| Durable at-most-once | Revit + host external mutation ledger | Not established from public README | Not established from public README |
| NWC | Implemented natively | Roadmap/in progress | Not established |
| FBX | Implemented natively | Not established | Not established |
| Direct Power BI rows | Implemented with fixed endpoints and durable replay | Roadmap/in progress | Not established |
| Loaded RFA compiler | Parameters/types/reference skeleton/sweeps/nested RFA/MEP connectors | Family capabilities require pinned case run | Family creation claim requires pinned case run |
| System-family types | Dedicated verified operation, including host compound layers | Requires pinned case run | Requires pinned case run |
| Installation | One setup; no Git/SDK; checksum/manifest/SBOM; guarded Codex/Claude registration | Bundled Windows installer, MCPB and source workflow | Source/pyRevit workflow |
| Publicly trusted signing | No | Must be checked per asset | Must be checked per asset |

This table does not say that an undocumented competitor feature is impossible;
it says it is not benchmark evidence yet. Before publishing an overall
competitor score, the harness must pin commits, install each server on a clean
machine, execute the same fixtures and attach raw outputs. Horizun's own live-
pending rows are subject to the same rule.

## Reproduction

```powershell
dotnet test tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj -c Release
dotnet test tests/Horizun.Server.Tests/Horizun.Server.Tests.csproj -c Release
dotnet build src/Horizun.Revit/Horizun.Revit.csproj -c Release -p:RevitYear=2023
# Repeat the last command for 2024, 2025, 2026 and 2027.
pwsh scripts/verify-live.ps1 -Year 2026 -ReleaseGate -ExpectedCommit <commit>
pwsh scripts/verify-queue-live.ps1 -Year 2026 -Document <fixture>
pwsh scripts/verify-idempotency-live.ps1 -Year 2026 -Document <fixture>
```

Future release gates should retain explicit live fixtures for O1–O6 and X1–X3
before assigning those rows evidence grade L. X4 requires a disposable Power BI
push semantic model and must never use a production table as its fixture. Hosted
CI intentionally reports these checks as skipped when the private Revit runner is
not configured.
