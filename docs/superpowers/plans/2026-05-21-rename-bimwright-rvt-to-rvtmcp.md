# Rename RvtMcp → RvtMcp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **This plan is also intended to be handed to `opencode` CLI for implementation under Claude orchestration. See `executing-plans-via-opencode` skill.**

**Goal:** Rename the codebase from `RvtMcp.*` to `RvtMcp.*` so MCP clients/agents see a name that immediately conveys "Revit MCP", while preserving Bimwright branding in copyright/author metadata and keeping the GitHub repo path `bimwright/rvt-mcp` unchanged.

**Architecture:** A wide-but-shallow rename. Touches ~200 files but each file changes a fixed set of identifiers. The work is sequenced into 7 waves where each wave produces a buildable, testable state. The risk surface is concentrated in three areas — discovery file paths, deploy folder paths, and the NuGet package id — each of which must be changed atomically with companion code paths and migration shims.

**Tech Stack:** C# / .NET 8 (server) + .NET 4.8/8/10 (plugin shells), MSBuild, xUnit, PowerShell, NuGet, Smithery, GitHub Actions.

---

## Pre-flight Findings (read before starting)

**Surface area (from `grep` against working tree):**

- ~2,001 occurrences of `bimwright` (case-insensitive) across 200+ files
- ~1,301 occurrences of `RvtMcp` specifically
- 18+ `.cs` files reference the literal string `"Bimwright"` for `LOCALAPPDATA` path (`Path.Combine(..., "Bimwright")`)
- 6 `.addin` files reference `Bimwright\RvtMcp.Plugin.dll`
- 6 plugin `.csproj` files reference `$(APPDATA)\Autodesk\Revit\Addins\<year>\RvtMcp\`
- 1 solution file + 8 csproj names use `RvtMcp.*`
- 1 GitHub workflow (`.github/workflows/build.yml`) references csproj paths and artifact names
- 1 `server.json`, 1 `smithery.yaml`, 1 `.mcp.json.example` reference user-facing names
- 9 doc files (README × 3, ARCHITECTURE, AGENTS, CLAUDE, SECURITY, CONTRIBUTING, CHANGELOG) need updates
- 20+ files under `docs/` reference `BIMWRIGHT_` env vars and `Bimwright` brand
- 30+ test files in `tests/RvtMcp.Tests/` reference `RvtMcpConfig` and namespace `RvtMcp.*`

**NuGet status (verified 2026-05-21):**

```
GET https://api.nuget.org/v3-flatcontainer/bimwright.rvt.server/index.json
→ { "versions": ["0.1.0", "0.2.0", "0.3.0"] }
```

- `RvtMcp.Server` 0.1.0, 0.2.0, 0.3.0 are **published**.
- Working tree is at 0.4.0 — **not yet published**.
- Plan ships the rename **as part of 0.4.0 release**. Old package gets a deprecation notice. New package `RvtMcp.Server` starts at 0.4.0.

**Decisions locked in (per user, 2026-05-21):**

| Item | Decision |
|---|---|
| MCP server registration name | `rvt-mcp` (single entry, no R22-R27 fan-out) |
| Code identifier prefix | `RvtMcp.*` → `RvtMcp.*` (full rename) |
| Brand `Bimwright` | Keep ONLY in csproj `<Authors>`, `<Copyright>`, `<Description>` text. Remove from namespaces, file names, paths, vendor IDs. |
| AddInId GUIDs | **KEEP unchanged.** Ship `uninstall-old.ps1` to clean legacy `Addins\<year>\RvtMcp\` folder. |
| NuGet | New package `RvtMcp.Server` 0.4.0. Old `RvtMcp.Server` 0.3.0 deprecated with redirect note in README. |
| GitHub repo path | Keep `bimwright/rvt-mcp` — not renaming repo. |
| Implementation | Plan-then-opencode workflow with per-wave human review. |

**Decisions locked in by user (2026-05-21, post-plan review):**

| Question | Decision |
|---|---|
| `BIMWRIGHT_*` env var prefix | **KEEP unchanged.** No rename, no alias needed. |
| `<VendorId>bimwright</VendorId>` in `.addin` | **KEEP** `bimwright` as VendorId (publisher segment, not product). |
| `groupName = "Bimwright"` default in `CreateSharedParameter` | **KEEP** `Bimwright` (Revit persists this into user .rvt files; changing breaks compatibility). |
| First-launch data migration | **Auto-migrate** `%LOCALAPPDATA%\RvtMcp\{baked,journal,firm-profiles,*.log}` → `\RvtMcp\` on first run. |

---

## Naming Mapping (canonical reference — opencode reads this)

| Category | Old | New |
|---|---|---|
| **Namespaces** | `RvtMcp.Server` | `RvtMcp.Server` |
|  | `RvtMcp.Plugin` | `RvtMcp.Plugin` |
|  | `RvtMcp.Plugin.Views` | `RvtMcp.Plugin.Views` |
|  | `RvtMcp.Plugin.ToolBaker` | `RvtMcp.Plugin.ToolBaker` |
| **Solution** | `src/RvtMcp.sln` | `src/RvtMcp.sln` |
| **Server csproj** | `src/server/RvtMcp.Server.csproj` | `src/server/RvtMcp.Server.csproj` |
| **Plugin csproj** (×6) | `src/plugin-rXX/RvtMcp.Plugin.RXX.csproj` | `src/plugin-rXX/RvtMcp.Plugin.RXX.csproj` |
| **Test csproj** | `tests/RvtMcp.Tests/RvtMcp.Tests.csproj` | `tests/RvtMcp.Tests/RvtMcp.Tests.csproj` |
| **Test folder** | `tests/RvtMcp.Tests/` | `tests/RvtMcp.Tests/` |
| **Addin manifest** (×6) | `src/plugin-rXX/Bimwright.RXX.addin` | `src/plugin-rXX/RvtMcp.RXX.addin` |
| **Plugin output DLL** | `RvtMcp.Plugin.dll` | `RvtMcp.Plugin.dll` |
| **Server output exe** | `RvtMcp.Server.exe` | `RvtMcp.Server.exe` |
| **NuGet PackageId** | `RvtMcp.Server` | `RvtMcp.Server` |
| **ToolCommandName** | `rvt-mcp` | `rvt-mcp` |
| **MSBuild skip flag** | `BimwrightSkipDeploy` | `RvtMcpSkipDeploy` |
| **MSBuild RootNamespace** (plugin) | `RvtMcp.Plugin` | `RvtMcp.Plugin` |
| **MSBuild AssemblyName** (plugin) | `RvtMcp.Plugin` | `RvtMcp.Plugin` |
| **MSBuild RootNamespace** (server) | `RvtMcp.Server` | `RvtMcp.Server` |
| **MSBuild AssemblyName** (server) | `RvtMcp.Server` | `RvtMcp.Server` |
| **Deploy folder** | `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` | `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` |
| **Discovery folder** | `%LOCALAPPDATA%\RvtMcp\` | `%LOCALAPPDATA%\RvtMcp\` |
| **Server install root** | `%LOCALAPPDATA%\RvtMcp\rvt\server\<ver>\` | `%LOCALAPPDATA%\RvtMcp\server\<ver>\` |
| **C# class `RvtMcpConfig`** | `RvtMcpConfig` | `RvtMcpConfig` |
| **C# class `RvtMcpConfigTests`** | `RvtMcpConfigTests` | `RvtMcpConfigTests` |
| **C# class `RvtMcpConfigAdaptiveBakeTests`** | `RvtMcpConfigAdaptiveBakeTests` | `RvtMcpConfigAdaptiveBakeTests` |
| **Backup file suffix** (install.ps1) | `*.bimwright.bak` | `*.rvtmcp.bak` |
| **GH artifact names** | `bimwright-plugin-*`, `bimwright-server-nupkg`, `rvt-mcp-client-setup` | `rvtmcp-plugin-*`, `rvtmcp-server-nupkg`, `rvtmcp-client-setup` |
| **Client setup zip** | `RvtMcp.Setup-*-win-x64.zip` | `RvtMcp.Setup-*-win-x64.zip` |
| **Addin Name** (XML) | `<Name>Bimwright</Name>` | `<Name>RvtMcp</Name>` |
| **Addin VendorDescription** | `<VendorDescription>Revit MCP Gateway</VendorDescription>` | unchanged |

**KEEP unchanged:**

- `<Authors>Khoa Le</Authors>`
- `<Copyright>Copyright 2026 Khoa Le</Copyright>`
- `<Description>` text (may still mention "Bimwright" as the brand)
- `<PackageTags>mcp;revit;bim;ai</PackageTags>`
- `<PackageProjectUrl>` and `<RepositoryUrl>` → `https://github.com/bimwright/rvt-mcp` (repo path unchanged)
- `server.json` `name` field: `io.github.bimwright/rvt-mcp` (the org segment is github username; not changing)
- `<AddInId>` GUIDs in all 6 `.addin` files
- All Project GUIDs in `.sln` (`{5171C140-...}`, `{B1A2C3D4-...}`, etc.)

**Locked decisions (do not change without user re-confirmation):**

- `BIMWRIGHT_*` env vars: **KEEP**. No rename in `RvtMcpConfig.cs` constants or in docs/READMEs.
- `<VendorId>bimwright</VendorId>` in `.addin`: **KEEP** as `bimwright`.
- `groupName = "Bimwright"` default in `CreateSharedParameter`: **KEEP** as `"Bimwright"`.
- First-launch data migration: **YES** (Task 6.2 Step 2 implemented).

---

## Execution Workflow

This plan is executed via **opencode CLI driving keystrokes, Claude orchestrating**. Between waves:

1. opencode performs the wave's tasks.
2. Claude reviews the diff (`git diff` against last commit) for correctness.
3. Claude runs the wave's **Verify** step (e.g., `dotnet build`).
4. If verify passes, Claude makes the commit listed at the end of the wave.
5. If verify fails, Claude diagnoses, instructs opencode to fix, re-verifies.
6. After commit, proceed to next wave.

**No wave commits until verify passes.** No `--no-verify`. No skipping waves.

