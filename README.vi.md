<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#toolsets"><img src="https://img.shields.io/badge/MCP-175%20tools%20%7C%20178%20adaptive-6C47FF" alt="MCP tools" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · Tiếng Việt · <a href="README.zh-CN.md">简体中文</a>
</p>

---

## Cài nhanh cho client (khuyến nghị)

Với máy người dùng Revit, hãy dùng setup ZIP trên GitHub Releases. Gói này đã có MCP server self-contained và plugin Revit, nên máy client **không cần** .NET SDK, NuGet global tool, source repo hoặc build local.

**AI agent:** nếu user yêu cầu cài Bimwright trên máy client, hãy tải release asset mới nhất tên `Bimwright.Rvt.Setup-*-win-x64.zip` và chạy installer bên dưới. Không clone repo, không build source, không cài .NET SDK, và không dùng `dotnet tool install` trừ khi user yêu cầu cài kiểu developer. Xem [AGENTS.md](AGENTS.md) để biết protocol cài đặt đầy đủ cho agent.

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\Bimwright.Rvt.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\Bimwright.Rvt.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/Bimwright.Rvt.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

Installer tự detect Revit 2022-2027, chỉ cài plugin tương ứng, copy server vào `%LOCALAPPDATA%\Bimwright\rvt\server\<version>\`, và wire MCP client đã có bằng absolute path. Dùng `-Client codex`, `-Client opencode`, `-Client claude`, hoặc `-Client none` nếu muốn chỉ định rõ.

`dwg-mcp` chưa nằm trong setup RVT này; cài riêng khi nó có release client-ready.

---

## Tự động hóa Revit không nên dừng ở câu "tôi không biết code"

Trước khi có AI agent, rất nhiều BIM user đã muốn cùng một điều: làm Revit nhanh hơn, bớt click lặp lại, và chỉnh phần mềm theo đúng cách mình làm việc.

Khó khăn không nằm ở ý tưởng. Khó khăn là biến ý tưởng đó thành tool.

Để làm một Revit add-in nhỏ, người làm BIM thường phải:

- Xác định input và output đủ rõ để phần mềm hiểu được.
- Nghĩ thuật toán, edge case, parameter, category, filter, đơn vị, và các ràng buộc của Revit API.
- Prototype bằng Dynamo, có thể chuyển qua Python, rồi cuối cùng viết lại bằng C# nếu muốn workflow ổn định.
- Đóng gói thành add-in, xử lý dependency, đường dẫn cài đặt, file `.addin`, khác biệt giữa các phiên bản Revit, và ribbon button.

Đó là quá nhiều việc đối với người học kiến trúc, kết cấu, MEP, QS, BIM coordination - chứ không phải software engineering.

Các lựa chọn cũ đều tốn kém theo một cách nào đó:

- Mất nhiều tháng hoặc nhiều năm để học code đủ sâu rồi tự bảo trì tool.
- Thuê người viết custom add-in.
- Mua add-in có sẵn rồi chỉnh workflow của mình theo giả định của vendor.
- Tiếp tục làm thủ công vì đường tự động hóa quá nhiều ma sát.

`rvt-mcp` được tạo ra để rút ngắn vòng lặp đó.

Nó cho AI agent một cây cầu local an toàn để đi vào Revit, rồi để các workflow lặp lại tiến hóa thành tool cá nhân thông qua ToolBaker. Mục tiêu không phải tạo ra một add-in vạn năng cho mọi người. Revit phục vụ quá nhiều bộ môn, công ty, tiêu chuẩn và thói quen để có một tool chung theo nổi tất cả. Mục tiêu là một hệ thống để mỗi người có thể tự nuôi lớn bộ công cụ phù hợp với chính cách làm việc của mình.

Tự động hóa cá nhân thì nên mang tính cá nhân.

---

## rvt-mcp là gì

`rvt-mcp` là một MCP gateway local cho Autodesk Revit 2022-2027.

Nó gồm hai phần:

- `Bimwright.Rvt.Server`: MCP server .NET 8, được Claude, Cursor, Codex, OpenCode, Cline, VS Code Copilot hoặc MCP client khác launch qua stdio.
- `Bimwright.Rvt.Plugin`: Revit add-in shell cho từng năm Revit, chạy bên trong Revit và thực thi command trên Revit UI thread.

Agent nói chuyện bằng MCP. Server nói chuyện với plugin qua localhost TCP hoặc Named Pipe. Plugin nói chuyện với Revit API.

Model của bạn vẫn nằm trên máy bạn.

---

## Vì sao nó quan trọng

AI agent giúp BIM user mô tả ý định thay vì tự viết code. Nhưng chỉ có ý định thì chưa đủ. Tự động hóa Revit vẫn cần một runtime hiểu transaction, parameter, đơn vị, selection, trạng thái model, version drift, an toàn và rollback.

`rvt-mcp` là runtime đó.

Nó được thiết kế quanh bốn ý tưởng:

- **Local first.** Không cần cloud bridge. Revit, plugin, MCP server, logs và ToolBaker storage đều nằm trên máy người dùng.
- **Reversible by default.** Workflow có chỉnh model có thể chạy qua `batch_execute`, gom nhiều command vào một Revit `TransactionGroup` để một lần undo có thể rollback cả batch.
- **Progressively exposed.** Toolsets và `--read-only` kiểm soát agent được thấy và được làm gì. Agent yếu hoặc task hẹp không cần nhìn thấy destructive tools.
- **Personal over generic.** Adaptive ToolBaker có thể quan sát workflow local lặp lại, đề xuất tool cá nhân, và đưa tool đã accept vào MCP lẫn Revit ribbon.

Đây không phải black-box demo và không phải courseware. Đây là mã nguồn Apache-2.0 public. Claim nào cũng nên được kiểm chứng bằng build, test, chạy thử và đọc source.

---

## Vòng lặp ToolBaker

Phần lớn tự động hóa Revit chết ở khoảng giữa "ý tưởng hay" và "add-in dùng được".

ToolBaker là đường đi từ workflow có AI agent hỗ trợ đến tool cá nhân:

1. Dùng các MCP tool có sẵn để query, create, lint, inspect hoặc batch operation trong Revit.
2. Khi cần automation nâng cao, gọi trực tiếp `send_code_to_revit` từ tool surface mặc định.
3. Nếu adaptive bake được bật, usage lặp lại được ghi local dưới `%LOCALAPPDATA%\Bimwright\`.
4. Pattern lặp lại trở thành suggestion, xem qua `list_bake_suggestions`.
5. Bạn chủ động accept suggestion bằng `accept_bake_suggestion`, gồm tên tool, schema và output choice.
6. Tool đã accept có thể gọi qua `list_baked_tools` / `run_baked_tool` và xuất hiện trong Revit ribbon runtime cache.

Adaptive bake mặc định tắt. Nó dành cho người muốn dữ liệu sử dụng local của mình tự hình thành tool của riêng mình.

---

## Kiến trúc

```text
+---------------------------+
| AI Client                 |
| Claude / Cursor / Codex   |
+---------------------------+
              |
              | stdio MCP
              v
