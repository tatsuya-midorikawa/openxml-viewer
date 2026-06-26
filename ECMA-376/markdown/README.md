# ECMA-376 (Office Open XML) 仕様書 — Markdown 版

`ECMA-376/` 配下の Open Office XML 仕様書 PDF（全 4 部）を、AI / 開発者が参照しやすい
ように Markdown へ変換したものです。各部 = 1 ファイルで、章節番号（例: `§17.3.2.34`）が
そのまま見出しとして残るため、引用・検索のアンカーとして利用できます。

## ファイル一覧

| ファイル | 内容 | 原典ページ数 | サイズ | 見出し数 | コードブロック数 |
|----------|------|-------------:|-------:|---------:|----------------:|
| [Part 1 - Fundamentals and Markup Language Reference.md](./Part%201%20-%20Fundamentals%20and%20Markup%20Language%20Reference.md) | 基礎とマークアップ言語リファレンス（WordprocessingML / SpreadsheetML / PresentationML / DrawingML 等の要素・属性定義） | 5039 | 8.8 MB | 4824 | 8632 |
| [Part 2 - Open Packaging Conventions.md](./Part%202%20-%20Open%20Packaging%20Conventions.md) | Open Packaging Conventions（OPC：パッケージ / パート / リレーションシップ） | 95 | 172 KB | 295 | 103 |
| [Part 3 - Markup Compatibility and Extensibility.md](./Part%203%20-%20Markup%20Compatibility%20and%20Extensibility.md) | Markup Compatibility and Extensibility（MCE：`AlternateContent` 等） | 43 | 68 KB | 71 | 231 |
| [Part 4 - Transitional Migration Features.md](./Part%204%20-%20Transitional%20Migration%20Features.md) | Transitional な移行用機能 | 1553 | 3.1 MB | 725 | 740 |

> Part 1 は約 9 MB と大きいため、エディタで全体を開くより、見出し（`## §...`）やキーワードで
> `grep` / 検索してから該当箇所を開く使い方を推奨します。

## 変換方法

- ツール: [`pymupdf4llm`](https://pymupdf.readthedocs.io/en/latest/pymupdf4llm/)（PyMuPDF の LLM/RAG 向け Markdown 抽出、layout エンジン）
- 主なオプション:
  - `header=False, footer=False` … ページ上部の running header と下部のページ番号フッターを除去
  - `use_ocr=False` … テキスト PDF のため OCR 不要（高速化）
- 後処理: 変換器が誤って見出しに昇格させた行（`[ _Example_ :` / `_end example_ ]` などの
  例・注釈マーカー、インライン XML コード断片、`This subclause is informative.` 等の定型句、
  `Syntax:` / `Switches:` ラベル）を通常段落へ降格。番号付き見出し・Annex・前付けタイトルは
  一切変更していません。

## 既知の制限

- 図・画像は本文に含めず省略しています（テキスト参照用途のため）。
- 用語定義節など、原典で「番号」と「用語」が別行になっている箇所は、見出しが 2 つに分かれて
  現れることがあります（内容は連続しています）。
- 一部の表（特に目次）には PDF 由来のドットリーダー（`......`）が残ります。
- 附属書のスキーマ列挙などでは、原典の行番号が本文中に残ることがあります。