---

## Wave 0: Pre-flight

**Goal:** Capture a baseline so we can prove the rename didn't break anything functional.

### Task 0.1: Confirm deferred decisions

**Status:** ✅ All four decisions locked in by user on 2026-05-21. See "Locked decisions" block in Pre-flight section. Skip this task and proceed to Task 0.2.

### Task 0.2: Baseline build + test

**Files:** none (read-only)

- [ ] **Step 1: Build the whole solution clean.**

Run: `dotnet build src/RvtMcp.sln -c Debug /p:BimwrightSkipDeploy=true`
Expected: Build succeeds. **If build fails, STOP and notify user — the rename cannot start from a broken baseline.**

- [ ] **Step 2: Run all tests.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug --no-build`
Expected: All tests pass. **If any fail, STOP and notify user.** Record the passing test count in a scratchpad — we'll re-check this number after Wave 7.

- [ ] **Step 3: Snapshot the test count.**

Capture stdout from previous step; note "Passed: N" count. Write to scratchpad: `baseline-test-count.txt`:

```
date: 2026-05-21
commit: <git rev-parse HEAD>
tests-passed: <N>
build-warnings: <count>
```

- [ ] **Step 4: Verify clean working tree.**

Run: `git status -s`
Expected: No uncommitted changes. **If dirty, STOP and ask user — we need a clean starting point.**

### Task 0.3: Create branch + uninstall-old.ps1 stub

**Files:**
- Create: `scripts/uninstall-old.ps1` (stub, filled in Wave 6)
- Branch: `chore/rename-to-rvtmcp`

- [ ] **Step 1: Create feature branch.**

Run: `git checkout -b chore/rename-to-rvtmcp`
Expected: switched to new branch.

- [ ] **Step 2: Create empty `scripts/uninstall-old.ps1`.**

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Remove legacy RvtMcp plugin installation before/after upgrading to RvtMcp.

.DESCRIPTION
  Stub — filled in Wave 6. Cleans up:
    - %APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\ (legacy plugin folder, all years 2022-2027)
    - %APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright.R<XX>.addin (legacy manifests)
    - %LOCALAPPDATA%\RvtMcp\rvt\server\* (legacy server install root)
  Does NOT remove %LOCALAPPDATA%\RvtMcp\ entirely — that contains user data
  (journal, baked tools, logs) that Wave 6 migrates instead.
#>
param([switch]$WhatIf)
Write-Host "uninstall-old.ps1: stub — implementation in Wave 6"
```

- [ ] **Step 3: Commit baseline.**

```bash
git add docs/superpowers/plans/2026-05-21-rename-rvt-mcp-to-rvtmcp.md scripts/uninstall-old.ps1
git commit -m "chore: branch off for RvtMcp → RvtMcp rename"
```

---

## Wave 1: Solution + Project Files

**Goal:** Rename the `.sln` and all 8 `.csproj` files (+ folders/test project). Update `RootNamespace`/`AssemblyName` MSBuild properties. **Do not touch `.cs` code yet** — namespace strings inside files stay `RvtMcp.*` until Wave 2. Build still passes because MSBuild compiles whatever namespace the `.cs` files declare.

**Files:**
- Rename: `src/RvtMcp.sln` → `src/RvtMcp.sln`
- Rename: `src/server/RvtMcp.Server.csproj` → `src/server/RvtMcp.Server.csproj`
- Rename: `src/plugin-r22/RvtMcp.Plugin.R22.csproj` → `src/plugin-r22/RvtMcp.Plugin.R22.csproj`
- Rename: `src/plugin-r23/RvtMcp.Plugin.R23.csproj` → `src/plugin-r23/RvtMcp.Plugin.R23.csproj`
- Rename: `src/plugin-r24/RvtMcp.Plugin.R24.csproj` → `src/plugin-r24/RvtMcp.Plugin.R24.csproj`
- Rename: `src/plugin-r25/RvtMcp.Plugin.R25.csproj` → `src/plugin-r25/RvtMcp.Plugin.R25.csproj`
- Rename: `src/plugin-r26/RvtMcp.Plugin.R26.csproj` → `src/plugin-r26/RvtMcp.Plugin.R26.csproj`
- Rename: `src/plugin-r27/RvtMcp.Plugin.R27.csproj` → `src/plugin-r27/RvtMcp.Plugin.R27.csproj`
- Rename: `tests/RvtMcp.Tests/` → `tests/RvtMcp.Tests/`
- Rename: `tests/RvtMcp.Tests/RvtMcp.Tests.csproj` → `tests/RvtMcp.Tests/RvtMcp.Tests.csproj`
- Modify: `src/RvtMcp.sln` — update Project lines + paths
- Modify: 6 plugin csproj — update `<RootNamespace>`, `<AssemblyName>`, `<DeployDir>` path, `<DeployFiles>` glob, copy-manifest path, `BimwrightSkipDeploy` → `RvtMcpSkipDeploy`, `<InternalsVisibleTo>`
- Modify: server csproj — update `<RootNamespace>`, `<AssemblyName>`, `<PackageId>`, `<ToolCommandName>`, `<InternalsVisibleTo>`, shared-Compile Link paths
- Modify: test csproj — update ProjectReference path

### Task 1.1: Rename solution file

- [ ] **Step 1: Git-rename the solution.**

Run: `git mv src/RvtMcp.sln src/RvtMcp.sln`

- [ ] **Step 2: Update Project lines inside the sln.**

Open `src/RvtMcp.sln` and edit lines 6, 8, 10, 12, 14, 16, 18, 22 (the 8 Project declarations). Replace these tokens:

| Old | New |
|---|---|
| `"RvtMcp.Server"` | `"RvtMcp.Server"` |
| `server\RvtMcp.Server.csproj` | `server\RvtMcp.Server.csproj` |
| `"RvtMcp.Plugin.R22"` | `"RvtMcp.Plugin.R22"` |
| `plugin-r22\RvtMcp.Plugin.R22.csproj` | `plugin-r22\RvtMcp.Plugin.R22.csproj` |
| `"RvtMcp.Plugin.R23"` | `"RvtMcp.Plugin.R23"` |
| `plugin-r23\RvtMcp.Plugin.R23.csproj` | `plugin-r23\RvtMcp.Plugin.R23.csproj` |
| `"RvtMcp.Plugin.R24"` | `"RvtMcp.Plugin.R24"` |
| `plugin-r24\RvtMcp.Plugin.R24.csproj` | `plugin-r24\RvtMcp.Plugin.R24.csproj` |
| `"RvtMcp.Plugin.R25"` | `"RvtMcp.Plugin.R25"` |
| `plugin-r25\RvtMcp.Plugin.R25.csproj` | `plugin-r25\RvtMcp.Plugin.R25.csproj` |
| `"RvtMcp.Plugin.R26"` | `"RvtMcp.Plugin.R26"` |
| `plugin-r26\RvtMcp.Plugin.R26.csproj` | `plugin-r26\RvtMcp.Plugin.R26.csproj` |
| `"RvtMcp.Plugin.R27"` | `"RvtMcp.Plugin.R27"` |
| `plugin-r27\RvtMcp.Plugin.R27.csproj` | `plugin-r27\RvtMcp.Plugin.R27.csproj` |
| `"RvtMcp.Tests"` | `"RvtMcp.Tests"` |
| `..\tests\RvtMcp.Tests\RvtMcp.Tests.csproj` | `..\tests\RvtMcp.Tests\RvtMcp.Tests.csproj` |

**Do not touch project GUIDs.**

### Task 1.2: Rename + edit server csproj

- [ ] **Step 1: Git-rename.**

Run: `git mv src/server/RvtMcp.Server.csproj src/server/RvtMcp.Server.csproj`

- [ ] **Step 2: Edit RootNamespace and AssemblyName.**

In `src/server/RvtMcp.Server.csproj`:
```xml
<RootNamespace>RvtMcp.Server</RootNamespace>
<AssemblyName>RvtMcp.Server</AssemblyName>
```
becomes
```xml
<RootNamespace>RvtMcp.Server</RootNamespace>
<AssemblyName>RvtMcp.Server</AssemblyName>
```

- [ ] **Step 3: Edit NuGet packaging properties.**

```xml
<ToolCommandName>rvt-mcp</ToolCommandName>
<PackageId>RvtMcp.Server</PackageId>
```
becomes
```xml
<ToolCommandName>rvt-mcp</ToolCommandName>
<PackageId>RvtMcp.Server</PackageId>
```

Keep `<Authors>`, `<Copyright>`, `<Description>`, `<PackageProjectUrl>`, `<RepositoryUrl>`, `<PackageLicenseExpression>`, `<PackageTags>` unchanged.

- [ ] **Step 4: Edit `<Compile Include>` Link paths for shared files.**

```xml
<Compile Include="..\shared\Config\RvtMcpConfig.cs" Link="Shared\RvtMcpConfig.cs" />
```
becomes
```xml
<Compile Include="..\shared\Config\RvtMcpConfig.cs" Link="Shared\RvtMcpConfig.cs" />
```

(The file `RvtMcpConfig.cs` itself is renamed in Wave 2 Task 2.3. We update the csproj include here so Wave 2 just needs to rename the file on disk.)

**IMPORTANT — temporary inconsistency:** between this task and Task 2.3, `RvtMcp.Server.csproj` references `..\shared\Config\RvtMcpConfig.cs` but the file is still at `..\shared\Config\RvtMcpConfig.cs`. **Build will fail in Wave 1 verify if this is done before renaming the file.** Therefore: defer this `<Compile>` Link path edit to Wave 2 Task 2.3 Step 3. Strike this step in Wave 1.

**Revised Step 4:** Skip. The `<Compile Include>` shared-file paths get updated in Wave 2 Task 2.3 alongside the file rename.

- [ ] **Step 5: Update `<InternalsVisibleTo>`.**

```xml
<InternalsVisibleTo Include="RvtMcp.Tests" />
```
becomes
```xml
<InternalsVisibleTo Include="RvtMcp.Tests" />
```

