# 開発ガイド (DEVELOPMENT)

OpenXML Viewer のビルド・デバッグ・パッケージング手順をまとめます。
本拡張機能は **F# → [Fable](https://fable.io/) → JavaScript → [esbuild](https://esbuild.github.io/) でバンドル** という流れで構築します。
パーサー / ビューアーは外部ランタイムライブラリに依存せず、フルスクラッチで実装しています（詳細は [README](./README.md) を参照）。

---

## 目次

- [設計と仕様](#設計と仕様)
  - [設計方針（依存関係）](#設計方針依存関係)
  - [アーキテクチャ](#アーキテクチャ)
  - [技術スタック](#技術スタック)
  - [ファイル解析](#ファイル解析)
  - [レンダリング方針](#レンダリング方針)
  - [セキュリティ](#セキュリティ)
- [必要環境](#必要環境)
- [初回セットアップ](#初回セットアップ)
- [ビルドの仕組み](#ビルドの仕組み)
- [npm スクリプト](#npm-スクリプト)
- [デバッグ実行](#デバッグ実行-f5)
- [開発ワークフロー](#開発ワークフロー)
- [パッケージング](#パッケージング)
- [プロジェクト構成](#プロジェクト構成)
- [トラブルシューティング](#トラブルシューティング)

---

## 設計と仕様

### 設計方針（依存関係）

本拡張機能は、**F# / JavaScript ともに標準ライブラリ以外の外部ライブラリを原則として利用しない** 方針で設計しています。

- **フルスクラッチ実装**: OOXML のパーサー（ZIP 展開・XML 解析）およびビューアー（レンダリング）は、外部ライブラリに依存せず自前で実装します。
- **方針の理由**:
  - 依存ライブラリの脆弱性・破壊的変更・メンテナンス停止といったリスクを排除する
  - 拡張機能のサイズと読み込みコストを最小化する
  - 解析・描画の挙動を完全に制御し、読み取り専用・オフライン動作を保証する
- **例外**: ビルドツールチェーン（F# → JavaScript 変換を行う Fable、`.vsix` を生成する `@vscode/vsce` など）や、VS Code 拡張ホストが提供する `vscode` API は対象外です。これらは実行時の機能ライブラリではなく、開発・配布のための基盤として利用します。

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
| 拡張機能 API バインディング | 自前の最小バインディング（[src/Vscode.fs](./src/Vscode.fs)） |
| バンドル | [esbuild](https://esbuild.github.io/) |
| ビルドオーケストレーション | npm スクリプト |
| パッケージング | [`@vscode/vsce`](https://github.com/microsoft/vscode-vsce) |
| 外部ランタイムライブラリ | 不使用（F# / JavaScript ともに標準ライブラリのみ・フルスクラッチ実装） |

### ファイル解析

- Office Open XML ファイルは実体が **ZIP アーカイブ** であり、内部の XML パーツ（`xl/`, `word/`, `ppt/` 配下）を解析します。
- **ZIP 展開（DEFLATE 解凍）および XML 解析は、外部ライブラリを使わずフルスクラッチで実装** します。
- 解析対象の主なパーツ:
  - `.xlsx`: `xl/workbook.xml`, `xl/worksheets/sheet*.xml`, `xl/sharedStrings.xml`, `xl/styles.xml`
  - `.docx`: `word/document.xml`, `word/styles.xml`, `word/media/*`
  - `.pptx`: `ppt/presentation.xml`, `ppt/slides/slide*.xml`, `ppt/media/*`

### レンダリング方針

- **フルスクラッチ**: ビューアー（レンダリング）は外部 UI ライブラリ・フレームワークを使わず、標準の HTML/CSS/JS のみで実装します。
- **読み取り専用**: Webview からファイルへの書き込みは行いません。
- **段階的描画**: 大きなファイルでもフリーズしないよう、行・スライド単位で段階的に描画します（上限はユーザー設定で調整可能）。
- **テーマ追従**: VS Code のカラーテーマに追従して表示色を切り替えます（`openxmlViewer.theme` で固定も可能）。

### セキュリティ

- Webview には厳格な **Content Security Policy（CSP）** を適用し、外部スクリプト・外部リソースの読み込みを禁止します。
- 文書内に埋め込まれた画像は、拡張機能が `webview.asWebviewUri` で安全に変換した上で表示します。
- 外部ネットワークへの通信は行いません（オフラインで完結します）。

---

## 必要環境

| ツール | バージョン | 用途 |
| --- | --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/) | `10.0` 以降 | F# / Fable のビルド |
| [Node.js](https://nodejs.org/) | `18` 以降 | esbuild によるバンドル・パッケージング |
| [Visual Studio Code](https://code.visualstudio.com/) | `1.90.0` 以降 | 拡張機能のデバッグ実行 |

> Microsoft Office のインストールは不要です。

---

## 初回セットアップ

```bash
# .NET ローカルツール（Fable）の復元
dotnet tool restore

# npm 依存関係（esbuild / @vscode/vsce / @types/vscode）の復元
npm install
```

`dotnet tool restore` は [.config/dotnet-tools.json](./.config/dotnet-tools.json) に定義された Fable を取得します。
初回の `npm run fable` 実行時に Fable ランタイムライブラリ（`@fable-org/fable-library-js`）が `build/fable_modules/` へ展開されます。

---

## ビルドの仕組み

```
 src/**/*.fs ──(dotnet fable)──▶ build/**/*.js ──(esbuild --bundle)──▶ dist/extension.js
   F# ソース        ESM 形式の JS          単一の CommonJS バンドル（vscode は external）
```

1. **Fable**: `src` 配下の F# を ES モジュール形式の JavaScript へトランスパイルし、`build/` に出力します。
2. **esbuild**: エントリーポイント `build/Extension.js` を起点に依存（Fable ランタイム含む）を 1 ファイルへバンドルし、
   VS Code 拡張ホストが読み込める CommonJS 形式（`dist/extension.js`）として出力します。`vscode` モジュールは
   実行時に拡張ホストから提供されるため `--external:vscode` で除外します。

[package.json](./package.json) の `main` は `./dist/extension.js` を指します。
Webview 用アセット（[media/main.js](./media/main.js) / [media/style.css](./media/style.css)）はバンドル対象外で、
実行時に `asWebviewUri` 経由で読み込まれます。

---

## npm スクリプト

| スクリプト | コマンド | 説明 |
| --- | --- | --- |
| `npm run fable` | `dotnet fable src --outDir build --test:MSBuildCracker` | F# を JavaScript へトランスパイル |
| `npm run bundle` | `esbuild build/Extension.js ... --outfile=dist/extension.js` | esbuild で CommonJS バンドルを生成 |
| `npm run build` | `npm run fable && npm run bundle` | フルビルド（Fable → esbuild） |
| `npm run watch` | `dotnet fable watch src --outDir build --test:MSBuildCracker` | F# の変更を監視して再トランスパイル |
| `npm run clean` | `dotnet fable clean src --outDir build --yes` | Fable の出力・キャッシュを削除 |
| `npm run package` | `npm run build && vsce package` | `.vsix` を生成 |

---

## デバッグ実行 (F5)

1. VS Code でこのリポジトリのルートを開きます。
2. `F5` を押す（または「実行とデバッグ」から **Run Extension** を選択）。
   - [.vscode/launch.json](./.vscode/launch.json) の設定により、`preLaunchTask` として `npm run build` が走ります。
   - ビルド成功後、拡張機能を読み込んだ **Extension Development Host** ウィンドウが起動します。
3. 起動したウィンドウで `.xlsx` / `.docx` / `.pptx` ファイルを開くと、カスタムエディターでプレビューされます。

> 既定のエディターがテキストになっている場合は、ファイルを右クリック →
> **「Open With...」→ 該当の OpenXML Viewer** を選択してください。

---

## 開発ワークフロー

ソースを変更したあとは、以下のいずれかで反映します。

- **手動ビルド**: `npm run build` を実行し、Extension Development Host で
  コマンドパレットの **Developer: Reload Window**（`⌘R` / `Ctrl+R`）を実行。
- **ウォッチ + 手動バンドル**: 1 つのターミナルで `npm run watch`（Fable 監視）を起動しておき、
  必要なタイミングで `npm run bundle` を実行してから Extension Development Host をリロード。

Webview（`media/` 配下）だけを変更した場合は、再ビルド不要で
**OpenXML Viewer: Reload** コマンド、または Extension Development Host のリロードで反映されます。

---

## パッケージング

```bash
npm run package
```

`npm run build`（Fable → esbuild）を実行したのち、`vsce package` で `openxml-viewer-<version>.vsix` を生成します。
`.vsix` に含めるファイルは [.vscodeignore](./.vscodeignore) で制御しており、F# ソース・`build/`・
ECMA-376 仕様書・`node_modules` などは除外されます（実行時に必要なのは `dist/` と `media/` のみ）。

ローカルへインストールして確認する場合:

```bash
code --install-extension openxml-viewer-<version>.vsix
```

---

## プロジェクト構成

```
openxml-viewer/
├── src/                       # 拡張機能のソース（F#）
│   ├── Core/                  # フルスクラッチの基盤
│   │   ├── Inflate.fs         #   DEFLATE 解凍（RFC 1951）
│   │   ├── Zip.fs             #   ZIP アーカイブ読み取り
│   │   ├── Xml.fs             #   XML パーサー
│   │   ├── Text.fs            #   UTF-8 デコード
│   │   └── Opc.fs             #   Open Packaging Conventions（リレーションシップ）
│   ├── Model.fs               # Webview へ渡す共有モデル
│   ├── Parsers/               # 各形式パーサー
│   │   ├── Spreadsheet.fs     #   .xlsx（SpreadsheetML）
│   │   ├── Document.fs        #   .docx（WordprocessingML）
│   │   └── Presentation.fs    #   .pptx（PresentationML）
│   ├── Vscode.fs              # VS Code API への最小バインディング
│   ├── Extension.fs           # エントリーポイント / カスタムエディター登録
│   └── OpenXmlViewer.fsproj   # F# プロジェクトファイル
├── media/                     # Webview アセット（バニラ JS/CSS）
│   ├── main.js
│   └── style.css
├── .config/dotnet-tools.json  # Fable のローカルツール定義
├── .vscode/                   # launch.json / tasks.json
├── ECMA-376/                  # Office Open XML 仕様（参照用）
├── dist/                      # esbuild の出力（生成物）
├── build/                     # Fable の出力（生成物）
├── package.json               # 拡張機能マニフェスト
├── README.md
└── DEVELOPMENT.md
```

> `dist/` と `build/` はビルド生成物で、`.gitignore` の対象です。

---

## トラブルシューティング

| 症状 | 対処 |
| --- | --- |
| `dotnet fable` が見つからない | `dotnet tool restore` を実行する。 |
| Fable のビルドが固まる / 出力が古い | `npm run clean` でキャッシュを削除し、`npm run build` を再実行する。 |
| `esbuild` が見つからない | `npm install` を実行する。 |
| Extension Development Host に変更が反映されない | `npm run build` 後、`⌘R` / `Ctrl+R` でウィンドウをリロードする。 |
| Webview が空白のまま | 開発者ツール（**Developer: Open Webview Developer Tools**）でコンソールエラーを確認する。 |
| `.vsix` にソースが含まれてしまう | [.vscodeignore](./.vscodeignore) の除外設定を確認する。 |
