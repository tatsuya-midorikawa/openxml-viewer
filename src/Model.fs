/// Webview へ送信する解析結果の共有モデル。
/// JS 側 (Webview) でそのまま扱えるよう、判別共用体ではなくレコード/配列で表現する。
module OpenXmlViewer.Model

// ---------------------------------------------------------------------------
// スプレッドシート (.xlsx)
// ---------------------------------------------------------------------------

/// セル内の文字列 run。
type TextRun =
    { text: string
      bold: bool
      italic: bool
      underline: bool
      strike: bool
      fontSize: float
      fontName: string
      color: string }

/// セル。col は 0 始まりの列番号。
type Cell =
    { col: int
      text: string
      runs: TextRun[]
      fillColor: string
      align: string
      valign: string
      wrap: bool }

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
      runs: TextRun[]
      align: string
      rows: string[][] }

/// 文書全体。
type DocumentData = { kind: string; blocks: Block[] }

// ---------------------------------------------------------------------------
// プレゼンテーション (.pptx)
// ---------------------------------------------------------------------------

/// テキストボックス内の 1 段落 (DrawingML a:p)。
type SlideParagraph =
    { runs: TextRun[]
      align: string
      level: int
      bullet: string
      bulletColor: string
      marginLeft: float
      indent: float
      lineSpace: float }

/// スライド上に配置されたテキストボックス。
type SlideTextBox =
    { x: float
      y: float
      width: float
      height: float
      text: string
      paragraphs: SlideParagraph[]
      fillColor: string
      lineColor: string
      lineWidth: float
      shapeType: string
      verticalAlign: string
      adj1: float
      adj2: float }

/// スライド上に配置された図形。
type SlideShape =
    { x: float
      y: float
      width: float
      height: float
      fillColor: string
      lineColor: string
      lineWidth: float
      shapeType: string
      adj1: float
      adj2: float }

/// スライド上に配置された表セル。
type SlideTableCell =
    { text: string
      runs: TextRun[]
      fillColor: string
      textAlign: string
      verticalAlign: string }

/// スライド上に配置された表。
type SlideTable =
    { x: float
      y: float
      width: float
      height: float
      rows: SlideTableCell[][]
      columnWidths: float[]
      rowHeights: float[] }

/// スライド上に配置された画像。
type SlideImage =
    { x: float
      y: float
      width: float
      height: float
      altText: string
      contentType: string
      data: string }

/// スライド。
type Slide =
    { index: int
      title: string
      texts: string[]
      textBoxes: SlideTextBox[]
      shapes: SlideShape[]
      tables: SlideTable[]
      images: SlideImage[]
      backgroundColor: string
      width: float
      height: float }

/// プレゼンテーション全体。
type PresentationData = { kind: string; slides: Slide[] }
