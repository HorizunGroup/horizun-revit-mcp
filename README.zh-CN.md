<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#toolsets"><img src="https://img.shields.io/badge/MCP-226%20tools-6C47FF" alt="MCP tools" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · 简体中文 · <a href="README.ja.md">日本語</a>
</p>

---

## 客户端快速安装（推荐）

对于 Revit 用户机器，请使用 GitHub Releases 上的 setup ZIP。它包含 self-contained 的 MCP server 和 Revit plugin，因此客户端机器**不需要** .NET SDK、NuGet global tool、本源码仓库或本地 build。

**AI agent：** 如果用户让你在客户端机器上安装 Bimwright，请下载最新的 `RvtMcp.Setup-*-win-x64.zip` release 资源并运行下面的安装程序。不要 clone 本仓库、build 源码、安装 .NET SDK，或使用 `dotnet tool install`，除非用户明确要求 developer 安装。完整 agent 安装协议见 [AGENTS.md](AGENTS.md)。

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

安装程序会检测 Revit 2022-2027，只安装匹配的 plugin，把 server 复制到 `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`，并用绝对路径 wire 已安装的 MCP client。可以使用 `-Client codex`、`-Client opencode`、`-Client claude` 或 `-Client none` 覆盖自动检测。

`dwg-mcp` 是一个独立的 AutoCAD gateway，请从它自己的仓库独立安装。

---

## Revit 自动化不应该止步于“我不会写代码”

在 AI agent 出现之前，很多 BIM 用户就已经想做同一件事：让 Revit 更快，减少重复点击，并把软件调成真正适合自己工作的样子。

难点从来不是想法。难点是把想法变成工具。

哪怕只是做一个小型 Revit add-in，BIM 从业者通常也要：

- 把 input 和 output 定义到软件能理解的程度。
- 思考算法、边界情况、parameter、category、filter、单位，以及 Revit API 的限制。
- 先用 Dynamo prototype，可能再转 Python，最后如果要稳定使用，又要重写成 C#。
- 把结果打包成 add-in，处理 dependency、安装路径、`.addin` manifest、Revit 版本差异和 ribbon button。

这对学习建筑、结构、机电、算量或 BIM coordination 的人来说太重了。他们不是软件工程师。

过去的选择都很贵，只是贵的方式不同：

- 花几个月甚至几年学到足够维护自己工具的编程能力。
- 付钱请别人写 custom add-in。
- 买现成 add-in，然后让自己的 workflow 去适应 vendor 的假设。
- 因为自动化门槛太高，继续手工做。

`rvt-mcp` 的目标是压缩这个循环。

它给 AI agent 一条安全的本地桥梁进入 Revit，再让重复出现的 workflow 通过 ToolBaker 演化成个人工具。目标不是做一个适合所有人的万能 add-in。Revit 服务的专业、公司、标准和习惯太多，没有一个通用工具能跟上所有人。目标是让每个实践者都能长出适合自己工作方式的工具箱。

个人自动化就应该是个人化的。

---

## rvt-mcp 是什么

`rvt-mcp` 是面向 Autodesk Revit 2022-2027 的本地 MCP gateway。

它由两部分组成：

- `RvtMcp.Server`：.NET 8 MCP server，由 Claude、Cursor、Codex、OpenCode、Cline、VS Code Copilot 或其他 stdio MCP client 启动。
- `RvtMcp.Plugin`：每个 Revit 年份一个 add-in shell，运行在 Revit 内部，并在 Revit UI thread 上执行命令。

Agent 说 MCP。Server 通过 localhost TCP 或 Named Pipe 和 plugin 通信。Plugin 和 Revit API 通信。

你的模型留在你的机器上。

---

## 为什么它重要

AI agent 让 BIM 用户可以描述意图，而不是手写代码。但只有意图还不够。Revit 自动化仍然需要一个 runtime，理解 transaction、parameter、单位、selection、model state、version drift、安全和 rollback。

`rvt-mcp` 就是这个 runtime。

