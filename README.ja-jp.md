# OpenXML Viewer

Office Open XML 形式のファイル（`.xlsx` / `.docx` / `.pptx`）を **Visual Studio Code 上で直接プレビュー** するための拡張機能です。
ファイルをダブルクリックするだけで、Excel・Word・PowerPoint を起動せずに中身を素早く確認できます。

> [!NOTE]
> 本拡張機能は **閲覧（読み取り専用）** を目的としています。ファイルの編集・保存機能は提供しません。

---

## 目次

- [OpenXML Viewer](#openxml-viewer)
  - [目次](#目次)
  - [主な機能](#主な機能)
  - [対応ファイル形式](#対応ファイル形式)
  - [動作要件](#動作要件)
  - [インストール](#インストール)
    - [Visual Studio Code Marketplace から](#visual-studio-code-marketplace-から)
    - [VSIX ファイルから](#vsix-ファイルから)
  - [使い方](#使い方)
  - [コマンド](#コマンド)
  - [設定](#設定)
  - [制限事項](#制限事項)
  - [ロードマップ](#ロードマップ)
  - [コントリビュート](#コントリビュート)
  - [スポンサー](#スポンサー)
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
- 🔍 **ビューアー内テキスト検索**
  - 表示中の内容を全文検索し、一致箇所をハイライト
  - 前後の一致へジャンプ（シート／スライドをまたいで移動）
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

プレビュー上部の検索バーにキーワードを入力すると、表示中のドキュメントを全文検索できます。
`Enter` / `Shift+Enter` で次 / 前の一致へ移動し、`Esc` で検索をクリアします。
スプレッドシートやプレゼンテーションでは、一致するシート / スライドへ自動的に切り替わります。

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

## 制限事項

> [!WARNING]
> 本拡張機能は **簡易的なプレビュー** を目的としています。Excel・Word・PowerPoint と完全に互換性のある描画を保証するものではありません。
> 元の Office アプリケーションでの表示とは、レイアウト・書式・配色などが異なる場合があります。

- 編集・保存・印刷には対応していません（閲覧専用）。
- 複雑なレイアウト（高度な図形・SmartArt・グラフ・条件付き書式・マクロなど）は簡略化、または非表示となる場合があります。
- パスワード保護・暗号化されたファイルは開けません。
- 数式は計算結果のキャッシュ値を表示します（再計算は行いません）。
- 旧バイナリ形式（`.xls` / `.doc` / `.ppt`）には対応していません。

---

## ロードマップ

- [x] `.xlsx` ビューアー（セル・複数シート）
- [x] `.docx` ビューアー（本文・表・画像）
- [x] `.pptx` ビューアー（スライド・サムネイル）
- [x] ビューアー内テキスト検索（一致のハイライト・前後移動）
- [ ] テキストのコピー対応
- [ ] グラフ・図形の描画強化
- [ ] エクスポート（PDF / HTML）

---

## コントリビュート

バグ報告・機能要望・プルリクエストを歓迎します。
[Issues](https://github.com/tatsuya-midorikawa/openxml-viewer/issues) からお気軽にご連絡ください。

仕様・設計やビルド／デバッグ手順は [DEVELOPMENT.md](./DEVELOPMENT.md)、Marketplace への公開は [PUBLISHING.md](./PUBLISHING.md) を参照してください。

---

## スポンサー

本プロジェクトの開発を応援していただける方は、[GitHub Sponsors](https://github.com/sponsors/tatsuya-midorikawa) からのご支援を歓迎します。
いただいたご支援は、機能の改善や継続的なメンテナンスに活用させていただきます。

---

## ライセンス

本プロジェクトは [MIT License](./LICENSE) の下で公開されています。