+---------------------------+
| Bimwright.Rvt.Server      |
| .NET 8 / C#               |
+---------------------------+
              |
              | TCP (R22-R24)
              | Named Pipe (R25-R27)
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

`rvt-mcp` là full C# MCP stack. MCP server, plugin shell theo từng năm Revit, transport bridge, command handler, DTO mapping và ToolBaker pipeline đều viết bằng C# với MCP C# SDK chính thức.

Không có Node.js sidecar trên máy Revit.

Phần tách version nằm rõ ở rìa: một plugin shell mỏng cho mỗi năm Revit, cùng compile chung `src/shared/`. Xem [ARCHITECTURE.md](ARCHITECTURE.md) để biết chi tiết threading, transport, DTO và ToolBaker.

---

## Trạng thái hiện tại

`rvt-mcp` dùng được nhưng vẫn còn trẻ.

- Compile gate bao phủ plugin shell Revit R22-R27.
- Unit test bao phủ logic pure .NET, tool-surface snapshot, ToolBaker storage/policy, config, logging, privacy và batch behavior.
- Core runtime coverage có cho R23-R26.
- Accepted ToolBaker list/run/ribbon path có smoke evidence trên R22, R26 và R27.
- Fresh-machine install testing được theo dõi tại [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md).

Hãy xem nó như hạ tầng open-source nghiêm túc: test trên môi trường của bạn trước khi tin dùng cho production model.