### Task 1.3: Rename + edit plugin csproj × 6

For **each year XX in {22, 23, 24, 25, 26, 27}**, do all of:

- [ ] **Step 1: Git-rename csproj.**

Run: `git mv src/plugin-rXX/RvtMcp.Plugin.RXX.csproj src/plugin-rXX/RvtMcp.Plugin.RXX.csproj`

- [ ] **Step 2: Edit `<RootNamespace>` and `<AssemblyName>`.**

```xml
<RootNamespace>RvtMcp.Plugin</RootNamespace>
<AssemblyName>RvtMcp.Plugin</AssemblyName>
```
becomes
```xml
<RootNamespace>RvtMcp.Plugin</RootNamespace>
<AssemblyName>RvtMcp.Plugin</AssemblyName>
```

- [ ] **Step 3: Edit `<DeployDir>` path in `<Target Name="Deploy">`.**

```xml
<DeployDir>$(APPDATA)\Autodesk\Revit\Addins\20XX\Bimwright\</DeployDir>
```
becomes
```xml
<DeployDir>$(APPDATA)\Autodesk\Revit\Addins\20XX\RvtMcp\</DeployDir>
```

- [ ] **Step 4: Edit `<DeployFiles Include>` glob.**

```xml
<DeployFiles Include="$(OutputPath)Bimwright.*.dll;..."/>
```
becomes
```xml
<DeployFiles Include="$(OutputPath)RvtMcp.*.dll;..."/>
```

(Keep the rest of the glob: `Newtonsoft.Json.dll`, `Microsoft.Data.Sqlite.dll`, etc.)

- [ ] **Step 5: Edit the `<Copy SourceFiles="Bimwright.RXX.addin">` line.**

```xml
<Copy SourceFiles="Bimwright.RXX.addin" DestinationFolder="$(APPDATA)\Autodesk\Revit\Addins\20XX\" .../>
```
becomes
```xml
<Copy SourceFiles="RvtMcp.RXX.addin" DestinationFolder="$(APPDATA)\Autodesk\Revit\Addins\20XX\" .../>
```

(`.addin` file is renamed in Wave 3.)

- [ ] **Step 6: Edit the `Condition` attribute on `<Target Name="Deploy">`.**

```xml
<Target Name="Deploy" AfterTargets="Build" Condition="'$(Configuration)' != '' and '$(BimwrightSkipDeploy)' != 'true'">
```
becomes
```xml
<Target Name="Deploy" AfterTargets="Build" Condition="'$(Configuration)' != '' and '$(RvtMcpSkipDeploy)' != 'true'">
```

**IMPORTANT — temporary inconsistency in Wave 1 verify:**
- The `<Copy SourceFiles="RvtMcp.RXX.addin"...>` line references a file that hasn't been renamed yet (still on disk as `Bimwright.RXX.addin`). The `Deploy` target runs `AfterTargets="Build"` — so a full build would attempt to copy a missing file.
- **Workaround for Wave 1 verify:** Pass `/p:RvtMcpSkipDeploy=true` to `dotnet build` so the Deploy target is skipped. This is what CI already does (see `build.yml` line 57 — but with the old flag name; CI is updated in Wave 4).

### Task 1.4: Rename + edit test project

- [ ] **Step 1: Git-rename folder + csproj.**

Run:
```
git mv tests/RvtMcp.Tests tests/RvtMcp.Tests
git mv tests/RvtMcp.Tests/RvtMcp.Tests.csproj tests/RvtMcp.Tests/RvtMcp.Tests.csproj
```

- [ ] **Step 2: Edit `<ProjectReference Include>` in test csproj.**

```xml
<ProjectReference Include="..\..\src\server\RvtMcp.Server.csproj" />
```
becomes
```xml
<ProjectReference Include="..\..\src\server\RvtMcp.Server.csproj" />
```

- [ ] **Step 3: Leave `<Compile Include="..\..\src\shared\...">` paths alone.**

These reference shared source files by path — paths are unchanged; only file CONTENTS (namespace declarations) change in Wave 2.

### Task 1.5: Verify Wave 1

- [ ] **Step 1: Build with deploy skipped.**

Run: `dotnet build src/RvtMcp.sln -c Debug /p:RvtMcpSkipDeploy=true`
Expected: Build **succeeds**. The `.cs` files still declare `namespace RvtMcp.*` — that's fine; MSBuild only cares about file paths and project names in this wave.

- [ ] **Step 2: Confirm produced binaries have new names.**

Check that `src/server/bin/Debug/net8.0/RvtMcp.Server.exe` exists (not `RvtMcp.Server.exe`).
Check that `src/plugin-r22/bin/Debug/net48/RvtMcp.Plugin.dll` exists.

If old-named binaries remain alongside, run `dotnet clean src/RvtMcp.sln` then re-build. Old-named DLLs from prior builds are stale and must not ship in artifacts.

- [ ] **Step 3: Run tests.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug`
Expected: Same passing count as Wave 0 baseline. Tests still compile because they reference symbols in `RvtMcp.*` namespaces which haven't been renamed yet — and the test code itself also still says `RvtMcp.Tests` in its `namespace` declaration.

- [ ] **Step 4: Commit.**

```bash
git add src/RvtMcp.sln \
        src/server/RvtMcp.Server.csproj \
        src/plugin-r22/RvtMcp.Plugin.R22.csproj \
        src/plugin-r23/RvtMcp.Plugin.R23.csproj \
        src/plugin-r24/RvtMcp.Plugin.R24.csproj \
        src/plugin-r25/RvtMcp.Plugin.R25.csproj \
        src/plugin-r26/RvtMcp.Plugin.R26.csproj \
        src/plugin-r27/RvtMcp.Plugin.R27.csproj \
        tests/RvtMcp.Tests/RvtMcp.Tests.csproj
git rm src/RvtMcp.sln \
       src/server/RvtMcp.Server.csproj \
       src/plugin-r22/RvtMcp.Plugin.R22.csproj \
       src/plugin-r23/RvtMcp.Plugin.R23.csproj \
       src/plugin-r24/RvtMcp.Plugin.R24.csproj \
       src/plugin-r25/RvtMcp.Plugin.R25.csproj \
       src/plugin-r26/RvtMcp.Plugin.R26.csproj \
       src/plugin-r27/RvtMcp.Plugin.R27.csproj
git commit -m "refactor(build): rename solution + csproj files to RvtMcp"
```

(`git mv` already staged most renames; the explicit `git add`/`git rm` is defensive.)

---

## Wave 2: Namespace Rename in C# Source

**Goal:** Update every `.cs` file's `namespace` declarations, `using` statements, and type references that mention `RvtMcp.*` to `RvtMcp.*`. Rename the class `RvtMcpConfig` → `RvtMcpConfig` and its file. Build must produce identical functional behavior.

**Strategy:** Use a scripted bulk replace, then `dotnet build` to catch anything missed.

### Task 2.1: Bulk rename namespace declarations

**Files:** All `.cs` files in `src/`, `tests/`.

- [ ] **Step 1: Run scripted bulk replace (PowerShell).**

```powershell
$root = "D:/Projects/bimwright/rvt-mcp"
$files = Get-ChildItem -Path "$root/src","$root/tests" -Recurse -Include *.cs -File
foreach ($f in $files) {
    $content = Get-Content -Raw -Path $f.FullName -Encoding UTF8
    $orig = $content
    # Order matters: longest-first to avoid double-replacement
    $content = $content.Replace("RvtMcp.Server", "RvtMcp.Server")
    $content = $content.Replace("RvtMcp.Plugin", "RvtMcp.Plugin")
    $content = $content.Replace("RvtMcp.Tests", "RvtMcp.Tests")
    $content = $content.Replace("RvtMcp", "RvtMcp")  # catches any remaining (e.g. assembly attribute names)
    if ($content -ne $orig) {
        Set-Content -Path $f.FullName -Value $content -Encoding UTF8 -NoNewline
        Write-Host "updated: $($f.FullName)"
    }
}
```

Expected: ~100-150 `.cs` files updated.

- [ ] **Step 2: Verify no `RvtMcp` literal remains in `.cs` files except inside string literals.**

Run:
```powershell
Get-ChildItem -Path src,tests -Recurse -Include *.cs -File |
  Select-String -Pattern 'Bimwright\.Rvt' |
  Format-List
```

Expected: empty output, OR only matches inside `"…string literals…"` that intentionally reference legacy paths (Wave 6 migration code may need these). Triage each match. If any is a code identifier (not a string), revisit Step 1.

### Task 2.2: Rename `RvtMcpConfig` class + file

**Files:**
- Rename: `src/shared/Config/RvtMcpConfig.cs` → `src/shared/Config/RvtMcpConfig.cs`
- Rename: `tests/RvtMcp.Tests/RvtMcpConfigTests.cs` → `tests/RvtMcp.Tests/RvtMcpConfigTests.cs`
- Rename: `tests/RvtMcp.Tests/RvtMcpConfigAdaptiveBakeTests.cs` → `tests/RvtMcp.Tests/RvtMcpConfigAdaptiveBakeTests.cs`

- [ ] **Step 1: Git-rename files.**

```
git mv src/shared/Config/RvtMcpConfig.cs src/shared/Config/RvtMcpConfig.cs
git mv tests/RvtMcp.Tests/RvtMcpConfigTests.cs tests/RvtMcp.Tests/RvtMcpConfigTests.cs
git mv tests/RvtMcp.Tests/RvtMcpConfigAdaptiveBakeTests.cs tests/RvtMcp.Tests/RvtMcpConfigAdaptiveBakeTests.cs
```

- [ ] **Step 2: Rename the class identifier.**

In all `.cs` files under `src/` and `tests/`, replace the class identifiers `RvtMcpConfig`, `RvtMcpConfigTests`, and `RvtMcpConfigAdaptiveBakeTests` with their `RvtMcp` equivalents. PowerShell:

```powershell
$root = "D:/Projects/bimwright/rvt-mcp"
$files = Get-ChildItem -Path "$root/src","$root/tests" -Recurse -Include *.cs -File
foreach ($f in $files) {
    $content = Get-Content -Raw -Path $f.FullName -Encoding UTF8
    $orig = $content
    # Use word boundary (\b) so we don't do partial replacements
    $content = $content -replace '\bRvtMcpConfig\b', 'RvtMcpConfig'
    $content = $content -replace '\bRvtMcpConfigTests\b', 'RvtMcpConfigTests'
    $content = $content -replace '\bRvtMcpConfigAdaptiveBakeTests\b', 'RvtMcpConfigAdaptiveBakeTests'
    if ($content -ne $orig) {
        Set-Content -Path $f.FullName -Value $content -Encoding UTF8 -NoNewline
        Write-Host "updated: $($f.FullName)"
    }
}
```


- [ ] **Step 3: Update `RvtMcp.Server.csproj` `<Compile Include>` Link paths** (the deferred edit from Wave 1 Task 1.2 Step 4).

In `src/server/RvtMcp.Server.csproj`:

```xml
<Compile Include="..\shared\Config\RvtMcpConfig.cs" Link="Shared\RvtMcpConfig.cs" />
```
becomes
```xml
<Compile Include="..\shared\Config\RvtMcpConfig.cs" Link="Shared\RvtMcpConfig.cs" />
```

### Task 2.3: Verify Wave 2

- [ ] **Step 1: Build.**

Run: `dotnet build src/RvtMcp.sln -c Debug /p:RvtMcpSkipDeploy=true`
Expected: **Succeeds.** If `error CS0234: The type or namespace name 'X' does not exist in the namespace 'RvtMcp.…'` appears, the bulk replace missed a spot — grep for the symbol and fix.

- [ ] **Step 2: Test.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug`
Expected: Same passing count as baseline.