它围绕四个原则设计：

- **Local first.** 不需要 cloud bridge。Revit、plugin、MCP server、logs 和 ToolBaker storage 都在用户机器上。
- **Reversible by default.** 会修改模型的 workflow 可以通过 `revit_batch_execute` 执行，把多个 command 包在一个 Revit `TransactionGroup` 里，一次 undo 可以回滚整个 batch。
- **Progressively exposed.** Toolsets 和 `--read-only` 控制 agent 能看到什么、能做什么。弱模型或窄任务不需要看到 destructive tools。
- **Personal over generic.** Adaptive ToolBaker 可以观察重复的本地 workflow，提出个人工具建议，并把 accepted tools 暴露给 MCP 和 Revit ribbon。

这不是 black-box demo，也不是 courseware。它是 Apache-2.0 的公开源码。所有 claim 都应该通过 build、test、运行和读源码来验证。

---

## ToolBaker 循环

大多数 Revit 自动化死在“好想法”和“可用 add-in”之间。

ToolBaker 是从 agent-assisted workflow 走向个人工具的路径：

1. 使用现有 MCP tools 在 Revit 里 query、create、lint、inspect 或 batch operations。
2. 当需要更高级的 automation 时，直接从默认 tool surface 调用 `revit_send_code_to_revit`。
3. 如果 adaptive bake 已启用，重复的本地 usage 会记录在 `%LOCALAPPDATA%\RvtMcp\` 下。
4. 重复 pattern 会变成 suggestion，可通过 `revit_list_bake_suggestions` 查看。
5. 你显式通过 `revit_accept_bake_suggestion` 接受 suggestion，包括 tool name、schema 和 output choice。
6. Accepted tools 可以通过 `revit_list_baked_tools` / `revit_run_baked_tool` 调用，也会进入 Revit ribbon runtime cache。

Adaptive bake 默认关闭。它适合希望用自己的本地使用数据塑造自己工具的人。

---

## 架构

```text
+---------------------------+
| AI Client                 |
| Claude / Cursor / Codex   |
+---------------------------+
              |
              | stdio MCP
              v
+---------------------------+
| RvtMcp.Server      |
| .NET 8 / C#               |
+---------------------------+
              |
               | TCP (2022–2024)
               | Named Pipe (2025–2027)
              v
+---------------------------+
| Plugin Shell              |
| thin add-in per Revit yr  |
+---------------------------+
              |
              | shared command core
              | from `src/shared/`
              v
+---------------------------+
| ExternalEvent Marshal     |
| execution -> Revit UI     |
+---------------------------+
              |
              v
+---------------------------+
| Revit API                 |
+---------------------------+
              |
              v