---

## Cấu trúc dự án

```text
rvt-mcp/
├── src/
│   ├── Bimwright.Rvt.sln         # Solution (server + 6 plugin shells)
│   ├── server/                   # Bimwright.Rvt.Server - stdio MCP server
│   ├── shared/                   # Source glob dùng chung cho mọi plugin shell
│   │   ├── Handlers/             # Một file cho mỗi Revit command handler
│   │   ├── Commands/             # Revit ribbon commands
│   │   ├── ToolBaker/            # Baked-tool registry/runtime/policy
│   │   ├── Transport/            # TCP + Named Pipe abstraction
│   │   ├── Infrastructure/       # Dispatcher, schema validation, ExternalEvent marshal
│   │   └── Security/             # Auth token, redaction, secret masking
│   ├── plugin-r22/               # Revit 2022 shell - .NET 4.8, TCP
│   ├── plugin-r23/               # Revit 2023 shell - .NET 4.8, TCP
│   ├── plugin-r24/               # Revit 2024 shell - .NET 4.8, TCP
│   ├── plugin-r25/               # Revit 2025 shell - .NET 8, Named Pipe
│   ├── plugin-r26/               # Revit 2026 shell - .NET 8, Named Pipe
│   └── plugin-r27/               # Revit 2027 shell - .NET 10, Named Pipe
├── tests/                        # xUnit, tool snapshots, policy/privacy tests
├── benchmarks/                   # Weak-model accuracy harness
├── scripts/                      # install, uninstall, plugin ZIP staging
├── docs/                         # Architecture, roadmap, ToolBaker, testing notes
├── server.json                   # MCP registry manifest
├── smithery.yaml                 # Smithery directory manifest
├── AGENTS.md                     # Agent-led install guide cho MCP clients
└── ARCHITECTURE.md               # Deep dive về runtime architecture
```

Sáu plugin shell compile từ cùng `src/shared/`. Các `#if` theo năm xử lý Revit API drift như `ElementId.IntegerValue` chuyển sang `.Value` ở các bản mới hơn.

---

## Cài đặt

### Setup ZIP cho client

Tải `Bimwright.Rvt.Setup-v<version>-win-x64.zip` từ [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest), giải nén, rồi chạy:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf   # xem trước file và config sẽ đổi
powershell -ExecutionPolicy Bypass -File .\install.ps1           # cài server, plugin, và wire client đã detect
```

Các tùy chọn hay dùng:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client codex      # chỉ wire Codex
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client opencode   # chỉ wire OpenCode
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client claude     # wire Claude Code/Desktop nếu có config
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client none       # chỉ cài file, không sửa MCP config
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Years 2024        # ép năm Revit nếu registry không detect được
```

Setup ZIP có sẵn `bimwright-rvt.exe` self-contained, nên máy client không cần `.NET 8 SDK`, `dotnet tool install`, hoặc repo này. Config MCP dùng absolute path đã cài, không phụ thuộc `%USERPROFILE%\.dotnet\tools` hay PATH. Installer deploy plugin cho mọi năm Revit detect được, rồi chỉ ghi một MCP entry auto-detect tên `bimwright-rvt`.

### Verify

1. Mở Revit 2022-2027 và một model.
2. Dùng BIMwright ribbon panel để start/toggle MCP plugin.
3. Trong MCP client, chạy `tools/list`.
4. Gọi `get_current_view_info`.

Response mẫu:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

Đừng xem install là xong nếu MCP client chưa list được tools và gọi Revit thành công.

### Gỡ cài đặt

Để gỡ plugin, self-contained server, legacy .NET global tool nếu có, host-config entries, discovery files, logs và ToolBaker cache:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

