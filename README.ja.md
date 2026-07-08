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
  English · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a>
</p>

---

## クイックインストール（推奨）

Revit ユーザーマシンには、GitHub Releases のセットアップ ZIP を使用してください。セルフコンテナ型の MCP サーバと Revit プラグインが含まれているため、クライアントマシンに .NET SDK、NuGet グローバルツール、このソースリポジトリ、またはローカルビルドは **不要** です。

**AI エージェントへ:** ユーザーがクライアントマシンに Bimwright をインストールするよう依頼した場合、最新の `RvtMcp.Setup-*-win-x64.zip` リリースアセットをダウンロードし、以下のインストーラを実行してください。ユーザーが明示的に開発者インストールを要求しない限り、このリポジトリをクローンしたり、ソースをビルドしたり、.NET SDK をインストールしたり、`dotnet tool install` を使用したりしないでください。完全なエージェントインストール手順については [AGENTS.md](AGENTS.md) を参照してください。

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

インストーラは Revit 2022-2027 を検出し、該当するプラグインのみをインストールし、サーバを `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\` にコピーし、インストール済みの MCP クライアントを絶対パスで構成します。自動検出を上書きするには `-Client codex`、`-Client opencode`、`-Client claude`、または `-Client none` を使用してください。

`dwg-mcp` は AutoCAD 用の独立したゲートウェイです。自身のリポジトリから個別にインストールしてください。

---

## Revit 自動化は「コードが書けない」で止まるべきではない

AI エージェント以前から、多くの BIM ユーザーは同じことを望んでいました。Revit をより速くし、反復作業を減らし、ソフトウェアを実際のワークフローに合わせたいと願っていたのです。

難しいのはアイデアではありません。アイデアを道具に変えることこそが難しいのです。

たとえ小さな Revit アドインを作るにしても、実務者は通常以下の作業を必要とします：

- 入出力をソフトウェアが処理できる形で明確に定義する。
- アルゴリズム、エッジケース、パラメータ、カテゴリ、フィルタ、単位、Revit API の制約を考慮する。
- Dynamo でプロトタイプを作り、Python に移行し、ワークフローが安定したら最終的に C# で書き直す。
- 結果をアドインとしてパッケージ化し、依存関係、インストールパス、`.addin` マニフェスト、Revit バージョンの変動、リボンボタンを処理する。

建築、構造、MEP、積算、BIM コーディネーションを学んできた人にとっては、これは膨大な作業です。

通常の選択肢はいずれも、別の形でコストがかかります：

- 自分のツールを維持できるようになるまで何ヶ月もかけてコーディングを学ぶ。
- カスタムアドインの作成を誰かに依頼する（外注する）。
- 既製のアドインを購入し、ベンダーの前提に合わせてワークフローを適応させる。
- 自動化への障壁が高すぎるため、手作業を続ける。

`rvt-mcp` はこのループを短縮するために存在します。

AI エージェントに Revit への安全なローカルブリッジを提供すると同時に、ToolBaker を通じて繰り返し行うワークフローをパーソナルツールへと進化させます。目標は、万人向けの汎用アドインを作ることではありません。Revit はあまりにも多くの専門分野、オフィス、標準、習慣に対応しているため、それは非現実的です。目標は、各実務者が自分の仕事に合ったツールキットを育てられるシステムです。

パーソナルな自動化は、パーソナルであるべきです。

---

## rvt-mcp とは

`rvt-mcp` は Autodesk Revit 2022-2027 向けのローカル MCP ゲートウェイです。

2 つの部分から構成されます：

- `RvtMcp.Server`: Claude、Cursor、Codex、OpenCode、Cline、VS Code Copilot、またはその他の stdio MCP クライアントによって起動される .NET 8 MCP サーバ。
- `RvtMcp.Plugin`: サポート対象の各 Revit バージョン向けの Revit アドインシェル。Revit 内部で動作し、Revit UI スレッド上でコマンドを実行します。

エージェントは MCP で通信します。サーバは localhost TCP または Named Pipe を介してプラグインと通信します。プラグインは Revit API と通信します。

モデルは自分のマシン上に残ります。

---

## なぜ重要か

AI エージェントにより、BIM ユーザーは手作業でコードを書く代わりに意図を記述できるようになりました。しかし、意図だけでは不十分です。Revit の自動化には、トランザクション、パラメータ、単位、選択、モデル状態、バージョンの変動、安全性、ロールバックを理解するランタイムが依然として必要です。

`rvt-mcp` はそのランタイムです。

4 つの理念に基づいて設計されています：

- **ローカルファースト。** クラウドブリッジは不要です。Revit、プラグイン、MCP サーバ、ログ、ToolBaker ストレージはすべてユーザーのマシン上に存在します。
- **デフォルトでリバーシブル。** 変更を伴うワークフローは `batch_execute` を通じて実行でき、複数のコマンドを 1 つの Revit `TransactionGroup` にラップすることで、1 回の元に戻す操作でバッチ全体をロールバックできます。
- **段階的に公開。** ツールセットと `--read-only` モードにより、エージェントが表示・実行できる内容を制御します。性能の低いエージェントや限定的なエージェントに破壊的なツールは必要ありません。
- **汎用ではなく個人用に。** アダプティブ ToolBaker は繰り返し行われるローカルワークフローを観察し、パーソナルツールを提案し、承認されたツールを MCP および Revit リボンから利用可能にします。

これはブラックボックスのデモでも、コースウェアでもありません。公開された Apache-2.0 のコードです。主張は、ビルド、テスト、実行、ソースの読解によって検証されるべきです。

---

## ToolBaker のループ

Revit 自動化の多くは、「良いアイデア」から「使えるアドイン」に至る前に頓挫します。

ToolBaker は、エージェント支援ワークフローからパーソナルツールへの道筋です：

1. 既存の MCP ツールを使用して、Revit 内での照会、作成、リント、検査、バッチ操作を行います。
2. 高度な自動化が必要な場合は、デフォルトのツールサーフェスから直接 `send_code_to_revit` を呼び出します。
3. アダプティブベイクが有効な場合、繰り返し行われるローカル使用パターンが `%LOCALAPPDATA%\RvtMcp\` の下にローカル記録されます。
4. 繰り返しパターンが提案として表示され、`list_bake_suggestions` で確認できます。
5. ツール名、スキーマ、出力先を指定して、`accept_bake_suggestion` で明示的に提案を承認します。
6. 承認されたツールは `list_baked_tools` / `run_baked_tool` で呼び出し可能になり、Revit リボンランタイムキャッシュからも利用できます。

アダプティブベイクはデフォルトでオフです。自分のローカル使用データを自分のツール形成に活用したいユーザーのための機能です。

---

## アーキテクチャ

```text
+---------------------------+
| AI クライアント           |
| Claude / Cursor / Codex   |
+---------------------------+
              |
              | stdio MCP
              v
