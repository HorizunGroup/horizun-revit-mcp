# Revit MCP benchmark

Updated: 2026-08-25. This benchmark measures useful outcomes, not the number of
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
| P1 | Planimetry query | The whole documentation surface — sheets, views, placements, annotations, references — is readable FROM THE MODEL with real ElementIds, declared coordinate frames and units, exact totals under pagination, and a total that could not be computed reported as absent-and-named rather than zero. |
| P2 | Universal audit | Findings without a company standard: overlapping placements beyond an explicit tolerance where touching is NOT overlapping, sheets with zero or several title blocks, broken placements and references, orphan and duplicate tags, empty text, degenerate detail, annotations demonstrably outside an active crop. An unreadable fact is `unknown` and never a pass, and a check with unknowns is never `passed`. |
| P3 | Configurable requirement sets | Everything with a number or a name in it — naming, allowed scales/templates/types, margins, gaps, required parameters, forbidden overrides, tag coverage — arrives as an INLINE artifact, is refused whole when malformed, and stamps its id, version and SHA-256 on every finding it produced. No standard is compiled into the binary. |
| P4 | Typed, verified correction | A finding becomes a typed write whose every promised property is re-read from the committed model, in one atomic batch that rolls back entirely on any failed check, with no export and no arbitrary-code path anywhere on it. |
| P5 | Stale plan and rollback | A finding that no longer exists, an observed state that moved, an `unknown`, a modified requirement set or a model that moved between rehearsal and apply each refuse with NOTHING written; a failed postcondition rolls the whole batch back rather than leaving a partial commit. |
| P6 | At-most-once correction | A correction requires a durable idempotency key; an identical retry replays the recorded answer without writing again, a conflicting key refuses, and a claim-only record (a lost response) becomes in-doubt rather than a second write. |
| P7 | Multi-version live matrix | The whole planimetry surface — query, audit and fix — is exercised against a real Revit for 2023, 2024, 2025, 2026 and 2027, with failures and uncovered cases published rather than omitted. |
| P8 | Autonomous production | Sheets are composed, populated, tagged, dimensioned, revised and judged end to end without a human choosing each placement: automatic packing, auto-tagging, dimensioning by intent, revision generation and visual review. |

In-place families are deliberately not a pass condition: Autodesk's public
Revit API does not provide general creation of them. A competitor receives no
credit for driving the modal editor through screen coordinates. Loaded RFAs and
project-resident system types are measured separately because they are different
Revit concepts.

## Horizun source-candidate result

