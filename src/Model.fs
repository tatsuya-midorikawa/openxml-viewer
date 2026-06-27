/// Webview へ送信する解析結果の共有モデル。
/// JS 側 (Webview) でそのまま扱えるよう、判別共用体ではなくレコード/配列で表現する。
module OpenXmlViewer.Model

// ---------------------------------------------------------------------------
// スプレッドシート (.xlsx)
// ---------------------------------------------------------------------------

/// セル。col は 0 始まりの列番号。
type Cell = { col: int; text: string }

/// 行。index は 1 始まりの行番号。
type Row =
    { index: int
      height: float
      cells: Cell[] }

/// 列幅。min/max は 0 始まりの列番号。
type Column =
    { min: int
      max: int
      width: float }

/// ワークシート上に配置された画像。
type SheetImage =
    { col: int
      row: int
      colOffset: float
      rowOffset: float
      toCol: int
      toRow: int
      width: float
      height: float
      altText: string
      contentType: string
      data: string }

/// ワークシート。
type Sheet =
    { name: string
      rows: Row[]
      columns: Column[]
      images: SheetImage[]
      maxCol: int
      showGridLines: bool
      defaultColWidth: float
      defaultRowHeight: float }

/// スプレッドシート全体。
type SpreadsheetData = { kind: string; sheets: Sheet[] }

// ---------------------------------------------------------------------------
// 文書 (.docx)
// ---------------------------------------------------------------------------

/// 文書ブロック。kind により段落/見出し/表を区別する。
/// kind = "paragraph" | "heading" | "table"
type Block =
    { kind: string
      style: string
      level: int
      text: string
      rows: string[][] }

/// 文書全体。
type DocumentData = { kind: string; blocks: Block[] }

// ---------------------------------------------------------------------------
// プレゼンテーション (.pptx)
// ---------------------------------------------------------------------------

/// スライド。
type Slide =
    { index: int
      title: string
      texts: string[] }

/// プレゼンテーション全体。
type PresentationData = { kind: string; slides: Slide[] }