+---------------------------+
| Model / Transaction /     |
| Undo                      |
+---------------------------+
```

`rvt-mcp` 是完整 C# MCP stack。MCP server、按 Revit 年份拆分的 plugin shells、transport bridge、command handlers、DTO mapping 和 ToolBaker pipeline 都用 C# 编写，并使用官方 MCP C# SDK。

Revit 机器上没有 Node.js sidecar。

版本差异明确放在边界：每个 Revit 年份一个薄 plugin shell，共同 compile `src/shared/`。详见 [ARCHITECTURE.md](ARCHITECTURE.md)，包括 threading、transport、DTO 和 ToolBaker 细节。

---

## 当前状态

`rvt-mcp` 可用，但仍然年轻。

- Compile gate 覆盖 Revit 2022–2027 plugin shells。
- Unit tests 覆盖 pure .NET logic、tool-surface snapshots、ToolBaker storage/policy paths、config、logging、privacy 和 batching behavior。
- 2023–2026 有 core runtime coverage。
- Accepted ToolBaker list/run/ribbon path 在 2022、2026、2027 有 smoke evidence。
- Fresh-machine install testing 在 [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md) 跟踪。

请把它当作严肃的 open-source infrastructure：在自己的环境测试后，再用于 production models。

---

## 项目结构

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln         # Solution (server + 6 plugin shells)
│   ├── server/                   # RvtMcp.Server - .NET 8 global tool, stdio MCP
│   ├── shared/                   # 所有 plugin shell 共享的 source glob
│   │   ├── Handlers/             # 每个 Revit command handler 一个文件
│   │   ├── Commands/             # Revit ribbon commands
│   │   ├── ToolBaker/            # Baked-tool registry/runtime/policy
│   │   ├── Transport/            # TCP + Named Pipe abstraction
│   │   ├── Infrastructure/       # Dispatcher, schema validation, ExternalEvent marshal
│   │   └── Security/             # Auth token, redaction, secret masking
│   ├── plugin-r22/              # Revit 2022 shell - .NET 4.8, TCP
│   ├── plugin-r23/              # Revit 2023 shell - .NET 4.8, TCP
│   ├── plugin-r24/              # Revit 2024 shell - .NET 4.8, TCP
│   ├── plugin-r25/              # Revit 2025 shell - .NET 8, Named Pipe
│   ├── plugin-r26/              # Revit 2026 shell - .NET 8, Named Pipe
│   └── plugin-r27/              # Revit 2027 shell - .NET 10, Named Pipe
├── tests/                        # xUnit, tool snapshots, policy/privacy tests
├── benchmarks/                   # Weak-model accuracy harness
├── scripts/                      # install, uninstall, plugin ZIP staging
├── docs/                         # Architecture, roadmap, ToolBaker, testing notes
├── server.json                   # MCP registry manifest
├── smithery.yaml                 # Smithery directory manifest
├── AGENTS.md                     # Agent-led install guide for MCP clients
└── ARCHITECTURE.md               # Runtime architecture deep dive
```

六个 plugin shells 都从同一份 `src/shared/` compile。按年份的 `#if` 处理 Revit API drift，例如新版本中 `ElementId.IntegerValue` 迁移到 `.Value`。

---

## 安装

## 从 `Bimwright.Rvt.*` 迁移（v0.3.0 及更早版本）

v0.4.0 把 codebase 从 `Bimwright.Rvt.*` 重命名为 `RvtMcp.*`。GitHub 仓库（`bimwright/rvt-mcp`）和品牌不变；只有文件名、package ID 和 folder path 改变。

如果你安装了 v0.3.0 或更早版本：

1. **关闭所有正在运行的 Revit 实例。**
2. **运行迁移脚本：**
   ```powershell
   pwsh scripts/uninstall-old.ps1
   ```
   这会移除：
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\`（plugin DLLs）
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright.R<year>.addin`（manifests）
   - `%LOCALAPPDATA%\Bimwright\rvt\server\`（server install root）

   它会**保留** `%LOCALAPPDATA%\Bimwright\baked\`、`journal\`、`firm-profiles\` 和 `*.log` 文件——这些包含用户数据，会在 v0.4.0 首次启动时迁移到 `%LOCALAPPDATA%\RvtMcp\`。

3. **安装 v0.5.0：**
   ```powershell
   dotnet tool update -g Bimwright.Rvt.Server --version 0.3.0   # 先确保干净卸载
   dotnet tool uninstall -g Bimwright.Rvt.Server
   dotnet tool install -g RvtMcp.Server --version 0.5.0
   ```

4. **重新 wire 你的 MCP client。** 旧的 MCP entries `bimwright-rvt-r22`..`bimwright-rvt-r27` 会被 `install.ps1` 自动移除。新的 entry 是 `rvt-mcp`（单一，auto-detect Revit version）。

旧的 NuGet package `Bimwright.Rvt.Server` 在 0.3.0 被废弃，并带有指向 `RvtMcp.Server` 的 redirect note。

### 客户端 setup ZIP

从 [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest) 下载 `RvtMcp.Setup-v<version>-win-x64.zip`，解压后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf   # 预览将要改动的文件和 config
powershell -ExecutionPolicy Bypass -File .\install.ps1           # 安装 server、plugin 和检测到的 client config entries
```