+---------------------------+
| RvtMcp.Server             |
| .NET 8 / C#               |
+---------------------------+
              |
              | TCP (2022–2024)
              | Named Pipe (2025–2027)
              v
+---------------------------+
| プラグインシェル          |
| Revit バージョン別の      |
| 薄いアドイン              |
+---------------------------+
              |
              | 共有コマンドコア
              | `src/shared/` より
              v
+---------------------------+
| ExternalEvent マーシャラ  |
| 実行 -> Revit UI スレッド |
+---------------------------+
              |
              v
+---------------------------+
| Revit API                 |
+---------------------------+
              |
              v
+---------------------------+
| モデル / トランザクション |
| / 元に戻す                |
+---------------------------+
```

`rvt-mcp` は完全な C# MCP スタックです。MCP サーバ、バージョン別 Revit プラグインシェル、トランスポートブリッジ、コマンドハンドラ、DTO マッピング、ToolBaker パイプラインはすべて、公式 MCP C# SDK を使用して C# で記述されています。

Revit マシン上に Node.js サイドカーは必要ありません。

バージョンの分割はエッジで明示的に行われます。各 Revit バージョンに対して 1 つの薄いプラグインシェルがあり、すべてが同じ `src/shared/` ソースグロブをコンパイルします。スレッド、トランスポート、DTO、ToolBaker の詳細については [ARCHITECTURE.md](ARCHITECTURE.md) を参照してください。

---

## 現在の状態

`rvt-mcp` は使用可能ですが、まだ開発初期段階にあります。

- コンパイルゲートは Revit 2022–2027 のプラグインシェルをカバーしています。
- 単体テストは、純粋な .NET ロジック、ツールサーフェススナップショット、ToolBaker のストレージ/ポリシーパス、設定、ログ、プライバシー、バッチ動作をカバーしています。
- コアランタイムのカバレッジは 2023–2026 に対して存在します。
- 承認済み ToolBaker のリスト/実行/リボンパスは 2022、2026、2027 でスモークテスト済みです。
- 新規マシンインストールのテストは [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md) で追跡されています。

本格的なオープンソースインフラストラクチャとして扱ってください。プロダクションモデルで信頼する前に、自身の環境でテストしてください。

---

## プロジェクト構造

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln         # ソリューション（サーバ + 6 プラグインシェル）
│   ├── server/                   # RvtMcp.Server — stdio MCP サーバ
│   ├── shared/                   # 全プラグインシェルで共有するソースグロブ
│   │   ├── Handlers/             # Revit コマンドハンドラ（ファイル単位）
│   │   ├── Commands/             # Revit リボンコマンド
│   │   ├── ToolBaker/            # ベイクドツールのレジストリ/ランタイム/ポリシー
│   │   ├── Transport/            # TCP + Named Pipe 抽象化
│   │   ├── Infrastructure/       # ディスパッチャ、スキーマ検証、ExternalEvent マーシャラ
│   │   └── Security/             # 認証トークン、編集、シークレットマスキング
│   ├── plugin-2022/              # Revit 2022 シェル — .NET 4.8、TCP
│   ├── plugin-2023/              # Revit 2023 シェル — .NET 4.8、TCP
│   ├── plugin-2024/              # Revit 2024 シェル — .NET 4.8、TCP
│   ├── plugin-2025/              # Revit 2025 シェル — .NET 8、Named Pipe
│   ├── plugin-2026/              # Revit 2026 シェル — .NET 8、Named Pipe
│   └── plugin-2027/              # Revit 2027 シェル — .NET 10、Named Pipe
├── tests/                        # xUnit、ツールスナップショット、ポリシー/プライバシーテスト
├── benchmarks/                   # 弱モデル精度ベンチマーク
├── scripts/                      # インストール、アンインストール、プラグイン ZIP ステージング
├── docs/                         # アーキテクチャ、ロードマップ、ToolBaker、テストノート
├── server.json                   # MCP レジストリマニフェスト
├── smithery.yaml                 # Smithery ディレクトリマニフェスト
├── AGENTS.md                     # MCP クライアント向けエージェント主導インストールガイド
└── ARCHITECTURE.md               # ランタイムアーキテクチャの詳細
```