- [ ] **Step 3: Commit.**

```bash
git add -A src/ tests/
git commit -m "refactor: rename RvtMcp.* namespaces and RvtMcpConfig class to RvtMcp"
```

---

## Wave 3: Addin Manifests, Deploy & Discovery Paths

**Goal:** Rename the `.addin` files and update every string literal that hard-codes `"Bimwright"` for a filesystem path. Also update the install/uninstall scripts to use new paths.

**⚠ ATOMICITY:** Discovery file path is referenced by both plugin (writer) and server (reader). They MUST change together. After this wave, an old plugin + new server (or vice versa) cannot talk to each other. Document this in CHANGELOG (Wave 6).

### Task 3.1: Rename `.addin` files

For **each year XX in {22, 23, 24, 25, 26, 27}**:

- [ ] **Step 1: Git-rename.**

```
git mv src/plugin-rXX/Bimwright.RXX.addin src/plugin-rXX/RvtMcp.RXX.addin
```

- [ ] **Step 2: Edit the XML.**

In `src/plugin-rXX/RvtMcp.RXX.addin`:

```xml
<Name>Bimwright</Name>
<Assembly>Bimwright\RvtMcp.Plugin.dll</Assembly>
<FullClassName>RvtMcp.Plugin.App</FullClassName>
<AddInId>{<existing-guid-unchanged>}</AddInId>
<VendorId>bimwright</VendorId>
<VendorDescription>Revit MCP Gateway</VendorDescription>
```
becomes
```xml
<Name>RvtMcp</Name>
<Assembly>RvtMcp\RvtMcp.Plugin.dll</Assembly>
<FullClassName>RvtMcp.Plugin.App</FullClassName>
<AddInId>{<existing-guid-unchanged>}</AddInId>
<VendorId>bimwright</VendorId>     <!-- OR rvtmcp — see Task 0.1 decision -->
<VendorDescription>Revit MCP Gateway</VendorDescription>
```

**KEEP `<AddInId>` GUID** so Revit treats the new install as an upgrade of the existing addin.

### Task 3.2: Update string literals for discovery folder

**Files (15 .cs files identified in Pre-flight scan):**
- `src/shared/Transport/TcpTransportServer.cs:265`
- `src/shared/Transport/PipeTransportServer.cs:258`
- `src/shared/Commands/CopyConnectionInfoCommand.cs:22`
- `src/shared/Security/AuthToken.cs:53,90`
- `src/shared/ToolBaker/ToolCompiler.cs:185`
- `src/shared/Logging/McpLogger.cs:20`
- `src/shared/ToolBaker/BakedToolRegistry.cs:41`
- `src/plugin-r22/App.cs:168`, `r23/App.cs:168`, `r24/App.cs:168`, `r25/App.cs:155`, `r26/App.cs:155`, `r27/App.cs:155`
- `src/shared/Handlers/CaptureViewImageHandler.cs:92`
- `src/server/AuthToken.cs:26,57`
- `src/server/Memory/JournalLogger.cs:20`
- `src/server/Bake/BakePaths.cs:18`

- [ ] **Step 1: Bulk-replace "Bimwright" string literals in C# source files.**

Run (PowerShell):
```powershell
$root = "D:/Projects/bimwright/rvt-mcp"
$files = Get-ChildItem -Path "$root/src" -Recurse -Include *.cs -File
foreach ($f in $files) {
    $content = Get-Content -Raw -Path $f.FullName -Encoding UTF8
    $orig = $content
    
    # Core path folder
    $content = $content.Replace('"Bimwright"', '"RvtMcp"')
    
    # Threads, Named Pipes, and Event Handlers
    $content = $content.Replace('"Bimwright.McpEventHandler"', '"RvtMcp.McpEventHandler"')
    $content = $content.Replace('"Bimwright.PipeTransportServer"', '"RvtMcp.PipeTransportServer"')
    $content = $content.Replace('"Bimwright.TcpTransportServer"', '"RvtMcp.TcpTransportServer"')
    $content = $content.Replace('"Bimwright.ResponseReader"', '"RvtMcp.ResponseReader"')
    $content = $content.Replace('"Bimwright-"', '"RvtMcp-"')
    
    # Logging Prefixes (Stdout / Console / Debug logs)
    $content = $content.Replace('"[Bimwright] "', '"[RvtMcp] "')
    $content = $content.Replace('"[Bimwright] Connection closed."', '"[RvtMcp] Connection closed."')
    
    # Revit Ribbon / UI Brand and Transaction names
    $content = $content.Replace('"Bimwright Clash Review"', '"RvtMcp Clash Review"')
    $content = $content.Replace('"Bimwright Room Finish Schedule"', '"RvtMcp Room Finish Schedule"')
    $content = $content.Replace('"Bimwright Workflow"', '"RvtMcp Workflow"')
    $content = $content.Replace('"Bimwright: room documentation"', '"RvtMcp: room documentation"')
    $content = $content.Replace('"Bimwright: workflow room documentation"', '"RvtMcp: workflow room documentation"')
    $content = $content.Replace('"Bimwright: workflow naming normalization"', '"RvtMcp: workflow naming normalization"')
    $content = $content.Replace('"Bimwright: workflow sheet set"', '"RvtMcp: workflow sheet set"')
    $content = $content.Replace('"Bimwright: create workflow sheet"', '"RvtMcp: create workflow sheet"')
    $content = $content.Replace('"Bimwright: workflow view cleanup"', '"RvtMcp: workflow view cleanup"')
    $content = $content.Replace('"Bimwright: workflow data roundtrip import"', '"RvtMcp: workflow data roundtrip import"')

    if ($content -ne $orig) {
        Set-Content -Path $f.FullName -Value $content -Encoding UTF8 -NoNewline
        Write-Host "updated: $($f.FullName)"
    }
}
```


- [ ] **Step 2: Special case — `Program.cs` user-facing strings.**

`src/server/Program.cs` has three strings that say `"%LOCALAPPDATA%\\Bimwright\\"` (lines 181, 210, 364) shown in `--help` output and error messages. Replace each:

| Line | Old (excerpt) | New (excerpt) |
|---|---|---|
| 181 | `"%LOCALAPPDATA%\\Bimwright\\."` | `"%LOCALAPPDATA%\\RvtMcp\\."` |
| 210 | `"%LOCALAPPDATA%\\Bimwright\\bimwright.config.json"` | `"%LOCALAPPDATA%\\RvtMcp\\rvtmcp.config.json"` |
| 364 | `"…%LOCALAPPDATA%\\Bimwright\\"` | `"…%LOCALAPPDATA%\\RvtMcp\\"` |
| 1113 | `"…inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\captures\\."` | `"…inside %TEMP% or %LOCALAPPDATA%\\RvtMcp\\captures\\."` |

These appear inside `[McpServerTool]` `Description` strings and `--help` text. Same bulk PowerShell replace would catch them if we replace `"Bimwright\\"` and `"Bimwright."` patterns. Verify by grepping after Step 1.

- [ ] **Step 3: Special case — `RvtMcpConfig.cs` (now `RvtMcpConfig.cs`).**

Already covered by Step 1 (the `"Bimwright"` literal in `Path.Combine(..., "Bimwright", ...)`). Also update the config filename `bimwright.config.json` if Program.cs references it as a CLI argument default — `RvtMcpConfig.Load()` may have its own default path. Check `src/shared/Config/RvtMcpConfig.cs:65-66` and rename:

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Bimwright",       // → "RvtMcp"
    "bimwright.config.json")   // → "rvtmcp.config.json"