| Case | Score | Evidence now | Implementation |
| --- | ---: | --- | --- |
| O1 | 5 | B/S; L pending for new kinds | `horizun_create_elements`, one transaction, confirmation and durable replay. |
| O2 | 5 | B/S; L pending | Typed structural curve/point creation and structural-type re-read. |
| O3 | 5 | B/S; L pending for cable tray | Native typed MEP APIs. |
| O4 | 5 | B/S/T; L for the W10 batch (area plans, callouts, placeholder conversion, view range/crops, alignment) pending green matrix | `horizun_manage_views` at 24 operations, aliases, per-value post-commit re-reads (view ranges, crops, rotations, alignments), document+batch-unique sheet numbers. |
| O5 | 5 | B/S; L pending | `horizun_create_family`; RFT→RFA compiler with connector face selection and file/project verification. |
| O6 | 5 | B/S; L pending | `horizun_manage_system_types`; generic type parameters plus verified host compound structures. |
| O7 | 5 | L/T/S | Native linked schedule creation/list/read path. |
| X1 | 5 | B/S; L pending for expanded options | `horizun_export` IFC branch. |
| X2 | 5 | B/S; L pending | Native optional NWC exporter branch. |
| X3 | 5 | B/S; L pending | Native multi-View3D FBX branch. |
| X4 | 5 | T/S; tenant L pending | Direct REST; tests prove bounds, one send on replay and fail-closed lost response. |
| R1 | 5 | L/T/S | Bounded FIFO, fairness, capacity and cancellation harnesses. |
| R2 | 5 | L/T/S | Append-only durable ledger shared by Revit and Power BI mutations. |
| D1 | 5 | L/T/S | One-paste setup, explicit permanent unsigned disclosure, checksum, payload manifest, SBOM, provenance attestation, safe deferred Claude/Codex registration, durable resume and first-live health verification. CI refuses misleading invalid/self-signed states and verifies the exact installed bytes. |
| D2 | 5 | B/L | Five independently compiled payloads. |
| P1 | 5 | L/T/S | `horizun_query_planimetry`, six modes over one collector; cursors bound to arguments AND result set; unreadables named. Live-verified 2023–2027 on 2026-08-24 (11 query cases per year). |
| P2 | 5 | L/T/S | `horizun_audit_planimetry`, 46 universal checks; `unknown` never passes and a check with unknowns is never `passed`. Live-verified 2023–2027 on 2026-08-24 (11 audit cases per year). |
| P3 | 5 | L/T/S | Inline requirement sets with canonical SHA-256, refused whole when malformed; nothing corporate compiled in. Live-verified inside the P2 cases. |
| P4 | 5 | L/T/B/S | `horizun_fix_planimetry`: nine operations, rehearsed by provisional materialisation, one `TransactionGroup`, every promised property re-read, and the audit re-run so `resolved` is the rule's verdict rather than the write's. |
| P5 | 5 | L/T/B/S | Stale finding, stale observation, `unknown`, modified requirement set and `stale_plan` all refuse with nothing written; any failed postcondition rolls the whole batch back. |
| P6 | 5 | L/T/S | The durable ledger every typed mutation shares: replay on an identical retry, refusal on a conflicting key, in-doubt on a claim-only record. Live-verified for the fix path in all five years on 2026-08-25. |
| P7 | 5 | L/B | The whole planimetry surface — query, audit, fix and production — ran against real Revit 2023, 2024, 2025, 2026 and 2027 on 2026-08-25 at candidate `32baa87`: 165 probes per year, 825 total, 0 failed / 0 unverified / 0 not covered. The durable evidence separately names 22 query/audit, 23 correction and 5 production cases per year. Instabilities encountered on the way remain published in production-readiness rather than omitted. |
| P8 | 5 | L/T/B/S | `horizun_pack_sheets`, collision-aware auto-tag planning plus verified explicit-type annotation, semantic intent dimensioning, atomic revision/sheet/cloud production and direct sheet capture without PDF are live-verified on Revit 2023–2027 at `32baa87`: 5/5 production cases per year, 25/25 total. Packing measures real provisional viewport+label/schedule extents with confirmed rollback and preserves insertion-point offset; the visual-review prompt requires exhaustive model facts plus actual sheet PNGs and returns UNKNOWN on missing evidence. |

Twenty-three cases, so the ceiling is **115**. Current source-candidate total:
**115/115** under the permanent unsigned-release policy. D1 measures deployable,
verified distribution; it does not claim Windows publisher authentication.

P8 reached 5 only after packing, auto-tagging, intent dimensioning, revision
generation and direct visual evidence all passed in every supported year. The
matrix, harness commit and each full artifact SHA-256 are pinned in
`docs/evidence/live-matrix.json` (private evidence, kept out of the public
repository); the score is not inferred from composability.

## Market baseline

The market changed materially in June 2026: Autodesk now ships an official Revit
Public MCP Server technical preview. “Best” therefore cannot mean one scalar or
“has the most tool names”. Official support, safe read access, workflow breadth,
verified mutation, version coverage, extensibility and distribution are distinct
axes and sometimes conflict.

The public comparison set was re-pinned on 2026-08-20:

- [Autodesk Revit Public MCP Server](https://www.autodesk.com/blogs/aec/2026/06/17/revit-public-mcp-server/):
  official and supported for Revit 2027, automatically configures Claude Desktop
  or Cursor, and deliberately exposes seven read/navigation/export operations.
- [Shuotao Revit MCP at `bae94d9`](https://github.com/shuotao/REVIT_MCP_study/tree/bae94d961d5f5d7d0f7124232a7c7b0204abc8e1):
  the largest documented public surface in this pass: 173 MCP tools and 76 BIM SOPs,
  Revit 2022–2026, distributed through npm.
- [KenLP RevitMCPServer at `a248bb8`](https://github.com/KenLP/RevitMCPServer/tree/a248bb85021f3d97798348710133537ffe249648):
  93 exposed tools, Revit 2025–2027, schema validation, dry-runs and one-transaction
  multi-step batches.
- [LuDattilo revit-mcp-server at `2c33b84`](https://github.com/LuDattilo/revit-mcp-server/tree/2c33b848602ca56a4043de603f2914d6fdf0c104):
  124 documented MCP tools (the README header also says 138 exposed), Revit
  2023–2027 and prebuilt release installation.
- [BIMwright rvt-mcp at `47d3619`](https://github.com/bimwright/rvt-mcp/tree/47d36194d35cac787c199d5b45be49dd4039b7f7):
  widest documented Revit-year range, 2022–2027, with an end-to-end C# server/plugin.
- [Revit MCP v2 at `95880f4`](https://github.com/mskim274/revit-mcp-v2/tree/95880f480da15158d56d674c4089434a8a6314bb):
  smaller 37-tool surface but notable hot-reloadable command set, session routing,
  checksums, attestations and bounded responses.
- [SAM Autodesk Revit MCP Server at `bc31aef`](https://github.com/Sam-AEC/Autodesk-Revit-MCP-Server/tree/bc31aeffece0f1d5cb9812f46e80462b3b0f93cd):
  100+ documented tools, Revit 2024–2027 and a reflection escape hatch.
- [Demolinator at `11bd37a`](https://github.com/Demolinator/revit-mcp-plugin/tree/11bd37a5f98868a458508c381d4da52af623fa72):
  48 documented tools for Revit 2024–2027.

Repository statements are discovery evidence, not automatically proof of a
postcondition. A level 4–5 mutation still requires the same fixture, model/file/API
re-read and lost-response replay for every product, including Horizun.

### Where Horizun leads, ties and does not lead yet

| Axis | Strongest market evidence | Horizun position on 2026-08-20 | What remains |
| --- | --- | --- | --- |
| Vendor trust/support | Autodesk official server | Cannot honestly beat Autodesk on vendor identity or Revit entitlement support | Complement it; publish signed binaries and independent evidence |
| Safe default | Autodesk read-oriented preview | Typed in-model writes available, Python and external/session effects off by default; MCP may request but only the Revit user may persistently enable or revoke Python | Live adversarial proof and approval history |
| Raw breadth/SOP library | Shuotao 173 tools / 76 SOPs | Lower raw count; broader verified verticals in family authoring, exports and Power BI | Versioned recipe/SOP marketplace, without compiling client standards into the bridge |
| Version range | BIMwright 2022–2027 | 2023–2027 | Revit 2022 only if demand justifies a separately tested payload |
| Atomic multi-step writes | KenLP documents one-transaction batches | `horizun_execute_plan`, dry-run, stale-plan binding, rollback trace and postcondition re-read | Publish common live fixtures against both products |
| At-most-once recovery | No stronger public evidence found in this pass | Durable idempotency ledger and MCP Tasks backed by durable Revit jobs | Crash/kill matrix and task cancellation before start |
| MCP protocol surface | Varies by repository | Tools, Resources, Prompts, Completions, progress, opt-in Logging and 2025-11-25 Tasks | Official Inspector/SDK conformance and multi-client matrix |
| Extensibility | Revit MCP v2 hot-reload; Shuotao SOP library | Typed source extension plus bounded Python escape hatch | Signed plugin/recipe SDK and isolated hot reload |
| Install/provenance | Autodesk entitlement install; several prebuilt community packages | Manifest/SBOM/checksum/attestation pipeline; public identity absent | Public Authenticode identity and clean-machine install evidence |
| Performance | No comparable public fixture series found | Queue/backpressure and response budgets are tested | Publish latency, peak memory and longest continuous UI block by model size/year |

### Definition of “better than all” used here

Horizun may claim leadership only per axis with reproducible evidence. It must not
claim universal superiority while any of these remain open:

1. public Authenticode identity and clean-machine verification;
2. live certification matrix for Revit 2023–2027, with failures and uncovered
   cases published rather than omitted;
3. modeless Revit approval/history UI for high-risk operations;
4. cooperative progress/cancellation for operations explicitly safe to chunk;
5. signed extension/recipe SDK with compatibility and isolation policy;
6. common-fixture performance and outcome runs against pinned competitors;
7. MCP Inspector plus Codex, Claude, Cursor and VS Code compatibility evidence.

Public certification procurement is external. Everything else above can be built
or evidenced in this repository, and the release gate must stay fail-closed where
external proof is absent.

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

P1-P7 are measured by the planimetry probes inside `verify-live.ps1` under
`-WriteProbes`; P4-P6 need the write tier because a correction that is only
rehearsed proves the refusals and not the commit. P8 has no reproduction step
by design: there is nothing to run.

The release gate must add explicit live fixtures for O1–O6 and X1–X3 before the
new source candidate is tagged. X4 requires a disposable Power BI push semantic
model and must never use a production table as its fixture.
