# Wave 14 & 15 — Structural Deep + Final Fill — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to execute task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm 18 MCP tools mới cho rvt-mcp (10 structural + 8 metadata/view), theo đúng pattern handler đang có.

**Architecture:** C# multi-version: handler đặt ở `src/shared/Handlers/` (1 file/tool, glob-included vào 6 plugin csproj R22-R27). Mỗi handler implement `IRevitCommand`, đăng ký vào `CommandDispatcher`, expose qua tool wrapper `[McpServerTool]` static method trong `Program.cs`. Cross-version API differences xử lý qua `#if REVIT2024_OR_GREATER`/`REVIT2026_OR_GREATER` + `RevitCompat` helper. Toolset gating qua `ToolsetFilter.cs`.

**Tech Stack:**
- .NET 4.8 (R22-R24), .NET 8 (R25-R26), .NET 10 (R27)
- Revit API 2022-2027 (Autodesk.Revit.DB, Autodesk.Revit.DB.Structure)
- ModelContextProtocol NuGet (MCP SDK)
- Newtonsoft.Json (13.0.3)
- Test: xUnit, snapshot via `UPDATE_SNAPSHOTS=1`

---

## Design Decisions (chốt từ review 2026-05-21)

1. **Read-only filtering cho view tools:** Per-tool check trong wrapper (không tách toolset). Mỗi view-write wrapper guard bằng `ServerState.IsReadOnly` trước khi gọi `ToolGateway.SendToRevit`. Toolset `view` KHÔNG thêm vào `WriteCapable`.
2. **Rebar handlers DEFER sang Wave 16:** `create_rebar_set` và `create_rebar_stirrup` bỏ khỏi plan này. Chỉ giữ `list_rebar` (read-only). Wave 14 còn 10 handlers thay vì 12.
3. **`purge_unused` scope MVP = families only:** Chỉ purge loadable family symbol có 0 instance VÀ không bị reference từ tag/schedule/view filter. Param `targets` chỉ chấp nhận `["families"]`. Không có `materials`/`types`/`all` ở wave này.
4. **`set_project_info` chỉ typed fields:** Bỏ generic `parameters` object. Chỉ chấp nhận `name`, `number`, `client_name`, `address`, `status`, `issue_date`. Mở rộng sau nếu cần.
5. **`structural` toolset opt-in:** Thêm vào `KnownToolsets` + `WriteCapable` nhưng KHÔNG thêm vào `DefaultOn`. Theo precedent của `delete`/`modify`. User phải `--toolsets structural` để bật.
6. **Transaction naming:** Dùng `"Bimwright: <Action>"` (theo pattern mới, ≈50 handler đang dùng). Migrate 9 handler `MCP:` cũ ngoài scope plan này.

---

## File Structure

### Created files (12 handler + 1 helper + 1 test util)

| Path | Responsibility |
|---|---|
| `src/shared/Handlers/CreateStructuralColumnHandler.cs` | W14 handler |
| `src/shared/Handlers/CreateStructuralBeamHandler.cs` | W14 handler |
| `src/shared/Handlers/CreateStructuralWallHandler.cs` | W14 handler |
| `src/shared/Handlers/CreateFoundationIsolatedHandler.cs` | W14 handler |
| `src/shared/Handlers/CreateFoundationWallHandler.cs` | W14 handler |
| `src/shared/Handlers/ListRebarHandler.cs` | W14 read-only handler |
| `src/shared/Handlers/GetStructuralLoadsHandler.cs` | W14 read-only handler |
| `src/shared/Handlers/SetStructuralLoadHandler.cs` | W14 handler |
| `src/shared/Handlers/AnalyzeStructuralConnectionsHandler.cs` | W14 read-only handler |
| `src/shared/Handlers/TagStructuralFramingHandler.cs` | W14 handler |
| `src/shared/Handlers/SetProjectInfoHandler.cs` | W15 handler (meta) |
| `src/shared/Handlers/GetModelWarningsSummaryHandler.cs` | W15 read-only (lint) |
| `src/shared/Handlers/PurgeUnusedHandler.cs` | W15 handler (meta) |
| `src/shared/Handlers/CaptureViewImageHandler.cs` | W15 handler (view) |
| `src/shared/Handlers/SetViewCropHandler.cs` | W15 handler (view) |
| `src/shared/Handlers/SetViewScaleHandler.cs` | W15 handler (view) |
| `src/shared/Handlers/ActivateViewHandler.cs` | W15 handler (view, UI-only) |
| `src/shared/Handlers/ShowElementInViewHandler.cs` | W15 handler (view, UI-only) |
| `src/server/ServerState.cs` | Static config accessor for wrappers |

### Modified files

| Path | Change |
|---|---|
| `src/shared/Infrastructure/CommandDispatcher.cs` | Register 18 new handlers |
| `src/server/Program.cs` | Add `StructuralTools` class + extend `MetaTools`/`LintTools`/`ViewTools` wrappers + set `ServerState.Config` in startup |
| `src/server/ToolsetFilter.cs` | Add `"structural"` to `KnownToolsets` + `WriteCapable` |
| `tests/RvtMcp.Tests/Golden/tools-list.json` | Snapshot regen with new tools |
| `tests/RvtMcp.Tests/Golden/tools-list-adaptive-bake.json` | Snapshot regen with new tools |

### Files NOT touched

- 6 `plugin-r{22..27}/*.csproj`: nguồn shared/Handlers tự include qua glob, không cần sửa.
- Plugin `App.cs`/`RibbonSetup.cs`: tool mới chỉ exposes qua MCP, không cần ribbon button.

---

## Pre-flight Checks

### Task 0: Verify clean baseline

- [ ] **Step 1: Check working tree clean**

```powershell
git -C D:\Projects\bimwright\rvt-mcp status --short
```

Expected: empty output (no uncommitted changes). If not clean, stash or commit first.

- [ ] **Step 2: Verify build passes on baseline**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (or only pre-existing warnings).
**If fails:** STOP. Fix baseline first.

- [ ] **Step 3: Verify snapshot test passes on baseline**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: all tests PASS. This is the "green" we will iterate from.

- [ ] **Step 4: Create feature branch**

```powershell
git -C D:\Projects\bimwright\rvt-mcp checkout -b feature/wave-14-15-structural-final-fill
```

Expected: `Switched to a new branch 'feature/wave-14-15-structural-final-fill'`.

---

## WAVE 14 — STRUCTURAL DEEP (10 handlers + setup)

### Task 1: Bootstrap `structural` toolset infrastructure

**Files:**
- Modify: `D:\Projects\bimwright\rvt-mcp\src\server\ToolsetFilter.cs` (lines 15-32)
- Modify: `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs` (RegisterToolsets + ResolveRegisteredToolTypes + add empty `StructuralTools` class)

- [ ] **Step 1: Add `"structural"` to ToolsetFilter sets**

Edit `D:\Projects\bimwright\rvt-mcp\src\server\ToolsetFilter.cs`:

Replace lines 15-20:
```csharp
public static readonly string[] KnownToolsets =
{
    "query", "create", "modify", "delete", "view",
    "export", "annotation", "mep", "schedule", "families", "graphics", "toolbaker", "meta", "lint",
    "sheets", "materials", "geometry", "rooms", "links", "parameters", "organization", "workflows",
    "structural"
};
```

Replace lines 28-32 (`WriteCapable`):
```csharp
public static readonly string[] WriteCapable =
{
    "create", "modify", "delete", "schedule", "families", "mep", "graphics", "export", "toolbaker",
    "sheets", "materials", "annotation", "rooms", "links", "parameters", "organization", "workflows",
    "structural"
};
```

**Do NOT add to `DefaultOn`** — structural is opt-in.

- [ ] **Step 2: Add `StructuralTools` class skeleton in Program.cs**

In `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs`, locate the end of `MetaTools` class (search for `public static class MetaTools` and find its closing `}`). Add immediately AFTER it:

```csharp
[McpServerToolType, Toolset("structural")]
public static class StructuralTools
{
    // Handlers added in Tasks 2-11.
}
```

- [ ] **Step 3: Register `StructuralTools` in RegisterToolsets + ResolveRegisteredToolTypes**

Find `RegisterToolsets` method (lines ~217-244). Add line BEFORE `return mcp;`:
```csharp
if (enabled.Contains("structural")) mcp = mcp.WithTools<StructuralTools>();
```

Find `ResolveRegisteredToolTypes` method (lines ~246-274). Add line BEFORE the final `return ...;`:
```csharp
if (enabled.Contains("structural")) types.Add(typeof(StructuralTools));
```

(Adjust syntax to match the existing pattern in that method — it likely uses `List<Type>.Add(typeof(X))` or similar.)

- [ ] **Step 4: Build solution**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: `Build succeeded`. The empty `StructuralTools` class compiles. ToolsetFilter changes do not break anything because no handler is registered yet.

- [ ] **Step 5: Verify snapshot still passes**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: PASS. Adding `structural` to `KnownToolsets` does not break snapshot — `structural` is opt-in, default config does not enable it, so snapshot tool list unchanged.

- [ ] **Step 6: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/server/ToolsetFilter.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): bootstrap structural toolset (opt-in)"
```

---

### Task 2: `create_structural_column`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralColumnHandler.cs`
- Modify: `D:\Projects\bimwright\rvt-mcp\src\shared\Infrastructure\CommandDispatcher.cs` (add 1 line)
- Modify: `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs` (add wrapper in `StructuralTools`)

- [ ] **Step 1: Create handler file**