常用的安装选项：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client codex      # 只 wire Codex
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client opencode   # 只 wire OpenCode
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client claude     # 如果有对应 config，wire Claude Code/Desktop
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client none       # 只安装文件，不改 MCP client config
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Years 2024        # 如果注册表检测不可用，强制指定 Revit 年份
```

典型的 MCP client config（使用安装后的绝对路径）：

```json
{
  "mcpServers": {
    "rvt-mcp": {
      "command": "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\<version>\\rvt-mcp.exe",
      "args": []
    }
  }
}
```

Setup ZIP 内含 self-contained 的 `rvt-mcp.exe`，因此客户端机器不需要 `.NET 8 SDK`、`dotnet tool install` 或本仓库。Config entries 使用已安装的绝对路径，所以不依赖 `%USERPROFILE%\.dotnet\tools` 和 PATH。安装程序为所有检测到的 Revit 年份部署 plugin，然后写入一个名为 `rvt-mcp` 的 auto-detect MCP entry。

### 验证

1. 打开 Revit 2022-2027 和一个 model。
2. 使用 BIMwright ribbon panel 来 start/toggle MCP plugin。
3. 在 MCP client 中运行 `tools/list`。
4. 调用 `revit_get_current_view_info`。

预期 response 形态：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

在 MCP client 能 list tools 并成功调用 Revit 之前，不要声称安装完成。

### 卸载

一次性移除 plugin、self-contained server、可能存在的 legacy .NET global tool、host-config entries、discovery files、logs 和 ToolBaker cache：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

Setup ZIP 也包含 `uninstall-all.ps1` 作为同一完整清理的别名。

### Developer / legacy 安装

开发者仍可以把 server 作为 NuGet .NET tool 安装，并使用仅-plugin 的 bundle：

```powershell
dotnet tool install -g RvtMcp.Server
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SourceDir . -Client none
```

这条路用于开发和向后兼容。客户端机器应使用 setup ZIP。

---

## 支持的 MCP clients

| Client | 状态 | 说明 |
|--------|------|------|
| Claude Code CLI | documented | project `.mcp.json` 或 global `~/.claude.json` |
| Claude Desktop | documented | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode | scripted | `install.ps1 -Client opencode` |
| Codex | scripted | `install.ps1 -Client codex` |
| Cursor | documented | project 或 user `mcp.json` |
| Cline (VS Code) | documented | Cline MCP settings JSON |
| VS Code Copilot | documented | native `servers` schema with `type: stdio` |
| Gemini CLI | documented | `gemini mcp add ...` 或 settings JSON |
| Antigravity | documented | Gemini/Antigravity MCP config JSON |

---

## Toolsets

完整 surface 为 **226 个 tools，分布在 23 个 toolsets**（`--toolsets all`）。默认除 `modify` 和 `delete` 外，所有 toolsets 均启用。启用 adaptive bake 后会额外加入 accepted baked tools；`--read-only` 会移除所有 write-capable toolsets。

默认启用 toolsets：`query`、`create`、`view`、`schedule`、`families`、`mep`、`graphics`、`export`、`toolbaker`、`meta`、`lint`、`sheets`、`materials`、`geometry`、`annotation`、`rooms`、`links`、`parameters`、`organization`、`workflows`、`structural`。

可选 toolsets（默认关闭）：`modify`、`delete`。需显式启用或通过 `--toolsets all`。

使用 `--toolsets query,create,modify,meta` 或 `--toolsets all` 启用。加上 `--read-only` 会移除 write-capable toolsets，无论它们是如何被请求的。

| Toolset | Tools | Default |
|---------|-------|---------|
| `query` | current view, selected elements, family types, material quantities, model stats, AI element filter | on |
| `create` | grid, level, room, line-based, point-based, surface-based element, group from elements | on |
| `view` | create view, sheet layout, place view on sheet, capture image, set crop/scale, activate view, show element | on |
| `meta` | `revit_show_message`, `revit_switch_target`, `revit_batch_execute`, usage stats, set project info, purge unused families | on |
| `lint` | view-naming pattern analysis, correction suggestions, firm-profile detect | on |
| `schedule` | list/inspect, fields/formulas/data/elements, create + add/update field, filter+sort | on |
| `families` | list loaded families, load/unload/replace family, export/list family types, duplicate/rename family type | on |
| `modify` | `revit_operate_element`, `revit_color_elements`, parameter/type/workset edits | off |
| `delete` | `revit_delete_element` | off |
| `annotation` | element/category 标注、文字注释、尺寸标注、filled region、detail line、callout、keynote、未标注/未尺寸检查、空 tag 清理 | on |
| `export` | `revit_export_room_data` | on |
| `mep` | `revit_detect_system_elements` | on |
| `graphics` | view filters (create/list/apply/remove), element graphic overrides, category visibility, view phase/visibility | on |
| `toolbaker` | accepted-tool list/run, send-code, adaptive suggestion lifecycle | on |
| `sheets` | sheet 创建、复制、占位符 sheet、列表 sheet、图纸标题栏参数设置、明细表放置、版本修订及关联、图纸重命名/重编号 | on |
| `materials` | 列表/创建/复制材质，设置外观/身份/结构/热力属性，材质工程量统计，分配材质到图元 | on |
| `geometry` | 图元包围盒、几何实体信息、测量图元间距、冲突碰撞检测、射线投射、体积和面积分析、图元形心位置、几何复杂度分析 | on |
| `rooms` | rooms、areas、spaces、边界、洞口、room separator、finishes、自动创建 rooms、area tag | on |
| `links` | Revit/CAD link 列表、CAD import/link、Revit link load/unload/reload、link elements、坐标、project base point | on |
| `parameters` | 创建 project/shared parameter、binding/unbinding、list/export shared params、按 GUID 设置值 | on |
| `organization` | saved selections（save/load/list/delete）、选择元素、view templates（list/apply/create-from-view/duplicate/delete） | on |
| `workflows` | clash review、data roundtrip、model audit、naming normalization、room documentation、sheet set、takeoff report、view cleanup | on |
| `structural` | structural columns/beams/walls/foundations、rebar set + stirrup、结构荷载、framing tags、连接分析 | on |

### 全部 tools

| Toolset | Tool | 描述 |
|---|---|---|
| `query` | `revit_get_current_view_info` | Active view metadata: type, level, scale, detail level. |
| `query` | `revit_get_selected_elements` | 当前选中 elements: id, name, category, type. |
| `query` | `revit_get_available_family_types` | Project 中的 family types，可按 category filter. |
| `query` | `revit_ai_element_filter` | 按 category 和 parameter/operator filter，数值单位为 mm. |
| `query` | `revit_analyze_model_statistics` | 按 category 统计 element 数量. |
| `query` | `revit_get_material_quantities` | 某 category 的 area 和 volume 汇总. |
| `query` | `revit_get_element_details` | Element metadata、location、bounding box、workset、phase、group 和 assembly ids. |
| `query` | `revit_get_element_parameters` | Instance parameters: storage type、display value、raw value 和 data/spec ids. |
| `query` | `revit_get_type_parameters` | 从 type ids 或 element ids 读取 type parameters. |
| `query` | `revit_list_project_parameters` | Project/shared parameter bindings、binding kind 和 categories. |
| `query` | `revit_get_element_relationships` | Host、group、assembly、owner view、design option、nesting 和 dependents. |
| `query` | `revit_list_groups` | Group instances with type、attached/detail metadata 和 optional member ids. |
| `query` | `revit_get_group_members` | Group instance members with category、type、owner view 和 pinned state. |
| `query` | `revit_list_assemblies` | Assembly instances with type、naming category、member count 和 optional member ids. |
| `query` | `revit_get_assembly_members` | Assembly instance members with category、type、group 和 workset ids. |
| `query` | `revit_list_worksets` | Worksets、active workset、edit/open state 和 optional element counts. |
| `create` | `revit_create_line_based_element` | Wall 或其他 line-based element. |
| `create` | `revit_create_point_based_element` | Door, window, furniture 或其他 point element. |
| `create` | `revit_create_surface_based_element` | 从 polyline 创建 floor 或 ceiling. |
| `create` | `revit_create_level` | 按 mm elevation 创建 level. |
| `create` | `revit_create_grid` | 按两个点创建 grid line，单位 mm. |
| `create` | `revit_create_room` | 在 point 创建 room，由 walls 围合. |
| `create` | `revit_create_group_from_elements` | 从两个或多个 elements 创建 group. |
| `modify` | `revit_operate_element` | Select, hide, unhide, isolate 或按 IDs set-color. |
| `modify` | `revit_color_elements` | 按 parameter value 给 category 上色. |
| `modify` | `revit_set_element_parameter_values` | 批量设置 elements 的 instance parameter. |
| `modify` | `revit_set_type_parameter_values` | 设置 type ids 或 element-resolved types 的 type parameter. |
| `modify` | `revit_change_element_type` | 将 elements 切换到兼容的 target type. |
| `modify` | `revit_assign_elements_to_workset` | 在 workshared model 中把 elements 分配到 user workset. |
| `delete` | `revit_delete_element` | 按 ID list 删除。除非明确需要，否则保持关闭. |
| `view` | `revit_create_view` | 创建 floor plan 或 3D view. |
| `view` | `revit_place_view_on_sheet` | 把 view 放到新 sheet 或现有 sheet 上. |
| `view` | `revit_analyze_sheet_layout` | Title block、viewport positions 和 scales，单位 mm. |
| `export` | `revit_export_room_data` | Rooms: name, number, area, perimeter, level, volume. |
| `annotation` | `revit_tag_all_walls` | 在 midpoint 打 wall-type tag，跳过已 tag 的 wall. |
| `annotation` | `revit_tag_all_rooms` | 在 location point 打 room tag，跳过已 tag 的 room. |
| `mep` | `revit_detect_system_elements` | 从 seed 沿 connectors traverse，返回 system members. |
| `toolbaker` | `revit_send_code_to_revit` | 从默认 tool surface 在 Revit 中 compile 并运行 ad-hoc C#. |
| `toolbaker` | `revit_list_baked_tools` | 列出已 accept 的 personal baked tools. |
| `toolbaker` | `revit_run_baked_tool` | 按名称调用 accepted baked tool. |
| `toolbaker` | `revit_list_bake_suggestions` | Adaptive bake only: 列出 local suggestions. |
| `toolbaker` | `revit_accept_bake_suggestion` | Adaptive bake only: accept 并 apply local suggestion. |
| `toolbaker` | `revit_dismiss_bake_suggestion` | Adaptive bake only: snooze 或 dismiss local suggestion. |
| `meta` | `revit_show_message` | Revit 内 TaskDialog，用于 connection test 或通知. |
| `meta` | `revit_switch_target` | 多个 Revit version 同时运行时切换 connection. |
| `meta` | `revit_batch_execute` | 在一个 `TransactionGroup` 中 atomically 执行 commands. |
| `meta` | `revit_analyze_usage_patterns` | Local usage stats: tool calls, sessions, errors. |
| `lint` | `revit_analyze_view_naming_patterns` | 推断 dominant view-naming pattern 和 outliers. |
| `lint` | `revit_suggest_view_name_corrections` | 为 view outliers 提出 corrected names. |
| `lint` | `revit_detect_firm_profile` | 根据 firm profiles fingerprint project naming. |

---

## Supported Revit Versions

| Revit | Target Framework | Transport | 备注 |
|-------|------------------|-----------|------|
| 2022 | .NET 4.8 | TCP | Accepted ToolBaker path smoke-tested |
| 2023 | .NET 4.8 | TCP | Core runtime coverage |
| 2024 | .NET 4.8 | TCP | Core runtime coverage |
| 2025 | .NET 8 (`net8.0-windows7.0`) | Named Pipe | Core runtime coverage |
| 2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe | Core runtime coverage; accepted ToolBaker path smoke-tested |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe | Accepted ToolBaker path smoke-tested |

不同 Revit 年份的 runtime behavior 仍可能不同，因为 Revit API 会变化。Custom baked C# tools 在跨版本测试前，应视为 version-sensitive。

---

## Security 和 Privacy

简短版：你的模型留在你的机器上。

- **默认 loopback。** TCP transport listen 在 `127.0.0.1`；Named Pipe scoped local-machine。
- **Per-session token handshake。** `%LOCALAPPDATA%\RvtMcp\` 下的 discovery files 包含 connection info 和 auth token。
- **Schema validation。** 错误 shape 的 tool call 会在 command handler 运行前被 reject。
- **Path masking。** 返回给 model 的 error 会 sanitize，避免泄露 absolute path。
- **ToolBaker controls。** `revit_send_code_to_revit` 默认可用。Adaptive bake 仍是 opt-in，只控制 suggestion/logging；`--read-only` 或 `--disable-toolbaker` 会移除 ToolBaker surface。
- **Local storage。** Usage events、bake database、logs 和 accepted-tool metadata 都在本地 Bimwright storage。

详见 [SECURITY.md](SECURITY.md) 的 threat model 和 vulnerability disclosure 流程。

---

## Configuration

三层配置，后者覆盖前者：JSON file、env vars、CLI args。

| Setting | CLI | Env | JSON key |
|---------|-----|-----|----------|
| Target Revit year | `--target 2023` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| Allow LAN bind | plugin-side only | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| Allow ToolBaker tools | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Enable adaptive bake suggestions | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send-code bodies | `--cache-send-code-bodies` / `--no-cache-send-code-bodies` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |

JSON file path: `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`。

---

## Development

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
```