Setup ZIP cũng có `uninstall-all.ps1` như alias cho cùng một full sweep.

### Cài kiểu developer / legacy

Developer vẫn có thể cài server bằng NuGet .NET tool và dùng plugin-only bundle:

```powershell
dotnet tool install -g Bimwright.Rvt.Server
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SourceDir . -Client none
```

Đường này dành cho development và backward compatibility. Máy client nên dùng setup ZIP.

---

## MCP clients được hỗ trợ

| Client | Trạng thái | Ghi chú |
|--------|------------|---------|
| Claude Code CLI | documented | project `.mcp.json` hoặc global `~/.claude.json` |
| Claude Desktop | documented | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode | scripted | `install.ps1 -Client opencode` |
| Codex | scripted | `install.ps1 -Client codex` |
| Cursor | documented | project hoặc user `mcp.json` |
| Cline (VS Code) | documented | Cline MCP settings JSON |
| VS Code Copilot | documented | schema `servers` với `type: stdio` |
| Gemini CLI | documented | `gemini mcp add ...` hoặc settings JSON |
| Antigravity | documented | Gemini/Antigravity MCP config JSON |

---

## Toolsets

Surface không adaptive có 175 tools trên 19 toolsets. Khi bật adaptive bake, surface mở rộng thành 178 tools.

Toolsets bật mặc định: `query`, `create`, `view`, `schedule`, `families`, `mep`, `graphics`, `export`, `toolbaker`, `meta`, `lint`, `sheets`, `materials`, `geometry`, `annotation`, `rooms`, `links`.

Toolsets tùy chọn: `modify`, `delete`.

Bật bằng `--toolsets query,create,modify,meta` hoặc `--toolsets all`. Thêm `--read-only` để loại write-capable toolsets dù chúng được request bằng cách nào.

| Toolset | Tools | Default |
|---------|-------|---------|
| `query` | current view, selected elements, family types, material quantities, model stats, AI element filter | on |
| `create` | grid, level, room, line-based, point-based, surface-based element, group from elements | on |
| `view` | create view, sheet layout, place view on sheet | on |
| `meta` | `show_message`, `switch_target`, `batch_execute`, usage stats | on |
| `lint` | view-naming pattern analysis, correction suggestions, firm-profile detect | on |
| `schedule` | list/inspect, fields/formulas/data/elements, create + add/update field, filter+sort | on |
| `modify` | `operate_element`, `color_elements`, parameter/type/workset edits | off |
| `delete` | `delete_element` | off |
| `annotation` | tag element/category, text note, dimension, filled region, detail line, callout, keynote, kiểm tra untagged/undimensioned, dọn empty tags | on |
| `export` | `export_room_data` | on |
| `mep` | `detect_system_elements` | on |
| `toolbaker` | accepted-tool list/run, send-code, adaptive suggestion lifecycle | on |
| `sheets` | tạo sheet, duplicate, placeholder sheet, list sheets, titleblock parameters, đặt schedule, revisions, đánh số lại sheet | on |
| `materials` | list/tạo/duplicate material, properties của appearance/identity/structural/thermal, material takeoff, gán vào element | on |
| `geometry` | bounding box của element, geometry, đo khoảng cách, phát hiện va chạm (clash), raycast, tính volume/area, centroid, độ phức tạp | on |
| `rooms` | rooms, areas, spaces, boundaries, openings, room separators, finishes, auto-create rooms, tag areas | on |
| `links` | list Revit/CAD links, import/link CAD, link/unload/reload Revit model, link elements, coordinates, project base point | on |

### Tất cả tools

