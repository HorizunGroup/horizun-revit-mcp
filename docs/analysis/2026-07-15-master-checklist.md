# rvt-mcp master checklist (verified 2026-07-15)

Tracking list from owner brainstorm. Each item verified against code via explore sub-agents + direct reads.  
**Waves A–C closed 2026-07-15** — see `2026-07-15-product-decisions-closeout.md`. Paperwork for toast + non-goals updated (README, CHANGELOG Unreleased, roadmap, design statuses).

Legend:

| Status | Meaning |
|---|---|
| **DONE** | Implemented; only docs/status text may lag |
| **PARTIAL** | Works under conditions / incomplete |
| **MISSING** | Not in product |
| **DISCUSS** | Needs product/tech decision before code |
| **VERIFY** | Code likely OK; needs live smoke |

**Speed tier:** Q = hours–1 day (check/docs/small code) · M = multi-day feature · L = multi-week / research spike

---

## Wave A decision log

| Item | Decision | Date | Notes |
|---|---|---|---|
| **A1** Toast | **PASS** | 2026-07-15 | Result-only toast shipped; owner live OK. Docs: README `enableToast`, CHANGELOG Unreleased, design 07-06 **SHIPPED**. |
| **A2** Enable bake / adaptive bake UX | **PASS** | 2026-07-15 | Adaptive OFF default; env/CLI/JSON + reconnect; list/accept/run; no VS. |
| **A3** Bake vs body lock / privacy | **PASS** | 2026-07-15 | Body locked by default; send_code bake needs `cacheSendCodeBodies` + adaptive. |
| **A4** Bake needs VS? | **PASS** | 2026-07-15 | No VS/MSBuild; Roslyn in-process. |
| **A5** Status visibility for privacy/bake flags | **PASS** | 2026-07-15 | Status dialog + tooltip; no risk-toggle ribbon. |
| **B** Family authoring (#7) | **PASS (will not do this cycle)** | 2026-07-15 | Context budget. Project `families` only; authoring → **`send_code`**. |
| **C1** Python send_code | **PASS (will not do)** | 2026-07-15 | C# only. |
| **C2** Revit Viewer | **PASS (will not do)** | 2026-07-15 | Full Revit only. |

---

## Master list (priority = solve-fast first)

| # | Topic | Status | Speed | Next action |
|---|---|---|---|---|
| **1** | Toast hoàn thiện? | **DONE** · **A1 PASS** | **Q** | Closed |
| **2** | `send_code` + bake enable | **DONE** · **A2 PASS** | **Q** | Closed |
| **3** | Bake khi body “khóa” | **PARTIAL by design** · **A3 PASS** | **Q** | Closed |
| **4** | Bake cần VS? | **NO** · **A4 PASS** | **Q** | Closed |
| **5** | Risk switch / Status UX | **A5 PASS** | **Q** | Closed |
| **6** | Python send_code | **WILL NOT DO** · **C1** | **L** | Use C# `send_code` |
| **7** | Family authoring (#7) | **WILL NOT DO this cycle** · **B** | **M→L** | Use `send_code` |
| **8** | Revit Viewer | **WILL NOT DO** · **C2** | **L** | Full Revit only |

**Out of typed scope → `revit_send_code_to_revit` (C#).**  
Still open outside waves: KEI uncommitted; formal v0.6.0 tag — see completion backlog.

---

## 1. Toast — verified **DONE**

**Code:** `src/shared/Views/Toast/*`, wired from each `plugin-r**/App.cs` → `McpEventHandler`.

| Expectation | Verdict |
|---|---|
| Toast “đang làm” (in-progress) | **Removed** — result-only by design |
| Toast when call completes | **Yes** — `McpToastNotifier.OnCompleted` (4 paths in `McpEventHandler`) |
| Capture success + thumbnail + click-open | **Yes** — allowlist paths only |
| Thumbnail size cap | **Yes** — 8 MB load + decode width 300 |
| `enableToast` | Default **OFF**; ribbon toggle persists JSON; env `BIMWRIGHT_ENABLE_TOAST` |
| send_code privacy on toast | Redact scoped to send_code; capture keeps real path |

**Docs (2026-07-15):** design 07-06 marked **SHIPPED**; README documents `enableToast` + result-only behavior; Status lists toast ON/OFF; close-out note has toast facts table.

---

## 2. Unlock conditions for `send_code` — verified **default ON**

| Gate | Effect on `revit_send_code_to_revit` |
|---|---|
| Toolset `toolbaker` | **Default ON** |
| `--disable-toolbaker` / `BIMWRIGHT_ENABLE_TOOLBAKER=0` | Tool hidden |
| `--read-only` | toolbaker stripped → **hidden** |
| Adaptive bake flag | **Does not** gate send_code |
| Plugin `#if ALLOW_SEND_CODE` / plugin env | **None** — handler always registered |
| Sibling products (dwg/nwd/ipt) | send_code **OFF** + dual opt-in — **different** from rvt |

**Important split:**

| Concept | Default | Unlocks execution? |
|---|---|---|
| Tool available to agent | **ON** | Yes |
| `cacheSendCodeBodies` | OFF | **No** |
| `persistSendCodeBodies` + TTL | OFF | **No** |

**Risk:** Plugin will still run wire command `send_code_to_revit` even if MCP tool is hidden (trust = loopback + token). Not dual-gated like siblings.

**Doc bug:** `ARCHITECTURE.md` may still claim adaptive-bake gates send_code — **false**.

---

## 3. Bake when bodies are locked — verified **PARTIAL by design**

Pipeline (adaptive bake **master OFF by default**):

```
tool calls → UsageEventLogger (usage.jsonl) → ClusterEngine → suggestions
  → accept → plugin ToolCompiler (Roslyn) → bake.db registry
  → list_baked_tools / run_baked_tool
```

| Source type | Without body cache | With `cacheSendCodeBodies=1` |
|---|---|---|
| **preset** patterns | Can cluster/suggest | Same |
| **macro** sequences | Can cluster/suggest | Same |
| **send_code** | **Cannot** usefully cluster (all keyed `body-cache-disabled`) | **Can** (redacted `cluster_material`) |

- `mcp-calls.jsonl` is **hash-only** for send_code — **not** bake’s input; bake uses `usage.jsonl`.
- `BakeRedactor` **sanitizes** (paths/secrets), does **not** wipe entire source when cache/journal on.
- After **successful accept**, `run_baked_tool` uses **stored `source_code`** in `bake.db` — does **not** need original history.
- send_code **accept** needs condensation samples (+ often `ANTHROPIC_API_KEY`) — fails without cache samples.

**Answer to “bake works if send_code locked?”**  
- **Run already-accepted tools:** **YES**.  
- **Discover new tools from send_code:** **NO** until `enableAdaptiveBake` + `cacheSendCodeBodies`.  
- **Discover from presets/macros:** **YES** with adaptive bake alone.

---

## 4. Bake without Visual Studio 2022 — verified **NO VS required**

| User needs | Required? |
|---|---|
| Visual Studio / MSBuild project build | **No** |
| Separate `dotnet` SDK for bake runtime | **No** |
| Revit + rvt-mcp plugin (ships Roslyn DLLs) | **Yes** |
| MCP server for agent accept/list/run | **Yes** |
| `ANTHROPIC_API_KEY` | Only for **send_code** condense/name; not preset/macro/run |

**Mechanism:** `ToolCompiler.Compile` — Roslyn `CSharpCompilation` → `Emit` → `MemoryStream` → `Assembly.Load` **inside Revit process**. Not write `.csproj` + build.

**Developer** building the repo still needs normal .NET SDK — unrelated to end-user bake.

---

## 5. Switch “user accepts risk, can re-read bodies?” — verified **already exists**

Not one umbrella switch — **three** knobs:

| Switch | Role | Default |
|---|---|---|
| `enableAdaptiveBake` | Usage log + suggestions surface | OFF |
| `cacheSendCodeBodies` | Redacted bodies for **bake clustering** | OFF |
| `persistSendCodeBodies` + `Until` TTL | Disk journal for **troubleshoot/improve** (1h–2d, purge 7d) | OFF |

**Trade-off already productized:** default privacy; opt-in for bake quality and/or journal.

**Possible product enhancement (DISCUSS, optional):** single “I accept risk” profile that flips adaptive + cache + TTL together — **convenience only**, not a security gap fix.

---

## 6. Expand `send_code` to Python — **DISCUSS / L**

**Today:** C# only — body wrapped as static `Run(UIApplication)`, Roslyn compile in plugin (`SendCodeToRevitHandler` + same Roslyn stack as bake).

| Option | Feasibility notes |
|---|---|
| **CPython ≥3.10 in-process** | Hard: need embed runtime, ship or detect install, native deps, Revit API interop (pythonnet?), security sandbox, multi-version Revit TFMs. Large surface. |
| **IronPython 2.7** | Historical Revit/Dynamo path; language frozen; limited libs; still not “free” — must host engine + API bindings; many packages lack IPY support. |
| **Out-of-process Python calling MCP/C# bridge** | Doesn’t replace in-Revit API access unless you still round-trip to C#/send_code. |
| **Keep C#; improve DX** | Snippets, templates, typed tools, bake — lower risk. |

**Dependency reality:** any Python path couples **host environment + library packaging + API bridge**, not just “change language flag.”

**Recommendation:** keep as **discussion / ADR** after Q items; do not block family/viewer decisions.

---

## 7. Family environment tools — **PARTIAL**

### Have today (project document)

Management/placement under `families` (+ query/create):

- list/load/unload, types, instances, audit, replace/duplicate/rename type  
- `export_family_to_path` — **one-shot** `EditFamily` → `SaveAs` → `Close`  
- place instances (`create_point_based_element`, MEP fixtures, structural, …)  
- escape: `send_code` for ad-hoc Family Editor C#

### Missing (#7 authoring)

Session-based family document tools: open/create session, chassis ref planes, `FamilyManager` params/formulas, flex, label dim, extrusion, QA, save session.

Design: `docs/superpowers/specs/2026-06-18-family-authoring-phased-plan-design.md`  
Spike: compile **PASS 6/6**; **runtime evidence + gate blank**.

**Minimal path:** close Phase 0 gate → Phase 1 session spine only (open/create/status/save/close).

---

## 8. Revit Viewer environment — **MISSING**

- No `IsViewer` / product-type in discovery  
- Discovery file is year-only (`revit-YYYY.json`) — full Revit + Viewer same year would collide  
- No toolset or docs for Viewer  
- Write-heavy surface assumes full Revit + transactions  

**Verdict: explicit NO Viewer support today.**

**If ever:** host loadability spike → discovery product field → install path → force read-only allowlist. First tools would be pure read (view info, selection, element details, filters) — not family edit / send_code / export suite.

---

## Suggested discussion / work order

```text
Wave A — DONE (A1–A5 PASS)  including toast docs + Status flags
Wave B — DONE (will not do family-authoring tools); use send_code
Wave C — DONE (will not do Python host; will not do Viewer)
Posture — out of typed scope → revit_send_code_to_revit (C#)
Open   — KEI uncommitted; formal 0.6.0 release tag when ready
```

### Wave B / C note (2026-07-15)

- **B:** No `revit_family_*` / Phase 0 gate. Project `families` only. Authoring → **`send_code`**.
- **C1:** No Python/IronPython execution host.
- **C2:** No Revit Viewer host support.
- **Canonical write-up:** `docs/analysis/2026-07-15-product-decisions-closeout.md`.

---

## Evidence sources

- Sub-agent explore (2026-07-15): toast+send_code gates; bake mechanics; family+viewer  
- `docs/bake.md`, `ToolCompiler.cs`, `ClusterEngine.cs`, `UsageEventLogger.cs`, `RvtMcpConfig.cs`  
- `docs/superpowers/specs/2026-06-18-family-authoring-…`, spike log  
- Sibling note: `docs/analysis/2026-07-15-completion-backlog-findings.md`