File `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralColumnHandler.cs`:

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralColumnHandler : IRevitCommand
    {
        public string Name => "create_structural_column";
        public string Description => "Create a structural column at a point. Resolves FamilySymbol via type_id or type_name (structural column category). Returns created_id, type_id, level_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type_id"":{""type"":""integer""},""type_name"":{""type"":""string""},""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""height_mm"":{""type"":""number""},""rotation_deg"":{""type"":""number""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("type_id");
            var typeName = req.Value<string>("type_name");
            var xMm = req.Value<double?>("x_mm") ?? 0;
            var yMm = req.Value<double?>("y_mm") ?? 0;
            var zMm = req.Value<double?>("z_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var heightMm = req.Value<double?>("height_mm");
            var rotationDeg = req.Value<double?>("rotation_deg") ?? 0;

            // Resolve FamilySymbol (structural columns)
            var symbol = ResolveSymbol(doc, typeId, typeName);
            if (symbol == null)
                return CommandResult.Fail("Could not resolve structural column FamilySymbol. Provide type_id or type_name.");

            // Resolve Level
            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null)
                return CommandResult.Fail("Could not resolve Level. Provide level_id or level_name, or ensure project has at least one level.");

            var pt = new XYZ(xMm / 304.8, yMm / 304.8, zMm / 304.8);

            using (var tx = new Transaction(doc, "Bimwright: Create structural column"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    doc.Regenerate();

                    var inst = doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.Column);

                    if (heightMm.HasValue)
                    {
                        var topParam = inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                        topParam?.Set(heightMm.Value / 304.8);
                    }

                    if (Math.Abs(rotationDeg) > 1e-6)
                    {
                        var axis = Line.CreateBound(pt, pt + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, inst.Id, axis, rotationDeg * Math.PI / 180.0);
                    }

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(inst.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        base_point_mm = new { x = xMm, y = yMm, z = zMm },
                        structural_type = "Column"
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural column: {ex.Message}");
                }
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .Cast<FamilySymbol>();

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return query.FirstOrDefault(s =>
                    s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    $"{s.Family.Name}: {s.Name}".Equals(typeName, StringComparison.OrdinalIgnoreCase));
            }
            return query.FirstOrDefault();
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

In `D:\Projects\bimwright\rvt-mcp\src\shared\Infrastructure\CommandDispatcher.cs`, in the constructor, after any existing structural-adjacent registration (or at the end of built-in handler registrations, BEFORE the baked-tool block), add:

```csharp
Register(new Handlers.CreateStructuralColumnHandler());
```

- [ ] **Step 3: Add MCP wrapper in `StructuralTools`**

In `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs`, inside `StructuralTools` class (added in Task 1), add:

```csharp
[McpServerTool(Name = "create_structural_column"), System.ComponentModel.Description("Create a structural column at a point. Params: type_id OR type_name (structural column family), x_mm/y_mm/z_mm (default 0), level_id OR level_name (default lowest level), height_mm (optional top offset), rotation_deg (optional, default 0).")]
public static async Task<string> CreateStructuralColumn(
    long? type_id = null, string type_name = null,
    double x_mm = 0, double y_mm = 0, double z_mm = 0,
    long? level_id = null, string level_name = null,
    double? height_mm = null, double rotation_deg = 0)
{
    try
    {
        var result = await ToolGateway.SendToRevit("create_structural_column", new {
            type_id, type_name, x_mm, y_mm, z_mm,
            level_id, level_name, height_mm, rotation_deg
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build all 6 plugin csproj + server**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: `Build succeeded. 0 Error(s)`. If errors on `StructuralType.Column`, verify `using Autodesk.Revit.DB.Structure;` is included.

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CreateStructuralColumnHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add create_structural_column handler"
```

---

### Task 3: `create_structural_beam`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralBeamHandler.cs`
- Modify: `CommandDispatcher.cs`, `Program.cs` (StructuralTools)

- [ ] **Step 1: Create handler file**

File `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralBeamHandler.cs`:

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralBeamHandler : IRevitCommand
    {
        public string Name => "create_structural_beam";
        public string Description => "Create a structural beam between two points. Resolves FamilySymbol via type_id or type_name (StructuralFraming category). Returns created_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type_id"":{""type"":""integer""},""type_name"":{""type"":""string""},""start_x_mm"":{""type"":""number""},""start_y_mm"":{""type"":""number""},""start_z_mm"":{""type"":""number""},""end_x_mm"":{""type"":""number""},""end_y_mm"":{""type"":""number""},""end_z_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""usage"":{""type"":""string"",""enum"":[""beam"",""brace"",""joist""]}},""required"":[""start_x_mm"",""start_y_mm"",""end_x_mm"",""end_y_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("type_id");
            var typeName = req.Value<string>("type_name");
            var sx = req.Value<double?>("start_x_mm") ?? 0;
            var sy = req.Value<double?>("start_y_mm") ?? 0;
            var sz = req.Value<double?>("start_z_mm") ?? 0;
            var ex = req.Value<double?>("end_x_mm") ?? 0;
            var ey = req.Value<double?>("end_y_mm") ?? 0;
            var ez = req.Value<double?>("end_z_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var usage = (req.Value<string>("usage") ?? "beam").ToLowerInvariant();

            var symbol = ResolveSymbol(doc, typeId, typeName);
            if (symbol == null)
                return CommandResult.Fail("Could not resolve structural framing FamilySymbol. Provide type_id or type_name.");

            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null)
                return CommandResult.Fail("Could not resolve Level.");

            var start = new XYZ(sx / 304.8, sy / 304.8, sz / 304.8);
            var end = new XYZ(ex / 304.8, ey / 304.8, ez / 304.8);
            if (start.DistanceTo(end) < 1e-6)
                return CommandResult.Fail("start and end points are identical.");

            var line = Line.CreateBound(start, end);

            var structType = usage switch
            {
                "brace" => StructuralType.Brace,
                "joist" => StructuralType.Beam, // R22-R27 has no Joist; Beam + joist family is common
                _ => StructuralType.Beam
            };

            using (var tx = new Transaction(doc, "Bimwright: Create structural beam"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    doc.Regenerate();

                    var inst = doc.Create.NewFamilyInstance(line, symbol, level, structType);

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(inst.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        usage,
                        structural_type = structType.ToString()
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural beam: {ex_.Message}");
                }
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .Cast<FamilySymbol>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return query.FirstOrDefault(s =>
                    s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    $"{s.Family.Name}: {s.Name}".Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return query.FirstOrDefault();
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

Add after the column registration:
```csharp
Register(new Handlers.CreateStructuralBeamHandler());
```

- [ ] **Step 3: Add MCP wrapper in `StructuralTools`**

```csharp
[McpServerTool(Name = "create_structural_beam"), System.ComponentModel.Description("Create a structural beam between two points. Params: type_id OR type_name (structural framing family), start_x_mm/start_y_mm/start_z_mm (required), end_x_mm/end_y_mm/end_z_mm (required), level_id OR level_name, usage ('beam'|'brace'|'joist', default 'beam').")]
public static async Task<string> CreateStructuralBeam(
    double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
    long? type_id = null, string type_name = null,
    double start_z_mm = 0, double end_z_mm = 0,
    long? level_id = null, string level_name = null, string usage = "beam")
{
    try
    {
        var result = await ToolGateway.SendToRevit("create_structural_beam", new {
            type_id, type_name, start_x_mm, start_y_mm, start_z_mm,
            end_x_mm, end_y_mm, end_z_mm, level_id, level_name, usage
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CreateStructuralBeamHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add create_structural_beam handler"
```

---

### Task 4: `create_structural_wall`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralWallHandler.cs`
- Modify: `CommandDispatcher.cs`, `Program.cs`

- [ ] **Step 1: Create handler file**

File `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateStructuralWallHandler.cs`:

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralWallHandler : IRevitCommand
    {
        public string Name => "create_structural_wall";
        public string Description => "Create a structural wall (isStructural=true) between two points on a level. Uses Wall.Create with structural flag.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""wall_type_id"":{""type"":""integer""},""wall_type_name"":{""type"":""string""},""start_x_mm"":{""type"":""number""},""start_y_mm"":{""type"":""number""},""end_x_mm"":{""type"":""number""},""end_y_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""height_mm"":{""type"":""number""}},""required"":[""start_x_mm"",""start_y_mm"",""end_x_mm"",""end_y_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("wall_type_id");
            var typeName = req.Value<string>("wall_type_name");
            var sx = req.Value<double?>("start_x_mm") ?? 0;
            var sy = req.Value<double?>("start_y_mm") ?? 0;
            var ex = req.Value<double?>("end_x_mm") ?? 0;
            var ey = req.Value<double?>("end_y_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var heightMm = req.Value<double?>("height_mm") ?? 3000;

            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null) return CommandResult.Fail("Could not resolve Level.");

            var start = new XYZ(sx / 304.8, sy / 304.8, 0);
            var end = new XYZ(ex / 304.8, ey / 304.8, 0);
            if (start.DistanceTo(end) < 1e-6)
                return CommandResult.Fail("start and end points are identical.");
            var line = Line.CreateBound(start, end);

            using (var tx = new Transaction(doc, "Bimwright: Create structural wall"))
            {
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, line, level.Id, true); // isStructural=true
                    wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(heightMm / 304.8);

                    if (typeId.HasValue || !string.IsNullOrWhiteSpace(typeName))
                    {
                        var wallType = ResolveWallType(doc, typeId, typeName);
                        if (wallType != null) wall.WallType = wallType;
                    }

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(wall.Id),
                        wall_type_id = RevitCompat.GetId(wall.WallType.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        height_mm = heightMm,
                        is_structural = true
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural wall: {ex_.Message}");
                }
            }
        }

        private static WallType ResolveWallType(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as WallType;
            var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

```csharp
Register(new Handlers.CreateStructuralWallHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "create_structural_wall"), System.ComponentModel.Description("Create a structural wall between two points. Params: start_x_mm/start_y_mm/end_x_mm/end_y_mm (required), wall_type_id OR wall_type_name (optional, default current), level_id OR level_name, height_mm (default 3000). Sets isStructural=true.")]
public static async Task<string> CreateStructuralWall(
    double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
    long? wall_type_id = null, string wall_type_name = null,
    long? level_id = null, string level_name = null, double height_mm = 3000)
{
    try
    {
        var result = await ToolGateway.SendToRevit("create_structural_wall", new {
            wall_type_id, wall_type_name, start_x_mm, start_y_mm,
            end_x_mm, end_y_mm, level_id, level_name, height_mm
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CreateStructuralWallHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add create_structural_wall handler"
```

---

### Task 5: `create_foundation_isolated`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateFoundationIsolatedHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateFoundationIsolatedHandler : IRevitCommand
    {
        public string Name => "create_foundation_isolated";
        public string Description => "Create an isolated/spread footing at a point. Resolves FamilySymbol via type_id or type_name (StructuralFoundation category).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type_id"":{""type"":""integer""},""type_name"":{""type"":""string""},""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""host_column_id"":{""type"":""integer""},""rotation_deg"":{""type"":""number""}},""required"":[""x_mm"",""y_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("type_id");
            var typeName = req.Value<string>("type_name");
            var xMm = req.Value<double?>("x_mm") ?? 0;
            var yMm = req.Value<double?>("y_mm") ?? 0;
            var zMm = req.Value<double?>("z_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var hostColumnId = req.Value<long?>("host_column_id");
            var rotationDeg = req.Value<double?>("rotation_deg") ?? 0;

            var symbol = ResolveSymbol(doc, typeId, typeName);
            if (symbol == null)
                return CommandResult.Fail("Could not resolve isolated foundation FamilySymbol.");

            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null) return CommandResult.Fail("Could not resolve Level.");

            XYZ pt;
            if (hostColumnId.HasValue)
            {
                var host = doc.GetElement(RevitCompat.ToElementId(hostColumnId.Value));
                if (host?.Location is LocationPoint lp) pt = new XYZ(lp.Point.X, lp.Point.Y, zMm / 304.8);
                else pt = new XYZ(xMm / 304.8, yMm / 304.8, zMm / 304.8);
            }
            else
            {
                pt = new XYZ(xMm / 304.8, yMm / 304.8, zMm / 304.8);
            }

            using (var tx = new Transaction(doc, "Bimwright: Create isolated foundation"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    doc.Regenerate();

                    var inst = doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.Footing);

                    if (Math.Abs(rotationDeg) > 1e-6)
                    {
                        var axis = Line.CreateBound(pt, pt + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, inst.Id, axis, rotationDeg * Math.PI / 180.0);
                    }

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(inst.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        host_column_id = hostColumnId,
                        structural_type = "Footing"
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create isolated foundation: {ex.Message}");
                }
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;
            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .Cast<FamilySymbol>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return query.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return query.FirstOrDefault();
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

```csharp
Register(new Handlers.CreateFoundationIsolatedHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "create_foundation_isolated"), System.ComponentModel.Description("Create an isolated/spread footing at a point or under an existing column. Params: type_id OR type_name (StructuralFoundation), x_mm/y_mm (required), z_mm (default 0), level_id OR level_name, host_column_id (optional — when supplied, location is taken from the column), rotation_deg.")]
public static async Task<string> CreateFoundationIsolated(
    double x_mm, double y_mm,
    long? type_id = null, string type_name = null, double z_mm = 0,
    long? level_id = null, string level_name = null,
    long? host_column_id = null, double rotation_deg = 0)
{
    try
    {
        var result = await ToolGateway.SendToRevit("create_foundation_isolated", new {
            type_id, type_name, x_mm, y_mm, z_mm,
            level_id, level_name, host_column_id, rotation_deg
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CreateFoundationIsolatedHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add create_foundation_isolated handler"
```

---

### Task 6: `create_foundation_wall`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CreateFoundationWallHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateFoundationWallHandler : IRevitCommand
    {
        public string Name => "create_foundation_wall";
        public string Description => "Create a wall foundation under an existing wall using WallFoundation.Create.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""wall_id"":{""type"":""integer""},""foundation_type_id"":{""type"":""integer""},""foundation_type_name"":{""type"":""string""}},""required"":[""wall_id""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var wallId = req.Value<long?>("wall_id");
            if (!wallId.HasValue) return CommandResult.Fail("wall_id is required.");

            var wallElemId = RevitCompat.ToElementId(wallId.Value);
            var wall = doc.GetElement(wallElemId) as Wall;
            if (wall == null) return CommandResult.Fail($"wall_id {wallId} is not a Wall element.");

            var footingType = ResolveFootingType(doc, req.Value<long?>("foundation_type_id"), req.Value<string>("foundation_type_name"));
            if (footingType == null)
                return CommandResult.Fail("Could not resolve WallFoundation type. Provide foundation_type_id or foundation_type_name.");

            using (var tx = new Transaction(doc, "Bimwright: Create wall foundation"))
            {
                tx.Start();
                try
                {
                    var wf = WallFoundation.Create(doc, footingType.Id, wallElemId);
                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(wf.Id),
                        wall_id = wallId.Value,
                        foundation_type_id = RevitCompat.GetId(footingType.Id)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create wall foundation: {ex.Message}");
                }
            }
        }

        private static FoundationType ResolveFootingType(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FoundationType;
            var types = new FilteredElementCollector(doc).OfClass(typeof(FoundationType)).Cast<FoundationType>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return types.FirstOrDefault();
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

```csharp
Register(new Handlers.CreateFoundationWallHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "create_foundation_wall"), System.ComponentModel.Description("Create a wall foundation under an existing wall. Params: wall_id (required), foundation_type_id OR foundation_type_name (optional, defaults to first WallFoundation type).")]
public static async Task<string> CreateFoundationWall(
    long wall_id,
    long? foundation_type_id = null, string foundation_type_name = null)
{
    try
    {
        var result = await ToolGateway.SendToRevit("create_foundation_wall", new {
            wall_id, foundation_type_id, foundation_type_name
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: build succeeds across R22-R27. `WallFoundation.Create` and `FoundationType` are available since R2014.

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CreateFoundationWallHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add create_foundation_wall handler"
```

---

### Task 7: `list_rebar` (read-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\ListRebarHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListRebarHandler : IRevitCommand
    {
        public string Name => "list_rebar";
        public string Description => "List rebar instances. Optionally filter by host_id or view_id. Returns per-rebar: id, bar_type, diameter_mm, quantity, layout_rule, host_id, host_category.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""host_id"":{""type"":""integer""},""view_id"":{""type"":""integer""},""limit"":{""type"":""integer"",""default"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var hostId = req.Value<long?>("host_id");
            var viewId = req.Value<long?>("view_id");
            var limit = req.Value<int?>("limit") ?? 500;

            FilteredElementCollector collector;
            if (viewId.HasValue)
                collector = new FilteredElementCollector(doc, RevitCompat.ToElementId(viewId.Value));
            else
                collector = new FilteredElementCollector(doc);

            var rebars = collector.OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            if (hostId.HasValue)
            {
                var hostElemId = RevitCompat.ToElementId(hostId.Value);
                rebars = rebars.Where(r => r.GetHostId() == hostElemId).ToList();
            }

            var items = new List<object>();
            foreach (var r in rebars.Take(limit))
            {
                var barType = doc.GetElement(r.GetTypeId()) as RebarBarType;
                double diameterMm = 0;
                try { if (barType != null) diameterMm = barType.BarDiameter * 304.8; } catch { }

                var host = doc.GetElement(r.GetHostId());

                items.Add(new
                {
                    id = RevitCompat.GetId(r.Id),
                    bar_type_id = RevitCompat.GetIdOrNull(r.GetTypeId()),
                    bar_type_name = barType?.Name,
                    diameter_mm = Math.Round(diameterMm, 2),
                    quantity = r.Quantity,
                    layout_rule = r.LayoutRule.ToString(),
                    host_id = RevitCompat.GetIdOrNull(r.GetHostId()),
                    host_category = host?.Category?.Name
                });
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                total_matched = rebars.Count,
                truncated = rebars.Count > limit,
                items
            });
        }
    }
}
```

- [ ] **Step 2: Register in CommandDispatcher**

```csharp
Register(new Handlers.ListRebarHandler());
```

- [ ] **Step 3: MCP wrapper (mark ReadOnly = true)**

```csharp
[McpServerTool(Name = "list_rebar", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List rebar instances. Optional filters: host_id (limit to one host element), view_id (limit to one view), limit (default 500). Returns id, bar_type, diameter_mm, quantity, layout_rule, host info per rebar.")]
public static async Task<string> ListRebar(
    long? host_id = null, long? view_id = null, int limit = 500)
{
    try
    {
        var result = await ToolGateway.SendToRevit("list_rebar", new { host_id, view_id, limit });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: build OK. `Rebar` and `RebarBarType` are stable across R22-R27. If any deprecation warning, note for future cleanup but do not block.

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/ListRebarHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add list_rebar read-only handler"
```

---

### Task 8: `get_structural_loads` (read-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\GetStructuralLoadsHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetStructuralLoadsHandler : IRevitCommand
    {
        public string Name => "get_structural_loads";
        public string Description => "Return structural loads (point/line/area). Optional filters: element_id (host), load_type ('point'|'line'|'area'). Returns id, type, host_id, force XYZ, moment XYZ, case info.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_id"":{""type"":""integer""},""load_type"":{""type"":""string"",""enum"":[""point"",""line"",""area""]},""limit"":{""type"":""integer"",""default"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var elementId = req.Value<long?>("element_id");
            var loadType = (req.Value<string>("load_type") ?? "").ToLowerInvariant();
            var limit = req.Value<int?>("limit") ?? 500;

            var cats = new List<BuiltInCategory>();
            if (loadType == "point") cats.Add(BuiltInCategory.OST_PointLoads);
            else if (loadType == "line") cats.Add(BuiltInCategory.OST_LineLoads);
            else if (loadType == "area") cats.Add(BuiltInCategory.OST_AreaLoads);
            else { cats.Add(BuiltInCategory.OST_PointLoads); cats.Add(BuiltInCategory.OST_LineLoads); cats.Add(BuiltInCategory.OST_AreaLoads); }

            var items = new List<object>();
            foreach (var cat in cats)
            {
                var loads = new FilteredElementCollector(doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var load in loads)
                {
                    if (items.Count >= limit) break;

                    // host id via param HOST_ID_PARAM where available, else dynamic
                    long? hostIdValue = TryGetLongParam(load, BuiltInParameter.HOST_ID_PARAM);

                    if (elementId.HasValue && hostIdValue != elementId) continue;

                    items.Add(new
                    {
                        id = RevitCompat.GetId(load.Id),
                        load_kind = cat switch
                        {
                            BuiltInCategory.OST_PointLoads => "point",
                            BuiltInCategory.OST_LineLoads => "line",
                            BuiltInCategory.OST_AreaLoads => "area",
                            _ => "unknown"
                        },
                        host_id = hostIdValue,
                        force_x = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FX),
                        force_y = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FY),
                        force_z = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FZ),
                        moment_x = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MX),
                        moment_y = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MY),
                        moment_z = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MZ),
                        case_id = TryGetLongParam(load, BuiltInParameter.LOAD_CASE_ID),
                        case_name = TryGetStringParam(load, BuiltInParameter.LOAD_CASE_ID)
                    });
                }
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                truncated = items.Count >= limit,
                items
            });
        }

        private static double? TryGetDoubleParam(Element e, BuiltInParameter bip)
        {
            try { var p = e.get_Parameter(bip); return p != null && p.HasValue ? p.AsDouble() : (double?)null; }
            catch { return null; }
        }
        private static long? TryGetLongParam(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                if (p.StorageType == StorageType.ElementId) return RevitCompat.GetIdOrNull(p.AsElementId());
                if (p.StorageType == StorageType.Integer) return p.AsInteger();
                return null;
            }
            catch { return null; }
        }
        private static string TryGetStringParam(Element e, BuiltInParameter bip)
        {
            try { var p = e.get_Parameter(bip); return p?.AsValueString(); } catch { return null; }
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.GetStructuralLoadsHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "get_structural_loads", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List structural loads (point, line, area). Filter by element_id (host) or load_type ('point'|'line'|'area'). Returns force/moment components per load.")]
public static async Task<string> GetStructuralLoads(
    long? element_id = null, string load_type = null, int limit = 500)
{
    try
    {
        var result = await ToolGateway.SendToRevit("get_structural_loads", new { element_id, load_type, limit });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/GetStructuralLoadsHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add get_structural_loads read-only handler"
```

---

### Task 9: `set_structural_load`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\SetStructuralLoadHandler.cs`

> **Note:** For MVP this handler supports **update only** (modifying force/moment via parameters on an existing load). Creating new loads via `PointLoad.Create` etc. has cross-version surface area that needs spike validation; **`action=create` returns a structured `not_implemented` response** in this wave. Future wave can extend.

- [ ] **Step 1: Create handler file**

```csharp
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetStructuralLoadHandler : IRevitCommand
    {
        public string Name => "set_structural_load";
        public string Description => "Update force/moment components of an existing structural load. action='update' supported; action='create' returns not_implemented (deferred).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""create"",""update""]},""load_id"":{""type"":""integer""},""force_x"":{""type"":""number""},""force_y"":{""type"":""number""},""force_z"":{""type"":""number""},""moment_x"":{""type"":""number""},""moment_y"":{""type"":""number""},""moment_z"":{""type"":""number""}},""required"":[""action""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var action = (req.Value<string>("action") ?? "").ToLowerInvariant();

            if (action == "create")
                return CommandResult.Ok(new
                {
                    status = "not_implemented",
                    reason = "Load creation deferred to a future wave; spike PointLoad/LineLoad/AreaLoad.Create across R22-R27 first."
                });

            if (action != "update")
                return CommandResult.Fail("action must be 'update' (create is deferred).");

            var loadId = req.Value<long?>("load_id");
            if (!loadId.HasValue) return CommandResult.Fail("load_id is required for update.");

            var load = doc.GetElement(RevitCompat.ToElementId(loadId.Value));
            if (load == null) return CommandResult.Fail($"load_id {loadId} not found.");

            using (var tx = new Transaction(doc, "Bimwright: Update structural load"))
            {
                tx.Start();
                try
                {
                    int changed = 0;
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FX, req, "force_x");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FY, req, "force_y");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_FORCE_FZ, req, "force_z");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MX, req, "moment_x");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MY, req, "moment_y");
                    changed += SetIfPresent(load, BuiltInParameter.LOAD_MOMENT_MZ, req, "moment_z");

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        load_id = loadId.Value,
                        changed_fields = changed,
                        status = changed == 0 ? "no_changes" : "updated"
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to update load: {ex.Message}");
                }
            }
        }

        private static int SetIfPresent(Element e, BuiltInParameter bip, JObject req, string key)
        {
            var v = req.Value<double?>(key);
            if (!v.HasValue) return 0;
            var p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return 0;
            p.Set(v.Value);
            return 1;
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.SetStructuralLoadHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "set_structural_load"), System.ComponentModel.Description("Update force/moment of an existing structural load. action='update' supported; action='create' returns not_implemented. Params: action ('update'), load_id (required for update), force_x/y/z, moment_x/y/z (optional, units = Revit internal).")]
public static async Task<string> SetStructuralLoad(
    string action,
    long? load_id = null,
    double? force_x = null, double? force_y = null, double? force_z = null,
    double? moment_x = null, double? moment_y = null, double? moment_z = null)
{
    try
    {
        var result = await ToolGateway.SendToRevit("set_structural_load", new {
            action, load_id, force_x, force_y, force_z, moment_x, moment_y, moment_z
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/SetStructuralLoadHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add set_structural_load (update-only; create deferred)"
```

---

### Task 10: `analyze_structural_connections` (read-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\AnalyzeStructuralConnectionsHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeStructuralConnectionsHandler : IRevitCommand
    {
        public string Name => "analyze_structural_connections";
        public string Description => "Audit structural joins between columns/beams. Reports per-element: joined neighbor count + neighbor ids. Optional element_ids filter; default = all structural framing + columns in model.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""limit"":{""type"":""integer"",""default"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var limit = req.Value<int?>("limit") ?? 500;
            var idsToken = req["element_ids"] as JArray;

            IEnumerable<Element> elements;
            if (idsToken != null && idsToken.Any())
            {
                elements = idsToken.Select(t => doc.GetElement(RevitCompat.ToElementId(t.Value<long>())))
                                   .Where(e => e != null);
            }
            else
            {
                var framing = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType();
                var columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType();
                elements = framing.Cast<Element>().Concat(columns.Cast<Element>());
            }

            var items = new List<object>();
            foreach (var e in elements.Take(limit))
            {
                var neighborsStart = new List<long>();
                var neighborsEnd = new List<long>();

                try
                {
                    var startJoined = JoinGeometryUtils.GetJoinedElements(doc, e).Select(id => RevitCompat.GetId(id)).ToList();
                    neighborsStart = startJoined;
                }
                catch { /* not joinable */ }

                items.Add(new
                {
                    id = RevitCompat.GetId(e.Id),
                    category = e.Category?.Name,
                    name = e.Name,
                    joined_count = neighborsStart.Count,
                    joined_with = neighborsStart
                });
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                items
            });
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.AnalyzeStructuralConnectionsHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "analyze_structural_connections", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Audit structural joins between columns and beams. Optional element_ids filter (default = all structural framing + columns). Returns joined_count and joined_with per element.")]
public static async Task<string> AnalyzeStructuralConnections(
    long[] element_ids = null, int limit = 500)
{
    try
    {
        var result = await ToolGateway.SendToRevit("analyze_structural_connections", new { element_ids, limit });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/AnalyzeStructuralConnectionsHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add analyze_structural_connections handler"
```

---

### Task 11: `tag_structural_framing`

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\TagStructuralFramingHandler.cs`

> **Note:** Per design decision (item 6), **no `dry_run` parameter** — consistent with `TagAllWallsHandler` and `TagAllRoomsHandler`.

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class TagStructuralFramingHandler : IRevitCommand
    {
        public string Name => "tag_structural_framing";
        public string Description => "Place structural framing tags on structural beams in the active or specified view. Uses default tag type unless tag_type_id provided.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""tag_type_id"":{""type"":""integer""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var tagTypeIdParam = req.Value<long?>("tag_type_id");
            var elementIdsToken = req["element_ids"] as JArray;

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");

            FamilySymbol tagType;
            if (tagTypeIdParam.HasValue)
                tagType = doc.GetElement(RevitCompat.ToElementId(tagTypeIdParam.Value)) as FamilySymbol;
            else
                tagType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_StructuralFramingTags)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
            if (tagType == null)
                return CommandResult.Fail("No StructuralFramingTags family loaded in project.");

            List<Element> elements;
            if (elementIdsToken != null && elementIdsToken.Any())
            {
                var framingCatId = new ElementId(BuiltInCategory.OST_StructuralFraming);
                elements = elementIdsToken.Select(t => doc.GetElement(RevitCompat.ToElementId(t.Value<long>())))
                                          .Where(e => e != null && e.Category?.Id == framingCatId)
                                          .ToList();
            }
            else
            {
                elements = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            int tagged = 0;
            int skipped = 0;
            using (var tx = new Transaction(doc, "Bimwright: Tag structural framing"))
            {
                tx.Start();
                try
                {
                    if (!tagType.IsActive) tagType.Activate();
                    doc.Regenerate();

                    foreach (var e in elements)
                    {
                        try
                        {
                            XYZ pt;
                            if (e.Location is LocationCurve lc)
                                pt = (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) * 0.5;
                            else if (e.Location is LocationPoint lp)
                                pt = lp.Point;
                            else { skipped++; continue; }

                            var tag = IndependentTag.Create(doc, tagType.Id, view.Id,
                                new Reference(e), false, TagOrientation.Horizontal, pt);
                            if (tag != null) tagged++;
                        }
                        catch { skipped++; }
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to tag structural framing: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                tag_type_id = RevitCompat.GetId(tagType.Id),
                tagged,
                skipped,
                total_candidates = elements.Count
            });
        }
    }
}
```

> **Cross-version note:** `IndependentTag.Create` signature with `TagOrientation` is stable since R2018. The `Reference(e)` constructor exists in all R22-R27. If R22 build fails on a missing overload, fall back to `IndependentTag.Create(doc, view.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, pt)` guarded by `#if !REVIT2024_OR_GREATER`.

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.TagStructuralFramingHandler());
```

- [ ] **Step 3: MCP wrapper**

```csharp
[McpServerTool(Name = "tag_structural_framing"), System.ComponentModel.Description("Place structural framing tags on beams in the active or specified view. Params: view_id (optional, default active view), tag_type_id (optional, default first StructuralFramingTags), element_ids (optional, default all framing in view).")]
public static async Task<string> TagStructuralFraming(
    long? view_id = null, long? tag_type_id = null, long[] element_ids = null)
{
    try
    {
        var result = await ToolGateway.SendToRevit("tag_structural_framing", new { view_id, tag_type_id, element_ids });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build (across all 6 plugin csproj)**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

**If R22/R23 fails on `IndependentTag.Create` signature:** Apply the `#if !REVIT2024_OR_GREATER` fallback noted above, then rebuild.

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/TagStructuralFramingHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(structural): add tag_structural_framing handler"
```

---

### Task 12: Wave 14 snapshot regen + manual verification

**Files:**
- Modify: `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\Golden\tools-list.json`
- Modify: `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\Golden\tools-list-adaptive-bake.json`

> **Note:** Default snapshot does NOT enable `structural` (it's opt-in). The snapshot test should still pass without changes. We regenerate to lock-in any incidental ordering/description changes.

- [ ] **Step 1: Run snapshot test to see if any diff**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: PASS (no diff, because default snapshot doesn't include `structural`).
If FAIL: inspect the diff. Should only show new structural tools — but those should not appear if `structural` not in `DefaultOn`. If they do appear, investigate Program.cs wiring.

- [ ] **Step 2: Add explicit structural toolset snapshot test**

Open `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\ToolsListSnapshotTests.cs`. Find existing snapshot test method (e.g., `Default_Config_Snapshot`). Add a new sibling test:

```csharp
[Fact]
public void Structural_Toolset_Snapshot()
{
    var config = new RvtMcpConfig { Toolsets = new List<string> { "structural" } };
    CompareToolListAgainstGolden(config, "tools-list-structural.json");
}
```

(Match the exact helper name used in the existing test file — likely `CompareToolListAgainstGolden` or similar. Adjust accordingly.)

- [ ] **Step 3: Regenerate snapshots with new golden file**

```powershell
$env:UPDATE_SNAPSHOTS="1"
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
Remove-Item Env:UPDATE_SNAPSHOTS
```

Expected: PASS, new file created at `tests\RvtMcp.Tests\Golden\tools-list-structural.json` containing all 10 structural tools.

- [ ] **Step 4: Verify regenerated snapshot manually**

Open `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\Golden\tools-list-structural.json` and confirm it contains exactly these 10 tool names:

```
create_structural_column
create_structural_beam
create_structural_wall
create_foundation_isolated
create_foundation_wall
list_rebar
get_structural_loads
set_structural_load
analyze_structural_connections
tag_structural_framing
```

If any missing: rebuild + rerun snapshot regen.

- [ ] **Step 5: Run final snapshot test (now from clean state)**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: PASS (3+ snapshot tests, including new `Structural_Toolset_Snapshot`).

- [ ] **Step 6: Manual verification checklist (live Revit, optional but recommended before merging W14)**

Document this checklist in `D:\Projects\bimwright\rvt-mcp\docs\221-tools\wave-14-verification.md` (NEW file). Mark `(skipped)` next to items not run yet — they can be exercised later.

```markdown
# Wave 14 Manual Verification — Live Revit Smoke Test

Run server with `--toolsets structural`. Open a sample structural model (e.g., `samples/structural-skeleton.rvt`).

- [ ] create_structural_column at (0, 0, 0), level "Level 1": returns created_id; visible in 3D view.
- [ ] create_structural_beam from (0,0,3000) to (5000,0,3000), level "Level 2": returns created_id; visible joining columns.
- [ ] create_structural_wall from (0,0) to (5000,0), height_mm=3000: isStructural=true confirmed in Properties panel.
- [ ] create_foundation_isolated at (0,0,0) with host_column_id pointing to the column: footing appears under column.
- [ ] create_foundation_wall with wall_id of structural wall: WallFoundation appears under wall.
- [ ] list_rebar (without filters): returns count + total_matched; if model has no rebar, items=[].
- [ ] get_structural_loads (without filters): returns count of any point/line/area loads.
- [ ] set_structural_load action='update' on an existing load: changed_fields > 0; force value in Properties panel reflects change.
- [ ] set_structural_load action='create': returns status='not_implemented'.
- [ ] analyze_structural_connections: returns joined_count > 0 for elements that share joins.
- [ ] tag_structural_framing on a plan view with beams: tagged > 0; tags visible.

If any item fails, log to `docs/221-tools/wave-14-issues.md` and address before merging.
```

- [ ] **Step 7: Commit snapshots + verification doc**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add tests/RvtMcp.Tests/Golden/ tests/RvtMcp.Tests/ToolsListSnapshotTests.cs docs/221-tools/wave-14-verification.md
git -C D:\Projects\bimwright\rvt-mcp commit -m "test(structural): add structural toolset snapshot + manual verification checklist"
```

---

## WAVE 15 — FINAL FILL (8 handlers + setup)

### Task 13: Add `ServerState.IsReadOnly` accessor + read-only guard helper

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\server\ServerState.cs`
- Modify: `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs` (set ServerState.Config in startup)

- [ ] **Step 1: Create `ServerState.cs`**

File `D:\Projects\bimwright\rvt-mcp\src\server\ServerState.cs`:

```csharp
using RvtMcp.Plugin;
using Newtonsoft.Json;

namespace RvtMcp.Server
{
    /// <summary>
    /// Static config accessor shared by all MCP tool wrappers. Set once during startup
    /// in Program.Main. Wrappers query <see cref="IsReadOnly"/> to short-circuit
    /// write operations on toolsets that include both read and write tools (e.g., view).
    /// </summary>
    internal static class ServerState
    {
        public static RvtMcpConfig Config { get; set; }

        public static bool IsReadOnly => Config?.ReadOnlyOrDefault ?? false;

        /// <summary>
        /// Returns a JSON-serialized refusal payload if read-only mode is on, else null.
        /// Usage in wrappers: <c>var blocked = ServerState.BlockIfReadOnly("set_view_scale"); if (blocked != null) return blocked;</c>
        /// </summary>
        public static string BlockIfReadOnly(string toolName)
        {
            if (!IsReadOnly) return null;
            return JsonConvert.SerializeObject(new
            {
                error = "read_only_mode",
                tool = toolName,
                message = $"Tool '{toolName}' is disabled because the server is running with --read-only."
            }, Formatting.Indented);
        }
    }
}
```

- [ ] **Step 2: Wire up ServerState.Config in Program.Main**

In `D:\Projects\bimwright\rvt-mcp\src\server\Program.cs`, locate the line that loads config (`var config = RvtMcpConfig.Load(args);`, around line 43). Add IMMEDIATELY AFTER it:

```csharp
ServerState.Config = config;
```

- [ ] **Step 3: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/server/ServerState.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(server): add ServerState.IsReadOnly accessor for per-tool guard"
```

---

### Task 14: `set_project_info` (meta)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\SetProjectInfoHandler.cs`
- Modify: `CommandDispatcher.cs`, `Program.cs` (MetaTools)

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetProjectInfoHandler : IRevitCommand
    {
        public string Name => "set_project_info";
        public string Description => "Set typed fields on doc.ProjectInformation: name, number, client_name, address, status, issue_date. Skips read-only/missing parameters with structured warnings. At least one field required.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""number"":{""type"":""string""},""client_name"":{""type"":""string""},""address"":{""type"":""string""},""status"":{""type"":""string""},""issue_date"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var fields = new Dictionary<string, (BuiltInParameter bip, string value)>
            {
                ["name"]        = (BuiltInParameter.PROJECT_NAME,         req.Value<string>("name")),
                ["number"]      = (BuiltInParameter.PROJECT_NUMBER,       req.Value<string>("number")),
                ["client_name"] = (BuiltInParameter.CLIENT_NAME,          req.Value<string>("client_name")),
                ["address"]     = (BuiltInParameter.PROJECT_ADDRESS,      req.Value<string>("address")),
                ["status"]      = (BuiltInParameter.PROJECT_STATUS,       req.Value<string>("status")),
                ["issue_date"]  = (BuiltInParameter.PROJECT_ISSUE_DATE,   req.Value<string>("issue_date")),
            };

            var supplied = 0;
            foreach (var kv in fields) if (kv.Value.value != null) supplied++;
            if (supplied == 0)
                return CommandResult.Fail("At least one field must be supplied.");

            var pi = doc.ProjectInformation;
            if (pi == null) return CommandResult.Fail("doc.ProjectInformation is null.");

            var changed = new List<string>();
            var skipped = new List<object>();

            using (var tx = new Transaction(doc, "Bimwright: Set project info"))
            {
                tx.Start();
                try
                {
                    foreach (var kv in fields)
                    {
                        var key = kv.Key;
                        var (bip, value) = kv.Value;
                        if (value == null) continue;

                        var p = pi.get_Parameter(bip);
                        if (p == null) { skipped.Add(new { field = key, reason = "parameter_not_found" }); continue; }
                        if (p.IsReadOnly) { skipped.Add(new { field = key, reason = "read_only" }); continue; }

                        try
                        {
                            p.Set(value);
                            changed.Add(key);
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new { field = key, reason = "set_failed", detail = ex.Message });
                        }
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Transaction failed: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                project_info_id = RevitCompat.GetId(pi.Id),
                supplied,
                changed_fields = changed,
                skipped
            });
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.SetProjectInfoHandler());
```

- [ ] **Step 3: Add wrapper in `MetaTools` class**

Find `MetaTools` class in `Program.cs`. Add:

```csharp
[McpServerTool(Name = "set_project_info"), System.ComponentModel.Description("Set typed fields on doc.ProjectInformation. Params: name, number, client_name, address, status, issue_date (all optional, at least one required). Returns changed_fields and skipped reasons for read-only/missing parameters.")]
public static async Task<string> SetProjectInfo(
    string name = null, string number = null,
    string client_name = null, string address = null,
    string status = null, string issue_date = null)
{
    try
    {
        var result = await ToolGateway.SendToRevit("set_project_info", new {
            name, number, client_name, address, status, issue_date
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/SetProjectInfoHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(meta): add set_project_info handler (typed fields only)"
```

---

### Task 15: `get_model_warnings_summary` (lint, read-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\GetModelWarningsSummaryHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetModelWarningsSummaryHandler : IRevitCommand
    {
        public string Name => "get_model_warnings_summary";
        public string Description => "Return a grouped summary of doc.GetWarnings(): per warning type, count + optional example failing element ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""include_examples"":{""type"":""boolean"",""default"":true},""max_examples_per_type"":{""type"":""integer"",""default"":5}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var includeExamples = req.Value<bool?>("include_examples") ?? true;
            var maxExamples = req.Value<int?>("max_examples_per_type") ?? 5;

            var warnings = doc.GetWarnings();

            var grouped = warnings
                .GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    examples = includeExamples
                        ? g.Take(maxExamples).Select(w => new
                        {
                            failing_element_ids = w.GetFailingElements().Select(id => RevitCompat.GetId(id)).ToList()
                        }).ToList<object>()
                        : null
                })
                .ToList();

            return CommandResult.Ok(new
            {
                total_warnings = warnings.Count,
                unique_descriptions = grouped.Count,
                warnings = grouped
            });
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.GetModelWarningsSummaryHandler());
```

- [ ] **Step 3: Add wrapper in `LintTools` class**

```csharp
[McpServerTool(Name = "get_model_warnings_summary", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Group doc.GetWarnings() by description; return count, severity, and optional example failing element ids per group. Params: include_examples (default true), max_examples_per_type (default 5).")]
public static async Task<string> GetModelWarningsSummary(
    bool include_examples = true, int max_examples_per_type = 5)
{
    try
    {
        var result = await ToolGateway.SendToRevit("get_model_warnings_summary", new { include_examples, max_examples_per_type });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/GetModelWarningsSummaryHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(lint): add get_model_warnings_summary handler"
```

---

### Task 16: `purge_unused` (meta) — families only, with reference guard

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\PurgeUnusedHandler.cs`

> **Scope (MVP):** Only purges loadable `FamilySymbol` (types) where (a) zero placed instances AND (b) not referenced by tag families, schedules, or view filters. Param `targets` only accepts `["families"]` in this wave. `dry_run` default = true.

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class PurgeUnusedHandler : IRevitCommand
    {
        public string Name => "purge_unused";
        public string Description => "Conservative purge of unused loadable family symbols. MVP scope: targets=['families'] only. Skips symbols with any placed instance OR any reference from tags/schedules/view filters. dry_run defaults to true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""targets"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""families""]}},""dry_run"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var targets = (req["targets"] as JArray)?.Select(t => t.Value<string>()?.ToLowerInvariant()).ToHashSet()
                          ?? new HashSet<string> { "families" };
            var dryRun = req.Value<bool?>("dry_run") ?? true;
            var limit = req.Value<int?>("limit") ?? 500;

            if (targets.Any(t => t != "families"))
                return CommandResult.Fail("MVP: only targets=['families'] is supported in this wave.");

            // 1) Collect candidate FamilySymbols with 0 instances
            var allSymbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();

            var candidates = new List<FamilySymbol>();
            foreach (var sym in allSymbols)
            {
                bool hasInstance = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Any(fi => fi.GetTypeId() == sym.Id);
                if (!hasInstance) candidates.Add(sym);
            }

            // 2) Guard: drop candidates referenced by tags/schedules/view filters
            var referencedIds = CollectReferencedSymbolIds(doc);
            var safeToPurge = candidates.Where(s => !referencedIds.Contains(s.Id)).Take(limit).ToList();

            var report = safeToPurge.Select(s => new
            {
                id = RevitCompat.GetId(s.Id),
                name = s.Name,
                family = s.Family?.Name,
                category = s.Category?.Name
            }).ToList();

            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    total_candidates = candidates.Count,
                    safe_to_purge = report.Count,
                    skipped_due_to_references = candidates.Count - safeToPurge.Count,
                    items = report
                });
            }

            int deleted = 0;
            int failed = 0;
            using (var tx = new Transaction(doc, "Bimwright: Purge unused family symbols"))
            {
                tx.Start();
                try
                {
                    foreach (var s in safeToPurge)
                    {
                        try { doc.Delete(s.Id); deleted++; }
                        catch { failed++; }
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Purge transaction failed: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                dry_run = false,
                total_candidates = candidates.Count,
                attempted = safeToPurge.Count,
                deleted,
                failed,
                items = report
            });
        }

        private static HashSet<ElementId> CollectReferencedSymbolIds(Document doc)
        {
            var ids = new HashSet<ElementId>();

            // Tag families (tags reference categories, not symbol IDs — but tag types are themselves family symbols)
            // Schedules: skip MVP — conservative.
            // View filters: skip MVP — view filters reference categories, not symbols.
            // Nested family hosting: a symbol can host nested families; we check via Family.GetFamilySymbolIds().
            // For MVP we use FamilyInstance.Symbol references already covered above.

            // Additionally exclude: symbols used as door/window types embedded in walls (already covered by FamilyInstance check).
            // Exclude in-place families (Family.IsInPlace) — never purge those.
            var inPlaceSymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family?.IsInPlace == true)
                .Select(s => s.Id);
            foreach (var id in inPlaceSymbols) ids.Add(id);

            return ids;
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.PurgeUnusedHandler());
```

- [ ] **Step 3: Add wrapper in `MetaTools` class (with read-only guard)**

```csharp
[McpServerTool(Name = "purge_unused"), System.ComponentModel.Description("Conservative purge of unused loadable family symbols. MVP supports targets=['families'] only. Skips in-place families and symbols with any placed instance. dry_run defaults to true.")]
public static async Task<string> PurgeUnused(
    string[] targets = null, bool dry_run = true, int limit = 500)
{
    if (!dry_run)
    {
        var blocked = ServerState.BlockIfReadOnly("purge_unused");
        if (blocked != null) return blocked;
    }
    try
    {
        var result = await ToolGateway.SendToRevit("purge_unused", new { targets = targets ?? new[] { "families" }, dry_run, limit });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/PurgeUnusedHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(meta): add purge_unused handler (families only MVP, dry_run default)"
```

---

### Task 17: `capture_view_image` (view, with path safety)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\CaptureViewImageHandler.cs`

> **Path safety:** output_path must be absolute, fully qualified, not contain `..`, and resolve to either `%TEMP%`, `%LOCALAPPDATA%\RvtMcp\captures\`, or a user-configured sandbox directory. UNC paths rejected.

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CaptureViewImageHandler : IRevitCommand
    {
        public string Name => "capture_view_image";
        public string Description => "Export a view to a raster image (png/jpeg). output_path must be absolute and inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\captures\\. Returns saved path + pixel size.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""output_path"":{""type"":""string""},""pixel_size"":{""type"":""integer"",""default"":1600},""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png""}},""required"":[""output_path""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var outputPath = req.Value<string>("output_path");
            var pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            var imageFormat = (req.Value<string>("image_format") ?? "png").ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(outputPath))
                return CommandResult.Fail("output_path is required.");

            // Path safety
            var sandboxError = ValidateOutputPath(outputPath);
            if (sandboxError != null) return CommandResult.Fail(sandboxError);

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot export a view template.");

            var dir = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var options = new ImageExportOptions
            {
                FilePath = outputPath,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixelSize,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ShadowViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                doc.ExportImage(options);

                // ExportImage may append extension/view-name suffix; find actual saved file.
                var actualPath = FindActualOutput(outputPath, imageFormat);

                return CommandResult.Ok(new
                {
                    view_id = RevitCompat.GetId(view.Id),
                    saved_path = actualPath ?? outputPath,
                    pixel_size = pixelSize,
                    image_format = imageFormat
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"ExportImage failed: {ex.Message}");
            }
        }

        private static string ValidateOutputPath(string path)
        {
            if (path.StartsWith(@"\\")) return "UNC paths are not allowed.";
            if (path.Contains("..")) return "output_path cannot contain '..'.";
            try
            {
                var full = Path.GetFullPath(path);
                if (full != path) return $"output_path must be canonical. Did you mean: {full}";

                var temp = Path.GetFullPath(Path.GetTempPath());
                var bimwrightCaptures = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bimwright", "captures"));

                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return null;
                if (full.StartsWith(bimwrightCaptures, StringComparison.OrdinalIgnoreCase)) return null;

                return $"output_path must be inside %TEMP% ({temp}) or %LOCALAPPDATA%\\Bimwright\\captures\\ ({bimwrightCaptures}).";
            }
            catch (Exception ex)
            {
                return $"Invalid output_path: {ex.Message}";
            }
        }

        private static string FindActualOutput(string requestedPath, string ext)
        {
            var dir = Path.GetDirectoryName(requestedPath);
            var baseName = Path.GetFileNameWithoutExtension(requestedPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var candidates = Directory.GetFiles(dir, baseName + "*." + (ext == "jpeg" ? "jpg" : ext));
            return candidates.Length > 0 ? candidates[0] : null;
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.CaptureViewImageHandler());
```

- [ ] **Step 3: Add wrapper in `ViewTools` class (with read-only guard)**

```csharp
[McpServerTool(Name = "capture_view_image"), System.ComponentModel.Description("Export a view to a raster image. output_path must be absolute and inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\captures\\. Params: view_id (optional, default active), output_path (required), pixel_size (default 1600), image_format ('png'|'jpeg', default 'png').")]
public static async Task<string> CaptureViewImage(
    string output_path,
    long? view_id = null, int pixel_size = 1600, string image_format = "png")
{
    var blocked = ServerState.BlockIfReadOnly("capture_view_image");
    if (blocked != null) return blocked;
    try
    {
        var result = await ToolGateway.SendToRevit("capture_view_image", new {
            view_id, output_path, pixel_size, image_format
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/CaptureViewImageHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(view): add capture_view_image handler with path sandbox"
```

---

### Task 18: `set_view_crop` (view)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\SetViewCropHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewCropHandler : IRevitCommand
    {
        public string Name => "set_view_crop";
        public string Description => "Modify view crop box: toggle active/visible, set explicit bounds (mm), or fit to a list of element ids with optional padding_mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""enabled"":{""type"":""boolean""},""visible"":{""type"":""boolean""},""bounds"":{""type"":""object"",""properties"":{""min_x_mm"":{""type"":""number""},""min_y_mm"":{""type"":""number""},""min_z_mm"":{""type"":""number""},""max_x_mm"":{""type"":""number""},""max_y_mm"":{""type"":""number""},""max_z_mm"":{""type"":""number""}}},""fit_element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""padding_mm"":{""type"":""number"",""default"":100}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var enabled = req.Value<bool?>("enabled");
            var visible = req.Value<bool?>("visible");
            var boundsObj = req["bounds"] as JObject;
            var fitIdsToken = req["fit_element_ids"] as JArray;
            var paddingMm = req.Value<double?>("padding_mm") ?? 100;

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");

            using (var tx = new Transaction(doc, "Bimwright: Set view crop"))
            {
                tx.Start();
                try
                {
                    if (enabled.HasValue) view.CropBoxActive = enabled.Value;
                    if (visible.HasValue) view.CropBoxVisible = visible.Value;

                    BoundingBoxXYZ newBox = null;
                    if (boundsObj != null)
                    {
                        newBox = new BoundingBoxXYZ
                        {
                            Min = new XYZ(
                                (boundsObj.Value<double?>("min_x_mm") ?? 0) / 304.8,
                                (boundsObj.Value<double?>("min_y_mm") ?? 0) / 304.8,
                                (boundsObj.Value<double?>("min_z_mm") ?? 0) / 304.8),
                            Max = new XYZ(
                                (boundsObj.Value<double?>("max_x_mm") ?? 0) / 304.8,
                                (boundsObj.Value<double?>("max_y_mm") ?? 0) / 304.8,
                                (boundsObj.Value<double?>("max_z_mm") ?? 0) / 304.8)
                        };
                    }
                    else if (fitIdsToken != null && fitIdsToken.Any())
                    {
                        var ids = fitIdsToken.Select(t => RevitCompat.ToElementId(t.Value<long>())).ToList();
                        XYZ min = null, max = null;
                        foreach (var id in ids)
                        {
                            var bb = doc.GetElement(id)?.get_BoundingBox(view);
                            if (bb == null) continue;
                            min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                            max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
                        }
                        if (min != null && max != null)
                        {
                            var pad = paddingMm / 304.8;
                            newBox = new BoundingBoxXYZ
                            {
                                Min = new XYZ(min.X - pad, min.Y - pad, min.Z - pad),
                                Max = new XYZ(max.X + pad, max.Y + pad, max.Z + pad)
                            };
                        }
                    }

                    if (newBox != null) view.CropBox = newBox;

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        crop_active = view.CropBoxActive,
                        crop_visible = view.CropBoxVisible,
                        bounds_updated = newBox != null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view crop: {ex.Message}");
                }
            }
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.SetViewCropHandler());
```

- [ ] **Step 3: Add wrapper in `ViewTools` class (with read-only guard)**

```csharp
[McpServerTool(Name = "set_view_crop"), System.ComponentModel.Description("Modify view crop: enabled, visible, explicit bounds (mm), or fit_element_ids with padding_mm. Params: view_id (optional, default active), enabled, visible, bounds {min/max x/y/z mm}, fit_element_ids, padding_mm (default 100).")]
public static async Task<string> SetViewCrop(
    long? view_id = null, bool? enabled = null, bool? visible = null,
    string bounds_json = null, long[] fit_element_ids = null, double padding_mm = 100)
{
    var blocked = ServerState.BlockIfReadOnly("set_view_crop");
    if (blocked != null) return blocked;
    try
    {
        object boundsObj = null;
        if (!string.IsNullOrWhiteSpace(bounds_json))
            boundsObj = Newtonsoft.Json.Linq.JObject.Parse(bounds_json);

        var result = await ToolGateway.SendToRevit("set_view_crop", new {
            view_id, enabled, visible, bounds = boundsObj, fit_element_ids, padding_mm
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

> **Note on wrapper signature:** MCP wrappers cannot bind complex `bounds` object directly; accept a `bounds_json` string and parse server-side. Document this in the description above.

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/SetViewCropHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(view): add set_view_crop handler with fit-to-elements support"
```

---

### Task 19: `set_view_scale` (view)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\SetViewScaleHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewScaleHandler : IRevitCommand
    {
        public string Name => "set_view_scale";
        public string Description => "Set the graphical scale of a view. scale is the denominator (e.g., 50 for 1:50).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""scale"":{""type"":""integer""}},""required"":[""scale""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var scale = req.Value<int?>("scale");
            if (!scale.HasValue || scale.Value <= 0) return CommandResult.Fail("scale must be a positive integer.");

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            using (var tx = new Transaction(doc, "Bimwright: Set view scale"))
            {
                tx.Start();
                try
                {
                    var p = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                    if (p == null || p.IsReadOnly)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("View does not accept scale changes (VIEW_SCALE parameter not writable).");
                    }
                    var previous = view.Scale;
                    p.Set(scale.Value);
                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        previous_scale = previous,
                        new_scale = scale.Value
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view scale: {ex.Message}");
                }
            }
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.SetViewScaleHandler());
```

- [ ] **Step 3: Wrapper (with read-only guard)**

```csharp
[McpServerTool(Name = "set_view_scale"), System.ComponentModel.Description("Set the graphical scale denominator of a view (e.g., 50 for 1:50). Params: view_id (optional, default active), scale (required, positive integer).")]
public static async Task<string> SetViewScale(int scale, long? view_id = null)
{
    var blocked = ServerState.BlockIfReadOnly("set_view_scale");
    if (blocked != null) return blocked;
    try
    {
        var result = await ToolGateway.SendToRevit("set_view_scale", new { view_id, scale });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/SetViewScaleHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(view): add set_view_scale handler"
```

---

### Task 20: `activate_view` (view, UI-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\ActivateViewHandler.cs`

> **UI-only:** This handler modifies UIDocument.ActiveView. It does NOT modify document state, so no Transaction is required. The MCP wrapper still guards with read-only check for consistency (read-only mode typically means "don't change anything visible to user").

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ActivateViewHandler : IRevitCommand
    {
        public string Name => "activate_view";
        public string Description => "Set UIDocument.ActiveView. UI-only operation, no document Transaction. Resolves view by view_id or view_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""view_name"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var viewName = req.Value<string>("view_name");

            View view = null;
            if (viewIdParam.HasValue)
                view = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View;
            else if (!string.IsNullOrWhiteSpace(viewName))
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                       .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

            if (view == null) return CommandResult.Fail("Could not resolve view (provide view_id or view_name).");
            if (view.IsTemplate) return CommandResult.Fail("Cannot activate a view template.");

            try
            {
                uidoc.ActiveView = view;
                return CommandResult.Ok(new
                {
                    activated_view_id = RevitCompat.GetId(view.Id),
                    view_name = view.Name,
                    view_type = view.ViewType.ToString()
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to activate view: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.ActivateViewHandler());
```

- [ ] **Step 3: Wrapper (with read-only guard)**

```csharp
[McpServerTool(Name = "activate_view"), System.ComponentModel.Description("Set the active view in Revit UI. UI-only operation. Params: view_id OR view_name (required).")]
public static async Task<string> ActivateView(long? view_id = null, string view_name = null)
{
    var blocked = ServerState.BlockIfReadOnly("activate_view");
    if (blocked != null) return blocked;
    try
    {
        var result = await ToolGateway.SendToRevit("activate_view", new { view_id, view_name });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/ActivateViewHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(view): add activate_view UI-only handler"
```

---

### Task 21: `show_element_in_view` (view, UI-only)

**Files:**
- Create: `D:\Projects\bimwright\rvt-mcp\src\shared\Handlers\ShowElementInViewHandler.cs`

- [ ] **Step 1: Create handler file**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ShowElementInViewHandler : IRevitCommand
    {
        public string Name => "show_element_in_view";
        public string Description => "Activate view (optional), set element selection (optional), and zoom/show elements. UI-only, no document Transaction.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""view_id"":{""type"":""integer""},""activate_view"":{""type"":""boolean"",""default"":true},""select"":{""type"":""boolean"",""default"":true},""zoom"":{""type"":""boolean"",""default"":true}},""required"":[""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var idsToken = req["element_ids"] as JArray;
            if (idsToken == null || !idsToken.Any())
                return CommandResult.Fail("element_ids is required and must be non-empty.");

            var viewIdParam = req.Value<long?>("view_id");
            var doActivate = req.Value<bool?>("activate_view") ?? true;
            var doSelect = req.Value<bool?>("select") ?? true;
            var doZoom = req.Value<bool?>("zoom") ?? true;

            var ids = idsToken.Select(t => RevitCompat.ToElementId(t.Value<long>()))
                              .Where(id => doc.GetElement(id) != null).ToList();
            if (!ids.Any()) return CommandResult.Fail("No valid element ids resolved.");

            try
            {
                if (viewIdParam.HasValue && doActivate)
                {
                    var view = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View;
                    if (view != null && !view.IsTemplate) uidoc.ActiveView = view;
                }

                if (doSelect)
                    uidoc.Selection.SetElementIds(ids);

                if (doZoom)
                    uidoc.ShowElements(ids);

                return CommandResult.Ok(new
                {
                    element_count = ids.Count,
                    activated_view_id = uidoc.ActiveView != null ? (long?)RevitCompat.GetId(uidoc.ActiveView.Id) : null,
                    selected = doSelect,
                    zoomed = doZoom
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to show elements: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 2: Register**

```csharp
Register(new Handlers.ShowElementInViewHandler());
```

- [ ] **Step 3: Wrapper (with read-only guard)**

```csharp
[McpServerTool(Name = "show_element_in_view"), System.ComponentModel.Description("Activate view, select elements, zoom to elements. UI-only. Params: element_ids (required), view_id (optional), activate_view (default true), select (default true), zoom (default true).")]
public static async Task<string> ShowElementInView(
    long[] element_ids,
    long? view_id = null, bool activate_view = true, bool select = true, bool zoom = true)
{
    var blocked = ServerState.BlockIfReadOnly("show_element_in_view");
    if (blocked != null) return blocked;
    try
    {
        var result = await ToolGateway.SendToRevit("show_element_in_view", new {
            element_ids, view_id, activate_view, select, zoom
        });
        return JsonConvert.SerializeObject(result, Formatting.Indented);
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build D:\Projects\bimwright\rvt-mcp\src\RvtMcp.sln -c Debug
```

- [ ] **Step 5: Commit**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add src/shared/Handlers/ShowElementInViewHandler.cs src/shared/Infrastructure/CommandDispatcher.cs src/server/Program.cs
git -C D:\Projects\bimwright\rvt-mcp commit -m "feat(view): add show_element_in_view UI-only handler"
```

---

### Task 22: Wave 15 snapshot regen + manual verification

**Files:**
- Modify: `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\Golden\tools-list.json`
- Modify: `D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\Golden\tools-list-adaptive-bake.json`
- Create: `D:\Projects\bimwright\rvt-mcp\docs\221-tools\wave-15-verification.md`

> **Note:** Unlike W14 (opt-in `structural`), W15 adds tools to `meta`/`lint`/`view` which ARE in `DefaultOn`. So default snapshot WILL diff. Regenerate.

- [ ] **Step 1: Run snapshot test → expect FAIL with diff for new W15 tools**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: FAIL. Diff should list these 8 new tools (added to default snapshot):
```
set_project_info (meta)
get_model_warnings_summary (lint)
purge_unused (meta)
capture_view_image (view)
set_view_crop (view)
set_view_scale (view)
activate_view (view)
show_element_in_view (view)
```

If any tools missing from diff: investigate Program.cs wrapper registration in the corresponding toolset class.

- [ ] **Step 2: Regenerate snapshots**

```powershell
$env:UPDATE_SNAPSHOTS="1"
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
Remove-Item Env:UPDATE_SNAPSHOTS
```

Expected: PASS. Golden files updated.

- [ ] **Step 3: Inspect snapshot diff**

```powershell
git -C D:\Projects\bimwright\rvt-mcp diff tests/RvtMcp.Tests/Golden/tools-list.json
```

Expected: Only additions (no deletions). All 8 tool names present with correct schemas.

- [ ] **Step 4: Run snapshot test again to confirm green**

```powershell
dotnet test D:\Projects\bimwright\rvt-mcp\tests\RvtMcp.Tests\RvtMcp.Tests.csproj --filter "FullyQualifiedName~ToolsListSnapshotTests"
```

Expected: PASS.

- [ ] **Step 5: Read-only mode smoke test**

```powershell
# Start server with --read-only and verify view-write tools refuse
dotnet run --project D:\Projects\bimwright\rvt-mcp\src\server\RvtMcp.Server.csproj -- --read-only --target R26 2>&1 | Select-Object -First 5
```

(Or use MCP inspector / Claude Code to call `set_view_scale` against the server — expect `{ "error": "read_only_mode" }` response.)

- [ ] **Step 6: Create manual verification checklist**

File `D:\Projects\bimwright\rvt-mcp\docs\221-tools\wave-15-verification.md`:

```markdown
# Wave 15 Manual Verification — Live Revit Smoke Test

Run server with default toolsets. Open any sample model.

- [ ] set_project_info name="Test Project" number="P-001": changed_fields contains both; Properties → Manage → Project Info reflects values.
- [ ] get_model_warnings_summary: returns total_warnings; if model has no warnings, total_warnings=0.
- [ ] purge_unused dry_run=true: returns safe_to_purge count; no symbols deleted (verify via Browser).
- [ ] purge_unused dry_run=false: actually deletes symbols (be on a SCRATCH model only).
- [ ] capture_view_image output_path=%TEMP%\bimwright-test.png: file appears at given path.
- [ ] capture_view_image output_path=C:\Windows\test.png: returns error (sandbox).
- [ ] set_view_crop enabled=true: crop visible in view.
- [ ] set_view_crop fit_element_ids=[<wall id>] padding_mm=200: crop fits wall + padding.
- [ ] set_view_scale scale=50: view scale changes to 1:50.
- [ ] activate_view view_name="Level 1": active view switches.
- [ ] show_element_in_view element_ids=[<wall id>] zoom=true: Revit zooms to wall.

Read-only mode (start server with --read-only):
- [ ] set_view_scale returns { error: "read_only_mode" }.
- [ ] capture_view_image returns { error: "read_only_mode" }.
- [ ] get_model_warnings_summary still works (it's read-only).
- [ ] activate_view returns { error: "read_only_mode" }.

If any item fails, log to docs/221-tools/wave-15-issues.md.
```

- [ ] **Step 7: Commit snapshots + verification doc**

```powershell
git -C D:\Projects\bimwright\rvt-mcp add tests/RvtMcp.Tests/Golden/ docs/221-tools/wave-15-verification.md
git -C D:\Projects\bimwright\rvt-mcp commit -m "test(wave-15): regen snapshots + add manual verification checklist"
```

---

## Self-Review Checklist (before merging to main)

- [ ] **Spec coverage:** All 18 tools from `docs/221-tools/wave-14-structural.md` (10 of 12) and `docs/221-tools/wave-15-final-fill.md` (8 of 8) implemented. Rebar tools (2) deferred to Wave 16 with stub doc.
- [ ] **Build clean:** `dotnet build src/RvtMcp.sln -c Debug` succeeds across all 6 plugin csproj + server, 0 errors.
- [ ] **Snapshot tests pass:** `dotnet test --filter "FullyQualifiedName~ToolsListSnapshotTests"` green. Three snapshot files exist: `tools-list.json`, `tools-list-adaptive-bake.json`, `tools-list-structural.json`.
- [ ] **CommandDispatcher:** All 18 new handlers registered in constructor. No duplicate names (existing duplicate-check test passes).
- [ ] **ToolsetFilter:** `structural` in `KnownToolsets` + `WriteCapable`, NOT in `DefaultOn`. `view` unchanged (per-tool guard used instead).
- [ ] **Read-only mode:** All write wrappers in `view` toolset + `purge_unused` (when not dry_run) call `ServerState.BlockIfReadOnly`. Read-only tools (`list_rebar`, `get_structural_loads`, `analyze_structural_connections`, `get_model_warnings_summary`) do NOT have the guard.
- [ ] **Transaction naming:** All new handlers use `"Bimwright: <Action>"`.
- [ ] **DTO discipline:** No Revit API object serialized directly. All responses are anonymous DTOs with `RevitCompat.GetId` for element ids.
- [ ] **mm/feet conversion:** All public params in mm; conversion to feet at API boundary (divide by 304.8).
- [ ] **Type consistency:** `ResolveSymbol`, `ResolveLevel` helpers have the same signature shape across all column/beam/foundation handlers. (They are local to each handler — acceptable duplication for self-containment.)
- [ ] **Cross-version:** No new `#if REVIT2024_OR_GREATER` guards needed (all APIs used are available in R22-R27). If `IndependentTag.Create` fallback was needed in Task 11, that guard is present.
- [ ] **Path safety:** `capture_view_image` rejects UNC, `..`, non-canonical, and out-of-sandbox paths.
- [ ] **No placeholders:** Each step has actual code/commands; no "TBD" or "similar to above" without code.
- [ ] **Commits granular:** 22 commits (one per task), each green-buildable. No mixed concerns.
- [ ] **Verification docs:** `wave-14-verification.md` and `wave-15-verification.md` exist with checklists.

---

## Execution Notes for the Implementing Agent

1. **Each task is independent in commit history but cumulative in code.** Don't skip Task 1 (toolset bootstrap) or Task 13 (ServerState helper) — later tasks depend on them.
2. **If build fails on a specific Revit version (R22-R27):** check `#if REVIT2024_OR_GREATER` guards. The most likely culprits are `IndependentTag.Create`, `WallFoundation`, and load APIs.
3. **If a handler's `ParametersSchema` JSON has a syntax error**, the handler still compiles but MCP clients see a broken schema. Validate schema strings with `JObject.Parse(handler.ParametersSchema)` when in doubt (can be done in a one-off unit test if desired).
4. **Snapshot regen is idempotent.** If your local snapshot diverges from what you expect, run `UPDATE_SNAPSHOTS=1 dotnet test` and inspect the diff before committing.
5. **Live Revit verification is OPTIONAL per task** but REQUIRED at wave boundaries (Task 12, Task 22). If you can't run Revit, note that explicitly in the wave verification doc and defer testing to a reviewer.
6. **Rebar deferral:** Do NOT implement `create_rebar_set` or `create_rebar_stirrup` in this plan. They are Wave 16 scope.

---

**End of plan.**