| Toolset | Tool | Mô tả |
|---|---|---|
| `query` | `get_current_view_info` | Metadata của active view: type, level, scale, detail level. |
| `query` | `get_selected_elements` | Element đang chọn với id, name, category, type. |
| `query` | `get_available_family_types` | Family types trong project, filter theo category. |
| `query` | `ai_element_filter` | Filter theo category và parameter/operator, giá trị tính bằng mm. |
| `query` | `analyze_model_statistics` | Đếm element theo category. |
| `query` | `get_material_quantities` | Tổng area và volume cho một category. |
| `query` | `get_element_details` | Metadata, location, bounding box, workset, phase, group và assembly ids. |
| `query` | `get_element_parameters` | Instance parameters với storage type, display value, raw value và data/spec ids. |
| `query` | `get_type_parameters` | Type parameters từ type ids hoặc từ element ids. |
| `query` | `list_project_parameters` | Project/shared parameter bindings, binding kind và categories. |
| `query` | `get_element_relationships` | Host, group, assembly, owner view, design option, nesting và dependents. |
| `query` | `list_groups` | Group instances với type, attached/detail metadata và optional member ids. |
| `query` | `get_group_members` | Members của group instance với category, type, owner view và pinned state. |
| `query` | `list_assemblies` | Assembly instances với type, naming category, member count và optional member ids. |
| `query` | `get_assembly_members` | Members của assembly instance với category, type, group và workset ids. |
| `query` | `list_worksets` | Worksets, active workset, edit/open state và optional element counts. |
| `create` | `create_line_based_element` | Wall hoặc element theo line. |
| `create` | `create_point_based_element` | Door, window, furniture hoặc point element khác. |
| `create` | `create_surface_based_element` | Floor hoặc ceiling từ polyline. |
| `create` | `create_level` | Level tại elevation tính bằng mm. |
| `create` | `create_grid` | Grid line giữa hai điểm tính bằng mm. |
| `create` | `create_room` | Room tại một điểm, được bao bởi wall. |
| `create` | `create_group_from_elements` | Tạo group từ hai hoặc nhiều elements. |
| `modify` | `operate_element` | Select, hide, unhide, isolate hoặc set-color theo IDs. |
| `modify` | `color_elements` | Tô màu category theo parameter value. |
| `modify` | `set_element_parameter_values` | Set instance parameter cho nhiều elements. |
| `modify` | `set_type_parameter_values` | Set type parameter cho type ids hoặc types suy ra từ elements. |
| `modify` | `change_element_type` | Đổi elements sang một target type compatible. |
| `modify` | `assign_elements_to_workset` | Gán elements vào user workset trong model workshared. |
| `delete` | `delete_element` | Delete theo danh sách ID. Chỉ bật khi thật sự cần. |
| `view` | `create_view` | Tạo floor plan hoặc 3D view. |
| `view` | `place_view_on_sheet` | Đặt view lên sheet mới hoặc sheet có sẵn. |
| `view` | `analyze_sheet_layout` | Title block, viewport positions và scales tính bằng mm. |
| `export` | `export_room_data` | Room data: name, number, area, perimeter, level, volume. |
| `annotation` | `tag_all_walls` | Tag wall-type tại midpoint; bỏ qua wall đã tag. |
| `annotation` | `tag_all_rooms` | Room tag tại location point; bỏ qua room đã tag. |
| `mep` | `detect_system_elements` | Traverse connector từ seed và trả về system members. |
| `toolbaker` | `send_code_to_revit` | Compile và chạy C# ad-hoc trong Revit từ tool surface mặc định. |
| `toolbaker` | `list_baked_tools` | List personal baked tools đã accept. |
| `toolbaker` | `run_baked_tool` | Gọi accepted baked tool theo tên. |
| `toolbaker` | `list_bake_suggestions` | Adaptive bake only: list local suggestions. |
| `toolbaker` | `accept_bake_suggestion` | Adaptive bake only: accept và apply local suggestion. |
| `toolbaker` | `dismiss_bake_suggestion` | Adaptive bake only: snooze hoặc dismiss local suggestion. |
| `meta` | `show_message` | TaskDialog trong Revit để test connection hoặc thông báo. |
| `meta` | `switch_target` | Đổi Revit connection khi chạy nhiều version. |
| `meta` | `batch_execute` | Chạy commands atomically trong một `TransactionGroup`. |
| `meta` | `analyze_usage_patterns` | Local usage stats: tool calls, sessions, errors. |
| `lint` | `analyze_view_naming_patterns` | Infer dominant view-naming pattern và outliers. |
| `lint` | `suggest_view_name_corrections` | Đề xuất tên đúng cho view outliers. |
| `lint` | `detect_firm_profile` | Fingerprint project naming theo firm profiles. |

