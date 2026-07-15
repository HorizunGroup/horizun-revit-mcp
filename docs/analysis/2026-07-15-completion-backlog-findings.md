# rvt-mcp completion backlog findings

- **Date:** 2026-07-15 (updated same day after waves A–C close-out)
- **Scope:** What remains after audit + product decisions.
- **Closed waves:** A (toast/bake/status), B (no family-authoring tools), C (no Python host, no Viewer). Canonical: `2026-07-15-product-decisions-closeout.md`.
- **Posture:** out of typed tool scope → **`revit_send_code_to_revit` (C# only)**.

---

## 1. Bottom line

| Category | Reality |
|---|---|
| Toast / BCD / capture / Status flags | **Shipped** on master (+ Status privacy section); docs updated 2026-07-15 |
| Family authoring / Python / Viewer | **Will not do this cycle** — use `send_code` where relevant |
| **Uncommitted product code** | **KEI toolset** only (handlers + guard + golden + local doc) |
| Open public issues | **#7** still open upstream; implementation not scheduled |

**One-liner:** Product waves closed; remaining code delta on disk is **KEI** (plus formal 0.6.0 tag when ready).

---

## 2. Dirty working tree (code)

On `master` (relative to `origin/master`), uncommitted:

| Path / area | Role |
|---|---|
| `src/shared/Handlers/*Kei*`, `ImportProjectEquipment`, `GetActiveProjectDb` | Plugin commands for KEI SQLite |
| `src/shared/Database/KeiSqlGuard.cs`, `KeiDatabaseResolver.cs` | SQL guard + path resolve |
| `src/server/Program.cs` (`KeiTools`), `ToolsetFilter.cs` (`kei` default-on) | MCP surface |
| `CommandDispatcher.cs` | Handler registration |
| Golden + `ToolsetFilterTests` + `KeiSqlGuardTests` | Test surface |
| `docs/kei-equipment-import.md` | Local ops note |
| `publish/` (~60 MB) | **Do not commit** (build output) |

**Product decision still open:** KEI was historically **stripped** from the public tree. Re-adding as default-on toolset needs explicit public vs private vs opt-in choice before “finish & commit.”

**To close KEI as code (when decided):** tests green, live smoke with Revit + real `*_local.db`, no `publish/` in git, then later paperwork (counts/CHANGELOG/mcps).

---

## 3. Already shipped on `master` (plan docs may be stale)

| Workstream | Evidence |
|---|---|
| B — persist `send_code` bodies + TTL | Commits + `SendCodeJournal.cs`, `PersistSendCodeTtl.cs` |
| C — capture path UX | Commits + `CaptureViewImageHandler` default/normalize path |
| D — toast thumbnail cap / hardening | Commits + toast subsystem |
| A — README count / factual sync (partial) | Commits on 2026-07-09 line |

Version string: **`0.6.0-dev`**. **CHANGELOG still ends at v0.5.0** (paperwork debt).

Stale plan/spec paths (checkboxes not closed; do not re-implement):

- `docs/superpowers/plans/2026-07-09-rvt-mcp-bcd-sendcode-persist-capture-toast.md` (monorepo root `docs/`)
- `docs/superpowers/specs/2026-07-09-session-backlog-…`
- `docs/superpowers/specs/2026-07-06-rvt-toast-result-only-capture-fix-design.md` (status text outdated)

---

## 4. Paperwork / process (deferred by owner)

When docs pass is allowed:

1. CHANGELOG **0.6.0** (toast, persist send_code, capture UX, any KEI decision).
2. README / i18n tool counts if KEI ships or toolsets change.
3. Mark BCD/toast plans **shipped**; refresh `docs/roadmap.md` (still describes ~47 tools / v0.3 era).
4. Optional: `mcps/rvt-mcp` schema regen if publishing registry.
5. Tag/release when leaving `0.6.0-dev` (release assets still largely manual — only `build.yml`).

---

## 5. Optional code hardening (not required to call 0.6 “feature complete”)

| Item | Kind |
|---|---|
| Strip non-win-x64 `runtimes/` in plugin ZIP | Script / packaging |
| `release.yml` for tag → zip + nupkg | CI |
| Verify NuGet `ToolCommandName=rvt-mcp` vs install exe naming | Small verify |
| AspNetCore slim-down (~40 DLLs for stdio) | Larger server refactor |
| Firm profiles content under `docs/firm-profiles/` | Content, not handlers |
| Schedule handler TODOs (combined fields, cell merge API) | Edge polish |
| Roadmap v0.4+ (MCP Resources, async jobs, prompt library, signed bake) | Future features |

---

## 6. Issue #7 pointer (not in this backlog’s “do” list)

- **Issue:** [#7 Family Authoring Tool Suite](https://github.com/bimwright/rvt-mcp/issues/7)
- **Intent:** MCP tools that operate in a **Revit family document** (Family Editor / background family `Document`): ref planes, family params/formulas, extrusion, flex test, save — not project-level place/load family only.
- **Design / spike already in tree:**
  - Spec: `docs/superpowers/specs/2026-06-18-family-authoring-phased-plan-design.md`
  - Phase 0 plan: `docs/superpowers/plans/2026-06-18-family-authoring-phase0.md`
  - Spike log: `docs/superpowers/spikes/2026-06-18-family-authoring-phase0-spike.md` (compile matrix PASS 6/6; **runtime evidence + gate decision blank**)
- **Today’s family-related tools are project/management only** — see § next discussion. No production Family Editor authoring surface.

---

## 7. Suggested discussion order (after this note)

1. Decide KEI public/private/opt-in (code commit vs leave dirty).
2. Discuss **#7** Family Authoring: scope, gates, Phase 0 completion, vs escape-hatch `send_code` only.
3. Later: paperwork 0.6 + optional packaging hardening.