6 つのプラグインシェルすべてが同じ `src/shared/` グロブからコンパイルされます。バージョン固有の `#if` ディレクティブにより、新しいバージョンで `ElementId.IntegerValue` が `.Value` に変更されるような Revit API の変動を処理します。

---

## インストール

## `Bimwright.Rvt.*`（v0.3.0 以前）からの移行

v0.4.0 ではコードベースの名前が `Bimwright.Rvt.*` から `RvtMcp.*` に変更されました。GitHub リポジトリ（`bimwright/rvt-mcp`）とブランド名は変わりません。ファイル名、パッケージ ID、フォルダパスのみが変更されます。

v0.3.0 以前をインストールしている場合：

1. **実行中のすべての Revit インスタンスを閉じてください。**
2. **移行スクリプトを実行します：**
   ```powershell
   pwsh scripts/uninstall-old.ps1
   ```
   これにより以下が削除されます：
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\`（プラグイン DLL）
   - `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright.R<year>.addin`（マニフェスト）
   - `%LOCALAPPDATA%\Bimwright\rvt\server\`（サーバインストールルート）

   `%LOCALAPPDATA%\Bimwright\baked\`、`journal\`、`firm-profiles\`、および `*.log` ファイルは **保持** されます — これらにはユーザーデータが含まれており、v0.4.0 の初回起動時に `%LOCALAPPDATA%\RvtMcp\` に移行されます。

3. **v0.5.0 をインストール：**
   ```powershell
   dotnet tool update -g Bimwright.Rvt.Server --version 0.3.0   # まず確実にアンインストール
   dotnet tool uninstall -g Bimwright.Rvt.Server
   dotnet tool install -g RvtMcp.Server --version 0.5.0
   ```

4. **MCP クライアントを再設定します。** 古い MCP エントリ `bimwright-rvt-r22`..`bimwright-rvt-r27` は `install.ps1` により自動的に削除されます。新しいエントリは `rvt-mcp`（単一、Revit バージョンを自動検出）です。

古い NuGet パッケージ `Bimwright.Rvt.Server` は 0.3.0 で非推奨となり、`RvtMcp.Server` を指すリダイレクトノートが付与されています。

### クライアントセットアップ ZIP

[GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest) から `RvtMcp.Setup-v<version>-win-x64.zip` をダウンロードし、展開した後、以下を実行します：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf   # ファイルと設定の変更をプレビュー
powershell -ExecutionPolicy Bypass -File .\install.ps1           # サーバ、プラグイン、検出されたクライアント設定をインストール
```