```

- [ ] **Step 4: Update `CaptureViewImageHandler.cs` description string.**

Line 13 + 98 contain `%LOCALAPPDATA%\RvtMcp\captures\` — same find/replace as above.

### Task 3.3: Update `install.ps1`

**File:** `scripts/install.ps1`

This script has ~50 occurrences of `Bimwright` / `bimwright` / `rvt-mcp`. Most are user-facing names that need to change. A few are inside legacy-detection regex that must KEEP `rvt-mcp-r\d{2}` so we can detect old entries.

- [ ] **Step 1: Replace function names.**

| Old | New |
|---|---|
| `Get-BimwrightClientTargets` | `Get-RvtMcpClientTargets` |
| `Install-BimwrightServer` | `Install-RvtMcpServer` |
| `Remove-LegacyBimwrightEntries` | `Remove-LegacyBimwrightEntries` (KEEP — this function removes the old name; rename semantically inappropriate) |

- [ ] **Step 2: Replace path defaults.**

| Old | New |
|---|---|
| `'Bimwright\rvt\server\{0}'` (line 90) | `'RvtMcp\server\{0}'` |
| `Join-Path $addinsRoot 'Bimwright'` (line 458) | `Join-Path $addinsRoot 'RvtMcp'` |
| `Find-ServerSourceExe` preferred = `'rvt-mcp.exe'` | `'rvt-mcp.exe'` |
| `Find-ServerSourceExe` fallback = `'RvtMcp.Server.exe'` | `'RvtMcp.Server.exe'` |
| `$addinFile = "Bimwright.R$yearTwo.addin"` (line 456) | `"RvtMcp.R$yearTwo.addin"` |
| `RvtMcp.Plugin.R{0}.zip` (line 490) | `RvtMcp.Plugin.R{0}.zip` |

- [ ] **Step 3: Replace client wire entry names.**

In `Get-RvtMcpClientTargets`:
```powershell
Name = 'rvt-mcp'
```
becomes
```powershell
Name = 'rvt-mcp'
```

And in the `Find-ServerSourceExe` last-resort PATH check (line 548-550):
```powershell
if (Get-Command rvt-mcp -ErrorAction SilentlyContinue) {
    $serverCommand = 'rvt-mcp'
}
```
becomes
```powershell
if (Get-Command rvt-mcp -ErrorAction SilentlyContinue) {
    $serverCommand = 'rvt-mcp'
}
```

- [ ] **Step 4: Update legacy-detection regex.**

`Remove-LegacyBimwrightEntries` (line 215) — KEEP regex `'^rvt-mcp-r\d{2}$'` because it deletes legacy fan-out entries. ADD a second regex for the previous-but-not-yet-legacy entry name:

```powershell
function Remove-LegacyBimwrightEntries {
    param([Parameter(Mandatory = $true)][hashtable]$Map)
    $keys = @($Map.Keys | Where-Object {
        $_ -match '^rvt-mcp-r\d{2}$' -or $_ -eq 'rvt-mcp'
    })
    foreach ($k in $keys) { $Map.Remove($k) | Out-Null }
    return $keys.Count
}
```

The codex regex (line 319):
```powershell
$legacyPattern = '(?ms)^\[mcp_servers\.rvt-mcp-r\d{2}\].*?(?=^\[|\z)'
```
becomes
```powershell
$legacyPattern = '(?ms)^\[mcp_servers\.rvt-mcp(?:-r\d{2})?\].*?(?=^\[|\z)'
```

- [ ] **Step 5: Update backup-file suffix.**

```powershell
$bak = "$Path.bimwright.bak"
```
becomes
```powershell
$bak = "$Path.rvtmcp.bak"
```

Both occurrences inside `Write-ConfigAtomic`.

- [ ] **Step 6: Update log/Write-Host strings.**

Replace user-facing strings:
- `'Install self-contained RvtMcp server'` → `'Install self-contained RvtMcp server'`
- `'Upsert rvt-mcp entry'` → `'Upsert rvt-mcp entry'`
- `'Upsert [mcp_servers.rvt-mcp] block'` → `'Upsert [mcp_servers.rvt-mcp] block'`
- `'Upsert mcpServers.rvt-mcp entry'` → `'Upsert mcpServers.rvt-mcp entry'`
- `'[server] installed'`, `'[opencode] wired … rvt-mcp'`, etc. — replace `rvt-mcp` → `rvt-mcp` in all status/log strings
- `.SYNOPSIS` line `'Install or uninstall Bimwright Revit client components.'` → `'Install or uninstall RvtMcp (Revit MCP) client components.'`

### Task 3.4: Update other scripts

**Files:**
- `scripts/uninstall-all.ps1`
- `scripts/package-client-setup.ps1`
- `scripts/stage-plugin-zip.ps1`

- [ ] **Step 1: Each script — find/replace project zip names, setup files, server executable names, addin names, paths, and MSBuild SkipDeploy flag.**

Run PowerShell per file or use a single bulk pass:

```powershell
$scripts = @(
    "scripts/install.ps1",
    "scripts/uninstall-all.ps1",
    "scripts/package-client-setup.ps1",
    "scripts/stage-plugin-zip.ps1"
)
foreach ($s in $scripts) {
    $p = "D:/Projects/bimwright/rvt-mcp/$s"
    $c = Get-Content -Raw -Path $p
    $c = $c.Replace('RvtMcp.Plugin', 'RvtMcp.Plugin')
    $c = $c.Replace('RvtMcp.Setup', 'RvtMcp.Setup')
    $c = $c.Replace('RvtMcp.Server', 'RvtMcp.Server')
    $c = $c.Replace('Bimwright.R$', 'RvtMcp.R$')   # for $year/$yearTwo interpolation
    $c = $c.Replace('"Bimwright"', '"RvtMcp"')
    $c = $c.Replace("'Bimwright'", "'RvtMcp'")
    $c = $c.Replace('rvt-mcp.exe', 'rvt-mcp.exe')
    $c = $c.Replace('rvt-mcp', 'rvt-mcp')   # client entry name + tool name (after .exe handled above)
    $c = $c.Replace('RvtMcp', 'RvtMcp')
    $c = $c.Replace('RvtMcp', 'RvtMcp')
    $c = $c.Replace('BimwrightSkipDeploy', 'RvtMcpSkipDeploy') # Skip deploy flag in package script
    Set-Content -Path $p -Value $c -Encoding UTF8 -NoNewline
}
```


**WARNING:** This script does NOT touch `install.ps1`'s legacy-detection regex — those were already correctly handled in Task 3.3 Step 4. Re-run the script unchanged is safe (replaces are idempotent on the new strings).

- [ ] **Step 2: Sanity check.**

```powershell
Select-String -Path scripts/*.ps1 -Pattern 'Bimwright'
```

Expected output: only matches inside legacy-detection regex (`rvt-mcp-r\d{2}`, `rvt-mcp`) and the `Remove-LegacyBimwrightEntries` function name. Triage each remaining hit.

### Task 3.5: Verify Wave 3

- [ ] **Step 1: Build with deploy enabled (Revit must be closed).**

Run: `dotnet build src/RvtMcp.sln -c Debug`
Expected: Build **succeeds**, including the `Deploy` target. New plugin DLLs deploy to `%APPDATA%\Autodesk\Revit\Addins\2022\RvtMcp\RvtMcp.Plugin.dll` and the `RvtMcp.R22.addin` manifest is copied alongside.

**If Revit is open:** build will succeed but Deploy step may fail with "file locked." Use `/p:RvtMcpSkipDeploy=true` to skip and verify build-only.

- [ ] **Step 2: Confirm deploy paths.**

Check that `%APPDATA%\Autodesk\Revit\Addins\2022\RvtMcp\` exists with `RvtMcp.Plugin.dll`, and `Bimwright\` legacy folder is **untouched** (Wave 6 cleans it).

- [ ] **Step 3: Tests.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug`
Expected: Same passing count as baseline.

- [ ] **Step 4: Commit.**

```bash
git add -A src/plugin-r22 src/plugin-r23 src/plugin-r24 src/plugin-r25 src/plugin-r26 src/plugin-r27 src/server src/shared scripts/
git commit -m "refactor: rename addin manifests, deploy folder, discovery paths, install scripts"
```

---

## Wave 4: Metadata Files (server.json, smithery.yaml, GH Actions, .mcp.json.example)

**Goal:** Update files that downstream tooling reads.

### Task 4.1: `server.json`

**File:** `server.json`

- [ ] **Step 1: Update `title`, `identifier`, and `version`.**

In `server.json`:

```json
  "title": "Bimwright",
  ...
  "packages": [
    {
      "registryType": "nuget",
      "identifier": "RvtMcp.Server",
      "version": "0.4.0",
```
becomes
```json
  "title": "RvtMcp",
  ...
  "packages": [
    {
      "registryType": "nuget",
      "identifier": "RvtMcp.Server",
      "version": "0.4.0",
```

Keep `"name": "io.github.bimwright/rvt-mcp"` (the GitHub org/repo segment).


### Task 4.2: `smithery.yaml`

**File:** `smithery.yaml`

- [ ] **Step 1: Update comments, install command, and tool name.**

In `smithery.yaml`:

```yaml
# Smithery manifest — exposes bimwright to the Smithery.ai MCP directory.
# ...
# bimwright ships as a .NET global tool on NuGet (RvtMcp.Server).
# ...
#   dotnet tool install -g RvtMcp.Server
# ...
#   "command": "rvt-mcp",
```
becomes
```yaml
# Smithery manifest — exposes rvt-mcp to the Smithery.ai MCP directory.
# ...
# RvtMcp ships as a .NET global tool on NuGet (RvtMcp.Server).
# ...
#   dotnet tool install -g RvtMcp.Server
# ...
#   "command": "rvt-mcp",
```

Also update the `commandFunction` block:

```yaml
commandFunction: |
  (config) => ({
    "command": "rvt-mcp",
    "args": []
  })
```


### Task 4.3: `.mcp.json.example`

**File:** `.mcp.json.example`

- [ ] **Step 1: Show it first.**

Run: `Get-Content D:/Projects/bimwright/rvt-mcp/.mcp.json.example`

- [ ] **Step 2: Update any `rvt-mcp` entry names and exe paths shown in the example.**

Replace `"rvt-mcp"` → `"rvt-mcp"`, `RvtMcp.Server.exe` → `RvtMcp.Server.exe`, `rvt-mcp.exe` → `rvt-mcp.exe`.

### Task 4.4: GitHub Actions workflow

**File:** `.github/workflows/build.yml`

- [ ] **Step 1: Update csproj paths in matrix.**

Lines 20-41 — replace each `src/plugin-rXX/RvtMcp.Plugin.RXX.csproj` with `src/plugin-rXX/RvtMcp.Plugin.RXX.csproj`.

- [ ] **Step 2: Update server csproj path.**

Lines 78-84: `src/server/RvtMcp.Server.csproj` → `src/server/RvtMcp.Server.csproj`.

- [ ] **Step 3: Update test csproj path.**

Lines 130-133: `tests/RvtMcp.Tests/RvtMcp.Tests.csproj` → `tests/RvtMcp.Tests/RvtMcp.Tests.csproj`. Same for the `TestResults` path on line 140.

- [ ] **Step 4: Update artifact names.**

| Old | New |
|---|---|
| `bimwright-plugin-${{ matrix.revit }}` | `rvtmcp-plugin-${{ matrix.revit }}` |
| `bimwright-server-nupkg` | `rvtmcp-server-nupkg` |
| `rvt-mcp-client-setup` | `rvtmcp-client-setup` |

- [ ] **Step 5: Update MSBuild flag.**

`/p:BimwrightSkipDeploy=true` → `/p:RvtMcpSkipDeploy=true` (line 57).

- [ ] **Step 6: Update setup-zip artifact path.**

Line 115: `build/client-setup/RvtMcp.Setup-*-win-x64.zip` → `build/client-setup/RvtMcp.Setup-*-win-x64.zip`.

### Task 4.5: Verify Wave 4

- [ ] **Step 1: Re-build the solution to confirm csproj path updates didn't drift.**

Run: `dotnet build src/RvtMcp.sln -c Debug /p:RvtMcpSkipDeploy=true`
Expected: succeeds.

- [ ] **Step 2: Lint the workflow YAML.**

Run: `pwsh -Command "Get-Content .github/workflows/build.yml | ConvertFrom-Yaml"` if `powershell-yaml` module available. Otherwise visual inspect or use:

```bash
gh workflow view build.yml
```

Expected: no parse errors.

- [ ] **Step 3: Commit.**

```bash
git add server.json smithery.yaml .mcp.json.example .github/workflows/build.yml
git commit -m "chore: update server.json, smithery.yaml, GH Actions, .mcp.json.example for RvtMcp"
```

---

## Wave 5: Documentation

**Goal:** Update prose, README install instructions, and architectural docs.

**Strategy:** Mostly bulk find/replace, but README install commands are critical and need a careful hand pass. Each README is 400+ lines.

### Task 5.1: Bulk replace in non-README docs

**Files:** `ARCHITECTURE.md`, `CLAUDE.md`, `AGENTS.md`, `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` (if relevant), `docs/**/*.md` (except auto-generated runs).

- [ ] **Step 1: Bulk replace.**

```powershell
$docs = @(
    "ARCHITECTURE.md",
    "CLAUDE.md",
    "AGENTS.md",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "CHANGELOG.md"
)
$docs += (Get-ChildItem -Path D:/Projects/bimwright/rvt-mcp/docs -Recurse -Include *.md -File | Where-Object { $_.FullName -notmatch '\\runs\\' }).FullName