Plugin projects 在 normal `Build` 后会 auto-deploy，复制到 `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`。Build plugin 前请关闭 Revit，因为 Revit 会锁住已加载 DLL。

为 release stage plugin ZIPs：

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

详见 [CONTRIBUTING.md](CONTRIBUTING.md)，包括 test strategy、tool-surface snapshot rules 和 contribution notes。

---

## 文档

- [AGENTS.md](AGENTS.md) - AI coding agents 的 install 和 MCP client wiring guide。
- [ARCHITECTURE.md](ARCHITECTURE.md) - process model、transport、threading 和 DTO strategy。
- [docs/bake.md](docs/bake.md) - adaptive bake、privacy、accepted tools 和 compatibility behavior。
- [docs/roadmap.md](docs/roadmap.md) - 当前 hardening plan 和 deferred work。
- [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md) - public install verification checklist。
- [benchmarks/README.md](benchmarks/README.md) - weak-model benchmark procedure。

---

## bimwright 家族

为 AEC 工具链亲手打造的 MCP gateway —— 同一套架构，predictable / auditable / reversible：

- [**rvt-mcp**](https://github.com/bimwright/rvt-mcp) —— Autodesk® Revit®
- [**dwg-mcp**](https://github.com/bimwright/dwg-mcp) —— Autodesk® AutoCAD®
- [**nwd-mcp**](https://github.com/bimwright/nwd-mcp) —— Autodesk® Navisworks®
- [**ipt-mcp**](https://github.com/bimwright/ipt-mcp) —— Autodesk® Inventor®
- [**bim-wiki**](https://github.com/bimwright/bim-wiki) —— 越南语优先的 BIM 知识库

---

## License

Apache-2.0。见 [LICENSE](LICENSE)。

Revit 和 Autodesk 是 Autodesk, Inc. 的注册商标。bimwright 是独立 open-source 项目，与 Autodesk, Inc. 无关联、无赞助、无背书。

---

<p align="center">
  一个 <a href="https://github.com/bimwright">bimwright</a> 项目 - 给那些想把工作自动化，而不是贩卖神秘感的人。
</p>