便利なインストーラオプション：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client codex      # Codex のみ設定
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client opencode   # OpenCode のみ設定
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client claude     # Claude Code/Desktop 設定があれば設定
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Client none       # MCP クライアント設定を編集せずにファイルのみインストール
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Years 2024        # レジストリ検出が利用できない場合に Revit バージョンを指定
```

セットアップ ZIP にはセルフコンテナ型の `rvt-mcp.exe` が含まれているため、クライアントマシンに `.NET 8 SDK`、`dotnet tool install`、またはこのリポジトリは必要ありません。設定エントリは絶対インストールパスを使用するため、`%USERPROFILE%\.dotnet\tools` や PATH は関与しません。インストーラは検出されたすべての Revit バージョン用のプラグインを展開し、`rvt-mcp` という名前の自動検出 MCP エントリを 1 つ作成します。

### 動作確認

1. Revit 2022-2027 を開き、モデルを開きます。
2. BIMwright リボンパネルを使用して MCP プラグインを起動/切替します。
3. MCP クライアントで `tools/list` を実行します。
4. `get_current_view_info` を呼び出します。

期待される応答形式：

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

MCP クライアントがツールをリスト表示し、Revit への呼び出しが成功するまで、インストール完了と主張しないでください。

### アンインストール

プラグイン、セルフコンテナ型サーバ、レガシー .NET グローバルツール（存在する場合）、ホスト設定エントリ、検出ファイル、ログ、ToolBaker キャッシュを一括で削除するには：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

セットアップ ZIP には `uninstall-all.ps1` も同じ一括削除のエイリアスとして含まれています。

### 開発者 / レガシーインストール

開発者は、サーバを NuGet .NET ツールとしてインストールし、プラグインのみのバンドルを使用することもできます：

```powershell
dotnet tool install -g RvtMcp.Server
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SourceDir . -Client none
```

この方法は開発および後方互換性のためのものです。クライアントマシンではセットアップ ZIP を使用してください。

---

## サポート対象 MCP クライアント

| クライアント | ステータス | 備考 |
|---|---|---|
| Claude Code CLI | ドキュメント化 | プロジェクト `.mcp.json` またはグローバル `~/.claude.json` |
| Claude Desktop | ドキュメント化 | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode | スクリプト化 | `install.ps1 -Client opencode` |
| Codex | スクリプト化 | `install.ps1 -Client codex` |
| Cursor | ドキュメント化 | プロジェクトまたはユーザー `mcp.json` |
| Cline (VS Code) | ドキュメント化 | Cline MCP 設定 JSON |
| VS Code Copilot | ドキュメント化 | ネイティブ `servers` スキーマ（`type: stdio`） |
| Gemini CLI | ドキュメント化 | `gemini mcp add ...` または設定 JSON |
| Antigravity | ドキュメント化 | Gemini/Antigravity MCP 設定 JSON |

---

## ツールセット

全サーフェスは **23 ツールセットにわたる 226 ツール**（`--toolsets all`）です。デフォルトでは `modify` と `delete` を除くすべてのツールセットが有効です。アダプティブベイクが有効な場合、承認されたベイクドツールが追加で読み込まれます。`--read-only` は書き込み可能なすべてのツールセットを削除します。

デフォルトで有効なツールセット：`query`、`create`、`view`、`schedule`、`families`、`mep`、`graphics`、`export`、`toolbaker`、`meta`、`lint`、`sheets`、`materials`、`geometry`、`annotation`、`rooms`、`links`、`parameters`、`organization`、`workflows`、`structural`。

オプトインツールセット（デフォルトでオフ）：`modify`、`delete`。明示的に指定するか、`--toolsets all` で有効にします。

特定のセットを有効にするには `--toolsets query,create,modify,meta`、すべて有効にするには `--toolsets all` を使用します。`--read-only` を追加すると、要求内容に関係なく書き込み可能なツールセットが削除されます。

| ツールセット | ツール | デフォルト |
|---|---|---|
| `query` | 現在のビュー、選択要素、利用可能なファミリタイプ、マテリアル数量、モデル統計、AI 要素フィルタ | オン |
| `create` | グリッド、レベル、ルーム、ライン基準要素、ポイント基準要素、サーフェス基準要素、グループ作成 | オン |
| `view` | ビュー作成、シートレイアウト、シートへのビュー配置、画像キャプチャ、トリミング/縮尺設定、ビュー切替、要素表示 | オン |
| `meta` | `show_message`、`switch_target`、`batch_execute`、使用状況統計、プロジェクト情報設定、未使用ファミリ削除 | オン |
| `lint` | ビュー命名パターン分析、修正提案、ファームプロファイル検出、モデル警告サマリ | オン |
| `schedule` | 一覧/検査、フィールド/数式/データ/要素、作成 + フィールド追加/更新、フィルタ+ソート | オン |
| `families` | ロード済みファミリ一覧、ファミリのロード/アンロード/置換、ファミリタイプのエクスポート/一覧、ファミリタイプの複製/リネーム | オン |
| `modify` | `operate_element`、`color_elements`、パラメータ/タイプ/ワークセット編集 | オフ |
| `delete` | `delete_element` | オフ |
| `annotation` | 要素/カテゴリタグ付け、テキスト注釈、寸法、塗り潰し領域、詳細線、コールアウト、キーノート、未タグ/未寸法チェック、空タグ削除 | オン |
| `export` | `export_room_data` | オン |
| `mep` | `detect_system_elements` | オン |
| `graphics` | ビューフィルタ（作成/一覧/適用/削除）、要素グラフィックオーバーライド、カテゴリ表示設定、ビューフェーズ/表示設定 | オン |
| `toolbaker` | 承認済みツールの一覧/実行、コード送信、アダプティブベイク提案ライフサイクル | オン |
| `sheets` | シート作成、複製、プレースホルダーシート、シート一覧、タイトルブロックパラメータ、スケジュール配置、リビジョン、シート番号変更 | オン |
| `materials` | マテリアルの一覧/作成/複製、マテリアルの外観/識別/構造/熱特性、マテリアル拾い出し、要素割り当て | オン |
| `geometry` | 要素バウンディングボックス、要素ジオメトリ、距離測定、干渉検出、レイキャスティング、体積/面積分析、重心、複雑度 | オン |
| `rooms` | ルーム、エリア、スペース、境界、開口部、ルームセパレータ、仕上、自動ルーム作成、エリアタグ付け | オン |
| `links` | Revit/CAD リンク一覧、CAD インポート/リンク、Revit リンクのロード/アンロード/リロード、リンク要素、座標、プロジェクト基点 | オン |
| `parameters` | プロジェクト/共有パラメータの作成、バインド/アンバインド、共有パラメータの一覧/エクスポート、GUID による値設定 | オン |
| `organization` | 保存済み選択（保存/ロード/一覧/削除）、要素選択、ビューテンプレート（一覧/適用/ビューから作成/複製/削除） | オン |
| `workflows` | 干渉レビュー、データラウンドトリップ、モデル監査、命名標準化、ルームドキュメンテーション、シートセット、拾い出しレポート、ビュークリーニング | オン |
| `structural` | 構造柱/梁/壁/基礎、鉄筋セット + スターラップ、構造荷重、フレームタグ、接合解析 | オン |

### 全ツール

以下の表は代表的なツールに焦点を当てています。全サーフェスは 23 ツールセットにわたる 226 ツールです。

| ツールセット | ツール | 説明 |
|---|---|---|
| `query` | `get_current_view_info` | アクティブビューのメタデータ：タイプ、レベル、縮尺、詳細レベル。 |
| `query` | `get_selected_elements` | ID、名前、カテゴリ、タイプを含む現在選択中の要素。 |
| `query` | `get_available_family_types` | プロジェクト内のファミリタイプ、カテゴリでフィルタ可能。 |
| `query` | `ai_element_filter` | カテゴリとパラメータ/演算子でフィルタ、値は mm 単位。 |
| `query` | `analyze_model_statistics` | カテゴリ別にグループ化された要素数。 |
| `query` | `get_material_quantities` | カテゴリの面積と体積の合計。 |
| `query` | `get_element_details` | 要素メタデータ、位置、バウンディングボックス、ワークセット、フェーズ、グループ、アセンブリ ID。 |
| `query` | `get_element_parameters` | ストレージタイプ、表示値、生の値、データ/スペック ID を含むインスタンスパラメータ。 |
| `query` | `get_type_parameters` | タイプ ID または要素 ID からのタイプパラメータ。 |
| `query` | `list_project_parameters` | プロジェクト/共有パラメータバインディング、バインディング種別、カテゴリ。 |
| `query` | `get_element_relationships` | ホスト、グループ、アセンブリ、所有ビュー、設計オプション、ネスティング、従属要素。 |
| `query` | `list_groups` | タイプ、添付/詳細メタデータ、オプションのメンバー ID を含むグループインスタンス。 |
| `query` | `get_group_members` | カテゴリ、タイプ、所有ビュー、固定状態を含むグループインスタンスのメンバー。 |
| `query` | `list_assemblies` | タイプ、命名カテゴリ、メンバー数、オプションのメンバー ID を含むアセンブリインスタンス。 |
| `query` | `get_assembly_members` | カテゴリ、タイプ、グループ、ワークセット ID を含むアセンブリインスタンスのメンバー。 |
| `query` | `list_worksets` | ワークセット、アクティブワークセット、編集/オープン状態、オプションの要素数。 |
| `create` | `create_line_based_element` | 壁またはその他のライン基準要素。 |
| `create` | `create_point_based_element` | ドア、窓、家具、またはその他のポイント要素。 |
| `create` | `create_surface_based_element` | ポリラインから床または天井を作成。 |
| `create` | `create_level` | 標高（mm）でのレベルの作成。 |
| `create` | `create_grid` | 2 点間（mm）のグリッドライン。 |
| `create` | `create_room` | 壁で囲まれたポイントにルームを作成。 |
| `create` | `create_group_from_elements` | 2 つ以上の要素からモデル/詳細グループを作成。 |
| `modify` | `operate_element` | ID に対して選択、非表示、表示、分離、色設定を実行。 |
| `modify` | `color_elements` | パラメータ値に基づいてカテゴリを色分け。 |
| `modify` | `set_element_parameter_values` | 複数の要素にまたがってインスタンスパラメータを設定。 |
| `modify` | `set_type_parameter_values` | 明示的または要素解決済みタイプにまたがってタイプパラメータを設定。 |
| `modify` | `change_element_type` | 要素を互換性のあるターゲットタイプに変更。 |
| `modify` | `assign_elements_to_workset` | 共有モデル内のユーザーワークセットに要素を割り当て。 |
| `delete` | `delete_element` | ID リストで削除。明示的に必要な場合を除きオフにしておくこと。 |
| `view` | `create_view` | フロアプランまたは 3D ビュー。 |
| `view` | `place_view_on_sheet` | ビューを新規または既存のシートに配置。 |
| `view` | `analyze_sheet_layout` | タイトルブロック、ビューポート位置、縮尺（mm）。 |
| `export` | `export_room_data` | 名前、番号、面積、周長、レベル、体積を含むルームデータ。 |
| `annotation` | `tag_all_walls` | 中央点に壁タイプタグを配置。既存タグはスキップ。 |
| `annotation` | `tag_all_rooms` | 位置点にルームタグを配置。既存タグはスキップ。 |
| `mep` | `detect_system_elements` | シードからコネクタを辿り、システムメンバーを返す。 |
| `toolbaker` | `send_code_to_revit` | デフォルトのツールサーフェスから、Revit 内でアドホック C# をコンパイル・実行。 |
| `toolbaker` | `list_baked_tools` | 承認済みパーソナルベイクドツールの一覧。 |
| `toolbaker` | `run_baked_tool` | 承認済みベイクドツールを名前で呼び出し。 |
| `toolbaker` | `list_bake_suggestions` | アダプティブベイクのみ：ローカル提案の一覧。 |
| `toolbaker` | `accept_bake_suggestion` | アダプティブベイクのみ：ローカル提案を承認・適用。 |
| `toolbaker` | `dismiss_bake_suggestion` | アダプティブベイクのみ：ローカル提案をスヌーズまたは却下。 |
| `meta` | `show_message` | Revit 内の TaskDialog — 接続テストや通知用。 |
| `meta` | `switch_target` | 複数バージョン実行時にアクティブな Revit 接続を切替。 |
| `meta` | `batch_execute` | 1 つの `TransactionGroup` 内でコマンドを原子的に実行。 |
| `meta` | `analyze_usage_patterns` | ローカル使用統計：ツール呼び出し、セッション、エラー。 |
| `lint` | `analyze_view_naming_patterns` | 主要なビュー命名パターンと外れ値を推測。 |
| `lint` | `suggest_view_name_corrections` | ビュー外れ値に対する修正名を提案。 |
| `lint` | `detect_firm_profile` | プロジェクト命名をファームプロファイルと照合。 |

---

## サポート対象 Revit バージョン

| Revit | ターゲットフレームワーク | トランスポート | 備考 |
|---|---|---|---|
| 2022 | .NET 4.8 | TCP | 承認済み ToolBaker パスのスモークテスト済み |
| 2023 | .NET 4.8 | TCP | コアランタイムカバレッジ |
| 2024 | .NET 4.8 | TCP | コアランタイムカバレッジ |
| 2025 | .NET 8（`net8.0-windows7.0`） | Named Pipe | コアランタイムカバレッジ |
| 2026 | .NET 8（`net8.0-windows7.0`） | Named Pipe | コアランタイムカバレッジ；承認済み ToolBaker パスのスモークテスト済み |
| 2027 | .NET 10（`net10.0-windows7.0`） | Named Pipe | 承認済み ToolBaker パスのスモークテスト済み |

Revit API が変更されるため、ランタイムの動作は Revit のバージョンによって依然として異なる可能性があります。カスタムベイクド C# ツールは、対象のバージョンでテストしない限り、バージョン依存として扱う必要があります。

---

## セキュリティとプライバシー

簡潔に言えば：モデルは自分のマシン上に残ります。

- **デフォルトでループバック。** TCP トランスポートは `127.0.0.1` でリッスンし、Named Pipe はローカルマシンスコープです。
- **セッションごとのトークンハンドシェイク。** `%LOCALAPPDATA%\RvtMcp\` 以下の検出ファイルには接続情報と認証トークンが含まれます。
- **スキーマ検証。** 不正な形式のツール呼び出しは、コマンドハンドラが実行される前に拒否されます。
- **パスマスキング。** モデルに返されるエラーは、絶対パスが漏洩しないようサニタイズされます。
- **ToolBaker 制御。** `send_code_to_revit` はデフォルトで利用可能です。アダプティブベイクはオプトイン制であり、提案/ログ機能のみを制御します。`--read-only` または `--disable-toolbaker` により ToolBaker サーフェス全体が削除されます。
- **ローカルストレージ。** 使用状況イベント、ベイクデータベース、ログ、承認済みツールメタデータは、ローカルの Bimwright ストレージ下に保持されます。

開示および脅威モデルの詳細については [SECURITY.md](SECURITY.md) を参照してください。

---

## 設定

3 層構成。後から指定されたものが優先されます：JSON ファイル、次に環境変数、最後に CLI 引数。

| 設定項目 | CLI | 環境変数 | JSON キー |
|---|---|---|---|
| ターゲット Revit バージョン | `--target 2023` | `BIMWRIGHT_TARGET` | `target` |
| ツールセット | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| 読み取り専用 | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN バインド許可 | プラグイン側のみ | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker ツール許可 | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| アダプティブベイク提案有効化 | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| コード送信本文のキャッシュ | `--cache-send-code-bodies` / `--no-cache-send-code-bodies` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |

JSON ファイルのパス：`%LOCALAPPDATA%\RvtMcp\bimwright.config.json`。

---

## 開発

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-2026/RvtMcp.Plugin.R26.csproj -c Release
```