foreach ($d in $docs) {
    $p = if ([System.IO.Path]::IsPathRooted($d)) { $d } else { "D:/Projects/bimwright/rvt-mcp/$d" }
    if (-not (Test-Path $p)) { continue }
    $c = Get-Content -Raw -Path $p
    $orig = $c
    $c = $c.Replace('RvtMcp.Plugin', 'RvtMcp.Plugin')
    $c = $c.Replace('RvtMcp.Server', 'RvtMcp.Server')
    $c = $c.Replace('RvtMcp.Tests', 'RvtMcp.Tests')
    $c = $c.Replace('RvtMcp', 'RvtMcp')
    $c = $c.Replace('RvtMcpConfig', 'RvtMcpConfig')
    $c = $c.Replace('rvt-mcp.exe', 'rvt-mcp.exe')
    $c = $c.Replace('rvt-mcp', 'rvt-mcp')
    # %LOCALAPPDATA%\RvtMcp\ → %LOCALAPPDATA%\RvtMcp\
    $c = $c.Replace('%LOCALAPPDATA%\RvtMcp\', '%LOCALAPPDATA%\RvtMcp\')
    $c = $c.Replace('Addins\<year>\RvtMcp\', 'Addins\<year>\RvtMcp\')
    $c = $c.Replace('Addins/<year>/RvtMcp/', 'Addins/<year>/RvtMcp/')
    # Don't touch "Bimwright" as a brand name in prose (e.g., "RvtMcp-MCP" header, copyright)
    # — only path/identifier matches above are replaced.
    if ($c -ne $orig) {
        Set-Content -Path $p -Value $c -Encoding UTF8 -NoNewline
        Write-Host "updated: $p"
    }
}
```

- [ ] **Step 2: Per-decision: `BIMWRIGHT_*` env vars.**

If Task 0.1 decided env vars rename:
- Add replace: `$c = $c -replace 'BIMWRIGHT_', 'RVT_MCP_'` and document both as accepted in `RvtMcpConfig.Load()`.

If Task 0.1 decided keep env vars:
- Skip this step. Make a note in `docs/configuration.md` (if exists) or in README that env vars retain `BIMWRIGHT_` prefix for backwards compatibility.

### Task 5.2: README × 3 (en, vi, zh-CN)

**Files:** `README.md`, `README.vi.md`, `README.zh-CN.md`

These contain install commands, troubleshooting, and table-of-config that must match the code reality. After the bulk replace above, hand-check:

- [ ] **Step 1: Confirm install instructions are accurate.**

For each README, grep for these and verify the surrounding instruction text still reads correctly:
- `dotnet tool install` line — must say `RvtMcp.Server` (not `RvtMcp.Server`)
- `claude mcp add` examples — must use `rvt-mcp` as the server name
- Plugin install paths — `Addins\<year>\RvtMcp\`
- Discovery file path — `%LOCALAPPDATA%\RvtMcp\portR22.txt`
- Env var table — matches Task 5.1 decision

- [ ] **Step 2: Add migration note to README.md (English) only.**

Insert a `## Migration from RvtMcp.* (v0.3.0 and earlier)` section near the top of the install section. Body:

```markdown
## Migration from `RvtMcp.*` (v0.3.0 and earlier)

v0.4.0 renames the codebase from `RvtMcp.*` to `RvtMcp.*`. The GitHub repo (`bimwright/rvt-mcp`) and brand are unchanged; only file names, package IDs, and folder paths change.

If you have v0.3.0 or earlier installed:

1. **Close all running Revit instances.**
2. **Run the migration script:**
   ```powershell
   pwsh scripts/uninstall-old.ps1
   ```
   This removes:
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` (plugin DLLs)
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright.R<year>.addin` (manifests)
   - `%LOCALAPPDATA%\RvtMcp\rvt\server\` (server install root)

   It **preserves** `%LOCALAPPDATA%\RvtMcp\baked\`, `journal\`, `firm-profiles\`, and `*.log` files — these contain user data and are migrated to `%LOCALAPPDATA%\RvtMcp\` on first launch of v0.4.0.

3. **Install v0.4.0:**
   ```powershell
   dotnet tool update -g RvtMcp.Server --version 0.3.0   # ensure clean uninstall first
   dotnet tool uninstall -g RvtMcp.Server
   dotnet tool install -g RvtMcp.Server --version 0.4.0
   ```

4. **Re-wire your MCP client.** Old MCP entries `rvt-mcp-r22`..`rvt-mcp-r27` are auto-removed by `install.ps1`. The new entry is `rvt-mcp` (single, auto-detects Revit version).

The old NuGet package `RvtMcp.Server` is deprecated at 0.3.0 with a redirect note pointing to `RvtMcp.Server`.
```

Equivalent migration notes can be added to vi/zh READMEs if the user wants — defer that decision; **the English version is mandatory** because it's what NuGet users will hit first.

### Task 5.3: Verify Wave 5

- [ ] **Step 1: Spot-check 3 random doc files for correctness.**

Pick `ARCHITECTURE.md` line 112 area (env var mention), `docs/bake.md` (env var examples), and `README.md` (install instructions). Read with `Read` tool and confirm `BIMWRIGHT_` / `RvtMcp` references are handled per Task 0.1 decisions.

- [ ] **Step 2: Verify no stale `RvtMcp` survives in docs (except in CHANGELOG entries about v0.3.0 and earlier).**

```powershell
Select-String -Path README.md,README.vi.md,README.zh-CN.md,ARCHITECTURE.md,CLAUDE.md,AGENTS.md,SECURITY.md,CONTRIBUTING.md,CHANGELOG.md -Pattern 'Bimwright\.Rvt'
Select-String -Path docs -Recurse -Include *.md -Pattern 'Bimwright\.Rvt' | Where-Object { $_.Path -notmatch '\\runs\\' }
```

Expected: only matches inside CHANGELOG entries describing past releases (acceptable historical record).

- [ ] **Step 3: Commit.**

```bash
git add README.md README.vi.md README.zh-CN.md ARCHITECTURE.md CLAUDE.md AGENTS.md SECURITY.md CONTRIBUTING.md CHANGELOG.md docs/
git commit -m "docs: rename RvtMcp → RvtMcp in README, ARCHITECTURE, CLAUDE, docs/"
```

---

## Wave 6: Uninstall-old.ps1 + CHANGELOG + Data Migration

**Goal:** Replace the Wave 0 stub with a real `uninstall-old.ps1` and add a CHANGELOG entry describing the breaking change.

### Task 6.1: Fill in `scripts/uninstall-old.ps1`

**File:** `scripts/uninstall-old.ps1`

- [ ] **Step 1: Write the full script.**

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
  Remove legacy RvtMcp v0.3.0-and-earlier plugin installation before upgrading to RvtMcp v0.4.0+.

.DESCRIPTION
  Cleans up addin folders and manifests installed by the legacy `Bimwright` plugin layout.
  Preserves user data (baked tools, journal, firm profiles, debug logs) in `%LOCALAPPDATA%\RvtMcp\`
  for migration on next launch of v0.4.0+.

  Run this BEFORE installing v0.4.0 to avoid Revit loading both legacy + new plugins simultaneously.

.PARAMETER WhatIf
  Show what would be removed without actually removing anything.

.PARAMETER Years
  Specific Revit years to clean. Defaults to all detected years 2022-2027.

.EXAMPLE
  pwsh .\uninstall-old.ps1 -WhatIf
  pwsh .\uninstall-old.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int[]]$Years
)

$ErrorActionPreference = 'Stop'

function Get-InstalledRevitYears {
    $detected = @()
    $root = 'HKLM:\SOFTWARE\Autodesk\Revit'
    if (-not (Test-Path $root)) { return $detected }
    foreach ($year in 2022..2027) {
        if (Test-Path (Join-Path $root "$year")) { $detected += $year }
    }
    return $detected
}

if (-not $Years -or $Years.Count -eq 0) {
    $Years = Get-InstalledRevitYears
    if ($Years.Count -eq 0) {
        $Years = 2022..2027   # try all years even if registry detection fails
    }
}

$removed = @()
$skipped = @()

foreach ($year in $Years) {
    $yearTwo = "{0:D2}" -f ($year - 2000)
    $addinsRoot = Join-Path $env:APPDATA ("Autodesk\Revit\Addins\{0}" -f $year)
    $pluginDir = Join-Path $addinsRoot 'Bimwright'
    $addinPath = Join-Path $addinsRoot "Bimwright.R$yearTwo.addin"

    if (Test-Path $pluginDir) {
        if ($PSCmdlet.ShouldProcess($pluginDir, 'Remove legacy plugin folder')) {
            Remove-Item $pluginDir -Recurse -Force
        }
        $removed += "R$yearTwo plugin folder"
    } else {
        $skipped += "R$yearTwo plugin folder (not present)"
    }

    if (Test-Path $addinPath) {
        if ($PSCmdlet.ShouldProcess($addinPath, 'Remove legacy addin manifest')) {
            Remove-Item $addinPath -Force
        }
        $removed += "R$yearTwo addin manifest"
    } else {
        $skipped += "R$yearTwo addin manifest (not present)"
    }
}

# Server install root (separate from per-Revit-year plugin folders)
$legacyServerRoot = Join-Path $env:LOCALAPPDATA 'Bimwright\rvt\server'
if (Test-Path $legacyServerRoot) {
    if ($PSCmdlet.ShouldProcess($legacyServerRoot, 'Remove legacy server install root')) {
        Remove-Item $legacyServerRoot -Recurse -Force
    }
    $removed += "legacy server install root"
}

# Legacy global .NET tool — try to uninstall (best-effort).
try {
    $toolList = & dotnet tool list -g 2>&1
    if ($LASTEXITCODE -eq 0 -and $toolList -match 'bimwright\.rvt\.server') {
        if ($PSCmdlet.ShouldProcess('RvtMcp.Server', 'Uninstall legacy .NET global tool')) {
            & dotnet tool uninstall -g RvtMcp.Server
            if ($LASTEXITCODE -eq 0) { $removed += 'RvtMcp.Server (.NET tool)' }
        }
    }
} catch {
    Write-Warning "Could not uninstall .NET global tool RvtMcp.Server: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "=== uninstall-old.ps1 summary ==="
Write-Host ("Removed: {0}" -f ($(if ($removed.Count -gt 0) { $removed -join '; ' } else { 'nothing' })))
if ($skipped.Count -gt 0) {
    Write-Host ("Skipped: {0}" -f ($skipped -join '; '))
}
Write-Host ""
Write-Host "PRESERVED user data (will migrate on next RvtMcp launch):"
Write-Host "  %LOCALAPPDATA%\RvtMcp\baked\        (custom baked tools)"
Write-Host "  %LOCALAPPDATA%\RvtMcp\journal\      (session history)"
Write-Host "  %LOCALAPPDATA%\RvtMcp\firm-profiles\ (firm settings)"
Write-Host "  %LOCALAPPDATA%\RvtMcp\*.log         (debug logs)"
```

### Task 6.2: First-launch data migration in `RvtMcpConfig` / `App.cs`

**Status:** ✅ User locked in **Yes — auto-migrate**. Proceed directly to Step 2.

- [ ] **Step 2: Add migration helper.**

Create `src/shared/Infrastructure/LegacyDataMigration.cs`:

```csharp
using System;
using System.IO;

namespace RvtMcp.Plugin
{
    internal static class LegacyDataMigration
    {
        public static void MigrateOnce(string localAppDataPath = null)
        {
            var local = string.IsNullOrEmpty(localAppDataPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localAppDataPath;
            var legacy = Path.Combine(local, "Bimwright");
            var current = Path.Combine(local, "RvtMcp");
            var marker = Path.Combine(current, ".migrated-from-bimwright");

            if (!Directory.Exists(legacy)) return;
            if (File.Exists(marker)) return;

            Directory.CreateDirectory(current);

            foreach (var sub in new[] { "baked", "journal", "firm-profiles" })
            {
                var src = Path.Combine(legacy, sub);
                var dst = Path.Combine(current, sub);
                if (Directory.Exists(src) && !Directory.Exists(dst))
                {
                    CopyDirectory(src, dst);
                }
            }

            foreach (var log in Directory.Exists(legacy)
                ? Directory.GetFiles(legacy, "*.log")
                : Array.Empty<string>())
            {
                var dst = Path.Combine(current, Path.GetFileName(log));
                if (!File.Exists(dst)) File.Copy(log, dst);
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
```

Then in each of `src/plugin-rXX/App.cs` `OnStartup` method, near the top after `McpLogger.Initialize()`, add:

```csharp
LegacyDataMigration.MigrateOnce();
```

And in `src/server/Program.cs` `Main()` near the top, add equivalent call (the helper is included in server via `<Compile Include>` in Wave 7 if needed, OR duplicate the call into a server-side version).

- [ ] **Step 3: Test the migration helper.**

Create `tests/RvtMcp.Tests/LegacyDataMigrationTests.cs`:

```csharp
using System;
using System.IO;
using Xunit;
using RvtMcp.Plugin;

public class LegacyDataMigrationTests
{
    [Fact]
    public void MigrateOnce_CopiesBakedFolderAndCreatesMarker()
    {
        // Arrange: redirect LOCALAPPDATA to temp using parameterized helper.
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            var legacyBaked = Path.Combine(tempLocal, "Bimwright", "baked");
            Directory.CreateDirectory(legacyBaked);
            File.WriteAllText(Path.Combine(legacyBaked, "tool1.json"), "{}");

            // Act
            LegacyDataMigration.MigrateOnce(tempLocal);

            // Assert
            var newBaked = Path.Combine(tempLocal, "RvtMcp", "baked", "tool1.json");
            Assert.True(File.Exists(newBaked));
            var marker = Path.Combine(tempLocal, "RvtMcp", ".migrated-from-bimwright");
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }

    [Fact]
    public void MigrateOnce_IsIdempotent()
    {
        var tempLocal = Path.Combine(Path.GetTempPath(), "rvtmcp-migration-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempLocal);
        try
        {
            Directory.CreateDirectory(Path.Combine(tempLocal, "Bimwright", "baked"));
            File.WriteAllText(Path.Combine(tempLocal, "Bimwright", "baked", "tool1.json"), "{}");

            LegacyDataMigration.MigrateOnce(tempLocal);
            var firstMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "RvtMcp", "baked", "tool1.json"));

            LegacyDataMigration.MigrateOnce(tempLocal);   // second call — should be no-op
            var secondMtime = File.GetLastWriteTimeUtc(Path.Combine(tempLocal, "RvtMcp", "baked", "tool1.json"));

            Assert.Equal(firstMtime, secondMtime);
        }
        finally
        {
            Directory.Delete(tempLocal, recursive: true);
        }
    }
}
```

Note: `LegacyDataMigration` uses `Environment.GetFolderPath(...LocalApplicationData)` by default, but accepts a parameterized override to allow testing without Windows system directory query interference.

- [ ] **Step 4: Build + test.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug`
Expected: both new tests pass.

### Task 6.3: Update CHANGELOG

**File:** `CHANGELOG.md`

- [ ] **Step 1: Add v0.4.0 entry at the top.**

Open `CHANGELOG.md` and insert a new entry above the most recent prior entry:

```markdown
## v0.4.0 — Wave 17: Rename to RvtMcp (BREAKING)

### Breaking changes

- **Package renamed:** `RvtMcp.Server` → `RvtMcp.Server`. The old NuGet package is deprecated at v0.3.0. New users install with `dotnet tool install -g RvtMcp.Server`.
- **Tool command renamed:** `rvt-mcp` → `rvt-mcp`. After install, the binary on PATH is `rvt-mcp`.
- **Plugin folder renamed:** `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` → `…\RvtMcp\`. Run `scripts/uninstall-old.ps1` to clean the legacy folder before/after upgrade.
- **Addin manifest renamed:** `Bimwright.R<year>.addin` → `RvtMcp.R<year>.addin`. AddInId GUIDs are preserved so Revit treats this as an upgrade, not a new addin.
- **Discovery folder renamed:** `%LOCALAPPDATA%\RvtMcp\` → `%LOCALAPPDATA%\RvtMcp\`. User data (baked tools, journal, firm-profiles, logs) is auto-migrated on first launch.
- **MCP client entry renamed:** `rvt-mcp-r22`..`r27` (or `rvt-mcp`) → `rvt-mcp`. `install.ps1` removes legacy entries from opencode/codex/claude configs automatically.

### Migration

See README → "Migration from RvtMcp.* (v0.3.0 and earlier)" for full upgrade steps.

### Unchanged

- GitHub repo path: `github.com/bimwright/rvt-mcp`.
- Brand and author metadata: `Bimwright` remains in csproj `<Authors>`, `<Copyright>`, and `<Description>` text.
- AddInId GUIDs in `.addin` files.
- All API surface, MCP tool names, and behavior.
```

### Task 6.4: Verify Wave 6

- [ ] **Step 1: Dry-run uninstall-old.ps1.**

Run: `pwsh scripts/uninstall-old.ps1 -WhatIf`
Expected: lists what would be removed without actually removing. No errors.

- [ ] **Step 2: Test migration.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug --filter "FullyQualifiedName~LegacyDataMigration"`
Expected: 2 tests pass.

- [ ] **Step 3: Commit.**

```bash
git add scripts/uninstall-old.ps1 CHANGELOG.md \
        src/shared/Infrastructure/LegacyDataMigration.cs \
        tests/RvtMcp.Tests/LegacyDataMigrationTests.cs \
        src/plugin-r22/App.cs src/plugin-r23/App.cs src/plugin-r24/App.cs \
        src/plugin-r25/App.cs src/plugin-r26/App.cs src/plugin-r27/App.cs \
        src/server/Program.cs
git commit -m "feat: legacy uninstall + data migration helper (Bimwright → RvtMcp)"
```

---

## Wave 7: Final Verify

**Goal:** Confirm the renamed codebase is functionally equivalent to the baseline.

### Task 7.1: Clean build everything

- [ ] **Step 1: Clean.**

Run: `dotnet clean src/RvtMcp.sln`

- [ ] **Step 2: Restore.**

Run: `dotnet restore src/RvtMcp.sln`
Expected: succeeds.

- [ ] **Step 3: Build Debug.**

Run: `dotnet build src/RvtMcp.sln -c Debug /p:RvtMcpSkipDeploy=true`
Expected: succeeds with **zero errors**. Warning count should match baseline (Wave 0).

- [ ] **Step 4: Build Release.**

Run: `dotnet build src/RvtMcp.sln -c Release /p:RvtMcpSkipDeploy=true`
Expected: succeeds.

### Task 7.2: Run all tests

- [ ] **Step 1: Test Debug.**

Run: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Debug --no-build`
Expected: same passing count as Wave 0 baseline. **If lower, STOP and diagnose.** If higher (only because Wave 6 added migration tests), document the new count.

- [ ] **Step 2: Pack NuGet.**

Run: `dotnet pack src/server/RvtMcp.Server.csproj -c Release --output artifacts/`
Expected: produces `artifacts/RvtMcp.Server.0.4.0.nupkg`. **NOT** `RvtMcp.Server.0.4.0.nupkg`.

- [ ] **Step 3: Inspect NuGet metadata.**

```bash
unzip -p artifacts/RvtMcp.Server.0.4.0.nupkg "*.nuspec" | head -40
```

Confirm:
- `<id>RvtMcp.Server</id>`
- `<title>` or `<description>` may mention Bimwright (brand) but `<id>` MUST be `RvtMcp.Server`.

### Task 7.3: Stale-reference grep

- [ ] **Step 1: Final grep for stale references.**

```powershell
Set-Location D:/Projects/bimwright/rvt-mcp
$stale = Select-String -Path src,tests,scripts,server.json,smithery.yaml,.mcp.json.example,.github -Recurse -Include *.cs,*.csproj,*.sln,*.addin,*.ps1,*.json,*.yml,*.yaml -Pattern 'Bimwright\.Rvt'
$stale | Format-List
```

Expected: empty output, OR only matches inside Wave 6 `uninstall-old.ps1` legacy-detection code (acceptable). **If any other match remains, treat as bug.**

- [ ] **Step 2: Lower-case `bimwright` literal grep (excluding brand-allowed locations).**

```powershell
$stale = Select-String -Path src,tests,scripts,server.json,smithery.yaml,.mcp.json.example,.github -Recurse -Include *.cs,*.csproj,*.sln,*.addin,*.ps1,*.json,*.yml,*.yaml -Pattern '\bbimwright\b'
$stale | Format-List
```

Expected matches (allowed):
- `<VendorId>bimwright</VendorId>` in `.addin` files (per Task 0.1 decision — or empty if user chose rename)
- Legacy-detection regex in `install.ps1` and `uninstall-old.ps1`
- `server.json` `name: io.github.bimwright/rvt-mcp` (GitHub org segment)
- `<RepositoryUrl>https://github.com/bimwright/rvt-mcp</RepositoryUrl>` in server csproj
- `Authors`, `Copyright`, `Description` brand mentions

Anything else → bug, fix it.

### Task 7.4: Smoke test (manual)

- [ ] **Step 1: Close Revit.**

User must close all running Revit instances. (Cannot be automated.)

- [ ] **Step 2: Deploy.**

Run: `dotnet build src/RvtMcp.sln -c Debug` (without `RvtMcpSkipDeploy` flag).
Expected: succeeds; `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` populated for each detected Revit year.

- [ ] **Step 3: Run uninstall-old (dry).**

Run: `pwsh scripts/uninstall-old.ps1 -WhatIf`
Expected: shows what would be cleaned. If legacy `Bimwright\` folder exists from prior installs, it's listed.

- [ ] **Step 4: Register the MCP server.**

Run: `claude mcp add rvt-mcp "D:/Projects/bimwright/rvt-mcp/src/server/bin/Debug/net8.0/RvtMcp.Server.exe"`
Expected: server registered with name `rvt-mcp`.

- [ ] **Step 5: Open Revit, verify ribbon loads.**

Open a Revit version (e.g., R24). Verify the **RvtMcp** ribbon panel/buttons appear (no "Bimwright" label). Verify `%LOCALAPPDATA%\RvtMcp\portR24.txt` is created when the plugin starts.

- [ ] **Step 6: Verify Claude Code can call a tool.**

In Claude Code, run `claude mcp list` → `rvt-mcp` should show **✓ Connected**. Invoke a simple tool like `analyze_model_statistics` → must return data without error.

If any of Steps 5-6 fail, the rename has a regression — diagnose and fix before final commit.

### Task 7.5: Final commit + push

- [ ] **Step 1: Update baseline-test-count.txt with new count.**

Append to scratchpad:
```
date: 2026-05-21
post-rename-tests-passed: <N>
match-baseline: yes/no
```

If `match-baseline: no`, **do not push.** Diagnose first.

- [ ] **Step 2: Commit any final tweaks.**

```bash
git status -s
# If any uncommitted fixes from smoke testing:
git add <files>
git commit -m "fix: post-rename smoke-test corrections"
```

- [ ] **Step 3: Push branch (manual user action — do NOT auto-push).**

Stop. Tell the user:

> *Rename complete. Branch `chore/rename-to-rvtmcp` is ready. Review the diff one more time, then push and open a PR:*
> ```
> git push -u origin chore/rename-to-rvtmcp
> gh pr create --title "Rename RvtMcp → RvtMcp" --body "See docs/superpowers/plans/2026-05-21-rename-rvt-mcp-to-rvtmcp.md"
> ```

Do not push or open the PR — user reviews and ships.

### Task 7.6: Post-merge follow-ups (track separately, NOT in this plan)

These are listed for awareness; create separate tickets/plans:

- Publish `RvtMcp.Server` 0.4.0 to NuGet.
- Deprecate `RvtMcp.Server` on NuGet: add `<PackageDescription>` notice pointing to new package; mark all versions as deprecated via `nuget.org` UI.
- Update Smithery registry entry (if active).
- Update MCP Registry entry at `io.github.bimwright/rvt-mcp` (if active).
- Rename GitHub release artifact names to match new naming (next release cycle).
- Inform users (release notes, README banner, etc.).

---

## Self-Review

**Spec coverage:**

- ✅ Solution + 8 csproj renames → Wave 1
- ✅ `<RootNamespace>`/`<AssemblyName>` updates → Wave 1
- ✅ Namespace `RvtMcp.*` → `RvtMcp.*` in code → Wave 2
- ✅ `RvtMcpConfig` class rename → Wave 2
- ✅ Addin manifests + GUIDs preserved → Wave 3
- ✅ Deploy paths `Addins\<year>\RvtMcp\` → Wave 3
- ✅ Discovery folder `%LOCALAPPDATA%\RvtMcp\` → Wave 3
- ✅ `install.ps1` + scripts → Wave 3
- ✅ `server.json` + `smithery.yaml` + GH Actions → Wave 4
- ✅ NuGet PackageId + ToolCommandName → Wave 1 (Server csproj) + reflected in Wave 4
- ✅ Docs (README × 3, ARCHITECTURE, AGENTS, CLAUDE, CHANGELOG, etc.) → Wave 5
- ✅ `uninstall-old.ps1` → Wave 6
- ✅ NuGet deprecation note in CHANGELOG + README → Wave 5 + Wave 6
- ✅ Brand `Bimwright` kept in Author/Copyright → explicit in mapping table + Wave 5 grep allowlist
- ✅ GitHub repo path unchanged → server.json + csproj URLs preserved per mapping
- ✅ AddInId GUIDs preserved → Wave 3 Task 3.1 Step 2

**Placeholder scan:** No "TBD"/"add appropriate"/"fill in later" remain. Migration helper code is fully written in Task 6.2 Step 2.

**Type/name consistency:** `RvtMcpConfig` is consistent everywhere (Wave 2 Task 2.2 + csproj Compile Include update). `RvtMcpSkipDeploy` MSBuild flag consistent in plugin csproj (Wave 1) + GH Actions (Wave 4). Tool command `rvt-mcp` consistent in csproj `<ToolCommandName>` (Wave 1), smithery (Wave 4), install.ps1 wire entries (Wave 3). MCP entry name `rvt-mcp` consistent with user's stated decision.

**Open risks called out:**

- Three deferred decisions in Task 0.1 — must be answered before Wave 2.
- One deferred decision in Task 6.2 — must be answered before Wave 6 Step 2.
- Wave 1 build is conditional on `RvtMcpSkipDeploy=true` because addin XML files aren't renamed yet; documented in Task 1.5 Step 1.
- Wave 3 changes discovery file path atomically — old plugin + new server cannot inter-communicate; CHANGELOG flags this as breaking (Wave 6 Task 6.3).

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-21-rename-rvt-mcp-to-rvtmcp.md`.

**Per user's earlier choice, execution proceeds via `executing-plans-via-opencode`:**

1. opencode CLI performs each wave's tasks.
2. Claude reviews diffs per wave, runs the verify step, and commits when verify passes.
3. Three deferred decisions in Task 0.1 are answered by user before Wave 2 starts.
4. One deferred decision in Task 6.2 is answered before Wave 6 Step 2.

**Ready to start Wave 0 when you give the go.**