---

## Supported Revit Versions

| Revit | Target Framework | Transport | Ghi chú |
|-------|------------------|-----------|---------|
| 2022 | .NET 4.8 | TCP | Accepted ToolBaker path đã smoke-test |
| 2023 | .NET 4.8 | TCP | Core runtime coverage |
| 2024 | .NET 4.8 | TCP | Core runtime coverage |
| 2025 | .NET 8 (`net8.0-windows7.0`) | Named Pipe | Core runtime coverage |
| 2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe | Core runtime coverage; accepted ToolBaker path đã smoke-test |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe | Accepted ToolBaker path đã smoke-test |

Runtime vẫn có thể khác nhau giữa các năm Revit vì Revit API thay đổi. Custom baked C# tools nên được xem là version-sensitive nếu chưa test qua các năm target.

---

## Security và Privacy

Ngắn gọn: model của bạn ở lại trên máy bạn.

- **Loopback mặc định.** TCP transport listen trên `127.0.0.1`; Named Pipe scoped local-machine.
- **Per-session token handshake.** Discovery files dưới `%LOCALAPPDATA%\Bimwright\` chứa connection info và auth token.
- **Schema validation.** Tool call sai shape bị reject trước khi command handler chạy.
- **Path masking.** Error trả về model được sanitize để tránh leak absolute path.
- **ToolBaker controls.** `send_code_to_revit` có sẵn mặc định. Adaptive bake vẫn là opt-in và chỉ điều khiển suggestion/logging; `--read-only` hoặc `--disable-toolbaker` sẽ bỏ ToolBaker surface.
- **Local storage.** Usage events, bake database, logs và accepted-tool metadata nằm trong local Bimwright storage.

Xem [SECURITY.md](SECURITY.md) để biết threat model và cách báo cáo vulnerability.

---

## Configuration

Ba lớp, lớp sau thắng lớp trước: JSON file, env vars, CLI args.

| Setting | CLI | Env | JSON key |
|---------|-----|-----|----------|
| Target Revit year | `--target R23` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| Allow LAN bind | plugin-side only | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| Allow ToolBaker tools | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Enable adaptive bake suggestions | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send-code bodies | `--cache-send-code-bodies` / `--no-cache-send-code-bodies` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |

JSON file path: `%LOCALAPPDATA%\Bimwright\bimwright.config.json`.

---

## Development

```bash
dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj
dotnet build src/server/Bimwright.Rvt.Server.csproj -c Release
dotnet build src/plugin-r26/Bimwright.Rvt.Plugin.R26.csproj -c Release
```

Plugin projects auto-deploy sau normal `Build`, copy vào `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\`. Hãy đóng Revit trước khi build plugin vì Revit lock DLL đã load.

Stage plugin ZIPs cho release:

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Xem [CONTRIBUTING.md](CONTRIBUTING.md) để biết test strategy, tool-surface snapshot rules và contribution notes.

---

## Tài liệu

- [AGENTS.md](AGENTS.md) - hướng dẫn install và wire MCP client cho AI coding agents.
- [ARCHITECTURE.md](ARCHITECTURE.md) - process model, transport, threading và DTO strategy.
- [docs/bake.md](docs/bake.md) - adaptive bake, privacy, accepted tools và compatibility behavior.
- [docs/roadmap.md](docs/roadmap.md) - hardening plan hiện tại và deferred work.
- [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md) - checklist verify public install.
- [benchmarks/README.md](benchmarks/README.md) - weak-model benchmark procedure.

---

## License

Apache-2.0. Xem [LICENSE](LICENSE).

Revit và Autodesk là thương hiệu đã đăng ký của Autodesk, Inc. bimwright là dự án open-source độc lập, không liên kết, không được tài trợ và không được bảo chứng bởi Autodesk, Inc.

---

<p align="center">
  Một dự án <a href="https://github.com/bimwright">bimwright</a> - dành cho người muốn tự động hóa công việc thay vì bán sự huyền bí.
</p>
