# 変更履歴 (Changelog)

本ファイルは OpenXML Viewer の主要な変更点を記録します。
フォーマットは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、
バージョン番号は [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [Unreleased]

## [0.0.4] - 2026-06-30

### Added

- スプレッドシート（`.xlsx`）の行高・列幅の調整機能。列・行ヘッダーの境界をドラッグしてサイズを変更でき、境界のダブルクリックで内容に合わせた自動調整、ボタンによるシート単位のリセットに対応。調整したサイズはファイルを再度開いても保持されます（読み取り専用のため、元のファイルは変更しません）。

## [0.0.3] - 2026-06-29

### Added

- ビューアー内テキスト検索機能。`.xlsx` / `.docx` / `.pptx` の表示中の内容を全文検索し、一致箇所のハイライトと前後移動に対応（シート／スライドをまたいだ移動を含む）。

## [0.0.2] - 2026-06-29

### Fixed

- ダークテーマ（`Dark+` など）選択時にプレビュー背景が黒くなる問題を修正。背景色が指定されていない場合の既定背景を `#fdfdfd` に固定。

## [0.0.1] - 2026-06-29

### Added

- Excel ブック（`.xlsx`）の読み取り専用プレビュー（シート単位の表示・複数シートのタブ切り替え）。
- Word 文書（`.docx`）の読み取り専用プレビュー（段落・見出し・表などの本文構造表示）。
- PowerPoint プレゼンテーション（`.pptx`）の読み取り専用プレビュー（テキスト・図形・画像・サムネイル一覧）。
- カスタムエディターによる自動プレビュー（`.xlsx` / `.docx` / `.pptx`）。
- コマンド `OpenXML Viewer: Open Preview` / `OpenXML Viewer: Reload`。
- 設定項目（最大描画行数・グリッド線・画像表示・サムネイル・配色テーマ）。
- Content Security Policy（CSP）による安全なオフライン閲覧。

[Unreleased]: https://github.com/tatsuya-midorikawa/openxml-viewer/compare/v0.0.4...HEAD
[0.0.4]: https://github.com/tatsuya-midorikawa/openxml-viewer/compare/v0.0.3...v0.0.4
[0.0.3]: https://github.com/tatsuya-midorikawa/openxml-viewer/compare/v0.0.2...v0.0.3
[0.0.2]: https://github.com/tatsuya-midorikawa/openxml-viewer/compare/v0.0.1...v0.0.2
[0.0.1]: https://github.com/tatsuya-midorikawa/openxml-viewer/releases/tag/v0.0.1