プラグインプロジェクトは通常の `Build` 後に自動デプロイされ、`%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` にコピーされます。プラグインプロジェクトをビルドする前に Revit を閉じてください。Revit はロード中の DLL をロックするためです。

リリース用にプラグイン ZIP をステージングするには：

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

テスト戦略、ツールサーフェススナップショットルール、コントリビューションについての注意事項は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。

---

## ドキュメント

- [AGENTS.md](AGENTS.md) — AI コーディングエージェント向けインストール・配線ガイド。
- [ARCHITECTURE.md](ARCHITECTURE.md) — プロセスモデル、トランスポート、スレッディング、DTO 戦略。
- [docs/bake.md](docs/bake.md) — アダプティブベイク、プライバシー、承認済みツール、互換性動作。
- [docs/roadmap.md](docs/roadmap.md) — 現在の堅牢化計画と延期された作業。
- [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md) — 公開インストール確認チェックリスト。
- [benchmarks/README.md](benchmarks/README.md) — 弱モデルベンチマーク手順。

---

## bimwright ファミリー

AEC ツールチェーン向けに手作業で作られた MCP ゲートウェイ — 単一のアーキテクチャ、予測可能/監査可能/リバーシブル：

- [**rvt-mcp**](https://github.com/bimwright/rvt-mcp) — Autodesk® Revit®
- [**dwg-mcp**](https://github.com/bimwright/dwg-mcp) — Autodesk® AutoCAD®
- [**nwd-mcp**](https://github.com/bimwright/nwd-mcp) — Autodesk® Navisworks®
- [**ipt-mcp**](https://github.com/bimwright/ipt-mcp) — Autodesk® Inventor®
- [**bim-wiki**](https://github.com/bimwright/bim-wiki) — ベトナム語ファーストの BIM 知識ベース

---

## ライセンス

Apache-2.0。[LICENSE](LICENSE) を参照してください。

Revit および Autodesk は Autodesk, Inc. の登録商標です。bimwright は独立したオープンソースプロジェクトであり、Autodesk, Inc. との提携、スポンサー、または推奨関係はありません。

---

<p align="center">
  <a href="https://github.com/bimwright">bimwright</a> プロジェクト — 神秘性を売るよりも作業を自動化することを選ぶ実務者のために作られました。
</p>
