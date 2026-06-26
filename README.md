# OpenXML Viewer

Office Open XML 形式のファイル（`.xlsx` / `.docx` / `.pptx`）を **Visual Studio Code 上で直接プレビュー** するための拡張機能です。
ファイルをダブルクリックするだけで、Excel・Word・PowerPoint を起動せずに中身を素早く確認できます。

> [!NOTE]
> 本拡張機能は **閲覧（読み取り専用）** を目的としています。ファイルの編集・保存機能は提供しません。

---

## 目次

- [主な機能](#主な機能)
- [対応ファイル形式](#対応ファイル形式)
- [動作要件](#動作要件)
- [インストール](#インストール)
- [使い方](#使い方)
- [コマンド](#コマンド)
- [設定](#設定)
- [仕様](#仕様)
- [制限事項](#制限事項)
- [開発](#開発)
- [ロードマップ](#ロードマップ)
- [コントリビュート](#コントリビュート)
- [ライセンス](#ライセンス)

---

## 主な機能

- 📊 **Excel ブック（`.xlsx`）の閲覧**
  - シート単位でセル内容を表形式で表示
  - 複数シートのタブ切り替え
- 📝 **Word 文書（`.docx`）の閲覧**
  - 段落・見出し・表などの本文構造を表示
- 📑 **PowerPoint プレゼンテーション（`.pptx`）の閲覧**
  - スライドごとのテキスト・図形・画像を表示
  - スライド一覧（サムネイル）からのジャンプ
- 🖱️ **シームレスな操作**
  - エクスプローラーでファイルを開くだけでプレビューを起動（カスタムエディター）
  - 外部アプリケーションの起動が不要
- 🔒 **安全な閲覧**
  - 読み取り専用のため、誤編集による破損が発生しない
  - Webview のコンテンツセキュリティポリシー（CSP）により外部リソース読み込みを制限

---

## 対応ファイル形式

| 種類 | 拡張子 | 形式 | 対応状況 |
| --- | --- | --- | --- |
| Excel ブック | `.xlsx` | SpreadsheetML | ✅ 閲覧 |
| Word 文書 | `.docx` | WordprocessingML | ✅ 閲覧 |
| PowerPoint | `.pptx` | PresentationML | ✅ 閲覧 |

> [!IMPORTANT]
> 対象は **Office Open XML（OOXML）形式** のファイルです。
> 旧バイナリ形式（`.xls` / `.doc` / `.ppt`）やマクロ有効形式（`.xlsm` など）は対象外です。

---

## 動作要件

- Visual Studio Code `1.90.0` 以降
- 追加のランタイムや外部アプリケーション（Microsoft Office など）のインストールは **不要**

---

## インストール

### Visual Studio Code Marketplace から

1. VS Code のサイドバーから **拡張機能（Extensions）** ビューを開く（`Ctrl+Shift+X` / `⌘+Shift+X`）
2. `OpenXML Viewer` を検索
3. **Install** をクリック

### VSIX ファイルから

```bash
code --install-extension openxml-viewer-<version>.vsix
```

または、コマンドパレット（`Ctrl+Shift+P` / `⌘+Shift+P`）から
**「Extensions: Install from VSIX...」** を実行し、`.vsix` ファイルを選択します。

---

## 使い方

1. VS Code のエクスプローラーで `.xlsx` / `.docx` / `.pptx` ファイルを開きます。
2. 本拡張機能のカスタムエディターが自動的に起動し、内容がプレビュー表示されます。
3. 既定のテキストエディターなどで開きたい場合は、ファイルを右クリックして
   **「Open With...（アプリケーションを選択して開く）」** から開き方を切り替えられます。

> [!TIP]
> 既定の開き方を変更したい場合は、**「Open With...」→「Configure default editor for '*.xlsx'...」** から
> 拡張子ごとに既定エディターを設定できます。

---

## コマンド

コマンドパレット（`Ctrl+Shift+P` / `⌘+Shift+P`）から利用できます。

| コマンド | コマンドID | 説明 |
| --- | --- | --- |
| OpenXML Viewer: Open Preview | `openxml-viewer.openPreview` | アクティブなファイルをプレビューで開く |
| OpenXML Viewer: Reload | `openxml-viewer.reload` | 現在のプレビューを再読み込みする |

---

## 設定

`settings.json`（ユーザー設定 / ワークスペース設定）から構成できます。

| 設定キー | 型 | 既定値 | 説明 |
| --- | --- | --- | --- |
| `openxmlViewer.spreadsheet.maxRows` | `number` | `1000` | スプレッドシートで一度に描画する最大行数 |
| `openxmlViewer.spreadsheet.showGridlines` | `boolean` | `true` | セルのグリッド線を表示するか |
| `openxmlViewer.document.showImages` | `boolean` | `true` | Word 文書内の画像を表示するか |
| `openxmlViewer.presentation.thumbnails` | `boolean` | `true` | スライドのサムネイル一覧を表示するか |
| `openxmlViewer.theme` | `string` | `"auto"` | プレビューの配色（`auto` / `light` / `dark`） |

設定例:

```jsonc
{
  "openxmlViewer.spreadsheet.maxRows": 5000,
  "openxmlViewer.theme": "dark"
}
```

---

## 仕様

### アーキテクチャ

本拡張機能は **F#** で実装し、[Fable](https://fable.io/)（F# → JavaScript コンパイラ）で
VS Code 拡張として動作する JavaScript へトランスパイルします。
VS Code の **Custom Editor API**（`CustomReadonlyEditorProvider`）を利用して、
対象拡張子のファイルに対するカスタムエディターを登録します。
描画は **Webview** 上で行い、拡張機能本体（Extension Host 側）でファイルを解析した結果を Webview へ送信します。

```
┌──────────────────────────────────────────────────────────────┐
│                     VS Code Extension Host                     │
│                  （F# → Fable → JavaScript）                    │
│                                                                │
│   ┌────────────────────────┐     ┌──────────────────────────┐ │
│   │  CustomEditorProvider   │ ──▶ │   OOXML Parser (F#)       │ │
│   │  (.xlsx/.docx/.pptx)    │     │  (ZIP 展開 + XML 解析)     │ │
│   └────────────┬───────────┘     └──────────────────────────┘ │
│                │  postMessage                                  │
│                ▼                                               │
│         ┌────────────┐                                         │
│         │   Webview   │  ◀── HTML/CSS/JS でレンダリング          │
│         └────────────┘                                         │
└──────────────────────────────────────────────────────────────┘
```

### 技術スタック

| 領域 | 採用技術 |
| --- | --- |
| 実装言語 | [F#](https://fsharp.org/) |
| JS へのコンパイル | [Fable](https://fable.io/) |
| 拡張機能 API バインディング | [Fable.VSCode](https://www.nuget.org/packages/Fable.VSCode/) など |
| ビルドオーケストレーション | npm スクリプト / [FAKE](https://fake.build/)（任意） |
| パッケージング | [`@vscode/vsce`](https://github.com/microsoft/vscode-vsce) |

### ファイル解析

- Office Open XML ファイルは実体が **ZIP アーカイブ** であり、内部の XML パーツ（`xl/`, `word/`, `ppt/` 配下）を解析します。
- 解析対象の主なパーツ:
  - `.xlsx`: `xl/workbook.xml`, `xl/worksheets/sheet*.xml`, `xl/sharedStrings.xml`, `xl/styles.xml`
  - `.docx`: `word/document.xml`, `word/styles.xml`, `word/media/*`
  - `.pptx`: `ppt/presentation.xml`, `ppt/slides/slide*.xml`, `ppt/media/*`

### レンダリング方針

- **読み取り専用**: Webview からファイルへの書き込みは行いません。
- **段階的描画**: 大きなファイルでもフリーズしないよう、行・スライド単位で段階的に描画します（上限は[設定](#設定)で調整可能）。
- **テーマ追従**: VS Code のカラーテーマに追従して表示色を切り替えます（`openxmlViewer.theme` で固定も可能）。

### セキュリティ

- Webview には厳格な **Content Security Policy（CSP）** を適用し、外部スクリプト・外部リソースの読み込みを禁止します。
- 文書内に埋め込まれた画像は、拡張機能が `webview.asWebviewUri` で安全に変換した上で表示します。
- 外部ネットワークへの通信は行いません（オフラインで完結します）。

---

## 制限事項

- 編集・保存・印刷には対応していません（閲覧専用）。
- 複雑なレイアウト（高度な図形・SmartArt・グラフ・条件付き書式・マクロなど）は簡略化、または非表示となる場合があります。
- パスワード保護・暗号化されたファイルは開けません。
- 数式は計算結果のキャッシュ値を表示します（再計算は行いません）。
- 旧バイナリ形式（`.xls` / `.doc` / `.ppt`）には対応していません。

---

## 開発

### 必要環境

- [.NET SDK](https://dotnet.microsoft.com/) `8.0` 以降（F# / Fable のビルドに使用）
- [Node.js](https://nodejs.org/) `18` 以降（Fable の出力実行・パッケージングに使用）
- [Visual Studio Code](https://code.visualstudio.com/)

### セットアップ

```bash
git clone https://github.com/tatsuya-midorikawa/openxml-viewer.git
cd openxml-viewer

# .NET ローカルツール（Fable など）の復元
dotnet tool restore

# npm 依存関係の復元
npm install
```

### ビルド / デバッグ実行

```bash
# F# を JavaScript へコンパイル（ウォッチモード）
npm run watch          # 例: dotnet fable watch src --outDir dist

# 単発ビルド
npm run build          # 例: dotnet fable src --outDir dist
```

VS Code でプロジェクトを開き、`F5`（**Run Extension**）を押すと、
拡張機能を読み込んだ **Extension Development Host** が起動します。
そのウィンドウで `.xlsx` / `.docx` / `.pptx` を開いて動作を確認できます。

### パッケージング

```bash
npm run package        # Fable ビルド後に .vsix を生成（vsce package）
```

### プロジェクト構成（想定）

```
openxml-viewer/
├── src/                    # 拡張機能のソースコード（F#）
│   ├── Extension.fs        # エントリーポイント / カスタムエディター登録
│   ├── Providers/          # 各形式の CustomEditorProvider
│   ├── Parsers/            # OOXML パーサー（xlsx / docx / pptx）
│   ├── Webview/            # Webview 用の HTML/CSS/JS
│   └── OpenXmlViewer.fsproj # F# プロジェクトファイル
├── .config/
│   └── dotnet-tools.json   # Fable などの .NET ローカルツール定義
├── package.json            # 拡張機能マニフェスト（contributes 等）
├── LICENSE
└── README.md
```

---

## ロードマップ

- [ ] `.xlsx` ビューアー（セル・複数シート）
- [ ] `.docx` ビューアー（本文・表・画像）
- [ ] `.pptx` ビューアー（スライド・サムネイル）
- [ ] 検索・コピー対応
- [ ] グラフ・図形の描画強化
- [ ] エクスポート（PDF / HTML）

---

## コントリビュート

バグ報告・機能要望・プルリクエストを歓迎します。
[Issues](https://github.com/tatsuya-midorikawa/openxml-viewer/issues) からお気軽にご連絡ください。

---

## ライセンス

本プロジェクトは [MIT License](./LICENSE) の下で公開されています。
